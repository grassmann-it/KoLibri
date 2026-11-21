using System.Text.Json;
using KolLibri.Mcp;
using Xunit;
using KolArbitrage.Core.Spiel;

namespace KolArbitrage.Core.Tests;

public sealed class DailyPlanExecutionToolTests
{
    [Fact]
    public void Preview_without_execute_returns_plan_only()
    {
        var transport = new AdventureExecutionFixtureTransport();
        var store = new InMemoryAdventureRunCheckpointStore();
        var factory = new TransportAdventureExecutionGatewayFactory(transport, store, expectedToken: "allow-token");
        using var bridge = new AdventureMcpExecutionBridge(factory, store, auditPath: "<memory>");
        var server = new KolibriMcpServer(runtime: null, options: new KolibriMcpServerOptions(), executionBridge: bridge);

        var payload = Invoke(server, "kol_plan_daily_adventures_execute", JsonDocument.Parse("""
            {
              "state":{"knucklebones":50,"adventureTurnsRemaining":50,"luckySurpriseEggs":0,"foreignBribeBags":0,"pirateEncryptionKeys":0,"elfGuardPresents":0,"crimbucaneerBooty":0,"skeletonOfCrimbosPastAvailable":true},
              "options":{"dailyKnucklebonesTarget":100,"expectedKnucklebonesPerAdventure":0.5}
            }
            """).RootElement.Clone());

        Assert.Equal(JsonValueKind.Object, payload.ValueKind);
        Assert.True((bool)payload.GetProperty("previewOnly").GetBoolean());
        Assert.True((bool)payload.GetProperty("readOnly").GetBoolean());
        Assert.False((bool)payload.GetProperty("executed").GetBoolean());
        Assert.True(payload.TryGetProperty("plan", out _));
    }

    [Fact]
    public async Task Execute_with_token_runs_plan_steps()
    {
        var transport = new AdventureExecutionFixtureTransport();
        var store = new InMemoryAdventureRunCheckpointStore();
        var factory = new TransportAdventureExecutionGatewayFactory(transport, store, expectedToken: "allow-token");
        using var bridge = new AdventureMcpExecutionBridge(factory, store, auditPath: "<memory>");
        var server = new KolibriMcpServer(runtime: null, options: new KolibriMcpServerOptions(), executionBridge: bridge);

        var payload = await InvokeAsync(server, "kol_plan_daily_adventures_execute", JsonDocument.Parse("""
            {
              "state":{"knucklebones":50,"adventureTurnsRemaining":50,"luckySurpriseEggs":0,"foreignBribeBags":0,"pirateEncryptionKeys":0,"elfGuardPresents":0,"crimbucaneerBooty":0,"skeletonOfCrimbosPastAvailable":true},
              "options":{"dailyKnucklebonesTarget":100,"expectedKnucklebonesPerAdventure":0.5},
              "execute":true,
              "approvalToken":"allow-token",
              "maxSteps":5
            }
            """).RootElement.Clone());

        Assert.True((bool)payload.GetProperty("executed").GetBoolean());
        var stopReason = payload.GetProperty("stopReason").GetString();
        Assert.True(stopReason is "Completed" or "StepLimitReached" or "DeadlineReached",
            $"Unexpected stopReason '{stopReason}'.");
    }

    [Fact]
    public void Server_without_bridge_does_not_register_live_plan_tool()
    {
        var server = new KolibriMcpServer(runtime: null, options: new KolibriMcpServerOptions());
        Assert.DoesNotContain(server.Tools.Tools, t => t.Name == "kol_plan_daily_adventures_execute");
    }

    private static JsonElement Invoke(KolibriMcpServer server, string tool, JsonElement arguments)
        => InvokeAsync(server, tool, arguments).GetAwaiter().GetResult();

    private static async Task<JsonElement> InvokeAsync(KolibriMcpServer server, string tool, JsonElement arguments)
    {
        var argsJson = arguments.GetRawText();
        var envelopeJson = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":" + argsJson + "}}";
        var envelope = JsonDocument.Parse(envelopeJson).RootElement.Clone();
        var response = await server.HandleAsync(envelope, CancellationToken.None);
        Assert.NotNull(response);
        var result = response!.Value.GetProperty("result");
        return result.GetProperty("structuredContent").Clone();
    }
}
