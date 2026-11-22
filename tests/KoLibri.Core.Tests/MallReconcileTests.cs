using System.Net;
using System.Text;
using KoLibri.Core.Markt;
using KoLibri.Desktop;
using Xunit;

namespace KoLibri.Core.Tests;

/// <summary>A successful purchase settles even when the shop earns Meat meanwhile; unclear entries are resolved explicitly.</summary>
public sealed class MallReconcileTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1)]
    [InlineData(13_337)]
    public async Task Confirmed_purchase_with_inventory_plus_one_settles_despite_shop_income(long income)
    {
        var before = State(meat: 100_000, inv: 0);
        var after = State(meat: 100_000 - 222 + income, inv: 1);
        var (status, _, posts) = await Run(before, [after], qty: 1);
        Assert.Equal(DesktopMallPurchaseStatus.Purchased, status.Status);
        Assert.Equal(1, posts);
    }

    [Fact]
    public async Task Inventory_unchanged_stays_unclear_with_a_precise_message_after_bounded_rereads()
    {
        var before = State(meat: 100_000, inv: 0);
        var same = State(meat: 100_000 - 222, inv: 0);
        var (result, reads, posts) = await Run(before, [same], qty: 1);
        Assert.Equal(DesktopMallPurchaseStatus.Unclear, result.Status);
        Assert.Contains("inventory still does not show the item", result.Message);
        Assert.Contains("2 more inventory read", result.Message);
        Assert.Equal(1, posts);          // never a second POST
        Assert.Equal(1 + 2, reads);      // the transport's own after-read plus two re-reads
    }

    [Fact]
    public async Task Stale_first_read_is_retried_and_then_settles()
    {
        var before = State(meat: 100_000, inv: 0);
        var stale = State(meat: 100_000 - 222, inv: 0);
        var fresh = State(meat: 100_000 - 222 + 500, inv: 1);
        var (result, _, posts) = await Run(before, [stale, fresh], qty: 1);
        Assert.Equal(DesktopMallPurchaseStatus.Purchased, result.Status);
        Assert.Equal(1, posts);
    }

    [Fact]
    public async Task Partial_purchase_is_reported_with_the_received_quantity()
    {
        var before = State(meat: 100_000, inv: 0);
        var after = State(meat: 100_000 - 222, inv: 1);
        var (result, _, _) = await Run(before, [after], qty: 2);
        Assert.Equal(DesktopMallPurchaseStatus.Partial, result.Status);
        Assert.Equal(1, result.ReceivedQuantity);
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(MallUnclearDecision.WasSpent, 25_000, 0)]
    [InlineData(MallUnclearDecision.NotSpent, 0, 0)]
    [InlineData(MallUnclearDecision.Cancel, 0, 25_000)]
    public async Task Reconcile_resolves_unclear_meat_only_by_explicit_decision(
        MallUnclearDecision decision, long expectedSpent, long expectedUnclear)
    {
        var log = new SessionLogViewModel(now: () => Now);
        var ledger = new MallSpendLedger(new Fixed());
        Assert.True(ledger.TryReserve("mallbot", 25_000, null, out _));
        ledger.MarkUnclear("mallbot", 25_000);
        var prompt = new Prompt(decision);
        var coordinator = new MallPurchaseCoordinator("mallbot",
            _ => Task.FromResult<(DesktopMallPurchaseService, MallPurchaseObserver)?>(null),
            null!, log, ledger, null, prompt, _ => Task.FromResult("Meat now 5."));

        var result = await coordinator.ReconcileUnclearAsync(default);
        Assert.Equal(decision, result);
        Assert.Equal(25_000, prompt.AmountShown);
        Assert.Equal(expectedSpent, ledger.SpentToday("mallbot") - expectedUnclear);
        Assert.Equal(expectedUnclear, ledger.UnclearToday("mallbot"));
    }

    [Fact]
    public async Task Reconcile_with_nothing_unclear_asks_nothing()
    {
        var prompt = new Prompt(MallUnclearDecision.WasSpent);
        var coordinator = new MallPurchaseCoordinator("mallbot",
            _ => Task.FromResult<(DesktopMallPurchaseService, MallPurchaseObserver)?>(null),
            null!, new SessionLogViewModel(now: () => Now), new MallSpendLedger(new Fixed()), null, prompt, null);
        await coordinator.ReconcileUnclearAsync(default);
        Assert.Equal(0, prompt.Asked);
    }

    [Fact]
    public void ResolveUnclear_refuses_a_changed_amount()
    {
        var ledger = new MallSpendLedger(new Fixed());
        Assert.True(ledger.TryReserve("mallbot", 100, null, out _));
        ledger.MarkUnclear("mallbot", 100);
        Assert.False(ledger.ResolveUnclear("mallbot", 99, wasSpent: true));
        Assert.Equal(100, ledger.UnclearAmount("mallbot"));
        Assert.True(ledger.ResolveUnclear("mallbot", 100, wasSpent: false));
        Assert.Equal(0, ledger.SpentToday("mallbot"));
    }

    [Fact]
    public async Task OneStepBuy_reads_once_and_buys_at_the_price_just_read()
    {
        var handler = new Handler(_ => Ok("You acquire an item: 11-leaf clover"));
        using var http = new HttpClient(handler);
        var beforeReads = 0;
        var before = State(meat: 100_000, inv: 0, price: 200);   // cheaper than the shown 222
        using var transport = new HttpMallPurchaseTransport(http, new HttpMallPurchaseTransportOptions(),
            (_, _) => { beforeReads++; return Task.FromResult(before); },
            (_, _) => Task.FromResult(State(meat: 100_000 - 200 + 40, inv: 1, price: 200)));
        var service = new DesktopMallPurchaseService(transport,
            new DesktopMallPurchaseOptions { ReconcileDelay = TimeSpan.Zero }, null, new Fixed());
        var result = await service.BuyAsync("mallbot", "789", "123", 222, 1);
        Assert.Equal(DesktopMallPurchaseStatus.Purchased, result.Status);
        Assert.Equal(200, result.SpentMeat);
        Assert.Equal(1, beforeReads);          // no separate preview round trip
        Assert.Single(handler.Bodies);
        Assert.Contains("whichitem=789000000200", handler.Bodies[0]);
    }

    [Fact]
    public async Task OneStepBuy_refuses_only_when_the_price_rose_and_sends_nothing()
    {
        var handler = new Handler(_ => Ok("You acquire an item"));
        using var http = new HttpClient(handler);
        var before = State(meat: 100_000, inv: 0, price: 300);
        using var transport = new HttpMallPurchaseTransport(http, new HttpMallPurchaseTransportOptions(),
            (_, _) => Task.FromResult(before), (_, _) => Task.FromResult(before));
        var service = new DesktopMallPurchaseService(transport,
            new DesktopMallPurchaseOptions { ReconcileDelay = TimeSpan.Zero }, null, new Fixed());
        var result = await service.BuyAsync("mallbot", "789", "123", 222, 1);
        Assert.Equal(DesktopMallPurchaseStatus.PriceChanged, result.Status);
        Assert.Contains("rose from 222 to 300", result.Message);
        Assert.Empty(handler.Bodies);
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static MallOfferRow Row() => new()
    {
        Rank = 1, ItemId = "789", ItemName = "11-leaf clover", ShopId = "123", ShopName = "S", UnitPrice = 222,
        Stock = 5, LimitKind = MallLimitKind.Unlimited
    };

    /// <summary>Runs one purchase. <paramref name="afterReads"/> are served in order; the last one repeats.</summary>
    private static async Task<(DesktopMallPurchaseResult Result, int AfterReads, int Posts)> Run(
        MallPurchaseObservation before, MallPurchaseObservation[] afterReads, int qty)
    {
        var handler = new Handler(_ => Ok("You acquire an item: 11-leaf clover"));
        using var http = new HttpClient(handler);
        int verifyCalls = 0, rereadCalls = 0;
        // The transport's own after-read serves afterReads[0]; the gateway's re-reads serve the rest (last repeats).
        using var transport = new HttpMallPurchaseTransport(http, new HttpMallPurchaseTransportOptions(),
            (_, _) => Task.FromResult(before),
            (_, _) => { verifyCalls++; return Task.FromResult(afterReads[0]); });
        var service = new DesktopMallPurchaseService(
            new Rereading(transport, () => afterReads[Math.Min(1 + rereadCalls++, afterReads.Length - 1)]),
            new DesktopMallPurchaseOptions { ReconcileDelay = TimeSpan.Zero }, null, new Fixed());
        var preview = await service.PreviewAsync("mallbot", "789", "123", 222, qty);
        var result = await service.ConfirmAsync(preview);
        return (result, verifyCalls + rereadCalls, handler.Bodies.Count);
    }

    /// <summary>First RefreshAsync is the pre-POST read (before); later ones are re-reads.</summary>
    private sealed class Rereading(HttpMallPurchaseTransport inner, Func<MallPurchaseObservation> reread) : IMallPurchaseTransport
    {
        private int _refreshes;
        public Task<MallPurchaseObservation> RefreshAsync(MallPurchaseRequest request, CancellationToken ct = default)
            => ++_refreshes <= 2 ? inner.RefreshAsync(request, ct) : Task.FromResult(reread());   // preview + commit-time read
        public Task<MallPurchaseTransportReceipt> CommitAsync(MallPurchaseTransportCommit request, CancellationToken ct = default)
            => inner.CommitAsync(request, ct);
    }

    private static MallPurchaseObservation State(long meat, int inv, long price = 222)
        => new(new MallPurchaseOffer("123", "123", "789", price, 5, Now),
            new MallPurchaseAccountState("mallbot", meat, 0, 1_000_000,
                new Dictionary<string, int> { ["789"] = inv }, Now));

    private static HttpResponseMessage Ok(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    private sealed class Fixed : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Prompt(MallUnclearDecision decision) : IMallReconcilePrompt
    {
        public int Asked { get; private set; }
        public long AmountShown { get; private set; }
        public Task<MallUnclearDecision> AskAsync(string account, long amount, string evidence, CancellationToken ct)
        {
            Asked++;
            AmountShown = amount;
            return Task.FromResult(decision);
        }
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
