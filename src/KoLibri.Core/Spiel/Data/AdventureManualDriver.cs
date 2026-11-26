using KoLibri.Core.Basis;

namespace KoLibri.Core.Spiel;

/// <summary>
/// Result of one manual step. Holds the gateway response and the operator's
/// view of the post-step state (encounter, choice options, manual action).
/// </summary>
public sealed record ManualStepOutcome
{
    public required string RunId { get; init; }
    public required string Location { get; init; }
    public required AdventureExecutionStatus Status { get; init; }
    public required bool IsComplete { get; init; }
    public required bool RequiresRefresh { get; init; }
    public required bool IsAborted { get; init; }
    public required AdventureEncounterState? Encounter { get; init; }
    public required string StopReason { get; init; }
    public required AdventureKernelAction? NextAction { get; init; }
    public required string? Message { get; init; }
    public required string? RequestId { get; init; }
    public IReadOnlyList<QuestDrop> Drops { get; init; } = Array.Empty<QuestDrop>();
    /// <summary>Redacted copy of the page the run could not handle, when one was saved.</summary>
    public string? UnknownPagePath { get; init; }

    /// <summary>" The page was saved (redacted) to …" or empty, for user-facing stop messages.</summary>
    public string SavedPageNote => UnknownPagePath is null ? string.Empty : $" The page was saved (redacted) to {UnknownPagePath}.";
}

/// <summary>
/// Bridges a manual UI / AutoCombat into the existing
/// <see cref="AdventureExecutionGateway"/> + allowlist contract. Each call
/// to <see cref="StartAsync"/> begins an adventure, each call to
/// <see cref="ExecuteAsync"/> runs the next bounded step.
/// </summary>
public sealed class AdventureManualDriver
{
    private readonly AccountRole _role;
    private readonly AdventureExecutionGateway _gateway;
    public AdventureManualDriver(AccountRole role, AdventureExecutionGateway gateway)
    {
        _role = role;
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
    }

    public AdventureManualDriver(AccountRole role, AdventureExecutionGateway gateway, string approvalToken)
        : this(role, gateway)
    {
        ArgumentNullException.ThrowIfNull(approvalToken);
    }

    public async ValueTask<ManualStepOutcome> StartAsync(
        AdventureRunRequest request,
        AdventureActionAllowlist allowlist,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(allowlist);
        var enriched = new AdventureRunRequest
        {
            RunId = request.RunId,
            AdventureId = request.AdventureId,
            Snarfblat = request.Snarfblat,
            Role = _role,
            DailyLimit = request.DailyLimit,
            Counters = request.Counters
        };
        var result = await _gateway.StartAsync(enriched, allowlist, cancellationToken).ConfigureAwait(false);
        return ToOutcome(result, enriched.RunId, enriched.AdventureId);
    }

