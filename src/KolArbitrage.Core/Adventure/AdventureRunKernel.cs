namespace KolArbitrage.Core.Adventure;

/// <summary>
/// Deterministic adventure state machine. Callers provide observations (usually
/// from <see cref="KoLAdventureEncounterParser"/>); the kernel returns an
/// approved action or an aborted state. It never performs I/O.
/// </summary>
public sealed class AdventureRunKernel
{
    public AdventureKernelDecision Begin(
        AdventureRunRequest request,
        AdventureActionAllowlist allowlist)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(allowlist);
        var value = request.Normalize();

        if (string.IsNullOrWhiteSpace(value.RunId))
            return Abort(
                value,
                AdventureAbortReason.InvalidObservation,
                "A run id is required.");
        if (string.IsNullOrWhiteSpace(value.AdventureId))
            return Abort(
                value,
                AdventureAbortReason.InvalidObservation,
                "An adventure id is required.");
        if (!allowlist.IsAdventureAllowed(value.AdventureId))
            return Abort(
                value,
                AdventureAbortReason.AdventureNotAllowlisted,
                $"Adventure '{value.AdventureId}' is not allowlisted.");

        var limit = value.DailyLimit.Normalize();
        if (!limit.CanStart())
            return Abort(
                value,
                AdventureAbortReason.DailyLimitReached,
                "The daily adventure limit has been reached.");

        // Reserve one turn/run in the checkpoint before emitting the start
        // action. A crash after this point cannot accidentally start an extra
        // run on resume.
        var reserved = limit.Reserve();
        var counters = value.Counters with
        {
            AdventuresStarted = Increment(value.Counters.AdventuresStarted),
            EncounterActions = Increment(value.Counters.EncounterActions)
        };
        var action = new AdventureEncounterAction(value.AdventureId);
        var state = new AdventureEncounterRunState(
            value.RunId,
            value.AdventureId,
            value.Role,
            reserved,
            counters)
        {
            PendingAction = action
        };

