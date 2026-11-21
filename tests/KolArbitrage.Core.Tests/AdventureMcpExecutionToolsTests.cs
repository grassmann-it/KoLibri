using System.Text.Json;
using KolLibri.Mcp;
using Xunit;
using KolArbitrage.Core.Basis;
using KolArbitrage.Core.Spiel;

namespace KolArbitrage.Core.Tests;

/// <summary>
/// Tests for the bounded-runner MCP wiring added in P1. The execution bridge
/// runs against an in-memory gateway and an <see cref="AdventureExecutionFixtureTransport"/>;
/// we never touch a live KoL session.
/// </summary>
public sealed class AdventureMcpExecutionToolsTests
{
    [Fact]
    public void Run_execute_without_execute_flag_returns_preview_only()
    {
        var transport = new AdventureExecutionFixtureTransport();
        var store = new KolArbitrage.Core.Spiel.InMemoryAdventureRunCheckpointStore();
        var factory = new TransportAdventureExecutionGatewayFactory(transport, store);
        using var bridge = new AdventureMcpExecutionBridge(factory, store, auditPath: "<memory>");
        var server = new KolibriMcpServer(runtime: null, options: new KolibriMcpServerOptions(), executionBridge: bridge);

        var result = server.Tools.Get("kol_adventure_run_execute");
        Assert.NotNull(result);

        var arguments = Parse("""
            {"locationId":"skeleton-of-crimbos-past","runId":"preview-1","maxTurns":1,"maxRuns":1}
            """);
        var payload = Invoke(server, "kol_adventure_run_execute", arguments).GetAwaiter().GetResult();

        Assert.Equal(JsonValueKind.Object, payload.ValueKind);
        Assert.Equal(false, ReadProperty(payload, "executed"));
        Assert.Equal("ReadyToRun", ReadProperty(payload, "stopReason"));
        Assert.Equal(0, transport.RequestCount);
    }

    [Fact]
    public async Task Run_execute_with_token_drives_bounded_runner_via_factory()
    {
        var transport = new AdventureExecutionFixtureTransport();
        transport.EnqueueObservation(AdventureTransportOperation.Adventure,
            new AdventureKernelObservation
            {
                AdventureId = "skeleton", Kind = AdventureEncounterKind.Reward,
                CanContinue = false, ResponseHash = "reward"
            });
        var store = new KolArbitrage.Core.Spiel.InMemoryAdventureRunCheckpointStore();
        var factory = new TransportAdventureExecutionGatewayFactory(transport, store, expectedToken: "allow-token");
        using var bridge = new AdventureMcpExecutionBridge(factory, store, auditPath: "<memory>");
        var server = new KolibriMcpServer(runtime: null, options: new KolibriMcpServerOptions(), executionBridge: bridge);

        var role = AccountRole.Main.ToString();
        var runId = "chain-" + Guid.NewGuid().ToString("N");
        var argsJson = "{\"role\":\"" + role + "\",\"locationId\":\"skeleton\",\"runId\":\"" + runId + "\",\"execute\":true,\"approvalToken\":\"allow-token\",\"maxTurns\":1,\"maxRuns\":1,\"maxGatewaySteps\":3,\"maxDurationSeconds\":30,\"snapshot\":{\"turnsRemaining\":1}}";
        var arguments = Parse(argsJson);
        var payload = await Invoke(server, "kol_adventure_run_execute", arguments);

        Assert.Equal(true, ReadProperty(payload, "executed"));
        Assert.Equal("Complete", ReadProperty(payload, "stopReason"));
        Assert.True(transport.RequestCount >= 1);
    }

    [Fact]
    public async Task Run_execute_rejects_mismatched_token()
    {
        var transport = new AdventureExecutionFixtureTransport();
        var store = new KolArbitrage.Core.Spiel.InMemoryAdventureRunCheckpointStore();
        var factory = new TransportAdventureExecutionGatewayFactory(transport, store, expectedToken: "allow-token");
        using var bridge = new AdventureMcpExecutionBridge(factory, store, auditPath: "<memory>");
        var server = new KolibriMcpServer(runtime: null, options: new KolibriMcpServerOptions(), executionBridge: bridge);

        var runId = "mismatch-" + Guid.NewGuid().ToString("N");
        var argsJson = "{\"locationId\":\"skeleton\",\"runId\":\"" + runId + "\",\"execute\":true,\"approvalToken\":\"wrong-token\",\"snapshot\":{\"turnsRemaining\":1}}";
        var arguments = Parse(argsJson);
        var payload = await Invoke(server, "kol_adventure_run_execute", arguments);
        // Mismatched token means the factory refuses; we get an MCP error
        // (no structuredContent) or a GatewayStopped stop reason.
        Assert.False(payload.TryGetProperty("structuredContent", out _)
            && ReadProperty(payload, "stopReason")?.ToString() == "Complete",
            $"Mismatch token should not produce Complete; payload={payload.GetRawText()}");
        Assert.Equal(0, transport.RequestCount);
    }

