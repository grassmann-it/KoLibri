namespace KolArbitrage.Core.Adventure;

/// <summary>Result of applying a deterministic AutoCombat policy to one fight page.</summary>
public sealed record AutoCombatAdventureDecision
{
    public required AutoCombatDecision PolicyDecision { get; init; }
    public AdventureFightAction? Action { get; init; }
    public bool Allowed { get; init; }
    public string? BlockReason { get; init; }
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
        AutoCombatUsage? usage = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentException.ThrowIfNullOrWhiteSpace(adventureId);
        var value = observation.Normalize();
        if (value.IsTerminal || value.Round <= 0)
            return Block(policy.Evaluate(ToContext(value), usage), "The combat observation is terminal or has no valid round.");

        var decision = policy.Evaluate(ToContext(value), usage);
        if (!decision.IsAction)
            return Block(decision, decision.Reason);

        var actionId = ToActionId(decision.Action);
        if (actionId is null)
            return Block(decision, "The policy action is not a fight action.");
        if (!IsLegal(decision.Action, value.AvailableActions))
            return Block(decision, $"Policy action '{actionId}' is not present in the current legal-action snapshot.");

        return new AutoCombatAdventureDecision
        {
            PolicyDecision = decision,
            Action = new AdventureFightAction(adventureId.Trim(), actionId, value.Round, encounterId, value.MonsterId ?? value.MonsterName),
            Allowed = true
        };
    }

    private static AutoCombatAdventureDecision Block(AutoCombatDecision decision, string? reason)
        => new() { PolicyDecision = decision, Allowed = false, BlockReason = reason };

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
