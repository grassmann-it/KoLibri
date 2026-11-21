using Xunit;
using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Tests;

public sealed class DailyOrchestratorTests
{
    [Fact]
    public void Build_plan_aggregates_per_role_previews()
    {
        var orchestrator = new DailyOrchestrator(
            _ => new AdventureExecutionGateway(
                new AdventureExecutionFixtureTransport(),
                new InMemoryAdventureRunCheckpointStore()));

        var defaults = new DailyRoutineSnapshot
        {
            CampfireLeaves = 7,
            MainMeat = 1_000_000,
            MallbotMeat = 5_000_000
        };
        var overrides = new[]
        {
            new RoleOverride(AccountRole.Main, new DailyRoutineSnapshot { CampfireLeaves = 10, MainMeat = 2_000_000 }),
            new RoleOverride(AccountRole.Mallbot, new DailyRoutineSnapshot { MallbotMeat = 10_000_000 })
        };
        var report = orchestrator.BuildPlan(overrides, defaults);

        Assert.Equal(2, report.Plans.Count);
        Assert.Equal(AccountRole.Main, report.Plans[0].Role);
        Assert.Equal(10, report.Plans[0].Snapshot.CampfireLeaves);
        Assert.Equal(AccountRole.Mallbot, report.Plans[1].Role);
        Assert.Equal(10_000_000, report.Plans[1].Snapshot.MallbotMeat);
        Assert.True(report.TotalApprovableSteps >= 0);
    }

    [Fact]
    public void MergeSnapshots_uses_override_when_set()
    {
        var orchestrator = new DailyOrchestrator(
            _ => new AdventureExecutionGateway(
                new AdventureExecutionFixtureTransport(),
                new InMemoryAdventureRunCheckpointStore()));
        var defaults = new DailyRoutineSnapshot { CampfireLeaves = 1, MainMeat = 100 };
        var o = new DailyRoutineSnapshot { CampfireLeaves = 9 };
        var overrides = new[] { new RoleOverride(AccountRole.Main, o) };
        var report = orchestrator.BuildPlan(overrides, defaults);

        // Override wins for leaves, defaults fill in meat.
        Assert.Equal(9, report.Plans[0].Snapshot.CampfireLeaves);
        Assert.Equal(100, report.Plans[0].Snapshot.MainMeat);
    }
}
