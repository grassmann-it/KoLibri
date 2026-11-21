namespace KoLibri.Core.Spiel;

/// <summary>Bounds for a read-only recurring daily adventure-plan pass.</summary>
public sealed record DailyAdventureSchedulerOptions
{
    public int MaxIterations { get; init; } = 1;
    public TimeSpan Interval { get; init; } = TimeSpan.Zero;
    public int WorkerLimit { get; init; } = 1;

    public DailyAdventureSchedulerOptions Normalize()
        => this with
        {
            MaxIterations = Math.Clamp(MaxIterations, 1, 1_000),
            Interval = Interval < TimeSpan.Zero ? TimeSpan.Zero : Interval,
            WorkerLimit = Math.Clamp(WorkerLimit, 1, 20)
        };
}

public sealed record DailyAdventureScheduleTick
{
    public required int Iteration { get; init; }
    public required DateTimeOffset ObservedAt { get; init; }
    public required DailyAdventurePlan Plan { get; init; }
    public DateTimeOffset? NextDueAt { get; init; }
    public int WorkerLimit { get; init; } = 1;
    public bool ReadOnly { get; init; } = true;
    public bool MutationExecuted { get; init; }
}

/// <summary>
/// Bounded recurring planner. It refreshes state before every tick, emits a
/// deterministic daily plan, and never starts an adventure or spends an item.
/// A host can hand each tick to the existing approval-bound runner.
/// </summary>
public sealed class DailyAdventurePlanScheduler
{
    private readonly DailyAdventurePlanner _planner;
    private readonly TimeProvider _clock;

    public DailyAdventurePlanScheduler(
        DailyAdventurePlanner? planner = null,
        TimeProvider? clock = null)
    {
        _planner = planner ?? new DailyAdventurePlanner();
        _clock = clock ?? TimeProvider.System;
    }

    public async IAsyncEnumerable<DailyAdventureScheduleTick> RunAsync(
        Func<CancellationToken, Task<AdventureState>> readState,
        AdventurePlannerOptions? plannerOptions = null,
        DailyAdventureSchedulerOptions? schedulerOptions = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readState);
        var options = (schedulerOptions ?? new DailyAdventureSchedulerOptions()).Normalize();

        for (var iteration = 1; iteration <= options.MaxIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observedAt = _clock.GetUtcNow();
            var state = (await readState(cancellationToken).ConfigureAwait(false)).Normalize();
            var plan = _planner.Plan(
                state,
                DateOnly.FromDateTime(observedAt.DateTime),
                plannerOptions);
            var nextDue = iteration < options.MaxIterations && options.Interval > TimeSpan.Zero
                ? observedAt + options.Interval
                : (DateTimeOffset?)null;

            yield return new DailyAdventureScheduleTick
            {
                Iteration = iteration,
                ObservedAt = observedAt,
                Plan = plan,
                NextDueAt = nextDue,
                WorkerLimit = options.WorkerLimit,
                ReadOnly = true,
                MutationExecuted = false
            };

            if (nextDue is not null)
                await Task.Delay(options.Interval, _clock, cancellationToken).ConfigureAwait(false);
        }
    }
}
