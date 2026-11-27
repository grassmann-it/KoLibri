
namespace KoLibri.Desktop;

/// <summary>What a bounded batch of turns did and why it stopped, as readable text.</summary>
public sealed record BoundedRunReport(
    int TurnsUsed, int TurnsLeft, string StopReason, string WhyStopped,
    IReadOnlyList<AdventureDrop> Drops, bool NeedsUser)
{
    public static BoundedRunReport From(
        int turnsStarted, int turnsLeft, BoundedAdventureRunStopReason stop,
        IEnumerable<AdventureDrop>? drops, string? failure = null)
    {
        var merged = (drops ?? Array.Empty<AdventureDrop>())
            .GroupBy(d => d.ItemName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AdventureDrop { ItemName = g.First().ItemName, Quantity = g.Sum(d => d.Quantity) })
            .OrderByDescending(d => d.Quantity).ToArray();
        var (why, needsUser) = stop switch
        {
            BoundedAdventureRunStopReason.Complete when turnsLeft <= 0 => ("the turn budget is used up", false),
            BoundedAdventureRunStopReason.Complete => ("the run ended early", false),
            BoundedAdventureRunStopReason.ConditionsSatisfied => ("the stop condition (item owned) was reached", false),
            BoundedAdventureRunStopReason.ConditionsBlocked => ("a stop condition could not be checked", true),
            BoundedAdventureRunStopReason.AwaitingManualChoice => ("a choice adventure is open and needs your decision", true),
            BoundedAdventureRunStopReason.NeedsDecision => ("a choice adventure is open and the choice policy has no answer", true),
            BoundedAdventureRunStopReason.StepLimitReached => ("the step limit was reached", false),
            BoundedAdventureRunStopReason.FightRoundLimitReached => ("the combat round limit was reached; the fight is still open", true),
            BoundedAdventureRunStopReason.HpBelowMinimum => ("HP is below the stop threshold (or unknown); the fight is still open", true),
            BoundedAdventureRunStopReason.CombatPolicyStopped => (failure is { Length: > 0 }
                ? $"the combat plan has no action for this round ({failure}); the fight is still open"
                : "the combat plan has no action for this round; the fight is still open", true),
            BoundedAdventureRunStopReason.DeadlineReached => ("the time limit was reached", false),
            BoundedAdventureRunStopReason.GatewayStopped => (failure is { Length: > 0 }
                ? AdventureFailureMapper.Describe(failure).Text : "KoL stopped the run (fight, error or refresh needed)", true),
            _ => (stop.ToString(), false)
        };
        return new BoundedRunReport(turnsStarted, turnsLeft, stop.ToString(), why, merged, needsUser);
    }

    public string Summary
    {
        get
        {
            var loot = Drops.Count == 0 ? "no drops"
                : "drops: " + string.Join(", ", Drops.Select(d => $"{d.Quantity} × {d.ItemName}"));
            return $"Used {TurnsUsed} turn{(TurnsUsed == 1 ? "" : "s")}, {TurnsLeft} left; {loot}. Stopped because {WhyStopped}.";
        }
    }
}
