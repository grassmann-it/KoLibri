using System.Net;
using System.Net.Sockets;
using Xunit;
using KoLibri.Core.Basis;

namespace KoLibri.Core.Tests;

public sealed class WoResidualTests
{
    private sealed class Upstream(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => Task.FromResult(f(r));
    }

    [Fact]
    public async Task Relay_refuses_requests_once_the_session_check_reports_a_change()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        string? stale = null; var calls = 0;
        using var server = new RelayServer(new HttpClient(new Upstream(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") }; })),
            new RelayOptions { Port = port, KolCookie = "PHPSESSID=x", SessionUserAgent = "ua", SessionCheck = () => Task.FromResult(stale) });
        server.Start();
        using var cts = new CancellationTokenSource();
        _ = server.RunAsync(cts.Token);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        HttpRequestMessage Req() { var r = new HttpRequestMessage(HttpMethod.Get, server.Origin + "/main.php"); r.Headers.TryAddWithoutValidation("Cookie", $"{server.CookieName}={server.SessionCookieValue}"); return r; }
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Req())).StatusCode);
        stale = "The Main login changed";
        var res = await client.SendAsync(Req());
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("login changed", await res.Content.ReadAsStringAsync());
        Assert.Equal(1, calls);
        cts.Cancel();
    }

    [Theory]
    [InlineData("--role", "mainn")]
    [InlineData("--role")]
    [InlineData("--rol", "main")]
    public void Login_clear_roles_reject_bad_input(params string[] args)
        => Assert.Throws<ArgumentException>(() => StrictOptions.LoginRoles(args));

    [Fact]
    public void Login_clear_roles_default_only_when_option_absent()
    {
        Assert.Equal(2, StrictOptions.LoginRoles(Array.Empty<string>()).Count);
        Assert.Equal(new[] { KoLibri.Cli.AccountRole.Mallbot }, StrictOptions.LoginRoles(new[] { "--role", "mallbot" }));
    }
}
