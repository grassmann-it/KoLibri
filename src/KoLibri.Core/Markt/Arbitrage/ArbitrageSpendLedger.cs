using System.Globalization;
using System.Text.Json;

namespace KoLibri.Core.Markt;

/// <summary>
/// Meat spent through arbitrage purchases per account and KoL day, kept in a small JSON file (never
/// credentials) so the budget survives a restart. A purchase is reserved before the POST, settled once meat
/// and inventory prove it, released when nothing was sent, and counted as unclear when the outcome is not
/// proven. A reservation still open at startup counts as unclear. An unreadable file counts as fully spent.
/// Without a path the ledger lives in memory only.
/// </summary>
public sealed class ArbitrageSpendLedger
{
    private sealed class Entry
    {
        public string Day { get; set; } = "";
        public long Settled { get; set; }
        public long Unclear { get; set; }
        public long Reserved { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _clock;
    private readonly string? _path;
    private bool _unreadable;

    public ArbitrageSpendLedger(string? path = null, TimeProvider? clock = null)
    {
        _path = path;
        _clock = clock ?? TimeProvider.System;
        Load();
    }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KoLibri", "arbitrage-ledger.json");

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

    private Entry Current(string account)
    {
        var today = Today;
        if (!_entries.TryGetValue(account, out var e) || e.Day != today)
            _entries[account] = e = new Entry { Day = today };
        return e;
    }

    /// <summary>Settled plus unclear meat of today. The purchase being sent right now is not included: the gateway adds it itself.</summary>
    public long SpentToday(string account)
    {
        lock (_gate)
        {
            var e = Current(account);
            return e.Settled + e.Unclear + (_unreadable ? long.MaxValue / 4 : 0);
        }
    }

    public long UnclearToday(string account) { lock (_gate) return Current(account).Unclear; }

    /// <summary>Written before the POST so a crash in between still counts the meat.</summary>
    public void Reserve(string account, long meat) => Change(account, meat, e => e.Reserved += meat);

    public void Settle(string account, long meat)
        => Change(account, meat, e => { e.Reserved = Math.Max(0, e.Reserved - meat); e.Settled += meat; });

    public void Release(string account, long meat)
        => Change(account, meat, e => e.Reserved = Math.Max(0, e.Reserved - meat));

    public void MarkUnclear(string account, long meat)
        => Change(account, meat, e => { e.Reserved = Math.Max(0, e.Reserved - meat); e.Unclear += meat; });

    private void Change(string account, long meat, Action<Entry> apply)
    {
        if (meat <= 0) return;
        lock (_gate)
        {
            apply(Current(account));
            Save();
        }
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path));
            if (loaded is null) { _unreadable = true; return; }
            foreach (var (account, entry) in loaded)
            {
                // A reservation that was never settled may have been sent: it counts as unclear.
                entry.Unclear += entry.Reserved;
                entry.Reserved = 0;
                _entries[account] = entry;
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            _unreadable = true;
        }
    }

    private void Save()
    {
        if (_path is null) return;
        var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        Directory.CreateDirectory(directory);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_entries));
        File.Move(temp, _path, overwrite: true);
    }
}
