using System.Globalization;

namespace KoLibri.Desktop;

/// <summary>Keep one effect up between turns: below <see cref="MinTurns"/> turns, run <see cref="Step"/> once.</summary>
public sealed record UpkeepRule(int EffectId, string EffectName, int MinTurns, RecoveryStep Step);

/// <summary>
/// What an auto-combat batch does between and around fights, on KoLibri's building blocks:
/// heal below an HP percentage (<see cref="RecoveryPolicies.Simple"/>), keep one buff up
/// (<see cref="RecoveryPolicies.Upkeep"/>), stop on a monster or a Turtle Tamer encounter (opt-in),
/// and answer choices from the curated defaults (<see cref="ChoiceDefaults.Resolver"/>, on by default).
/// Turtle Tamer encounters are clicked through unless <see cref="StopOnTurtle"/> is set.
/// </summary>
public sealed record FarmOptions
{
    /// <summary>Heal when HP is below this percentage before a new turn; 0 turns healing off.</summary>
    public int HealBelowHpPercent { get; init; }

    /// <summary>Heal steps in priority order (rest, item, skill). An item step counts only when the item is owned.</summary>
    public IReadOnlyList<RecoveryStep> HealSteps { get; init; } = Array.Empty<RecoveryStep>();

    public UpkeepRule? Upkeep { get; init; }

    /// <summary>Stop after the turn that showed this monster; null = off.</summary>
    public string? StopOnMonster { get; init; }

    /// <summary>Opt-in: stop on a Turtle Tamer encounter (the fight is handed back, a turtle choice is not answered).</summary>
    public bool StopOnTurtle { get; init; }

    /// <summary>Answer choices from KoLibri's curated table (choices.json). On by default.</summary>
    public bool UseChoiceDefaults { get; init; } = true;

    /// <summary>Choice id → option, wins over the curated defaults and the choice policy file.</summary>
    public IReadOnlyDictionary<int, int> ChoiceOverrides { get; init; } = new Dictionary<int, int>();

    /// <summary>The defaults of the configurator: no healing, no upkeep, no stops, choice defaults on.</summary>
    public static FarmOptions Default { get; } = new();

    /// <summary>The recovery policy for <see cref="RunRequest.Recovery"/>: healing first, then the upkeep.</summary>
    public IRecoveryPolicy BuildRecovery()
    {
        var policies = new List<IRecoveryPolicy>();
        if (HealBelowHpPercent > 0 && HealSteps.Count > 0)
            policies.Add(RecoveryPolicies.Simple(HealBelowHpPercent, 0, HealSteps, Array.Empty<RecoveryStep>()));
        if (Upkeep is { } upkeep)
            policies.Add(RecoveryPolicies.Upkeep(upkeep.EffectId, upkeep.MinTurns, upkeep.Step));
        return policies.Count switch
        {
            0 => RecoveryPolicies.None,
            1 => policies[0],
            _ => RecoveryPolicies.Combine(policies.ToArray())
        };
    }

    /// <summary>The extra stop conditions (monster, turtle) for <see cref="RunRequest.StopWhen"/>.</summary>
    public IReadOnlyList<StopCondition> BuildStops()
    {
        var stops = new List<StopCondition>();
        if (!string.IsNullOrWhiteSpace(StopOnMonster)) stops.Add(StopCondition.MonsterSeen(StopOnMonster.Trim()));
        if (StopOnTurtle) stops.Add(StopCondition.TurtleEncounter());
        return stops;
    }

    /// <summary>The fight strategy: with <see cref="StopOnTurtle"/> a turtle monster hands the fight back.</summary>
    public ICombatStrategy WrapCombat(ICombatStrategy inner)
        => StopOnTurtle ? CombatStrategies.StopOnTurtle(inner) : inner;

    /// <summary>
    /// The choice resolver of the batch. With choice defaults: overrides (this form, then the choice policy file)
    /// before the curated table; a choice the table cannot answer goes to the policy file's resolver (auto-safe
    /// mode), except a turtle choice when the operator asked to stop on turtles. Without choice defaults the
    /// policy file decides alone, as before 0.5.
    /// </summary>
    public IChoiceResolver BuildChoiceResolver(IReadOnlyDictionary<int, int> policyTable, Func<IChoiceResolver> policyResolver)
    {
        ArgumentNullException.ThrowIfNull(policyTable);
        ArgumentNullException.ThrowIfNull(policyResolver);
        if (!UseChoiceDefaults) return policyResolver();
        var overrides = new Dictionary<int, int>(policyTable);
        foreach (var (id, option) in ChoiceOverrides) overrides[id] = option;
        return new FarmChoiceResolver(ChoiceDefaults.Resolver(overrides, StopOnTurtle), policyResolver(), StopOnTurtle);
    }

