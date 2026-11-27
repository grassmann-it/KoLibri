using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using KoLibri.Core.Basis;
using Xunit;

namespace KoLibri.Core.Tests;

/// <summary>
/// End to end through a real <see cref="RelayHost"/> (real HttpClient upstream) against a local fake KoL server:
/// no request ever leaves the machine. Covers rewriting, redirects, POST with pwd, assets, rate limiting, logging.
/// </summary>
public sealed class RelayFakeKolTests : IDisposable
{
    private sealed record Seen(string Method, string RawUrl, string? Cookie, string Body, DateTime At);

    private sealed class FakeKol : IDisposable
    {
        private readonly HttpListener _l = new();
        private readonly CancellationTokenSource _cts = new();
        public readonly List<Seen> Requests = new();
        public int Port { get; } = RelayHost.FindFreePort();
        public string Origin => $"http://127.0.0.1:{Port}";

        public FakeKol()
        {
            _l.Prefixes.Add(Origin + "/");
            _l.Start();
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    HttpListenerContext c;
                    try { c = await _l.GetContextAsync(); } catch { break; }
                    _ = Task.Run(() => Handle(c));
                }
            });
        }

        private void Handle(HttpListenerContext c)
        {
            var req = c.Request; var res = c.Response;
            var body = req.HasEntityBody ? new StreamReader(req.InputStream, Encoding.UTF8).ReadToEnd() : "";
            lock (Requests) Requests.Add(new(req.HttpMethod, req.RawUrl!, req.Headers["Cookie"], body, DateTime.UtcNow));
            var path = req.Url!.AbsolutePath;
            switch (path)
            {
                case "/main.php":
                    Html(res, $"<html><a href=\"{Origin}/fight.php\">abs</a> <a href=\"//127.0.0.1:{Port}/choice.php\">rel</a> " +
                              "<a href=\"/adventure.php\">local</a> <img src=\"/images/a.gif\"> <a href=\"https://forums.kingdomofloathing.com/x\">forum</a></html>");
                    break;
                case "/login.php":
                    res.StatusCode = 302; res.RedirectLocation = $"{Origin}/main.php";
                    res.AddHeader("Set-Cookie", "PHPSESSID=fromkol; path=/; HttpOnly"); res.ContentLength64 = 0; res.Close(); break;
                case "/fight.php": Html(res, "<html>fight pwd=abc123</html>"); break;
                case "/choice.php":
                    Html(res, "<html>" + (req.HttpMethod == "POST" ? "posted:" + body : "choice") + "</html>"); break;
                case "/style.css":
                    res.ContentType = "text/css"; Write(res, $"body{{background:url({Origin}/images/bg.png)}}"); break;
                case "/images/a.gif":
                    res.ContentType = "image/gif"; Write(res, "GIF89a\0\0\0", Encoding.Latin1); break;
                default: res.StatusCode = 404; Write(res, "nope"); break;
            }
        }

        private static void Html(HttpListenerResponse res, string html) { res.ContentType = "text/html; charset=utf-8"; Write(res, html); }
        private static void Write(HttpListenerResponse res, string text, Encoding? enc = null)
        {
            var b = (enc ?? Encoding.UTF8).GetBytes(text);
            res.ContentLength64 = b.Length; res.OutputStream.Write(b); res.Close();
        }
        public void Dispose() { _cts.Cancel(); try { _l.Stop(); } catch { } }
    }

    private readonly FakeKol _kol = new();
    private readonly List<RelayHost> _hosts = new();
    private readonly string _log = Path.Combine(Path.GetTempPath(), "kolibri-fakekol-" + Guid.NewGuid().ToString("N") + ".jsonl");
    public void Dispose() { foreach (var h in _hosts) h.Dispose(); _kol.Dispose(); try { File.Delete(_log); } catch { } }

    private static AccountSession Session() => new(
        new AccountProfile { Id = "main", DisplayName = "Fixture", Role = AccountRole.Main },
        new AccountCredentials { Username = "Fixture", CookieHeader = "PHPSESSID=SECRETKOL", ExpiresAt = DateTimeOffset.UtcNow.AddHours(2) });

    private RelayHost Start(KoLRateLimiter? limiter = null)
    {
        // No injected handler: the real upstream path (rate-limited HttpClient) against the local fake.
        var h = RelayHost.Start(Session(), 0, _log, false, null, null, _kol.Origin, limiter ?? new KoLRateLimiter(TimeSpan.Zero));
        _hosts.Add(h); return h;
    }

    private static HttpClient Client() => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });

    private static HttpRequestMessage Req(RelayHost h, HttpMethod m, string path, bool post = false)
    {
        var r = new HttpRequestMessage(m, h.Origin + path);
        r.Headers.TryAddWithoutValidation("Cookie", $"{h.CookieNameForTests}={h.SessionCookieForTests}");
        if (post) r.Headers.TryAddWithoutValidation("Origin", h.Origin);
        return r;
    }

    [Fact]
    public async Task Pages_links_and_css_are_rewritten_to_the_relay_but_other_hosts_stay()
    {
        using var h = Start(); using var c = Client();
        var html = await (await c.SendAsync(Req(h, HttpMethod.Get, "/main.php"))).Content.ReadAsStringAsync();
        var port = new Uri(h.Origin).Port;
        Assert.Contains($"href=\"{h.Origin}/fight.php\"", html);
        Assert.Contains($"href=\"//127.0.0.1:{port}/choice.php\"", html);
        Assert.Contains("href=\"/adventure.php\"", html);
        Assert.Contains("https://forums.kingdomofloathing.com/x", html);
        Assert.DoesNotContain(_kol.Origin, html);
        var css = await (await c.SendAsync(Req(h, HttpMethod.Get, "/style.css"))).Content.ReadAsStringAsync();
        Assert.Contains($"url({h.Origin}/images/bg.png)", css);
        var img = await c.SendAsync(Req(h, HttpMethod.Get, "/images/a.gif"));
        Assert.Equal("image/gif", img.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("GIF89a", await img.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Location_is_rewritten_and_the_game_set_cookie_never_reaches_the_browser()
    {
        using var h = Start(); using var c = Client();
        var res = await c.SendAsync(Req(h, HttpMethod.Get, "/login.php"));
        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.Equal($"{h.Origin}/main.php", res.Headers.Location!.ToString());
        Assert.False(res.Headers.Contains("Set-Cookie"));
        lock (_kol.Requests) Assert.Equal("PHPSESSID=SECRETKOL", _kol.Requests[^1].Cookie);
    }

    [Fact]
    public async Task Post_with_pwd_reaches_the_game_intact_and_the_log_keeps_only_safe_fields()
    {
        using var h = Start(); using var c = Client();
        var r = Req(h, HttpMethod.Post, "/choice.php", post: true);
        r.Content = new StringContent("pwd=SECRETPWD1234&whichchoice=7&option=2", Encoding.UTF8, "application/x-www-form-urlencoded");
        var body = await (await c.SendAsync(r)).Content.ReadAsStringAsync();
        Assert.Contains("posted:pwd=SECRETPWD1234&whichchoice=7&option=2", body);
        for (var i = 0; i < 40 && (!File.Exists(_log) || !File.ReadAllText(_log).Contains("choice.php")); i++) await Task.Delay(50);
        var log = File.ReadAllText(_log);
        Assert.Contains("whichchoice", log);
        Assert.DoesNotContain("SECRETPWD1234", log);
        Assert.DoesNotContain("SECRETKOL", log);
        // pwd in a GET query is redacted as well
        await c.SendAsync(Req(h, HttpMethod.Get, "/fight.php?action=attack&pwd=QUERYPWD99"));
        for (var i = 0; i < 40 && !File.ReadAllText(_log).Contains("fight.php"); i++) await Task.Delay(50);
        Assert.DoesNotContain("QUERYPWD99", File.ReadAllText(_log));
    }

    [Theory]
    [InlineData("GET http://evil.example/steal HTTP/1.1")]
    [InlineData("GET //evil.example/steal HTTP/1.1")]
    [InlineData("GET /\\evil.example/steal HTTP/1.1")]
    public async Task Only_the_game_host_is_ever_contacted(string requestLine)
    {
        using var h = Start();
        var port = new Uri(h.Origin).Port;
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var s = tcp.GetStream();
        await s.WriteAsync(Encoding.ASCII.GetBytes($"{requestLine}\r\nHost: 127.0.0.1:{port}\r\n" +
            $"Cookie: {h.CookieNameForTests}={h.SessionCookieForTests}\r\nConnection: close\r\n\r\n"));
        var buf = new byte[512]; var n = await s.ReadAsync(buf);
        var first = Encoding.ASCII.GetString(buf, 0, n).Split("\r\n")[0];
        Assert.DoesNotContain(" 200 ", first);
        lock (_kol.Requests) Assert.Empty(_kol.Requests);
    }

    [Fact]
    public async Task Requests_are_spaced_by_the_global_limiter()
    {
        var limiter = new KoLRateLimiter(TimeSpan.FromMilliseconds(400));
        using var h = Start(limiter); using var c = Client();
        var sw = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, 3).Select(i => c.SendAsync(Req(h, HttpMethod.Get, "/fight.php?i=" + i))));
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds >= 700, $"3 parallel requests took only {sw.ElapsedMilliseconds} ms");
        List<DateTime> at; lock (_kol.Requests) at = _kol.Requests.Select(r => r.At).OrderBy(x => x).ToList();
        Assert.Equal(3, at.Count);
        Assert.True((at[2] - at[0]).TotalMilliseconds >= 700);
    }
}
