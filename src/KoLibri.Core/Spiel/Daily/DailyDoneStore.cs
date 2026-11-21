using System.Text.Json;

namespace KoLibri.Core.Spiel;

/// <summary>
/// Remembers which once-per-day actions (for example the Day Shortener from
/// the pile of burning leaves) were already submitted today for an account.
/// The day rolls over at KoL's rollover, about 03:30 US Eastern. State lives in
/// a small JSON file so CLI, MCP and Desktop agree.
/// </summary>
public sealed class DailyDoneStore
{
    public const string DayShortener = "day-shortener";
    public const string BaconMachine = "bacon-machine";
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
    {
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
        catch (TimeZoneNotFoundException) { zone = TimeZoneInfo.CreateCustomTimeZone("EST", TimeSpan.FromHours(-5), "EST", "EST"); }
        var local = TimeZoneInfo.ConvertTime(now, zone);
        return local.AddHours(-3.5).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }

    public bool IsDone(string account, string action)
    {
        var key = Key(account, action);
        lock (_gate) return Load().TryGetValue(key, out var day) && day == KolDay(_clock.GetUtcNow());
    }

    public void MarkDone(string account, string action)
    {
        var key = Key(account, action);
        var today = KolDay(_clock.GetUtcNow());
        lock (_gate)
        {
            var map = Load();
            foreach (var stale in map.Where(kv => kv.Value != today).Select(kv => kv.Key).ToList())
                map.Remove(stale);
            map[key] = today;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(map));
        }
    }

    private static string Key(string account, string action) => $"{account.ToLowerInvariant()}|{action}";

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
