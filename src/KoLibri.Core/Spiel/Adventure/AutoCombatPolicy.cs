using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace KoLibri.Core.Spiel;

/// <summary>Actions a deterministic combat policy may approve.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AutoCombatActionKind
{
    Attack,
    Skill,
    Item,
    Run,
    Manual,
    Abort
}

/// <summary>Compatibility spelling for callers that call the discriminator a type.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AutoCombatActionType
{
    Attack,
    Skill,
    Item,
    Run,
    Manual,
    Abort
}

/// <summary>One action emitted by an auto-combat policy.</summary>
public sealed record AutoCombatAction
{
    public AutoCombatAction() { }

    public AutoCombatAction(AutoCombatActionKind kind, string? id = null, int quantity = 1, string? reason = null)
    {
        Kind = kind;
        Id = id;
        Quantity = quantity;
        Reason = reason;
    }

    public AutoCombatActionKind Kind { get; init; }
    [JsonIgnore]
    public AutoCombatActionKind ActionKind { get => Kind; init => Kind = value; }
    public AutoCombatActionType Type
    {
        get => (AutoCombatActionType)Kind;
        init => Kind = (AutoCombatActionKind)value;
    }

    /// <summary>Skill, item, or other optional action identifier.</summary>
    public string? Id { get; init; }
    [JsonIgnore]
    public string? Name { get => Id; init => Id = value; }
    [JsonIgnore]
    public string? ActionId { get => Id; init => Id = value; }
    [JsonIgnore]
    public string? SkillId { get => Kind == AutoCombatActionKind.Skill ? Id : null; init => Id = value; }
    [JsonIgnore]
    public string? ItemId { get => Kind == AutoCombatActionKind.Item ? Id : null; init => Id = value; }
    public int Quantity { get; init; } = 1;
    public string? Reason { get; init; }

    public static AutoCombatAction Attack(string? reason = null)
        => new() { Kind = AutoCombatActionKind.Attack, Reason = reason };

    public static AutoCombatAction Skill(string id, string? reason = null)
        => new() { Kind = AutoCombatActionKind.Skill, Id = RequireId(id, nameof(id)), Reason = reason };

    public static AutoCombatAction Item(string id, string? reason = null, int quantity = 1)
        => new()
        {
            Kind = AutoCombatActionKind.Item,
            Id = RequireId(id, nameof(id)),
            Quantity = quantity,
            Reason = reason
        };

    public static AutoCombatAction Run(string? reason = null)
        => new() { Kind = AutoCombatActionKind.Run, Reason = reason };

    public static AutoCombatAction Manual(string? reason = null)
        => new() { Kind = AutoCombatActionKind.Manual, Reason = reason };

    public static AutoCombatAction Abort(string? reason = null)
        => new() { Kind = AutoCombatActionKind.Abort, Reason = reason };

    private static string RequireId(string value, string parameter)
        => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("An action identifier is required.", parameter) : value.Trim();
}

/// <summary>Predicates available in the small, side-effect-free combat DSL.</summary>
public sealed record AutoCombatPredicate
{
    public int? Round { get; init; }
    [JsonIgnore] public int? ROUND { get => Round; init => Round = value; }
    public int? MinRound { get; init; }
    public int? MaxRound { get; init; }
    [JsonIgnore]
    public int? RoundAtLeast { get => MinRound; init => MinRound = value; }
    [JsonIgnore]
    public int? RoundAtMost { get => MaxRound; init => MaxRound = value; }

    public int? Hp { get; init; }
    [JsonIgnore] public int? HP { get => Hp; init => Hp = value; }
    public int? MinHp { get; init; }
    public int? MaxHp { get; init; }
    [JsonIgnore]
    public int? HpAtLeast { get => MinHp; init => MinHp = value; }
    [JsonIgnore]
    public int? HpAtMost { get => MaxHp; init => MaxHp = value; }
    public decimal? HpPercent { get; init; }
    [JsonIgnore] public decimal? HPPercent { get => HpPercent; init => HpPercent = value; }
    public decimal? MinHpPercent { get; init; }
    public decimal? MaxHpPercent { get; init; }
    [JsonIgnore]
    public decimal? HpPercentAtLeast { get => MinHpPercent; init => MinHpPercent = value; }
    [JsonIgnore]
    public decimal? HpPercentAtMost { get => MaxHpPercent; init => MaxHpPercent = value; }

    public int? Mp { get; init; }
    [JsonIgnore] public int? MP { get => Mp; init => Mp = value; }
    public int? MinMp { get; init; }
    public int? MaxMp { get; init; }
    [JsonIgnore]
    public int? MpAtLeast { get => MinMp; init => MinMp = value; }
    [JsonIgnore]
    public int? MpAtMost { get => MaxMp; init => MaxMp = value; }
    public decimal? MpPercent { get; init; }
    [JsonIgnore] public decimal? MPPercent { get => MpPercent; init => MpPercent = value; }
    public decimal? MinMpPercent { get; init; }
    public decimal? MaxMpPercent { get; init; }
    [JsonIgnore]
    public decimal? MpPercentAtLeast { get => MinMpPercent; init => MinMpPercent = value; }
    [JsonIgnore]
    public decimal? MpPercentAtMost { get => MaxMpPercent; init => MaxMpPercent = value; }

    public string? Monster { get; init; }
    [JsonIgnore]
    public string? MonsterId { get => Monster; init => Monster = value; }
    [JsonIgnore]
    public string? MonsterName { get => Monster; init => Monster = value; }
    public IReadOnlyList<string>? Monsters { get; init; }

    /// <summary>Effects that must all be active.</summary>
    public IReadOnlyList<string>? Effects { get; init; }
    public IReadOnlyList<string>? HasEffects { get; init; }
    [JsonIgnore]
    public IReadOnlyList<string>? EffectsPresent { get => HasEffects; init => HasEffects = value; }
    /// <summary>Effects that must all be absent.</summary>
    public IReadOnlyList<string>? MissingEffects { get; init; }
    [JsonIgnore]
    public IReadOnlyList<string>? EffectsAbsent { get => MissingEffects; init => MissingEffects = value; }
    /// <summary>Effects where at least one must be active.</summary>
    public IReadOnlyList<string>? AnyEffects { get; init; }

    public static AutoCombatPredicate Any { get; } = new();
}

/// <summary>Read-only snapshot consumed by <see cref="AutoCombatPolicyEvaluator"/>.</summary>
public record AutoCombatContext
{
    public int Round { get; init; }
    public int Hp { get; init; }
    public int MaxHp { get; init; }
    public int Mp { get; init; }
    public int MaxMp { get; init; }
    public string? Monster { get; init; }
    [JsonIgnore]
    public string? MonsterId { get => Monster; init => Monster = value; }
    [JsonIgnore]
    public string? MonsterName { get => Monster; init => Monster = value; }
    public IReadOnlyCollection<string> Effects { get; init; } = Array.Empty<string>();

