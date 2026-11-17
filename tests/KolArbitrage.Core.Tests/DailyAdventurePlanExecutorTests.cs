using KolArbitrage.Core.Adventure;
using Xunit;

namespace KolArbitrage.Core.Tests;

public sealed class DailyAdventurePlanExecutorTests
{
    [Fact]
    public async Task Blocked_plan_never_calls_step_executor()
    {
        var plan = new DailyAdventurePlan
        {
            PlanDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Status = DailyAdventurePlanStatus.BlockedStaleSnapshot,
            TargetKnucklebones = 100,
            StartingKnucklebones = 0,
            ProjectedKnucklebones = 0,
            Advisories = Array.Empty<string>(),
            Steps = new[] { new AdventureStep(1, AdventureActionKind.RunAdventure, 1, 1, "stale") }
        };
        var calls = 0;
        var result = await new DailyAdventurePlanExecutor().ExecuteAsync(
            plan,
            (step, _) =>
            {
                calls++;
                return Task.FromResult(new DailyAdventureStepExecutionResult { Step = step, Succeeded = true });
            });

        Assert.Equal(DailyAdventurePlanExecutionStopReason.PlanBlocked, result.StopReason);
        Assert.Empty(result.Steps);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Executor_stops_on_failed_step_and_never_runs_following_steps()
    {
        var plan = new DailyAdventurePlan
        {
            PlanDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Status = DailyAdventurePlanStatus.PartiallyReady,
            TargetKnucklebones = 100,
            StartingKnucklebones = 0,
            ProjectedKnucklebones = 1,
            Advisories = Array.Empty<string>(),
            Steps = new[]
            {
                new AdventureStep(1, AdventureActionKind.RunAdventure, 1, 1, "first"),
                new AdventureStep(2, AdventureActionKind.RunAdventure, 1, 1, "second")
            }
        };
        var calls = 0;
        var result = await new DailyAdventurePlanExecutor().ExecuteAsync(
            plan,
            (step, _) =>
            {
                calls++;
                return Task.FromResult(new DailyAdventureStepExecutionResult { Step = step, Succeeded = false, Message = "gateway stopped" });
            });

        Assert.Equal(DailyAdventurePlanExecutionStopReason.StepFailed, result.StopReason);
        Assert.Single(result.Steps);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Executor_enforces_step_bound()
    {
        var plan = new DailyAdventurePlan
        {
            PlanDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Status = DailyAdventurePlanStatus.PartiallyReady,
            TargetKnucklebones = 100,
            StartingKnucklebones = 0,
            ProjectedKnucklebones = 3,
            Advisories = Array.Empty<string>(),
            Steps = Enumerable.Range(1, 3)
                .Select(i => new AdventureStep(i, AdventureActionKind.RunAdventure, 1, 1, i.ToString()))
                .ToArray()
        };

        var result = await new DailyAdventurePlanExecutor().ExecuteAsync(
            plan,
            (step, _) => Task.FromResult(new DailyAdventureStepExecutionResult { Step = step, Succeeded = true }),
            new DailyAdventurePlanExecutionOptions { MaxSteps = 2 });

        Assert.Equal(DailyAdventurePlanExecutionStopReason.StepLimitReached, result.StopReason);
        Assert.Equal(2, result.Steps.Count);
    }
}
