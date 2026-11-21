using KolArbitrage.Core.Basis;
using KolArbitrage.Core.Spiel;

namespace KolLibri.Mcp;

/// <summary>
/// Result returned by the bounded execution bridge. The MCP layer serializes
/// the inner <see cref="BoundedAdventureRunResult"/> / <see cref="BoundedChoiceChainResult"/>
/// alongside a couple of meta fields so callers can correlate with the audit
/// ledger.
/// </summary>
public sealed record AdventureExecutionBridgeResult(
    bool Executed,
    string StopReason,
    IReadOnlyList<object> Steps,
    string? AuditPath,
    string? RequestId);

/// <summary>
/// Shared bridge that turns an MCP tool call into a bounded runner invocation.
/// Preview paths short-circuit before any transport is created.
/// </summary>
public sealed class AdventureMcpExecutionBridge : IDisposable
{
    private readonly IAdventureExecutionGatewayFactory _factory;
    private readonly IAdventureRunCheckpointStore _checkpointStore;
    private readonly IAuditEventJournal _auditJournal;
    private readonly string _auditPath;

    public AdventureMcpExecutionBridge(
        IAdventureExecutionGatewayFactory factory,
        IAdventureRunCheckpointStore checkpointStore,
        IAuditEventJournal? auditJournal = null,
        string? auditPath = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
        _auditJournal = auditJournal ?? new AuditEventJournal(
            auditPath ?? Path.Combine(Path.GetTempPath(), "kolibri-mcp-audit.jsonl"));
        _auditPath = auditPath ?? "<memory>";
    }

    public string AuditPath => _auditPath;

    public void Dispose()
    {
        if (_auditJournal is IDisposable disposable) disposable.Dispose();
    }

    public async ValueTask<AdventureExecutionBridgeResult> RunBoundedAsync(
        AccountRole role,
        AdventureRunRequest request,
        AdventureActionAllowlist allowlist,
        IReadOnlyList<AdventureCondition> conditions,
        IAdventureConditionSnapshotProvider snapshotProvider,
        BoundedAdventureRunnerOptions options,
        bool execute,
        string? approvalToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(allowlist);

        if (!execute)
            return PreviewResult(BoundedAdventureRunStopReason.ReadyToRun.ToString(), Array.Empty<object>());

        var gateway = await ResolveGatewayAsync(role, approvalToken, cancellationToken).ConfigureAwait(false);

        var runner = new BoundedAdventureRunner(gateway);
        var result = await runner.RunAsync(request, allowlist, conditions, snapshotProvider, options, cancellationToken)
            .ConfigureAwait(false);
        return ToBridgeResult(result.Steps, result.StopReason.ToString());
    }

    public async ValueTask<AdventureExecutionBridgeResult> RunChoiceChainAsync(
        AccountRole role,
        string runId,
        AdventureActionAllowlist allowlist,
        BoundedChoiceChainOptions options,
        bool execute,
        string? approvalToken,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(allowlist);

        if (!execute)
            return PreviewResult(BoundedChoiceChainStopReason.Complete.ToString(), Array.Empty<object>());

        var gateway = await ResolveGatewayAsync(role, approvalToken, cancellationToken).ConfigureAwait(false);

        var runner = new BoundedChoiceChainRunner(gateway);
        var result = await runner.RunAsync(runId.Trim(), allowlist, options, cancellationToken).ConfigureAwait(false);
        return ToBridgeResult(result.Steps, result.StopReason.ToString());
    }

    private async ValueTask<AdventureExecutionGateway> ResolveGatewayAsync(
        AccountRole role,
        string? approvalToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(approvalToken))
            throw new McpInvalidParamsException("execute=true requires an approvalToken.");

        var gateway = await _factory.CreateAsync(role, approvalToken!, cancellationToken).ConfigureAwait(false)
            ?? throw new McpInvalidParamsException(
                $"No live adventure execution transport is configured for role {role}.");
        return gateway;
    }

    private AdventureExecutionBridgeResult PreviewResult(string stopReason, IReadOnlyList<object> steps)
        => new(Executed: false, StopReason: stopReason, Steps: steps, AuditPath: _auditPath, RequestId: null);

    private static AdventureExecutionBridgeResult ToBridgeResult(
        IReadOnlyList<AdventureExecutionResult> steps,
        string stopReason)
        => new(
            Executed: true,
            StopReason: stopReason,
            Steps: steps.Select(step => new
            {
                status = step.Status.ToString(),
                requestId = step.RequestId,
                responseHash = step.Response?.ResponseHash,
                abortReason = step.AbortReason?.ToString(),
                requiresRefresh = step.RequiresRefresh,
                complete = step.IsComplete
            }).Cast<object>().ToArray(),
            AuditPath: "<memory>",
            RequestId: steps.Count == 0 ? null : steps[^1].RequestId);
}

/// <summary>
/// In-memory checkpoint store used when the MCP host wants bounded execution
/// without writing to disk. The bounded runners read/write checkpoints per
/// run id and a single instance is safe for one tool call.
/// </summary>