    [Fact]
    public void Choice_chain_execute_without_execute_flag_returns_preview_only()
    {
        var transport = new AdventureExecutionFixtureTransport();
        var store = new KolArbitrage.Core.Spiel.InMemoryAdventureRunCheckpointStore();
        var factory = new TransportAdventureExecutionGatewayFactory(transport, store);
        using var bridge = new AdventureMcpExecutionBridge(factory, store, auditPath: "<memory>");
        var server = new KolibriMcpServer(runtime: null, options: new KolibriMcpServerOptions(), executionBridge: bridge);

        var arguments = Parse("""
            {"runId":"preview-chain","locationId":"skeleton","choiceActions":"choice-1:1"}
            """);
        var payload = Invoke(server, "kol_choice_chain_execute", arguments).GetAwaiter().GetResult();

        Assert.Equal(JsonValueKind.Object, payload.ValueKind);
        Assert.Equal(false, ReadProperty(payload, "executed"));
        Assert.Equal(0, transport.RequestCount);
    }

    [Fact]
    public async Task Choice_chain_execute_with_token_runs_against_existing_checkpoint()
    {
        var transport = new AdventureExecutionFixtureTransport();
        var store = new KolArbitrage.Core.Spiel.InMemoryAdventureRunCheckpointStore();
        var factory = new TransportAdventureExecutionGatewayFactory(transport, store, expectedToken: "allow-token");
        var allowlist = new AdventureActionAllowlist()
            .AddAdventure("skeleton")
            .AddEncounter("skeleton", "skeleton")
            .AddChoiceOption("skeleton", "choice-1", "1", "skeleton");

        transport.EnqueueObservation(AdventureTransportOperation.Adventure,
            new AdventureKernelObservation
            {
                AdventureId = "skeleton", Kind = AdventureEncounterKind.Choice, EncounterId = "skeleton",
                ChoiceId = "choice-1", AvailableChoices = new[] { "1" }, CanContinue = true, ResponseHash = "c1"
            });
        await using var seeded = await factory.CreateAsync(AccountRole.Main, "allow-token", CancellationToken.None);
        var started = await seeded!.StartAsync(new AdventureRunRequest
        {
            RunId = "seeded-chain",
            AdventureId = "skeleton",
            Role = AccountRole.Main
        }, allowlist);
        Assert.IsType<AdventureChoiceAction>(started.NextAction);

        transport.EnqueueObservation(AdventureTransportOperation.Choice,
            new AdventureKernelObservation
            {
                AdventureId = "skeleton", Kind = AdventureEncounterKind.Reward, CanContinue = false, ResponseHash = "reward"
            });

        using var bridge = new AdventureMcpExecutionBridge(factory, store, auditPath: "<memory>");
        var server = new KolibriMcpServer(runtime: null, options: new KolibriMcpServerOptions(), executionBridge: bridge);

        var arguments = Parse("""
            {
              "runId":"seeded-chain",
              "locationId":"skeleton",
              "choiceActions":"choice-1:1",
              "execute":true,
              "approvalToken":"allow-token",
              "maxChoices":3,
              "maxDurationSeconds":30
            }
            """);
        var payload = await Invoke(server, "kol_choice_chain_execute", arguments);

        Assert.Equal(true, ReadProperty(payload, "executed"));
        Assert.Equal("Complete", ReadProperty(payload, "stopReason"));
        Assert.True(transport.Count(AdventureTransportOperation.Choice) >= 1);
    }

    [Fact]
    public void Server_without_execution_bridge_does_not_register_live_tools()
    {
        var server = new KolibriMcpServer(runtime: null, options: new KolibriMcpServerOptions());
        Assert.DoesNotContain(server.Tools.Tools, t => t.Name == "kol_adventure_run_execute");
        Assert.DoesNotContain(server.Tools.Tools, t => t.Name == "kol_choice_chain_execute");
    }

    private static JsonElement Parse(string raw)
        => JsonDocument.Parse(raw).RootElement.Clone();

    private static async Task<JsonElement> Invoke(KolibriMcpServer server, string tool, JsonElement arguments)
    {
        var argsJson = arguments.GetRawText();
        var envelopeJson = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":" + argsJson + "}}";
        var envelope = JsonDocument.Parse(envelopeJson).RootElement.Clone();
        var response = await server.HandleAsync(envelope, CancellationToken.None);
        Assert.NotNull(response);
        var result = response!.Value.GetProperty("result");
        if (result.TryGetProperty("structuredContent", out var structured))
            return structured.Clone();
        return result.Clone();
    }

    private static object? ReadProperty(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object) return null;
        if (!payload.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.TryGetInt64(out var n) ? n : value.GetDouble(),
            JsonValueKind.Null => null,
            _ => value.Clone()
        };
    }
}
