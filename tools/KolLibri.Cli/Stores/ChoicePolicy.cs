// CLI copy of the 0.4 store from KoLibri.Core (0.5 step 3): same file format, so tokens and settings stay valid.
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace KoLibri.Cli;

/// <summary>What to do with a choice adventure that has no explicit allowlist rule.</summary>
internal enum ChoiceResolutionMode
{
    /// <summary>Default. Stop with NeedsDecision unless a per-choice default is configured.</summary>
    Ask,
    /// <summary>Additionally use the built-in table and a conservative "leave/ignore/skip" label match.</summary>
    AutoSafe
}

internal enum ChoiceResolutionSource { None, Configured, BuiltIn, SafeLabel }

/// <summary>A configured default for one choice id (choice-policy.json).</summary>
internal sealed record ChoiceDefaultEntry
{
    public int Option { get; init; }
    public string? Title { get; init; }
    public string? Note { get; init; }
}

/// <summary>
/// User configuration for automatic choice handling. Numeric values have a
/// default and a hard range (see the *Min/*Max constants) that <see cref="Normalize"/> enforces.
/// </summary>
internal sealed record ChoiceResolutionConfig
{
    public const int MaxMeatCostDefault = 0, MaxMeatCostMin = 0, MaxMeatCostMax = 1_000_000;
    public const int MaxHpPercentDefault = 0, MaxHpPercentMin = 0, MaxHpPercentMax = 100;
    public const int MaxAdventureCostDefault = 0, MaxAdventureCostMin = 0, MaxAdventureCostMax = 10;

    public ChoiceResolutionMode Mode { get; init; } = ChoiceResolutionMode.Ask;
    /// <summary>Highest meat cost a built-in/auto-safe option may have.</summary>
    public int MaxMeatCost { get; init; } = MaxMeatCostDefault;
    /// <summary>Highest HP loss (percent of max HP) a built-in/auto-safe option may have.</summary>
    public int MaxHpPercent { get; init; } = MaxHpPercentDefault;
    /// <summary>Highest adventure cost a built-in/auto-safe option may have.</summary>
    public int MaxAdventureCost { get; init; } = MaxAdventureCostDefault;
    /// <summary>Per choice id defaults. These are explicit user decisions and are not cost-checked.</summary>
    public Dictionary<string, ChoiceDefaultEntry> Choices { get; init; } = new(StringComparer.Ordinal);

    public ChoiceResolutionConfig Normalize()
        => this with
        {
            MaxMeatCost = Math.Clamp(MaxMeatCost, MaxMeatCostMin, MaxMeatCostMax),
            MaxHpPercent = Math.Clamp(MaxHpPercent, MaxHpPercentMin, MaxHpPercentMax),
            MaxAdventureCost = Math.Clamp(MaxAdventureCost, MaxAdventureCostMin, MaxAdventureCostMax),
            Choices = new Dictionary<string, ChoiceDefaultEntry>(
                (Choices ?? new()).Where(x => !string.IsNullOrWhiteSpace(x.Key) && x.Value is { Option: > 0 })
                    .Select(x => KeyValuePair.Create(x.Key.Trim(), x.Value)),
                StringComparer.Ordinal)
        };
}

/// <summary>Reads and writes <see cref="ChoiceResolutionConfig"/> as JSON in the app data dir.</summary>
internal sealed class ChoiceResolutionConfigStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public ChoiceResolutionConfigStore(string? path = null) => Path_ = path ?? DefaultPath;
    private string Path_ { get; }
    public string Path => Path_;

    public static string DefaultPath
        => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KoLibri", "choice-policy.json");

    public ChoiceResolutionConfig Load()
    {
        try
        {
            if (!File.Exists(Path_)) return new ChoiceResolutionConfig().Normalize();
            var loaded = JsonSerializer.Deserialize<ChoiceResolutionConfig>(File.ReadAllText(Path_), Json);
            return (loaded ?? new ChoiceResolutionConfig()).Normalize();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            // A broken file must never auto-pick anything: fall back to the strict default.
            return new ChoiceResolutionConfig().Normalize();
        }
    }

    public void Save(ChoiceResolutionConfig config)
    {
        var dir = System.IO.Path.GetDirectoryName(Path_);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = Path_ + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config.Normalize(), Json));
        File.Move(temp, Path_, overwrite: true);
    }
}

/// <summary>One visible option of a choice page.</summary>
