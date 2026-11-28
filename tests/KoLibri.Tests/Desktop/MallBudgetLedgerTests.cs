using KoLibri.Desktop;
using Xunit;

namespace KoLibri.Tests.Desktop;

/// <summary>BUDGET-05: the desktop callers of the spend ledger finish a reservation once and respect the KoL day.</summary>
public sealed class MallBudgetLedgerTests
{
    private static readonly DateTimeOffset Now = MallKit.Now;

    private static (DesktopSettings Settings, MallSpendLedger Ledger) Bacon()
    {
        var settings = new DesktopSettings();
        settings.Set("meatPerDay", 100_000);
        settings.Set("meatPerPurchase", 50_000);
        settings.Set("unitsPerPurchase", 4);
        return (settings, new MallSpendLedger(new MallKit.Clock(Now)));
    }

    [Fact]
    public void Finishing_the_same_bacon_budget_twice_books_once()   // review #3
    {
        var (settings, ledger) = Bacon();
        var budget = DailyBaconBudget.Reserve(settings, ledger, "Main", 20, 1500, 10_000_000, 200_000);
        Assert.True(budget.Allowed);
        Assert.False(budget.Reservation.IsEmpty);

        DailyBaconBudget.Finish(ledger, "Main", budget, spent: 3_000, unclear: false);
        DailyBaconBudget.Finish(ledger, "Main", budget, spent: 3_000, unclear: false);
        Assert.Equal(3_000, ledger.SpentToday("Main"));
        Assert.Equal(3_000, ledger.CountedToday("Main"));
        Assert.Equal(0, ledger.ReservedAmount("Main"));
    }

    [Fact]
    public void Finishing_a_bacon_budget_without_a_reservation_handle_books_once_and_releases_the_rest()   // review #2/#3
    {
        var (settings, ledger) = Bacon();
        var budget = DailyBaconBudget.Reserve(settings, ledger, "Main", 20, 1500, 10_000_000, 200_000) with { Reservation = default };
        Assert.Equal(6_000, ledger.CountedToday("Main"));

        DailyBaconBudget.Finish(ledger, "Main", budget, spent: 3_000, unclear: false);   // 3k settled, 3k released
        Assert.Equal(3_000, ledger.SpentToday("Main"));
        Assert.Equal(3_000, ledger.CountedToday("Main"));
        Assert.Equal(0, ledger.ReservedAmount("Main"));
        DailyBaconBudget.Finish(ledger, "Main", budget, spent: 3_000, unclear: false);   // nothing open any more: nothing
        Assert.Equal(3_000, ledger.CountedToday("Main"));
    }

    private sealed class GatedEngine : IMallBuyEngine
    {
        public readonly TaskCompletionSource ReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource FirstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Sends;

        public async Task<MallAccountReading> ReadAccountAsync(CancellationToken ct)
        {
            ReadStarted.TrySetResult();
            await FirstRead.Task.WaitAsync(ct);
            return new MallAccountReading(100_000, new Dictionary<string, int> { ["789"] = 0 }, true);
        }

        public Task<DesktopMallPurchaseResult> SendAsync(MallBuyOrder order, CancellationToken ct)
        {
            Interlocked.Increment(ref Sends);
            return Task.FromResult(new DesktopMallPurchaseResult
            {
                Status = DesktopMallPurchaseStatus.Purchased, Confirmed = true, UnitPrice = order.ShownUnitPrice,
                ReceivedQuantity = order.Quantity, Message = "ok"
            });
        }
    }

