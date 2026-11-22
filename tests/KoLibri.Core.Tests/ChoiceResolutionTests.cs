using System.Net;
using System.Text.Json;
using Xunit;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Tests;

public sealed class ChoiceResolutionTests
{
    // KoLmafia's bundled data (external/kolmafia-data) has no choice-adventure table, so the Turtle
    // Tamer choice id cannot be taken from it; the fixture uses a neutral id and labels.
    private const string FixtureChoice = "9001";
    private static readonly ChoiceOptionInfo[] LeaveOptions =
    {
        new(1, "Help the turtle"), new(2, "Leave it alone")
    };

    private static ChoiceResolutionRequest Request(string id = FixtureChoice, params ChoiceOptionInfo[] options)
        => new("knucklebones", id, "A Turtle", options.Length == 0 ? LeaveOptions : options);

    [Fact]
    public void Ask_mode_never_picks_without_a_configured_default()
    {
        var result = new ChoiceResolver().Resolve(Request());
        Assert.Null(result.Option);
        Assert.Equal(ChoiceResolutionSource.None, result.Source);
    }

    [Fact]
    public void Configured_default_wins_even_in_ask_mode_and_is_logged()
    {
        var config = new ChoiceResolutionConfig();
        config.Choices[FixtureChoice] = new ChoiceDefaultEntry { Option = 1 };
        var resolver = new ChoiceResolver(config);
        var result = resolver.Resolve(Request());
        Assert.Equal(1, result.Option);
        Assert.Equal(ChoiceResolutionSource.Configured, result.Source);
        var log = Assert.Single(resolver.Log);
        Assert.Equal((FixtureChoice, "A Turtle", 1), (log.ChoiceId, log.Title, log.Option));
    }

    [Fact]
    public void Configured_option_that_is_not_visible_is_not_submitted()
    {
        var config = new ChoiceResolutionConfig();
        config.Choices[FixtureChoice] = new ChoiceDefaultEntry { Option = 7 };
        Assert.Null(new ChoiceResolver(config).Resolve(Request()).Option);
    }

    [Fact]
    public void AutoSafe_picks_the_single_free_leave_option()
    {
        var resolver = new ChoiceResolver(new ChoiceResolutionConfig { Mode = ChoiceResolutionMode.AutoSafe });
        var result = resolver.Resolve(Request());
        Assert.Equal(2, result.Option);
        Assert.Equal(ChoiceResolutionSource.SafeLabel, result.Source);
    }

    [Theory]
    [InlineData("Leave it alone", "Ignore it")]      // ambiguous
    [InlineData("Leave and pay 500 Meat", "Fight")]  // costs
    [InlineData("Help", "Buy the turtle")]           // nothing safe
    public void AutoSafe_refuses_costly_or_ambiguous_options(string first, string second)
    {
        var resolver = new ChoiceResolver(new ChoiceResolutionConfig { Mode = ChoiceResolutionMode.AutoSafe });
        Assert.Null(resolver.Resolve(Request(FixtureChoice, new ChoiceOptionInfo(1, first), new ChoiceOptionInfo(2, second))).Option);
    }

    [Fact]
    public void Built_in_rule_respects_cost_limits()
    {
        var table = new[] { new BuiltInChoiceRule(FixtureChoice, 2, "Turtle", MeatCost: 100) };
        var strict = new ChoiceResolver(new ChoiceResolutionConfig { Mode = ChoiceResolutionMode.AutoSafe }, table);
        Assert.Null(strict.Resolve(Request()).Option);
        var relaxed = new ChoiceResolver(
            new ChoiceResolutionConfig { Mode = ChoiceResolutionMode.AutoSafe, MaxMeatCost = 100 }, table);
        Assert.Equal(ChoiceResolutionSource.BuiltIn, relaxed.Resolve(Request()).Source);
    }

    [Fact]
    public void Config_values_are_clamped_and_round_trip_through_the_store()
    {
        var config = new ChoiceResolutionConfig { MaxHpPercent = 500, MaxMeatCost = -4, MaxAdventureCost = 99 }.Normalize();
        Assert.Equal((100, 0, 10), (config.MaxHpPercent, config.MaxMeatCost, config.MaxAdventureCost));

        var path = Path.Combine(Path.GetTempPath(), $"choice-policy-{Guid.NewGuid():N}.json");
        try
        {
            var store = new ChoiceResolutionConfigStore(path);
            Assert.Equal(ChoiceResolutionMode.Ask, store.Load().Mode);
            config.Choices[FixtureChoice] = new ChoiceDefaultEntry { Option = 2, Title = "Turtle" };
            store.Save(config with { Mode = ChoiceResolutionMode.AutoSafe });
            var loaded = store.Load();
            Assert.Equal(ChoiceResolutionMode.AutoSafe, loaded.Mode);
            Assert.Equal(2, loaded.Choices[FixtureChoice].Option);
            File.WriteAllText(path, "{ not json");
            Assert.Equal(ChoiceResolutionMode.Ask, store.Load().Mode);
        }
        finally { File.Delete(path); }
    }

