using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace KoLibri.Core.Spiel;

/// <summary>What to do with a choice adventure that has no explicit allowlist rule.</summary>
public enum ChoiceResolutionMode
{
    /// <summary>Default. Stop with NeedsDecision unless a per-choice default is configured.</summary>
    Ask,
    /// <summary>Additionally use the built-in table and a conservative "leave/ignore/skip" label match.</summary>
    AutoSafe
}

public enum ChoiceResolutionSource { None, Configured, BuiltIn, SafeLabel }

/// <summary>A configured default for one choice id (choice-policy.json).</summary>
public sealed record ChoiceDefaultEntry
{
    public int Option { get; init; }
    public string? Title { get; init; }
    public string? Note { get; init; }
}

/// <summary>
/// User configuration for automatic choice handling. Numeric values have a
/// default and a hard range (see the *Min/*Max constants) that <see cref="Normalize"/> enforces.
/// </summary>
public sealed record ChoiceResolutionConfig
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
public sealed class ChoiceResolutionConfigStore
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
public sealed record ChoiceOptionInfo(int Number, string Text);

public sealed record ChoiceResolutionRequest(
    string AdventureId,
    string ChoiceId,
    string? Title,
    IReadOnlyList<ChoiceOptionInfo> Options);

public sealed record ChoiceResolution(int? Option, ChoiceResolutionSource Source, string Reason)
{
    public static ChoiceResolution None(string reason) => new(null, ChoiceResolutionSource.None, reason);
}

/// <summary>Journal line for every automatic decision.</summary>
public sealed record ChoiceResolutionLogEntry(
    string AdventureId, string ChoiceId, string? Title, int Option, ChoiceResolutionSource Source, string Reason);

public interface IChoiceResolver
{
    ChoiceResolution Resolve(ChoiceResolutionRequest request);
}

/// <summary>A built-in, cost-annotated safe option for a choice id. Extend <see cref="ChoiceResolver.BuiltInTable"/>.</summary>
public sealed record BuiltInChoiceRule(
    string ChoiceId, int Option, string Title, int MeatCost = 0, int HpPercentCost = 0, int AdventureCost = 0);

/// <summary>
/// Picks an option for a choice that has no allowlist rule. Per-choice defaults always apply; the
/// built-in table and label match only apply in <see cref="ChoiceResolutionMode.AutoSafe"/>.
/// It never selects a costly option unless the user configured that exact choice id.
/// </summary>
public sealed class ChoiceResolver : IChoiceResolver
{
    /// <summary>
    /// Built-in safe options. KoLmafia's data bundled in external/kolmafia-data has no choice
    /// adventure table, so this list starts empty on purpose: only add an id once the option
    /// was verified (ids/options from KoLmafia's choice-adventure data or the wiki).
    /// </summary>
    public static readonly IReadOnlyList<BuiltInChoiceRule> BuiltInTable = Array.Empty<BuiltInChoiceRule>();

