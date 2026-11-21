using Xunit;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Tests;

public sealed class DailyAdventureSchedulerTests
{
    [Fact]
    public void StaleAdventureStateBlocksPlannerWithoutSteps()
    {
        var plan = new DailyAdventurePlanner().Plan(new AdventureState
        {
            IsStale = true,
            Knucklebones = 20,
            AdventureTurnsRemaining = 10
        });

        Assert.Equal(DailyAdventurePlanStatus.BlockedStaleSnapshot, plan.Status);
        Assert.Empty(plan.Steps);
        Assert.False(plan.IsActionable);
    }

    [Fact]
    public async Task RefreshesStateForEachBoundedReadOnlyTick()
    {
        var reads = 0;
        var scheduler = new DailyAdventurePlanScheduler();

        var ticks = new List<DailyAdventureScheduleTick>();
        await foreach (var tick in scheduler.RunAsync(
            _ => Task.FromResult(new AdventureState
            {
                Knucklebones = reads++ == 0 ? 20 : 100,
                AdventureTurnsRemaining = 10
            }),
            schedulerOptions: new DailyAdventureSchedulerOptions
            {
                MaxIterations = 2,
                Interval = TimeSpan.Zero,
                WorkerLimit = 20
            }))
        {
            ticks.Add(tick);
        }

        Assert.Equal(2, reads);
        Assert.Equal(2, ticks.Count);
        Assert.Equal(20, ticks[0].Plan.StartingKnucklebones);
        Assert.Equal(DailyAdventurePlanStatus.TargetReached, ticks[1].Plan.Status);
        Assert.All(ticks, tick =>
        {
            Assert.True(tick.ReadOnly);
            Assert.False(tick.MutationExecuted);
            Assert.Equal(20, tick.WorkerLimit);
        });
    }

    [Fact]
    public async Task CancellationStopsBeforeAnotherSnapshotRead()
    {
        using var cancellation = new CancellationTokenSource();
        var reads = 0;
        var scheduler = new DailyAdventurePlanScheduler();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in scheduler.RunAsync(
                _ =>
                {
                    reads++;
                    cancellation.Cancel();
                    return Task.FromResult(new AdventureState());
                },
                schedulerOptions: new DailyAdventureSchedulerOptions { MaxIterations = 3 },
                cancellationToken: cancellation.Token))
            {
            }
        });

        Assert.Equal(1, reads);
    }
}
