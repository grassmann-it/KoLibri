namespace KolArbitrage.Core.Adventure;

/// <summary>Boundaries for one foreground or scheduled adventure run.</summary>
public sealed record BoundedAdventureRunnerOptions
{
    public int MaxGatewaySteps { get; init; } = 20;
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(5);

    public BoundedAdventureRunnerOptions Normalize()
        => this with
        {
            MaxGatewaySteps = Math.Clamp(MaxGatewaySteps, 1, 10_000),
            MaxDuration = MaxDuration <= TimeSpan.Zero ? TimeSpan.FromMinutes(5) : MaxDuration
        };
}

public interface IAdventureConditionSnapshotProvider
{
    ValueTask<AdventureConditionSnapshot> ReadAsync(CancellationToken cancellationToken = default);
}

public enum BoundedAdventureRunStopReason
{
    Complete,
    ConditionsSatisfied,
    ConditionsBlocked,
    StepLimitReached,
    DeadlineReached,
    GatewayStopped
}

public sealed record BoundedAdventureRunResult
{
    public required BoundedAdventureRunStopReason StopReason { get; init; }
    public required IReadOnlyList<AdventureExecutionResult> Steps { get; init; }
    public IReadOnlyList<AdventureConditionResult> Conditions { get; init; }
        = Array.Empty<AdventureConditionResult>();

    public AdventureExecutionResult? LastStep => Steps.Count == 0 ? null : Steps[^1];
    public bool IsSafeStop => StopReason is not BoundedAdventureRunStopReason.GatewayStopped;
}

/// <summary>
/// Bounded coordinator for a daily run. It never invents actions: all network
/// work is delegated to <see cref="AdventureExecutionGateway"/> and therefore
/// retains its approval, correlation, timeout and checkpoint guarantees.
/// </summary>
public sealed class BoundedAdventureRunner
{
    private readonly AdventureExecutionGateway _gateway;
    private readonly TimeProvider _clock;

    public BoundedAdventureRunner(AdventureExecutionGateway gateway, TimeProvider? clock = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<BoundedAdventureRunResult> RunAsync(
        AdventureRunRequest request,
        AdventureActionAllowlist allowlist,
        IEnumerable<AdventureCondition>? stopWhenSatisfied = null,
        IAdventureConditionSnapshotProvider? snapshotProvider = null,
        BoundedAdventureRunnerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(allowlist);
        var settings = (options ?? new BoundedAdventureRunnerOptions()).Normalize();
        var conditions = (stopWhenSatisfied ?? Array.Empty<AdventureCondition>()).Select(x => x.Normalize()).ToArray();
        var steps = new List<AdventureExecutionResult>();
        var started = _clock.GetUtcNow();

        var initial = await EvaluateAsync(conditions, snapshotProvider, cancellationToken).ConfigureAwait(false);
        if (initial.Count > 0 && initial.All(x => x.IsSatisfied))
            return Result(BoundedAdventureRunStopReason.ConditionsSatisfied, steps, initial);
        if (initial.Count > 0 && snapshotProvider is null)
            return Result(BoundedAdventureRunStopReason.ConditionsBlocked, steps, initial);

        var current = await _gateway.StartAsync(request, allowlist, cancellationToken).ConfigureAwait(false);
        steps.Add(current);
        while (true)
        {
            if (current.IsComplete)
                return Result(BoundedAdventureRunStopReason.Complete, steps, await EvaluateAsync(conditions, snapshotProvider, cancellationToken).ConfigureAwait(false));
            if (current.IsAborted || current.RequiresRefresh || current.IsIndeterminate)
                return Result(BoundedAdventureRunStopReason.GatewayStopped, steps, await EvaluateAsync(conditions, snapshotProvider, cancellationToken).ConfigureAwait(false));
            if (steps.Count >= settings.MaxGatewaySteps)
                return Result(BoundedAdventureRunStopReason.StepLimitReached, steps, await EvaluateAsync(conditions, snapshotProvider, cancellationToken).ConfigureAwait(false));
            if (_clock.GetUtcNow() - started >= settings.MaxDuration)
                return Result(BoundedAdventureRunStopReason.DeadlineReached, steps, await EvaluateAsync(conditions, snapshotProvider, cancellationToken).ConfigureAwait(false));

            var observed = await EvaluateAsync(conditions, snapshotProvider, cancellationToken).ConfigureAwait(false);
            if (observed.Count > 0 && observed.All(x => x.IsSatisfied))
                return Result(BoundedAdventureRunStopReason.ConditionsSatisfied, steps, observed);

            current = await _gateway.ExecutePendingAsync(request.RunId, allowlist, cancellationToken).ConfigureAwait(false);
            steps.Add(current);
        }
    }

    private async ValueTask<IReadOnlyList<AdventureConditionResult>> EvaluateAsync(
        IReadOnlyList<AdventureCondition> conditions,
        IAdventureConditionSnapshotProvider? provider,
        CancellationToken cancellationToken)
    {
        if (conditions.Count == 0)
            return Array.Empty<AdventureConditionResult>();
        if (provider is null)
        {
            return conditions.Select(condition => new AdventureConditionResult
            {
                Condition = condition,
                Reason = "A fresh condition snapshot is required before an adventure run can start.",
                IsSatisfied = false
            }).ToArray();
        }
        var snapshot = await provider.ReadAsync(cancellationToken).ConfigureAwait(false);
        return AdventureConditionEvaluator.EvaluateAll(conditions, snapshot);
    }

    private static BoundedAdventureRunResult Result(
        BoundedAdventureRunStopReason reason,
        IReadOnlyList<AdventureExecutionResult> steps,
        IReadOnlyList<AdventureConditionResult> conditions)
        => new() { StopReason = reason, Steps = steps.ToArray(), Conditions = conditions.ToArray() };
}
