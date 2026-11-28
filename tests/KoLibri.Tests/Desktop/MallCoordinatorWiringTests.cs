using KoLibri.Extras;
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

    private sealed class FakeReads(long meat, int owned) : IMallPurchaseReads
    {
        public long Meat = meat;
        public int Owned = owned;
        public Task<Status?> StatusAsync(CancellationToken ct)
            => Task.FromResult<Status?>(new Status("Bot", 10, Meat, 1, Now));
        public Task<DesktopInventorySnapshot> InventoryAsync(CancellationToken ct)
            => Task.FromResult(new DesktopInventorySnapshot
            {
                Role = AccountRole.Mallbot,
                Items = [new DesktopInventoryItem { ItemId = "789", Name = "Thing", Quantity = Owned }],
                ObservedAt = Now
            });
    }

    /// <summary>Stands in for Mall.BuyAsync: counts purchases and answers with a scripted library result.</summary>
    private sealed class FakeMall(Func<PurchaseQuote, PurchaseResult> respond)
    {
        public int Buys;
        public PurchaseQuote? Last;
        public async Task<PurchaseResult> BuyAsync(PurchaseQuote quote,
            Func<CancellationToken, ValueTask<GuardDecision>>? beforeEachSend, CancellationToken ct)
        {
            if (beforeEachSend is not null && await beforeEachSend(ct) is { Allowed: false } denied)
            {
                var line = quote.Plan[0];
                return new PurchaseResult(PurchaseStatus.Refused, 0, 0,
                    [new PurchaseLine(line.StoreId, line.UnitPrice, line.Quantity, 0, denied.Reason)]);
            }
            Buys++;
            Last = quote;
            return respond(quote);
        }

        public static FakeMall Delivering() => new(q => new PurchaseResult(PurchaseStatus.Bought, q.PlannedQuantity,
            q.PlannedQuantity * q.Order.MaxUnitPrice, [new PurchaseLine(q.Plan[0].StoreId, q.Order.MaxUnitPrice, q.PlannedQuantity, q.PlannedQuantity, null)]));
    }

    [Fact]
    public async Task ServiceReadsMeatAndInventoryFresh()
    {
        var service = new DesktopMallPurchaseService(new FakeReads(10_000, 2), FakeMall.Delivering().BuyAsync);
        var reading = await service.ReadAccountAsync(default);
        Assert.True(reading.Fresh);
        Assert.Equal(10_000, reading.Meat);
        Assert.Equal(2, reading.Items["789"]);
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

    private static (MallPurchaseCoordinator Coordinator, FakeMall Mall, SessionLogViewModel Log, MallSpendLedger Ledger)
        Build(Prompt prompt, bool signedIn = true, FakeMall? mall = null, MallSpendLedger? ledger = null)
    {
        mall ??= FakeMall.Delivering();
        var log = new SessionLogViewModel(now: () => Now, post: MallBuyQueueTests.SerialPost()); // log writes serialized like the UI thread
        ledger ??= new MallSpendLedger(new FixedTime());
        var service = new DesktopMallPurchaseService(new FakeReads(10_000, 0), mall.BuyAsync);
        var queue = new MallBuyQueue("Mallbot", ledger, log, verifyDelay: TimeSpan.Zero, batchWait: TimeSpan.Zero);
        var coordinator = new MallPurchaseCoordinator("Mallbot",
            _ => Task.FromResult<DesktopMallPurchaseService?>(signedIn ? service : null),
            prompt, log, ledger, queue: queue);
        return (coordinator, mall, log, ledger);
    }

    [Fact]
    public async Task ConfirmedBuySendsOnePurchaseAndRecordsSpend()
    {
        var prompt = new Prompt(true);
        var (coordinator, mall, log, ledger) = Build(prompt);
        Assert.True(coordinator.CanRequest(Row()));
        await coordinator.RequestAsync(Row(), default);
        await coordinator.Queue.WhenIdleAsync();
        Assert.Equal(1, mall.Buys);
        Assert.Equal(2, prompt.Quantity);
        Assert.Equal(100, mall.Last!.Order.MaxUnitPrice);       // never above the price the row showed
        Assert.Equal(42, mall.Last.Plan.Single().StoreId);      // from the shop the user clicked
        Assert.Equal(200, ledger.SpentToday("Mallbot"));
        Assert.Contains(log.Entries, e => e.Kind == SessionLogKind.Purchase && e.Message.StartsWith("#1 Settled:"));
    }

    [Fact]
    public async Task CancelledPromptSendsNothing()
    {
        var (coordinator, mall, log, ledger) = Build(new Prompt(false));
        await coordinator.RequestAsync(Row(), default);
        Assert.Equal(0, mall.Buys);
        Assert.Equal(0, ledger.SpentToday("Mallbot"));
        Assert.Contains(log.Entries, e => e.Message.Contains("nothing was sent"));
    }

    [Fact]
    public async Task SignedOutAndSoldOutOffersDoNotBuy()
    {
        var prompt = new Prompt(true);
        var (coordinator, mall, log, _) = Build(prompt, signedIn: false);
        Assert.False(coordinator.CanRequest(Row(stock: 0)));
        await coordinator.RequestAsync(Row(), default);
        Assert.Equal(0, prompt.Asked);
        Assert.Equal(0, mall.Buys);
        Assert.Equal(1, log.ErrorCount);
    }

    private static string TempLedger() => Path.Combine(Path.GetTempPath(), "kolibri-ledger-" + Guid.NewGuid().ToString("N"), "ledger.json");

    [Fact]
    public async Task TheBudgetShownCountsAForeignReservationOnce()   // review #10
    {
        var dir = Directory.CreateTempSubdirectory("money-").FullName;
        try
        {
            var path = Path.Combine(dir, "ledger.json");
            Assert.True(new MallSpendLedger(new FixedTime(), path).TryReserve("Mallbot", 40, null, out _, out _));   // another process
            var ledger = new MallSpendLedger(new FixedTime(), path);
            var log = new SessionLogViewModel(now: () => Now, post: MallBuyQueueTests.SerialPost());
            var service = new DesktopMallPurchaseService(new FakeReads(10_000, 0), FakeMall.Delivering().BuyAsync,
                new DesktopMallPurchaseOptions { MaxDailySpend = 100, MaxMeatPerPurchase = 1_000, MaxQuantityPerPurchase = 5 });
            var prompt = new Prompt(false);
            var coordinator = new MallPurchaseCoordinator("Mallbot",
                _ => Task.FromResult<DesktopMallPurchaseService?>(service), prompt, log, ledger,
                queue: new MallBuyQueue("Mallbot", ledger, log, verifyDelay: TimeSpan.Zero, batchWait: TimeSpan.Zero));

            await coordinator.RequestAsync(Row(), default);
            Assert.Equal(1, prompt.Asked);
            Assert.Equal(60, prompt.Remaining.Budget);   // 100 - 40, not 100 - 40 - 40
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task StaleRowIsRefusedWithoutAskingOrBuying()
    {
        // The library never serves an old page, but a row the view marked stale is still never bought.
        var prompt = new Prompt(true);
        var (coordinator, mall, log, _) = Build(prompt);
        var row = Row() with { IsStale = true };
        Assert.False(coordinator.CanRequest(row));
        await coordinator.RequestAsync(row, default);
        Assert.Equal(0, prompt.Asked);
        Assert.Equal(0, mall.Buys);
        Assert.Contains(log.Entries, e => e.Message.Contains("stale"));
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
            var snapshot = second.ReadUnclear("Mallbot");
            Assert.Equal(50, snapshot.Total);
            Assert.True(second.Reconcile("Mallbot", snapshot, 0));
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

    [Theory]
    [InlineData(PurchaseStatus.Unclear, 200)]          // may have been bought: stays counted after a restart
    [InlineData(PurchaseStatus.NothingBought, 0)]      // the listing was gone, nothing sent: released
    [InlineData(PurchaseStatus.PriceChanged, 0)]
    public async Task UnclearPurchaseStaysCountedAndNotSentIsReleased(PurchaseStatus status, long expectedSpent)
    {
        var path = TempLedger();
        try
        {
            var ledger = new MallSpendLedger(new FixedTime(), path);
            var mall = new FakeMall(q => new PurchaseResult(status, 0, 0,
                status == PurchaseStatus.NothingBought ? [new PurchaseLine(42, 100, 2, 0, "The listing is gone.")]
                : status == PurchaseStatus.PriceChanged ? [new PurchaseLine(42, 150, 2, 0, "The price rose.")]
                : []));
            var (coordinator, _, _, _) = Build(new Prompt(true), mall: mall, ledger: ledger);
            await coordinator.RequestAsync(Row(), default);
            await coordinator.Queue.WhenIdleAsync();
            Assert.Equal(1, mall.Buys);
            Assert.Equal(expectedSpent, new MallSpendLedger(new FixedTime(), path).SpentToday("Mallbot"));
            Assert.Equal(expectedSpent, coordinator.UnclearAmount);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }
}
