using KolArbitrage.Core.Integration;

namespace KolArbitrage.Core.Adventure;

/// <summary>
/// Decorates adventure approval/verification hooks with the common audit
/// journal. Only identifiers, action metadata and response hashes are stored;
/// request fields, HTML, cookies and credentials are deliberately excluded.
/// </summary>
public sealed class AuditedAdventureHooks : IAdventureApprovalHook, IAdventureVerificationHook
{
    private readonly IAdventureApprovalHook _approval;
    private readonly IAdventureVerificationHook _verification;
    private readonly IAuditEventJournal _journal;
    private readonly string _sourceId;

    public AuditedAdventureHooks(
        IAdventureApprovalHook approval,
        IAdventureVerificationHook verification,
        IAuditEventJournal journal,
        string sourceId = "kolibri.adventure")
    {
        _approval = approval ?? throw new ArgumentNullException(nameof(approval));
        _verification = verification ?? throw new ArgumentNullException(nameof(verification));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _sourceId = string.IsNullOrWhiteSpace(sourceId) ? "kolibri.adventure" : sourceId.Trim();
    }

    public async ValueTask<AdventureApprovalDecision> ApproveAsync(
        AdventureApprovalContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Request;
        var operationId = request.RunId;
        var action = ActionName(context.Action);
        var data = Data(request, context.Action);
        await _journal.AppendPreviewAsync(
            operationId,
            _sourceId,
            request.RequestId,
            action,
            status: "approval_requested",
            data: data,
            cancellationToken: cancellationToken,
            accountId: request.AccountId).ConfigureAwait(false);

        var decision = await _approval.ApproveAsync(context, cancellationToken).ConfigureAwait(false);
        if (!decision.Approved)
        {
            await _journal.AppendPreviewAsync(
                operationId,
                _sourceId,
                request.RequestId,
                action,
                status: "denied",
                message: decision.Reason,
                data: data,
                cancellationToken: cancellationToken,
                accountId: request.AccountId).ConfigureAwait(false);
            return decision;
        }

        await _journal.AppendCommitAsync(
            operationId,
            _sourceId,
            request.RequestId,
            action,
            status: "approved",
            data: data,
            cancellationToken: cancellationToken,
            accountId: request.AccountId).ConfigureAwait(false);
        return decision;
    }

    public async ValueTask<AdventureVerificationResult> VerifyAsync(
        AdventureVerificationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Request;
        var action = ActionName(context.Action);
        var data = Data(request, context.Action);
        data["responseHash"] = context.Response.ResponseHash;
        data["statusCode"] = context.Response.StatusCode is { } statusCode
            ? ((int)statusCode).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
        data["phase"] = context.Decision.State.Phase.ToString();
        var result = await _verification.VerifyAsync(context, cancellationToken).ConfigureAwait(false);
        await _journal.AppendVerifyAsync(
            request.RunId,
            _sourceId,
            request.RequestId,
            action,
            status: result.Verified ? "verified" : "verification_failed",
            message: result.Reason,
            data: data,
            cancellationToken: cancellationToken,
            accountId: request.AccountId).ConfigureAwait(false);
        return result;
    }

    private static string ActionName(AdventureKernelAction action)
        => action.Kind switch
        {
            AdventureKernelActionKind.Encounter => "adventure.start",
            AdventureKernelActionKind.Fight => "adventure.fight",
            AdventureKernelActionKind.Choice => "adventure.choice",
            _ => "adventure.action"
        };

    private static Dictionary<string, string?> Data(
        AdventureTransportRequest request,
        AdventureKernelAction action)
        => new(StringComparer.Ordinal)
        {
            ["runId"] = request.RunId,
            ["operation"] = request.Operation.ToString(),
            ["adventureId"] = request.AdventureId,
            ["actionFingerprint"] = request.ActionFingerprint,
            ["actionKind"] = action.Kind.ToString(),
            ["checkpointRevision"] = request.CheckpointRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
}
