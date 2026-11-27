using System.Text.Json;
using KolLibri.Mcp;
using KoLibri.Safety.Access;
using Xunit;

namespace KoLibri.Tests;

/// <summary>
/// The MCP adventure tools on KoLibri's Adventurer (0.5; replaces 0.4's AdventureMcpExecutionToolsTests,
/// McpAdventureGateTests and the MCP part of AdventureChoiceHardeningTests). Everything runs against the fake KoL.
/// </summary>
public sealed class McpAdventureToolsTests : IDisposable
{
    static McpAdventureToolsTests()
    {
        // Never read the user's real choice policy in tests.
        Environment.SetEnvironmentVariable("KOLIBRI_CHOICE_POLICY",
            Path.Combine(Path.GetTempPath(), "kolibri-no-policy-" + Guid.NewGuid().ToString("N") + ".json"));
    }

    private readonly string _dir = Directory.CreateTempSubdirectory("kolibri-mcp-adv-").FullName;
    private readonly FakeKol _kol = new();
    private readonly McpAccounts _accounts;
    private readonly AdventureMcpExecutionBridge _bridge;
    private readonly KolibriMcpServer _server;

    public McpAdventureToolsTests()
    {
        _accounts = McpTest.Accounts(_kol);
        _bridge = new AdventureMcpExecutionBridge(_accounts);
        _server = new KolibriMcpServer(_accounts, new KolibriMcpServerOptions(), executionBridge: _bridge);
    }

    public void Dispose()
    {
        McpRequestContext.Token = null;
        _bridge.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static JsonElement Structured(JsonElement response)
    {
        var result = response.GetProperty("result");
        Assert.False(result.GetProperty("isError").GetBoolean(), result.GetRawText());
        return result.GetProperty("structuredContent");
    }

    private static string ErrorText(JsonElement response)
    {
        var result = response.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean(), result.GetRawText());
        return result.GetProperty("content")[0].GetProperty("text").GetString()!;
    }

    private Task<JsonElement> Call(string tool, string arguments) => McpReadToolsTests.CallAsync(_server, tool, arguments);

    private void FightThenWin()
        => _kol.On("POST adventure.php", (_, _) => FakeKol.Redirect("https://www.kingdomofloathing.com/fight.php"))
            .On("GET fight.php", ActionTests.FightRound1)
            .On("POST fight.php", (_, form) =>
            {
                Assert.Equal("action=attack", form);
                _kol.StatusAdventures--;
                return FakeKol.Html(ActionTests.FightWon);
            });

    private void ChoiceOpens()
        => _kol.On("POST adventure.php", (_, _) => FakeKol.Redirect("https://www.kingdomofloathing.com/choice.php"))
            .On("GET choice.php", ActionTests.ChoicePage);

    [Fact]
    public void Server_without_execution_bridge_does_not_register_live_tools()
    {
        var server = new KolibriMcpServer(_accounts);
        Assert.DoesNotContain(server.Tools.Tools, t => t.Name == "kol_adventure_run_execute");
        Assert.DoesNotContain(server.Tools.Tools, t => t.Name == "kol_choice_chain_execute");
        Assert.DoesNotContain(server.Tools.Tools, t => t.Name == "kol_plan_daily_adventures_execute");
    }

    [Fact]
    public async Task Daily_plan_execution_is_a_placeholder_that_sends_nothing()
    {
        var experimental = new KolibriMcpServer(_accounts, new KolibriMcpServerOptions { ExperimentalTools = true }, executionBridge: _bridge);
        var text = ErrorText(await McpReadToolsTests.CallAsync(experimental, "kol_plan_daily_adventures_execute",
            """{"role":"Main","execute":true,"approvalToken":"t"}"""));

        Assert.Contains(KolibriMcpServer.NotYetMessage, text);
        Assert.Empty(_kol.Requests);
    }

    [Fact]
    public async Task Run_without_execute_is_a_preview_and_sends_nothing()
    {
        var payload = Structured(await Call("kol_adventure_run_execute", """{"locationId":"skeleton","runId":"p"}"""));

        Assert.False(payload.GetProperty("executed").GetBoolean());
        Assert.Equal("ReadyToRun", payload.GetProperty("stopReason").GetString());
        Assert.Empty(_kol.Requests);
    }

    [Fact]
    public async Task Live_run_without_a_token_or_location_number_sends_nothing()
    {
        Assert.Contains("approvalToken", ErrorText(await Call("kol_adventure_run_execute", """{"locationId":"adventure=113","execute":true}""")));
        Assert.Contains("snarfblat", ErrorText(await Call("kol_adventure_run_execute", """{"locationId":"skeleton","execute":true,"approvalToken":"t"}""")));
        Assert.Empty(_kol.Requests);
    }

