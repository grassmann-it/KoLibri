namespace KoLibri.Core.Spiel;

/// <summary>Result of applying a deterministic AutoCombat policy to one fight page.</summary>
public sealed record AutoCombatAdventureDecision
{
    public required AutoCombatDecision PolicyDecision { get; init; }
    public AdventureFightAction? Action { get; init; }
    public bool Allowed { get; init; }
    public string? BlockReason { get; init; }
    /// <summary>Recovery evidence used to gate this decision, when supplied.</summary>
    public RecoveryPlan? RecoveryPlan { get; init; }
}

/// <summary>
/// Bridges the pure AutoCombat DSL to the kernel's explicit fight action. It
/// never sends a request and requires the selected action to be visible on the
/// current fight page before producing an allowlisted action.
/// </summary>
public static class AutoCombatAdventureAdapter
{
    public static AutoCombatAdventureDecision Evaluate(
        AutoCombatPolicy policy,
        CombatObservation observation,
        string adventureId,
        string? encounterId = null,
        AutoCombatUsage? usage = null,
        RecoveryPlan? recoveryPlan = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentException.ThrowIfNullOrWhiteSpace(adventureId);
        var value = observation.Normalize();
        if (value.IsTerminal || value.Round <= 0)
            return Block(policy.Evaluate(ToContext(value), usage), "The combat observation is terminal or has no valid round.", recoveryPlan);

        var decision = policy.Evaluate(ToContext(value), usage);
        if (recoveryPlan is not null && !recoveryPlan.CanEnterCombat)
        {
            var reason = recoveryPlan.StopReasons.Count > 0
                ? $"Recovery gate blocked combat: {string.Join(", ", recoveryPlan.StopReasons)}."
                : "Recovery gate blocked combat; a fresh safe recovery plan is required.";
            return Block(decision, reason, recoveryPlan);
        }
        if (!decision.IsAction)
            return Block(decision, decision.Reason, recoveryPlan);

        var actionId = ToActionId(decision.Action);
        if (actionId is null)
            return Block(decision, "The policy action is not a fight action.", recoveryPlan);
        if (!IsLegal(decision.Action, value.AvailableActions))
            return Block(decision, $"Policy action '{actionId}' is not present in the current legal-action snapshot.", recoveryPlan);

        return new AutoCombatAdventureDecision
        {
            PolicyDecision = decision,
            Action = new AdventureFightAction(adventureId.Trim(), actionId, value.Round, encounterId, value.MonsterId ?? value.MonsterName),
            Allowed = true,
            RecoveryPlan = recoveryPlan
        };
    }

    private static AutoCombatAdventureDecision Block(
        AutoCombatDecision decision,
        string? reason,
        RecoveryPlan? recoveryPlan = null)
        => new()
        {
            PolicyDecision = decision,
            Allowed = false,
            BlockReason = reason,
            RecoveryPlan = recoveryPlan
        };

    private static AutoCombatContext ToContext(CombatObservation observation)
        => new()
        {
            Round = observation.Round,
            Hp = observation.PlayerHp ?? 0,
            MaxHp = observation.PlayerMaxHp ?? 0,
            Mp = observation.PlayerMp ?? 0,
            MaxMp = observation.PlayerMaxMp ?? 0,
            Monster = observation.MonsterId ?? observation.MonsterName ?? observation.Monster,
            Effects = observation.Effects
                .Select(effect => effect.Id ?? effect.Name)
                .Where(effect => !string.IsNullOrWhiteSpace(effect))
                .Select(effect => effect!)
                .ToArray()
        };

    private static string? ToActionId(AutoCombatAction action)
        => action.Kind switch
        {
            AutoCombatActionKind.Attack => "attack",
            AutoCombatActionKind.Skill when !string.IsNullOrWhiteSpace(action.Id) => $"skill:{action.Id}",
            AutoCombatActionKind.Item when !string.IsNullOrWhiteSpace(action.Id) => $"item:{action.Id}",
            AutoCombatActionKind.Run => "runaway",
            _ => null
        };

    /// <summary>
    /// The adventure-kernel fight action ids a policy can emit (attack, run, skill:id, item:id), so a
    /// batch allowlist is derived from the policy instead of a hard-coded attack. Without a policy, or
    /// for a policy without any fight action, the list is just "attack".
    /// </summary>
    public static IReadOnlyList<string> AllowedFightActionIds(AutoCombatPolicy? policy)
    {
        if (policy is null) return new[] { "attack" };
        var ids = policy.Rules.Select(rule => rule.Then).Append(policy.DefaultAction)
            .Where(action => action is not null)
            .Select(action => action!.Kind == AutoCombatActionKind.Run ? "run" : ToActionId(action))
            .Where(id => id is not null).Select(id => id!)
            .Distinct(StringComparer.Ordinal).ToArray();
        return ids.Length == 0 ? new[] { "attack" } : ids;
    }

    /// <summary>The kernel action id of the enabled legal action that matches <paramref name="action"/>.</summary>
    internal static string? LegalAdventureActionId(AutoCombatAction action, IReadOnlyList<CombatAction> legal)
        => legal.FirstOrDefault(candidate => IsLegal(action, new[] { candidate }))?.AdventureActionId;

    private static bool IsLegal(AutoCombatAction action, IReadOnlyList<CombatAction> legal)
    {
        if (legal.Count == 0) return false;
        return legal.Any(candidate => candidate.IsEnabled && action.Kind switch
        {
            AutoCombatActionKind.Attack => candidate.Kind == CombatActionKind.Attack,
            AutoCombatActionKind.Skill => candidate.Kind == CombatActionKind.Skill
                && Matches(candidate, action.Id),
            AutoCombatActionKind.Item => candidate.Kind == CombatActionKind.Item
                && Matches(candidate, action.Id),
            AutoCombatActionKind.Run => candidate.Kind == CombatActionKind.Run,
            _ => false
        });
    }

    private static bool Matches(CombatAction candidate, string? id)
        => string.IsNullOrWhiteSpace(id)
            || string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.Name, id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.Value, id, StringComparison.OrdinalIgnoreCase);
}
