using System.Text.Json;
using System.Text.Json.Nodes;
using KoLibri.Core.Basis;
using KoLibri.Core.Markt;

namespace KoLibri.Desktop;

/// <summary>One persisted number with its default and allowed range (min/max are the UI/clamp bounds).</summary>
public sealed record SettingDefinition(string Key, string Label, double Default, double Min, double Max, string Unit);

/// <summary>Where an effective interval comes from.</summary>
public sealed record EffectiveSetting(double Value, bool OverriddenByEnvironment, string? EnvironmentVariable);

/// <summary>
/// Persistent desktop settings: a small, versioned, non-secret JSON file (never credentials). Every numeric
/// value has a default, a minimum and a maximum; missing, invalid or out-of-range values fall back or are clamped.
/// </summary>
public sealed class DesktopSettings
{
    public const int CurrentVersion = 1;

    public static readonly IReadOnlyList<SettingDefinition> Definitions =
    [
        new("meatPerDay", "Meat per day", 500_000, 0, 100_000_000, "Meat"),
        new("meatPerPurchase", "Meat per purchase", 250_000, 0, 100_000_000, "Meat"),
        new("unitsPerPurchase", "Units per purchase", 10, 1, 1_000, "units"),
        new("resultLimit", "Result / page limit", 50, 1, 500, "rows"),
        new("kolIntervalSeconds", "KoL request interval", 2, 1, 60, "s"),
        new("pricegunIntervalSeconds", "Pricegun request interval", 2, 1, 60, "s"),
        new("cacheMinutes", "Cache duration", 20, 1, 1_440, "min"),
        new("maxStaleHours", "Maximum age of stale data", 12, 1, 168, "h"),
        new("turnBudget", "Turn budget", 100, 1, 1_000, "turns"),
        new("hpStopPercent", "HP stop", 30, 0, 100, "%"),
        new("maxCombatRounds", "Max combat rounds", 30, 1, 50, "rounds"),
    ];

    public int Version { get; set; } = CurrentVersion;
    public string? DefaultAccount { get; set; }
    public bool ReloginAutomatically { get; set; }
    public bool PricegunDisplay { get; set; } = true;
    public bool DailyDefaults { get; set; } = true;
    public bool OptionalTools { get; set; } = true;
    public string Theme { get; set; } = "Light";
    public Dictionary<string, double> Numbers { get; set; } = new();

    public static SettingDefinition Definition(string key) => Definitions.First(d => d.Key == key);

    public double Get(string key)
    {
        var d = Definition(key);
        return Numbers.TryGetValue(key, out var v) ? Clamp(d, v) : d.Default;
    }

    public void Set(string key, double value) => Numbers[key] = Clamp(Definition(key), value);

    public static double Clamp(SettingDefinition d, double v)
        => double.IsNaN(v) || double.IsInfinity(v) ? d.Default : Math.Min(d.Max, Math.Max(d.Min, v));

    public TimeSpan CacheDuration => TimeSpan.FromMinutes(Get("cacheMinutes"));
    public TimeSpan MaxStale
    {
        get
        {
            var stale = TimeSpan.FromHours(Get("maxStaleHours"));
            return stale < CacheDuration ? CacheDuration : stale;
        }
    }

    /// <summary>Settings for the Pricegun read client (interval as configured; the limiter applies the env override).</summary>
    public PricegunReadPolicy ToPricegunPolicy() => new()
    {
        CacheTtl = CacheDuration,
        MaxStale = MaxStale,
        MaxSales = (int)Get("resultLimit") is var n && n > 0 ? Math.Max(n, 1) : 100,
    };

    /// <summary>Effective KoL interval: environment variable wins, then settings; floor 1 s.</summary>
    public EffectiveSetting EffectiveKolInterval(Func<string, string?>? env = null)
        => Effective("kolIntervalSeconds", KoLRateLimiter.IntervalEnvironmentVariable, env);

    public EffectiveSetting EffectivePricegunInterval(Func<string, string?>? env = null)
        => Effective("pricegunIntervalSeconds", PricegunRateLimit.IntervalEnvironmentVariable, env);

    private EffectiveSetting Effective(string key, string variable, Func<string, string?>? env)
    {
        var raw = (env ?? Environment.GetEnvironmentVariable)(variable);
        var over = KoLRateLimiter.IsEnvironmentOverride(raw);
        var span = KoLRateLimiter.EffectiveInterval(TimeSpan.FromSeconds(Get(key)), raw);
        return new EffectiveSetting(span.TotalSeconds, over, over ? variable : null);
    }

    /// <summary>Hands the configured intervals to the process-wide limiters; call once at startup, before the first request.</summary>
    public void ApplyToLimiters(Func<string, string?>? env = null)
    {
        var get = env ?? Environment.GetEnvironmentVariable;
        KoLRateLimiter.Shared.ApplyConfigured(TimeSpan.FromSeconds(Get("kolIntervalSeconds")), get(KoLRateLimiter.IntervalEnvironmentVariable));
        PricegunRateLimit.Shared.ApplyConfigured(TimeSpan.FromSeconds(Get("pricegunIntervalSeconds")), get(PricegunRateLimit.IntervalEnvironmentVariable));
    }
}

/// <summary>Reads and writes <see cref="DesktopSettings"/> as JSON; tolerant of missing files and bad values.</summary>
public sealed class DesktopSettingsStore
{
    private static readonly JsonSerializerOptions Write = new() { WriteIndented = true };

    public DesktopSettingsStore(string? path = null) => Path = path ?? DefaultPath();

    public string Path { get; }

    public static string DefaultPath()
    {
        var root = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(root))
            root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return System.IO.Path.Combine(root, "KoLibri", "desktop-settings.json");
    }

    public DesktopSettings Load()
    {
        var settings = new DesktopSettings();
        try
        {
            if (!File.Exists(Path)) return settings;
            if (JsonNode.Parse(File.ReadAllText(Path)) is not JsonObject root) return settings;
            settings.DefaultAccount = Str(root, "defaultAccount");
            settings.ReloginAutomatically = Bool(root, "reloginAutomatically", settings.ReloginAutomatically);
            settings.PricegunDisplay = Bool(root, "pricegunDisplay", settings.PricegunDisplay);
            settings.DailyDefaults = Bool(root, "dailyDefaults", settings.DailyDefaults);
            settings.OptionalTools = Bool(root, "optionalTools", settings.OptionalTools);
            if (Str(root, "theme") is "Light" or "Dark" or "System") settings.Theme = Str(root, "theme")!;
            if (root["numbers"] is JsonObject numbers)
                foreach (var d in DesktopSettings.Definitions)
                    if (numbers[d.Key] is JsonValue v && v.TryGetValue<double>(out var n))
                        settings.Set(d.Key, n);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new DesktopSettings();
        }
        return settings;
    }

    public void Save(DesktopSettings settings)
    {
        var numbers = new Dictionary<string, double>();
        foreach (var d in DesktopSettings.Definitions) numbers[d.Key] = settings.Get(d.Key);
        var json = JsonSerializer.Serialize(new
        {
            version = DesktopSettings.CurrentVersion,
            defaultAccount = settings.DefaultAccount,
            reloginAutomatically = settings.ReloginAutomatically,
            pricegunDisplay = settings.PricegunDisplay,
            dailyDefaults = settings.DailyDefaults,
            optionalTools = settings.OptionalTools,
            theme = settings.Theme,
            numbers,
        }, Write);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, Path, overwrite: true);
    }

    private static string? Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
    private static bool Bool(JsonObject o, string k, bool d) => o[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : d;
}