        return Decision(state, action);
    }

    /// <summary>Alias for hosts that prefer a verb matching a resume call.</summary>
    public AdventureKernelDecision Start(
        AdventureRunRequest request,
        AdventureActionAllowlist allowlist)
        => Begin(request, allowlist);

    public AdventureKernelDecision Observe(
        AdventureRunState state,
        AdventureKernelObservation observation,
        AdventureActionAllowlist allowlist)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(allowlist);

        if (state.IsTerminal)
            return AbortExisting(state, AdventureAbortReason.TerminalState, "The run is already terminal.");

        if (!string.Equals(state.AdventureId, observation.AdventureId, StringComparison.Ordinal))
            return AbortExisting(
                state,
                AdventureAbortReason.ResumeMismatch,
                "The observed adventure does not match the checkpoint.",
                observation.ResponseHash);

        var counters = state.Counters with
        {
            ResponsesObserved = Increment(state.Counters.ResponsesObserved)
        };
        var responseState = state with
        {
            // A with-expression preserves the runtime state type and the
            // checkpoint's non-HTTP metadata while the typed transition below
            // chooses its next phase.
            LastResponseHash = observation.ResponseHash,
            PendingAction = null,
            Counters = counters
        };

        // The parser's enum is intentionally reused. A cast value that was
        // added later, or a malformed fixture, enters this default branch and
        // aborts rather than guessing an action.
        return observation.Kind switch
        {
            AdventureEncounterKind.Fight => ObserveFight(responseState, observation, allowlist),
            AdventureEncounterKind.Choice => ObserveChoice(responseState, observation, allowlist),
            AdventureEncounterKind.Reward or AdventureEncounterKind.Complete
                => Complete(responseState, observation),
            _ => AbortExisting(
                responseState,
                AdventureAbortReason.UnknownEncounter,
                observation.UnknownReason ?? "The encounter kind is unknown.",
                observation.ResponseHash)
        };
    }

    /// <summary>
    /// Converts a parser result without requiring callers to construct a second
    /// observation object themselves.
    /// </summary>
    public AdventureKernelDecision Observe(
        AdventureRunState state,
        AdventureEncounterState encounter,
        AdventureActionAllowlist allowlist)
        => Observe(state, AdventureKernelObservation.From(encounter), allowlist);

    public AdventureKernelDecision Advance(
        AdventureRunState state,
        AdventureKernelObservation observation,
        AdventureActionAllowlist allowlist)
        => Observe(state, observation, allowlist);

    public AdventureKernelDecision Advance(
        AdventureRunState state,
        AdventureEncounterState encounter,
        AdventureActionAllowlist allowlist)
        => Observe(state, encounter, allowlist);

    /// <summary>
    /// Resume is deliberately read-before-act. A pending action from a
    /// checkpoint is not re-emitted; callers must refresh the encounter and
    /// pass the resulting observation to <see cref="Observe"/>.
    /// </summary>
    public AdventureKernelDecision Resume(
        AdventureCheckpoint checkpoint,
        AdventureActionAllowlist allowlist)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(allowlist);
        var value = checkpoint.Normalize();
        if (value.Version != 1
            || string.IsNullOrWhiteSpace(value.RunId)
            || string.IsNullOrWhiteSpace(value.AdventureId))
        {
            var invalid = new AdventureRunRequest
            {
                RunId = value.RunId,
                AdventureId = value.AdventureId,
                Role = value.Role,
                DailyLimit = value.DailyLimit,
                Counters = value.Counters
            };
            return Abort(
                invalid,
                AdventureAbortReason.InvalidCheckpoint,
                "The checkpoint is missing a supported version or identity.");
        }

        if (!allowlist.IsAdventureAllowed(value.AdventureId))
        {
            var request = new AdventureRunRequest
            {
                RunId = value.RunId,
                AdventureId = value.AdventureId,
                Role = value.Role,
                DailyLimit = value.DailyLimit,
                Counters = value.Counters
            };
            return Abort(
                request,
                AdventureAbortReason.AdventureNotAllowlisted,
                $"Adventure '{value.AdventureId}' is not allowlisted.");
        }

        var state = ToState(value);
        return new AdventureKernelDecision
        {
            State = state,
            Checkpoint = AdventureCheckpoint.FromState(state),
            Actions = Array.Empty<AdventureKernelAction>(),
            RequiresRefresh = state.PendingAction is not null,
            WasResumed = true
        };
    }

    private static AdventureKernelDecision ObserveFight(
        AdventureRunState state,
        AdventureKernelObservation observation,
        AdventureActionAllowlist allowlist)
    {
        if (!observation.FightRound.HasValue || observation.FightRound.Value < 0)
            return AbortExisting(
                state,
                AdventureAbortReason.UnknownFight,
                "The fight response has no valid round.",
                observation.ResponseHash);
        if (!allowlist.IsEncounterAllowed(state.AdventureId, observation.EncounterId))
            return AbortExisting(
                state,
                AdventureAbortReason.EncounterNotAllowlisted,
                "The fight encounter is not allowlisted.",
                observation.ResponseHash);
        if (!allowlist.TryGetFightAction(
                state.AdventureId,
                observation.EncounterId,
                observation.MonsterId,
                observation.FightRound.Value,
                out var action))
        {
            return AbortExisting(
                state,
                AdventureAbortReason.UnknownFight,
                "No allowlisted fight action matches the observed monster and round.",
                observation.ResponseHash);
        }

        var next = new AdventureFightRunState(
            state.RunId,
            state.AdventureId,
            state.Role,
            state.DailyLimit,
            state.Counters with { FightActions = Increment(state.Counters.FightActions) })
        {
            EncounterId = observation.EncounterId,
            MonsterId = observation.MonsterId,
            FightRound = observation.FightRound,
            LastResponseHash = observation.ResponseHash,
            PendingAction = action
        };
        return Decision(next, action);
    }

    private static AdventureKernelDecision ObserveChoice(
        AdventureRunState state,
        AdventureKernelObservation observation,
        AdventureActionAllowlist allowlist)
    {
        if (string.IsNullOrWhiteSpace(observation.ChoiceId))
            return AbortExisting(
                state,
                AdventureAbortReason.UnknownChoice,
                "The choice response has no choice id.",
                observation.ResponseHash);
        if (!allowlist.IsEncounterAllowed(state.AdventureId, observation.EncounterId))
            return AbortExisting(
                state,
                AdventureAbortReason.EncounterNotAllowlisted,
                "The choice encounter is not allowlisted.",
                observation.ResponseHash);
        if (!allowlist.TryGetChoiceAction(
                state.AdventureId,
                observation.ChoiceId,
                observation.AvailableChoices,
                observation.EncounterId,
                out var action))
        {
            var reason = observation.AvailableChoices.Count == 0
                ? AdventureAbortReason.UnknownChoice
                : AdventureAbortReason.ChoiceOptionNotAvailable;
            return AbortExisting(
                state,
                reason,
                "No allowlisted choice option matches the observed page.",
                observation.ResponseHash);
        }

        var next = new AdventureChoiceRunState(
            state.RunId,
            state.AdventureId,
            state.Role,
            state.DailyLimit,
            state.Counters with { ChoicesMade = Increment(state.Counters.ChoicesMade) })
        {
            EncounterId = observation.EncounterId,
            ChoiceId = observation.ChoiceId,
            SelectedOption = action.OptionId,
            LastResponseHash = observation.ResponseHash,
            PendingAction = action
        };
        return Decision(next, action);
    }

    private static AdventureKernelDecision Complete(
        AdventureRunState state,
        AdventureKernelObservation observation)
    {
        var next = new AdventureCompleteRunState(
            state.RunId,
            state.AdventureId,
            state.Role,
            state.DailyLimit,
            state.Counters)
        {
            EncounterId = observation.EncounterId,
            LastResponseHash = observation.ResponseHash
        };
        return new AdventureKernelDecision
        {
            State = next,
            Checkpoint = AdventureCheckpoint.FromState(next),
            Actions = Array.Empty<AdventureKernelAction>()
        };
    }

    private static AdventureKernelDecision Decision(AdventureRunState state, AdventureKernelAction action)
        => new()
        {
            State = state,
            Checkpoint = AdventureCheckpoint.FromState(state),
            Actions = new[] { action }
        };

    private static AdventureKernelDecision Abort(
        AdventureRunRequest request,
        AdventureAbortReason reason,
        string message)
    {
        var state = new AdventureAbortedRunState(
            request.RunId,
            request.AdventureId,
            request.Role,
            request.DailyLimit.Normalize(),
            request.Counters.Normalize(),
            reason,
            message);
        return new AdventureKernelDecision
        {
            State = state,
            Checkpoint = AdventureCheckpoint.FromState(state),
            Actions = Array.Empty<AdventureKernelAction>(),
            AbortReason = reason
        };
    }

    private static AdventureKernelDecision AbortExisting(
        AdventureRunState state,
        AdventureAbortReason reason,
        string message,
        string? responseHash = null)
    {
        var next = new AdventureAbortedRunState(
            state.RunId,
            state.AdventureId,
            state.Role,
            state.DailyLimit,
            state.Counters,
            reason,
            message)
        {
            EncounterId = state.EncounterId,
            MonsterId = state.MonsterId,
            FightRound = state.FightRound,
            ChoiceId = state.ChoiceId,
            SelectedOption = state.SelectedOption,
            LastResponseHash = responseHash ?? state.LastResponseHash,
            AbortMessage = message
        };
        return new AdventureKernelDecision
        {
            State = next,
            Checkpoint = AdventureCheckpoint.FromState(next),
            Actions = Array.Empty<AdventureKernelAction>(),
            AbortReason = reason
        };
    }

    private static AdventureRunState ToState(AdventureCheckpoint checkpoint)
        => checkpoint.ToState();

    private static int Increment(int value)
        => value == int.MaxValue ? int.MaxValue : value + 1;
}
