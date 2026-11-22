using KoLibri.Core.Spiel;
using KoLibri.Desktop;
using Xunit;

namespace KoLibri.Core.Tests;

public sealed class AdventureFailureMapperTests
{
    [Fact]
    public void Open_choice_and_fight_exceptions_become_next_steps_not_302()
    {
        var choice = AdventureFailureMapper.Describe(new OpenChoiceException(OpenChoiceKind.Choice));
        Assert.Equal(AdventureFailureKind.OpenChoice, choice.Kind);
        Assert.DoesNotContain("302", choice.Text);
        Assert.Contains("choice panel", choice.NextStep);
        Assert.Equal(AdventureFailureKind.OpenFight, AdventureFailureMapper.Describe(new OpenChoiceException(OpenChoiceKind.Fight)).Kind);
    }

    [Theory]
    [InlineData("KoL adventure redirects are disabled; refresh the session manually. (HTTP 302 to 'choice.php')", AdventureFailureKind.OpenChoice)]
    [InlineData("KoL adventure redirects are disabled; refresh the session manually. (HTTP 302 to 'fight.php')", AdventureFailureKind.OpenFight)]
    [InlineData("KoL adventure start was redirected to main.php, but the page is not the recognised interstitial; no further start was sent.", AdventureFailureKind.Interstitial)]
    [InlineData("KoL adventure redirects are disabled; refresh the session manually. (HTTP 302 to 'main.php')", AdventureFailureKind.Interstitial)]
    [InlineData("(HTTP 302 to 'login.php?notloggedin=1')", AdventureFailureKind.NotLoggedIn)]
    [InlineData("You're out of adventures", AdventureFailureKind.NoAdventures)]
    [InlineData("This location requires a special route.", AdventureFailureKind.LocationUnavailable)]
    [InlineData("KoL adventure redirects are disabled; refresh the session manually. (HTTP 302 to 'somewhere.php')", AdventureFailureKind.Redirect)]
    [InlineData("Something odd happened", AdventureFailureKind.Unknown)]
    public void Known_failures_map_to_a_kind_with_a_readable_message(string message, AdventureFailureKind kind)
    {
        var info = AdventureFailureMapper.Describe(message);
        Assert.Equal(kind, info.Kind);
        Assert.False(string.IsNullOrWhiteSpace(info.Message));
        if (kind != AdventureFailureKind.Unknown) Assert.False(string.IsNullOrWhiteSpace(info.NextStep));
        Assert.DoesNotContain("HTTP 302", info.Text);
    }

    [Fact]
    public void Unknown_messages_stay_visible()
        => Assert.Equal("Something odd happened", AdventureFailureMapper.Describe("Something odd happened").Text);
}

public sealed class AdventureDecisionControllerTests
{
    private sealed class FakePort : IOpenChoicePort
    {
        public OpenChoiceDetection Open = OpenChoiceDetection.NoneOpen;
        public List<(string Choice, int Option)> Sent { get; } = new();
        public Task<OpenChoiceDetection> DetectAsync(CancellationToken ct) => Task.FromResult(Open);
        public Task<OpenChoiceSubmitResult> SubmitAsync(string choiceId, int option, CancellationToken ct)
        {
            Sent.Add((choiceId, option));
            Open = OpenChoiceDetection.NoneOpen;
            return Task.FromResult(new OpenChoiceSubmitResult(true, "Submitted. No choice is open any more.", Open));
        }
    }

    private static OpenChoiceDetection Choice(string id = "1003")
        => new(OpenChoiceKind.Choice, new OpenChoiceState(id, "Turtle Tamer",
            new[] { new ChoiceOptionInfo(1, "Tame it"), new ChoiceOptionInfo(2, "Leave") }));

    private static AdventureDecisionController Controller(FakePort port, ChoiceResolutionConfig? config = null)
        => new(port, () => new ChoiceResolver(config));

    [Fact]
    public async Task Open_choice_is_shown_as_options_and_submit_sends_one_post()
    {
        var port = new FakePort { Open = Choice() };
        var c = Controller(port);
        await c.RefreshAsync();
        Assert.True(c.IsVisible);
        Assert.Equal(new[] { 1, 2 }, c.Options.Select(o => o.Number));

        await c.SubmitAsync(2);
        Assert.Equal(("1003", 2), Assert.Single(port.Sent));
        Assert.False(c.IsVisible);
    }

    [Fact]
    public async Task Option_that_is_not_offered_sends_nothing()
    {
        var port = new FakePort { Open = Choice() };
        var c = Controller(port);
        await c.RefreshAsync();
        var message = await c.SubmitAsync(9);
        Assert.Empty(port.Sent);
        Assert.Contains("nothing was sent", message);
    }

