namespace KolArbitrage.Core.Spiel;

/// <summary>Bounds for executing one already-approved daily plan.</summary>
public sealed record DailyAdventurePlanExecutionOptions
{
    public int MaxSteps { get; init; } = 20;
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(5);

    public DailyAdventurePlanExecutionOptions Normalize()
        => this with
        {
            MaxSteps = Math.Clamp(MaxSteps, 1, 1_000),
            MaxDuration = MaxDuration <= TimeSpan.Zero ? TimeSpan.FromMinutes(5) : MaxDuration
        };
}

public enum DailyAdventurePlanExecutionStopReason
{
    Completed,
    PlanBlocked,
    StepLimitReached,
    DeadlineReached,
    StepFailed,
    Canceled
}

public sealed record DailyAdventureStepExecutionResult
{
    public required AdventureStep Step { get; init; }
    public required bool Succeeded { get; init; }
    public string? Message { get; init; }
    public string? RunId { get; init; }
}

public sealed record DailyAdventurePlanExecutionResult
{
    public required DailyAdventurePlanExecutionStopReason StopReason { get; init; }
    public required IReadOnlyList<DailyAdventureStepExecutionResult> Steps { get; init; }
    public bool IsSafeStop => StopReason is not DailyAdventurePlanExecutionStopReason.StepFailed;
    public DailyAdventureStepExecutionResult? LastStep => Steps.Count == 0 ? null : Steps[^1];
}

/// <summary>
/// Executes a planner result without owning transport or approvals. The
/// callback is the only mutation boundary and should delegate to the existing
/// approval-bound AdventureExecutionGateway. This keeps CLI, MCP and Desktop
/// on one deterministic bounded contract.
/// </summary>
public sealed class DailyAdventurePlanExecutor
{
    private readonly TimeProvider _clock;

    public DailyAdventurePlanExecutor(TimeProvider? clock = null)
        => _clock = clock ?? TimeProvider.System;

    public async Task<DailyAdventurePlanExecutionResult> ExecuteAsync(
        DailyAdventurePlan plan,
        Func<AdventureStep, CancellationToken, Task<DailyAdventureStepExecutionResult>> executeStep,
        DailyAdventurePlanExecutionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(executeStep);
        var settings = (options ?? new DailyAdventurePlanExecutionOptions()).Normalize();
        var steps = new List<DailyAdventureStepExecutionResult>();
        if (plan.Status is DailyAdventurePlanStatus.BlockedStaleSnapshot
            or DailyAdventurePlanStatus.BlockedNoYield
            or DailyAdventurePlanStatus.BlockedNoAdventureTurns)
        {
            return new DailyAdventurePlanExecutionResult
            {
                StopReason = DailyAdventurePlanExecutionStopReason.PlanBlocked,
                Steps = steps
            };
        }

        var started = _clock.GetUtcNow();
        foreach (var step in plan.Steps.Take(settings.MaxSteps))
        {
            if (cancellationToken.IsCancellationRequested)
                return new DailyAdventurePlanExecutionResult { StopReason = DailyAdventurePlanExecutionStopReason.Canceled, Steps = steps };
            if (_clock.GetUtcNow() - started >= settings.MaxDuration)
                return new DailyAdventurePlanExecutionResult { StopReason = DailyAdventurePlanExecutionStopReason.DeadlineReached, Steps = steps };

            DailyAdventureStepExecutionResult result;
            try
            {
                result = await executeStep(step, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new DailyAdventurePlanExecutionResult { StopReason = DailyAdventurePlanExecutionStopReason.Canceled, Steps = steps };
            }
            steps.Add(result with { Step = step });
            if (!result.Succeeded)
                return new DailyAdventurePlanExecutionResult { StopReason = DailyAdventurePlanExecutionStopReason.StepFailed, Steps = steps };
        }

        var stop = plan.Steps.Count > settings.MaxSteps
            ? DailyAdventurePlanExecutionStopReason.StepLimitReached
            : DailyAdventurePlanExecutionStopReason.Completed;
        return new DailyAdventurePlanExecutionResult { StopReason = stop, Steps = steps };
    }
}
