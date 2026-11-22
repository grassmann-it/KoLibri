using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Xunit;
using KoLibri.Core.Markt;

namespace KoLibri.Core.Tests;

public sealed class HttpMallPurchaseTransportTests
{
    [Fact]
    public void ConstructorRejectsHostOutsideKoLAllowlist()
    {
        using var client = new HttpClient(new RecordingHandler());
        var options = new HttpMallPurchaseTransportOptions
        {
            BaseUri = new Uri("https://evil.example/", UriKind.Absolute)
        };

        Assert.Throws<ArgumentException>(() => new HttpMallPurchaseTransport(client, options));
    }

    [Fact]
    public async Task CommitBindsNumericOfferAndNeverSendsApprovalToken()
    {
        var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var before = State(now, meat: 10_000, quantity: 5, inventory: 0);
        var after = before with
        {
            Offer = before.Offer with { Quantity = 3, ObservedAt = now.AddSeconds(1) },
            Account = before.Account with
            {
                Meat = 9_800,
                DailySpent = 200,
                Inventory = new Dictionary<string, int> { ["789"] = 2 },
                ObservedAt = now.AddSeconds(1)
            },
            ObservedAt = now.AddSeconds(1)
        };
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("You bought 2 items.", Encoding.UTF8, "text/html")
        });
        using var client = new HttpClient(handler);
        var transport = new HttpMallPurchaseTransport(
            client,
            new HttpMallPurchaseTransportOptions
            {
                BaseUri = new Uri("https://www.kingdomofloathing.com/", UriKind.Absolute)
            },
            freshRead: (_, _) => Task.FromResult(before),
            verify: (_, _) => Task.FromResult(after));

        var request = new MallPurchaseRequest("mallbot", "42", "123", "789", 100, 2, 100_000);
        var preview = new MallPurchasePreview("action-1", request, before, now, now.AddMinutes(5), false, false);
        var receipt = await transport.CommitAsync(
            new MallPurchaseTransportCommit(preview, before, "approval-secret", now));

        Assert.True(receipt.Accepted);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.NotNull(sent.RequestUri);
        Assert.Equal(string.Empty, sent.RequestUri!.Query);
        var body = handler.RequestBodies[0];
        Assert.Contains("whichitem=789000000100", body, StringComparison.Ordinal);
        Assert.Contains("whichstore=123", body, StringComparison.Ordinal);
        Assert.Contains("buying=1", body, StringComparison.Ordinal);
        Assert.Contains("quantity=2", body, StringComparison.Ordinal);
        Assert.DoesNotContain("approval-secret", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostFailureIsIndeterminateAndIsNeverRetried()
    {
        var now = DateTimeOffset.UtcNow;
        var before = State(now, meat: 10_000, quantity: 5, inventory: 0);
        var handler = new RecordingHandler(new HttpRequestException("socket closed"));
        using var client = new HttpClient(handler);
        var transport = new HttpMallPurchaseTransport(
            client,
            new HttpMallPurchaseTransportOptions
            {
                BaseUri = new Uri("https://www.kingdomofloathing.com/", UriKind.Absolute)
            },
            freshRead: (_, _) => Task.FromResult(before),
            verify: (_, _) => Task.FromResult(before));

        var request = new MallPurchaseRequest("mallbot", "42", "123", "789", 100, 2, 100_000);
        var preview = new MallPurchasePreview("action-1", request, before, now, now.AddMinutes(5), false, false);
        var receipt = await transport.CommitAsync(new MallPurchaseTransportCommit(preview, before, null, now));

        Assert.False(receipt.Accepted);
        Assert.Contains("indeterminate", receipt.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests);
        Assert.Equal(MallPurchaseHttpResponseClassification.Indeterminate, transport.LastResponse!.Classification);
    }

    [Fact]
    public async Task RefreshHookIsCalledForFreshReadsAndRejectsNonNumericIds()
    {
        using var client = new HttpClient(new RecordingHandler());
        var calls = 0;
        var transport = new HttpMallPurchaseTransport(
            client,
            new HttpMallPurchaseTransportOptions
            {
                BaseUri = new Uri("https://www.kingdomofloathing.com/", UriKind.Absolute)
            },
            freshRead: (request, _) =>
            {
                calls++;
                return Task.FromResult(State(DateTimeOffset.UtcNow, 10_000, 5, 0));
            },
            verify: null);

        var observation = await transport.RefreshAsync(new MallPurchaseRequest("mallbot", "42", "123", "789", 100, 1));
        Assert.Equal("789", observation.Offer.ItemId);
        Assert.Equal(1, calls);

        await Assert.ThrowsAsync<ArgumentException>(() => transport.RefreshAsync(
            new MallPurchaseRequest("mallbot", "seller-name", "123", "789", 100, 1)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RefreshHttpFixtureReadsExactListingStatusAndInventory()
    {
        var listing = "<html><tr id=\"stock_123_789\"><input name=\"whichitem\" value=\"789\"><input name=\"whichstore\" value=\"123\"><input name=\"sellerid\" value=\"42\"><input name=\"price\" value=\"100\"><input name=\"quantity\" value=\"5\"></tr></html>";
        var handler = new RecordingHandler(
            Response(listing, "text/html"),
            Response("{\"meat\":10000,\"dailySpent\":0,\"dailyLimit\":100000}", "application/json"),
            Response("{\"inventory\":{\"789\":{\"quantity\":1}}}", "application/json"));
        using var client = new HttpClient(handler);
        var transport = new HttpMallPurchaseTransport(
            client,
            new HttpMallPurchaseTransportOptions
            {
                BaseUri = new Uri("https://www.kingdomofloathing.com/", UriKind.Absolute)
            });

        var observation = await transport.RefreshAsync(new MallPurchaseRequest("mallbot", "42", "123", "789", 100, 2, 100_000));

        Assert.Equal("42", observation.Offer.SellerId);
        Assert.Equal("123", observation.Offer.StoreId);
        Assert.Equal("789", observation.Offer.ItemId);
        Assert.Equal(100, observation.Offer.UnitPrice);
        Assert.Equal(5, observation.Offer.Quantity);
        Assert.Equal(10_000, observation.Account.Meat);
        Assert.Equal(1, observation.Account.InventoryQuantity("789"));
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    private static MallPurchaseObservation State(DateTimeOffset at, long meat, int quantity, int inventory)
        => new(
            new MallPurchaseOffer("42", "123", "789", 100, quantity, at),
            new MallPurchaseAccountState(
                "mallbot", meat, 0, 100_000,
                new Dictionary<string, int> { ["789"] = inventory }, at));

    private static HttpResponseMessage Response(string body, string mediaType)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType)
        };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<object> _responses = new();

        public RecordingHandler(params object[] responses)
        {
            foreach (var response in responses) _responses.Enqueue(response);
        }

        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            if (_responses.Count == 0)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("You bought 1 item.", Encoding.UTF8, "text/html")
                };
            var next = _responses.Dequeue();
            if (next is Exception exception) throw exception;
            return (HttpResponseMessage)next;
        }
    }
}
