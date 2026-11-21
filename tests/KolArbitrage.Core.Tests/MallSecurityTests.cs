using System.Net;
using System.Net.Http;
using Xunit;
using KolArbitrage.Core.Basis;
using KolArbitrage.Core.Markt;

namespace KolArbitrage.Core.Tests;

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
            new MallReadPolicy { MinimumRequestInterval = TimeSpan.Zero });

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
            new MallReadPolicy { MinimumRequestInterval = TimeSpan.Zero });

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
