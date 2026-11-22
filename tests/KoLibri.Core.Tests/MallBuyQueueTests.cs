using KoLibri.Core.Markt;
using KoLibri.Desktop;
using Xunit;

namespace KoLibri.Core.Tests;

/// <summary>Queued background buys: own budget slice each, sends never wait for verification, one read verifies a batch.</summary>
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
    public void Receipt_reader_follows_KoLmafias_markers()
    {
        Assert.Equal(MallReceiptKind.Confirmed, MallPurchaseReceiptReader.Read("<center><table><tr><td><table class=\"item\" rel=\"x\"><tr><td>You acquire an item</td></tr></table>You spent 222 Meat.</td></tr></table>").Kind);
        Assert.Equal(MallReceiptKind.Refused, MallPurchaseReceiptReader.Read("<center>This store doesn't have that item at that price.</center>").Kind);
        Assert.Equal(MallReceiptKind.Refused, MallPurchaseReceiptReader.Read("You can't afford that item.").Kind);
        Assert.Equal(MallReceiptKind.Unknown, MallPurchaseReceiptReader.Read("<html>Something else entirely</html>").Kind);
        Assert.Equal(MallReceiptKind.Unknown, MallPurchaseReceiptReader.Read("").Kind);
    }

    [Fact]
    public void Diagnostics_snippet_strips_tags_and_secrets_and_is_short()
    {
        var body = "<script>var pwd='abcdef0123456789abcdef0123456789';</script><b>Hello</b> pwd=secretvalue world " + new string('x', 600);
        var snippet = MallPurchaseDiagnostics.Snippet(body, 300);
        Assert.DoesNotContain("secretvalue", snippet);
        Assert.DoesNotContain("<b>", snippet);
        Assert.DoesNotContain("abcdef0123456789abcdef0123456789", snippet);
        Assert.StartsWith("Hello", snippet);
        Assert.True(snippet.Length <= 301);
    }
}
