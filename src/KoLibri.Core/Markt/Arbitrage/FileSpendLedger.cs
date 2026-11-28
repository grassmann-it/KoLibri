// Keep the Core and Extras copies in sync. They share the same ledger files.
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KoLibri.Core.Markt;

/// <summary>The ledger cannot be read or written; spending stays blocked until that is fixed.</summary>
public sealed class SpendLedgerException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>
/// One reservation made by <c>FileSpendLedger.TryReserve</c>: the account, the KoL day it was booked on, the reserved
/// meat and its id in the ledger file. Finishing it books on the current KoL day, and only once: a second finish of the
/// same reservation (or a reservation this instance does not hold) changes nothing.
/// </summary>
public readonly record struct SpendReservation(string Account, string Day, long Amount, string Id = "")
{
    public bool IsEmpty => Amount <= 0;
}

/// <summary>
/// The unclear meat of one KoL day as read by <see cref="FileSpendLedger.ReadUnclear"/>: unclear purchases plus open
/// reservations of other processes (or of a crashed run), with the account, entry revision and foreign ids. Pass it back to
/// <see cref="FileSpendLedger.Reconcile(string, UnclearSnapshot, long)"/>.
/// </summary>
public readonly record struct UnclearSnapshot(
    string Account, string Day, long Unclear, long Foreign, long Revision = 0, IReadOnlyList<string>? ForeignIds = null)
{
    public long Total => Unclear + Foreign;
}

/// <summary>
/// Meat spent per account and KoL day, kept in a small JSON file (never credentials) so the budget survives a
/// restart and is shared by every process that uses the same file. Each operation is one transaction: take an
/// exclusive lock file, determine the KoL day, read the file fresh, apply the change, write it only when something
/// changed, release. A purchase is reserved before the POST (<c>TryReserve</c> checks the limit and writes the
/// reservation, with its own id, in the same transaction), settled once meat and inventory prove it, released when
/// nothing was sent, and counted as unclear when the outcome is not proven. Reservations of other processes (or of a
/// run that crashed) count as spent. This instance remembers the ids of its own open reservations, so finishing one
/// only ever removes that id, even when it has been carried across rollover, and finishing it twice changes nothing.
/// The ledger fails closed: an unreadable or unexpected file, a lock that cannot be taken, or a failed write
/// blocks reservations until fixed; <see cref="SpentToday"/> then reports the budget as fully spent.
/// Without a path the ledger lives in memory only.
/// </summary>
public class FileSpendLedger
{
    /// <summary>Lock wait limits in milliseconds (default 10 000, min 100, max 60 000).</summary>
    public const int DefaultLockTimeoutMs = 10_000, MinLockTimeoutMs = 100, MaxLockTimeoutMs = 60_000;

    private const long Blocked = long.MaxValue / 4;

    private sealed class Entry
    {
        public string Day { get; set; } = "";
        /// <summary>Raised by every change, so a reconcile notices any change since its snapshot.</summary>
        public long Revision { get; set; }
        public void BumpRevision() => Revision = checked(Revision + 1);
        public long Settled { get; set; }
        public long Unclear { get; set; }
        /// <summary>Reserved meat of an older file without reservation ids; it can only be cleared by a reconcile.</summary>
        public long LegacyReserved { get; set; }
        public Dictionary<string, long> Reservations { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Sending { get; } = new(StringComparer.Ordinal);

        public long TotalReserved => checked(LegacyReserved + Reservations.Values.Sum());
    }