    // Usage counters are part of the input snapshot so evaluation remains pure.
    public int ActionsTaken { get; init; }
    public int SkillUses { get; init; }
    public int ItemUses { get; init; }
    public int RunAttempts { get; init; }
    public int ManualDecisions { get; init; }
    public IReadOnlyDictionary<string, int> RuleUses { get; init; }
        = new Dictionary<string, int>(StringComparer.Ordinal);

    // Friendly aliases used by state adapters. They are ignored during JSON
    // serialization to keep one canonical wire representation.
    [JsonIgnore] public int CurrentHp { get => Hp; init => Hp = value; }
    [JsonIgnore] public int PlayerHp { get => Hp; init => Hp = value; }
    [JsonIgnore] public int PlayerMaxHp { get => MaxHp; init => MaxHp = value; }
    [JsonIgnore] public int CurrentMp { get => Mp; init => Mp = value; }
    [JsonIgnore] public int PlayerMp { get => Mp; init => Mp = value; }
    [JsonIgnore] public int PlayerMaxMp { get => MaxMp; init => MaxMp = value; }
    [JsonIgnore] public string? EncounterMonster { get => Monster; init => Monster = value; }
    [JsonIgnore] public IReadOnlyCollection<string> ActiveEffects { get => Effects; init => Effects = value ?? Array.Empty<string>(); }

    public AutoCombatContext Normalize()
        => this with
        {
            Round = Math.Max(0, Round),
            Hp = Math.Max(0, Hp),
            MaxHp = Math.Max(0, MaxHp),
            Mp = Math.Max(0, Mp),
            MaxMp = Math.Max(0, MaxMp),
            Effects = (Effects ?? Array.Empty<string>())
                .Where(static x => !string.IsNullOrWhiteSpace(x))
                .Select(static x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ActionsTaken = Math.Max(0, ActionsTaken),
            SkillUses = Math.Max(0, SkillUses),
            ItemUses = Math.Max(0, ItemUses),
            RunAttempts = Math.Max(0, RunAttempts),
            ManualDecisions = Math.Max(0, ManualDecisions),
            RuleUses = RuleUses ?? new Dictionary<string, int>(StringComparer.Ordinal)
        };

    public bool HasEffect(string effect)
        => !string.IsNullOrWhiteSpace(effect) && Effects.Any(x =>
            string.Equals(x, effect.Trim(), StringComparison.OrdinalIgnoreCase));

    public decimal HpRatio => MaxHp <= 0 ? 0m : Math.Clamp((decimal)Hp / MaxHp, 0m, 1m);
    public decimal MpRatio => MaxMp <= 0 ? 0m : Math.Clamp((decimal)Mp / MaxMp, 0m, 1m);
}

/// <summary>Alias retained for state-reader integrations.</summary>
public record AutoCombatObservation : AutoCombatContext;

/// <summary>Alias retained for execution/checkpoint integrations.</summary>
public record AutoCombatState : AutoCombatContext;

/// <summary>Counters supplied to an evaluation when they are stored separately from the observation.</summary>
public sealed record AutoCombatUsage
{
    public int ActionsTaken { get; init; }
    public int SkillUses { get; init; }
    public int ItemUses { get; init; }
    public int RunAttempts { get; init; }
    public int ManualDecisions { get; init; }
    public IReadOnlyDictionary<string, int> RuleUses { get; init; }
        = new Dictionary<string, int>(StringComparer.Ordinal);
}

/// <summary>Hard ceilings enforced before a policy can emit an action.</summary>
public sealed record AutoCombatBudget
{
    public int MaxRounds { get; init; } = 100;
    [JsonIgnore] public int MaxCombatRounds { get => MaxRounds; init => MaxRounds = value; }
    [JsonIgnore] public int MaxFightRounds { get => MaxRounds; init => MaxRounds = value; }
    public int MaxActions { get; init; } = 100;
    public int MaxActionCount { get => MaxActions; init => MaxActions = value; }
    public int MaxSkillUses { get; init; } = int.MaxValue;
    public int MaxItemUses { get; init; } = int.MaxValue;
    public int MaxRunAttempts { get; init; } = 1;
    public int MaxManualDecisions { get; init; } = int.MaxValue;

    public void Validate()
    {
        if (MaxRounds < 0) throw new ArgumentOutOfRangeException(nameof(MaxRounds));
        if (MaxActions < 0) throw new ArgumentOutOfRangeException(nameof(MaxActions));
        if (MaxSkillUses < 0) throw new ArgumentOutOfRangeException(nameof(MaxSkillUses));
        if (MaxItemUses < 0) throw new ArgumentOutOfRangeException(nameof(MaxItemUses));
        if (MaxRunAttempts < 0) throw new ArgumentOutOfRangeException(nameof(MaxRunAttempts));
        if (MaxManualDecisions < 0) throw new ArgumentOutOfRangeException(nameof(MaxManualDecisions));
    }
}

/// <summary>Ordered rule in the combat DSL.</summary>
public sealed record AutoCombatRule
{
    public string? Id { get; init; }
    [JsonIgnore]
    public string? Name { get => Id; init => Id = value; }
    public AutoCombatPredicate When { get; init; } = new();
    [JsonIgnore]
    public AutoCombatPredicate Predicate { get => When; init => When = value ?? new(); }
    public AutoCombatAction Action { get; init; } = AutoCombatAction.Manual("Rule did not specify an action.");
    [JsonIgnore]
    public AutoCombatAction Then { get => Action; init => Action = value ?? AutoCombatAction.Manual(); }
    public bool Enabled { get; init; } = true;
    public int? MaxUses { get; init; }

    public AutoCombatRule() { }

    public AutoCombatRule(AutoCombatPredicate when, AutoCombatAction action, string? id = null)
    {
        When = when ?? new();
        Action = action ?? throw new ArgumentNullException(nameof(action));
        Id = id;
    }

    public AutoCombatRule(string id, AutoCombatPredicate when, AutoCombatAction action)
        : this(when, action, id) { }
}

/// <summary>Trace entry explaining why one ordered rule did or did not match.</summary>
public sealed record AutoCombatRuleEvaluation
{
    public int Index { get; init; }
    public string? RuleId { get; init; }
    public bool Enabled { get; init; }
    public bool Matched { get; init; }
    public string Explanation { get; init; } = string.Empty;
    public string Reason { get => Explanation; init => Explanation = value; }
}

/// <summary>Stable machine-readable reason for a decision.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AutoCombatDecisionStatus
{
    Action,
    Manual,
    Aborted
}

/// <summary>One pure, explainable result from evaluating an auto-combat policy.</summary>
public sealed record AutoCombatDecision
{
    public AutoCombatAction Action { get; init; } = AutoCombatAction.Manual();
    public AutoCombatActionKind ActionKind => Action.Kind;
    public AutoCombatDecisionStatus Status { get; init; } = AutoCombatDecisionStatus.Manual;
    public string? RuleId { get; init; }
    public int? RuleIndex { get; init; }
    public string Reason { get; init; } = string.Empty;
    [JsonIgnore]
    public string Explanation { get => Reason; init => Reason = value; }
    public IReadOnlyList<AutoCombatRuleEvaluation> EvaluatedRules { get; init; }
        = Array.Empty<AutoCombatRuleEvaluation>();
    [JsonIgnore]
    public IReadOnlyList<AutoCombatRuleEvaluation> RuleEvaluations { get => EvaluatedRules; init => EvaluatedRules = value; }
    public bool BudgetExceeded { get; init; }
    public bool IsManual => Status == AutoCombatDecisionStatus.Manual;
    public bool IsAborted => Status == AutoCombatDecisionStatus.Aborted;
    public bool IsAction => Status == AutoCombatDecisionStatus.Action;
    public bool ShouldStop => IsManual || IsAborted || Action.Kind is AutoCombatActionKind.Run or AutoCombatActionKind.Abort;
    public string? AbortReason => IsAborted ? Reason : null;