    public async ValueTask<ManualStepOutcome> ExecuteAsync(
        string runId,
        AdventureActionAllowlist allowlist,
        AdventureStepSpec step,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(allowlist);
        ArgumentNullException.ThrowIfNull(step);

        var checkpoint = await _gateway.CheckpointStore.LoadAsync(runId, cancellationToken).ConfigureAwait(false);
        if (checkpoint is null)
        {
            return new ManualStepOutcome
            {
                RunId = runId,
                Location = "?",
                Status = AdventureExecutionStatus.Aborted,
                IsComplete = false,
                IsAborted = true,
                RequiresRefresh = false,
                StopReason = "NoCheckpoint",
                Encounter = null,
                NextAction = null,
                Message = "No checkpoint exists for this run; the session must be restarted.",
                RequestId = null
            };
        }

        if (!string.IsNullOrWhiteSpace(checkpoint.InFlightRequestId))
        {
            var refresh = await _gateway.RefreshAsync(runId, allowlist, cancellationToken).ConfigureAwait(false);
            return ToOutcome(refresh, runId, checkpoint.AdventureId);
        }

        var state = checkpoint.ToState();
        AdventureKernelAction? action = checkpoint.Role != _role || !allowlist.IsAdventureAllowed(checkpoint.AdventureId)
            ? null : (state, step) switch
        {
            ({ PendingAction: AdventureFightAction fight }, _) => SelectFightAction(fight, step),
            (AdventureChoiceRunState { PendingAction: null } waiting, ChoiceStepSpec selected)
                when waiting.ChoiceId == selected.ChoiceId
                    && checkpoint.ManualChoiceOptions.Contains(selected.OptionId, StringComparer.Ordinal)
                => new AdventureChoiceAction(checkpoint.AdventureId, selected.ChoiceId, selected.OptionId,
                    waiting.EncounterId, checkpoint.PendingChoiceFields),
            ({ PendingAction: AdventureChoiceAction choice }, ChoiceStepSpec selected) when choice.ChoiceId == selected.ChoiceId && choice.OptionId == selected.OptionId => choice,
            _ => null
        };
        if (action is null || (action is AdventureFightAction fightAction
            && checkpoint.ObservedFightActions is { } offered
            && !offered.Any(candidate => candidate.IsEnabled && candidate.AdventureActionId == fightAction.ActionId)))
            return new ManualStepOutcome
            {
                RunId = runId,
                Location = checkpoint.AdventureId,
                Status = AdventureExecutionStatus.Advanced,
                IsComplete = false,
                IsAborted = false,
                RequiresRefresh = false,
                StopReason = "ActionUnavailable",
                Encounter = null,
                NextAction = state.PendingAction,
                Message = "The selected action does not match the current encounter.",
                RequestId = null
            };

        var result = await _gateway.ExecuteManualActionAsync(checkpoint, action, allowlist, cancellationToken)
            .ConfigureAwait(false);
        return ToOutcome(result, runId, checkpoint.AdventureId);
    }

    private static AdventureFightAction? SelectFightAction(AdventureFightAction fight, AdventureStepSpec step)
    {
        var actionId = step switch
        {
            AttackStepSpec => "attack",
            RunStepSpec => "run",
            SkillStepSpec skill when MatchesFight(fight, skill.MonsterId, skill.Round) => $"skill:{skill.SkillId}",
            ItemStepSpec item when item.Quantity == 1 && MatchesFight(fight, item.MonsterId, item.Round)
                => $"item:{item.ItemId}",
            _ => null
        };
        return actionId is null ? null
            : new AdventureFightAction(fight.AdventureId, actionId, fight.Round, fight.EncounterId, fight.MonsterId);
    }

    private static bool MatchesFight(AdventureFightAction fight, string? monsterId, int? round)
        => (monsterId is null || monsterId == fight.MonsterId) && (round is null || round == fight.Round);

    public static ManualStepOutcome ToOutcome(AdventureExecutionResult result, string runId, string location)
        => new()
        {
            RunId = runId,
            Location = location,
            Status = result.Status,
            IsComplete = result.IsComplete,
            IsAborted = result.IsAborted,
            RequiresRefresh = result.RequiresRefresh,
            Encounter = result.Response?.Encounter ?? ToEncounter(result.Response?.Observation),
            StopReason = result.AbortReason?.ToString() ?? result.Status.ToString(),
            NextAction = result.NextAction,
            Message = result.Messages.Count == 0 ? result.State.AbortMessage : string.Join(" ", result.Messages),
            UnknownPagePath = result.UnknownPagePath,
            RequestId = result.RequestId,
            Drops = result.Response?.Html is { } html ? DropTracker.ReadDrops(html) : Array.Empty<QuestDrop>()
        };

    private static AdventureEncounterState? ToEncounter(AdventureKernelObservation? observation)
        => observation is null ? null : new AdventureEncounterState(
            observation.AdventureId, observation.Kind, observation.MonsterId ?? observation.EncounterId,
            observation.ChoiceId, observation.AvailableChoices, observation.CanContinue,
            observation.UnknownReason is { } reason ? new[] { reason } : Array.Empty<string>(),
            observation.FightRound, observation.ResponseHash) { ChoiceLabels = observation.ChoiceLabels, Combat = observation.Combat };

}
