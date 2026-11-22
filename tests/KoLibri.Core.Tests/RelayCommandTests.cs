using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace KoLibri.Core.Tests;

public sealed class RelayCommandTests : IDisposable
{
    private const string Kol = "https://www.kingdomofloathing.com";
    private readonly List<string> _log = new();
    private readonly List<HttpRequestMessage> _seen = new();
    private readonly List<RelayServer> _servers = new();
    private readonly CancellationTokenSource _cts = new();

    public void Dispose() { _cts.Cancel(); foreach (var s in _servers) s.Dispose(); }

    private sealed class FakeUpstream(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => f(r, ct);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }

    private RelayServer Start(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> upstream,
        Action<RelayOptionsBuilder>? tweak = null, int? port = null)
    {
        var b = new RelayOptionsBuilder { Port = port ?? FreePort() };
        tweak?.Invoke(b);
        var http = new HttpClient(new FakeUpstream((r, ct) => { lock (_seen) _seen.Add(r); return upstream(r, ct); }));
        var server = new RelayServer(http, new RelayOptions
        {
            Port = b.Port, KolCookie = "PHPSESSID=SECRETKOL; appserver=x", SessionUserAgent = "LibKolibri/test",
            Log = l => { lock (_log) _log.Add(l); },
            UpstreamTimeout = b.Timeout, MaxRewriteBytes = b.MaxRewrite,
        });
        _servers.Add(server);
        server.Start();
        _ = server.RunAsync(_cts.Token);
        return server;
    }

    private sealed class RelayOptionsBuilder
    {
        public int Port; public TimeSpan Timeout = TimeSpan.FromSeconds(30); public int MaxRewrite = 2 * 1024 * 1024;
    }

