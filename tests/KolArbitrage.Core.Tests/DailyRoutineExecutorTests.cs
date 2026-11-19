using KolArbitrage.Core.Accounts;
using KolArbitrage.Core.Adventure;
using KolArbitrage.Core.Daily;
using KolArbitrage.Core.Integration;
using Xunit;

namespace KolArbitrage.Core.Tests;

public sealed class DailyRoutineExecutorTests
{
    [Fact]
    public async Task Executor_without_token_returns_no_op_result()
    {
        var snapshot = new DailyRoutineSnapshot { CampfireLeaves = 7, MainMeat = 1_000_000 };
        var report = new DailyRoutineRunner().Run(snapshot);
        var transport = new AdventureExecutionFixtureTransport();
        var store = new InMemoryAdventureRunCheckpointStore();
        var executor = new DailyRoutineExecutor(
            gatewayFactory: _ => new AdventureExecutionGateway(transport, store),
            approvalToken: null);
        var result = await executor.ExecuteAsync(AccountRole.Main, report);
        Assert.False(result.Executed);
        Assert.Empty(result.Steps);
        Assert.Equal(0, transport.RequestCount);
    }

    [Fact]
    public async Task Executor_with_token_drives_bounded_runner_against_mock_transport()
    {
        // Mock transport: the first choice POST returns a fresh choice page,
        // the second POST completes. The executor maps the campfire step
        // to "choice-campfire:1" and walks the bounded runner.
        var transport = new AdventureExecutionFixtureTransport();
        transport.EnqueueObservation(AdventureTransportOperation.Choice,
            new AdventureKernelObservation
            {
                AdventureId = "choice-campfire",
                Kind = AdventureEncounterKind.Choice,
                EncounterId = "choice-campfire",
                ChoiceId = "choice-campfire",
                AvailableChoices = new[] { "1" },
                CanContinue = true,
                ResponseHash = "burn1"
            });
        transport.EnqueueObservation(AdventureTransportOperation.Choice,
            new AdventureKernelObservation
            {
                AdventureId = "choice-campfire",
                Kind = AdventureEncounterKind.Choice,
                EncounterId = "choice-campfire",
                ChoiceId = "choice-campfire",
                AvailableChoices = new[] { "2" },
                CanContinue = true,
                ResponseHash = "burn2"
            });

        var store = new InMemoryAdventureRunCheckpointStore();
        var executor = new DailyRoutineExecutor(
            gatewayFactory: _ => new AdventureExecutionGateway(transport, store),
            approvalToken: "allow-token");

        var snapshot = new DailyRoutineSnapshot
        {
            CampfireLeaves = 10,
            CampfireReserve = 3,
            MainMeat = 1_000_000,
            DayShortnerPrice = 50_000
        };
        var report = new DailyRoutineRunner().Run(snapshot);

        var result = await executor.ExecuteAsync(AccountRole.Main, report,
            new BoundedChoiceChainOptions { MaxChoices = 2, MaxDuration = TimeSpan.FromSeconds(5) });

        Assert.True(result.Executed);
        Assert.NotEmpty(result.Steps);
        var burnStep = result.Steps.First(s => s.StepKind.Contains("burn", StringComparison.OrdinalIgnoreCase));
        var allSteps = string.Join(", ", result.Steps.Select(s => $"{s.StepKind}@{s.Location}:{s.StopReason}"));
        Assert.Equal("choice-campfire", burnStep.Location);
    }
}