    private static AdventureKernelObservation ChoicePage() => new()
    {
        AdventureId = "knucklebones", Kind = AdventureEncounterKind.Choice, EncounterId = "knucklebones",
        ChoiceId = FixtureChoice, ChoiceTitle = "A Turtle", AvailableChoices = new[] { "1", "2" },
        ChoiceLabels = new Dictionary<string, string> { ["1"] = "Help the turtle", ["2"] = "Leave it alone" },
        ResponseHash = "choice-hash"
    };

    [Fact]
    public async Task Unknown_choice_in_ask_mode_stops_with_NeedsDecision_and_sends_no_choice()
    {
        using var transport = new AdventureExecutionFixtureTransport();
        transport.EnqueueObservation(AdventureTransportOperation.Adventure, ChoicePage());
        var checkpoints = new InMemoryAdventureRunCheckpointStore();
        await using var gateway = new AdventureExecutionGateway(transport, checkpoints);
        var allowlist = new AdventureActionAllowlist { ChoiceResolver = new ChoiceResolver() }.AddAdventure("knucklebones");

        var result = await new BoundedAdventureRunner(gateway).RunAsync(
            new AdventureRunRequest { RunId = "run-1", AdventureId = "knucklebones" }, allowlist);

        Assert.Equal(BoundedAdventureRunStopReason.NeedsDecision, result.StopReason);
        Assert.Equal(0, transport.Count(AdventureTransportOperation.Choice));
        var needs = Assert.IsType<NeedsDecisionInfo>(result.NeedsDecision);
        Assert.Equal(("run-1", FixtureChoice, "A Turtle"), (needs.RunId, needs.ChoiceId, needs.Title));
        Assert.Equal(new[] { (1, "Help the turtle"), (2, "Leave it alone") },
            needs.Options.Select(o => (o.Number, o.Text)).ToArray());
        Assert.Contains("do not guess", needs.Instruction);
        Assert.Contains("--choice 9001 --option <N>", needs.ResumeCommand());
    }

    [Fact]
    public async Task AutoSafe_submits_the_safe_option_and_logs_it()
    {
        using var transport = new AdventureExecutionFixtureTransport();
        transport.EnqueueObservation(AdventureTransportOperation.Adventure, ChoicePage());
        transport.EnqueueObservation(AdventureTransportOperation.Choice, new AdventureKernelObservation
        {
            AdventureId = "knucklebones", Kind = AdventureEncounterKind.Reward, CanContinue = false, ResponseHash = "done"
        });
        await using var gateway = new AdventureExecutionGateway(transport, new InMemoryAdventureRunCheckpointStore());
        var resolver = new ChoiceResolver(new ChoiceResolutionConfig { Mode = ChoiceResolutionMode.AutoSafe });
        var allowlist = new AdventureActionAllowlist { ChoiceResolver = resolver }.AddAdventure("knucklebones");

        var result = await new BoundedAdventureRunner(gateway).RunAsync(
            new AdventureRunRequest { RunId = "run-2", AdventureId = "knucklebones" }, allowlist);

        Assert.Equal(BoundedAdventureRunStopReason.Complete, result.StopReason);
        Assert.Equal(1, transport.Count(AdventureTransportOperation.Choice));
        var log = Assert.Single(resolver.Log);
        Assert.Equal(2, log.Option);
    }

    [Fact]
    public async Task Choice_chain_reports_a_stored_open_choice_as_NeedsDecision()
    {
        using var transport = new AdventureExecutionFixtureTransport();
        transport.EnqueueObservation(AdventureTransportOperation.Adventure, ChoicePage());
        var checkpoints = new InMemoryAdventureRunCheckpointStore();
        await using var gateway = new AdventureExecutionGateway(transport, checkpoints);
        var allowlist = new AdventureActionAllowlist { ChoiceResolver = new ChoiceResolver() }.AddAdventure("knucklebones");
        await new BoundedAdventureRunner(gateway).RunAsync(
            new AdventureRunRequest { RunId = "run-3", AdventureId = "knucklebones" }, allowlist);

        var chain = await new BoundedChoiceChainRunner(gateway).RunAsync("run-3", allowlist);

        Assert.Equal(BoundedChoiceChainStopReason.NeedsDecision, chain.StopReason);
        Assert.Equal(FixtureChoice, chain.NeedsDecision!.ChoiceId);
        Assert.Equal(0, transport.Count(AdventureTransportOperation.Choice));
    }

    private const string ChoiceHtml =
        "<html><body><table><tr><td bgcolor=blue><b>A Turtle</b></td></tr></table>"
        + "<form action=choice.php method=post><input type=hidden name=pwd value=abc123>"
        + "<input type=hidden name=whichchoice value=9001><input type=hidden name=option value=1>"
        + "<input class=button type=submit value=\"Help the turtle\"></form>"
        + "<form action=choice.php method=post><input type=hidden name=pwd value=abc123>"
        + "<input type=hidden name=whichchoice value=9001><input type=hidden name=option value=2>"
        + "<input class=button type=submit value=\"Leave it alone\"></form></body></html>";

