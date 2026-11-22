using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KoLibri.Core.Spiel;

namespace LibKolibri.Access;

/// <summary>One booked usage line. Chained by hash so later edits are detectable.</summary>
public sealed record UsageEntry
{
    public required long Seq { get; init; }
    public required DateTimeOffset At { get; init; }
    public required string KolDay { get; init; }
    public required string GrantId { get; init; }
    public required string Right { get; init; }
    public required string Operation { get; init; }
    public string? Account { get; init; }
    public required long Amount { get; init; }
    public string? Detail { get; init; }
    public required string PrevHash { get; init; }
    public string Hash { get; init; } = "";
}

/// <summary>
/// An amount held back while an action runs. <see cref="Commit"/> books what KoL
/// actually confirmed; <see cref="Release"/> frees the hold when nothing happened.
/// </summary>
public sealed class UsageReservation
{
    private readonly UsageLedger _ledger;
    private int _settled;

    internal UsageReservation(UsageLedger ledger, string grantId, string right, string operation, string? account, string day, long amount, string id = "")
    {
        Id = id;
        _ledger = ledger; GrantId = grantId; Right = right; Operation = operation; Account = account; Day = day; Amount = amount;
    }

    /// <summary>Id of the persisted hold (empty for a ledger without file).</summary>
    internal string Id { get; }
    public string GrantId { get; }
    public string Right { get; }
    public string Operation { get; }
    public string? Account { get; }
    public string Day { get; }
    public long Amount { get; }

    /// <summary>Books the amount KoL confirmed (0 books nothing). Settles only once.</summary>
    public void Commit(long confirmedAmount, string? detail = null)
    {
        if (Interlocked.Exchange(ref _settled, 1) != 0) return;
        _ledger.Settle(this, Math.Max(0, confirmedAmount), detail);
    }

    /// <summary>
    /// Books <paramref name="confirmedAmount"/> as usual and keeps <paramref name="unclearAmount"/> as an
    /// unreconciled spend in the ledger file: it counts against the budget, also after a restart, until
    /// <see cref="UsageLedger.Reconcile"/> settles it. The rest of the hold is freed.
    /// </summary>
    public void CommitUnclear(long confirmedAmount, long unclearAmount, string? detail = null)
    {
        if (Interlocked.Exchange(ref _settled, 1) != 0) return;
        var confirmed = Math.Clamp(confirmedAmount, 0, Amount);
        _ledger.Settle(this, confirmed, detail, Math.Clamp(unclearAmount, 0, Amount - confirmed));
    }

    public void Release()
    {
        if (Interlocked.Exchange(ref _settled, 1) != 0) return;
        _ledger.Settle(this, 0, null);
    }
}

/// <summary>
/// Per grant and KoL day usage. Amounts count after KoL confirmed the action,
/// not after the HTTP status; open reservations count too so parallel calls cannot overrun.
/// </summary>
public sealed class UsageLedger
{
    private const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _booked = new();
    private readonly HashSet<string> _myHolds = new(StringComparer.Ordinal);
    private readonly Func<string, bool> _ownerAlive;
    private readonly string _holdsPath;
    private readonly string _lockPath;
    private readonly string _owner;
    private long _seq;
    private string _lastHash = Genesis;

    /// <summary>A hold that is written to disk before the action's POST, so a crash cannot lose it.</summary>
    internal sealed record Hold(string Id, string Key, string GrantId, string Right, string Operation, string? Account, string Day, long Amount, string Owner);

    /// <param name="ownerAlive">Tells whether the process that wrote a hold ("pid:startTicks") still runs.
    /// Holds of dead processes are adopted at startup as unreconciled spends.</param>
    public UsageLedger(string? path = null, TimeProvider? clock = null, Func<string, bool>? ownerAlive = null)
    {
        _clock = clock ?? TimeProvider.System;
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KoLibri", "usage.jsonl");
        _holdsPath = _path + ".holds";
        _lockPath = _path + ".lock";
        _owner = OwnerOf(Environment.ProcessId) ?? Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _ownerAlive = ownerAlive ?? DefaultOwnerAlive;
        lock (_gate)
            using (FileLock())
            {
                Refresh();
                // Adopt: a hold left by a process that is gone (crash after the POST) becomes an unreconciled
                // spend that counts against the budget until the user reconciles it.
                var holds = ReadHolds();
                var dead = holds.Where(h => !_ownerAlive(h.Owner)).ToList();
                if (dead.Count > 0)
                {
                    foreach (var h in dead)
                        Append(h.Key, h.GrantId, h.Right, h.Day, UnclearPrefix + h.Operation, h.Account, h.Amount,
                            "adopted: the process holding this reservation ended before it was settled");
                    WriteHolds(holds.Where(h => dead.All(d => d.Id != h.Id)).ToList());
                }
            }
    }

