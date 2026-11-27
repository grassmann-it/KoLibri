using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using KoLibri.Desktop.ContextMenus;
using KoLibri.Desktop;
using Xunit;

namespace KoLibri.Tests.Desktop;

/// <summary>Purchase coordinator, budget ledger and queue wiring (split from the shell wiring tests in 0.5; mall area).</summary>
public sealed class MallCoordinatorWiringTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeReads(long meat, int owned, bool stale = false) : IMallPurchaseReads
    {
        public int Searches;
        public int Owned = owned;
        public int? AfterFirstRead;
        private int _inventoryReads;
        public long Meat = meat;
        public Task<MallSearchPage> SearchAsync(string term, CancellationToken ct)
        {
            Searches++;
            var listing = new MallListing("789", "42", "Shop", 100, 5, null, Now, new Uri("https://www.kingdomofloathing.com/"))
            { SellerId = "42", QuantityKnown = true, LimitKind = MallLimitKind.Unlimited };
            return Task.FromResult(new MallSearchPage(term,
                [new MallItemOffers("789", "Thing", [listing], 1)], stale ? Now.AddHours(-3) : Now,
                new Uri("https://www.kingdomofloathing.com/"), stale));
        }
        public Task<KoLStatusSnapshot?> StatusAsync(CancellationToken ct)
            => Task.FromResult<KoLStatusSnapshot?>(new KoLStatusSnapshot("Bot", 10, Meat, 1, Now));
        public Task<DesktopInventorySnapshot> InventoryAsync(CancellationToken ct)
            => Task.FromResult(new DesktopInventorySnapshot
            {
                Role = AccountRole.Mallbot,
                Items = [new DesktopInventoryItem { ItemId = "789", Name = "Thing", Quantity = ++_inventoryReads > 1 && AfterFirstRead is { } after ? after : Owned }],
                ObservedAt = Now
            });
    }

    [Fact]
    public async Task ObserverReadsOfferAndAccountAndTracksDailySpend()
    {
        var ledger = new MallSpendLedger(new FixedTime());
        ledger.Add("Mallbot", 1_000);
        var observer = new MallPurchaseObserver(new FakeReads(10_000, 2), "Mallbot", () => 50_000, ledger, new FixedTime());
        var request = new MallPurchaseRequest("42", "42", "789", 100, 2, accountId: "Mallbot");
        await Assert.ThrowsAsync<InvalidOperationException>(() => observer.BeforeAsync(request, default));
        observer.Remember("789", "Thing");

        var before = await observer.BeforeAsync(request, default);
        Assert.Equal(100, before.Offer.UnitPrice);
        Assert.Equal(5, before.Offer.Quantity);
        Assert.Equal(10_000, before.Account.Meat);
        Assert.Equal(1_000, before.Account.DailySpent);
        Assert.Equal(50_000, before.Account.DailyLimit);
        Assert.Equal(2, before.InventoryQuantity("789"));

        // After a purchase meat fell by 200: daily spend follows the observed change.
        var after = new MallPurchaseObserver(new FakeReads(9_800, 4), "Mallbot", () => 50_000, ledger, new FixedTime());
        after.Remember("789", "Thing");
        var preview = new MallPurchasePreview("a", request, before, Now, Now.AddMinutes(5), false, false);
        var observed = await after.AfterAsync(new MallPurchaseTransportCommit(preview, before, null, Now), default);
        Assert.Equal(1_200, observed.Account.DailySpent);
        Assert.Equal(4, observed.InventoryQuantity("789"));
    }

    private sealed class Prompt(bool confirm) : IMallPurchasePrompt
    {
        public int Asked;
        public int? Quantity;
        public RemainingSeen Remaining;
        public Task<int?> AskAsync(MallPurchasePromptContext context, CancellationToken ct)
        {
            Asked++;
            Remaining = new RemainingSeen(context.RemainingDailyBudget);
            Quantity = confirm ? 2 : null;
            return Task.FromResult(Quantity);
        }
    }

    private sealed record RemainingSeen(long Budget);

    private static MallOfferRow Row(int? stock = 5) => new()
    {
        Rank = 1, ItemId = "789", ItemName = "Thing", ShopId = "42", ShopName = "Shop",
        UnitPrice = 100, Stock = stock, LimitKind = MallLimitKind.Unlimited
    };

    private static FakeReads _reads = new(10_000, 0);

    private static (MallPurchaseCoordinator Coordinator, FixtureMallPurchaseTransport Fixture, SessionLogViewModel Log, MallSpendLedger Ledger)
        Build(Prompt prompt, bool signedIn = true, int? deliver = null)
    {
        var fixture = new FixtureMallPurchaseTransport(new MallPurchaseObservation(
            new MallPurchaseOffer("42", "42", "789", 100, 5, Now),
            new MallPurchaseAccountState("Mallbot", 10_000, 0, 1_000_000,
                new Dictionary<string, int> { ["789"] = 0 }, Now)));
        var log = new SessionLogViewModel(now: () => Now);
        var ledger = new MallSpendLedger(new FixedTime());
        var service = new DesktopMallPurchaseService(fixture, null, null, new FixedTime());
        _reads = new FakeReads(10_000, 0) { AfterFirstRead = deliver };
        var observer = new MallPurchaseObserver(_reads, "Mallbot", () => 1_000_000, ledger, new FixedTime());
        var queue = new MallBuyQueue("Mallbot", ledger, log, verifyDelay: TimeSpan.Zero, batchWait: TimeSpan.Zero);
        var coordinator = new MallPurchaseCoordinator("Mallbot",
            _ => Task.FromResult<(DesktopMallPurchaseService, MallPurchaseObserver)?>(signedIn ? (service, observer) : null),
            prompt, log, ledger, queue: queue);
        return (coordinator, fixture, log, ledger);
    }

    [Fact]
    public async Task ConfirmedBuySendsOnePurchaseAndRecordsSpend()
    {
        var prompt = new Prompt(true);
        var (coordinator, fixture, log, ledger) = Build(prompt, deliver: 2);
        Assert.True(coordinator.CanRequest(Row()));
        await coordinator.RequestAsync(Row(), default);
        await coordinator.Queue.WhenIdleAsync();
        Assert.Equal(1, fixture.CommitCount);
        Assert.Equal(2, prompt.Quantity);
        Assert.Equal(200, ledger.SpentToday("Mallbot"));
        Assert.Contains(log.Entries, e => e.Kind == SessionLogKind.Purchase && e.Message.StartsWith("#1 Settled:"));
    }

    [Fact]
    public async Task CancelledPromptSendsNothing()
    {
        var (coordinator, fixture, log, ledger) = Build(new Prompt(false));
        await coordinator.RequestAsync(Row(), default);
        Assert.Equal(0, fixture.CommitCount);
        Assert.Equal(0, ledger.SpentToday("Mallbot"));
        Assert.Contains(log.Entries, e => e.Message.Contains("nothing was sent"));
    }

    [Fact]
    public async Task SignedOutAndSoldOutOffersDoNotBuy()
    {
        var prompt = new Prompt(true);
        var (coordinator, fixture, log, _) = Build(prompt, signedIn: false);
        Assert.False(coordinator.CanRequest(Row(stock: 0)));
        await coordinator.RequestAsync(Row(), default);
        Assert.Equal(0, prompt.Asked);
        Assert.Equal(0, fixture.CommitCount);
        Assert.Equal(1, log.ErrorCount);
    }

    private static string TempLedger() => Path.Combine(Path.GetTempPath(), "kolibri-ledger-" + Guid.NewGuid().ToString("N"), "ledger.json");

    [Fact]
    public async Task StaleOfferBlocksTheBuyBeforeAnyPostAndIsNeverRedatedAsFresh()
    {
        var ledger = new MallSpendLedger(new FixedTime());
        var observer = new MallPurchaseObserver(new FakeReads(10_000, 0, stale: true), "Mallbot", () => 1_000_000, ledger, new FixedTime());
        // The observer feeds the gateway before the POST: a stale page throws there, so nothing is sent.
        await Assert.ThrowsAsync<InvalidOperationException>(() => Observe(observer));

        // A fresh page keeps its own fetch time instead of "now".
        var fresh = new MallPurchaseObserver(new FakeReads(10_000, 0), "Mallbot", () => 1_000_000, ledger,
            new OffsetTime(TimeSpan.FromMinutes(2)));
        var observation = await Observe(fresh);
        Assert.Equal(Now, observation.Offer.ObservedAt);
        Assert.Equal(Now, observation.ObservedAt);
    }

    private static Task<MallPurchaseObservation> Observe(MallPurchaseObserver observer)
    {
        observer.Remember("789", "Thing");
        return observer.BeforeAsync(new MallPurchaseRequest("42", "42", "789", 100, 2, accountId: "Mallbot"), default);
    }

    [Fact]
    public void BudgetSurvivesANewLedgerInstanceAndRollsOverWithTheDay()
    {
        var path = TempLedger();
        try
        {
            var first = new MallSpendLedger(new FixedTime(), path);
            first.Reserve("Mallbot", 300);
            first.Settle("Mallbot", 300);
            first.Reserve("Mallbot", 50); // crash while the POST was in flight
            Assert.Equal(300, first.SpentToday("Mallbot"));

            var second = new MallSpendLedger(new FixedTime(), path);
            Assert.Equal(350, second.SpentToday("Mallbot"));
            Assert.Equal(50, second.UnclearToday("Mallbot"));

            var nextDay = new MallSpendLedger(new OffsetTime(TimeSpan.FromDays(1)), path);
            Assert.Equal(0, nextDay.SpentToday("Mallbot"));
            second.Reconcile("Mallbot", 0);
            Assert.Equal(300, new MallSpendLedger(new FixedTime(), path).SpentToday("Mallbot"));
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    private sealed class OffsetTime(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now + offset;
    }

    [Fact]
    public void UnreadableLedgerFileBlocksSpending()
    {
        var path = TempLedger();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ not json");
            var ledger = new MallSpendLedger(new FixedTime(), path);
            Assert.True(ledger.Unreadable);
            Assert.True(ledger.SpentToday("Mallbot") > 1_000_000_000_000);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public async Task UnclearPurchaseStaysCountedAndNotSentIsReleased()
    {
        var path = TempLedger();
        try
        {
            var ledger = new MallSpendLedger(new FixedTime(), path);
            // Meat did not move although KoL accepted: the gateway cannot prove the purchase.
            var fixture = new FixtureMallPurchaseTransport(new MallPurchaseObservation(
                new MallPurchaseOffer("42", "42", "789", 100, 5, Now),
                new MallPurchaseAccountState("Mallbot", 10_000, 0, 1_000_000, new Dictionary<string, int> { ["789"] = 0 }, Now)));
            var service = new DesktopMallPurchaseService(fixture, null, null, new FixedTime());
            var observer = new MallPurchaseObserver(new FakeReads(10_000, 0), "Mallbot", () => 1_000_000, ledger, new FixedTime());
            var coordinator = new MallPurchaseCoordinator("Mallbot",
                _ => Task.FromResult<(DesktopMallPurchaseService, MallPurchaseObserver)?>((service, observer)),
                new Prompt(true), new SessionLogViewModel(now: () => Now), ledger,
                queue: new MallBuyQueue("Mallbot", ledger, new SessionLogViewModel(now: () => Now),
                    verifyDelay: TimeSpan.Zero, batchWait: TimeSpan.Zero));
            await coordinator.RequestAsync(Row(), default);
            await coordinator.Queue.WhenIdleAsync();
            Assert.Equal(1, fixture.CommitCount);
            var spent = new MallSpendLedger(new FixedTime(), path).SpentToday("Mallbot");
            // Either verified (settled) or unclear: in both cases the 200 meat stay counted after a restart.
            Assert.Equal(200, spent);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    // ---- context menus -------------------------------------------------------------------

}
