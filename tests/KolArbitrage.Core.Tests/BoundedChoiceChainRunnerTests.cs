using Xunit;
using KolArbitrage.Core.Spiel;

namespace KolArbitrage.Core.Tests;

public sealed class BoundedChoiceChainRunnerTests
{
    [Fact]
    public async Task Continues_two_allowlisted_choices_and_stops_at_reward()
    {
        var transport = new AdventureExecutionFixtureTransport();
        transport.EnqueueObservation(AdventureTransportOperation.Adventure, Choice("choice-1", "1"));
        transport.EnqueueObservation(AdventureTransportOperation.Choice, Choice("choice-2", "2"));
        transport.EnqueueObservation(AdventureTransportOperation.Choice, new AdventureKernelObservation
        {
            AdventureId = "skeleton", Kind = AdventureEncounterKind.Reward, CanContinue = false, ResponseHash = "reward"
        });
        var allowlist = new AdventureActionAllowlist()
            .AddAdventure("skeleton")
            .AddEncounter("skeleton", "skeleton")
            .AddChoiceOption("skeleton", "choice-1", "1", "skeleton")
            .AddChoiceOption("skeleton", "choice-2", "2", "skeleton");
        var store = new InMemoryAdventureRunCheckpointStore();
        await using var gateway = new AdventureExecutionGateway(transport, store);

        var started = await gateway.StartAsync(new AdventureRunRequest
        {
            RunId = "choice-chain",
            AdventureId = "skeleton",
            DailyLimit = new AdventureDailyLimitContract { MaxTurns = 1, MaxRuns = 1 }
        }, allowlist);
        Assert.IsType<AdventureChoiceAction>(started.NextAction);

        var result = await new BoundedChoiceChainRunner(gateway).RunAsync("choice-chain", allowlist);

        Assert.Equal(BoundedChoiceChainStopReason.Complete, result.StopReason);
        Assert.Equal(2, result.Steps.Count);
        Assert.Equal(2, transport.Count(AdventureTransportOperation.Choice));
    }

    [Fact]
    public async Task Stops_before_a_pending_fight()
    {
        var transport = new AdventureExecutionFixtureTransport();
        transport.EnqueueObservation(AdventureTransportOperation.Adventure, new AdventureKernelObservation
        {
            AdventureId = "skeleton", Kind = AdventureEncounterKind.Fight, EncounterId = "skeleton",
            MonsterId = "skeleton", FightRound = 1, CanContinue = true, ResponseHash = "fight"
        });
        var allowlist = new AdventureActionAllowlist()
            .AddAdventure("skeleton")
            .AddEncounter("skeleton", "skeleton")
            .AddFightAction("skeleton", "attack", "skeleton", 1, "skeleton");
        await using var gateway = new AdventureExecutionGateway(transport, new InMemoryAdventureRunCheckpointStore());
        await gateway.StartAsync(new AdventureRunRequest { RunId = "fight-chain", AdventureId = "skeleton" }, allowlist);

        var result = await new BoundedChoiceChainRunner(gateway).RunAsync("fight-chain", allowlist);

        Assert.Equal(BoundedChoiceChainStopReason.NoChoicePending, result.StopReason);
        Assert.Empty(result.Steps);
        Assert.Single(transport.Requests);
    }

    private static AdventureKernelObservation Choice(string choiceId, string option)
        => new()
        {
            AdventureId = "skeleton", Kind = AdventureEncounterKind.Choice, EncounterId = "skeleton",
            ChoiceId = choiceId, AvailableChoices = new[] { option }, CanContinue = true, ResponseHash = choiceId
        };
}
