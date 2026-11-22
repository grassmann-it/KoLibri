using Xunit;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Tests;

public sealed class BatchChoiceResolverTests
{
    private sealed class FixedResolver(int? option) : IChoiceResolver
    {
        public ChoiceResolution Resolve(ChoiceResolutionRequest request)
            => option is { } o
                ? new ChoiceResolution(o, ChoiceResolutionSource.Configured, "saved answer")
                : ChoiceResolution.None("no answer");
    }

    private static AdventureExecutionFixtureTransport ChoiceThenReward()
    {
        var transport = new AdventureExecutionFixtureTransport();
        transport.EnqueueObservation(AdventureTransportOperation.Adventure, new AdventureKernelObservation
        {
            AdventureId = "skeleton", Kind = AdventureEncounterKind.Choice, EncounterId = "skeleton",
            ChoiceId = "1003", AvailableChoices = new[] { "1", "2" }, CanContinue = true, ResponseHash = "c"
        });
        transport.EnqueueObservation(AdventureTransportOperation.Choice, new AdventureKernelObservation
        {
            AdventureId = "skeleton", Kind = AdventureEncounterKind.Reward, CanContinue = false, ResponseHash = "done"
        });
        return transport;
    }

    [Fact]
    public async Task Saved_answer_is_submitted_in_batch_mode_without_pausing()
    {
        using var transport = ChoiceThenReward();
        await using var gateway = new AdventureExecutionGateway(transport, new InMemoryAdventureRunCheckpointStore());
        var policy = new AdventureActionAllowlist { WaitForManualChoice = true, ChoiceResolver = new FixedResolver(2) }
            .AddAdventure("skeleton");

        var result = await new BoundedAdventureRunner(gateway).RunAsync(
            new AdventureRunRequest { RunId = "b1", AdventureId = "skeleton" }, policy);

        Assert.Equal(BoundedAdventureRunStopReason.Complete, result.StopReason);
        Assert.Equal(1, transport.Count(AdventureTransportOperation.Choice));
        Assert.Equal("2", transport.Requests.Last().Action is AdventureChoiceAction c ? c.OptionId : null);
    }

    [Fact]
    public async Task Missing_answer_pauses_for_the_user_and_sends_nothing()
    {
        using var transport = ChoiceThenReward();
        await using var gateway = new AdventureExecutionGateway(transport, new InMemoryAdventureRunCheckpointStore());
        var policy = new AdventureActionAllowlist { WaitForManualChoice = true, ChoiceResolver = new FixedResolver(null) }
            .AddAdventure("skeleton");

        var result = await new BoundedAdventureRunner(gateway).RunAsync(
            new AdventureRunRequest { RunId = "b2", AdventureId = "skeleton" }, policy);

        Assert.Equal(BoundedAdventureRunStopReason.NeedsDecision, result.StopReason);
        Assert.Equal(0, transport.Count(AdventureTransportOperation.Choice));
    }
}