    public string ToJson(JsonSerializerOptions? options = null)
        => JsonSerializer.Serialize(this, options ?? AutoCombatJson.Options);

    public static AutoCombatDecision FromJson(string json, JsonSerializerOptions? options = null)
        => JsonSerializer.Deserialize<AutoCombatDecision>(json, options ?? AutoCombatJson.Options)
            ?? throw new FormatException("Decision JSON was empty.");
}

/// <summary>JSON options shared by policy, action, and decision serialization.</summary>
public static class AutoCombatJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}

/// <summary>An ordered, deterministic policy. It never performs I/O or loops a fight.</summary>
public sealed record AutoCombatPolicy
{
    public IReadOnlyList<AutoCombatRule> Rules { get; init; } = Array.Empty<AutoCombatRule>();
    public AutoCombatBudget Budget { get; init; } = new();
    public AutoCombatAction? DefaultAction { get; init; }
    [JsonIgnore]
    public AutoCombatAction? FallbackAction { get => DefaultAction; init => DefaultAction = value; }

    public AutoCombatDecision Evaluate(AutoCombatContext context, AutoCombatUsage? usage = null)
        => new AutoCombatPolicyEvaluator().Evaluate(this, context, usage);

    public AutoCombatDecision Decide(AutoCombatContext context, AutoCombatUsage? usage = null)
        => Evaluate(context, usage);

    public void Validate()
    {
        (Budget ?? new()).Validate();
        if (Rules is null) throw new ArgumentNullException(nameof(Rules));
        for (var i = 0; i < Rules.Count; i++)
        {
            var rule = Rules[i] ?? throw new ArgumentException($"Rule {i} is null.", nameof(Rules));
            if (rule.MaxUses is < 0) throw new ArgumentOutOfRangeException(nameof(Rules), $"Rule {i} has a negative MaxUses.");
            if (rule.Action is null) throw new ArgumentException($"Rule {i} has no action.", nameof(Rules));
            ValidateAction(rule.Action, i);
        }
        if (DefaultAction is not null) ValidateAction(DefaultAction, -1);
    }

    public string ToJson(JsonSerializerOptions? options = null)
        => JsonSerializer.Serialize(this, options ?? AutoCombatJson.Options);

    public static AutoCombatPolicy FromJson(string json, JsonSerializerOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("Policy JSON is empty.", nameof(json));
        try
        {
            // Parse manually first so common DSL aliases (when/then, type/id,
            // nested comparisons) work in addition to direct record JSON.
            return AutoCombatPolicyDsl.ParseJson(json);
        }
        catch (JsonException)
        {
            return JsonSerializer.Deserialize<AutoCombatPolicy>(json, options ?? AutoCombatJson.Options)
                ?? throw new FormatException("Policy JSON was empty.");
        }
    }

    public static AutoCombatPolicy Parse(string text)
        => AutoCombatPolicyDsl.Parse(text);

    public static AutoCombatPolicy FromDsl(string text) => Parse(text);

    private static void ValidateAction(AutoCombatAction action, int index)
    {
        if (action.Kind is AutoCombatActionKind.Skill or AutoCombatActionKind.Item
            && string.IsNullOrWhiteSpace(action.Id))
            throw new ArgumentException($"Action {(index < 0 ? "default" : index.ToString(CultureInfo.InvariantCulture))} requires an id.");
        if (action.Quantity < 1) throw new ArgumentOutOfRangeException(nameof(action.Quantity));
    }
}

/// <summary>Pure evaluator for <see cref="AutoCombatPolicy"/>.</summary>
public sealed class AutoCombatPolicyEvaluator
{
    public AutoCombatDecision Evaluate(
        AutoCombatPolicy policy,
        AutoCombatContext context,
        AutoCombatUsage? usage = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(context);
        policy.Validate();
        var snapshot = MergeUsage(context.Normalize(), usage);
        var budget = policy.Budget ?? new AutoCombatBudget();

        if (snapshot.Round <= 0)
            return Abort("Round must be greater than zero.", budgetExceeded: false);
        if (snapshot.Round > budget.MaxRounds)
            return Abort($"Round budget exceeded: round {snapshot.Round} is greater than max {budget.MaxRounds}.", budgetExceeded: true);
        if (snapshot.ActionsTaken >= budget.MaxActions)
            return Abort($"Action budget exhausted: {snapshot.ActionsTaken} of {budget.MaxActions} actions used.", budgetExceeded: true);
        var traces = new List<AutoCombatRuleEvaluation>();
        for (var index = 0; index < policy.Rules.Count; index++)
        {
            var rule = policy.Rules[index];
            if (!rule.Enabled)
            {
                traces.Add(new AutoCombatRuleEvaluation
                {
                    Index = index,
                    RuleId = RuleId(rule, index),
                    Enabled = false,
                    Matched = false,
                    Explanation = "Rule is disabled."
                });
                continue;
            }

            if (rule.MaxUses is { } maxUses)
            {
                var used = snapshot.RuleUses.TryGetValue(RuleId(rule, index), out var count) ? count : 0;
                if (used >= maxUses)
                {
                    traces.Add(new AutoCombatRuleEvaluation
                    {
                        Index = index,
                        RuleId = RuleId(rule, index),
                        Enabled = true,
                        Matched = false,
                        Explanation = $"Rule use budget exhausted: {used} of {maxUses}."
                    });
                    continue;
                }
            }

            var (matched, explanation) = Match(rule.When ?? new AutoCombatPredicate(), snapshot);
            traces.Add(new AutoCombatRuleEvaluation
            {
                Index = index,
                RuleId = RuleId(rule, index),
                Enabled = true,
                Matched = matched,
                Explanation = explanation
            });
            if (!matched) continue;

            var action = rule.Action ?? AutoCombatAction.Manual("Rule has no action.");
            if (!FitsActionBudget(action, snapshot, budget, out var budgetReason))
                return Abort(budgetReason!, true, traces, RuleId(rule, index), index);
            return DecisionFor(action, AutoCombatDecisionStatusFor(action),
                $"Rule '{RuleId(rule, index)}' matched. {explanation}", traces,
                RuleId(rule, index), index);
        }

        var fallback = policy.DefaultAction;
        if (fallback is null)
        {
            var manual = AutoCombatAction.Manual("No ordered rule matched; manual review is required.");
            if (!FitsActionBudget(manual, snapshot, budget, out var manualBudgetReason))
                return Abort(manualBudgetReason!, true, traces);
            return DecisionFor(
                manual,
                AutoCombatDecisionStatus.Manual,
                "No ordered rule matched; manual review is required.", traces, null, null);
        }
        if (!FitsActionBudget(fallback, snapshot, budget, out var fallbackReason))
            return Abort(fallbackReason!, true, traces);
        return DecisionFor(fallback, AutoCombatDecisionStatusFor(fallback),
            "No ordered rule matched; the configured default action was selected.", traces, null, null);
    }

