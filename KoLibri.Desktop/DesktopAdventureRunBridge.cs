using System.Threading.Tasks;
using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;

namespace KoLibri.Desktop;

/// <summary>
/// Result surfaced to the shell after a bounded run. The view-model renders
/// StopReason, request id and step count; the actual step details stay in
/// <see cref="LastResult"/> for any deeper inspection.
/// </summary>
public sealed record BoundedRunResult(
    bool Executed,
    string StopReason,
    string? RequestId,
    string? AuditPath,
    int StepCount);

/// <summary>
/// Optional callback the host wires so the bridge can refuse live execution
/// when the supplied token does not match the configured one.
/// </summary>
public interface IApprovalTokenValidator
{
    bool IsValid(string? token);
}

/// <summary>
/// Default validator that compares the supplied token to the configured one.
/// Returning <c>false</c> causes the bridge to abort without ever touching the
/// runner; the audit trail still records the rejection.
/// </summary>
public sealed class FixedApprovalTokenValidator : IApprovalTokenValidator
{
    private readonly string _expected;
    public FixedApprovalTokenValidator(string expected)
        => _expected = expected ?? throw new ArgumentNullException(nameof(expected));
    public bool IsValid(string? token)
        => !string.IsNullOrWhiteSpace(token)
           && string.Equals(token, _expected, StringComparison.Ordinal);
}

/// <summary>
/// Desktop-friendly wrapper around the bounded runners. Mirrors the MCP
/// bridge so desktop, CLI and MCP share the same approval/audit shape.
/// </summary>
public sealed class DesktopAdventureRunBridge : IDisposable
{
    private readonly IAdventureExecutionGatewayFactory _factory;
    private readonly IApprovalTokenValidator _validator;
    private readonly IAuditEventJournal? _journal;
    private readonly string _auditPath;
    private bool _disposed;

    public DesktopAdventureRunBridge(
        IAdventureExecutionGatewayFactory factory,
        IApprovalTokenValidator validator,
        IAuditEventJournal? journal = null,
        string? auditPath = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _journal = journal;
        _auditPath = auditPath ?? "<memory>";
    }

    public string AuditPath => _auditPath;

    public async ValueTask<BoundedRunResult> RunBoundedAsync(
        AccountRole role,
        AdventureRunRequest request,
        AdventureActionAllowlist allowlist,
        IReadOnlyList<AdventureCondition> conditions,
        IAdventureConditionSnapshotProvider snapshotProvider,
        BoundedAdventureRunnerOptions options,
        bool execute,
        string? approvalToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(allowlist);

        if (!execute)
            return new BoundedRunResult(false, BoundedAdventureRunStopReason.ReadyToRun.ToString(), null, _auditPath, 0);

        if (!_validator.IsValid(approvalToken))
        {
            await AppendAuditAsync(role, request, approvalToken, rejected: true, cancellationToken).ConfigureAwait(false);
            return new BoundedRunResult(false, "ApprovalDenied", null, _auditPath, 0);
        }

        var gateway = await _factory.CreateAsync(role, approvalToken!, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No live adventure execution transport is configured for role {role}.");
        var runner = new BoundedAdventureRunner(gateway);
        var result = await runner.RunAsync(request, allowlist, conditions, snapshotProvider, options, cancellationToken)
            .ConfigureAwait(false);
        await AppendAuditAsync(role, request, approvalToken, rejected: false, cancellationToken).ConfigureAwait(false);
        return ToResult(result);
    }

    public async ValueTask<BoundedRunResult> RunChoiceChainAsync(
        AccountRole role,
        string runId,
        AdventureActionAllowlist allowlist,
        BoundedChoiceChainOptions options,
        bool execute,
        string? approvalToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(allowlist);

        if (!execute)
            return new BoundedRunResult(false, BoundedChoiceChainStopReason.Complete.ToString(), null, _auditPath, 0);

        if (!_validator.IsValid(approvalToken))
        {
            await AppendChoiceAuditAsync(role, runId, approvalToken, rejected: true, cancellationToken).ConfigureAwait(false);
            return new BoundedRunResult(false, "ApprovalDenied", null, _auditPath, 0);
        }

        var gateway = await _factory.CreateAsync(role, approvalToken!, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No live adventure execution transport is configured for role {role}.");
        var runner = new BoundedChoiceChainRunner(gateway);
        var result = await runner.RunAsync(runId.Trim(), allowlist, options, cancellationToken).ConfigureAwait(false);
        await AppendChoiceAuditAsync(role, runId, approvalToken, rejected: false, cancellationToken).ConfigureAwait(false);
        return ToResult(result);
    }

    private BoundedRunResult ToResult(BoundedAdventureRunResult result)
        => new(
            Executed: true,
            StopReason: result.StopReason.ToString(),
            RequestId: result.Steps.Count == 0 ? null : result.Steps[^1].RequestId,
            AuditPath: _auditPath,
            StepCount: result.Steps.Count);

    private BoundedRunResult ToResult(BoundedChoiceChainResult result)
        => new(
            Executed: true,
            StopReason: result.StopReason.ToString(),
            RequestId: result.Steps.Count == 0 ? null : result.Steps[^1].RequestId,
            AuditPath: _auditPath,
            StepCount: result.Steps.Count);

    private async ValueTask AppendAuditAsync(
        AccountRole role,
        AdventureRunRequest request,
        string? approvalToken,
        bool rejected,
        CancellationToken cancellationToken)
    {
        if (_journal is null) return;
        try
        {
            await _journal.AppendCommitAsync(
                operationId: $"bounded-run-{request.RunId}",
                sourceId: "desktop",
                requestId: request.RunId,
                action: rejected ? "adventure.bounded-run.rejected" : "adventure.bounded-run.committed",
                status: rejected ? "denied" : "submitted",
                message: $"Role={role}, ApprovalToken={(string.IsNullOrEmpty(approvalToken) ? "<none>" : "supplied")}",
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Audit failure is not allowed to break the user-visible flow.
        }
    }

    private async ValueTask AppendChoiceAuditAsync(
        AccountRole role,
        string runId,
        string? approvalToken,
        bool rejected,
        CancellationToken cancellationToken)
    {
        if (_journal is null) return;
        try
        {
            await _journal.AppendCommitAsync(
                operationId: $"bounded-choice-{runId}",
                sourceId: "desktop",
                requestId: runId,
                action: rejected ? "adventure.choice-chain.rejected" : "adventure.choice-chain.committed",
                status: rejected ? "denied" : "submitted",
                message: $"Role={role}, ApprovalToken={(string.IsNullOrEmpty(approvalToken) ? "<none>" : "supplied")}",
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Audit failure is not allowed to break the user-visible flow.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_journal is IDisposable disposable) disposable.Dispose();
    }
}