    /// <summary>An open reservation of this instance; <see cref="Remaining"/> shrinks when a legacy call consumes part of it.</summary>
    private sealed class OwnReservation(string id, string account, string day, long remaining, long seq)
    {
        public string Id { get; } = id;
        public string Account { get; } = account;
        public string Day { get; } = day;
        public long Remaining { get; set; } = remaining;
        public long Seq { get; } = seq;
        public bool WasSending { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _memory = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Open reservations of this instance by id. Only touched under <see cref="_gate"/>.</summary>
    private readonly Dictionary<string, OwnReservation> _own = new(StringComparer.Ordinal);
    private long _seq;
    private readonly TimeProvider _clock;
    private readonly string? _path;
    private readonly TimeSpan _lockTimeout;
    private readonly Func<int, bool> _isOwnerAlive;

    protected FileSpendLedger(string? path, TimeProvider? clock, int lockTimeoutMs = DefaultLockTimeoutMs)
        : this(path, clock, lockTimeoutMs, isOwnerAlive: null) { }

    protected FileSpendLedger(string? path, TimeProvider? clock, int lockTimeoutMs, Func<int, bool>? isOwnerAlive)
    {
        if (lockTimeoutMs is < MinLockTimeoutMs or > MaxLockTimeoutMs)
            throw new ArgumentOutOfRangeException(nameof(lockTimeoutMs),
                $"The lock timeout must be {MinLockTimeoutMs}..{MaxLockTimeoutMs} ms.");
        _path = path;
        _clock = clock ?? TimeProvider.System;
        _lockTimeout = TimeSpan.FromMilliseconds(lockTimeoutMs);
        _isOwnerAlive = isOwnerAlive ?? IsProcessAlive;
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }

    /// <summary>The last problem that blocked the ledger, or null when the last operation worked.</summary>
    public string? Problem { get; private set; }

    /// <summary>True when the ledger file cannot be read or locked right now.</summary>
    public bool Unreadable
    {
        get
        {
            try { Transact((_, _) => (0, false), allowWrite: false); return false; }
            catch (SpendLedgerException) { return true; }
        }
    }

    /// <summary>The KoL day rolls over at 03:30 Eastern time (fixed UTC-5 where the zone is unknown).</summary>
    private string Today
    {
        get
        {
            var utc = _clock.GetUtcNow();
            TimeSpan offset;
            try { offset = TimeZoneInfo.FindSystemTimeZoneById("America/New_York").GetUtcOffset(utc); }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
            { offset = TimeSpan.FromHours(-5); }
            return DateOnly.FromDateTime((utc + offset - TimeSpan.FromHours(3.5)).DateTime)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>True when <paramref name="reservation"/> was made on the current KoL day (no lock, no file access).</summary>
    public bool IsCurrentDay(SpendReservation reservation) => !reservation.IsEmpty && reservation.Day == Today;

    /// <summary>Today's entry of the account, or null; never creates one (read and compare-and-set paths).</summary>
    private static Entry? Find(Dictionary<string, Entry> entries, string account, string today)
        => entries.TryGetValue(account, out var e) && e.Day == today ? e : null;

    /// <summary>Today's entry of the account, created when missing (only on paths that change something).</summary>
    private static Entry Current(Dictionary<string, Entry> entries, string account, string today)
    {
        if (!entries.TryGetValue(account, out var e) || e.Day != today)
            entries[account] = e = new Entry { Day = today };
        return e;
    }

    // ---- own reservations (always under _gate) --------------------------------------------------------

    private IEnumerable<OwnReservation> OwnOpen(string account, string day)
        => _own.Values.Where(o => (o.Day == day || o.WasSending) && string.Equals(o.Account, account, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The part of today's stored reservations this instance still holds before sending, capped at the stored amount.
    /// </summary>
    private long Own(Entry e, string account, string today)
    {
        if (e.Day != today) return 0;
        long own = 0;
        foreach (var o in OwnOpen(account, today))
            if (!e.Sending.ContainsKey(o.Id) && e.Reservations.TryGetValue(o.Id, out var stored))
                own = checked(own + Math.Min(stored, o.Remaining));
        return own;
    }

    private string[] ForeignIds(Entry e, string account, string today)
    {
        var own = OwnOpen(account, today).Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        return e.Reservations.Keys.Where(id => !own.Contains(id) && (!e.Sending.TryGetValue(id, out var pid) || !_isOwnerAlive(pid)))
            .OrderBy(id => id, StringComparer.Ordinal).ToArray();
    }

    private static long ReconcilableReserved(Entry e, IEnumerable<string> foreignIds)
        => checked(e.LegacyReserved + foreignIds.Sum(id => e.Reservations[id]));

    private static long InternalCounted(Entry e) => checked(e.Settled + e.Unclear + e.TotalReserved);

    private static long IncreaseAmount(long current, long delta)
    {
        if (current >= Blocked - delta)
            throw new SpendLedgerException("The ledger amounts would exceed the maximum supported range.");
        return current + delta;
    }

    // ---- reads ----------------------------------------------------------------------------------------

    /// <summary>
    /// Settled, unclear, in-flight and foreign open reservations of today. This instance's queued reservations
    /// are excluded. A ledger that cannot be read counts as fully spent.
    /// </summary>
    public long SpentToday(string account)
    {
        try
        {
            return Transact((entries, today) => Find(entries, account, today) is { } e
                ? (InternalCounted(e) - Own(e, account, today), false)
                : (0L, false), allowWrite: false);
        }
        catch (SpendLedgerException) { return Blocked; }
    }

    /// <summary>
    /// Everything in the ledger counted against today's limit: settled, unclear and every open reservation
    /// (this instance's own ones included). A ledger that cannot be read counts as fully spent.
    /// </summary>
    public long CountedToday(string account) => CountedToday(account, 0);

    /// <summary>
    /// Like <see cref="CountedToday(string)"/>, with independent spend outside the ledger today
    /// (<paramref name="externalSpent"/>) counted exactly as <c>TryReserve</c> counts it.
    /// </summary>
    public long CountedToday(string account, long externalSpent)
    {
        try
        {
            return Transact((entries, today) =>
            {
                var found = Find(entries, account, today);
                var counted = checked((found is null ? 0 : InternalCounted(found)) + Math.Max(0, externalSpent));
                return (counted, false);
            }, allowWrite: false);
        }
        catch (SpendLedgerException) { return Blocked; }
    }

    /// <summary>Unclear meat plus foreign reservations available for reconciliation.</summary>
    public long UnclearToday(string account)
    {
        try
        {
            return Transact((entries, today) => Find(entries, account, today) is { } e
                ? (checked(e.Unclear + ReconcilableReserved(e, ForeignIds(e, account, today))), false)
                : (0L, false), allowWrite: false);
        }
        catch (SpendLedgerException) { return Blocked; }
    }

    /// <summary>
    /// Today's unclear meat (unclear purchases and reconcilable foreign reservations) with the account, revision and foreign ids, read in one
    /// transaction, for a later <see cref="Reconcile(string, UnclearSnapshot, long)"/>. Writes nothing.
    /// Throws <see cref="SpendLedgerException"/> when unreadable.
    /// </summary>
    public UnclearSnapshot ReadUnclear(string account)
        => Transact((entries, today) =>
        {
            if (Find(entries, account, today) is not { } e)
                return (new UnclearSnapshot(account, today, 0, 0, ForeignIds: Array.Empty<string>()), false);
            var ids = ForeignIds(e, account, today);
            return (new UnclearSnapshot(account, today, e.Unclear, ReconcilableReserved(e, ids), e.Revision,
                Array.AsReadOnly(ids)), false);
        }, allowWrite: false);

    /// <summary>Like <see cref="ReadUnclear"/>; false (with <see cref="Problem"/> set) when the ledger is unreadable.</summary>
    public bool TryReadUnclear(string account, out UnclearSnapshot snapshot)
    {
        try { snapshot = ReadUnclear(account); return true; }
        catch (SpendLedgerException) { snapshot = default; return false; }
    }

    /// <summary>Meat currently reserved by purchases in flight (all processes), 0 when unreadable.</summary>
    public long ReservedAmount(string account)
    {
        try { return Transact((entries, today) => (Find(entries, account, today)?.TotalReserved ?? 0, false), allowWrite: false); }
        catch (SpendLedgerException) { return 0; }
    }

    /// <summary>Meat of today that is counted as unclear (spent or not unknown) for this account, excluding reservations. -1 when the ledger is unreadable.</summary>
    public long UnclearAmount(string account)
    {
        try { return Transact((entries, today) => (Find(entries, account, today)?.Unclear ?? 0, false), allowWrite: false); }
        catch (SpendLedgerException) { return -1; }
    }

    // ---- reservations ---------------------------------------------------------------------------------

    /// <summary>
    /// Checks <paramref name="limit"/> against everything already counted (including foreign reservations) and
    /// durably writes the reservation, as one cross-process transaction. False (with a readable reason) means
    /// nothing was reserved and nothing may be bought.
    /// </summary>
    public bool TryReserve(string account, long meat, long? limit, out string? error)
        => TryReserve(account, meat, limit, out _, out error);

    /// <summary>
    /// Checks <paramref name="limit"/> against everything already counted (including foreign reservations) and
    /// durably writes the reservation, as one cross-process transaction. <paramref name="externalSpent"/> is
    /// independent spend outside the ledger today and counts in full against the limit. False (with a readable
    /// reason) means nothing was reserved and nothing may be bought.
    /// Finish the returned <paramref name="reservation"/> with exactly one of
    /// <see cref="Settle(SpendReservation)"/>, <see cref="SettlePartial(SpendReservation, long)"/>,
    /// <see cref="MarkUnclear(SpendReservation)"/> or <see cref="Release(SpendReservation)"/>.
    /// </summary>
    public bool TryReserve(string account, long meat, long? limit, out SpendReservation reservation, out string? error,
        long externalSpent = 0)
    {
        error = null;
        reservation = default;
        if (meat <= 0) { reservation = new SpendReservation(account, Today, 0); return true; }
        if (meat >= Blocked) { error = "The purchase total is not a plausible amount of Meat."; return false; }
        try
        {
            lock (_gate)   // re-entered by Transact: the own reservation is recorded before any other caller can read
            {
                var id = Guid.NewGuid().ToString("N");
                var day = "";
                var refused = Transact<string?>((entries, today) =>
                {
                    day = today;
                    return ReserveToday(entries, account, today, id, meat, limit, externalSpent);
                }, allowWrite: true);
                if (refused is not null) { error = refused; return false; }
                _own[id] = new OwnReservation(id, account, day, meat, ++_seq);   // only once it is durably written
                reservation = new SpendReservation(account, day, meat, id);
                return true;
            }
        }
        catch (SpendLedgerException e)
        {
            error = $"The spend ledger is blocked, so nothing was bought: {e.Message}";
            return false;
        }
    }

    /// <summary>
    /// Checks under the lock that <paramref name="reservation"/> is still open on today's KoL day and still
    /// present in the ledger. If the day rolled over or another process reconciled it away, drops the old handle
    /// and tries to reserve the same amount fresh on today under <paramref name="limit"/>.
    /// </summary>
    public bool EnsureActiveOrRenew(ref SpendReservation reservation, long? limit, out string? error)
        => EnsureActiveOrRenew(ref reservation, limit, markSending: false, out error);

    /// <summary>Checks or renews the reservation and protects it from reconciliation while this process is sending.</summary>
    public bool BeginSend(ref SpendReservation reservation, long? limit, out string? error)
        => EnsureActiveOrRenew(ref reservation, limit, markSending: true, out error);

    private bool EnsureActiveOrRenew(ref SpendReservation reservation, long? limit, bool markSending, out string? error)
    {
        var prior = reservation;
        if (prior.IsEmpty || string.IsNullOrEmpty(prior.Account) || string.IsNullOrEmpty(prior.Id))
        {
            reservation = default;
            error = "The reservation is no longer valid.";
            return false;
        }

        lock (_gate)
        {
            var renewed = default(SpendReservation);
            try
            {
                error = Transact<string?>((entries, today) =>
                {
                    if (_own.TryGetValue(prior.Id, out var own)
                        && Find(entries, own.Account, today) is { } existing
                        && (own.Day == today || existing.Sending.ContainsKey(own.Id))
                        && existing.Reservations.TryGetValue(own.Id, out var stored)
                        && stored >= prior.Amount && own.Remaining >= prior.Amount)
                    {
                        if (!markSending) return (null, false);
                        if (limit is { } max && InternalCounted(existing) > max)
                            return ("The daily budget would be exceeded.", false);
                        existing.Sending[own.Id] = Environment.ProcessId;
                        existing.BumpRevision();
                        return (null, true);
                    }
                    if (own is { WasSending: true })
                        return ("The in-flight reservation is no longer available.", false);

                    var releasedOld = false;
                    if (own is not null && own.Day == today
                        && Find(entries, own.Account, today) is { } current && current.Reservations.Remove(own.Id))
                    {
                        releasedOld = true;
                        current.Sending.Remove(own.Id);
                        current.BumpRevision();
                    }
                    if (prior.Amount >= Blocked)
                        return ("The purchase total is not a plausible amount of Meat.", releasedOld);
                    var id = Guid.NewGuid().ToString("N");
                    renewed = new SpendReservation(prior.Account, today, prior.Amount, id);
                    var outcome = ReserveToday(entries, prior.Account, today, id, prior.Amount, limit, externalSpent: 0);
                    if (outcome.Result is not null)
                    {
                        renewed = default;
                        return (outcome.Result, releasedOld);
                    }
                    if (markSending) Current(entries, prior.Account, today).Sending[id] = Environment.ProcessId;
                    return (null, true);
                }, allowWrite: true);
                if (error is not null)
                {
                    if (!markSending && (!_own.TryGetValue(prior.Id, out var own) || !own.WasSending))
                    {
                        _own.Remove(prior.Id);
                        reservation = default;
                    }
                    return false;
                }
                if (!renewed.IsEmpty)
                {
                    _own.Remove(prior.Id);
                    _own[renewed.Id] = new OwnReservation(renewed.Id, renewed.Account, renewed.Day, renewed.Amount, ++_seq);
                    reservation = renewed;
                }
                if (markSending) _own[reservation.Id].WasSending = true;
                return true;
            }
            catch (SpendLedgerException e)
            {
                error = $"The spend ledger is blocked, so nothing was bought: {e.Message}";
                return false;
            }
        }
    }

    private static (string? Result, bool Changed) ReserveToday(Dictionary<string, Entry> entries,
        string account, string today, string id, long meat, long? limit, long externalSpent)
    {
        var found = Find(entries, account, today);
        var internalCounted = found is null ? 0 : InternalCounted(found);
        var external = Math.Max(0, externalSpent);
        long counted;
        try { counted = checked(internalCounted + external); }
        catch (OverflowException)
        {
            return ("The amounts counted today are not plausible; nothing is reserved.", false);
        }
        if (limit is { } max && (max < 0 || meat > max || counted > max - meat))
            return (string.Create(CultureInfo.InvariantCulture,
                $"The daily budget would be exceeded: {counted:N0} Meat already counted today (settled, unclear, reserved and external), {meat:N0} more requested, limit {max:N0}."), false);
        if (internalCounted >= Blocked - meat)
            return ("The amounts counted today are not plausible; nothing is reserved.", false);

        var e = Current(entries, account, today);
        e.Reservations[id] = meat;
        e.BumpRevision();
        return (null, true);
    }

    /// <summary>Written before the POST so a crash in between still counts the meat. Throws when it cannot be stored.</summary>
    public void Reserve(string account, long meat)
    {
        if (!TryReserve(account, meat, null, out var error)) throw new SpendLedgerException(error ?? "Reservation refused.");
    }

    /// <summary>The purchase is proven: the reserved meat is settled.</summary>
    public void Settle(SpendReservation reservation)
        => AfterPurchase(reservation, (e, held) => e.Settled = IncreaseAmount(e.Settled, held));

    /// <summary>Nothing was sent: the reservation is dropped.</summary>
    public void Release(SpendReservation reservation)
        => AfterPurchase(reservation, null);

    /// <summary>The outcome is not proven: the reserved meat stays counted as unclear until reconciled.</summary>
    public void MarkUnclear(SpendReservation reservation)
        => FinishReservation(reservation, spent: null, unclear: true);

    /// <summary>A purchase that delivered less than ordered: the delivered part is settled, the rest released.</summary>
    public void SettlePartial(SpendReservation reservation, long spent)
        => FinishReservation(reservation, spent, unclear: false);

    /// <summary>Books the outcome and returns the persistence error, if any, to callers that can retry.</summary>
    protected string? FinishReservation(SpendReservation reservation, long? spent, bool unclear)
    {
        if (spent is { } amount)
            return AfterPurchase(reservation, (e, held) => e.Settled = IncreaseAmount(e.Settled, Math.Clamp(amount, 0, held)));
        if (unclear)
            return AfterPurchase(reservation, (e, held) => e.Unclear = IncreaseAmount(e.Unclear, held));
        return AfterPurchase(reservation, null);
    }

    /// <summary>Settles <paramref name="meat"/> out of this instance's open reservations of the account (see <see cref="AfterLegacy"/>).</summary>
    public void Settle(string account, long meat)
        => AfterLegacy(account, meat, (e, held) => e.Settled = IncreaseAmount(e.Settled, held));

    /// <summary>Releases <paramref name="meat"/> of this instance's open reservations of the account (see <see cref="AfterLegacy"/>).</summary>
    public void Release(string account, long meat) => AfterLegacy(account, meat, null);

    /// <summary>Moves <paramref name="meat"/> of this instance's open reservations of the account to unclear (see <see cref="AfterLegacy"/>).</summary>
    public void MarkUnclear(string account, long meat)
        => AfterLegacy(account, meat, (e, held) => e.Unclear = IncreaseAmount(e.Unclear, held));

    /// <summary>A purchase that delivered less than ordered: the delivered part is settled, the rest released.</summary>
    public void SettlePartial(string account, long reserved, long spent)
        => AfterLegacy(account, reserved, (e, held) => e.Settled = IncreaseAmount(e.Settled, Math.Clamp(spent, 0, held)));

    /// <summary>
    /// Finishes a reservation this instance holds and keeps the handle until the transaction succeeds.
    /// A sending reservation is carried to today and can already have been reconciled. Only older reservations
    /// without a send marker still need to be booked on the new day when their id is gone.
    /// </summary>
    private string? AfterPurchase(SpendReservation reservation, Action<Entry, long>? book)
    {
        if (reservation.IsEmpty || string.IsNullOrEmpty(reservation.Account) || string.IsNullOrEmpty(reservation.Id)) return null;
        lock (_gate)
        {
            if (!_own.TryGetValue(reservation.Id, out var own)) return null;
            try
            {
                Transact((entries, today) =>
                {
                    var e = Find(entries, own.Account, today);
                    if (e is not null && e.Reservations.ContainsKey(own.Id))
                    {
                        book?.Invoke(e, own.Remaining);
                        e.Reservations.Remove(own.Id);
                        e.Sending.Remove(own.Id);
                        e.BumpRevision();
                        return (0, true);
                    }
                    if (own.Day != today && !own.WasSending && book is not null)
                    {
                        var day = Current(entries, own.Account, today);
                        book(day, own.Remaining);
                        day.BumpRevision();
                        return (0, true);
                    }
                    return (0, false);
                }, allowWrite: true);
                _own.Remove(own.Id);
                return null;
            }
            catch (SpendLedgerException exception)
            {
                return exception.Message;
            }
        }
    }

    /// <summary>
    /// The account-based finish of older callers: takes up to <paramref name="meat"/> out of this instance's open
    /// reservations of the account, oldest first, and books the amount taken. When this instance holds reservations
    /// of several KoL days the call is ambiguous, so it changes nothing.
    /// A partly used reservation keeps its rest (in the instance and in the file).
    /// </summary>
    private void AfterLegacy(string account, long meat, Action<Entry, long>? book)
    {
        if (meat <= 0 || string.IsNullOrEmpty(account)) return;
        lock (_gate)
        {
            var ran = false;
            try
            {
                Transact((entries, today) =>
                {
                    ran = true;
                    var found = Find(entries, account, today);
                    var (totalTaken, usedToday) = TakeLegacy(account, meat, today, found);
                    if (totalTaken == 0) return (0, false);
                    if (book is not null)
                    {
                        var e = found ?? new Entry { Day = today };
                        book(e, totalTaken);
                        if (found is null) entries[account] = found = e;
                    }
                    if (found is null || (book is null && usedToday.Count == 0)) return (0, false);
                    foreach (var (id, taken) in usedToday)
                    {
                        var remaining = found.Reservations[id] - taken;
                        if (remaining == 0) found.Reservations.Remove(id); else found.Reservations[id] = remaining;
                    }
                    found.BumpRevision();
                    return (0, true);
                }, allowWrite: true);
            }
            catch (SpendLedgerException)
            {
                // Problem is set. Nothing was read: the own share is dropped anyway, so the stored meat counts as foreign.
                if (!ran) TakeLegacy(account, meat, today: null, found: null);
            }
        }
    }

    /// <summary>
    /// Takes up to <paramref name="meat"/> out of the open own reservations of the account (see
    /// <see cref="AfterLegacy"/>); returns the total taken and the portions taken from today's stored ids.
    /// When the file could not be read, <paramref name="today"/> is null and only in-memory ownership is changed.
    /// </summary>
    private (long TotalTaken, List<(string Id, long Taken)> UsedToday) TakeLegacy(
        string account, long meat, string? today, Entry? found)
    {
        var mine = _own.Values
            .Where(o => !o.WasSending && string.Equals(o.Account, account, StringComparison.OrdinalIgnoreCase))
            .OrderBy(o => o.Seq).ToList();
        if (mine.Select(o => o.Day).Distinct(StringComparer.Ordinal).Count() > 1)
            return (0, []);
        var usedToday = new List<(string Id, long Taken)>();
        var needed = meat;
        foreach (var o in mine)
        {
            if (needed <= 0) break;
            var taken = Math.Min(o.Remaining, needed);
            if (o.Day == today)
            {
                if (found is null || found.Sending.ContainsKey(o.Id) || !found.Reservations.TryGetValue(o.Id, out var stored)) continue;
                taken = Math.Min(taken, stored);
                usedToday.Add((o.Id, taken));
            }
            needed -= taken;
            if (taken >= o.Remaining) _own.Remove(o.Id); else o.Remaining -= taken;
        }
        return (meat - needed, usedToday);
    }

    // ---- reconcile ------------------------------------------------------------------------------------

    /// <summary>
    /// The user checked inventory and meat: the unclear meat of <paramref name="expected"/> (unclear purchases plus
    /// foreign reservations, from <see cref="ReadUnclear"/>) was spent (<paramref name="actuallySpent"/>) or not.
    /// Compare-and-set in one transaction: the account, KoL day, revision, unclear and foreign amounts and foreign
    /// ids must all match. The unclear meat and every foreign reservation are then cleared.
    /// </summary>
    public bool Reconcile(string account, UnclearSnapshot expected, long actuallySpent)
        => Transact((entries, today) =>
        {
            if (expected.Total <= 0 || !string.Equals(expected.Account, account, StringComparison.OrdinalIgnoreCase)
                || expected.Day != today || Find(entries, account, today) is not { } e
                || e.Revision != expected.Revision || e.Unclear != expected.Unclear)
                return (false, false);
            var currentForeignIds = ForeignIds(e, account, today);
            if (ReconcilableReserved(e, currentForeignIds) != expected.Foreign
                || !currentForeignIds.SequenceEqual(expected.ForeignIds ?? Array.Empty<string>(), StringComparer.Ordinal))
                return (false, false);
            e.Settled = IncreaseAmount(e.Settled, Math.Max(0, actuallySpent));
            e.Unclear = 0;
            e.LegacyReserved = 0;
            foreach (var id in currentForeignIds)
            {
                e.Reservations.Remove(id);
                e.Sending.Remove(id);
            }
            e.BumpRevision();
            return (true, true);
        }, allowWrite: true);

    /// <summary>
    /// Explicit user decision about the unclear meat of today: <paramref name="wasSpent"/> moves it to settled,
    /// otherwise it is released. Only <paramref name="expectedAmount"/> (the amount the user confirmed) is
    /// resolved; if the amount changed meanwhile nothing is written and false is returned.
    /// </summary>
    public bool ResolveUnclear(string account, long expectedAmount, bool wasSpent)
        => Transact((entries, today) =>
        {
            if (expectedAmount <= 0 || Find(entries, account, today) is not { } e || e.Unclear != expectedAmount)
                return (false, false);
            if (wasSpent) e.Settled = IncreaseAmount(e.Settled, e.Unclear);
            e.Unclear = 0;
            e.BumpRevision();
            return (true, true);
        }, allowWrite: true);

    /// <summary>Records meat that was spent and verified without a reservation.</summary>
    public void Add(string account, long meat)
    {
        if (meat <= 0) return;
        Transact((entries, today) =>
        {
            var found = Find(entries, account, today);
            if (found is not null && (meat >= Blocked || InternalCounted(found) >= Blocked - meat))
                throw new SpendLedgerException("The ledger amounts would exceed the maximum supported range.");
            var settled = IncreaseAmount(found?.Settled ?? 0, meat);
            var e = found ?? Current(entries, account, today);
            e.Settled = settled;
            e.BumpRevision();
            return (0, true);
        }, allowWrite: true);
    }

    // ---- transaction plumbing -------------------------------------------------------------------------

    /// <summary>
    /// One transaction. The KoL day is taken only once the lock is held and used for both the file read and the
    /// body; the file is written only when <paramref name="allowWrite"/> and the body reports a change. An
    /// arithmetic overflow blocks like an unreadable file.
    /// </summary>
    private T Transact<T>(Func<Dictionary<string, Entry>, string, (T Result, bool Changed)> body, bool allowWrite)
    {
        lock (_gate)
        {
            try
            {
                T result;
                if (_path is null)
                {
                    var today = Today;
                    foreach (var (account, entry) in _memory.Where(pair => pair.Value.Day != today).ToArray())
                    {
                        if (ForDay(entry, today) is { } carried) _memory[account] = carried;
                        else _memory.Remove(account);
                    }
                    result = body(_memory, today).Result;
                }
                else
                {
                    using var fileLock = AcquireLock();
                    var today = Today;
                    var entries = ReadFile(today);
                    var outcome = body(entries, today);
                    if (allowWrite && outcome.Changed) WriteFile(entries);
                    result = outcome.Result;
                }
                Problem = null;
                return result;
            }
            catch (OverflowException e)
            {
                var message = "The ledger amounts are out of range: " + e.Message;
                Problem = message;
                throw new SpendLedgerException(message, e);
            }
            catch (SpendLedgerException e)
            {
                Problem = e.Message;
                throw;
            }
        }
    }

    private FileStream AcquireLock()
    {
        var lockPath = _path + ".lock";
        try { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path!))!); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new SpendLedgerException($"The ledger folder is not usable: {e.Message}", e); }

        var waited = 0;
        while (true)
        {
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (waited < _lockTimeout.TotalMilliseconds)
            {
                Thread.Sleep(25);
                waited += 25;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { throw new SpendLedgerException($"The ledger lock could not be taken: {e.Message}", e); }
        }
    }

    private Dictionary<string, Entry> ReadFile(string today)
    {
        var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_path!)) return entries;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(_path!)) as JsonObject
                ?? throw new FormatException("the top level is not an object");
            // The older flat {"account":{...}} format and version 1 files with send markers are still read.
            if (root.TryGetPropertyValue("version", out var version)
                && (version is not JsonValue versionValue || !versionValue.TryGetValue<int>(out var number) || number is not (1 or 2)))
                throw new FormatException("unsupported ledger version");
            var accounts = root["accounts"] as JsonObject ?? (root.ContainsKey("accounts") ? null : root)
                ?? throw new FormatException("'accounts' is not an object");
            foreach (var (name, node) in accounts)
            {
                if (node is not JsonObject o) throw new FormatException($"entry '{name}' is not an object");
                var day = o["day"]?.GetValue<string>() ?? throw new FormatException($"entry '{name}' has no day");
                var e = new Entry
                {
                    Day = day,
                    Revision = RevisionAmount(o, "revision", name),
                    Settled = Amount(o, "settled", name),
                    Unclear = Amount(o, "unclear", name)
                };
                var reserved = Amount(o, "reserved", name);
                long sum = 0;
                if (o["reservations"] is { } map)
                {
                    if (map is not JsonObject ids) throw new FormatException($"'reservations' of '{name}' is not an object");
                    foreach (var (id, value) in ids)
                    {
                        var v = value?.GetValue<long>() ?? throw new FormatException($"reservation '{id}' of '{name}' has no amount");
                        if (v is <= 0 or >= Blocked || string.IsNullOrEmpty(id))
                            throw new FormatException($"reservation '{id}' of '{name}' is out of range");
                        sum += v;
                        if (sum >= Blocked) throw new FormatException($"the reservations of '{name}' are out of range");
                        e.Reservations[id] = v;
                    }
                }
                e.LegacyReserved = Math.Max(0, reserved - sum);
                if (o["sending"] is { } sending)
                {
                    if (sending is not JsonObject owners) throw new FormatException($"'sending' of '{name}' is not an object");
                    foreach (var (id, value) in owners)
                    {
                        if (value?.GetValue<int>() is not (> 0 and var pid) || !e.Reservations.ContainsKey(id))
                            throw new FormatException($"in-flight reservation '{id}' of '{name}' has an invalid owner or no amount");
                        e.Sending[id] = pid;
                    }
                }
                if (ForDay(e, today) is { } current) entries.Add(name, current);
            }
            return entries;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
                                      or InvalidOperationException or FormatException or ArgumentException)
        {
            throw new SpendLedgerException(
                $"The ledger file '{_path}' is unreadable or unexpected ({e.Message}). Fix or remove it; purchases stay blocked until then.", e);
        }
    }

