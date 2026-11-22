using System.Net;
using System.Net.Http;
using Xunit;
using KoLibri.Core.Basis;
using KoLibri.Core.Markt;

namespace KoLibri.Core.Tests;

public sealed class MallSecurityTests
{
    [Fact]
    public void HostPolicyRejectsLookalikeAndCrossHostRedirects()
    {
        var policy = new KoLSecurityPolicy();
        Assert.False(policy.IsAllowed(new Uri("https://kingdomofloathing.com.attacker.example/")));
        Assert.Throws<KoLSecurityException>(() =>
            policy.EnsureResponseUri(
                new Uri("https://www.kingdomofloathing.com/mall.php"),
                new Uri("https://evil.example/collect")));
    }

    [Fact]
    public async Task MallSearchFiltersRowsToTheRequestedItemAndParsesSellerAndLimit()
    {
        const string html = """
            <html><body>
            <table>
              <tr class='graybelow' id='stock_10_42'>
                <td class='small store'><b>Good Store</b></td>
                <td><a href='showplayer.php?who=77'>Seller Name</a></td>
                <td class='small price'><span>1,234 Meat</span></td>
                <td class='small stock'>7</td>
                <td class='small limit'>2</td>
              </tr>
              <tr class='graybelow' id='stock_11_99'>
                <td class='small store'><b>Unrequested</b></td>
                <td class='small price'><span>1 Meat</span></td>
                <td class='small stock'>99</td>
              </tr>
            </table>
            </body></html>
            """;
        using var http = new HttpClient(new StubHandler(html));
        var client = new KoLMallReadClient(
            http,
            new Uri("https://www.kingdomofloathing.com/"),
            new MallReadPolicy { MinimumRequestInterval = TimeSpan.Zero },
            itemNameResolver: id => id == "42" ? "test item" : null);

        var result = await client.SearchAsync("42");

        var listing = Assert.Single(result.Listings);
        Assert.Equal("42", listing.ItemId);
        Assert.Equal("77", listing.SellerId);
        Assert.Equal("Seller Name", listing.SellerName);
        Assert.Equal(2, listing.PurchaseLimit);
    }

    [Fact]
    public async Task LoginAndEmptyMallResponsesAreNotCachedAsEmptySnapshots()
    {
        using var loginHttp = new HttpClient(new StubHandler(
            "<html><form action='login.php'><input name='loginname'></form></html>"));
        var client = new KoLMallReadClient(
            loginHttp,
            new Uri("https://www.kingdomofloathing.com/"),
            new MallReadPolicy { MinimumRequestInterval = TimeSpan.Zero },
            itemNameResolver: id => id == "42" ? "test item" : null);

        var exception = await Assert.ThrowsAsync<KoLHtmlResponseException>(() => client.SearchAsync("42"));
        Assert.Equal(KoLHtmlResponseKind.LoginPage, exception.Kind);
    }

    [Fact]
    public void EmptyHtmlIsClassifiedSeparatelyFromLogin()
    {
        Assert.Equal(KoLHtmlResponseKind.Empty, KoLHtmlResponseClassifier.Classify("  <html>\n<body> </body></html>"));
        Assert.Equal(KoLHtmlResponseKind.LoginPage, KoLHtmlResponseClassifier.Classify("<input name='loginname'>"));
        Assert.Equal(KoLServerResponseKind.EmptyResponse, KoLServerResponse.Empty().Classify());
        Assert.Equal(KoLServerResponseKind.Unauthenticated,
            KoLServerResponse.FromHtml("<input name='loginname'>").Classify());
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(Uri Uri, string? Cookie)> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.Headers.TryGetValues("Cookie", out var cookies);
            Requests.Add((request.RequestUri!, cookies is null ? null : string.Join(";", cookies)));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("<html></html>")
            });
        }
    }

    [Fact]
    public async Task ScannerSendsCookieOnlyToKoLAndNeverToPricegun()
    {
        const string cookie = "PHPSESSID=test-cookie-value";
        var kol = new RecordingHandler();
        var pricegun = new RecordingHandler();
        using var authed = KoLLoginClient.CreateAuthenticatedClient(
            new AccountCredentials { CookieHeader = cookie },
            new Uri("https://www.kingdomofloathing.com/"),
            innerHandler: kol);
        using var anonymous = new HttpClient(pricegun);
        var scanner = new MarketScanner(authed, pricegunHttp: anonymous);

        await scanner.ScanAsync(42, "Thing", "Thing");

        var kolRequest = Assert.Single(kol.Requests);
        Assert.Equal("www.kingdomofloathing.com", kolRequest.Uri.Host);
        Assert.Equal(cookie, kolRequest.Cookie);
        var pricegunRequest = Assert.Single(pricegun.Requests);
        Assert.Equal("pricegun.loathers.net", pricegunRequest.Uri.Host);
        Assert.Null(pricegunRequest.Cookie);
    }

    [Fact]
    public async Task AuthenticatedClientRefusesForeignHostBeforeAnySend()
    {
        var kol = new RecordingHandler();
        using var authed = KoLLoginClient.CreateAuthenticatedClient(
            new AccountCredentials { CookieHeader = "PHPSESSID=test-cookie-value" },
            new Uri("https://www.kingdomofloathing.com/"),
            innerHandler: kol);

        await Assert.ThrowsAsync<KoLSecurityException>(() =>
            authed.GetStringAsync(new Uri("https://pricegun.loathers.net/item/42")));
        await Assert.ThrowsAsync<KoLSecurityException>(() =>
            authed.GetStringAsync(new Uri("https://evil.example/collect")));
        Assert.Empty(kol.Requests);
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body)
            });
    }
}