    [Fact]
    public async Task Live_run_sends_the_numeric_snarfblat_and_fights_with_the_first_allowed_action()
    {
        FightThenWin();

        var payload = Structured(await Call("kol_adventure_run_execute",
            """{"locationId":"adventure=543:Your Mushroom Garden","runId":"snarf","execute":true,"approvalToken":"t","fightActions":"attack","maxGatewaySteps":3}"""));

        Assert.True(payload.GetProperty("executed").GetBoolean());
        Assert.Equal("Complete", payload.GetProperty("stopReason").GetString());
        Assert.Contains(_kol.Requests, r => r is { Method: "POST", Path: "/adventure.php", Form: "snarfblat=543" });
        Assert.Equal(1, _kol.Count("POST", "adventure.php"));
    }

    [Fact]
    public async Task A_fight_without_an_allowed_action_stops_the_run_without_attacking()
    {
        FightThenWin();

        var payload = Structured(await Call("kol_adventure_run_execute",
            """{"locationId":"adventure=113","execute":true,"approvalToken":"t"}"""));

        Assert.Equal("GatewayStopped", payload.GetProperty("stopReason").GetString());
        Assert.Equal(0, _kol.Count("POST", "fight.php"));
    }

    [Fact]
    public async Task An_unknown_choice_stops_the_run_with_needs_decision_and_is_not_answered()
    {
        ChoiceOpens();

        var payload = Structured(await Call("kol_adventure_run_execute",
            """{"locationId":"adventure=113","runId":"c","execute":true,"approvalToken":"t"}"""));

        Assert.Equal("NeedsDecision", payload.GetProperty("stopReason").GetString());
        var needs = payload.GetProperty("needsDecision");
        Assert.Equal("1234", needs.GetProperty("choiceId").GetString());
        Assert.Equal(2, needs.GetProperty("options").GetArrayLength());
        Assert.Equal(0, _kol.Count("POST", "choice.php"));
    }

    [Fact]
    public async Task Choice_pending_reads_the_open_choice_only()
    {
        _kol.On("GET main.php", (_, _) => FakeKol.Redirect("https://www.kingdomofloathing.com/choice.php"))
            .On("GET choice.php", ActionTests.ChoicePage);

        var payload = Structured(await Call("kol_choice_pending", """{"role":"Main"}"""));

        Assert.True(payload.GetProperty("open").GetBoolean());
        Assert.Equal("Choice", payload.GetProperty("kind").GetString());
        Assert.Equal("1234", payload.GetProperty("choiceId").GetString());
        Assert.DoesNotContain(_kol.Requests, r => r.Method == "POST");
    }

    [Fact]
    public async Task Choice_chain_preview_sends_nothing_and_live_answers_from_choice_actions()
    {
        _kol.On("GET main.php", (_, _) => FakeKol.Redirect("https://www.kingdomofloathing.com/choice.php"))
            .On("GET choice.php", ActionTests.ChoicePage)
            .On("POST choice.php", (_, form) =>
            {
                Assert.Contains("option=2", form);
                Assert.Contains("whichchoice=1234", form);
                return FakeKol.Html(ActionTests.ChoiceDone);
            });

        var preview = Structured(await Call("kol_choice_chain_execute", """{"runId":"r","choiceActions":"1234:2"}"""));
        Assert.False(preview.GetProperty("executed").GetBoolean());
        Assert.Empty(_kol.Requests);

        var live = Structured(await Call("kol_choice_chain_execute", """{"runId":"r","choiceActions":"1234:2","execute":true,"approvalToken":"t","maxChoices":1}"""));
        Assert.True(live.GetProperty("executed").GetBoolean());
        Assert.Equal(1, _kol.Count("POST", "choice.php"));
    }

    [Fact]
    public async Task Choice_chain_with_nothing_open_reports_nothing_executed()
    {
        var payload = Structured(await Call("kol_choice_chain_execute", """{"runId":"no-such-run","execute":true,"approvalToken":"t"}"""));

        Assert.False(payload.GetProperty("executed").GetBoolean());
        Assert.Equal("Complete", payload.GetProperty("stopReason").GetString());
        Assert.DoesNotContain(_kol.Requests, r => r.Method == "POST");
    }

    [Fact]
    public async Task Live_conditions_use_the_account_not_a_supplied_snapshot()
    {
        _kol.On("GET api.php?what=inventory", """{"2":5}""");
        FightThenWin();

        var payload = Structured(await Call("kol_adventure_run_execute", """
            {"locationId":"adventure=113","execute":true,"approvalToken":"t","fightActions":"attack",
             "conditions":[{"kind":"ItemCount","key":"2","target":3}],"snapshot":{"isStale":true,"items":{"2":0}}}
            """));

        Assert.Equal("ConditionsSatisfied", payload.GetProperty("stopReason").GetString());
        Assert.False(payload.GetProperty("executed").GetBoolean());
        Assert.Equal(0, _kol.Count("POST", "adventure.php"));
    }

    [Fact]
    public async Task Unsupported_conditions_are_refused_before_anything_is_sent()
    {
        var text = ErrorText(await Call("kol_adventure_run_execute",
            """{"locationId":"adventure=113","execute":true,"approvalToken":"t","conditions":[{"kind":"Meat","target":1}]}"""));

        Assert.Contains("not supported", text);
        Assert.Empty(_kol.Requests);
    }

