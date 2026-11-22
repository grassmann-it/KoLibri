using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace KoLibri.Desktop;

public sealed record MallAccountReading(long Meat, IReadOnlyDictionary<string, int> Items, bool Fresh);

public sealed record MallBuyOrder(string ItemId, string ItemName, string ShopId, string ShopName, long ShownUnitPrice, int Quantity);

/// <summary>What the queue needs from the account: a fresh read and one deferred-verification POST.</summary>
public interface IMallBuyEngine
{
    Task<MallAccountReading> ReadAccountAsync(CancellationToken cancellationToken);
    Task<DesktopMallPurchaseResult> SendAsync(MallBuyOrder order, CancellationToken cancellationToken);
}

/// <summary>Production engine: one Mall search per buy, the account read once per batch, verification deferred.</summary>
public sealed class ServiceMallBuyEngine(string account, DesktopMallPurchaseService service, MallPurchaseObserver observer) : IMallBuyEngine
{
    public Task<MallAccountReading> ReadAccountAsync(CancellationToken cancellationToken) => observer.ReadAccountAsync(cancellationToken);

    public async Task<DesktopMallPurchaseResult> SendAsync(MallBuyOrder order, CancellationToken cancellationToken)
    {
        observer.Remember(order.ItemId, order.ItemName);
        var result = await service.BuyAsync(account, order.ItemId, order.ShopId, order.ShownUnitPrice, order.Quantity,
            cancellationToken, deferVerification: true).ConfigureAwait(false);
        if (result.Status == DesktopMallPurchaseStatus.Sent)
            observer.NoteSent(result.UnitPrice * order.Quantity);
        return result;
    }
}

public enum MallBuyState { Queued, Sending, Verifying, Settled, Partial, Unclear, Rejected, NotSent }

/// <summary>One buy as the Mall tab shows it.</summary>
public sealed class MallBuyTicket : INotifyPropertyChanged
{
    private MallBuyState _state = MallBuyState.Queued;
    private string _message = "Queued";

    public event PropertyChangedEventHandler? PropertyChanged;

    public MallBuyTicket(int number, MallBuyOrder order) { Number = number; Order = order; }

    public int Number { get; }
    public MallBuyOrder Order { get; }
    public long Reserved { get; internal set; }
    public long UnitPrice { get; internal set; }
    public int Received { get; internal set; }
    public bool Confirmed { get; internal set; }
    internal int Attempts { get; set; }

    public string Title => string.Create(CultureInfo.InvariantCulture,
        $"#{Number} {Order.Quantity} x {Order.ItemName} · {Order.ShopName} · up to {Order.ShownUnitPrice * Order.Quantity:N0} Meat");

