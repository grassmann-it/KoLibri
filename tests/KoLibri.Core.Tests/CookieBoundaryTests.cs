using System.Net;
using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;
using Xunit;

namespace KoLibri.Core.Tests;

/// <summary>The session cookie must never leave the KoL origin, whatever handler or redirect is involved.</summary>
public sealed class CookieBoundaryTests
{
    private static readonly AccountCredentials Credentials = new() { CookieHeader = "session=1", AccessToken = "t" };

    private sealed class Recording(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public List<(Uri Uri, string? Cookie)> Seen { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Seen.Add((r.RequestUri!, r.Headers.TryGetValues("Cookie", out var v) ? string.Join(";", v) : null));
            var res = reply(r);
            res.RequestMessage = r;
            return Task.FromResult(res);
        }
    }

    [Fact]
    public async Task Manual_redirect_to_a_foreign_host_is_not_followed_and_gets_no_cookie()
    {
        var handler = new Recording(_ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("https://evil.test/steal") }
        });
        var dispatcher = new FreePullDispatcher(new HttpClient(handler), new Uri("https://kol.test/"));

        await Assert.ThrowsAsync<KoLSecurityException>(() => dispatcher.VisitBurningLeavesAsync(Credentials));
        Assert.Single(handler.Seen);
        Assert.DoesNotContain(handler.Seen, s => s.Uri.Host == "evil.test");
    }

    [Fact]
    public async Task Same_origin_manual_redirect_is_still_followed()
    {
        var handler = new Recording(r => r.RequestUri!.AbsolutePath == "/campground.php"
            ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("/choice.php", UriKind.Relative) } }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("page") });
        var dispatcher = new FreePullDispatcher(new HttpClient(handler), new Uri("https://kol.test/"));

        await dispatcher.VisitBurningLeavesAsync(Credentials);
        Assert.Equal(2, handler.Seen.Count);
    }

    [Fact]
    public async Task A_client_that_auto_redirected_is_refused()
    {
        var handler = new Recording(r =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("x") };
            return res;
        });
        var inner = new RewritingHandler(handler, new Uri("https://evil.test/landed"));
        var dispatcher = new FreePullDispatcher(new HttpClient(inner), new Uri("https://kol.test/"));
        await Assert.ThrowsAsync<KoLSecurityException>(() => dispatcher.VisitBurningLeavesAsync(Credentials));
    }

    private sealed class RewritingHandler(HttpMessageHandler inner, Uri finalUri) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var res = await base.SendAsync(r, ct);
            res.RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUri);
            return res;
        }
    }

    [Fact]
    public void Auto_redirecting_handlers_are_rejected_for_authenticated_clients()
    {
        var uri = new Uri("https://www.kingdomofloathing.com/");
        Assert.Throws<KoLSecurityException>(() => KoLLoginClient.CreateAuthenticatedClient(
            Credentials, uri, innerHandler: new HttpClientHandler { AllowAutoRedirect = true }));
        Assert.Throws<KoLSecurityException>(() => KoLLoginClient.CreateAuthenticatedClient(
            Credentials, uri, innerHandler: new DelegatingHandlerWrap(new SocketsHttpHandler { AllowAutoRedirect = true })));
        using var ok = KoLLoginClient.CreateAuthenticatedClient(
            Credentials, uri, innerHandler: new HttpClientHandler { AllowAutoRedirect = false });
    }

    private sealed class DelegatingHandlerWrap(HttpMessageHandler inner) : DelegatingHandler(inner);
}