    [Fact]
    public async Task Policy_without_answer_keeps_the_options_for_the_user()
    {
        var port = new FakePort { Open = Choice() };
        var c = Controller(port);
        await c.RefreshAsync();
        var message = await c.ApplyPolicyAsync();
        Assert.Empty(port.Sent);
        Assert.True(c.IsChoiceOpen);
        Assert.Contains("no answer", message);
    }

    [Fact]
    public async Task Policy_with_configured_default_submits_that_option()
    {
        var port = new FakePort { Open = Choice() };
        var config = new ChoiceResolutionConfig
        {
            Choices = { ["1003"] = new ChoiceDefaultEntry { Option = 2 } }
        };
        var c = Controller(port, config);
        await c.RefreshAsync();
        var message = await c.ApplyPolicyAsync();
        Assert.Equal(("1003", 2), Assert.Single(port.Sent));
        Assert.Contains("Policy applied", message);
    }

    [Fact]
    public void Fight_encounter_shows_the_fight_state_and_needs_decision_shows_options()
    {
        var c = new AdventureDecisionController(null);
        c.Show(new AdventureEncounterState("a", AdventureEncounterKind.Fight, null, null,
            Array.Empty<string>(), false, Array.Empty<string>()));
        Assert.True(c.IsFightOpen);

        c.Show(new NeedsDecisionInfo
        {
            RunId = "r", ChoiceId = "1003", Title = "Turtle",
            Options = new[] { new ChoiceOptionInfo(1, "Yes") }
        });
        Assert.False(c.IsFightOpen);
        Assert.Equal("1003", c.ChoiceId);
        Assert.Single(c.Options);
    }

    [Fact]
    public async Task Without_a_live_session_buttons_report_instead_of_doing_nothing()
    {
        var c = new AdventureDecisionController(null);
        Assert.Contains("No live session", await c.SubmitAsync(1));
        Assert.Contains("No live session", await c.ApplyPolicyAsync());
        Assert.Contains("No live session", await c.RefreshAsync());
    }
}

public sealed class BoundedRunReportTests
{
    [Fact]
    public void Summary_reports_turns_drops_and_reason()
    {
        var report = BoundedRunReport.From(3, 7, BoundedAdventureRunStopReason.NeedsDecision,
            new[] { new QuestDrop { ItemName = "knucklebone", Quantity = 2 }, new QuestDrop { ItemName = "knucklebone" } });
        Assert.True(report.NeedsUser);
        Assert.Contains("Used 3 turns, 7 left", report.Summary);
        Assert.Contains("3 × knucklebone", report.Summary);
        Assert.Contains("choice policy has no answer", report.Summary);
    }

    [Fact]
    public void Finished_budget_is_not_a_user_problem_and_failures_are_mapped()
    {
        var done = BoundedRunReport.From(10, 0, BoundedAdventureRunStopReason.Complete, null);
        Assert.False(done.NeedsUser);
        Assert.Contains("no drops", done.Summary);
        var failed = BoundedRunReport.From(1, 9, BoundedAdventureRunStopReason.GatewayStopped, null,
            "redirects are disabled (HTTP 302 to 'choice.php')");
        Assert.Contains("choice adventure is open", failed.Summary);
    }
}

public sealed class LocationFilterTests
{
    [Theory]
    [InlineData("smut orc", true)]
    [InlineData("logging camp", true)]
    [InlineData("295", true)]
    [InlineData("#295", true)]
    [InlineData("skeleton store", false)]
    public void Filter_matches_words_and_snarfblat(string filter, bool expected)
    {
        var row = new EncounterBrowserLocationRow("adventure=295:The Smut Orc Logging Camp",
            "The Smut Orc Logging Camp", "Mountain", 295, 1, 0, 0);
        Assert.Equal(expected, EncounterBrowserViewModel.MatchesFilter(row, filter));
    }

    [Fact]
    public void Catalog_has_both_requested_locations_selectable_by_filter()
    {
        var vm = new EncounterBrowserViewModel(new MafiaDataLoader().Load(
            Path.Combine(AppContext.BaseDirectory, "external", "kolmafia-data")));
        vm.FilterText = "smut orc";
        Assert.Contains(vm.Locations, l => l.Snarfblat == 295);
        vm.FilterText = "439";
        Assert.Contains(vm.Locations, l => l.DisplayName == "The Skeleton Store" && l.Snarfblat == 439);
    }
}
