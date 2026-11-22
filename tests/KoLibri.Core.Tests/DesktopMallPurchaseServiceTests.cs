using System.Net;
using System.Text;
using KoLibri.Core.Basis;
using KoLibri.Core.Markt;
using KoLibri.Desktop;
using Xunit;

namespace KoLibri.Core.Tests;

public sealed class DesktopMallPurchaseServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task BuyClientAndTransportSendTheSamePriceBoundForm()
    {
        var expected = "whichitem=789000000100";
        Assert.Equal("789000000100", MallBuyClient.FormatWhichItem(789, 100));

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

    [Fact]
    public async Task BudgetOverrunSendsNothing()
    {
        var fixture = Fixture(100, 10_000_000, 5);
        var svc = Service(fixture, new DesktopMallPurchaseOptions { MaxMeatPerPurchase = 150 });
        var preview = await svc.PreviewAsync("mallbot", "item-1", "42", 100, 2);

        Assert.False(preview.CanConfirm);
        Assert.Contains(MallPurchaseAbortReason.PerPurchaseLimitExceeded, preview.Problems);
        var result = await svc.ConfirmAsync(preview);
        Assert.Equal(DesktopMallPurchaseStatus.NotSent, result.Status);
        Assert.Equal(0, fixture.CommitCount);

        // Daily budget: already spent almost everything.
        var fixture2 = Fixture(100, 10_000_000, 5, dailySpent: 499_900);
        var svc2 = Service(fixture2);
        var p2 = await svc2.PreviewAsync("mallbot", "item-1", "42", 100, 2);
        Assert.Equal(100, p2.RemainingDailyBudget);
        Assert.Equal(DesktopMallPurchaseStatus.NotSent, (await svc2.ConfirmAsync(p2)).Status);
        Assert.Equal(0, fixture2.CommitCount);
    }

    [Fact]
    public async Task PreviewReportsTotalsAndBudgets()
    {
        var svc = Service(Fixture(100, 10_000, 5));
        var p = await svc.PreviewAsync("mallbot", "item-1", "42", 100, 3);
        Assert.True(p.CanConfirm);
        Assert.Equal(300, p.TotalPrice);
        Assert.Equal(500_000, p.RemainingDailyBudget);
        Assert.Equal(250_000 - 300, p.RemainingPurchaseBudget);
    }

    [Fact]
    public async Task PriceChangeSendsNothingAndAsksForReconfirmation()
    {
        var fixture = Fixture(100, 10_000, 5);
        var svc = Service(fixture);
        var preview = await svc.PreviewAsync("mallbot", "item-1", "42", 100, 2);
        fixture.SetState(State("42", "item-1", 150, 10_000, 5, 0, 0, 1_000_000));

        var result = await svc.ConfirmAsync(preview);
        Assert.Equal(DesktopMallPurchaseStatus.PriceChanged, result.Status);
        Assert.Equal(0, fixture.CommitCount);
        Assert.NotNull(result.NewPreview);
        Assert.Equal(150, result.NewPreview!.UnitPrice);
        Assert.False(result.NewPreview.PriceChanged);
    }

    [Fact]
    public async Task TimeoutSendsAtMostOnePostAndIsUnclear()
    {
        var before = State(100, 10_000, 5, 0);
        var handler = new Handler(_ => throw new TaskCanceledException("timeout"));
        using var http = new HttpClient(handler);
        using var transport = new HttpMallPurchaseTransport(http, new HttpMallPurchaseTransportOptions(),
            (_, _) => Task.FromResult(before), (_, _) => Task.FromResult(before));
        var svc = Service(transport);
        var preview = await svc.PreviewAsync("mallbot", "789", "123", 100, 2);

        var result = await svc.ConfirmAsync(preview);
        Assert.Equal(DesktopMallPurchaseStatus.Unclear, result.Status);
        Assert.Equal(1, handler.Bodies.Count);

        // The spent preview cannot be confirmed again.
        var again = await svc.ConfirmAsync(preview);
        Assert.Equal(DesktopMallPurchaseStatus.NotSent, again.Status);
        Assert.Equal(1, handler.Bodies.Count);
    }

    [Fact]
    public async Task SuccessRequiresMatchingMeatAndInventory()
    {
        var before = State(100, 10_000, 5, 0);
        var good = State(100, 9_800, 3, 2, spent: 200);
        Assert.Equal(DesktopMallPurchaseStatus.Purchased, await Run(before, good));

        // The item arrived; Meat only has to be plausible (income may cancel the price), so this settles.
        Assert.Equal(DesktopMallPurchaseStatus.Purchased, await Run(before, State(100, 10_000, 3, 2, spent: 200)));
        // The item arrived but Meat fell by more than the price: unclear.
        Assert.Equal(DesktopMallPurchaseStatus.Unclear, await Run(before, State(100, 9_000, 3, 2, spent: 200)));
        // Meat moved but the item never arrived.
        Assert.Equal(DesktopMallPurchaseStatus.Unclear, await Run(before, State(100, 9_800, 3, 0, spent: 200)));

        static async Task<DesktopMallPurchaseStatus> Run(MallPurchaseObservation b, MallPurchaseObservation a)
        {
            var handler = new Handler(_ => Ok("You bought 2 items."));
            using var http = new HttpClient(handler);
            using var transport = new HttpMallPurchaseTransport(http, new HttpMallPurchaseTransportOptions(),
                (_, _) => Task.FromResult(b), (_, _) => Task.FromResult(a));
            var svc = Service(transport);
            var preview = await svc.PreviewAsync("mallbot", "789", "123", 100, 2);
            var result = await svc.ConfirmAsync(preview);
            Assert.Equal(1, handler.Bodies.Count);
            return result.Status;
        }
    }

    private static DesktopMallPurchaseService Service(
        IMallPurchaseTransport transport, DesktopMallPurchaseOptions? options = null)
        => new(transport, (options ?? new DesktopMallPurchaseOptions()) with { ReconcileDelay = TimeSpan.Zero }, null, new FixedTime());

    private static FixtureMallPurchaseTransport Fixture(long price, long meat, int qty, long dailySpent = 0)
        => new(State("42", "item-1", price, meat, qty, 0, dailySpent, 1_000_000));

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
