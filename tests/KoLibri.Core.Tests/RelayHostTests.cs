using System.Net;
using System.Net.Sockets;
using System.Text;
using KoLibri.Core.Basis;
using KoLibri.Desktop;
using Xunit;

namespace KoLibri.Core.Tests;

public sealed class RelayHostTests : IDisposable
{
    private readonly string _log = Path.Combine(Path.GetTempPath(), "kolibri-relayhost-" + Guid.NewGuid().ToString("N") + ".jsonl");
    private readonly List<RelayHost> _hosts = new();
    public void Dispose() { foreach (var h in _hosts) h.Dispose(); try { File.Delete(_log); } catch { } }

    private sealed class Fake : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("hello", Encoding.UTF8, "text/plain") });
    }

    private static AccountSession Session() => new(
        new AccountProfile { Id = "main", DisplayName = "Fixture", Role = AccountRole.Main },
        new AccountCredentials { Username = "Fixture", CookieHeader = "PHPSESSID=SECRETKOL", ExpiresAt = DateTimeOffset.UtcNow.AddHours(2) });

    private RelayHost Start() { var h = RelayHost.Start(Session(), 0, _log, false, null, new Fake()); _hosts.Add(h); return h; }
    private static HttpClient Client() => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });

    [Fact]
    public async Task Host_binds_loopback_on_a_free_port_and_the_origin_holds_no_token()
    {
        using var a = Start(); using var b = Start();
        Assert.StartsWith("http://127.0.0.1:", a.Origin);
        Assert.NotEqual(a.Origin, b.Origin);
        var token = a.EnterUrl("/main.php").Split("t=")[1].Split('&')[0];
        Assert.DoesNotContain(token, a.Origin);
        Assert.True(a.IsRelayUrl(a.EnterUrl("/x")));
        Assert.False(a.IsRelayUrl(b.Origin + "/"));
        Assert.False(a.IsRelayUrl("https://www.kingdomofloathing.com/"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Token_gives_a_cookie_and_everything_else_is_403_or_denied()
    {
        using var h = Start(); using var c = Client();
        var bad = await c.GetAsync($"{h.Origin}{RelayServer.EntryPath}?t=wrong");
        Assert.Equal(HttpStatusCode.Forbidden, bad.StatusCode);
        var ok = await c.GetAsync(h.EnterUrl("/adventure.php"));
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Equal("/adventure.php", ok.Headers.Location!.ToString());
        Assert.Contains(ok.Headers.GetValues("Set-Cookie"), v => v.Contains("HttpOnly") && v.Contains("SameSite=Strict"));
        // no cookie: forbidden; with the cookie: proxied
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync(h.Origin + "/main.php")).StatusCode);
        var req = new HttpRequestMessage(HttpMethod.Get, h.Origin + "/main.php");
        req.Headers.TryAddWithoutValidation("Cookie", $"{h.CookieNameForTests}={h.SessionCookieForTests}");
        Assert.Equal("hello", await (await c.SendAsync(req)).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Foreign_host_header_is_rejected()
    {
        using var h = Start();
        var port = new Uri(h.Origin).Port;
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {RelayServer.EntryPath}?t=x HTTP/1.1\r\nHost: evil.example:{port}\r\nConnection: close\r\n\r\n"));
        var buf = new byte[512]; var n = await stream.ReadAsync(buf);
        var text = Encoding.ASCII.GetString(buf, 0, n);
        Assert.DoesNotContain(" 200 ", text.Split("\r\n")[0]);
        Assert.DoesNotContain(" 302 ", text.Split("\r\n")[0]);
    }

    [Fact]
    public async Task Log_never_contains_the_token_or_the_kol_cookie()
    {
        using var h = Start(); using var c = Client();
        var url = h.EnterUrl("/main.php");
        var token = url.Split("t=")[1].Split('&')[0];
        await c.GetAsync(url);
        for (var i = 0; i < 40 && !File.Exists(_log); i++) await Task.Delay(50);
        var text = File.Exists(_log) ? File.ReadAllText(_log) : "";
        Assert.DoesNotContain(token, text);
        Assert.DoesNotContain("SECRETKOL", text);
    }

    [Fact]
    public async Task Start_without_a_session_explains_what_to_do()
    {
        var accounts = new AccountSessionManager(new InMemoryAccountStore(), new InMemorySecretStore());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RelayHost.StartAsync(accounts, AccountRole.Main));
        Assert.Contains("not logged in", ex.Message);
    }

    [Theory]
    [InlineData("http://127.0.0.1:9/main.php", BrowserNavigationDecision.Allow)]
    [InlineData("http://127.0.0.1:10/main.php", BrowserNavigationDecision.AskExternal)]
    [InlineData("https://www.kingdomofloathing.com/", BrowserNavigationDecision.AskExternal)]
    [InlineData("javascript:alert(1)", BrowserNavigationDecision.Block)]
    [InlineData("file:///etc/passwd", BrowserNavigationDecision.Block)]
    [InlineData("about:blank", BrowserNavigationDecision.Allow)]
    public void Navigation_policy_pins_the_view_to_the_relay_origin(string url, BrowserNavigationDecision expected)
        => Assert.Equal(expected, BrowserNavigationPolicy.Decide("http://127.0.0.1:9", url));

    [Fact]
    public void Address_bar_shows_paths_only_and_accepts_only_relay_paths()
    {
        const string o = "http://127.0.0.1:9";
        Assert.Equal("/", BrowserNavigationPolicy.DisplayPath(o, o + "/__kolibri/enter?t=SECRET&next=%2Fmain.php"));
        Assert.Equal("/a.php?pwd=…&action=x", BrowserNavigationPolicy.DisplayPath(o, o + "/a.php?pwd=abc&action=x"));
        Assert.Equal("", BrowserNavigationPolicy.DisplayPath(o, "https://example.com/"));
        Assert.Equal("/adventure.php?snarfblat=1", BrowserNavigationPolicy.ResolveTyped(o, "adventure.php?snarfblat=1"));
        Assert.Equal("/main.php", BrowserNavigationPolicy.ResolveTyped(o, o + "/main.php"));
        Assert.Null(BrowserNavigationPolicy.ResolveTyped(o, "https://example.com/x"));
        Assert.Null(BrowserNavigationPolicy.ResolveTyped(o, "//example.com"));
        Assert.Null(BrowserNavigationPolicy.ResolveTyped(o, "/__kolibri/enter?t=x"));
        Assert.Null(BrowserNavigationPolicy.ResolveTyped(o, "/../x"));
    }
}
