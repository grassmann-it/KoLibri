using KolArbitrage.Core.Accounts;
using KolArbitrage.Core.Adventure;
using KolArbitrage.Core.Daily;
using KolArbitrage.Core.Integration;
using Xunit;

namespace KolArbitrage.Core.Tests;

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
    public async Task Execute_without_token_returns_no_op_for_every_role()
    {
        var orchestrator = new DailyOrchestrator(
            _ => new AdventureExecutionGateway(
                new AdventureExecutionFixtureTransport(),
                new InMemoryAdventureRunCheckpointStore()),
            approvalToken: null);

        var overrides = new[]
        {
            new RoleOverride(AccountRole.Main, new DailyRoutineSnapshot { CampfireLeaves = 7 })
        };
        var plan = orchestrator.BuildPlan(overrides, new DailyRoutineSnapshot());
        var result = await orchestrator.ExecuteAsync(plan);

        Assert.Equal(0, result.ExecutedRoleCount);
        Assert.Equal(0, result.SucceededRoleCount);
        Assert.All(result.Results, r => Assert.False(r.Execution.Executed));
    }

    [Fact]
    public async Task Execute_with_token_attempts_every_role()
    {
        var transport1 = new AdventureExecutionFixtureTransport();
        var transport2 = new AdventureExecutionFixtureTransport();
        var store1 = new InMemoryAdventureRunCheckpointStore();
        var store2 = new InMemoryAdventureRunCheckpointStore();
        var transports = new Dictionary<AccountRole, AdventureExecutionFixtureTransport>
        {
            [AccountRole.Main] = transport1,
            [AccountRole.Mallbot] = transport2
        };
        var stores = new Dictionary<AccountRole, InMemoryAdventureRunCheckpointStore>
        {
            [AccountRole.Main] = store1,
            [AccountRole.Mallbot] = store2
        };
        var orchestrator = new DailyOrchestrator(
            role => new AdventureExecutionGateway(transports[role], stores[role]),
            approvalToken: "allow-token");

        var overrides = new[]
        {
            new RoleOverride(AccountRole.Main, new DailyRoutineSnapshot { CampfireLeaves = 7 }),
            new RoleOverride(AccountRole.Mallbot, new DailyRoutineSnapshot { CampfireLeaves = 4 })
        };
        var plan = orchestrator.BuildPlan(overrides, new DailyRoutineSnapshot());
        var result = await orchestrator.ExecuteAsync(plan);

        // The bounded runner needs an existing checkpoint to act; with a
        // fresh run id it returns immediately with no steps. The
        // orchestrator still touches both roles and surfaces them in the
        // aggregated result so the operator can audit the attempt.
        Assert.Equal(2, result.ExecutedRoleCount);
        Assert.All(result.Results, r => Assert.Equal(r.Plan.Role, r.Execution.Role));
        Assert.Contains(result.Results, r => r.Plan.Role == AccountRole.Main);
        Assert.Contains(result.Results, r => r.Plan.Role == AccountRole.Mallbot);
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