    public AutoCombatDecision Decide(AutoCombatPolicy policy, AutoCombatContext context, AutoCombatUsage? usage = null)
        => Evaluate(policy, context, usage);

    private static AutoCombatContext MergeUsage(AutoCombatContext context, AutoCombatUsage? usage)
    {
        if (usage is null) return context;
        return context with
        {
            ActionsTaken = usage.ActionsTaken,
            SkillUses = usage.SkillUses,
            ItemUses = usage.ItemUses,
            RunAttempts = usage.RunAttempts,
            ManualDecisions = usage.ManualDecisions,
            RuleUses = usage.RuleUses ?? new Dictionary<string, int>(StringComparer.Ordinal)
        };
    }

    private static bool FitsActionBudget(
        AutoCombatAction action,
        AutoCombatContext context,
        AutoCombatBudget budget,
        out string? reason)
    {
        if (action.Kind == AutoCombatActionKind.Skill && context.SkillUses + 1 > budget.MaxSkillUses)
        {
            reason = $"Skill budget would be exceeded: {context.SkillUses + 1} of {budget.MaxSkillUses} uses.";
            return false;
        }
        if (action.Kind == AutoCombatActionKind.Item && context.ItemUses + Math.Max(1, action.Quantity) > budget.MaxItemUses)
        {
            reason = $"Item budget would be exceeded: {context.ItemUses + Math.Max(1, action.Quantity)} of {budget.MaxItemUses} uses.";
            return false;
        }
        if (action.Kind == AutoCombatActionKind.Run && context.RunAttempts + 1 > budget.MaxRunAttempts)
        {
            reason = $"Run budget would be exceeded: {context.RunAttempts + 1} of {budget.MaxRunAttempts} attempts.";
            return false;
        }
        if (action.Kind == AutoCombatActionKind.Manual && context.ManualDecisions + 1 > budget.MaxManualDecisions)
        {
            reason = $"Manual-decision budget would be exceeded: {context.ManualDecisions + 1} of {budget.MaxManualDecisions}.";
            return false;
        }
        reason = null;
        return true;
    }

    private static (bool Matched, string Explanation) Match(AutoCombatPredicate predicate, AutoCombatContext context)
    {
        var checks = new List<(bool Match, string Text)>();
        if (predicate.Round is { } round)
            checks.Add((context.Round == round, $"round {context.Round} {(context.Round == round ? "equals" : "does not equal")} {round}"));
        if (predicate.MinRound is { } minRound)
            checks.Add((context.Round >= minRound, $"round {context.Round} {(context.Round >= minRound ? ">=" : "<")} {minRound}"));
        if (predicate.MaxRound is { } maxRound)
            checks.Add((context.Round <= maxRound, $"round {context.Round} {(context.Round <= maxRound ? "<=" : ">")} {maxRound}"));

        if (predicate.Hp is { } hp)
            checks.Add((context.Hp == hp, $"hp {context.Hp} {(context.Hp == hp ? "equals" : "does not equal")} {hp}"));
        if (predicate.MinHp is { } minHp)
            checks.Add((context.Hp >= minHp, $"hp {context.Hp} {(context.Hp >= minHp ? ">=" : "<")} {minHp}"));
        if (predicate.MaxHp is { } maxHp)
            checks.Add((context.Hp <= maxHp, $"hp {context.Hp} {(context.Hp <= maxHp ? "<=" : ">")} {maxHp}"));
        if (predicate.HpPercent is { } hpPercent)
            checks.Add((context.HpRatio == NormalizePercent(hpPercent), $"hp% {FormatPercent(context.HpRatio)} {(context.HpRatio == NormalizePercent(hpPercent) ? "equals" : "does not equal")} {FormatPercent(NormalizePercent(hpPercent))}"));
        if (predicate.MinHpPercent is { } minHpPercent)
            checks.Add((context.HpRatio >= NormalizePercent(minHpPercent), $"hp% {FormatPercent(context.HpRatio)} {(context.HpRatio >= NormalizePercent(minHpPercent) ? ">=" : "<")} {FormatPercent(NormalizePercent(minHpPercent))}"));
        if (predicate.MaxHpPercent is { } maxHpPercent)
            checks.Add((context.HpRatio <= NormalizePercent(maxHpPercent), $"hp% {FormatPercent(context.HpRatio)} {(context.HpRatio <= NormalizePercent(maxHpPercent) ? "<=" : ">")} {FormatPercent(NormalizePercent(maxHpPercent))}"));

        if (predicate.Mp is { } mp)
            checks.Add((context.Mp == mp, $"mp {context.Mp} {(context.Mp == mp ? "equals" : "does not equal")} {mp}"));
        if (predicate.MinMp is { } minMp)
            checks.Add((context.Mp >= minMp, $"mp {context.Mp} {(context.Mp >= minMp ? ">=" : "<")} {minMp}"));
        if (predicate.MaxMp is { } maxMp)
            checks.Add((context.Mp <= maxMp, $"mp {context.Mp} {(context.Mp <= maxMp ? "<=" : ">")} {maxMp}"));
        if (predicate.MpPercent is { } mpPercent)
            checks.Add((context.MpRatio == NormalizePercent(mpPercent), $"mp% {FormatPercent(context.MpRatio)} {(context.MpRatio == NormalizePercent(mpPercent) ? "equals" : "does not equal")} {FormatPercent(NormalizePercent(mpPercent))}"));
        if (predicate.MinMpPercent is { } minMpPercent)
            checks.Add((context.MpRatio >= NormalizePercent(minMpPercent), $"mp% {FormatPercent(context.MpRatio)} {(context.MpRatio >= NormalizePercent(minMpPercent) ? ">=" : "<")} {FormatPercent(NormalizePercent(minMpPercent))}"));
        if (predicate.MaxMpPercent is { } maxMpPercent)
            checks.Add((context.MpRatio <= NormalizePercent(maxMpPercent), $"mp% {FormatPercent(context.MpRatio)} {(context.MpRatio <= NormalizePercent(maxMpPercent) ? "<=" : ">")} {FormatPercent(NormalizePercent(maxMpPercent))}"));

        if (!string.IsNullOrWhiteSpace(predicate.Monster))
        {
            var expected = predicate.Monster.Trim();
            var match = string.Equals(context.Monster, expected, StringComparison.OrdinalIgnoreCase);
            checks.Add((match, $"monster '{context.Monster ?? ""}' {(match ? "equals" : "does not equal")} '{expected}'"));
        }
        if (predicate.Monsters is { Count: > 0 })
        {
            var match = context.Monster is not null && predicate.Monsters.Any(x => string.Equals(x, context.Monster, StringComparison.OrdinalIgnoreCase));
            checks.Add((match, $"monster '{context.Monster ?? ""}' {(match ? "is" : "is not")} in [{string.Join(", ", predicate.Monsters)}]"));
        }
        AddAllEffects(checks, context, predicate.Effects ?? predicate.HasEffects, "has");
        AddAllEffects(checks, context, predicate.MissingEffects, "does not have");
        if (predicate.AnyEffects is { Count: > 0 })
        {
            var active = predicate.AnyEffects.Where(context.HasEffect).ToArray();
            checks.Add((active.Length > 0, active.Length > 0
                ? $"effects include one of [{string.Join(", ", predicate.AnyEffects)}] (active: {string.Join(", ", active)})"
                : $"effects include none of [{string.Join(", ", predicate.AnyEffects)}]"));
        }

        if (checks.Count == 0) return (true, "Predicate is empty and therefore matches.");
        var matched = checks.All(static x => x.Match);
        return (matched, string.Join("; ", checks.Select(static x => x.Text)) + ".");
    }

