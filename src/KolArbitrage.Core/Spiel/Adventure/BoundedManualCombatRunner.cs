namespace KolArbitrage.Core.Spiel;

public sealed record BoundedManualCombatRunnerOptions
{
    public int MaxActions { get; init; } = 20;
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(5);

    public BoundedManualCombatRunnerOptions Normalize()
        => this with
        {
            MaxActions = Math.Clamp(MaxActions, 1, 1_000),
            MaxDuration = MaxDuration <= TimeSpan.Zero ? TimeSpan.FromMinutes(5) : MaxDuration
        };
}

public enum BoundedManualCombatStopReason
{
    Complete,
    NoAction,
    StepLimitReached,
    DeadlineReached,
    RecoveryBlocked,
    PreviewRejected,
    GatewayStopped,
    InspectionBlocked
}

public sealed record BoundedManualCombatResult
{
    public required BoundedManualCombatStopReason StopReason { get; init; }
    public IReadOnlyList<ManualCombatExecutionResult> Steps { get; init; }
        = Array.Empty<ManualCombatExecutionResult>();
    public ManualCombatObservation? LastObservation { get; init; }
    public RecoveryPlan? RecoveryPlan { get; init; }
    public ManualCombatExecutionResult? LastStep => Steps.Count == 0 ? null : Steps[^1];
    public bool IsSafeStop => StopReason is not BoundedManualCombatStopReason.GatewayStopped;
}

/// <summary>
/// Bounded multi-round coordinator over the single-step manual gateway. The
/// selector is pure caller policy; every action still passes through fresh
/// inspection, typed preview, approval, checkpoint and post verification.
/// </summary>
public sealed class BoundedManualCombatRunner
{
    private readonly ManualCombatGateway _gateway;
    private readonly TimeProvider _clock;

    public BoundedManualCombatRunner(ManualCombatGateway gateway, TimeProvider? clock = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<BoundedManualCombatResult> RunAsync(
        string combatId,
        Func<ManualCombatObservation, CancellationToken, ValueTask<ManualCombatAction?>> selectAction,
        string? approvalToken = null,
        BoundedManualCombatRunnerOptions? options = null,
        CancellationToken cancellationToken = default,
        Func<ManualCombatObservation, CancellationToken, ValueTask<RecoveryPlan?>>? recoveryProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(combatId);
        ArgumentNullException.ThrowIfNull(selectAction);
        var settings = (options ?? new BoundedManualCombatRunnerOptions()).Normalize();
        var started = _clock.GetUtcNow();
        var steps = new List<ManualCombatExecutionResult>();
        var inspection = await _gateway.InspectAsync(combatId.Trim(), cancellationToken).ConfigureAwait(false);
        var observation = inspection.Observation;
        if (observation is null)
            return new BoundedManualCombatResult
            {
                StopReason = BoundedManualCombatStopReason.InspectionBlocked,
                Steps = steps,
                LastObservation = null
            };
        if (observation.IsTerminal)
            return new BoundedManualCombatResult
            {
                StopReason = BoundedManualCombatStopReason.Complete,
                Steps = steps,
                LastObservation = observation
            };

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (steps.Count >= settings.MaxActions)
                return Result(BoundedManualCombatStopReason.StepLimitReached, steps, observation);
            if (_clock.GetUtcNow() - started >= settings.MaxDuration)
                return Result(BoundedManualCombatStopReason.DeadlineReached, steps, observation);

            RecoveryPlan? recoveryPlan = null;
            if (recoveryProvider is not null)
            {
                recoveryPlan = await recoveryProvider(observation, cancellationToken).ConfigureAwait(false);
                if (recoveryPlan is not null && !recoveryPlan.CanEnterCombat)
                    return Result(BoundedManualCombatStopReason.RecoveryBlocked, steps, observation, recoveryPlan);
            }

            var action = await selectAction(observation, cancellationToken).ConfigureAwait(false);
            if (action is null)
                return Result(BoundedManualCombatStopReason.NoAction, steps, observation);

            var preview = await _gateway.PreviewAsync(new ManualCombatRequest
            {
                CombatId = observation.CombatId,
                AdventureId = observation.AdventureId,
                Action = action,
                ExpectedRound = observation.Round,
                ExpectedResponseHash = observation.ResponseHash ?? string.Empty
            }, cancellationToken).ConfigureAwait(false);
            if (!preview.IsReady)
                return Result(BoundedManualCombatStopReason.PreviewRejected, steps, preview.Observation, recoveryPlan);

            var execution = await _gateway.ExecuteAsync(preview, approvalToken, cancellationToken).ConfigureAwait(false);
            steps.Add(execution);
            if (execution.IsAborted || execution.IsIndeterminate || execution.RequiresRefresh)
                return Result(BoundedManualCombatStopReason.GatewayStopped, steps, execution.After ?? observation, recoveryPlan);
            if (execution.After is { IsTerminal: true } afterTerminal)
                return Result(BoundedManualCombatStopReason.Complete, steps, afterTerminal, recoveryPlan);

            var refreshed = await _gateway.RefreshAsync(combatId.Trim(), cancellationToken).ConfigureAwait(false);
            if (refreshed.Observation is null)
                return Result(BoundedManualCombatStopReason.InspectionBlocked, steps, execution.After ?? observation, recoveryPlan);
            observation = refreshed.Observation;
            if (observation.IsTerminal)
                return Result(BoundedManualCombatStopReason.Complete, steps, observation, recoveryPlan);
        }
    }

    private static BoundedManualCombatResult Result(
        BoundedManualCombatStopReason reason,
        IReadOnlyList<ManualCombatExecutionResult> steps,
        ManualCombatObservation? observation,
        RecoveryPlan? recoveryPlan = null)
        => new()
        {
            StopReason = reason,
            Steps = steps.ToArray(),
            LastObservation = observation,
            RecoveryPlan = recoveryPlan
        };
}