    private static Entry? ForDay(Entry entry, string today)
    {
        if (entry.Day == today) return entry;
        if (entry.Sending.Count == 0 || string.CompareOrdinal(entry.Day, today) > 0) return null;

        var carried = new Entry { Day = today, Revision = entry.Revision };
        foreach (var (id, pid) in entry.Sending)
        {
            carried.Reservations.Add(id, entry.Reservations[id]);
            carried.Sending.Add(id, pid);
        }
        return carried;
    }

    private static long Amount(JsonObject o, string key, string name)
    {
        if (o[key] is not { } node) return 0;
        var v = node.GetValue<long>();
        return v is < 0 or >= Blocked ? throw new FormatException($"'{key}' of '{name}' is out of range") : v;
    }

    private static long RevisionAmount(JsonObject o, string key, string name)
    {
        if (o[key] is not { } node) return 0;
        var v = node.GetValue<long>();
        return v < 0 ? throw new FormatException($"'{key}' of '{name}' is out of range") : v;
    }

    private void WriteFile(Dictionary<string, Entry> entries)
    {
        try
        {
            var accounts = new JsonObject();
            var version = 1;
            foreach (var (name, e) in entries)
            {
                var reservations = new JsonObject();
                foreach (var (id, meat) in e.Reservations) reservations[id] = meat;
                var o = new JsonObject
                {
                    ["day"] = e.Day,
                    ["revision"] = e.Revision,
                    ["settled"] = e.Settled,
                    ["unclear"] = e.Unclear,
                    ["reserved"] = e.TotalReserved,   // the total, so older builds still count every reservation
                    ["reservations"] = reservations
                };
                if (e.Sending.Count > 0)
                {
                    version = 2;
                    var sending = new JsonObject();
                    foreach (var (id, pid) in e.Sending) sending[id] = pid;
                    o["sending"] = sending;
                }
                accounts[name] = o;
            }
            var json = new JsonObject { ["version"] = version, ["accounts"] = accounts }
                .ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path!, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new SpendLedgerException($"The ledger could not be saved: {e.Message}", e);
        }
    }
}

/// <summary>
/// The desktop Mall purchase ledger (<c>mall-spend-ledger.json</c> next to the desktop settings) for hosts other than
/// the desktop, e.g. the CLI resolving unclear entries. Same file and transaction rules as the desktop's ledger.
/// </summary>
public sealed class SharedMallSpendLedger(string? path = null, TimeProvider? clock = null)
    : FileSpendLedger(path ?? DefaultPath(), clock)
{
    public static string DefaultPath()
    {
        var root = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(root))
            root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "KoLibri", "mall-spend-ledger.json");
    }
}
