using KolArbitrage.Core.Adventure;
using KolArbitrage.Core.Accounts;

namespace KolArbitrage.Core.Data;

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
    private readonly string _approvalToken;

    public AdventureManualDriver(AccountRole role, AdventureExecutionGateway gateway, string approvalToken)
    {
        _role = role;
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _approvalToken = approvalToken ?? throw new ArgumentNullException(nameof(approvalToken));
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

        var allowlistWithStep = allowlist;
        switch (step)
        {
            case SkillStepSpec skill:
                allowlistWithStep = allowlist.AddFightAction(
                    checkpoint.AdventureId,
                    $"skill:{skill.SkillId}",
                    encounterId: null,
                    monsterId: skill.MonsterId,
                    round: skill.Round);
                break;
            case ItemStepSpec item:
                allowlistWithStep = allowlist.AddFightAction(
                    checkpoint.AdventureId,
                    $"item:{item.ItemId}",
                    encounterId: null,
                    monsterId: item.MonsterId,
                    round: item.Round);
                break;
            case ChoiceStepSpec choice:
                allowlistWithStep = allowlist.AddChoiceOption(
                    checkpoint.AdventureId, choice.ChoiceId, choice.OptionId);
                break;
            case AttackStepSpec:
                allowlistWithStep = EnsureFightAction(allowlist, checkpoint, "attack");
                break;
            case RunStepSpec:
                allowlistWithStep = EnsureFightAction(allowlist, checkpoint, "run");
                break;
        }

        await Task.CompletedTask; // keep async signature for future awaiters
        var result = await _gateway.ExecutePendingAsync(runId, allowlistWithStep, cancellationToken).ConfigureAwait(false);
        return ToOutcome(result, runId, checkpoint.AdventureId);
    }

    private static AdventureActionAllowlist EnsureFightAction(
        AdventureActionAllowlist allowlist,
        AdventureCheckpoint checkpoint,
        string actionId)
    {
        if (allowlist.TryGetFightAction(checkpoint.AdventureId, null, 0, out _))
            return allowlist;
        return allowlist.AddFightAction(checkpoint.AdventureId, actionId);
    }

    private static ManualStepOutcome ToOutcome(AdventureExecutionResult result, string runId, string location)
        => new()
        {
            RunId = runId,
            Location = location,
            Status = result.Status,
            IsComplete = result.IsComplete,
            IsAborted = result.IsAborted,
            RequiresRefresh = result.RequiresRefresh,
            Encounter = result.Response?.Encounter,
            StopReason = result.AbortReason?.ToString() ?? result.Status.ToString(),
            NextAction = result.NextAction,
            Message = result.Messages.Count == 0 ? null : string.Join(" ", result.Messages),
            RequestId = result.RequestId
        };
}