    [Theory]
    [InlineData(800, MallBuyState.NotSent, 0)]
    [InlineData(500, MallBuyState.Settled, 1)]
    public async Task A_buy_reconciled_while_waiting_is_sent_only_if_it_can_reserve_again(
        long otherReservation, MallBuyState expectedState, int expectedSends)
    {
        var dir = Directory.CreateTempSubdirectory("money-").FullName;
        try
        {
            var path = Path.Combine(dir, "mall.json");
            var ledger = new MallSpendLedger(new MallKit.Clock(Now), path);
            var reconciler = new MallSpendLedger(new MallKit.Clock(Now), path);
            var other = new MallSpendLedger(new MallKit.Clock(Now), path);
            var log = new SessionLogViewModel(now: () => Now, post: MallBuyQueueTests.SerialPost());
            using var queue = new MallBuyQueue("Mallbot", ledger, log, verifyDelay: TimeSpan.Zero, batchWait: TimeSpan.Zero);
            var options = new DesktopMallPurchaseOptions { MaxDailySpend = 1_000, MaxMeatPerPurchase = 1_000, MaxQuantityPerPurchase = 5 };
            var engine = new GatedEngine();

            var ticket = queue.Enqueue(new MallBuyOrder("789", "Thing", "42", "Shop", 100, 5), engine, options);
            Assert.Equal(500, ticket.Reserved);
            await engine.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var snapshot = reconciler.ReadUnclear("Mallbot");
            Assert.Equal(500, snapshot.Foreign);
            Assert.True(reconciler.Reconcile("Mallbot", snapshot, 0));
            Assert.True(other.TryReserve("Mallbot", otherReservation, 1_000, out _, out _));

            engine.FirstRead.SetResult();
            await queue.WhenIdleAsync();

            Assert.Equal(expectedState, ticket.State);
            Assert.Equal(expectedSends, engine.Sends);
            Assert.Equal(otherReservation, other.ReservedAmount("Mallbot"));
            Assert.Equal(otherReservation + 500L * expectedSends, ledger.CountedToday("Mallbot"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task A_buy_queued_yesterday_is_not_sent_when_todays_budget_is_used_up()   // review #8
    {
        var clock = new MallKit.Clock(Now);
        var ledger = new MallSpendLedger(clock);
        var log = new SessionLogViewModel(now: () => Now, post: MallBuyQueueTests.SerialPost());
        using var queue = new MallBuyQueue("Mallbot", ledger, log, verifyDelay: TimeSpan.Zero, batchWait: TimeSpan.Zero);
        var options = new DesktopMallPurchaseOptions { MaxDailySpend = 1_000, MaxMeatPerPurchase = 1_000, MaxQuantityPerPurchase = 5 };
        var engine = new GatedEngine();

        var ticket = queue.Enqueue(new MallBuyOrder("789", "Thing", "42", "Shop", 100, 5), engine, options);   // 500 on day 1
        Assert.Equal(500, ticket.Reserved);

        clock.Value = Now.AddDays(1);
        Assert.True(ledger.TryReserve("Mallbot", 800, 1_000, out _, out _));   // day 2's budget is used elsewhere
        engine.FirstRead.SetResult();   // only now does the sender run
        await queue.WhenIdleAsync();

        Assert.Equal(MallBuyState.NotSent, ticket.State);
        Assert.Equal(0, engine.Sends);
        Assert.Equal(800, ledger.CountedToday("Mallbot"));   // nothing of the old ticket is counted on day 2
    }

    [Fact]
    public async Task A_buy_queued_yesterday_is_reserved_again_on_the_new_day_and_sent_when_it_fits()   // review #8
    {
        var clock = new MallKit.Clock(Now);
        var ledger = new MallSpendLedger(clock);
        var log = new SessionLogViewModel(now: () => Now, post: MallBuyQueueTests.SerialPost());
        using var queue = new MallBuyQueue("Mallbot", ledger, log, verifyDelay: TimeSpan.Zero, batchWait: TimeSpan.Zero);
        var options = new DesktopMallPurchaseOptions { MaxDailySpend = 1_000, MaxMeatPerPurchase = 1_000, MaxQuantityPerPurchase = 5 };
        var engine = new GatedEngine();

        var ticket = queue.Enqueue(new MallBuyOrder("789", "Thing", "42", "Shop", 100, 5), engine, options);
        clock.Value = Now.AddDays(1);
        engine.FirstRead.SetResult();
        await queue.WhenIdleAsync();

        Assert.Equal(MallBuyState.Settled, ticket.State);
        Assert.Equal(1, engine.Sends);
        Assert.Equal(500, ledger.SpentToday("Mallbot"));   // booked on day 2, where the new reservation was made
        Assert.Equal(500, ledger.CountedToday("Mallbot"));
    }

    [Fact]
    public async Task A_buy_whose_reservation_was_partially_released_is_not_sent_without_full_budget_coverage()   // final review #1b
    {
        var dir = Directory.CreateTempSubdirectory("money-").FullName;
        try
        {
            var path = Path.Combine(dir, "mall.json");
            var ledger = new MallSpendLedger(new MallKit.Clock(Now), path);
            var other = new MallSpendLedger(new MallKit.Clock(Now), path);
            var log = new SessionLogViewModel(now: () => Now, post: MallBuyQueueTests.SerialPost());
            using var queue = new MallBuyQueue("Mallbot", ledger, log, verifyDelay: TimeSpan.Zero, batchWait: TimeSpan.Zero);
            var options = new DesktopMallPurchaseOptions { MaxDailySpend = 500, MaxMeatPerPurchase = 500, MaxQuantityPerPurchase = 5 };
            var engine = new GatedEngine();

            var ticket = queue.Enqueue(new MallBuyOrder("789", "Thing", "42", "Shop", 100, 5), engine, options);   // 500 reserved
            Assert.Equal(500, ticket.Reserved);
            await engine.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            ledger.Release("Mallbot", 400);   // leaves 100 under the same reservation id
            Assert.True(other.TryReserve("Mallbot", 400, 500, out _, out _));

            engine.FirstRead.SetResult();
            await queue.WhenIdleAsync();

            Assert.Equal(MallBuyState.NotSent, ticket.State);
            Assert.Equal(0, engine.Sends);
            Assert.Equal(400, other.ReservedAmount("Mallbot"));
            Assert.Equal(400, ledger.CountedToday("Mallbot"));   // the leftover 100 of the cancelled ticket is also cleaned up
        }
        finally { Directory.Delete(dir, true); }
    }
}
