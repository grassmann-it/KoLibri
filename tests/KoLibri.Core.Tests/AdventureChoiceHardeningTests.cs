using System.Net;
using System.Text.Json;
using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;
using LibKolibri;
using LibKolibri.Access;
using Xunit;

namespace KoLibri.Core.Tests;

/// <summary>Package W-B: adventure and choice hardening, all against fake transports. The desktop part lives in KoLibri.Tests/Desktop.</summary>
public sealed class AdventureChoiceHardeningTests
{
    private const string ChoiceHtml =
        "<html><body><table><tr><td bgcolor=blue><b>A Turtle</b></td></tr></table>"
        + "<form action=choice.php method=post><input type=hidden name=pwd value=abc123>"
        + "<input type=hidden name=whichchoice value=9001><input type=hidden name=option value=1>"
        + "<input class=button type=submit value=\"Help the turtle\"></form>"
        + "<form action=choice.php method=post><input type=hidden name=pwd value=abc123>"
        + "<input type=hidden name=whichchoice value=9001><input type=hidden name=option value=2>"
        + "<input class=button type=submit value=\"Leave it alone\"></form></body></html>";

    private sealed class ScriptedKol(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return Task.FromResult(reply(request));
        }
    }

    private static HttpResponseMessage Redirect(string to)
    {
        var r = new HttpResponseMessage(HttpStatusCode.Found);
        r.Headers.Location = new Uri("/" + to, UriKind.Relative);
        return r;
    }

    private static HttpResponseMessage Html(string html, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(html) };

    // --- H12: a failed question is never "free" -------------------------------------------------

    [Theory]
    [InlineData("503")]
    [InlineData("login-redirect")]
    [InlineData("login-page")]
    [InlineData("throws")]
    public async Task Detect_reports_unavailable_for_errors_and_login_pages(string scenario)
    {
        var kol = new ScriptedKol(request => scenario switch
        {
            "503" => Html("busy", HttpStatusCode.ServiceUnavailable),
            "login-redirect" => Redirect("login.php"),
            "login-page" => Html("<form action=\"login.php\"><input name=loginname></form>"),
            _ => throw new HttpRequestException("connection refused")
        });

        var detection = await new OpenChoiceClient(new HttpClient(kol)).DetectAsync();

        Assert.Equal(OpenChoiceKind.Unavailable, detection.Kind);
        Assert.True(detection.IsUnavailable);
        Assert.False(detection.IsOpen);
        Assert.Contains("could not be read", detection.Message);
    }

    [Fact]
    public async Task Submit_does_not_post_when_the_state_cannot_be_read()
    {
        var kol = new ScriptedKol(_ => Html("busy", HttpStatusCode.BadGateway));
        var result = await new OpenChoiceClient(new HttpClient(kol)).SubmitAsync("9001", 1);
        Assert.False(result.Submitted);
        Assert.DoesNotContain(kol.Requests, r => r.StartsWith("POST"));
    }

    // --- H11: response text/redirect/follow-up state decide ------------------------------------

    [Fact]
    public async Task A_refused_option_with_the_same_choice_still_open_is_not_a_success()
    {
        var kol = new ScriptedKol(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("main.php")) return Redirect("choice.php");
            if (request.Method == HttpMethod.Post) return Html("<html>Invalid option.</html>");
            return Html(ChoiceHtml);
        });

        var result = await new OpenChoiceClient(new HttpClient(kol)).SubmitAsync("9001", 2);

        Assert.False(result.Submitted);
        Assert.Contains("did not accept", result.Message);
        Assert.Equal(OpenChoiceKind.Choice, result.Next.Kind);
    }

    [Fact]
    public async Task A_main_php_redirect_after_the_post_is_a_success()
    {
        var posted = false;
        var kol = new ScriptedKol(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post) { posted = true; return Redirect("main.php"); }
            if (path.EndsWith("main.php") && !posted) return Redirect("choice.php");
            if (path.EndsWith("choice.php")) return Html(ChoiceHtml);
            return Html("<html>main</html>");
        });

        var result = await new OpenChoiceClient(new HttpClient(kol)).SubmitAsync("9001", 2);

        Assert.True(result.Submitted, result.Message);
        Assert.False(result.Next.IsOpen);
    }

    [Fact]
    public async Task A_post_that_cannot_be_confirmed_after_a_connection_error_is_not_a_success()
    {
        var kol = new ScriptedKol(request =>
        {
            if (request.Method == HttpMethod.Post) throw new HttpRequestException("reset");
            return request.RequestUri!.AbsolutePath.EndsWith("main.php") ? Redirect("choice.php") : Html(ChoiceHtml);
        });
        var result = await new OpenChoiceClient(new HttpClient(kol)).SubmitAsync("9001", 2);
        Assert.False(result.Submitted);
        Assert.Contains("could not be confirmed", result.Message);
    }

    // --- M11: HP/MP live conditions ------------------------------------------------------------

    [Fact]
    public async Task Hp_and_mp_conditions_read_the_observed_status()
    {
        var client = new StatusClient(new KoLStatusSnapshot("main", 5, 0, 10, DateTimeOffset.UtcNow)
        { CurrentHp = 40, MaximumHp = 100, CurrentMp = 7, MaximumMp = 30 });
        var provider = new AccountAdventureConditionSnapshotProvider(client, new[]
        {
            new AdventureCondition { Kind = AdventureConditionKind.HitPoints, Operator = AdventureConditionOperator.AtMost, Target = 50 },
            new AdventureCondition { Kind = AdventureConditionKind.ManaPoints, Target = 5 }
        });
        var snapshot = await provider.ReadAsync();
        Assert.Equal(40, snapshot.HitPoints);
        Assert.Equal(7, snapshot.ManaPoints);
        Assert.Null(snapshot.UnavailableReason);
    }

    [Fact]
    public async Task Unknown_hp_blocks_instead_of_reading_as_zero()
    {
        var client = new StatusClient(new KoLStatusSnapshot("main", 5, 0, 10, DateTimeOffset.UtcNow));
        var provider = new AccountAdventureConditionSnapshotProvider(client,
            new[] { new AdventureCondition { Kind = AdventureConditionKind.HitPoints, Target = 1 } });
        Assert.NotNull((await provider.ReadAsync()).UnavailableReason);
    }

    private sealed class StatusClient(KoLStatusSnapshot status) : IAccountReadClient
    {
        public Task<KoLStatusSnapshot> FetchStatusAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
            => Task.FromResult(status);
        public Task<KoLInventorySnapshot> FetchInventoryAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
            => Task.FromResult(new KoLInventorySnapshot(Array.Empty<KoLInventoryItem>(), DateTimeOffset.UtcNow));
    }

    // --- H9 (core): fight limits in the runner --------------------------------------------------

    private static AdventureKernelObservation Fight(int round, int hp)
        => new()
        {
            AdventureId = "skeleton", Kind = AdventureEncounterKind.Fight, EncounterId = "skeleton",
            MonsterId = "skeleton", FightRound = round, CanContinue = true, ResponseHash = "fight" + round,
            Combat = new CombatObservation
            {
                MonsterId = "skeleton", Round = round, PlayerHp = hp, PlayerMaxHp = 100,
                AvailableActions = new[] { new CombatAction(CombatActionKind.Attack, "attack", "Attack") }
            }
        };

    private static AdventureActionAllowlist FightAllowlist()
        => new AdventureActionAllowlist().AddAdventure("skeleton").AddEncounter("skeleton", "skeleton")
            .AddFightAction("skeleton", "attack", "skeleton", 1, "skeleton")
            .AddFightAction("skeleton", "attack", "skeleton", 2, "skeleton")
            .AddFightAction("skeleton", "attack", "skeleton", 3, "skeleton");

    [Fact]
    public async Task The_runner_stops_before_a_fight_round_when_hp_is_below_the_minimum()
    {
        using var transport = new AdventureExecutionFixtureTransport();
        transport.EnqueueObservation(AdventureTransportOperation.Adventure, Fight(1, 10));
        await using var gateway = new AdventureExecutionGateway(transport, new InMemoryAdventureRunCheckpointStore());

        var result = await new BoundedAdventureRunner(gateway).RunAsync(
            new AdventureRunRequest { RunId = "hp", AdventureId = "skeleton" }, FightAllowlist(),
            options: new BoundedAdventureRunnerOptions { MinimumHpPercent = 50 });

        Assert.Equal(BoundedAdventureRunStopReason.HpBelowMinimum, result.StopReason);
        Assert.Single(transport.Requests); // the fight round was never sent
    }

    // Live 2026-11-26: fight.php shows no player HP, so every fight stopped with "HP below minimum (or unknown)".
    private static AdventureKernelObservation FightWithoutHp(int round)
    {
        var fight = Fight(round, 0);
        return fight with { Combat = fight.Combat! with { PlayerHp = null, PlayerMaxHp = null } };
    }

    [Fact]
    public async Task A_fight_page_without_hp_uses_the_fresh_status_hp()
    {
        using var transport = new AdventureExecutionFixtureTransport();
        transport.EnqueueObservation(AdventureTransportOperation.Adventure, FightWithoutHp(1));
        transport.EnqueueObservation(AdventureTransportOperation.Fight, Fight(2, 90) with { Kind = AdventureEncounterKind.Complete, CanContinue = false });
        await using var gateway = new AdventureExecutionGateway(transport, new InMemoryAdventureRunCheckpointStore());
        var asked = 0;

        var result = await new BoundedAdventureRunner(gateway).RunAsync(
            new AdventureRunRequest { RunId = "hp-status", AdventureId = "skeleton" }, FightAllowlist(),
            options: new BoundedAdventureRunnerOptions
            {
                MinimumHpPercent = 50,
                HitPointsProvider = _ => { asked++; return ValueTask.FromResult<(int, int)?>((494, 504)); }
            });

        Assert.NotEqual(BoundedAdventureRunStopReason.HpBelowMinimum, result.StopReason);
        Assert.Equal(1, asked);
        Assert.Equal(2, transport.Requests.Count); // the attack was sent
    }

    [Theory]
    [InlineData(10)]
    [InlineData(-1)] // provider returns unknown
    public async Task Status_hp_below_the_minimum_or_unknown_still_stops(int hp)
    {
        using var transport = new AdventureExecutionFixtureTransport();
        transport.EnqueueObservation(AdventureTransportOperation.Adventure, FightWithoutHp(1));
        await using var gateway = new AdventureExecutionGateway(transport, new InMemoryAdventureRunCheckpointStore());

        var result = await new BoundedAdventureRunner(gateway).RunAsync(
            new AdventureRunRequest { RunId = "hp-low", AdventureId = "skeleton" }, FightAllowlist(),
            options: new BoundedAdventureRunnerOptions
            {
                MinimumHpPercent = 50,
                HitPointsProvider = _ => ValueTask.FromResult<(int, int)?>(hp < 0 ? null : (hp, 100))
            });

        Assert.Equal(BoundedAdventureRunStopReason.HpBelowMinimum, result.StopReason);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task The_runner_stops_at_the_fight_round_limit()
    {
        using var transport = new AdventureExecutionFixtureTransport();
        transport.EnqueueObservation(AdventureTransportOperation.Adventure, Fight(1, 90));
        transport.EnqueueObservation(AdventureTransportOperation.Fight, Fight(2, 90));
        transport.EnqueueObservation(AdventureTransportOperation.Fight, Fight(3, 90));
        await using var gateway = new AdventureExecutionGateway(transport, new InMemoryAdventureRunCheckpointStore());

        var result = await new BoundedAdventureRunner(gateway).RunAsync(
            new AdventureRunRequest { RunId = "rounds", AdventureId = "skeleton" }, FightAllowlist(),
            options: new BoundedAdventureRunnerOptions { MaxFightRounds = 1, MinimumHpPercent = 50 });

        Assert.True(result.StopReason == BoundedAdventureRunStopReason.FightRoundLimitReached,
            result.LastStep?.State.AbortMessage + " / " + string.Join(";", result.LastStep?.Messages ?? new List<string>()));
        Assert.Equal(2, transport.Requests.Count); // start + exactly one round
    }

    // --- H14: resume applies the user's decision first -----------------------------------------

    [Fact]
    public async Task Resume_with_an_explicit_choice_rule_applies_it_once_before_anything_else()
    {
        using var transport = new AdventureExecutionFixtureTransport();
        transport.EnqueueObservation(AdventureTransportOperation.Adventure, new AdventureKernelObservation
        {
            AdventureId = "skeleton", Kind = AdventureEncounterKind.Choice, EncounterId = "skeleton",
            ChoiceId = "9001", AvailableChoices = new[] { "1", "2" }, CanContinue = true, ResponseHash = "c"
        });
        transport.EnqueueObservation(AdventureTransportOperation.Choice, new AdventureKernelObservation
        {
            AdventureId = "skeleton", Kind = AdventureEncounterKind.Reward, CanContinue = false, ResponseHash = "done"
        });
        var checkpoints = new InMemoryAdventureRunCheckpointStore();
        await using var gateway = new AdventureExecutionGateway(transport, checkpoints);
        var stopping = new AdventureActionAllowlist { ChoiceResolver = new ChoiceResolver() }.AddAdventure("skeleton");
        await new BoundedAdventureRunner(gateway).RunAsync(
            new AdventureRunRequest { RunId = "resume", AdventureId = "skeleton" }, stopping);
        Assert.Equal(BoundedChoiceChainStopReason.NeedsDecision,
            (await new BoundedChoiceChainRunner(gateway).RunAsync("resume", stopping)).StopReason);

        var decided = new AdventureActionAllowlist { ChoiceResolver = new ChoiceResolver() }
            .AddAdventure("skeleton").AddChoiceOption("skeleton", "9001", "2", "skeleton");
        var result = await new BoundedChoiceChainRunner(gateway).RunAsync("resume", decided);

        Assert.Equal(BoundedChoiceChainStopReason.Complete, result.StopReason);
        Assert.Equal(1, transport.Count(AdventureTransportOperation.Choice));
        Assert.Equal("2", transport.Requests.Last().Action is AdventureChoiceAction c ? c.OptionId : null);
    }

    [Fact]
    public async Task Resume_with_a_rule_for_an_option_that_is_not_visible_still_needs_a_decision()
    {
        using var transport = new AdventureExecutionFixtureTransport();
        transport.EnqueueObservation(AdventureTransportOperation.Adventure, new AdventureKernelObservation
        {
            AdventureId = "skeleton", Kind = AdventureEncounterKind.Choice, EncounterId = "skeleton",
            ChoiceId = "9001", AvailableChoices = new[] { "1" }, CanContinue = true, ResponseHash = "c"
        });
        await using var gateway = new AdventureExecutionGateway(transport, new InMemoryAdventureRunCheckpointStore());
        var allowlist = new AdventureActionAllowlist { ChoiceResolver = new ChoiceResolver() }
            .AddAdventure("skeleton").AddChoiceOption("skeleton", "9001", "7", "skeleton");
        await new BoundedAdventureRunner(gateway).RunAsync(
            new AdventureRunRequest { RunId = "bad", AdventureId = "skeleton" }, allowlist);

        var result = await new BoundedChoiceChainRunner(gateway).RunAsync("bad", allowlist);

        Assert.Equal(BoundedChoiceChainStopReason.NeedsDecision, result.StopReason);
        Assert.Equal(0, transport.Count(AdventureTransportOperation.Choice));
    }

    // --- M10 / H7 / H13: MCP --------------------------------------------------------------------

    [Theory]
    [InlineData("adventure=543:Your Mushroom Garden", 543)]
    [InlineData("543:Your Mushroom Garden", 543)]
    [InlineData("543", 543)]
    [InlineData("The Haunted Pantry", 0)]
    [InlineData("0:Nothing", 0)]
    public void Catalog_ids_resolve_to_a_numeric_snarfblat(string id, int expected)
    {
        Assert.Equal(expected > 0, AdventureLocationIds.TryGetSnarfblat(id, out var snarfblat));
        Assert.Equal(expected, snarfblat);
    }

    // The MCP adventure/choice tests moved to KoLibri.Tests/McpAdventureToolsTests (0.5).

    // --- CLI resume command (M10) --------------------------------------------------------------

    [Fact]
    public void The_cli_continue_command_keeps_snarfblat_rules_and_limits()
    {
        var args = new[]
        {
            "--location", "adventure=543:Your Mushroom Garden", "--snarfblat", "543", "--fight-action", "attack",
            "--choice-action", "9001:2", "--max-steps", "7", "--conditions-file", "my conditions.json", "--execute"
        };
        var command = KolibriCli.BuildContinueCommand(args, "adventure=543:Your Mushroom Garden", KoLibri.Cli.AccountRole.Main, 543);
        Assert.Contains("--snarfblat 543", command);
        Assert.Contains("--fight-action attack", command);
        Assert.Contains("--choice-action 9001:2", command);
        Assert.Contains("--max-steps 7", command);
        Assert.Contains("--conditions-file \"my conditions.json\"", command);
        Assert.EndsWith("--execute --approval-token <token>", command);
    }
}