    // --- gated (HTTP host): the grant is the approval, the budget limits turns ----------------------------------

    private (GrantStore Grants, UsageLedger Ledger) Gate()
    {
        var grants = new GrantStore(Path.Combine(_dir, "g.json"));
        var ledger = new UsageLedger(Path.Combine(_dir, "u.jsonl"));
        _server.Authorizer = McpAccessPolicy.ForToken(new AccessGate(grants, ledger), () => McpRequestContext.Token, "Main");
        return (grants, ledger);
    }

    private static string Token(GrantStore grants, long? adventures)
    {
        var rights = new Dictionary<string, RightLimit> { [Rights.AccountRead] = new() };
        if (adventures is not null) rights[Rights.Adventure] = new() { DailyBudget = adventures };
        return grants.Create("ai", ["Main"], rights, TimeSpan.FromDays(1)).Token;
    }

    private async Task<(bool Error, string Text)> GatedRun(string runId, int maxTurns, string? planId)
    {
        var args = "{\"role\":\"Main\",\"locationId\":\"adventure=113\",\"runId\":\"" + runId + "\",\"execute\":true,\"fightActions\":\"attack\","
            + (planId is null ? "" : "\"planId\":\"" + planId + "\",") + "\"maxTurns\":" + maxTurns + ",\"maxGatewaySteps\":3,\"maxDurationSeconds\":30}";
        var result = (await Call("kol_adventure_run_execute", args)).GetProperty("result");
        return (result.GetProperty("isError").GetBoolean(), result.GetProperty("content")[0].GetProperty("text").GetString()!);
    }

    private async Task<(bool Error, string Text)> GatedPlanAndRun(int maxTurns)
    {
        var runId = "r-" + Guid.NewGuid().ToString("N");
        var preview = await GatedRun(runId, maxTurns, null);
        if (preview.Error || !preview.Text.Contains("planId")) return preview;
        var planId = JsonDocument.Parse(preview.Text).RootElement.GetProperty("planId").GetString();
        return await GatedRun(runId, maxTurns, planId);
    }

    [Fact]
    public async Task Gated_the_grant_is_the_approval_and_the_budget_limits_turns()
    {
        var (grants, ledger) = Gate();
        FightThenWin();
        McpRequestContext.Token = Token(grants, adventures: 5);

        var first = await GatedPlanAndRun(3);
        Assert.False(first.Error, first.Text);
        Assert.Contains("\"executed\":true", first.Text.Replace(" ", ""));
        Assert.Equal(3, ledger.UsedToday(grants.FindByToken(McpRequestContext.Token)!.Id, Rights.Adventure));

        var second = await GatedPlanAndRun(3); // 3 + 3 > 5
        Assert.True(second.Error);
        Assert.Contains("BudgetExhausted", second.Text);
        Assert.Equal(1, _kol.Count("POST", "adventure.php"));
    }

    [Fact]
    public async Task Gated_execute_without_plan_only_previews_and_a_wrong_plan_is_refused()
    {
        var (grants, ledger) = Gate();
        McpRequestContext.Token = Token(grants, adventures: 5);

        var preview = await GatedRun("r-p", 3, null);
        Assert.False(preview.Error, preview.Text);
        Assert.Contains("\"preview\":true", preview.Text.Replace(" ", ""));
        var replay = await GatedRun("r-p", 3, "does-not-exist");
        Assert.True(replay.Error);
        Assert.Contains("PlanInvalid", replay.Text);
        Assert.DoesNotContain(_kol.Requests, r => r.Method == "POST");
        Assert.Equal(0, ledger.UsedToday(grants.FindByToken(McpRequestContext.Token)!.Id, Rights.Adventure));
    }

    [Fact]
    public async Task Gated_without_the_adventure_right_nothing_is_sent()
    {
        var (grants, _) = Gate();
        McpRequestContext.Token = Token(grants, adventures: null);

        var result = await GatedPlanAndRun(1);

        Assert.True(result.Error);
        Assert.Contains("RightNotGranted", result.Text);
        Assert.Empty(_kol.Requests);
    }

    [Fact]
    public async Task Gated_empty_choice_chain_costs_nothing()
    {
        var (grants, ledger) = Gate();
        McpRequestContext.Token = Token(grants, adventures: 50);
        string Args(string? plan) => "{\"role\":\"Main\",\"runId\":\"ghost\",\"execute\":true," + (plan is null ? "" : "\"planId\":\"" + plan + "\",") + "\"maxChoices\":20}";

        var preview = (await Call("kol_choice_chain_execute", Args(null))).GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        var planId = JsonDocument.Parse(preview).RootElement.GetProperty("planId").GetString();
        var run = Structured(await Call("kol_choice_chain_execute", Args(planId)));

        Assert.False(run.GetProperty("executed").GetBoolean());
        Assert.Equal(0, ledger.UsedToday(grants.FindByToken(McpRequestContext.Token)!.Id, Rights.Adventure));
    }
}