    private static string? OwnerOf(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return $"{pid}:{p.StartTime.ToUniversalTime().Ticks}";
        }
        catch { return null; }
    }

    private static bool DefaultOwnerAlive(string owner)
    {
        var parts = owner.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var pid)) return false;
        return OwnerOf(pid) == owner;
    }

    /// <summary>Cross-process lock: only one process at a time checks the budget and writes the file.</summary>
    private FileStream FileLock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_lockPath))!);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            try { return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(25); }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow < deadline) { Thread.Sleep(25); }
        }
    }

    /// <summary>Re-reads what other processes wrote. Call under the file lock.</summary>
    private void Refresh()
    {
        _booked.Clear();
        _seq = 0; _lastHash = Genesis;
        foreach (var entry in ReadAll())
        {
            _seq = entry.Seq;
            _lastHash = entry.Hash;
            var k = Key(entry.GrantId, entry.Right, entry.KolDay);
            _booked[k] = _booked.GetValueOrDefault(k) + entry.Amount;
        }
    }

    private List<Hold> ReadHolds()
    {
        if (!File.Exists(_holdsPath)) return new();
        try { return JsonSerializer.Deserialize<List<Hold>>(File.ReadAllText(_holdsPath), Json) ?? new(); }
        catch (JsonException) { throw new IOException("The reservation file is unreadable; refusing to continue."); }
    }

    /// <summary>Atomic replace: write a temp file, then move it over.</summary>
    private void WriteHolds(List<Hold> holds)
    {
        var tmp = _holdsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(holds, Json));
        File.Move(tmp, _holdsPath, overwrite: true);
    }

    private long HeldFor(List<Hold> holds, string key) => holds.Where(h => h.Key == key).Sum(h => h.Amount);

    public string Today => DailyDoneStore.KolDay(_clock.GetUtcNow());

    /// <summary>Booked plus currently held amount for today.</summary>
    public long UsedToday(string grantId, string right)
    {
        var key = Key(grantId, right, Today);
        lock (_gate)
            using (FileLock())
            {
                Refresh();
                return _booked.GetValueOrDefault(key) + HeldFor(ReadHolds(), key);
            }
    }

    /// <summary>Holds <paramref name="amount"/> if it fits under the budget; null otherwise.</summary>
    public UsageReservation? TryReserve(string grantId, string right, string operation, string? account, long amount, long dailyBudget)
    {
        var day = Today;
        var key = Key(grantId, right, day);
        lock (_gate)
        {
            try
            {
                using (FileLock())
                {
                    // Check and reserve under one cross-process lock; the hold is on disk before the caller may POST.
                    Refresh();
                    var holds = ReadHolds();
                    var used = _booked.GetValueOrDefault(key) + HeldFor(holds, key);
                    if (amount < 0 || used + amount > dailyBudget) return null;
                    var id = Guid.NewGuid().ToString("N");
                    holds.Add(new Hold(id, key, grantId, right, operation, account, day, amount, _owner));
                    WriteHolds(holds);
                    _myHolds.Add(id);
                    return new UsageReservation(this, grantId, right, operation, account, day, amount, id);
                }
            }
            catch (IOException) { return null; }               // no safe reservation: no spend
            catch (UnauthorizedAccessException) { return null; }
        }
    }

    /// <summary>Operation prefix of a spend whose outcome is unknown.</summary>
    public const string UnclearPrefix = "unclear:";
    /// <summary>Operation prefix of the entry that settles an unclear one.</summary>
    public const string ReconcilePrefix = "reconcile:";

    internal void Settle(UsageReservation reservation, long confirmed, string? detail, long unclear = 0)
    {
        var key = Key(reservation.GrantId, reservation.Right, reservation.Day);
        lock (_gate)
            using (FileLock())
            {
                Refresh();
                if (confirmed > 0) Append(key, reservation, reservation.Operation, confirmed, detail);
                if (unclear > 0) Append(key, reservation, UnclearPrefix + reservation.Operation, unclear, detail);
                // The hold goes last: a crash in between over-counts (safe) instead of losing the amount.
                var holds = ReadHolds();
                if (holds.RemoveAll(h => h.Id == reservation.Id) > 0) WriteHolds(holds);
                _myHolds.Remove(reservation.Id);
            }
    }

    private UsageEntry Append(string key, UsageReservation r, string operation, long amount, string? detail)
        => Append(key, r.GrantId, r.Right, r.Day, operation, r.Account, amount, detail);

    private UsageEntry Append(string key, string grantId, string right, string day, string operation, string? account, long amount, string? detail)
    {
        var entry = new UsageEntry
        {
            Seq = _seq + 1, At = _clock.GetUtcNow(), KolDay = day, GrantId = grantId,
            Right = right, Operation = operation, Account = account,
            Amount = amount, Detail = detail, PrevHash = _lastHash,
        };
        entry = entry with { Hash = Hash(entry) };
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.AppendAllText(_path, JsonSerializer.Serialize(entry, Json) + "\n");
        _seq = entry.Seq;
        _lastHash = entry.Hash;
        _booked[key] = _booked.GetValueOrDefault(key) + amount;
        return entry;
    }

    /// <summary>Unclear spends that nobody has reconciled yet (also from before a restart).</summary>
    public IReadOnlyList<UsageEntry> Unreconciled(string? grantId = null)
    {
        lock (_gate)
        {
            var all = ReadAll();
            var settled = all.Where(e => e.Operation.StartsWith(ReconcilePrefix, StringComparison.Ordinal))
                .Select(e => e.Operation[ReconcilePrefix.Length..]).ToHashSet(StringComparer.Ordinal);
            return all.Where(e => e.Operation.StartsWith(UnclearPrefix, StringComparison.Ordinal)
                    && !settled.Contains(e.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    && (grantId is null || e.GrantId == grantId)).ToList();
        }
    }

    /// <summary>
    /// Explicitly settles one unclear spend after the user checked the account: books what KoL really
    /// charged (0 frees it entirely) and closes the entry. False when it is not an open unclear entry.
    /// </summary>
    public bool Reconcile(long seq, long actualAmount, string? note = null)
    {
        lock (_gate)
        using (FileLock())
        {
            Refresh();
            var entry = Unreconciled().FirstOrDefault(e => e.Seq == seq);
            if (entry is null) return false;
            var delta = Math.Clamp(actualAmount, 0, entry.Amount) - entry.Amount;
            Append(Key(entry.GrantId, entry.Right, entry.KolDay), entry.GrantId, entry.Right, entry.KolDay,
                ReconcilePrefix + seq.ToString(System.Globalization.CultureInfo.InvariantCulture), entry.Account, delta, note);
            return true;
        }
    }

    public IReadOnlyList<UsageEntry> Entries(string? grantId = null)
    {
        lock (_gate) return ReadAll().Where(e => grantId is null || e.GrantId == grantId).ToList();
    }

    /// <summary>True when every line links to the previous one and no line was altered.</summary>
    public bool VerifyChain()
    {
        lock (_gate)
        {
            var prev = Genesis;
            long seq = 0;
            foreach (var entry in ReadAll())
            {
                if (entry.PrevHash != prev || entry.Seq != ++seq || entry.Hash != Hash(entry)) return false;
                prev = entry.Hash;
            }
            return true;
        }
    }

    private List<UsageEntry> ReadAll()
    {
        var list = new List<UsageEntry>();
        if (!File.Exists(_path)) return list;
        foreach (var line in File.ReadAllLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { var e = JsonSerializer.Deserialize<UsageEntry>(line, Json); if (e is not null) list.Add(e); }
            catch (JsonException) { /* a broken line breaks VerifyChain via the sequence check */ }
        }
        return list;
    }

    private static string Hash(UsageEntry e)
    {
        var text = string.Join('|', e.PrevHash, e.Seq, e.At.ToUnixTimeMilliseconds(), e.KolDay, e.GrantId,
            e.Right, e.Operation, e.Account, e.Amount, e.Detail);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static string Key(string grantId, string right, string day) => $"{grantId}|{right}|{day}";
}
