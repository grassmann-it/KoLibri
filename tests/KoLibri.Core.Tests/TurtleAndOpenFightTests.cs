using System.Text.Json;
using Xunit;
using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Tests;

public sealed class TurtleAndOpenFightTests
{
    [Theory]
    [InlineData("O Turtle Were Art Thou", true)]
    [InlineData("Duel Nature", true)]
    [InlineData("Training Day", true)]
    [InlineData("Testudinal tidings", true)]
    [InlineData("The Hidden Temple", false)]
    [InlineData(null, false)]
    public void Turtle_titles_are_recognised(string? title, bool expected)
        => Assert.Equal(expected, TurtleTamerEncounters.IsTurtleEncounter(title));

    [Fact]
    public void Unanswered_turtle_choice_hands_the_decision_to_the_user_with_a_clear_reason()
    {
        var resolver = new ChoiceResolver();
        var resolution = resolver.Resolve(new ChoiceResolutionRequest("a", "777", "O Turtle Were Art Thou",
            new[] { new ChoiceOptionInfo(1, "Help"), new ChoiceOptionInfo(2, "Leave") }));
        Assert.Null(resolution.Option);
        Assert.Contains("Turtle Tamer random encounter", resolution.Reason);
        Assert.Contains("choice policy set --choice 777", resolution.Reason);
    }

    [Fact]
    public void Configured_default_still_answers_a_turtle_choice()
    {
        var config = new ChoiceResolutionConfig
        {
            Choices = { ["777"] = new ChoiceDefaultEntry { Option = 2 } }
        };
        var resolution = new ChoiceResolver(config).Resolve(new ChoiceResolutionRequest("a", "777", "Duel Nature",
            new[] { new ChoiceOptionInfo(1, "Fight"), new ChoiceOptionInfo(2, "Leave") }));
        Assert.Equal(2, resolution.Option);
    }

    [Fact]
    public async Task Turtle_choice_without_answer_stops_with_needs_decision_not_an_abort()
    {
        using var transport = new AdventureExecutionFixtureTransport();
        transport.EnqueueObservation(AdventureTransportOperation.Adventure, new AdventureKernelObservation
        {
            AdventureId = "town", Kind = AdventureEncounterKind.Choice, EncounterId = "town",
            ChoiceId = "777", ChoiceTitle = "O Turtle Were Art Thou",
            AvailableChoices = new[] { "1", "2" }, CanContinue = true, ResponseHash = "t1"
        });
        await using var gateway = new AdventureExecutionGateway(transport, new InMemoryAdventureRunCheckpointStore());
        var allowlist = new AdventureActionAllowlist { ChoiceResolver = new ChoiceResolver() }.AddAdventure("town");

        var result = await new BoundedAdventureRunner(gateway).RunAsync(
            new AdventureRunRequest { RunId = "turtle", AdventureId = "town" }, allowlist);

        Assert.Equal(BoundedAdventureRunStopReason.NeedsDecision, result.StopReason);
        Assert.Equal("777", result.NeedsDecision?.ChoiceId);
        Assert.Contains("Turtle Tamer", result.NeedsDecision?.Reason);
    }

    [Theory]
    [InlineData("FightRoundLimitReached", "round limit")]
    [InlineData("HpBelowMinimum", "HP is below")]
    public void A_run_that_stops_mid_fight_names_the_open_fight_and_the_finishing_command(string reason, string expected)
    {
        var doc = KolibriCli.FightStopDocument(reason, false, new[] { "--location", "adventure=439:The Skeleton Store" },
            "adventure=439:The Skeleton Store", KoLibri.Cli.AccountRole.Main, 439);
        Assert.NotNull(doc);
        var json = JsonSerializer.Serialize(doc);
        Assert.Contains(expected, json);
        Assert.Contains("--fight-action attack", json);
        Assert.Contains("--snarfblat 439", json);
    }

    [Fact]
    public void A_completed_run_reports_no_open_fight()
        => Assert.Null(KolibriCli.FightStopDocument("Complete", false, Array.Empty<string>(), "x", KoLibri.Cli.AccountRole.Main, null));
}
