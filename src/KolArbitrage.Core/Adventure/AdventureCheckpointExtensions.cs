namespace KolArbitrage.Core.Adventure;

/// <summary>Helpers for hosts that persist and later inspect checkpoints.</summary>
public static class AdventureCheckpointExtensions
{
    public static AdventureRunState ToState(this AdventureCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var value = checkpoint.Normalize();
        AdventureRunState state = value.Phase switch
        {
            AdventureRunPhase.Ready => new AdventureReadyRunState(
                value.RunId, value.AdventureId, value.Role, value.DailyLimit, value.Counters),
            AdventureRunPhase.Encounter => new AdventureEncounterRunState(
                value.RunId, value.AdventureId, value.Role, value.DailyLimit, value.Counters),
            AdventureRunPhase.Fight => new AdventureFightRunState(
                value.RunId, value.AdventureId, value.Role, value.DailyLimit, value.Counters),
            AdventureRunPhase.Choice => new AdventureChoiceRunState(
                value.RunId, value.AdventureId, value.Role, value.DailyLimit, value.Counters),
            AdventureRunPhase.Reward => new AdventureRewardRunState(
                value.RunId, value.AdventureId, value.Role, value.DailyLimit, value.Counters),
            AdventureRunPhase.Complete => new AdventureCompleteRunState(
                value.RunId, value.AdventureId, value.Role, value.DailyLimit, value.Counters),
            AdventureRunPhase.Aborted => new AdventureAbortedRunState(
                value.RunId, value.AdventureId, value.Role, value.DailyLimit, value.Counters,
                value.AbortReason ?? AdventureAbortReason.InvalidCheckpoint,
                value.AbortMessage ?? "The checkpoint was aborted."),
            _ => new AdventureAbortedRunState(
                value.RunId, value.AdventureId, value.Role, value.DailyLimit, value.Counters,
                AdventureAbortReason.InvalidCheckpoint,
                "The checkpoint contains an unknown phase.")
        };

        return state with
        {
            EncounterId = value.EncounterId,
            MonsterId = value.MonsterId,
            FightRound = value.FightRound,
            ChoiceId = value.ChoiceId,
            SelectedOption = value.SelectedOption,
            LastResponseHash = value.LastResponseHash,
            PendingAction = PendingAction(value),
            AbortMessage = value.AbortMessage
        };
    }

    private static AdventureKernelAction? PendingAction(AdventureCheckpoint value)
        => value.PendingActionKind switch
        {
            AdventureKernelActionKind.Encounter => new AdventureEncounterAction(
                value.AdventureId, value.EncounterId),
            AdventureKernelActionKind.Fight when !string.IsNullOrWhiteSpace(value.PendingActionId)
                => new AdventureFightAction(
                    value.AdventureId,
                    value.PendingActionId,
                    value.FightRound ?? 0,
                    value.EncounterId,
                    value.MonsterId),
            AdventureKernelActionKind.Choice when !string.IsNullOrWhiteSpace(value.PendingChoiceId)
                && !string.IsNullOrWhiteSpace(value.PendingChoiceOption)
                => new AdventureChoiceAction(
                    value.AdventureId,
                    value.PendingChoiceId,
                    value.PendingChoiceOption,
                    value.EncounterId,
                    value.PendingChoiceFields),
            _ => null
        };
}
