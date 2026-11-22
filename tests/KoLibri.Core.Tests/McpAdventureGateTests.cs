using System.Text.Json;
using KoLibri.Core.Spiel;
using KolLibri.Mcp;
using LibKolibri.Access;
using Xunit;

namespace KoLibri.Core.Tests;

public sealed class McpAdventureGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kolibri-adv-" + Guid.NewGuid().ToString("N"));
    private readonly GrantStore _grants;
    private readonly AccessGate _gate;
    private readonly UsageLedger _ledger;
    private readonly AdventureExecutionFixtureTransport _transport = new();
    private readonly AdventureMcpExecutionBridge _bridge;
    private readonly KolibriMcpServer _server;

    public McpAdventureGateTests()
    {
        _grants = new GrantStore(Path.Combine(_dir, "g.json"));
        _ledger = new UsageLedger(Path.Combine(_dir, "u.jsonl"));
        _gate = new AccessGate(_grants, _ledger);
        var store = new InMemoryAdventureRunCheckpointStore();
        _bridge = new AdventureMcpExecutionBridge(new TransportAdventureExecutionGatewayFactory(_transport, store, "kolibri-mcp-gated"), store, auditPath: "<memory>");
        _server = new KolibriMcpServer(runtime: null, options: new KolibriMcpServerOptions(), executionBridge: _bridge);
        _server.Authorizer = McpAccessPolicy.ForToken(_gate, () => McpRequestContext.Token, "Main");
    }

    public void Dispose() { McpRequestContext.Token = null; _bridge.Dispose(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string Token(long? adventures)
    {
        var rights = new Dictionary<string, RightLimit> { [Rights.AccountRead] = new() };
        if (adventures is not null) rights[Rights.Adventure] = new() { DailyBudget = adventures };
        return _grants.Create("ai", ["Main"], rights, TimeSpan.FromDays(1)).Token;
    }

    private async Task<(bool Error, string Text)> RunAsync(string token, int maxTurns)
    {
        McpRequestContext.Token = token;
        var runId = "r-" + Guid.NewGuid().ToString("N");
        var preview = await CallAsync(runId, maxTurns, null);
        if (preview.Error || !preview.Text.Contains("planId")) return preview;
        var planId = JsonDocument.Parse(preview.Text).RootElement.GetProperty("planId").GetString();
        return await CallAsync(runId, maxTurns, planId);
    }

    private async Task<(bool Error, string Text)> CallAsync(string runId, int maxTurns, string? planId)
    {
        _transport.EnqueueObservation(AdventureTransportOperation.Adventure,
            new AdventureKernelObservation { AdventureId = "skeleton", Kind = AdventureEncounterKind.Reward, CanContinue = false, ResponseHash = "reward" });
        var json = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"kol_adventure_run_execute\",\"arguments\":"
            + "{\"role\":\"Main\",\"locationId\":\"skeleton\",\"runId\":\"" + runId + "\",\"execute\":true," + (planId is null ? "" : "\"planId\":\"" + planId + "\",") + "\"maxTurns\":" + maxTurns
            + ",\"maxRuns\":1,\"maxGatewaySteps\":3,\"maxDurationSeconds\":30,\"snapshot\":{\"turnsRemaining\":" + maxTurns + "}}}}";
        var res = (await _server.HandleAsync(JsonDocument.Parse(json).RootElement))!.Value.GetProperty("result");
        return (res.GetProperty("isError").GetBoolean(), res.GetProperty("content")[0].GetProperty("text").GetString()!);
    }

    [Fact]
    public async Task The_grant_is_the_approval_and_the_budget_limits_turns()
    {
        var token = Token(adventures: 5);
        var first = await RunAsync(token, 3);
        Assert.False(first.Error, first.Text);
        Assert.Contains("\"executed\":true", first.Text.Replace(" ", ""));
        // Only the requests that really went out are booked, not the reserved cap.
        var used = _ledger.UsedToday(_grants.FindByToken(token)!.Id, Rights.Adventure);
        Assert.Equal(_transport.RequestCount, used);
        Assert.InRange(used, 1, 3);

        var second = await RunAsync(token, 5);   // the reservation (5) no longer fits next to what was used
        Assert.True(second.Error);
        Assert.Contains("BudgetExhausted", second.Text);
    }

    [Fact]
    public async Task An_empty_choice_chain_run_costs_nothing()
    {
        var token = _grants.Create("ai", ["Main"], new Dictionary<string, RightLimit>
        {
            [Rights.AccountRead] = new(), [Rights.Adventure] = new() { DailyBudget = 50 },
        }, TimeSpan.FromDays(1)).Token;
        McpRequestContext.Token = token;
        string Args(string? plan) => "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"kol_choice_chain_execute\",\"arguments\":"
            + "{\"role\":\"Main\",\"locationId\":\"skeleton\",\"runId\":\"unknown-run\",\"execute\":true," + (plan is null ? "" : "\"planId\":\"" + plan + "\",") + "\"maxDurationSeconds\":30}}}";
        async Task<string> Call(string? plan)
        {
            var res = (await _server.HandleAsync(JsonDocument.Parse(Args(plan)).RootElement))!.Value.GetProperty("result");
            return res.GetProperty("content")[0].GetProperty("text").GetString()!;
        }
        var preview = await Call(null);
        var planId = JsonDocument.Parse(preview).RootElement.GetProperty("planId").GetString();
        var run = await Call(planId);
        Assert.Contains("\"executed\":false", run.Replace(" ", ""));
        Assert.Equal(0, _ledger.UsedToday(_grants.FindByToken(token)!.Id, Rights.Adventure));
    }

    [Fact]
    public async Task Execute_without_plan_only_previews_and_sends_nothing()
    {
        var token = Token(adventures: 5);
        McpRequestContext.Token = token;
        var res = await CallAsync("r-p", 3, null);
        Assert.False(res.Error, res.Text);
        Assert.Contains("\"preview\":true", res.Text.Replace(" ", ""));
        Assert.Equal(0, _transport.RequestCount);
        Assert.Equal(0, _ledger.UsedToday(_grants.FindByToken(token)!.Id, Rights.Adventure));
        var replay = await CallAsync("r-p", 3, "does-not-exist");
        Assert.True(replay.Error);
        Assert.Contains("PlanInvalid", replay.Text);
    }

    [Fact]
    public async Task Without_the_adventure_right_nothing_is_sent()
    {
        var res = await RunAsync(Token(adventures: null), 1);
        Assert.True(res.Error);
        Assert.Contains("RightNotGranted", res.Text);
        Assert.Equal(0, _transport.RequestCount);
    }
}