    private sealed class FakeKol : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();
        public bool ChoiceOpen { get; set; } = true;
        public string Redirect { get; set; } = "choice.php";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            var path = request.RequestUri.AbsolutePath;
            if (request.Method == HttpMethod.Post) ChoiceOpen = false;
            if (path.EndsWith("main.php") && ChoiceOpen)
            {
                var r = new HttpResponseMessage(HttpStatusCode.Found);
                r.Headers.Location = new Uri("/" + Redirect, UriKind.Relative);
                return Task.FromResult(r);
            }
            if (path.EndsWith("choice.php") && request.Method == HttpMethod.Get && ChoiceOpen)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ChoiceHtml) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>main</html>") });
        }
    }

    [Fact]
    public async Task Redirect_to_choice_php_is_detected_with_id_title_and_options()
    {
        var kol = new FakeKol();
        var detection = await new OpenChoiceClient(new HttpClient(kol)).DetectAsync();
        Assert.Equal(OpenChoiceKind.Choice, detection.Kind);
        Assert.Equal("9001", detection.Choice!.ChoiceId);
        Assert.Equal("A Turtle", detection.Choice.Title);
        Assert.Equal(new[] { (1, "Help the turtle"), (2, "Leave it alone") },
            detection.Choice.Options.Select(o => (o.Number, o.Text)).ToArray());
        Assert.DoesNotContain(kol.Requests, r => r.StartsWith("POST"));
    }

    [Fact]
    public async Task Redirect_to_fight_php_is_detected_and_no_choice_is_reported_when_closed()
    {
        var fight = await new OpenChoiceClient(new HttpClient(new FakeKol { Redirect = "fight.php" })).DetectAsync();
        Assert.Equal(OpenChoiceKind.Fight, fight.Kind);
        var none = await new OpenChoiceClient(new HttpClient(new FakeKol { ChoiceOpen = false })).DetectAsync();
        Assert.False(none.IsOpen);
    }

    [Fact]
    public void A_redirect_response_can_be_turned_into_OpenChoiceException()
    {
        var redirect = new HttpResponseMessage(HttpStatusCode.Found);
        redirect.Headers.Location = new Uri("https://www.kingdomofloathing.com/choice.php?forceoption=0");
        var error = Assert.Throws<OpenChoiceException>(() => OpenChoiceRedirect.ThrowIfOpenChoice(redirect));
        Assert.Equal(OpenChoiceKind.Choice, error.Kind);
        Assert.Contains("choice adventure is open", error.Message);
        redirect.Headers.Location = new Uri("/main.php", UriKind.Relative);
        OpenChoiceRedirect.ThrowIfOpenChoice(redirect); // other redirects are not ours
    }

    [Fact]
    public async Task Submit_refuses_a_mismatching_choice_or_option_and_sends_nothing()
    {
        var kol = new FakeKol();
        var client = new OpenChoiceClient(new HttpClient(kol));
        Assert.False((await client.SubmitAsync("1234", 1)).Submitted);
        Assert.False((await client.SubmitAsync("9001", 5)).Submitted);
        Assert.DoesNotContain(kol.Requests, r => r.StartsWith("POST"));

        var ok = await client.SubmitAsync("9001", 2);
        Assert.True(ok.Submitted);
        Assert.False(ok.Next.IsOpen);
        Assert.Single(kol.Requests, r => r.StartsWith("POST"));
    }

    [Fact]
    public async Task Cli_submit_without_execute_is_a_preview_that_sends_nothing()
    {
        var original = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            var code = await KolibriCli.RunAsync(new[] { "choice", "submit", "--role", "mallbot", "--choice", "9001", "--option", "2" });
            Assert.Equal(0, code);
        }
        finally { Console.SetOut(original); }
        using var doc = JsonDocument.Parse(writer.ToString());
        Assert.True(doc.RootElement.GetProperty("previewOnly").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("executed").GetBoolean());
        Assert.Equal("9001", doc.RootElement.GetProperty("choiceId").GetString());
    }

    [Fact]
    public void Cli_policy_set_enforces_ranges()
    {
        var config = KolibriCli.ApplyChoicePolicySet(new ChoiceResolutionConfig(),
            new[] { "--mode", "auto-safe", "--max-meat", "50", "--choice", "9001", "--option", "2" });
        Assert.Equal(ChoiceResolutionMode.AutoSafe, config.Mode);
        Assert.Equal(50, config.MaxMeatCost);
        Assert.Equal(2, config.Choices["9001"].Option);
        Assert.Throws<ArgumentException>(() => KolibriCli.ApplyChoicePolicySet(new ChoiceResolutionConfig(), new[] { "--max-hp-percent", "101" }));
        Assert.Throws<ArgumentException>(() => KolibriCli.ApplyChoicePolicySet(new ChoiceResolutionConfig(), new[] { "--mode", "yolo" }));
    }
}
