using KolArbitrage.Core.Adventure;
using KolArbitrage.Core.Accounts;
using KolArbitrage.Core.Integration;

namespace KolLibri.Mcp;

/// <summary>
/// Host-supplied factory that produces an <see cref="AdventureExecutionGateway"/>
/// for a given role. Hosts that have not configured a live transport receive
/// <c>null</c>, which keeps MCP servers read-only by default.
/// </summary>
public interface IAdventureExecutionGatewayFactory
{
    ValueTask<AdventureExecutionGateway?> CreateAsync(
        AccountRole role,
        string approvalToken,
        CancellationToken cancellationToken);
}

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
/// Approval token + hook glue. The factory already verified the supplied token
/// matches the host-bound one before the gateway was created, so the per-step
/// hook simply re-approves. The transport request carries the original token
/// for audit logging.
/// </summary>
internal sealed class TokenAdventureApprovalHook : IAdventureApprovalHook
{
    private readonly string _expected;

    public TokenAdventureApprovalHook(string expected)
        => _expected = expected ?? throw new ArgumentNullException(nameof(expected));

    public ValueTask<AdventureApprovalDecision> ApproveAsync(
        AdventureApprovalContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(AdventureApprovalDecision.Allow(_expected));
    }
}

internal sealed class KnownEncounterVerificationHook : IAdventureVerificationHook
{
    public ValueTask<AdventureVerificationResult> VerifyAsync(
        AdventureVerificationContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var known = context.Observation.Kind is not AdventureEncounterKind.Unknown
                    && context.Response.Accepted != false;
        return known
            ? ValueTask.FromResult(AdventureVerificationResult.Success())
            : ValueTask.FromResult(AdventureVerificationResult.Failed(
                "The live response was not a known, accepted adventure encounter."));
    }
}

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
/// Convenience adapter that builds an <see cref="AdventureExecutionGateway"/>
/// from an already-running transport + checkpoint store. Hosts wire the actual
/// HTTP client; the bridge simply wires approval + verification hooks.
/// </summary>
public sealed class TransportAdventureExecutionGatewayFactory : IAdventureExecutionGatewayFactory
{
    private readonly IAdventureTransport _transport;
    private readonly IAdventureRunCheckpointStore _checkpointStore;
    private readonly string? _expectedToken;

    public TransportAdventureExecutionGatewayFactory(
        IAdventureTransport transport,
        IAdventureRunCheckpointStore checkpointStore,
        string? expectedToken = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
        _expectedToken = expectedToken;
    }

    public ValueTask<AdventureExecutionGateway?> CreateAsync(
        AccountRole role,
        string approvalToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_expectedToken is not null
            && !string.Equals(approvalToken, _expectedToken, StringComparison.Ordinal))
            return ValueTask.FromResult<AdventureExecutionGateway?>(null);
        var gateway = new AdventureExecutionGateway(
            _transport,
            _checkpointStore,
            approval: new TokenAdventureApprovalHook(approvalToken),
            verification: new KnownEncounterVerificationHook());
        return ValueTask.FromResult<AdventureExecutionGateway?>(gateway);
    }
}

/// <summary>
/// In-memory checkpoint store used when the MCP host wants bounded execution
/// without writing to disk. The bounded runners read/write checkpoints per
/// run id and a single instance is safe for one tool call.
/// </summary>