    private static readonly Regex LeaveLabel = new(
        @"^\s*(leave|ignore|skip|walk away|never ?mind|go away|decline|no thanks|run away|move on|do nothing|turn back|back away|keep walking)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CostWords = new(
        @"\b(meat|hp|adventures?|pay|buy|spend|sacrifice|give|trade|fight|attack|lose|drink|eat|use)\b|\d",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly ChoiceResolutionConfig _config;
    private readonly IReadOnlyList<BuiltInChoiceRule> _table;
    private readonly List<ChoiceResolutionLogEntry> _log = new();

    public ChoiceResolver(ChoiceResolutionConfig? config = null, IReadOnlyList<BuiltInChoiceRule>? builtIn = null)
    {
        _config = (config ?? new ChoiceResolutionConfig()).Normalize();
        _table = builtIn ?? BuiltInTable;
    }

    public ChoiceResolutionConfig Config => _config;
    public IReadOnlyList<ChoiceResolutionLogEntry> Log => _log;
    public event Action<ChoiceResolutionLogEntry>? Resolved;

    public ChoiceResolution Resolve(ChoiceResolutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var numbers = request.Options.Select(o => o.Number).ToHashSet();
        var result = ResolveCore(request, numbers);
        if (result.Option is { } option)
        {
            var entry = new ChoiceResolutionLogEntry(
                request.AdventureId, request.ChoiceId, request.Title, option, result.Source, result.Reason);
            _log.Add(entry);
            Resolved?.Invoke(entry);
        }
        return result;
    }

    private ChoiceResolution ResolveCore(ChoiceResolutionRequest request, HashSet<int> numbers)
    {
        if (_config.Choices.TryGetValue(request.ChoiceId, out var configured))
        {
            return numbers.Contains(configured.Option)
                ? new ChoiceResolution(configured.Option, ChoiceResolutionSource.Configured,
                    $"Configured default for choice {request.ChoiceId}.")
                : ChoiceResolution.None(
                    $"Configured option {configured.Option} for choice {request.ChoiceId} is not visible on the page.");
        }
        if (_config.Mode != ChoiceResolutionMode.AutoSafe)
            return ChoiceResolution.None("No default is configured for this choice and the mode is 'ask'.");

        var rule = _table.FirstOrDefault(r => r.ChoiceId == request.ChoiceId);
        if (rule is not null)
        {
            if (!numbers.Contains(rule.Option))
                return ChoiceResolution.None($"Built-in option {rule.Option} is not visible on the page.");
            if (rule.MeatCost > _config.MaxMeatCost || rule.HpPercentCost > _config.MaxHpPercent
                || rule.AdventureCost > _config.MaxAdventureCost)
                return ChoiceResolution.None("The built-in option costs more than the configured limits allow.");
            return new ChoiceResolution(rule.Option, ChoiceResolutionSource.BuiltIn,
                $"Built-in safe option for '{rule.Title}'.");
        }

        var candidates = request.Options
            .Where(o => LeaveLabel.IsMatch(o.Text) && !CostWords.IsMatch(o.Text))
            .ToArray();
        if (candidates.Length == 1)
            return new ChoiceResolution(candidates[0].Number, ChoiceResolutionSource.SafeLabel,
                $"Only option that reads as leave/ignore/skip without a cost: '{candidates[0].Text}'.");
        return ChoiceResolution.None(candidates.Length == 0
            ? "No option is recognisably free to leave."
            : "More than one option looks like leaving; ambiguous.");
    }
}

/// <summary>Everything a caller needs to ask the user and resume a stopped run.</summary>
public sealed record NeedsDecisionInfo
{
    public required string RunId { get; init; }
    public string? AdventureId { get; init; }
    public string? ChoiceId { get; init; }
    public string? Title { get; init; }
    public IReadOnlyList<ChoiceOptionInfo> Options { get; init; } = Array.Empty<ChoiceOptionInfo>();
    public string? Reason { get; init; }
    public string AccountRole { get; init; } = string.Empty;
    /// <summary>Instruction for AI clients.</summary>
    public string Instruction { get; init; } =
        "A choice adventure is open and needs the user's decision. Show the options to the user and ask which one to take; do not guess.";

    /// <summary>Exact CLI command that submits the decision once the user chose (preview until --execute).</summary>
    public string ResumeCommand(string option = "<N>")
        => $"kolibri choice submit --role {AccountRole.ToLowerInvariant()} --choice {ChoiceId} --option {option} --execute --approval-token <token>";
}

public static class NeedsDecisionExtensions
{
    /// <summary>
    /// Describes the open choice of a checkpoint that stopped awaiting a decision; null when the
    /// checkpoint has a pending (already decided) choice or is not on a choice at all.
    /// </summary>
    public static NeedsDecisionInfo? ToNeedsDecision(this AdventureCheckpoint? checkpoint)
    {
        if (checkpoint is null || checkpoint.Phase != AdventureRunPhase.Choice
            || !string.IsNullOrWhiteSpace(checkpoint.PendingChoiceOption)
            || string.IsNullOrWhiteSpace(checkpoint.ChoiceId))
            return null;
        var labels = checkpoint.ManualChoiceLabels ?? new Dictionary<string, string>();
        var options = checkpoint.ManualChoiceOptions
            .Where(x => int.TryParse(x, out _))
            .Select(x => new ChoiceOptionInfo(int.Parse(x), labels.TryGetValue(x, out var t) ? t : x))
            .OrderBy(x => x.Number)
            .ToArray();
        return new NeedsDecisionInfo
        {
            RunId = checkpoint.RunId,
            AdventureId = checkpoint.AdventureId,
            ChoiceId = checkpoint.ChoiceId,
            Title = checkpoint.ChoiceTitle,
            Options = options,
            Reason = checkpoint.AbortMessage,
            AccountRole = checkpoint.Role.ToString()
        };
    }
}
