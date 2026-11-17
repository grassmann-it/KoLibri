namespace KolArbitrage.Core.Adventure;

/// <summary>Small typed condition set for bounded adventure runs.</summary>
public enum AdventureConditionKind
{
    ItemCount,
    Stat,
    HitPoints,
    ManaPoints,
    ChoiceCount,
    TurnsRemaining,
    AdventuresRemaining
}

public enum AdventureConditionOperator
{
    AtLeast,
    AtMost,
    Equals
}

public sealed record AdventureCondition
{
    public AdventureConditionKind Kind { get; init; }
    public AdventureConditionOperator Operator { get; init; } = AdventureConditionOperator.AtLeast;
    public string Key { get; init; } = string.Empty;
    public int Target { get; init; }

    public AdventureCondition Normalize()
        => this with { Key = Key?.Trim() ?? string.Empty, Target = Math.Max(0, Target) };
}

/// <summary>Read-only status needed to evaluate conditions; adapters populate it from KoL.</summary>
public sealed record AdventureConditionSnapshot
{
    public IReadOnlyDictionary<string, int> Items { get; init; }
        = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, int> Stats { get; init; }
        = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    public int HitPoints { get; init; }
    public int ManaPoints { get; init; }
    public int ChoicesMade { get; init; }
    public int TurnsRemaining { get; init; }
    public int AdventuresRemaining { get; init; }
    public bool IsStale { get; init; }
    public string? UnavailableReason { get; init; }
}

public sealed record AdventureConditionResult
{
    public AdventureCondition Condition { get; init; } = new();
    public int Actual { get; init; }
    public bool IsSatisfied { get; init; }
    public bool IsBlocked { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public static class AdventureConditionEvaluator
{
    public static IReadOnlyList<AdventureConditionResult> EvaluateAll(
        IEnumerable<AdventureCondition> conditions,
        AdventureConditionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(snapshot);
        return conditions.Select(condition => Evaluate(condition, snapshot)).ToArray();
    }

    public static AdventureConditionResult Evaluate(
        AdventureCondition condition,
        AdventureConditionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(snapshot);
        var normalized = condition.Normalize();
        var actual = ReadActual(normalized, snapshot);
        var satisfied = normalized.Operator switch
        {
            AdventureConditionOperator.AtLeast => actual >= normalized.Target,
            AdventureConditionOperator.AtMost => actual <= normalized.Target,
            AdventureConditionOperator.Equals => actual == normalized.Target,
            _ => false
        };
        var label = normalized.Kind == AdventureConditionKind.ItemCount || normalized.Kind == AdventureConditionKind.Stat
            ? $"{normalized.Kind} '{normalized.Key}'"
            : normalized.Kind.ToString();
        return new AdventureConditionResult
        {
            Condition = normalized,
            Actual = actual,
            IsSatisfied = satisfied,
            Reason = $"{label}: actual {actual}, target {normalized.Operator} {normalized.Target}."
        };
    }

    public static bool IsSatisfied(IEnumerable<AdventureCondition> conditions, AdventureConditionSnapshot snapshot)
        => EvaluateAll(conditions, snapshot).All(result => result.IsSatisfied);

    private static int ReadActual(AdventureCondition condition, AdventureConditionSnapshot snapshot)
        => condition.Kind switch
        {
            AdventureConditionKind.ItemCount => Read(snapshot.Items, condition.Key),
            AdventureConditionKind.Stat => Read(snapshot.Stats, condition.Key),
            AdventureConditionKind.HitPoints => Math.Max(0, snapshot.HitPoints),
            AdventureConditionKind.ManaPoints => Math.Max(0, snapshot.ManaPoints),
            AdventureConditionKind.ChoiceCount => Math.Max(0, snapshot.ChoicesMade),
            AdventureConditionKind.TurnsRemaining => Math.Max(0, snapshot.TurnsRemaining),
            AdventureConditionKind.AdventuresRemaining => Math.Max(0, snapshot.AdventuresRemaining),
            _ => 0
        };

    private static int Read(IReadOnlyDictionary<string, int> values, string key)
        => string.IsNullOrWhiteSpace(key) || !values.TryGetValue(key, out var value) ? 0 : Math.Max(0, value);
}