    /// <summary>One line for the preview: what happens between turns.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>
            {
                HealBelowHpPercent > 0 && HealSteps.Count > 0
                    ? $"heal below {HealBelowHpPercent} % HP ({string.Join(" → ", HealSteps.Select(FarmStepText.Describe))})"
                    : "no healing"
            };
            if (Upkeep is { } upkeep)
                parts.Add($"keep {upkeep.EffectName} ≥ {upkeep.MinTurns} turns ({FarmStepText.Describe(upkeep.Step)})");
            if (!string.IsNullOrWhiteSpace(StopOnMonster)) parts.Add($"stop on {StopOnMonster.Trim()}");
            parts.Add(StopOnTurtle ? "stop on turtles" : "turtles clicked through");
            parts.Add(UseChoiceDefaults
                ? ChoiceOverrides.Count == 0 ? "choice defaults on" : $"choice defaults on, {ChoiceOverrides.Count} override(s)"
                : "choice defaults off");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>Curated defaults first; when they ask, the policy file's resolver — but never for a turtle choice the operator wants to stop on.</summary>
internal sealed class FarmChoiceResolver(IChoiceResolver defaults, IChoiceResolver fallback, bool stopOnTurtle) : IChoiceResolver
{
    public ChoiceDecision Resolve(ChoiceContext context)
    {
        var decision = defaults.Resolve(context);
        if (decision.Option is not null) return decision;
        if (stopOnTurtle && IsTurtle(context)) return decision;
        var second = fallback.Resolve(context);
        return second.Option is not null ? second : decision;
    }

    private static bool IsTurtle(ChoiceContext context)
        => Encounters.IsTurtleEncounter(new ChoiceEncounter(
               new Page(new Uri("https://www.kingdomofloathing.com/choice.php"), 200, string.Empty, PageKind.Choice, DateTimeOffset.MinValue),
               context.Location, context.Title, context.ChoiceId, context.Options))
           || (ChoiceDefaults.Embedded.TryGet(context.ChoiceId, out var entry) && entry.Turtle);
}

/// <summary>
/// Parses the step text of the farm form, the same syntax as the CLI's <c>--recover-hp</c>:
/// <c>rest</c>, <c>item:ID|NAME</c>, <c>skill:ID|NAME</c>, separated by commas. Names resolve through the catalog.
/// </summary>
public static class FarmStepText
{
    public static bool TryParseSteps(
        string? text, Func<string, int?> itemByName, Func<string, int?> skillByName,
        out IReadOnlyList<RecoveryStep> steps, out string? error)
    {
        var list = new List<RecoveryStep>();
        steps = list;
        error = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        foreach (var raw in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParseStep(raw, itemByName, skillByName, out var step, out error)) return false;
            list.Add(step!);
        }
        return true;
    }

    public static bool TryParseStep(
        string? text, Func<string, int?> itemByName, Func<string, int?> skillByName,
        out RecoveryStep? step, out string? error)
    {
        step = null;
        error = null;
        var raw = text?.Trim() ?? string.Empty;
        if (raw.Equals("rest", StringComparison.OrdinalIgnoreCase))
        {
            step = RecoveryStep.Rest;
            return true;
        }
        var colon = raw.IndexOf(':');
        var kind = colon < 0 ? raw : raw[..colon].Trim();
        var value = colon < 0 ? string.Empty : raw[(colon + 1)..].Trim().Trim('"');
        var isItem = kind.Equals("item", StringComparison.OrdinalIgnoreCase);
        var isSkill = kind.Equals("skill", StringComparison.OrdinalIgnoreCase);
        if (!isItem && !isSkill || value.Length == 0)
        {
            error = $"'{raw}' is not a step. Use rest, item:ID or NAME, skill:ID or NAME.";
            return false;
        }
        var id = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
            ? n
            : isItem ? itemByName(value) : skillByName(value);
        if (id is not { } resolved)
        {
            error = $"Unknown {(isItem ? "item" : "skill")} '{value}'.";
            return false;
        }
        step = isItem ? RecoveryStep.UseItem(resolved) : RecoveryStep.Cast(resolved);
        return true;
    }

    /// <summary>"1060:2, 123:1" → choice id → option.</summary>
    public static bool TryParseOverrides(string? text, out IReadOnlyDictionary<int, int> overrides, out string? error)
    {
        var map = new Dictionary<int, int>();
        overrides = map;
        error = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        foreach (var raw in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = raw.Split(':');
            if (parts.Length != 2
                || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var choice) || choice <= 0
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var option) || option <= 0)
            {
                error = $"'{raw}' is not a choice override. Use CHOICE:OPTION, e.g. 1060:2.";
                return false;
            }
            map[choice] = option;
        }
        return true;
    }

    public static string Describe(RecoveryStep step) => step.Kind switch
    {
        RecoveryStepKind.Rest => "rest",
        RecoveryStepKind.UseItem => $"item {step.Id}",
        RecoveryStepKind.Cast => $"skill {step.Id}",
        _ => step.Kind.ToString().ToLowerInvariant()
    };
}
