using System.Net;
using System.Text.Json;
using KolLibri.Mcp;
using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;
using KoLibri.Desktop;
using KoLibri.Desktop.ContextMenus;
using LibKolibri;
using LibKolibri.Access;
using Xunit;

namespace KoLibri.Core.Tests;

/// <summary>Package W-B: adventure and choice hardening, all against fake transports.</summary>
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

    [Fact]
    public async Task Unavailable_keeps_the_choice_panel_and_says_so()
    {
        var port = new FakePort(OpenChoiceDetection.Unavailable("HTTP 503"));
        var decisions = new AdventureDecisionController(port);
        decisions.Show(new NeedsDecisionInfo
        {
            RunId = "r", ChoiceId = "9001", Options = new[] { new ChoiceOptionInfo(1, "a") }
        });
        var message = await decisions.RefreshAsync();
        Assert.Contains("could not be read", message);
        Assert.Equal("9001", decisions.ChoiceId); // never wiped by a failed read
    }

    private sealed class FakePort(OpenChoiceDetection detection) : IOpenChoicePort
    {
        public Task<OpenChoiceDetection> DetectAsync(CancellationToken ct) => Task.FromResult(detection);
        public Task<OpenChoiceSubmitResult> SubmitAsync(string choiceId, int option, CancellationToken ct)
            => Task.FromResult(new OpenChoiceSubmitResult(false, "refused", detection));
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

    [Fact]
    public async Task Mcp_run_sends_the_numeric_snarfblat_of_a_catalog_id()
    {
        var (server, transport, bridge) = McpServer();
        using var _ = bridge;
        transport.EnqueueObservation(AdventureTransportOperation.Adventure,
            new AdventureKernelObservation { AdventureId = "adventure=543:Your Mushroom Garden", Kind = AdventureEncounterKind.Reward, CanContinue = false, ResponseHash = "r" });

        await Invoke(server, "kol_adventure_run_execute", """
            {"locationId":"adventure=543:Your Mushroom Garden","runId":"snarf","execute":true,"approvalToken":"t","maxGatewaySteps":3,"snapshot":{"turnsRemaining":1}}
            """);

        Assert.Equal(543, Assert.Single(transport.Requests).Snarfblat);
    }

    [Fact]
    public async Task Mcp_chain_for_an_unknown_run_reports_nothing_executed()
    {
        var (server, _, bridge) = McpServer();
        using var _ = bridge;
        var payload = await Invoke(server, "kol_choice_chain_execute",
            """{"runId":"no-such-run","execute":true,"approvalToken":"t"}""");
        Assert.False(payload.GetProperty("executed").GetBoolean());
        Assert.Equal("Aborted", payload.GetProperty("stopReason").GetString());
    }

    [Fact]
    public async Task Mcp_gated_chain_for_an_unknown_run_books_zero_units()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kolibri-wb-" + Guid.NewGuid().ToString("N"));
        try
        {
            var grants = new GrantStore(Path.Combine(dir, "g.json"));
            var ledger = new UsageLedger(Path.Combine(dir, "u.jsonl"));
            var gate = new AccessGate(grants, ledger);
            var store = new InMemoryAdventureRunCheckpointStore();
            using var bridge = new AdventureMcpExecutionBridge(
                new TransportAdventureExecutionGatewayFactory(new AdventureExecutionFixtureTransport(), store, "kolibri-mcp-gated"), store, auditPath: "<memory>");
            var server = new KolibriMcpServer(runtime: null, options: new KolibriMcpServerOptions(), executionBridge: bridge);
            server.Authorizer = McpAccessPolicy.ForToken(gate, () => McpRequestContext.Token, "Main");
            var grant = grants.Create("ai", ["Main"], new Dictionary<string, RightLimit>
            {
                [Rights.AccountRead] = new(), [Rights.Adventure] = new() { DailyBudget = 50 }
            }, TimeSpan.FromDays(1));
            McpRequestContext.Token = grant.Token;

            string Call(string extra) => "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"kol_choice_chain_execute\",\"arguments\":"
                + "{\"role\":\"Main\",\"runId\":\"ghost\",\"execute\":true," + extra + "\"maxChoices\":20}}}";
            var preview = (await server.HandleAsync(JsonDocument.Parse(Call("")).RootElement))!.Value.GetProperty("result");
            var planId = JsonDocument.Parse(preview.GetProperty("content")[0].GetProperty("text").GetString()!).RootElement.GetProperty("planId").GetString();
            var done = (await server.HandleAsync(JsonDocument.Parse(Call("\"planId\":\"" + planId + "\",")).RootElement))!.Value.GetProperty("result");

            Assert.False(done.GetProperty("isError").GetBoolean(), done.GetRawText());
            Assert.Equal(0, ledger.UsedToday(grant.Grant.Id, Rights.Adventure));
        }
        finally
        {
            McpRequestContext.Token = null;
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Mcp_live_run_ignores_a_supplied_stale_snapshot_without_account_access()
    {
        var (server, transport, bridge) = McpServer();
        using var _ = bridge;
        var payload = await Invoke(server, "kol_adventure_run_execute", """
            {"locationId":"skeleton","runId":"stale","execute":true,"approvalToken":"t",
             "conditions":[{"kind":"ItemCount","key":"1","target":1}],"snapshot":{"isStale":true,"items":{"1":0}}}
            """);
        Assert.Equal("ConditionsBlocked", payload.GetProperty("stopReason").GetString());
        Assert.False(payload.GetProperty("executed").GetBoolean());
        Assert.Equal(0, transport.RequestCount);
    }

    private static (KolibriMcpServer, AdventureExecutionFixtureTransport, AdventureMcpExecutionBridge) McpServer()
    {
        var transport = new AdventureExecutionFixtureTransport();
        var store = new InMemoryAdventureRunCheckpointStore();
        var bridge = new AdventureMcpExecutionBridge(new TransportAdventureExecutionGatewayFactory(transport, store, "t"), store, auditPath: "<memory>");
        return (new KolibriMcpServer(runtime: null, options: new KolibriMcpServerOptions(), executionBridge: bridge), transport, bridge);
    }

    private static async Task<JsonElement> Invoke(KolibriMcpServer server, string tool, string argsJson)
    {
        var envelope = JsonDocument.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":" + argsJson + "}}").RootElement;
        var result = (await server.HandleAsync(envelope, CancellationToken.None))!.Value.GetProperty("result");
        return result.TryGetProperty("structuredContent", out var structured) ? structured.Clone() : result.Clone();
    }

    // --- CLI resume command (M10) --------------------------------------------------------------

    [Fact]
    public void The_cli_continue_command_keeps_snarfblat_rules_and_limits()
    {
        var args = new[]
        {
            "--location", "adventure=543:Your Mushroom Garden", "--snarfblat", "543", "--fight-action", "attack",
            "--choice-action", "9001:2", "--max-steps", "7", "--conditions-file", "my conditions.json", "--execute"
        };
        var command = KolibriCli.BuildContinueCommand(args, "adventure=543:Your Mushroom Garden", AccountRole.Main, 543);
        Assert.Contains("--snarfblat 543", command);
        Assert.Contains("--fight-action attack", command);
        Assert.Contains("--choice-action 9001:2", command);
        Assert.Contains("--max-steps 7", command);
        Assert.Contains("--conditions-file \"my conditions.json\"", command);
        Assert.EndsWith("--execute --approval-token <token>", command);
    }

    // --- job: one default, one definition (KoLmafia item 2) ------------------------------------

    [Fact]
    public void Menu_and_form_describe_the_same_job_with_one_default()
    {
        Assert.Equal(AdventureJob.DefaultTurns, new LocationMenuTarget("1", "x").RunTurns);
        var job = AdventureJob.TryCreate(3, " 123 ", 15, 40, out var error);
        Assert.Null(error);
        Assert.Equal(new AdventureJob(3, "123", 15, 40), job);
        Assert.Null(AdventureJob.TryCreate(3, "abc", 0, 0, out error));
        Assert.NotNull(error);
        Assert.Equal(AdventureJob.DefaultTurns, AdventureJob.TryCreate(null, null, 0, 0, out _)!.Turns);

        var withItem = new LocationContextMenu(new MenuHost()).Build(new LocationMenuTarget("1", "x", RunTurns: 3, StopItemId: "123"));
        Assert.Contains(withItem, i => i.Label == "Run 3 turns (until item 123)");
    }

    private sealed class MenuHost : IShellHost
    {
        public AccountRole ActiveRole => AccountRole.Main;
        public SessionLogViewModel SessionLog { get; } = new(post: action => action());
        public void ShowMessage(string message) { }
        public Task NavigateToAsync(string section) => Task.CompletedTask;
        public Task RefreshStatusAsync(bool forceRefresh) => Task.CompletedTask;
        public Task SearchMallAsync(string term) => Task.CompletedTask;
        public Task CopyTextAsync(string text) => Task.CompletedTask;
        public Task OpenUrlAsync(Uri uri) => Task.CompletedTask;
    }

    // --- desktop: End run, HP stop, answered choice --------------------------------------------

    private const string Status = """{"name":"Fixture","adventures":"9","hp":"80","maxhp":"100"}""";

    private static string Fixture(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { AppContext.BaseDirectory, "Fixtures" }.Concat(parts).ToArray()));

    private sealed class Handler(Func<int, HttpRequestMessage, CancellationToken, Task<string>> reply) : HttpMessageHandler
    {
        private int _count;
        public List<(string Path, HttpMethod Method)> Requests { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            int index;
            lock (Requests) { index = _count++; Requests.Add((request.RequestUri!.AbsolutePath, request.Method)); }
            var body = await reply(index, request, ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body), RequestMessage = request };
        }
    }

    private static EncounterBrowserSessionViewModel Session(Handler handler, string root, out EncounterBrowserViewModel browser)
    {
        var endpoint = new Uri("https://adventure.test/");
        var tokens = new ApprovalTokenStore(root);
        var audit = new AuditEventJournal(Path.Combine(root, "audit.jsonl"));
        var store = new InMemoryAdventureRunCheckpointStore();
        var viewModel = new EncounterBrowserSessionViewModel((role, approval, verification, _) =>
        {
            var account = new AccountSession(new AccountProfile { Id = "fixture", DisplayName = "Fixture", Role = role }, new AccountCredentials());
            var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = endpoint };
            return Task.FromResult(new ManualAdventureSession(KoLAdventureSessionFactory.Create(
                account, http, approval, verification, store, audit,
                new AdventureHttpTransportOptions { BaseUri = endpoint, AllowedHosts = new[] { endpoint.Host } })));
        }, tokens, AccountRole.Main) { ApprovalToken = "fixture" };
        browser = new EncounterBrowserViewModel(new MafiaDataLoader().Load(
            Path.Combine(AppContext.BaseDirectory, "external", "kolmafia-data")));
        return viewModel;
    }

    private static readonly KoLibri.Desktop.AdventureLocation Garden =
        new("fixture-zone", "Fixture", "Fixture", 543, 1, Array.Empty<AdventureEncounterMetadata>());

    [Fact]
    public async Task Run_turns_passes_the_hp_stop_to_the_fight_rounds()
    {
        var root = Directory.CreateTempSubdirectory("kolibri-wb-hp-");
        try
        {
            var fight = Fixture("Adventure", "combat-round-1.html"); // HP 80/100
            var handler = new Handler((_, request, _) => Task.FromResult(
                request.RequestUri!.AbsolutePath.EndsWith("api.php") ? Status : fight));
            var viewModel = Session(handler, root.FullName, out var browser);

            await viewModel.RunTurnsAsync(browser, Garden, 5, null, default, maxFightRounds: 0, minimumHpPercent: 90);

            Assert.Equal(1, handler.Requests.Count(r => r.Method == HttpMethod.Post)); // the adventure POST only
            Assert.DoesNotContain(handler.Requests, r => r.Path.EndsWith("fight.php"));
            Assert.Contains("HP is below", viewModel.LastMessage);
            Assert.True(viewModel.IsSessionActive); // the fight stays open for the user
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task End_run_cancels_a_running_batch_and_sends_no_more_turns()
    {
        var root = Directory.CreateTempSubdirectory("kolibri-wb-end-");
        try
        {
            var firstPostSeen = new TaskCompletionSource();
            var reward = Fixture("Html", "SkeletonOfCrimbosPast", "reward.html");
            var handler = new Handler(async (_, request, ct) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("adventure.php"))
                {
                    firstPostSeen.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct); // KoL is slow; End run must cut this short
                }
                return reward;
            });
            using var pane = new EncounterBrowserPane((role, approval, verification, _) =>
            {
                var endpoint = new Uri("https://adventure.test/");
                var account = new AccountSession(new AccountProfile { Id = "f", DisplayName = "F", Role = role }, new AccountCredentials());
                var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = endpoint };
                return Task.FromResult(new ManualAdventureSession(KoLAdventureSessionFactory.Create(
                    account, http, approval, verification, new InMemoryAdventureRunCheckpointStore(),
                    new AuditEventJournal(Path.Combine(root.FullName, "audit.jsonl")),
                    new AdventureHttpTransportOptions { BaseUri = endpoint, AllowedHosts = new[] { endpoint.Host } })));
            }, _ => { });
            pane.Initialise(Path.Combine(AppContext.BaseDirectory, "external", "kolmafia-data"), root.FullName);
            pane.SelectAccount(AccountRole.Main);
            pane.ApplyApprovalToken("fixture");
            var row = pane.Browser!.Locations.First(r => r.Snarfblat == 543);

            var running = pane.RunJobAsync(row, new AdventureJob(10, null, 0, 0)).AsTask();
            await firstPostSeen.Task.WaitAsync(TimeSpan.FromSeconds(10));
            pane.EndSession();
            await running.WaitAsync(TimeSpan.FromSeconds(10)); // returns instead of burning the budget

            Assert.Equal(1, handler.Requests.Count(r => r.Method == HttpMethod.Post));
            Assert.False(pane.Session!.IsSessionActive);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task An_answered_choice_updates_the_run_so_the_remaining_turns_continue()
    {
        var root = Directory.CreateTempSubdirectory("kolibri-wb-choice-");
        try
        {
            var choice = Fixture("Adventure", "choice-success.html");
            var reward = Fixture("Html", "SkeletonOfCrimbosPast", "reward.html");
            var replies = new Queue<string>(new[] { Status, "{}", choice, reward, Status, Status, "{}", reward, Status, "{\"123\":1}", Status });
            var handler = new Handler((_, _, _) => Task.FromResult(replies.Dequeue()));
            var viewModel = Session(handler, root.FullName, out var browser);

            await viewModel.RunTurnsAsync(browser, Garden, 10, "123");
            Assert.True(viewModel.IsSessionActive, viewModel.LastMessage);
            Assert.NotNull(viewModel.Decisions.ChoiceId);

            var message = await viewModel.Decisions.SubmitAsync(2);

            Assert.StartsWith("Chose option 2", message);
            Assert.False(viewModel.IsSessionActive, message); // RunId follows the answered choice
            Assert.Null(viewModel.Decisions.ChoiceId);
            await viewModel.RunTurnsAsync(browser, null, 0, null);
            Assert.Contains("left", viewModel.LastMessage);
            Assert.Equal(new[] { "/adventure.php", "/choice.php", "/adventure.php" },
                handler.Requests.Where(r => r.Method == HttpMethod.Post).Select(r => r.Path));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task A_role_switch_during_a_decision_is_not_an_unhandled_exception()
    {
        var root = Directory.CreateTempSubdirectory("kolibri-wb-switch-");
        try
        {
            var started = new TaskCompletionSource();
            var handler = new Handler(async (_, _, ct) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return ""; });
            var statuses = new List<string>();
            using var pane = new EncounterBrowserPane((role, approval, verification, _) =>
            {
                var endpoint = new Uri("https://adventure.test/");
                var account = new AccountSession(new AccountProfile { Id = "f", DisplayName = "F", Role = role }, new AccountCredentials());
                var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = endpoint };
                return Task.FromResult(new ManualAdventureSession(KoLAdventureSessionFactory.Create(
                    account, http, approval, verification, new InMemoryAdventureRunCheckpointStore(),
                    new AuditEventJournal(Path.Combine(root.FullName, "audit.jsonl")),
                    new AdventureHttpTransportOptions { BaseUri = endpoint, AllowedHosts = new[] { endpoint.Host } })));
            }, statuses.Add);
            pane.Initialise(Path.Combine(AppContext.BaseDirectory, "external", "kolmafia-data"), root.FullName);
            pane.SelectAccount(AccountRole.Main);

            var refresh = pane.RefreshDecisionAsync().AsTask();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            pane.SelectAccount(AccountRole.Mallbot);

            await refresh.WaitAsync(TimeSpan.FromSeconds(10)); // completes normally, no OperationCanceledException
            Assert.DoesNotContain(statuses, s => s.Contains("canceled")); // nothing lands in the new account's status
        }
        finally { root.Delete(recursive: true); }
    }
}