    private static void AddAllEffects(List<(bool Match, string Text)> checks, AutoCombatContext context, IReadOnlyList<string>? effects, string verb)
    {
        if (effects is not { Count: > 0 }) return;
        foreach (var effect in effects.Where(static x => !string.IsNullOrWhiteSpace(x)))
        {
            var has = context.HasEffect(effect);
            var pass = verb == "has" ? has : !has;
            checks.Add((pass, $"effects {(pass ? verb : verb == "has" ? "does not have" : "has")} '{effect.Trim()}'"));
        }
    }

    private static decimal NormalizePercent(decimal value)
        => value > 1m ? value / 100m : Math.Max(0m, value);

    private static string FormatPercent(decimal ratio)
        => (ratio * 100m).ToString("0.##", CultureInfo.InvariantCulture) + "%";

    private static AutoCombatDecisionStatus StatusFor(AutoCombatAction action)
        => DecisionStatusFor(action);

    private static AutoCombatDecisionStatus DecisionStatusFor(AutoCombatAction action)
        => action.Kind switch
        {
            AutoCombatActionKind.Manual => AutoCombatDecisionStatus.Manual,
            AutoCombatActionKind.Abort => AutoCombatDecisionStatus.Aborted,
            _ => AutoCombatDecisionStatus.Action
        };

    private static AutoCombatDecision DecisionFor(
        AutoCombatAction action,
        AutoCombatDecisionStatus status,
        string reason,
        IReadOnlyList<AutoCombatRuleEvaluation> traces,
        string? ruleId,
        int? ruleIndex)
        => new()
        {
            Action = action,
            Status = status,
            RuleId = ruleId,
            RuleIndex = ruleIndex,
            Reason = reason,
            EvaluatedRules = traces
        };

    private static AutoCombatDecision Abort(
        string reason,
        bool budgetExceeded,
        IReadOnlyList<AutoCombatRuleEvaluation>? traces = null,
        string? ruleId = null,
        int? ruleIndex = null)
        => new()
        {
            Action = AutoCombatAction.Abort(reason),
            Status = AutoCombatDecisionStatus.Aborted,
            RuleId = ruleId,
            RuleIndex = ruleIndex,
            Reason = reason,
            EvaluatedRules = traces ?? Array.Empty<AutoCombatRuleEvaluation>(),
            BudgetExceeded = budgetExceeded
        };

    private static string RuleId(AutoCombatRule rule, int index)
        => string.IsNullOrWhiteSpace(rule.Id) ? $"rule-{index + 1}" : rule.Id.Trim();

    private static AutoCombatDecisionStatus AutoCombatDecisionStatusFor(AutoCombatAction action)
        => DecisionStatusFor(action);
}

