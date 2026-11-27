using KoLibri.Desktop;
using Xunit;

namespace KoLibri.Tests.Desktop;

/// <summary>Queued background buys: own budget slice each, lanes per item, deferred verification for engines that ask for it.</summary>
public sealed class MallBuyQueueTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly DesktopMallPurchaseOptions Options = new() { MaxDailySpend = 10_000, MaxMeatPerPurchase = 1_000, MaxQuantityPerPurchase = 5 };

    private sealed class Fixed : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }

    private sealed class Engine : IMallBuyEngine
    {
        public Dictionary<string, int> Items = new() { ["789"] = 0 };
        public int Reads, Sends;
        public Func<MallBuyOrder, DesktopMallPurchaseResult>? Respond;
        public Action<MallBuyOrder>? OnSend;          // e.g. deliver items
        public TaskCompletionSource? ReadGate;        // blocks verification reads (not the baseline read)
        public bool GateOn;

        public async Task<MallAccountReading> ReadAccountAsync(CancellationToken ct)
        {
            var n = Interlocked.Increment(ref Reads);
            if (GateOn && n > 1 && ReadGate is { } gate) await gate.Task;
            return new MallAccountReading(100_000, new Dictionary<string, int>(Items), true);
        }

        public Task<DesktopMallPurchaseResult> SendAsync(MallBuyOrder order, CancellationToken ct)
        {
            Interlocked.Increment(ref Sends);
            OnSend?.Invoke(order);
            return Task.FromResult(Respond?.Invoke(order) ?? new DesktopMallPurchaseResult
            {
                Status = DesktopMallPurchaseStatus.Sent, Confirmed = true, UnitPrice = order.ShownUnitPrice, Message = "ok"
            });
        }
    }

    private static MallBuyOrder Order(int qty = 1, long price = 100) => new("789", "Thing", "42", "Shop", price, qty);

    private static (MallBuyQueue Queue, MallSpendLedger Ledger, SessionLogViewModel Log) Make()
    {
        var log = new SessionLogViewModel(now: () => Now);
        var ledger = new MallSpendLedger(new Fixed());
        return (new MallBuyQueue("Mallbot", ledger, log, verifyDelay: TimeSpan.Zero, batchWait: TimeSpan.Zero), ledger, log);
    }

    [Fact]
    public async Task Several_buys_are_queued_sent_and_verified_with_one_inventory_read()
    {
        var (queue, ledger, log) = Make();
        var engine = new Engine();
        engine.OnSend = o => engine.Items["789"] += o.Quantity;
        var tickets = new[] { queue.Enqueue(Order(), engine, Options), queue.Enqueue(Order(2), engine, Options), queue.Enqueue(Order(), engine, Options) };
        await queue.WhenIdleAsync();
        Assert.All(tickets, t => Assert.Equal(MallBuyState.Settled, t.State));
        Assert.Equal(3, engine.Sends);
        Assert.Equal(2, engine.Reads);            // one baseline read + one verification for the whole batch (or two at most)
        Assert.Equal(400, ledger.SpentToday("Mallbot"));
        Assert.Equal(0, ledger.UnclearToday("Mallbot"));
        Assert.Equal(3, log.Entries.Count(e => e.Message.Contains(" Settled: ")));
        Assert.Contains("3 buys", queue.TotalText);
    }

    [Fact]
    public async Task Each_buy_reserves_its_own_slice_up_front_and_overrun_is_refused_before_queueing()
    {
        var (queue, ledger, _) = Make();
        var engine = new Engine();
        var tight = Options with { MaxDailySpend = 250 };
        var a = queue.Enqueue(Order(), engine, tight);
        var b = queue.Enqueue(Order(), engine, tight);
        var c = queue.Enqueue(Order(), engine, tight);   // 300 > 250
        Assert.Equal(MallBuyState.NotSent, c.State);
        Assert.Contains("budget", c.Message);
        Assert.NotEqual(MallBuyState.NotSent, a.State);
        Assert.NotEqual(MallBuyState.NotSent, b.State);
        await queue.WhenIdleAsync();
        Assert.Equal(2, engine.Sends);
        Assert.Equal(200, ledger.SpentToday("Mallbot") + 0 * ledger.UnclearToday("Mallbot"));   // unclear still counts
    }

    [Fact]
    public async Task Sending_does_not_wait_for_the_inventory_verification()
    {
        var (queue, _, _) = Make();
        var engine = new Engine { ReadGate = new TaskCompletionSource(), GateOn = true };
        queue.Enqueue(Order(), engine, Options);
        queue.Enqueue(Order(), engine, Options);
        // Both POSTs go out although the verification read is still blocked.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (engine.Sends < 2 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal(2, engine.Sends);
        Assert.All(queue.Tickets, t => Assert.Equal(MallBuyState.Verifying, t.State));
        engine.Items["789"] = 2;
        engine.ReadGate.SetResult();
        await queue.WhenIdleAsync();
        Assert.All(queue.Tickets, t => Assert.Equal(MallBuyState.Settled, t.State));
    }

    [Fact]
    public async Task Item_that_never_shows_up_stays_unclear_and_counted_never_released()
    {
        var (queue, ledger, log) = Make();
        var engine = new Engine();   // inventory never changes
        var t = queue.Enqueue(Order(), engine, Options);
        await queue.WhenIdleAsync();
        Assert.Equal(MallBuyState.Unclear, t.State);
        Assert.Contains("still does not show the item", t.Message);
        Assert.Equal(100, ledger.UnclearAmount("Mallbot"));
        Assert.Equal(100, ledger.SpentToday("Mallbot"));
        Assert.Contains(log.Entries, e => e.Kind == SessionLogKind.Error && e.Message.Contains("Unclear"));
    }

    [Fact]
    public async Task Partial_delivery_settles_the_received_part()
    {
        var (queue, ledger, _) = Make();
        var engine = new Engine();
        engine.OnSend = _ => engine.Items["789"] += 1;
        var t = queue.Enqueue(Order(2), engine, Options);
        await queue.WhenIdleAsync();
        Assert.Equal(MallBuyState.Partial, t.State);
        Assert.Equal(1, t.Received);
        Assert.Equal(100, ledger.SpentToday("Mallbot"));
        Assert.Equal(0, ledger.UnclearAmount("Mallbot"));
    }

    [Fact]
    public async Task Refusal_by_KoL_releases_the_slice()
    {
        var (queue, ledger, _) = Make();
        var engine = new Engine
        {
            Respond = _ => new DesktopMallPurchaseResult { Status = DesktopMallPurchaseStatus.Rejected, Message = "The shop no longer has the item at that price (sold or repriced)." }
        };
        var t = queue.Enqueue(Order(), engine, Options);
        await queue.WhenIdleAsync();
        Assert.Equal(MallBuyState.Rejected, t.State);
        Assert.Equal(0, ledger.SpentToday("Mallbot"));
    }

    [Fact]
    public async Task A_verified_result_settles_at_once_without_a_verification_read()
    {
        var (queue, ledger, _) = Make();
        var engine = new Engine
        {
            Respond = o => new DesktopMallPurchaseResult
            {
                Status = DesktopMallPurchaseStatus.Purchased, ReceivedQuantity = o.Quantity, UnitPrice = 90, SpentMeat = 90 * o.Quantity, Message = "Bought."
            }
        };
        var t = queue.Enqueue(Order(2), engine, Options);
        await queue.WhenIdleAsync();
        Assert.Equal(MallBuyState.Settled, t.State);
        Assert.Equal(2, t.Received);
        Assert.Equal(1, engine.Reads);                  // only the baseline read
        Assert.Equal(180, ledger.SpentToday("Mallbot")); // the price actually paid, not the reserved 200
        Assert.Equal(0, ledger.ReservedAmount("Mallbot"));
    }

    [Fact]
    public async Task A_verified_partial_counts_only_what_arrived()
    {
        var (queue, ledger, _) = Make();
        var engine = new Engine
        {
            Respond = _ => new DesktopMallPurchaseResult
            {
                Status = DesktopMallPurchaseStatus.Partial, ReceivedQuantity = 1, UnitPrice = 100, SpentMeat = 100, Message = "Partial."
            }
        };
        var t = queue.Enqueue(Order(3), engine, Options);
        await queue.WhenIdleAsync();
        Assert.Equal(MallBuyState.Partial, t.State);
        Assert.Equal(100, ledger.SpentToday("Mallbot"));
    }

    [Fact]
    public async Task An_unclear_engine_result_stays_counted()
    {
        var (queue, ledger, _) = Make();
        var engine = new Engine { Respond = _ => new DesktopMallPurchaseResult { Status = DesktopMallPurchaseStatus.Unclear, Message = "check" } };
        var t = queue.Enqueue(Order(), engine, Options);
        await queue.WhenIdleAsync();
        Assert.Equal(MallBuyState.Unclear, t.State);
        Assert.Equal(100, ledger.UnclearAmount("Mallbot"));
    }

    [Fact]
    public async Task Different_items_run_in_parallel_and_the_same_item_waits_its_turn()
    {
        var (queue, _, _) = Make();
        var gate = new TaskCompletionSource();
        var engine = new BlockingEngine(gate.Task);
        queue.Enqueue(new MallBuyOrder("789", "Thing", "42", "Shop", 100, 1), engine, Options);
        queue.Enqueue(new MallBuyOrder("789", "Thing", "43", "Shop 2", 100, 1), engine, Options);
        queue.Enqueue(new MallBuyOrder("555", "Other", "42", "Shop", 100, 1), engine, Options);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (engine.Started.Count < 2 && DateTime.UtcNow < deadline) await Task.Delay(10);
        await Task.Delay(50);
        // One buy of each item is in flight; the second buy of item 789 has not started.
        Assert.Equal(new[] { "555", "789" }, engine.Started.OrderBy(x => x).ToArray());
        gate.SetResult();
        await queue.WhenIdleAsync();
        Assert.Equal(3, engine.Started.Count);
        Assert.All(queue.Tickets, t => Assert.Equal(MallBuyState.Settled, t.State));
    }

    [Fact]
    public async Task Production_engine_sends_through_the_library_and_settles_from_its_inventory_check()
    {
        var kol = MallKit.Kol(25_000, "{}", "{}", """{"10881":1}""")
            .On("POST mallstore.php", "You acquire an item: <b>11-leaf clover</b>. You spent 25,000 Meat.");
        await using var accounts = MallKit.Accounts(kol);
        var (queue, ledger, _) = Make();
        var options = new DesktopMallPurchaseOptions { MaxDailySpend = 100_000, MaxMeatPerPurchase = 50_000, MaxQuantityPerPurchase = 5 };

        var t = queue.Enqueue(new MallBuyOrder("10881", "11-leaf clover", "56226", "Crazy Gordon", 26_000, 1),
            new ServiceMallBuyEngine("Mallbot", MallKit.Service(accounts, options)), options);
        await queue.WhenIdleAsync();

        Assert.Equal(MallBuyState.Settled, t.State);
        Assert.Equal(25_000, ledger.SpentToday("Mallbot"));
        Assert.Single(MallKit.Purchases(kol));
    }

    private sealed class BlockingEngine(Task gate) : IMallBuyEngine
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Started { get; } = new();
        public Task<MallAccountReading> ReadAccountAsync(CancellationToken ct)
            => Task.FromResult(new MallAccountReading(100_000, new Dictionary<string, int>(), true));
        public async Task<DesktopMallPurchaseResult> SendAsync(MallBuyOrder order, CancellationToken ct)
        {
            Started.Enqueue(order.ItemId);
            await gate;
            return new DesktopMallPurchaseResult
            {
                Status = DesktopMallPurchaseStatus.Purchased, ReceivedQuantity = order.Quantity, UnitPrice = order.ShownUnitPrice, Message = "ok"
            };
        }
    }
}
