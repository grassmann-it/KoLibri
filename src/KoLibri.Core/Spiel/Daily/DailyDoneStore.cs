using System.Text.Json;
using KoLibri.Core.Basis;

namespace KoLibri.Core.Spiel;

/// <summary>
/// Remembers which once-per-day actions (for example the Day Shortener from
/// the pile of burning leaves) were already submitted today for an account.
/// The day rolls over at 03:30 UTC (20:30 Arizona). State lives in
/// a small JSON file so CLI, MCP and Desktop agree.
/// </summary>
public sealed class DailyDoneStore
{
    public const string DayShortener = "day-shortener";
    public const string BaconMachine = "bacon-machine";
    public const string CampfireLeaf = "campfire-leaf";
    public const string HermitClovers = "hermit-clovers";
    public static string MemeBuy(string itemName) => "meme-buy:" + itemName.ToLowerInvariant();

    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();

    public DailyDoneStore(string? path = null, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KoLibri", "daily-done.json");
    }

    /// <summary>The KoL day an instant belongs to (rollover shifted to midnight).</summary>
    public static string KolDay(DateTimeOffset now)
        => now.UtcDateTime.AddMinutes(-210).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The marker key for one player: the role plus the player id (or name) the status page reported,
    /// so a role that is switched to another character does not inherit the old character's markers.
    /// Without any identity the role alone is used.</summary>
    public static string AccountKey(AccountRole role, KoLStatusSnapshot? status)
    {
        var identity = status?.PlayerId is { Length: > 0 } id ? id : status?.PlayerName;
        return string.IsNullOrWhiteSpace(identity) ? role.ToString() : $"{role}:{identity.Trim()}";
    }

    public bool IsDone(string account, string action)
    {
        var key = Key(account, action);
        lock (_gate) return Transact(map => map.TryGetValue(key, out var day) && day == KolDay(_clock.GetUtcNow()), write: false);
    }

    public void MarkDone(string account, string action)
    {
        var key = Key(account, action);
        var today = KolDay(_clock.GetUtcNow());
        lock (_gate)
            Transact(map =>
            {
                foreach (var stale in map.Where(kv => kv.Value != today).Select(kv => kv.Key).ToList())
                    map.Remove(stale);
                map[key] = today;
                return true;
            }, write: true);
    }

    /// <summary>Actions marked done today for an account (for the KoLibri facade's DailyMarkers).</summary>
    internal IReadOnlyList<string> DoneToday(string account)
    {
        var prefix = account.ToLowerInvariant() + "|";
        var today = KolDay(_clock.GetUtcNow());
        lock (_gate)
            return Transact(map => (IReadOnlyList<string>)map
                .Where(kv => kv.Value == today && kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(kv => kv.Key[prefix.Length..]).OrderBy(k => k, StringComparer.Ordinal).ToArray(), write: false);
    }

    internal DateTimeOffset Now => _clock.GetUtcNow();

    // Separate from the marker file lock so callers can read and update markers during the action.
    internal async Task<FileStream> LockActionAsync(string action, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var lockPath = $"{_path}.{action}.lock";
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return OpenLock(lockPath); }
            catch (IOException) when (DateTime.UtcNow < deadline)
            { await Task.Delay(25, cancellationToken).ConfigureAwait(false); }
        }
    }

    private static string Key(string account, string action) => $"{account.ToLowerInvariant()}|{action}";

    /// <summary>Reads, changes and writes the marker file as one step that is serialized across processes
    /// (an exclusive lock file) and replaces the file atomically, so concurrent CLI/MCP/Desktop writers cannot
    /// overwrite each other's markers.</summary>
    private T Transact<T>(Func<Dictionary<string, string>, T> body, bool write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        using var gate = AcquireLock();
        var map = Load();
        var result = body(map);
        if (write)
        {
            var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(map));
            File.Move(temp, _path, overwrite: true);
        }
        return result;
    }

    private FileStream AcquireLock()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try { return OpenLock(_path + ".lock"); }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(25); }
        }
    }

    private static FileStream OpenLock(string path) => new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private Dictionary<string, string> Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new();
        }
    }
}
