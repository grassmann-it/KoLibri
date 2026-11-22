using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KoLibri.Core.Markt;

/// <summary>The ledger cannot be read or written; spending stays blocked until that is fixed.</summary>
public sealed class SpendLedgerException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>
/// Meat spent per account and KoL day, kept in a small JSON file (never credentials) so the budget survives a
/// restart and is shared by every process that uses the same file. Each operation is one transaction: take an
/// exclusive lock file, read the file fresh, apply the change, write it, release. A purchase is reserved before
/// the POST (<see cref="TryReserve"/> checks the limit and writes the reservation in the same transaction),
/// settled once meat and inventory prove it, released when nothing was sent, and counted as unclear when the
/// outcome is not proven. Reservations of other processes (or of a run that crashed) count as spent.
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
        public long Settled { get; set; }
        public long Unclear { get; set; }
        public long Reserved { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _memory = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _own = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _clock;
    private readonly string? _path;
    private readonly TimeSpan _lockTimeout;

    protected FileSpendLedger(string? path, TimeProvider? clock, int lockTimeoutMs = DefaultLockTimeoutMs)
    {
        if (lockTimeoutMs is < MinLockTimeoutMs or > MaxLockTimeoutMs)
            throw new ArgumentOutOfRangeException(nameof(lockTimeoutMs),
                $"The lock timeout must be {MinLockTimeoutMs}..{MaxLockTimeoutMs} ms.");
        _path = path;
        _clock = clock ?? TimeProvider.System;
        _lockTimeout = TimeSpan.FromMilliseconds(lockTimeoutMs);
    }

    /// <summary>The last problem that blocked the ledger, or null when the last operation worked.</summary>
    public string? Problem { get; private set; }

    /// <summary>True when the ledger file cannot be read or locked right now.</summary>
    public bool Unreadable
    {
        get
        {
            try { Transact(_ => 0, write: false); return false; }
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

    private static Entry Current(Dictionary<string, Entry> entries, string account, string today)
    {
        if (!entries.TryGetValue(account, out var e) || e.Day != today)
            entries[account] = e = new Entry { Day = today };
        return e;
    }

    private long Own(string account) => _own.TryGetValue(account, out var v) ? v : 0;

    private long Foreign(Entry e, string account) => Math.Max(0, e.Reserved - Math.Min(Own(account), e.Reserved));

    /// <summary>
    /// Settled plus unclear plus foreign open reservations of today. The purchase this instance is sending right
    /// now is not included: the gateway adds it itself. A ledger that cannot be read counts as fully spent.
    /// </summary>
    public long SpentToday(string account)
    {
        try
        {
            return Transact(entries =>
            {
                var e = Current(entries, account, Today);
                return e.Settled + e.Unclear + Foreign(e, account);
            }, write: false);
        }
        catch (SpendLedgerException) { return Blocked; }
    }

    /// <summary>Unclear meat plus reservations of other processes (they may be stuck).</summary>
    public long UnclearToday(string account)
    {
        try
        {
            return Transact(entries =>
            {
                var e = Current(entries, account, Today);
                return e.Unclear + Foreign(e, account);
            }, write: false);
        }
        catch (SpendLedgerException) { return Blocked; }
    }

    /// <summary>
    /// Checks <paramref name="limit"/> against everything already counted (including foreign reservations) and
    /// durably writes the reservation, as one cross-process transaction. False (with a readable reason) means
    /// nothing was reserved and nothing may be bought.
    /// </summary>
    public bool TryReserve(string account, long meat, long? limit, out string? error)
    {
        error = null;
        if (meat <= 0) return true;
        if (meat >= Blocked) { error = "The purchase total is not a plausible amount of Meat."; return false; }
        try
        {
            var refused = Transact(entries =>
            {
                var e = Current(entries, account, Today);
                var counted = e.Settled + e.Unclear + e.Reserved;
                if (limit is { } max && counted + meat > max)
                    return string.Create(CultureInfo.InvariantCulture,
                        $"The daily budget would be exceeded: {counted:N0} Meat already counted today (settled, unclear and reserved), {meat:N0} more requested, limit {max:N0}.");
                e.Reserved += meat;
                return null;
            }, write: true);
            if (refused is not null) { error = refused; return false; }
            lock (_gate) _own[account] = Own(account) + meat;
            return true;
        }
        catch (SpendLedgerException e)
        {
            error = $"The spend ledger is blocked, so nothing was bought: {e.Message}";
            return false;
        }
    }

    /// <summary>Written before the POST so a crash in between still counts the meat. Throws when it cannot be stored.</summary>
    public void Reserve(string account, long meat)
    {
        if (!TryReserve(account, meat, null, out var error)) throw new SpendLedgerException(error ?? "Reservation refused.");
    }

    public void Settle(string account, long meat)
        => AfterPurchase(account, meat, e => { e.Reserved = Math.Max(0, e.Reserved - meat); e.Settled += meat; });

    public void Release(string account, long meat)
        => AfterPurchase(account, meat, e => e.Reserved = Math.Max(0, e.Reserved - meat));

    public void MarkUnclear(string account, long meat)
        => AfterPurchase(account, meat, e => { e.Reserved = Math.Max(0, e.Reserved - meat); e.Unclear += meat; });

    /// <summary>
    /// The purchase is over. If the write fails the reservation simply stays in the file, which keeps the meat
    /// counted (the safe side), so this never throws.
    /// </summary>
    private void AfterPurchase(string account, long meat, Action<Entry> apply)
    {
        if (meat <= 0) return;
        lock (_gate) _own[account] = Math.Max(0, Own(account) - meat);
        try { Transact(entries => { apply(Current(entries, account, Today)); return 0; }, write: true); }
        catch (SpendLedgerException) { /* Problem is set; the reservation stays counted. */ }
    }

    /// <summary>The user checked inventory and meat: unclear meat is either spent or not.</summary>
    public void Reconcile(string account, long actuallySpent)
        => Transact(entries =>
        {
            var e = Current(entries, account, Today);
            e.Settled += Math.Max(0, actuallySpent);
            e.Unclear = 0;
            e.Reserved = Math.Min(e.Reserved, Own(account));   // stuck foreign reservations are cleared too
            return 0;
        }, write: true);

    /// <summary>Meat of today that is counted as unclear (spent or not unknown) for this account, excluding reservations. -1 when the ledger is unreadable.</summary>
    public long UnclearAmount(string account)
    {
        try { return Transact(entries => Current(entries, account, Today).Unclear, write: false); }
        catch (SpendLedgerException) { return -1; }
    }

    /// <summary>
    /// Explicit user decision about the unclear meat of today: <paramref name="wasSpent"/> moves it to settled,
    /// otherwise it is released. Only <paramref name="expectedAmount"/> (the amount the user confirmed) is
    /// resolved; if the amount changed meanwhile nothing happens and false is returned.
    /// </summary>
    public bool ResolveUnclear(string account, long expectedAmount, bool wasSpent)
        => Transact(entries =>
        {
            var e = Current(entries, account, Today);
            if (e.Unclear != expectedAmount || expectedAmount <= 0) return false;
            if (wasSpent) e.Settled += e.Unclear;
            e.Unclear = 0;
            return true;
        }, write: true);

    /// <summary>A purchase that delivered less than ordered: the delivered part is settled, the rest released.</summary>
    public void SettlePartial(string account, long reserved, long spent)
        => AfterPurchase(account, reserved, e =>
        {
            var used = Math.Clamp(spent, 0, reserved);
            e.Reserved = Math.Max(0, e.Reserved - reserved);
            e.Settled += used;
        });

    /// <summary>Records meat that was spent and verified without a reservation.</summary>
    public void Add(string account, long meat)
    {
        if (meat <= 0) return;
        Transact(entries => { Current(entries, account, Today).Settled += meat; return 0; }, write: true);
    }

    // ---- transaction plumbing -------------------------------------------------------------------------

    private T Transact<T>(Func<Dictionary<string, Entry>, T> body, bool write)
    {
        lock (_gate)
        {
            try
            {
                T result;
                if (_path is null)
                    result = body(_memory);
                else
                {
                    using var _ = AcquireLock();
                    var entries = ReadFile();
                    result = body(entries);
                    if (write) WriteFile(entries);
                }
                Problem = null;
                return result;
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

    private Dictionary<string, Entry> ReadFile()
    {
        var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_path!)) return entries;
        var today = Today;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(_path!)) as JsonObject
                ?? throw new FormatException("the top level is not an object");
            // Current format {"version":1,"accounts":{...}}; the older flat {"account":{...}} is still read.
            var accounts = root["accounts"] as JsonObject ?? (root.ContainsKey("accounts") ? null : root)
                ?? throw new FormatException("'accounts' is not an object");
            foreach (var (name, node) in accounts)
            {
                if (node is not JsonObject o) throw new FormatException($"entry '{name}' is not an object");
                var day = o["day"]?.GetValue<string>() ?? throw new FormatException($"entry '{name}' has no day");
                var e = new Entry
                {
                    Day = day,
                    Settled = Amount(o, "settled", name),
                    Unclear = Amount(o, "unclear", name),
                    Reserved = Amount(o, "reserved", name)
                };
                if (e.Day == today) entries[name] = e;   // older days are dropped on the next write
            }
            return entries;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
                                      or InvalidOperationException or FormatException)
        {
            throw new SpendLedgerException(
                $"The ledger file '{_path}' is unreadable or unexpected ({e.Message}). Fix or remove it; purchases stay blocked until then.", e);
        }
    }

    private static long Amount(JsonObject o, string key, string name)
    {
        if (o[key] is not { } node) return 0;
        var v = node.GetValue<long>();
        return v is < 0 or >= Blocked ? throw new FormatException($"'{key}' of '{name}' is out of range") : v;
    }

    private void WriteFile(Dictionary<string, Entry> entries)
    {
        try
        {
            var accounts = new JsonObject();
            foreach (var (name, e) in entries)
                accounts[name] = new JsonObject
                { ["day"] = e.Day, ["settled"] = e.Settled, ["unclear"] = e.Unclear, ["reserved"] = e.Reserved };
            var json = new JsonObject { ["version"] = 1, ["accounts"] = accounts }
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