    private static HttpClient Client() => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });

    private static HttpRequestMessage Get(RelayServer s, string path, bool session = true)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, s.Origin + path);
        if (session) r.Headers.TryAddWithoutValidation("Cookie", $"{s.CookieName}={s.SessionCookieValue}");
        return r;
    }

    private static HttpResponseMessage Ok(string body, string type = "text/html; charset=utf-8")
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type.Split(';')[0]) { Headers = { ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(type) } } };

    private async Task WaitLog(Func<string, bool> pred)
    {
        for (var i = 0; i < 100; i++)
        {
            lock (_log) if (_log.Any(pred)) return;
            await Task.Delay(50);
        }
        throw new Xunit.Sdk.XunitException("log entry did not appear");
    }

    private int UpstreamCalls() { lock (_seen) return _seen.Count; }

    [Fact]
    public async Task Entry_with_token_sets_strict_cookie_and_redirects_only_locally()
    {
        using var s = Start((_, _) => Task.FromResult(Ok("x")));
        using var c = Client();
        var ok = await c.GetAsync(new Uri(s.EntryUrl("//evil.example/x")));
        Assert.Equal(HttpStatusCode.Found, ok.StatusCode);
        Assert.Equal("/main.php", ok.Headers.Location!.OriginalString);
        var cookie = ok.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("SameSite=Strict", cookie); Assert.Contains("HttpOnly", cookie);
        Assert.Contains(s.CookieName + "=" + s.SessionCookieValue, cookie);

        var bad = await c.GetAsync($"{s.Origin}{RelayServer.EntryPath}?t=wrong");
        Assert.Equal(HttpStatusCode.Forbidden, bad.StatusCode);
        Assert.False(bad.Headers.Contains("Set-Cookie"));
        Assert.Equal(0, UpstreamCalls());
    }

    [Fact]
    public async Task Requests_without_session_cookie_or_with_foreign_host_never_reach_upstream()
    {
        using var s = Start((_, _) => Task.FromResult(Ok("x")));
        using var c = Client();
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(Get(s, "/main.php", session: false))).StatusCode);

        var wrongHost = Get(s, "/main.php"); wrongHost.Headers.Host = "evil.example:" + new Uri(s.Origin).Port;
        Assert.Contains((await c.SendAsync(wrongHost)).StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound }); // listener may reject first
        var wrongPort = Get(s, "/main.php"); wrongPort.Headers.Host = "127.0.0.1:1";
        Assert.Contains((await c.SendAsync(wrongPort)).StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound }); // listener may reject first
        var prefixHost = Get(s, "/main.php"); prefixHost.Headers.Host = $"127.0.0.1.evil.example:{new Uri(s.Origin).Port}";
        Assert.Contains((await c.SendAsync(prefixHost)).StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound }); // listener may reject first
        Assert.Equal(0, UpstreamCalls());
    }

    [Theory]
    [InlineData("Sec-Fetch-Site", "cross-site")]
    [InlineData("Sec-Fetch-Site", "same-site")]
    [InlineData("Origin", "https://evil.example")]
    [InlineData("Origin", "null")]
    [InlineData("Referer", "https://evil.example/page")]
    public async Task Cross_site_signals_are_rejected_even_with_cookie(string header, string value)
    {
        using var s = Start((_, _) => Task.FromResult(Ok("x")));
        using var c = Client();
        var r = Get(s, "/adventure.php?snarfblat=1"); r.Headers.TryAddWithoutValidation(header, value);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(r)).StatusCode);
        Assert.Equal(0, UpstreamCalls());
    }

    [Fact]
    public async Task Post_without_origin_is_rejected_and_same_origin_post_is_forwarded()
    {
        using var s = Start((_, _) => Task.FromResult(Ok("done")));
        using var c = Client();
        HttpRequestMessage Post() => new(HttpMethod.Post, s.Origin + "/choice.php")
        {
            Content = new StringContent("whichchoice=1&option=2&pwd=HASH123", Encoding.UTF8, "application/x-www-form-urlencoded"),
            Headers = { { "Cookie", $"{s.CookieName}={s.SessionCookieValue}" } }
        };
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(Post())).StatusCode);
        var good = Post(); good.Headers.TryAddWithoutValidation("Origin", s.Origin);
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(good)).StatusCode);
        await WaitLog(l => l.Contains("\"method\":\"POST\"") && l.Contains("\"status\":200"));
        Assert.Equal(1, UpstreamCalls());
    }

    [Fact]
    public async Task Redirect_and_body_are_rewritten_to_relay_origin()
    {
        using var s = Start((r, _) => Task.FromResult(r.RequestUri!.AbsolutePath == "/a.php"
            ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(Kol + "/b.php?pwd=H&action=x") } }
            : Ok($"<a href=\"{Kol}/c.php\">x</a> <img src=//www.kingdomofloathing.com/i.gif> http://www.kingdomofloathing.com/d")));
        using var c = Client();
        var redirect = await c.SendAsync(Get(s, "/a.php"));
        Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
        Assert.Equal(s.Origin + "/b.php?pwd=H&action=x", redirect.Headers.Location!.OriginalString);
        var body = await (await c.SendAsync(Get(s, "/c.php"))).Content.ReadAsStringAsync();
        Assert.Contains($"href=\"{s.Origin}/c.php\"", body);
        Assert.Contains($"src=//127.0.0.1:{new Uri(s.Origin).Port}/i.gif", body);
        Assert.DoesNotContain("kingdomofloathing", body);
    }

    [Fact]
    public async Task Latin1_charset_is_decoded_and_reencoded_without_corruption()
    {
        var bytes = Encoding.Latin1.GetBytes($"<p>Käse <a href=\"{Kol}/x\">ö</a></p>");
        using var s = Start((_, _) =>
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("text/html; charset=ISO-8859-1");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var c = Client();
        var res = await c.SendAsync(Get(s, "/m.php"));
        Assert.Contains("ISO-8859-1", res.Content.Headers.ContentType!.ToString());
        var got = await res.Content.ReadAsByteArrayAsync();
        Assert.Equal(Encoding.Latin1.GetBytes($"<p>Käse <a href=\"{s.Origin}/x\">ö</a></p>"), got);
    }

    [Fact]
    public async Task Binary_is_streamed_unchanged_and_oversized_text_is_not_rewritten()
    {
        var png = Enumerable.Range(0, 300_000).Select(i => (byte)i).ToArray();
        var big = "<a href=\"" + Kol + "\">" + new string('x', 5000);
        using var s = Start((r, _) =>
        {
            if (r.RequestUri!.AbsolutePath == "/i.png")
            {
                var bc = new ByteArrayContent(png); bc.Headers.ContentType = new("image/png");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = bc });
            }
            return Task.FromResult(Ok(big));
        }, b => b.MaxRewrite = 1000);
        using var c = Client();
        Assert.Equal(png, await (await c.SendAsync(Get(s, "/i.png"))).Content.ReadAsByteArrayAsync());
        Assert.Equal(big, await (await c.SendAsync(Get(s, "/big.html"))).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_flow_passes_redirect_drops_set_cookie_and_sends_only_allowlisted_headers()
    {
        using var s = Start((r, _) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(Kol + "/login.php") } };
            resp.Headers.TryAddWithoutValidation("Set-Cookie", "PHPSESSID=NEWSECRET; path=/");
            return Task.FromResult(resp);
        });
        using var c = Client();
        var r = Get(s, "/main.php?pwd=ABC123");
        r.Headers.TryAddWithoutValidation("Authorization", "Bearer TOPSECRET");
        r.Headers.TryAddWithoutValidation("Referer", s.Origin + "/game.php?pwd=REFPWD");
        r.Headers.TryAddWithoutValidation("X-Custom", "freevalue");
        var res = await c.SendAsync(r);
        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.Equal(s.Origin + "/login.php", res.Headers.Location!.OriginalString);
        Assert.False(res.Headers.Contains("Set-Cookie"));

        var up = _seen.Single();
        Assert.Equal("PHPSESSID=SECRETKOL; appserver=x", up.Headers.GetValues("Cookie").Single());
        Assert.False(up.Headers.Contains("Authorization")); Assert.False(up.Headers.Contains("X-Custom"));
        Assert.Equal("LibKolibri/test", up.Headers.UserAgent.ToString());
        Assert.StartsWith(Kol + "/game.php", up.Headers.GetValues("Referer").Single());
        await WaitLog(l => l.Contains("\"status\":302"));
    }

    [Fact]
    public async Task Logs_console_and_errors_contain_no_secrets()
    {
        var consoleLines = new List<string>();
        var port = FreePort();
        var http = new HttpClient(new FakeUpstream((_, _) => throw new HttpRequestException("connect failed for https://www.kingdomofloathing.com/x?pwd=LEAKED")));
        using var s = new RelayServer(http, new RelayOptions
        {
            Port = port, KolCookie = "PHPSESSID=SECRETKOL", SessionUserAgent = "ua",
            Log = l => { lock (_log) _log.Add(l); }, Console = l => consoleLines.Add(l)
        });
        s.Start(); _ = s.RunAsync(_cts.Token);
        using var c = Client();
        var r = Get(s, "/a.php?pwd=PWD1&password=PW2&action=buy&whichitem=5&junk=zzz");
        r.Headers.TryAddWithoutValidation("Referer", s.Origin + "/b.php?pwd=REFPWD");
        r.Headers.TryAddWithoutValidation("Accept", "text/html");
        var res = await c.SendAsync(r);
        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        await WaitLog(l => l.Contains("error:HttpRequestException"));
        var everything = string.Join("\n", _log) + string.Join("\n", consoleLines) + body;
        foreach (var secret in new[] { "PWD1", "PW2", "REFPWD", "LEAKED", "SECRETKOL", "zzz", s.Token, s.SessionCookieValue })
            Assert.DoesNotContain(secret, everything);
        Assert.Contains("action=buy", everything);
        Assert.Contains("whichitem=5", everything);
    }

    [Fact]
    public void RedactUrl_keeps_only_safe_values()
    {
        Assert.Equal("/a.php?pwd=…&action=x&flag", RelayServer.RedactUrl("/a.php?pwd=abc&action=x&flag"));
        Assert.Equal("/a.php", RelayServer.RedactUrl("/a.php"));
        Assert.Equal("/a.php?%70wd=…", RelayServer.RedactUrl("/a.php?%70wd=abc"));
    }

    [Fact]
    public async Task Upstream_timeout_returns_504()
    {
        using var s = Start(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Ok("never"); },
            b => b.Timeout = TimeSpan.FromMilliseconds(200));
        using var c = Client();
        var res = await c.SendAsync(Get(s, "/slow.php"));
        Assert.Equal(HttpStatusCode.GatewayTimeout, res.StatusCode);
        await WaitLog(l => l.Contains("\"outcome\":\"timeout\""));
    }

    private sealed class HangingStream : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { await Task.Delay(Timeout.Infinite, ct); return 0; }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Hanging_upstream_body_is_cut_off_by_the_same_timeout()
    {
        using var s = Start((_, _) =>
        {
            var content = new StreamContent(new HangingStream());
            content.Headers.ContentType = new("application/octet-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }, b => b.Timeout = TimeSpan.FromMilliseconds(300));
        using var c = Client();
        try { using var res = await c.SendAsync(Get(s, "/hang.bin"), HttpCompletionOption.ResponseHeadersRead); }
        catch (HttpRequestException) { }
        await WaitLog(l => l.Contains("\"outcome\":\"timeout\""));
    }

    [Fact]
    public void Cookie_name_is_random_per_run_and_not_port_derived()
    {
        using var a = Start((_, _) => Task.FromResult(Ok("x")));
        using var b = Start((_, _) => Task.FromResult(Ok("x")));
        Assert.NotEqual(a.CookieName, b.CookieName);
        Assert.DoesNotContain(FreePortText(a), a.CookieName);
    }

    private static string FreePortText(RelayServer s) => new Uri(s.Origin).Port.ToString();

    private sealed class EndlessStream(TaskCompletionSource disposed) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { Array.Fill(buffer, (byte)1, offset, count); return count; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { await Task.Yield(); buffer.Span.Fill(1); return buffer.Length; }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { disposed.TrySetResult(); base.Dispose(disposing); }
    }

    [Fact]
    public async Task Client_abort_stops_streaming_and_releases_upstream()
    {
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var s = Start((_, _) =>
        {
            var content = new StreamContent(new EndlessStream(disposed));
            content.Headers.ContentType = new("application/octet-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var c = Client();
        var res = await c.SendAsync(Get(s, "/dl.bin"), HttpCompletionOption.ResponseHeadersRead);
        var stream = await res.Content.ReadAsStreamAsync();
        var buf = new byte[1024];
        Assert.True(await stream.ReadAsync(buf) > 0);
        res.Dispose(); c.Dispose();
        Assert.Same(disposed.Task, await Task.WhenAny(disposed.Task, Task.Delay(TimeSpan.FromSeconds(15))));
        await WaitLog(l => l.Contains("\"outcome\":\"client-abort\""));
    }

    [Fact]
    public void Port_in_use_gives_clear_error()
    {
        var port = FreePort();
        using var first = Start((_, _) => Task.FromResult(Ok("x")), port: port);
        var second = new RelayServer(new HttpClient(), new RelayOptions { Port = port });
        using var _ = second;
        var ex = Assert.Throws<InvalidOperationException>(() => second.Start());
        Assert.Contains("--port", ex.Message);
    }
}
