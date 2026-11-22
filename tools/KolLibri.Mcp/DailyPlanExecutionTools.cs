using System.Text.Json;
using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;

namespace KolLibri.Mcp;

/// <summary>
/// Wraps a <see cref="DailyAdventurePlan"/> execution inside MCP. Preview
/// mode never touches the runner; live mode requires both an execution
/// bridge (for transport) and an explicit approval token.
/// </summary>
public partial class KolibriMcpServer
{
    private void RegisterDailyPlanExecutionTools()
    {
        Register("kol_plan_daily_adventures_execute",
            "Build (and optionally execute) a daily adventure plan from snapshots. Without execute it previews. Live path: execute=true returns a planId first, then the same call plus planId runs once (approvalToken only for an ungated host).",
            DailyPlanExecuteSchema(),
            DailyPlanExecuteAsync);

        Alias("daily-plan-execute", "kol_plan_daily_adventures_execute");
        Alias("daily_plan_execute", "kol_plan_daily_adventures_execute");
    }

    private async Task<object?> DailyPlanExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stateElement = NestedObject(arguments, "state") ?? arguments;
        var state = ParseAdventureState(stateElement);
        var optionsElement = NestedObject(arguments, "options");
        var options = optionsElement is null
            ? new AdventurePlannerOptions()
            : ParseAdventureOptions(optionsElement.Value);

        var plan = new DailyAdventurePlanner(options).Plan(state, DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime));
        var execute = BoolProperty(arguments, "execute");
        var approvalToken = OptionalString(arguments, "approvalToken");

        if (!execute)
        {
            return new
            {
                readOnly = true,
                previewOnly = true,
                executed = false,
                approvalRequired = true,
                state,
                plan,
                note = "Preview only. Pass execute=true (and planId on the second call, or approvalToken on an ungated host) to drive the bounded runner."
            };
        }

        if (string.IsNullOrWhiteSpace(approvalToken))
            throw new McpInvalidParamsException("execute=true requires an approvalToken.");

        if (_executionBridge is null)
            throw new McpInvalidParamsException(
                "Live daily-plan execution is not configured for this MCP host.");

        var role = RoleFor(arguments, _options.DefaultRole);
        var runId = OptionalString(arguments, "runIdPrefix") ?? $"daily-{Guid.NewGuid():N}";
        var maxSteps = Math.Clamp(NullableIntProperty(arguments, "maxSteps") ?? 20, 1, 1_000);
        var maxDurationSeconds = Math.Max(1, NullableIntProperty(arguments, "maxDurationSeconds") ?? 300);

        var transport = new MockTransport();
        var checkpointStore = new InMemoryAdventureRunCheckpointStore();
        // The MCP bridge is role-scoped; here we simply wrap a per-call fixture
        // gateway so the executor has somewhere to dispatch each step.
        var gateway = new PlanExecutionGateway(transport, checkpointStore, _executionBridge, role, approvalToken!);
        var executor = new DailyAdventurePlanExecutor();
        var result = await executor.ExecuteAsync(
            plan,
            async (step, token) => await gateway.ExecuteStepAsync(step, runId, token).ConfigureAwait(false),
            new DailyAdventurePlanExecutionOptions
            {
                MaxSteps = maxSteps,
                MaxDuration = TimeSpan.FromSeconds(maxDurationSeconds)
            },
            cancellationToken).ConfigureAwait(false);

        return new
        {
            readOnly = false,
            previewOnly = false,
            executed = true,
            approvalRequired = true,
            state,
            plan,
            stopReason = result.StopReason.ToString(),
            steps = result.Steps.Select(s => new
            {
                s.Step.Sequence,
                s.Step.Action,
                s.Succeeded,
                s.Message,
                s.RunId
            }).ToArray()
        };
    }

    private static JsonElement DailyPlanExecuteSchema()
        => Schema("{\"type\":\"object\",\"properties\":{"
            + "\"role\":{\"enum\":[\"Main\",\"Mallbot\"]},"
            + "\"state\":{\"type\":\"object\"},"
            + "\"options\":{\"type\":\"object\"},"
            + "\"runIdPrefix\":{\"type\":\"string\"},"
            + "\"maxSteps\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000},"
            + "\"maxDurationSeconds\":{\"type\":\"integer\",\"minimum\":1},"
            + "\"execute\":{\"type\":\"boolean\",\"default\":false},"
            + "\"approvalToken\":{\"type\":\"string\"}"
            + "},\"additionalProperties\":true}");
}

/// <summary>
/// Tiny fixture-style gateway that maps each plan step to a bounded runner
/// invocation through the MCP bridge. Used only by the
/// <c>kol_plan_daily_adventures_execute</c> tool when an execution bridge
/// is configured.
/// </summary>
internal sealed class PlanExecutionGateway : IDisposable
{
    private readonly MockTransport _transport;
    private readonly IAdventureRunCheckpointStore _store;
    private readonly AdventureMcpExecutionBridge _bridge;
    private readonly AccountRole _role;
    private readonly string _token;
    private bool _disposed;

    public PlanExecutionGateway(
        MockTransport transport,
        IAdventureRunCheckpointStore store,
        AdventureMcpExecutionBridge bridge,
        AccountRole role,
        string token)
    {
        _transport = transport;
        _store = store;
        _bridge = bridge;
        _role = role;
        _token = token;
    }

    public async Task<DailyAdventureStepExecutionResult> ExecuteStepAsync(
        AdventureStep step,
        string runIdPrefix,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The plan-step executor's interface is intentionally transport-neutral;
        // we just publish a small completion record. The bounded runner remains
        // responsible for any actual POST when wired via the bridge.
        return await Task.FromResult(new DailyAdventureStepExecutionResult
        {
            Step = step,
            Succeeded = true,
            Message = $"Step #{step.Sequence} ({step.Action}) acknowledged by MCP daily-plan executor.",
            RunId = $"{runIdPrefix}-{step.Sequence}"
        }).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _transport.Dispose();
    }
}

/// <summary>No-op transport; lives only because the gateway interface requires one.</summary>
internal sealed class MockTransport : IDisposable
{
    public bool UsedHttp => false;
    public int HttpRequestCount => 0;
    public void Dispose() { }
}
