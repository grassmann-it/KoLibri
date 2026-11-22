using System.Text.Json;
using System.Text.Json.Nodes;
using KoLibri.Core.Basis;
using KoLibri.Core.Markt;

namespace KoLibri.Desktop;

/// <summary>One persisted number with its default and allowed range (min/max are the UI/clamp bounds).</summary>
public sealed record SettingDefinition(string Key, string Label, double Default, double Min, double Max, string Unit);

/// <summary>One user-selected item the Daily tab consumes.</summary>
public sealed record DailyConsumable(int ItemId, string Name, int Quantity)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string Label => $"{Name} (#{ItemId}) × {Quantity}";
}

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
        new("cloverThreshold", "11-leaf clover highlight price", 27_000, 1_000, 1_000_000, "Meat"),
        new("resultLimit", "Result / page limit", 50, 1, 500, "rows"),
        new("kolIntervalSeconds", "KoL request interval", 2, 1, 60, "s"),
        new("kolReadIntervalSeconds", "KoL read interval (sidebar, status, searches)", 1, 0.2, 60, "s"),
        new("kolReadConcurrency", "KoL reads in flight at once", 2, 1, 4, "requests"),
        new("kolMaxPerSecond", "KoL requests per second, all lanes", 2, 1, 5, "req/s"),
        new("pricegunIntervalSeconds", "Pricegun request interval", 2, 1, 60, "s"),
        new("cacheMinutes", "Cache duration", 20, 1, 1_440, "min"),
        new("maxStaleHours", "Maximum age of stale data", 12, 1, 168, "h"),
        new("turnBudget", "Turn budget", 100, 1, 1_000, "turns"),
        new("hpStopPercent", "HP stop", 30, 0, 100, "%"),
        new("maxCombatRounds", "Max combat rounds", 30, 1, 50, "rounds"),
        new("dailyKnucklebonesTarget", "Daily knucklebones target", 100, 0, 10_000, "knucklebones"),
        new("dailyRests", "Rests per daily run", 5, 0, 100, "rests"),
    ];

    /// <summary>Most entries one daily consumable list (booze, spleen) may hold.</summary>
    public const int MaxConsumableEntries = 20;
    /// <summary>Largest quantity per consumable entry.</summary>
    public const int MaxConsumableQuantity = 99;

    public int Version { get; set; } = CurrentVersion;
    public string? DefaultAccount { get; set; }
    /// <summary>Default ON: stale or missing sessions are renewed from the stored password at start and when KoL asks for a login.</summary>
    public bool ReloginAutomatically { get; set; } = true;
    /// <summary>Legacy flags kept so old files round-trip. Nothing reads them and the settings page no longer shows them.</summary>
    public bool PricegunDisplay { get; set; } = true;
    /// <inheritdoc cref="PricegunDisplay"/>
    public bool DailyDefaults { get; set; } = true;
    public bool OptionalTools { get; set; } = true;
    public string Theme { get; set; } = "Light";
    /// <summary>"conservative" (default) or "browser-like": base values of the read lane and the global cap; explicit numbers and environment variables still win.</summary>
    public string LimiterPreset { get; set; } = "conservative";
    public Dictionary<string, double> Numbers { get; set; } = new();

    /// <summary>Booze the Daily tab drinks, chosen by the user (no built-in items).</summary>
    public List<DailyConsumable> BoozeItems { get; set; } = new();
    /// <summary>Spleen items the Daily tab takes, chosen by the user.</summary>
    public List<DailyConsumable> SpleenItems { get; set; } = new();
    /// <summary>Steps skipped for a day, key "Role|step" to ISO date.</summary>
    public Dictionary<string, string> SkippedSteps { get; set; } = new();

    public static string SkipKey(string role, string step) => $"{role}|{step}";

    public bool IsSkipped(string role, string step, DateOnly today)
        => SkippedSteps.TryGetValue(SkipKey(role, step), out var d)
           && string.Equals(d, today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

    public void SkipToday(string role, string step, DateOnly today)
    {
        var text = today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        // Forget older days so the file does not grow.
        foreach (var key in SkippedSteps.Where(kv => kv.Value != text).Select(kv => kv.Key).ToList())
            SkippedSteps.Remove(key);
        SkippedSteps[SkipKey(role, step)] = text;
    }

    public void UnskipToday(string role, string step) => SkippedSteps.Remove(SkipKey(role, step));

    /// <summary>Adds or merges an entry; false when the list is full or the id is invalid.</summary>
    public static bool AddConsumable(List<DailyConsumable> list, int itemId, string name, int quantity)
    {
        if (itemId <= 0) return false;
        quantity = Math.Clamp(quantity, 1, MaxConsumableQuantity);
        var at = list.FindIndex(x => x.ItemId == itemId);
        if (at >= 0) { list[at] = list[at] with { Quantity = quantity, Name = string.IsNullOrWhiteSpace(name) ? list[at].Name : name.Trim() }; return true; }
        if (list.Count >= MaxConsumableEntries) return false;
        list.Add(new DailyConsumable(itemId, string.IsNullOrWhiteSpace(name) ? $"Item {itemId}" : name.Trim(), quantity));
        return true;
    }

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

    /// <summary>Settings for the Mall read client: cache duration, maximum age and the "Result / page limit" per item.</summary>
    public MallReadPolicy ToMallPolicy() => new()
    {
        CacheTtl = CacheDuration,
        MaxStale = MaxStale,
        MaxListingsPerItem = (int)Get("resultLimit") is var n && n > 0 ? n : 100,
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

    /// <summary>Lane limits: preset (environment wins over settings), then explicitly set numbers, then environment values; clamped.</summary>
    public KoLLimiterOptions ToLimiterOptions(Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var options = KoLLimiterOptions.Preset(env(KoLLimiterOptions.PresetEnvironmentVariable) ?? LimiterPreset) with
        {
            ActionInterval = KoLRateLimiter.EffectiveInterval(TimeSpan.FromSeconds(Get("kolIntervalSeconds")), env(KoLRateLimiter.IntervalEnvironmentVariable))
        };
        if (Numbers.ContainsKey("kolReadIntervalSeconds")) options = options with { ReadInterval = TimeSpan.FromSeconds(Get("kolReadIntervalSeconds")) };
        if (Numbers.ContainsKey("kolReadConcurrency")) options = options with { ReadConcurrency = (int)Get("kolReadConcurrency") };
        if (Numbers.ContainsKey("kolMaxPerSecond")) options = options with { MaxPerSecond = Get("kolMaxPerSecond") };
        var fromEnv = KoLLimiterOptions.FromEnvironment(env);
        if (double.TryParse(env(KoLLimiterOptions.ReadIntervalEnvironmentVariable), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) options = options with { ReadInterval = fromEnv.ReadInterval };
        if (double.TryParse(env(KoLLimiterOptions.ReadConcurrencyEnvironmentVariable), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) options = options with { ReadConcurrency = fromEnv.ReadConcurrency };
        if (double.TryParse(env(KoLLimiterOptions.MaxPerSecondEnvironmentVariable), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) options = options with { MaxPerSecond = fromEnv.MaxPerSecond };
        return options.Clamped();
    }

    /// <summary>Hands the configured intervals to the process-wide limiters; call once at startup, before the first request.</summary>
    public void ApplyToLimiters(Func<string, string?>? env = null)
    {
        var get = env ?? Environment.GetEnvironmentVariable;
        KoLRateLimiter.Shared.ApplyOptions(ToLimiterOptions(get));
        PricegunRateLimit.Shared.ApplyConfigured(TimeSpan.FromSeconds(Get("pricegunIntervalSeconds")), get(PricegunRateLimit.IntervalEnvironmentVariable));
    }
}

/// <summary>Reads and writes <see cref="DesktopSettings"/> as JSON; tolerant of missing files and bad values.</summary>
public sealed class DesktopSettingsStore
{
    private static readonly JsonSerializerOptions Write = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

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
            settings.BoozeItems = Consumables(root, "boozeItems");
            settings.SpleenItems = Consumables(root, "spleenItems");
            if (root["skippedSteps"] is JsonObject skips)
                foreach (var (k, v) in skips)
                    if (v is JsonValue sv && sv.TryGetValue<string>(out var day) && !string.IsNullOrWhiteSpace(day))
                        settings.SkippedSteps[k] = day;
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
            boozeItems = settings.BoozeItems,
            spleenItems = settings.SpleenItems,
            skippedSteps = settings.SkippedSteps,
            numbers,
        }, Write);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, Path, overwrite: true);
    }

    private static List<DailyConsumable> Consumables(JsonObject o, string key)
    {
        var list = new List<DailyConsumable>();
        if (o[key] is not JsonArray array) return list;
        foreach (var node in array)
        {
            if (node is not JsonObject e) continue;
            var id = e["itemId"] is JsonValue i && i.TryGetValue<int>(out var n) ? n : 0;
            var name = Str(e, "name") ?? "";
            var qty = e["quantity"] is JsonValue q && q.TryGetValue<int>(out var m) ? m : 1;
            DesktopSettings.AddConsumable(list, id, name, qty);
        }
        return list;
    }

    private static string? Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
    private static bool Bool(JsonObject o, string k, bool d) => o[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : d;
}
