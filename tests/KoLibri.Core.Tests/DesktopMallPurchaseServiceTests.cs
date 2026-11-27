using System.Net;
using System.Text;
using KoLibri.Core.Basis;
using KoLibri.Core.Markt;
using Xunit;

namespace KoLibri.Core.Tests;

/// <summary>Core's price-bound purchase form. The desktop purchase service tests live in KoLibri.Tests/Desktop.</summary>
public sealed class DesktopMallPurchaseServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task BuyClientAndTransportSendTheSamePriceBoundForm()
    {
        var expected = "whichitem=789.100";
        Assert.Equal("789.100", MallBuyClient.FormatWhichItem(789, 100));

        var clientHandler = new Handler(_ => Ok("You acquire an item (2). You spent 200 Meat."));
        using var http = new HttpClient(clientHandler);
        var client = new MallBuyClient(http, new Uri("https://www.kingdomofloathing.com/"));
        var result = await client.BuyAsync(new AccountCredentials { CookieHeader = "c=1" }, 123, 789, 100, 2, "HASH");
        Assert.True(result.Accepted);
        var clientBody = Assert.Single(clientHandler.Bodies);

        var transportHandler = new Handler(_ => Ok("You bought 2 items."));
        using var http2 = new HttpClient(transportHandler);
        var before = State(100, 10_000, 5, 0);
        var after = State(100, 9_800, 3, 2, spent: 200);
        using var transport = new HttpMallPurchaseTransport(http2,
            new HttpMallPurchaseTransportOptions { PasswordHashProvider = (_, _) => Task.FromResult<string?>("HASH") },
            (_, _) => Task.FromResult(before), (_, _) => Task.FromResult(after));
        var request = new MallPurchaseRequest("mallbot", "123", "123", "789", 100, 2, 100_000);
        var preview = new MallPurchasePreview("a", request, before, Now, Now.AddMinutes(5), false, false);
        await transport.CommitAsync(new MallPurchaseTransportCommit(preview, before, null, Now));
        var transportBody = Assert.Single(transportHandler.Bodies);

        foreach (var body in new[] { clientBody, transportBody })
        {
            Assert.Contains(expected, body);
            Assert.Contains("whichstore=123", body);
            Assert.Contains("quantity=2", body);
            Assert.Contains("pwd=HASH", body);
            Assert.Contains("buying=1", body);
        }
    }

    private static MallPurchaseObservation State(long price, long meat, int qty, int inv, long spent = 0)
        => State("123", "789", price, meat, qty, inv, spent);

    private static MallPurchaseObservation State(
        string shop, string item, long price, long meat, int qty, int inv, long spent = 0, long limit = 100_000)
        => new(new MallPurchaseOffer(shop, shop, item, price, qty, Now),
            new MallPurchaseAccountState("mallbot", meat, spent, limit,
                new Dictionary<string, int> { [item] = inv }, Now));

    private static HttpResponseMessage Ok(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return respond(request);
        }
    }
}