/// <summary>Parser for the compact text and JSON policy DSL.</summary>
public static class AutoCombatPolicyDsl
{
    public static AutoCombatPolicy Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Policy DSL is empty.", nameof(text));
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith("{") || trimmed.StartsWith("[")) return ParseJson(text);

        var rules = new List<AutoCombatRule>();
        var budget = new AutoCombatBudget();
        AutoCombatAction? defaultAction = null;
        var generatedId = 0;
        foreach (var original in text.Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = original.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (line.StartsWith("budget", StringComparison.OrdinalIgnoreCase))
            {
                budget = ParseBudgetLine(line, budget);
                continue;
            }
            if (line.StartsWith("default", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("else", StringComparison.OrdinalIgnoreCase))
            {
                var separator = line.IndexOfAny(new[] { ':', '=', '>' });
                defaultAction = ParseAction(separator >= 0 ? line[(separator + 1)..].Trim() : line[7..].Trim());
                continue;
            }

            var ruleText = line;
            string? id = null;
            var colon = line.IndexOf(':');
            if (line.StartsWith("rule", StringComparison.OrdinalIgnoreCase) && colon > 0)
            {
                var heading = line[..colon].Trim();
                id = heading[4..].Trim().Trim(' ', '\t', '"', '\'');
                ruleText = line[(colon + 1)..].Trim();
            }
            var whenIndex = IndexOfWord(ruleText, "when");
            if (whenIndex >= 0) ruleText = ruleText[(whenIndex + 4)..].Trim();
            else if (ruleText.StartsWith("if ", StringComparison.OrdinalIgnoreCase)) ruleText = ruleText[3..].Trim();

            var actionSeparator = FindActionSeparator(ruleText);
            if (actionSeparator < 0)
                throw new FormatException($"Rule is missing an action separator (use '->' or 'then'): {line}");
            var predicateText = ruleText[..actionSeparator].Trim();
            var actionText = ruleText[(actionSeparator + SeparatorLength(ruleText, actionSeparator))..].Trim();
            if (id is null) id = $"rule-{++generatedId}";
            rules.Add(new AutoCombatRule
            {
                Id = id,
                When = ParsePredicate(predicateText),
                Action = ParseAction(actionText)
            });
        }

        var policy = new AutoCombatPolicy { Rules = rules, Budget = budget, DefaultAction = defaultAction };
        policy.Validate();
        return policy;
    }

    public static AutoCombatPolicy ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            return new AutoCombatPolicy
            {
                Rules = root.EnumerateArray().Select((item, index) => ParseRule(item, index)).ToArray()
            };
        }
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Policy JSON must be an object or rule array.");
        var rulesElement = Find(root, "rules", "orderedRules");
        var rules = rulesElement.ValueKind == JsonValueKind.Array
            ? rulesElement.EnumerateArray().Select((item, index) => ParseRule(item, index)).ToArray()
            : Array.Empty<AutoCombatRule>();
        var budget = Find(root, "budget", "budgets");
        var policy = new AutoCombatPolicy
        {
            Rules = rules,
            Budget = budget.ValueKind == JsonValueKind.Object ? ParseBudget(budget) : new AutoCombatBudget(),
            DefaultAction = ParseOptionalAction(Find(root, "defaultAction", "fallbackAction", "default", "fallback"))
        };
        policy.Validate();
        return policy;
    }

    private static AutoCombatRule ParseRule(JsonElement element, int index)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new JsonException($"Rule {index} must be an object.");
        var predicate = Find(element, "when", "predicate", "condition");
        var actionElement = Find(element, "action", "then", "do");
        var id = StringValue(Find(element, "id", "name"));
        var enabled = BoolValue(Find(element, "enabled", "active")) ?? true;
        var maxUses = IntValue(Find(element, "maxUses", "maxRuleUses"));
        return new AutoCombatRule
        {
            Id = string.IsNullOrWhiteSpace(id) ? $"rule-{index + 1}" : id,
            When = predicate.ValueKind == JsonValueKind.Object ? ParsePredicate(predicate) : new AutoCombatPredicate(),
            Action = ParseActionElement(actionElement),
            Enabled = enabled,
            MaxUses = maxUses
        };
    }

    private static AutoCombatBudget ParseBudget(JsonElement element)
        => new()
        {
            MaxRounds = IntValue(Find(element, "maxRounds", "maxCombatRounds", "rounds")) ?? 100,
            MaxActions = IntValue(Find(element, "maxActions", "maxActionCount", "actions")) ?? 100,
            MaxSkillUses = IntValue(Find(element, "maxSkillUses", "skillUses")) ?? int.MaxValue,
            MaxItemUses = IntValue(Find(element, "maxItemUses", "itemUses")) ?? int.MaxValue,
            MaxRunAttempts = IntValue(Find(element, "maxRunAttempts", "runAttempts")) ?? 1,
            MaxManualDecisions = IntValue(Find(element, "maxManualDecisions", "manualDecisions")) ?? int.MaxValue
        };

    private static AutoCombatBudget ParseBudgetLine(string line, AutoCombatBudget previous)
    {
        var values = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(line, @"(?<key>[A-Za-z][A-Za-z0-9_]*)\s*(?:=|:)\s*(?<value>-?\d+)", RegexOptions.CultureInvariant))
            values[match.Groups["key"].Value] = int.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture);
        return previous with
        {
            MaxRounds = Get(values, previous.MaxRounds, "maxRounds", "rounds", "maxCombatRounds"),
            MaxActions = Get(values, previous.MaxActions, "maxActions", "actions", "maxActionCount"),
            MaxSkillUses = Get(values, previous.MaxSkillUses, "maxSkillUses", "skills"),
            MaxItemUses = Get(values, previous.MaxItemUses, "maxItemUses", "items"),
            MaxRunAttempts = Get(values, previous.MaxRunAttempts, "maxRunAttempts", "runs"),
            MaxManualDecisions = Get(values, previous.MaxManualDecisions, "maxManualDecisions", "manual")
        };
    }

    private static int Get(Dictionary<string, int> values, int fallback, params string[] keys)
        => keys.FirstOrDefault(values.ContainsKey) is { } key ? values[key] : fallback;

    private static AutoCombatAction ParseActionElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String) return ParseAction(element.GetString() ?? string.Empty);
        if (element.ValueKind != JsonValueKind.Object) throw new JsonException("Rule action must be a string or object.");
        var kindText = StringValue(Find(element, "kind", "type", "action", "name")) ?? "manual";
        var id = StringValue(Find(element, "id", "skill", "item", "actionId", "value"));
        var reason = StringValue(Find(element, "reason", "explanation"));
        var quantity = IntValue(Find(element, "quantity", "count")) ?? 1;
        return BuildAction(kindText, id, reason, quantity);
    }

    private static AutoCombatAction? ParseOptionalAction(JsonElement element)
        => element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : ParseActionElement(element);

    private static AutoCombatAction ParseAction(string actionText)
    {
        var text = actionText.Trim();
        if (text.Length == 0) throw new FormatException("Action is empty.");
        var match = Regex.Match(text, @"^(?<kind>[A-Za-z]+)(?:\s*\(\s*(?<arg>[^)]*)\s*\)|\s*[:=]?\s+(?<arg2>[^\s]+))?", RegexOptions.CultureInvariant);
        if (!match.Success) throw new FormatException($"Unknown action: {actionText}");
        var kind = match.Groups["kind"].Value;
        var id = match.Groups["arg"].Success ? match.Groups["arg"].Value : match.Groups["arg2"].Value;
        id = Unquote(id.Trim());
        return BuildAction(kind, string.IsNullOrWhiteSpace(id) ? null : id, null, 1);
    }

    private static AutoCombatAction BuildAction(string kindText, string? id, string? reason, int quantity)
    {
        var kind = kindText.Trim().ToLowerInvariant() switch
        {
            "attack" or "hit" => AutoCombatActionKind.Attack,
            "skill" or "cast" => AutoCombatActionKind.Skill,
            "item" or "use" => AutoCombatActionKind.Item,
            "run" or "flee" or "逃" => AutoCombatActionKind.Run,
            "manual" or "review" or "ask" => AutoCombatActionKind.Manual,
            "abort" or "stop" or "block" => AutoCombatActionKind.Abort,
            _ => throw new FormatException($"Unknown action kind '{kindText}'.")
        };
        return new AutoCombatAction { Kind = kind, Id = id, Quantity = quantity, Reason = reason };
    }

    private static AutoCombatPredicate ParsePredicate(JsonElement element)
    {
        var result = new AutoCombatPredicate
        {
            Round = IntValue(Find(element, "round", "roundEquals")),
            MinRound = IntValue(Find(element, "minRound", "roundMin", "roundAtLeast")),
            MaxRound = IntValue(Find(element, "maxRound", "roundMax", "roundAtMost")),
            Hp = IntValue(Find(element, "hp", "hpEquals")),
            MinHp = IntValue(Find(element, "minHp", "hpMin", "hpAtLeast")),
            MaxHp = IntValue(Find(element, "maxHp", "hpMax", "hpAtMost")),
            HpPercent = DecimalValue(Find(element, "hpPercent", "hpPct", "hpPercentEquals")),
            MinHpPercent = DecimalValue(Find(element, "minHpPercent", "hpPercentMin", "hpPercentAtLeast")),
            MaxHpPercent = DecimalValue(Find(element, "maxHpPercent", "hpPercentMax", "hpPercentAtMost")),
            Mp = IntValue(Find(element, "mp", "mpEquals")),
            MinMp = IntValue(Find(element, "minMp", "mpMin", "mpAtLeast")),
            MaxMp = IntValue(Find(element, "maxMp", "mpMax", "mpAtMost")),
            MpPercent = DecimalValue(Find(element, "mpPercent", "mpPct", "mpPercentEquals")),
            MinMpPercent = DecimalValue(Find(element, "minMpPercent", "mpPercentMin", "mpPercentAtLeast")),
            MaxMpPercent = DecimalValue(Find(element, "maxMpPercent", "mpPercentMax", "mpPercentAtMost")),
            Monster = StringValue(Find(element, "monster", "monsterId", "monsterName")),
            Monsters = StringArray(Find(element, "monsters", "monsterIds")),
            Effects = StringArray(Find(element, "effects", "hasEffects", "effectsPresent")),
            HasEffects = StringArray(Find(element, "hasEffects", "effectsPresent")),
            MissingEffects = StringArray(Find(element, "missingEffects", "effectsAbsent", "withoutEffects")),
            AnyEffects = StringArray(Find(element, "anyEffects", "oneOfEffects"))
        };
        // Nested comparison objects are convenient JSON DSL syntax.
        result = ParseNestedComparisons(element, result, "round", isPercent: false, set: static (p, min, max, eq) => p with { Round = eq, MinRound = min, MaxRound = max });
        result = ParseNestedComparisons(element, result, "hp", isPercent: false, set: static (p, min, max, eq) => p with { Hp = eq, MinHp = min, MaxHp = max });
        result = ParseNestedDecimalComparisons(element, result, "hpPercent", static (p, min, max, eq) => p with { HpPercent = eq, MinHpPercent = min, MaxHpPercent = max });
        result = ParseNestedComparisons(element, result, "mp", isPercent: false, set: static (p, min, max, eq) => p with { Mp = eq, MinMp = min, MaxMp = max });
        result = ParseNestedDecimalComparisons(element, result, "mpPercent", static (p, min, max, eq) => p with { MpPercent = eq, MinMpPercent = min, MaxMpPercent = max });
        var effects = Find(element, "effects", "effect");
        if (effects.ValueKind == JsonValueKind.Object)
        {
            result = result with
            {
                Effects = StringArray(Find(effects, "has", "present", "contains", "all")) ?? result.Effects,
                HasEffects = StringArray(Find(effects, "has", "present", "contains", "all")) ?? result.HasEffects,
                MissingEffects = StringArray(Find(effects, "missing", "absent", "without", "none")) ?? result.MissingEffects,
                AnyEffects = StringArray(Find(effects, "any", "oneOf", "oneOfEffects")) ?? result.AnyEffects
            };
        }
        return result;
    }

    private static AutoCombatPredicate ParseNestedComparisons(JsonElement parent, AutoCombatPredicate predicate, string key, bool isPercent, Func<AutoCombatPredicate, int?, int?, int?, AutoCombatPredicate> set)
    {
        var element = Find(parent, key);
        if (element.ValueKind != JsonValueKind.Object) return predicate;
        int? min = IntValue(Find(element, "gte", "min", "atLeast", "ge"));
        int? max = IntValue(Find(element, "lte", "max", "atMost", "le"));
        int? eq = IntValue(Find(element, "eq", "equals", "equal"));
        if (IntValue(Find(element, "gt", "greaterThan")) is { } gt) min = gt + 1;
        if (IntValue(Find(element, "lt", "lessThan")) is { } lt) max = lt - 1;
        return set(predicate, min, max, eq);
    }

    private static AutoCombatPredicate ParseNestedDecimalComparisons(JsonElement parent, AutoCombatPredicate predicate, string key, Func<AutoCombatPredicate, decimal?, decimal?, decimal?, AutoCombatPredicate> set)
    {
        var element = Find(parent, key);
        if (element.ValueKind != JsonValueKind.Object) return predicate;
        decimal? min = DecimalValue(Find(element, "gte", "min", "atLeast", "ge"));
        decimal? max = DecimalValue(Find(element, "lte", "max", "atMost", "le"));
        decimal? eq = DecimalValue(Find(element, "eq", "equals", "equal"));
        if (DecimalValue(Find(element, "gt", "greaterThan")) is { } gt) min = gt + 0.0000001m;
        if (DecimalValue(Find(element, "lt", "lessThan")) is { } lt) max = lt - 0.0000001m;
        return set(predicate, min, max, eq);
    }

    private static AutoCombatPredicate ParsePredicate(string text)
    {
        var predicate = new AutoCombatPredicate();
        var clauses = Regex.Split(text.Trim(), @"\s+(?:and|&&)\s+|\s*,\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (var raw in clauses)
        {
            var clause = raw.Trim();
            if (clause.Length == 0 || clause.Equals("true", StringComparison.OrdinalIgnoreCase)) continue;
            var effect = Regex.Match(clause, @"^(?:has|missing|without|lacks|effect|effects)\b\s*(?:\(|\s)*(?<op>has|missing|without|absent|contains|includes|not)?\s*[:=]?\s*(?<value>[^)\s]+)\)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (effect.Success && (clause.Contains("effect", StringComparison.OrdinalIgnoreCase)
                || clause.StartsWith("has ", StringComparison.OrdinalIgnoreCase)
                || clause.StartsWith("missing ", StringComparison.OrdinalIgnoreCase)))
            {
                var effectOp = effect.Groups["op"].Value.ToLowerInvariant();
                var value = Unquote(effect.Groups["value"].Value);
                if (effectOp is "missing" or "without" or "absent" || clause.StartsWith("missing", StringComparison.OrdinalIgnoreCase) || clause.StartsWith("without", StringComparison.OrdinalIgnoreCase))
                    predicate = predicate with { MissingEffects = Append(predicate.MissingEffects, value) };
                else
                    predicate = predicate with { HasEffects = Append(predicate.HasEffects, value) };
                continue;
            }
            var function = Regex.Match(clause, @"^(?<fn>hasEffect|missingEffect|effect|effects)\s*\(\s*(?<value>[^)]*)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (function.Success)
            {
                var value = Unquote(function.Groups["value"].Value.Trim());
                var missing = function.Groups["fn"].Value.StartsWith("missing", StringComparison.OrdinalIgnoreCase);
                predicate = missing
                    ? predicate with { MissingEffects = Append(predicate.MissingEffects, value) }
                    : predicate with { HasEffects = Append(predicate.HasEffects, value) };
                continue;
            }
            var comparison = Regex.Match(clause, @"^(?<key>[A-Za-z][A-Za-z0-9_%]*)\s*(?<op>==|=|!=|>=|<=|>|<|is|atleast|atmost)\s*(?<value>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!comparison.Success)
            {
                var monsterBare = Regex.Match(clause, @"^monster\s+(?<value>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (monsterBare.Success)
                {
                    predicate = predicate with { Monster = Unquote(monsterBare.Groups["value"].Value.Trim()) };
                    continue;
                }
                throw new FormatException($"Unknown predicate clause '{clause}'.");
            }
            var key = comparison.Groups["key"].Value.ToLowerInvariant();
            var op = comparison.Groups["op"].Value.ToLowerInvariant();
            var valueText = Unquote(comparison.Groups["value"].Value.Trim());
            if (key is "monster" or "monsterid" or "monstername")
            {
                if (op is "!=") throw new FormatException("Monster != is not supported; use an explicit ordered fallback rule.");
                predicate = predicate with { Monster = valueText };
                continue;
            }
            if (key is "round")
            {
                if (!int.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) throw new FormatException($"Invalid round '{valueText}'.");
                predicate = SetInt(predicate, value, op, static (p, v) => p with { Round = v }, static (p, v) => p with { MinRound = v }, static (p, v) => p with { MaxRound = v });
                continue;
            }
            if (key is "hp" or "health" or "currenthp")
            {
                if (valueText.EndsWith('%'))
                {
                    if (!decimal.TryParse(valueText.TrimEnd('%'), NumberStyles.Number, CultureInfo.InvariantCulture, out var percent)) throw new FormatException($"Invalid hp percent '{valueText}'.");
                    predicate = SetDecimal(predicate, percent / 100m, op, static (p, v) => p with { HpPercent = v }, static (p, v) => p with { MinHpPercent = v }, static (p, v) => p with { MaxHpPercent = v });
                }
                else
                {
                    if (!int.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) throw new FormatException($"Invalid hp '{valueText}'.");
                    predicate = SetInt(predicate, value, op, static (p, v) => p with { Hp = v }, static (p, v) => p with { MinHp = v }, static (p, v) => p with { MaxHp = v });
                }
                continue;
            }
            if (key is "mp" or "mana" or "currentmp")
            {
                if (valueText.EndsWith('%'))
                {
                    if (!decimal.TryParse(valueText.TrimEnd('%'), NumberStyles.Number, CultureInfo.InvariantCulture, out var percent)) throw new FormatException($"Invalid mp percent '{valueText}'.");
                    predicate = SetDecimal(predicate, percent / 100m, op, static (p, v) => p with { MpPercent = v }, static (p, v) => p with { MinMpPercent = v }, static (p, v) => p with { MaxMpPercent = v });
                }
                else
                {
                    if (!int.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) throw new FormatException($"Invalid mp '{valueText}'.");
                    predicate = SetInt(predicate, value, op, static (p, v) => p with { Mp = v }, static (p, v) => p with { MinMp = v }, static (p, v) => p with { MaxMp = v });
                }
                continue;
            }
            if (key is "hp%" or "hppercent" or "health%" or "healthpercent")
            {
                if (!decimal.TryParse(valueText.TrimEnd('%'), NumberStyles.Number, CultureInfo.InvariantCulture, out var value)) throw new FormatException($"Invalid hp percent '{valueText}'.");
                predicate = SetDecimal(predicate, valueText.EndsWith('%') ? value / 100m : value, op, static (p, v) => p with { HpPercent = v }, static (p, v) => p with { MinHpPercent = v }, static (p, v) => p with { MaxHpPercent = v });
                continue;
            }
            if (key is "mp%" or "mppercent" or "manapercent")
            {
                if (!decimal.TryParse(valueText.TrimEnd('%'), NumberStyles.Number, CultureInfo.InvariantCulture, out var value)) throw new FormatException($"Invalid mp percent '{valueText}'.");
                predicate = SetDecimal(predicate, valueText.EndsWith('%') ? value / 100m : value, op, static (p, v) => p with { MpPercent = v }, static (p, v) => p with { MinMpPercent = v }, static (p, v) => p with { MaxMpPercent = v });
                continue;
            }
            throw new FormatException($"Unknown predicate key '{key}'.");
        }
        return predicate;
    }

    private static AutoCombatPredicate SetInt(AutoCombatPredicate predicate, int value, string op, Func<AutoCombatPredicate, int, AutoCombatPredicate> equals, Func<AutoCombatPredicate, int, AutoCombatPredicate> min, Func<AutoCombatPredicate, int, AutoCombatPredicate> max)
        => op switch
        {
            "=" or "==" or "is" => equals(predicate, value),
            ">" => min(predicate, value + 1),
            ">=" or "atleast" => min(predicate, value),
            "<" => max(predicate, value - 1),
            "<=" or "atmost" => max(predicate, value),
            "!=" => predicate,
            _ => predicate
        };

    private static AutoCombatPredicate SetDecimal(AutoCombatPredicate predicate, decimal value, string op, Func<AutoCombatPredicate, decimal, AutoCombatPredicate> equals, Func<AutoCombatPredicate, decimal, AutoCombatPredicate> min, Func<AutoCombatPredicate, decimal, AutoCombatPredicate> max)
        => op switch
        {
            "=" or "==" or "is" => equals(predicate, value),
            ">" => min(predicate, value + 0.0000001m),
            ">=" or "atleast" => min(predicate, value),
            "<" => max(predicate, value - 0.0000001m),
            "<=" or "atmost" => max(predicate, value),
            "!=" => predicate,
            _ => predicate
        };

    private static IReadOnlyList<string> Append(IReadOnlyList<string>? values, string value)
        => (values ?? Array.Empty<string>()).Concat(new[] { value }).ToArray();

    private static int FindActionSeparator(string value)
    {
        var arrow = value.IndexOf("->", StringComparison.Ordinal);
        if (arrow >= 0) return arrow;
        var then = IndexOfWord(value, "then");
        return then;
    }

    private static int SeparatorLength(string value, int index)
        => value.AsSpan(index).StartsWith("->", StringComparison.Ordinal) ? 2 : 4;

    private static int IndexOfWord(string text, string word)
    {
        var match = Regex.Match(text, $@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Index : -1;
    }

    private static JsonElement Find(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return default;
        foreach (var property in element.EnumerateObject())
            if (names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))) return property.Value;
        return default;
    }

    private static string? StringValue(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            _ => null
        };

    private static int? IntValue(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value)) return value;
        if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return value;
        return null;
    }

    private static decimal? DecimalValue(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var value)) return value;
        if (element.ValueKind == JsonValueKind.String && decimal.TryParse(element.GetString()?.TrimEnd('%'), NumberStyles.Number, CultureInfo.InvariantCulture, out value))
            return element.GetString()!.EndsWith('%') ? value / 100m : value;
        return null;
    }

    private static bool? BoolValue(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.True) return true;
        if (element.ValueKind == JsonValueKind.False) return false;
        if (element.ValueKind == JsonValueKind.String && bool.TryParse(element.GetString(), out var value)) return value;
        return null;
    }

    private static IReadOnlyList<string>? StringArray(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
            return element.EnumerateArray().Select(StringValue).Where(static x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToArray();
        if (element.ValueKind == JsonValueKind.String)
            return new[] { element.GetString()! };
        return null;
    }

    private static string Unquote(string value)
    {
        value = value.Trim();
        return value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1]
            : value;
    }
}

/// <summary>Short evaluator name for hosts that do not use the policy suffix.</summary>
public sealed class AutoCombatEvaluator
{
    private readonly AutoCombatPolicyEvaluator _inner = new();
    public AutoCombatDecision Evaluate(AutoCombatPolicy policy, AutoCombatContext context, AutoCombatUsage? usage = null)
        => _inner.Evaluate(policy, context, usage);
    public AutoCombatDecision Decide(AutoCombatPolicy policy, AutoCombatContext context, AutoCombatUsage? usage = null)
        => _inner.Evaluate(policy, context, usage);
}
