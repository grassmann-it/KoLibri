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

    internal UsageReservation(UsageLedger ledger, string grantId, string right, string operation, string? account, string day, long amount)
    {
        _ledger = ledger; GrantId = grantId; Right = right; Operation = operation; Account = account; Day = day; Amount = amount;
    }

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
    private readonly Dictionary<string, long> _held = new();
    private long _seq;
    private string _lastHash = Genesis;

    public UsageLedger(string? path = null, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KoLibri", "usage.jsonl");
        foreach (var entry in ReadAll())
        {
            _seq = entry.Seq;
            _lastHash = entry.Hash;
            _booked[Key(entry.GrantId, entry.Right, entry.KolDay)] = _booked.GetValueOrDefault(Key(entry.GrantId, entry.Right, entry.KolDay)) + entry.Amount;
        }
    }

    public string Today => DailyDoneStore.KolDay(_clock.GetUtcNow());

    /// <summary>Booked plus currently held amount for today.</summary>
    public long UsedToday(string grantId, string right)
    {
        var key = Key(grantId, right, Today);
        lock (_gate) return _booked.GetValueOrDefault(key) + _held.GetValueOrDefault(key);
    }

    /// <summary>Holds <paramref name="amount"/> if it fits under the budget; null otherwise.</summary>
    public UsageReservation? TryReserve(string grantId, string right, string operation, string? account, long amount, long dailyBudget)
    {
        var day = Today;
        var key = Key(grantId, right, day);
        lock (_gate)
        {
            var used = _booked.GetValueOrDefault(key) + _held.GetValueOrDefault(key);
            if (amount < 0 || used + amount > dailyBudget) return null;
            _held[key] = _held.GetValueOrDefault(key) + amount;
            return new UsageReservation(this, grantId, right, operation, account, day, amount);
        }
    }

    internal void Settle(UsageReservation reservation, long confirmed, string? detail)
    {
        var key = Key(reservation.GrantId, reservation.Right, reservation.Day);
        lock (_gate)
        {
            _held[key] = Math.Max(0, _held.GetValueOrDefault(key) - reservation.Amount);
            if (confirmed <= 0) return;
            var entry = new UsageEntry
            {
                Seq = _seq + 1, At = _clock.GetUtcNow(), KolDay = reservation.Day, GrantId = reservation.GrantId,
                Right = reservation.Right, Operation = reservation.Operation, Account = reservation.Account,
                Amount = confirmed, Detail = detail, PrevHash = _lastHash,
            };
            entry = entry with { Hash = Hash(entry) };
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.AppendAllText(_path, JsonSerializer.Serialize(entry, Json) + "\n");
            _seq = entry.Seq;
            _lastHash = entry.Hash;
            _booked[key] = _booked.GetValueOrDefault(key) + confirmed;
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
