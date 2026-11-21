using Xunit;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Tests;

public sealed class BoundedAdventureRunnerTests
{
    [Fact]
    public async Task Manual_choice_stops_without_sending_a_choice_or_aborting_its_checkpoint()
    {
        using var transport = new AdventureExecutionFixtureTransport();
        transport.EnqueueObservation(AdventureTransportOperation.Adventure,
            new AdventureKernelObservation
            {
                AdventureId = "skeleton", Kind = AdventureEncounterKind.Choice,
                ChoiceId = "1003", AvailableChoices = new[] { "1", "2" }, ResponseHash = "choice-hash"
            });
        var checkpoints = new InMemoryAdventureRunCheckpointStore();
        await using var gateway = new AdventureExecutionGateway(transport, checkpoints);
        var policy = new AdventureActionAllowlist { WaitForManualChoice = true }.AddAdventure("skeleton");
        var result = await new BoundedAdventureRunner(gateway).RunAsync(
            new AdventureRunRequest { RunId = "manual-choice", AdventureId = "skeleton" }, policy);

        Assert.Equal(BoundedAdventureRunStopReason.AwaitingManualChoice, result.StopReason);
        Assert.Single(transport.Requests);
        var checkpoint = await checkpoints.LoadAsync("manual-choice");
        Assert.Equal(new[] { "1", "2" }, checkpoint!.ManualChoiceOptions);
        Assert.Equal(AdventureRunPhase.Choice, checkpoint.Phase);
    }

    [Fact]
    public async Task DoesNotStartWhenStopConditionIsAlreadySatisfied()
    {
        using var transport = new AdventureExecutionFixtureTransport();
        await using var gateway = new AdventureExecutionGateway(transport, new InMemoryAdventureRunCheckpointStore());
        var runner = new BoundedAdventureRunner(gateway);
        var provider = new StaticConditionProvider(new AdventureConditionSnapshot
        {
            Items = new Dictionary<string, int> { ["knucklebone"] = 100 }
        });

        var result = await runner.RunAsync(
            new AdventureRunRequest { RunId = "condition-stop", AdventureId = "skeleton" },
            new AdventureActionAllowlist().AddAdventure("skeleton"),
            new[] { new AdventureCondition { Kind = AdventureConditionKind.ItemCount, Key = "knucklebone", Target = 100 } },
            provider);

        Assert.Equal(BoundedAdventureRunStopReason.ConditionsSatisfied, result.StopReason);
        Assert.Empty(result.Steps);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task StopsAtGatewayStepBound()
    {
        using var transport = new AdventureExecutionFixtureTransport();
        transport.EnqueueObservation(AdventureTransportOperation.Adventure,
            new AdventureKernelObservation { AdventureId = "skeleton", Kind = AdventureEncounterKind.Unknown, ResponseHash = "unknown" });
        await using var gateway = new AdventureExecutionGateway(transport, new InMemoryAdventureRunCheckpointStore());
        var runner = new BoundedAdventureRunner(gateway);

        var result = await runner.RunAsync(
            new AdventureRunRequest { RunId = "bounded", AdventureId = "skeleton" },
            new AdventureActionAllowlist().AddAdventure("skeleton"),
            options: new BoundedAdventureRunnerOptions { MaxGatewaySteps = 1 });

        Assert.Equal(BoundedAdventureRunStopReason.GatewayStopped, result.StopReason);
        Assert.Single(result.Steps);
        Assert.True(result.LastStep!.IsAborted);
    }

    [Fact]
    public async Task ConditionsRequireFreshSnapshotProvider()
    {
        using var transport = new AdventureExecutionFixtureTransport();
        await using var gateway = new AdventureExecutionGateway(transport, new InMemoryAdventureRunCheckpointStore());
        var runner = new BoundedAdventureRunner(gateway);

        var result = await runner.RunAsync(
            new AdventureRunRequest { RunId = "no-snapshot", AdventureId = "skeleton" },
            new AdventureActionAllowlist().AddAdventure("skeleton"),
            new[] { new AdventureCondition { Kind = AdventureConditionKind.TurnsRemaining, Target = 1 } });

        Assert.Equal(BoundedAdventureRunStopReason.ConditionsBlocked, result.StopReason);
        Assert.Empty(transport.Requests);
        Assert.Contains("fresh condition snapshot", result.Conditions[0].Reason);
    }

    [Fact]
    public async Task RunsAllowlistedAdventureFightAndRewardToCompletion()
    {
        using var transport = new AdventureExecutionFixtureTransport();
        transport.EnqueueObservation(AdventureTransportOperation.Adventure,
            new AdventureKernelObservation
            {
                AdventureId = "skeleton",
                Kind = AdventureEncounterKind.Fight,
                EncounterId = "encounter-1",
                MonsterId = "skeleton",
                FightRound = 1,
                ResponseHash = "start-hash"
            });
        transport.EnqueueObservation(AdventureTransportOperation.Fight,
            new AdventureKernelObservation
            {
                AdventureId = "skeleton",
                Kind = AdventureEncounterKind.Reward,
                ResponseHash = "reward-hash"
            });

        await using var gateway = new AdventureExecutionGateway(transport, new InMemoryAdventureRunCheckpointStore());
        var allowlist = new AdventureActionAllowlist()
            .AddAdventure("skeleton")
            .AddFightAction("skeleton", "attack", "encounter-1", 1, "skeleton");
        var result = await new BoundedAdventureRunner(gateway).RunAsync(
            new AdventureRunRequest { RunId = "complete", AdventureId = "skeleton" },
            allowlist,
            options: new BoundedAdventureRunnerOptions { MaxGatewaySteps = 3 });

        Assert.Equal(BoundedAdventureRunStopReason.Complete, result.StopReason);
        Assert.Equal(2, result.Steps.Count);
        Assert.Equal(1, transport.Count(AdventureTransportOperation.Adventure));
        Assert.Equal(1, transport.Count(AdventureTransportOperation.Fight));
        Assert.True(result.LastStep!.IsComplete);
    }

    private sealed class StaticConditionProvider(AdventureConditionSnapshot snapshot) : IAdventureConditionSnapshotProvider
    {
        public ValueTask<AdventureConditionSnapshot> ReadAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(snapshot);
    }
}
