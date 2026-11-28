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

        public async Task<DesktopMallPurchaseResult> SendAsync(MallBuyOrder order,
            Func<CancellationToken, ValueTask<GuardDecision>> beforeEachSend, CancellationToken ct)
        {
            var decision = await beforeEachSend(ct);
            return decision.Allowed ? await SendAsync(order, ct) : Refused(decision);
        }
    }

    internal static DesktopMallPurchaseResult Refused(GuardDecision decision) => new()
    {
        Status = DesktopMallPurchaseStatus.NotSent,
        Message = decision.Reason ?? "Blocked before send."
    };

    private static MallBuyOrder Order(int qty = 1, long price = 100) => new("789", "Thing", "42", "Shop", price, qty);

    /// <summary>
    /// Stands in for the window's Dispatcher post (MainWindow): log writes from the parallel buy lanes run one at a
    /// time, as on the UI thread. Without it two lanes append to the ObservableCollection at once and it throws.
    /// </summary>
    internal static Action<Action> SerialPost()
    {
        var gate = new object();
        return action => { lock (gate) action(); };
    }

    private static (MallBuyQueue Queue, MallSpendLedger Ledger, SessionLogViewModel Log) Make()
    {
        var log = new SessionLogViewModel(now: () => Now, post: SerialPost());
        var ledger = new MallSpendLedger(new Fixed());
        return (new MallBuyQueue("Mallbot", ledger, log, verifyDelay: TimeSpan.Zero, batchWait: TimeSpan.Zero), ledger, log);
    }

    private sealed class UngatedEngine : IMallBuyEngine
    {
        public int Sends { get; private set; }
        public Task<MallAccountReading> ReadAccountAsync(CancellationToken ct)
            => Task.FromResult(new MallAccountReading(100_000, new Dictionary<string, int>(), true));

        public Task<DesktopMallPurchaseResult> SendAsync(MallBuyOrder order, CancellationToken ct)
        {
            Sends++;
            return Task.FromResult(new DesktopMallPurchaseResult { Status = DesktopMallPurchaseStatus.Purchased, Message = "ok" });
        }
    }

    [Fact]
    public async Task A_two_argument_engine_cannot_send_a_queued_purchase()
    {
        var (queue, ledger, _) = Make();
        using var cleanup = queue;
        var engine = new UngatedEngine();
        var ticket = queue.Enqueue(Order(), engine, Options);
        await queue.WhenIdleAsync();

        Assert.Equal(MallBuyState.NotSent, ticket.State);
        Assert.Contains("does not verify pre-send gates at transport time.", ticket.Message);
        Assert.Equal(0, engine.Sends);
        Assert.Equal(0, ledger.CountedToday("Mallbot"));
        Assert.True(ticket.Reservation.IsEmpty);
    }

    [Fact]
    public async Task A_failed_ledger_finish_keeps_the_handle_and_dispose_retries_without_sending()
    {
        var dir = Directory.CreateTempSubdirectory("kolibri-mall-");
        try
        {
            var path = Path.Combine(dir.FullName, "mall.json");
            var log = new SessionLogViewModel(now: () => Now, post: SerialPost());
            var ledger = new MallSpendLedger(new Fixed(), path);
            using var queue = new MallBuyQueue("Mallbot", ledger, log);
            var engine = new Engine
            {
                OnSend = _ => Directory.CreateDirectory(path + ".tmp"),
                Respond = order => new DesktopMallPurchaseResult
                {
                    Status = DesktopMallPurchaseStatus.Purchased, ReceivedQuantity = order.Quantity,
                    UnitPrice = order.ShownUnitPrice, Message = "Bought."
                }
            };

            var ticket = queue.Enqueue(Order(), engine, Options);
            await queue.WhenIdleAsync();

            Assert.Equal(MallBuyState.Settled, ticket.State);
            Assert.Contains("Spend ledger update pending", ticket.Message);
            Assert.Contains(log.Entries, entry => entry.Kind == SessionLogKind.Error && entry.Message.Contains("Spend ledger update pending"));
            Assert.NotNull(ledger.Problem);
            Assert.False(ticket.Reservation.IsEmpty);
            Assert.Equal(100, ticket.Reserved);
            var reader = new MallSpendLedger(new Fixed(), path);
            Assert.Equal(100, reader.ReservedAmount("Mallbot"));
            Assert.Empty(reader.ReadUnclear("Mallbot").ForeignIds!);

            Directory.Delete(path + ".tmp");
            queue.Dispose();

            Assert.Null(ledger.Problem);
            Assert.True(ticket.Reservation.IsEmpty);
            Assert.Equal(0, ticket.Reserved);
            Assert.Equal("Bought.", ticket.Message);
            Assert.Equal(1, engine.Sends);
            Assert.Equal(0, reader.ReservedAmount("Mallbot"));
            Assert.Equal(100, reader.SpentToday("Mallbot"));
            Assert.DoesNotContain("\"sending\"", File.ReadAllText(path));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task Several_buys_are_queued_sent_and_verified_with_one_inventory_read()
    {
        var (queue, ledger, log) = Make();
        // The batch wait (1.5 s in the app) is what collects the buys into one verification. Hold it until all three
        // are waiting for verification, instead of racing a zero wait against the sends.
        var batchReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.BatchWaitForTests = ct => batchReady.Task.WaitAsync(ct);
        var engine = new Engine();
        engine.OnSend = o => engine.Items["789"] += o.Quantity;
        var tickets = new[] { queue.Enqueue(Order(), engine, Options), queue.Enqueue(Order(2), engine, Options), queue.Enqueue(Order(), engine, Options) };
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!tickets.All(t => t.State == MallBuyState.Verifying) && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.All(tickets, t => Assert.Equal(MallBuyState.Verifying, t.State));
        batchReady.SetResult();
        await queue.WhenIdleAsync();
        Assert.All(tickets, t => Assert.Equal(MallBuyState.Settled, t.State));
        Assert.Equal(3, engine.Sends);
        Assert.Equal(2, engine.Reads);            // one baseline read + one verification for the whole batch
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

        public async Task<DesktopMallPurchaseResult> SendAsync(MallBuyOrder order,
            Func<CancellationToken, ValueTask<GuardDecision>> beforeEachSend, CancellationToken ct)
        {
            var decision = await beforeEachSend(ct);
            return decision.Allowed ? await SendAsync(order, ct) : Refused(decision);
        }
    }
}