    public MallBuyState State { get => _state; internal set => Set(ref _state, value); }
    public string Message { get => _message; internal set => Set(ref _message, value); }
    public string StateText => State switch
    {
        MallBuyState.Queued => "Queued",
        MallBuyState.Sending => "Sending",
        MallBuyState.Verifying => "Sent, verifying",
        MallBuyState.Settled => "Settled",
        MallBuyState.Partial => "Partial",
        MallBuyState.Unclear => "Unclear",
        MallBuyState.Rejected => "Rejected",
        _ => "Not sent"
    };
    public bool IsFinal => State is not (MallBuyState.Queued or MallBuyState.Sending or MallBuyState.Verifying);
    public bool IsProblem => State is MallBuyState.Unclear or MallBuyState.Rejected or MallBuyState.NotSent;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name == nameof(State))
            foreach (var n in new[] { nameof(StateText), nameof(IsFinal), nameof(IsProblem) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}

/// <summary>
/// Background buy queue of one account. Each buy reserves its own budget slice up front; the sender sends the buys
/// one after another (one POST each, bound to the price just read) without waiting for any inventory check; one
/// deferred verifier reads the inventory once per batch and settles the sent buys in order. Unproven buys stay
/// Unclear (counted in the budget) until the user reconciles them; nothing is released silently.
/// </summary>
public sealed class MallBuyQueue : IDisposable
{
    private readonly string _account;
    private readonly MallSpendLedger _ledger;
    private readonly SessionLogViewModel _log;
    private readonly Action<Action> _ui;
    private readonly int _verifyReads;
    private readonly TimeSpan _verifyDelay;
    private readonly TimeSpan _batchWait;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly Queue<(MallBuyTicket Ticket, IMallBuyEngine Engine, DesktopMallPurchaseOptions Options)> _queued = new();
    private readonly List<MallBuyTicket> _pending = new();
    private Dictionary<string, int>? _baseline;
    private long _lastMeat;
    private long _sentSinceRead;
    private Task? _sender;
    private Task? _verifier;
    private int _counter;

    public MallBuyQueue(string account, MallSpendLedger ledger, SessionLogViewModel log, Action<Action>? ui = null,
        int verifyReads = 2, TimeSpan? verifyDelay = null, TimeSpan? batchWait = null)
    {
        _account = account;
        _ledger = ledger;
        _log = log;
        _ui = ui ?? (a => a());
        _verifyReads = verifyReads;
        _verifyDelay = verifyDelay ?? TimeSpan.FromSeconds(3);
        _batchWait = batchWait ?? TimeSpan.FromMilliseconds(1500);
    }

    public ObservableCollection<MallBuyTicket> Tickets { get; } = new();

    /// <summary>Raised on the UI context whenever a ticket changes; the host refreshes totals.</summary>
    public event Action? Changed;

    public string TotalText
    {
        get
        {
            var all = Tickets.ToList();
            if (all.Count == 0) return string.Empty;
            long spent = all.Where(t => t.State is MallBuyState.Settled or MallBuyState.Partial).Sum(t => t.UnitPrice * t.Received);
            int running = all.Count(t => !t.IsFinal);
            int unclear = all.Count(t => t.State == MallBuyState.Unclear);
            return string.Create(CultureInfo.InvariantCulture,
                $"{all.Count} buys · {all.Count(t => t.State == MallBuyState.Settled):N0} settled · {running:N0} in progress · {unclear:N0} unclear · {spent:N0} Meat spent");
        }
    }

    /// <summary>
    /// Queues a buy and returns at once. The budget slice is reserved (and saved) here; if that fails, or the
    /// quantity/total is over the per-purchase limits, the ticket ends as "Not sent" and nothing is queued.
    /// </summary>
    public MallBuyTicket Enqueue(MallBuyOrder order, IMallBuyEngine engine, DesktopMallPurchaseOptions options)
    {
        var ticket = new MallBuyTicket(Interlocked.Increment(ref _counter), order);
        Ui(() => Tickets.Add(ticket));
        var max = checked(order.ShownUnitPrice * order.Quantity);
        if (order.Quantity <= 0 || order.Quantity > options.MaxQuantityPerPurchase || max > options.MaxMeatPerPurchase)
        {
            Finish(ticket, MallBuyState.NotSent, "the quantity or total is above your per-purchase limit in Settings.", release: false);
            return ticket;
        }
        if (!_ledger.TryReserve(_account, max, options.MaxDailySpend, out var error))
        {
            Finish(ticket, MallBuyState.NotSent, error ?? "the daily budget is used up.", release: false);
            return ticket;
        }
        ticket.Reserved = max;
        _log.Purchase(string.Create(CultureInfo.InvariantCulture,
            $"Queued #{ticket.Number}: {order.Quantity} x {order.ItemName} at up to {order.ShownUnitPrice:N0}, total up to {max:N0}."));
        lock (_gate)
        {
            _queued.Enqueue((ticket, engine, options));
            _sender ??= Task.Run(SendLoopAsync);
        }
        Notify();
        return ticket;
    }

    /// <summary>Completes when nothing is queued, sending or waiting for verification (tests, shutdown).</summary>
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            Task? s, v;
            lock (_gate) { s = _sender; v = _verifier; }
            if (s is null && v is null) return;
            await Task.WhenAll(s ?? Task.CompletedTask, v ?? Task.CompletedTask).ConfigureAwait(false);
        }
    }

    // ---- sender -------------------------------------------------------------------------------------

    private async Task SendLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                (MallBuyTicket Ticket, IMallBuyEngine Engine, DesktopMallPurchaseOptions Options) next;
                lock (_gate)
                {
                    if (!_queued.TryDequeue(out next)) { _sender = null; return; }
                }
                await SendOneAsync(next.Ticket, next.Engine).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            _log.Error($"Buy queue stopped: {MallPurchaseMessages.Describe(exception)}");
            lock (_gate) _sender = null;
        }
    }

    private async Task SendOneAsync(MallBuyTicket ticket, IMallBuyEngine engine)
    {
        var token = _stop.Token;
        Ui(() => { ticket.State = MallBuyState.Sending; ticket.Message = "Sending"; });
        Notify();
        try
        {
            if (_baseline is null)
            {
                var reading = await engine.ReadAccountAsync(token).ConfigureAwait(false);
                if (!reading.Fresh)
                {
                    Finish(ticket, MallBuyState.NotSent, "the inventory could not be read fresh. Nothing was sent.", release: true);
                    return;
                }
                lock (_gate) { _baseline = new Dictionary<string, int>(reading.Items, StringComparer.Ordinal); _lastMeat = reading.Meat; _sentSinceRead = 0; }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Finish(ticket, MallBuyState.NotSent, $"the account could not be read ({MallPurchaseMessages.Describe(exception)}). Nothing was sent.", release: true);
            return;
        }

        DesktopMallPurchaseResult result;
        try
        {
            result = await engine.SendAsync(ticket.Order, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Finish(ticket, MallBuyState.Unclear, "cancelled while sending; it may or may not have gone through. Check your inventory.", release: false, unclear: true);
            return;
        }
        catch (Exception exception)
        {
            // Unknown whether a POST went out.
            Finish(ticket, MallBuyState.Unclear, $"the purchase failed ({MallPurchaseMessages.Describe(exception)}); it may or may not have gone through. Check your inventory.", release: false, unclear: true);
            return;
        }

        switch (result.Status)
        {
            case DesktopMallPurchaseStatus.Sent:
                ticket.UnitPrice = result.UnitPrice;
                ticket.Confirmed = result.Confirmed;
                lock (_gate)
                {
                    _sentSinceRead += result.UnitPrice * ticket.Order.Quantity;
                    _pending.Add(ticket);
                    _verifier ??= Task.Run(VerifyLoopAsync);
                }
                Ui(() => { ticket.State = MallBuyState.Verifying; ticket.Message = result.Confirmed ? "KoL confirmed; waiting for the inventory" : "KoL's answer was unclear; waiting for the inventory"; });
                Notify();
                lock (_gate) _engineForVerify = engine;
                break;
            case DesktopMallPurchaseStatus.Unclear:
                Finish(ticket, MallBuyState.Unclear, result.Message, release: false, unclear: true);
                break;
            case DesktopMallPurchaseStatus.Rejected:
                Finish(ticket, MallBuyState.Rejected, result.Message, release: true);
                break;
            default:   // NotSent, PriceChanged
                Finish(ticket, MallBuyState.NotSent, result.Message, release: true);
                break;
        }
    }

    // ---- deferred verifier ---------------------------------------------------------------------------

    private IMallBuyEngine? _engineForVerify;

    private async Task VerifyLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                if (_batchWait > TimeSpan.Zero) await Task.Delay(_batchWait, _stop.Token).ConfigureAwait(false);
                List<MallBuyTicket> batch;
                IMallBuyEngine? engine;
                lock (_gate)
                {
                    if (_pending.Count == 0) { _verifier = null; return; }
                    batch = _pending.ToList();
                    engine = _engineForVerify;
                }
                if (engine is null) { lock (_gate) _verifier = null; return; }
                await VerifyPassAsync(batch, engine).ConfigureAwait(false);
                bool more;
                lock (_gate) more = _pending.Count > 0;
                if (more && _verifyDelay > TimeSpan.Zero) await Task.Delay(_verifyDelay, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            _log.Error($"Buy verification stopped: {MallPurchaseMessages.Describe(exception)}");
            lock (_gate) _verifier = null;
        }
    }

    private async Task VerifyPassAsync(List<MallBuyTicket> batch, IMallBuyEngine engine)
    {
        MallAccountReading? reading = null;
        try { reading = await engine.ReadAccountAsync(_stop.Token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OperationCanceledException) { _ = exception; }

        var usable = reading is { Fresh: true };
        foreach (var t in batch) t.Attempts++;
        var finalPass = batch.Where(x => x.Attempts > _verifyReads).ToList();
        if (!usable)
        {
            foreach (var t in finalPass)
                Resolve(t, MallBuyState.Unclear, 0, "KoL's answer could not be matched: the inventory could not be read fresh. Check your inventory.");
            return;
        }

        Dictionary<string, int> baseline;
        long meatFloor;
        lock (_gate)
        {
            baseline = _baseline ?? new Dictionary<string, int>(StringComparer.Ordinal);
            meatFloor = _lastMeat - _sentSinceRead;
            _sentSinceRead = 0;
            _lastMeat = reading!.Meat;
        }
        var meatDropped = reading!.Meat < meatFloor;

        var newBase = new Dictionary<string, int>(reading.Items, StringComparer.Ordinal);
        foreach (var group in batch.GroupBy(t => t.Order.ItemId))
        {
            var now = reading.Items.TryGetValue(group.Key, out var n) ? n : 0;
            var before = baseline.TryGetValue(group.Key, out var b) ? b : 0;
            var remaining = Math.Max(0, now - before);
            var unresolved = new List<MallBuyTicket>();
            foreach (var t in group)
            {
                if (remaining >= t.Order.Quantity)
                {
                    remaining -= t.Order.Quantity;
                    if (meatDropped)
                        Resolve(t, MallBuyState.Unclear, 0, "the item arrived, but Meat fell by more than the purchases explain. Check your inventory and Meat.");
                    else
                        Resolve(t, MallBuyState.Settled, t.Order.Quantity, string.Create(CultureInfo.InvariantCulture,
                            $"bought {t.Order.Quantity} for {t.UnitPrice * t.Order.Quantity:N0} Meat; the inventory shows the item."));
                }
                else unresolved.Add(t);
            }
            foreach (var t in unresolved)
            {
                if (t.Attempts <= _verifyReads) continue;   // may still arrive: look again
                if (remaining > 0)
                {
                    var got = remaining;
                    remaining = 0;
                    Resolve(t, MallBuyState.Partial, got, string.Create(CultureInfo.InvariantCulture,
                        $"partial purchase: {got} of {t.Order.Quantity} unit(s) arrived ({t.UnitPrice * got:N0} Meat counted)."));
                }
                else
                {
                    var how = t.Confirmed ? "KoL confirmed the purchase" : "KoL's answer was unclear";
                    Resolve(t, MallBuyState.Unclear, 0, string.Create(CultureInfo.InvariantCulture,
                        $"{how}, but the inventory still does not show the item after {t.Attempts} read(s) (item {t.Order.ItemId}: {before} before, {now} now). Check your inventory; use 'Reconcile unclear purchases' once you know."));
                }
            }
            // Units seen but not yet assigned stay attributable to the still-waiting buys.
            newBase[group.Key] = now - remaining;
        }
        lock (_gate) _baseline = newBase;
    }

    private void Resolve(MallBuyTicket ticket, MallBuyState state, int received, string message)
    {
        ticket.Received = received;
        lock (_gate) _pending.Remove(ticket);
        Finish(ticket, state, message, release: false, unclear: state == MallBuyState.Unclear);
    }

    private void Finish(MallBuyTicket ticket, MallBuyState state, string message, bool release = false, bool unclear = false)
    {
        if (ticket.Reserved > 0)
        {
            if (state is MallBuyState.Settled or MallBuyState.Partial)
                _ledger.SettlePartial(_account, ticket.Reserved, ticket.UnitPrice * ticket.Received);
            else if (unclear) _ledger.MarkUnclear(_account, ticket.Reserved);
            else _ledger.Release(_account, ticket.Reserved);   // nothing was sent (not sent / refused by KoL)
            ticket.Reserved = 0;
        }
        var label = state switch { MallBuyState.Settled => "Settled", MallBuyState.NotSent => "Not sent", _ => state.ToString() };
        var line = $"#{ticket.Number} {label}: {message}";
        if (state is MallBuyState.Settled or MallBuyState.Partial) _log.Purchase(line); else _log.Error(line);
        Ui(() => { ticket.State = state; ticket.Message = message; });
        Notify();
    }

    private void Ui(Action action) => _ui(action);

    private void Notify() => Ui(() => Changed?.Invoke());

    public void Dispose() { _stop.Cancel(); _stop.Dispose(); }
}
