using System.Collections.ObjectModel;
using LibKolibri;

namespace KoLibri.Desktop;

/// <summary>Review row projected from the approval contract.</summary>
public sealed record ApprovalProjection
{
    public string ApprovalId { get; init; } = string.Empty;
    public string OperationId { get; init; } = string.Empty;
    public string? PreviewId { get; init; }
    public LibKolibri.ApprovalState State { get; init; }
    public bool RequiresApproval { get; init; }
    public string Summary { get; init; } = string.Empty;
    public IReadOnlyList<ExpectedStateCheck> Checks { get; init; } = Array.Empty<ExpectedStateCheck>();
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public IReadOnlyList<GatewayDiagnostic> Diagnostics { get; init; } = Array.Empty<GatewayDiagnostic>();
    public string? LastMessage { get; init; }
    public bool IsExpired => ExpiresAt is { } expiry && DateTimeOffset.UtcNow >= expiry;
    public bool IsPending => State == LibKolibri.ApprovalState.Pending;
    public string StateText => State switch
    {
        LibKolibri.ApprovalState.Pending => "Pending",
        LibKolibri.ApprovalState.Approved => "Approved",
        LibKolibri.ApprovalState.Rejected => "Rejected",
        LibKolibri.ApprovalState.Expired => "Expired",
        LibKolibri.ApprovalState.Committed => "Committed",
        LibKolibri.ApprovalState.Verified => "Verified",
        LibKolibri.ApprovalState.Failed => "Failed",
        _ => "Unknown"
    };

    public static ApprovalProjection From(ApprovalPreview approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        return new ApprovalProjection
        {
            ApprovalId = approval.ApprovalId,
            OperationId = approval.OperationId,
            PreviewId = approval.PreviewId,
            State = approval.State,
            RequiresApproval = approval.RequiresApproval,
            Summary = approval.Summary ?? approval.OperationId,
            Checks = approval.Checks.ToArray(),
            CreatedAt = approval.CreatedAt,
            ExpiresAt = approval.ExpiresAt,
            Diagnostics = approval.Diagnostics.ToArray()
        };
    }
}

public sealed record ApprovalInboxSnapshot(
    IReadOnlyList<ApprovalPreview> Approvals,
    DateTimeOffset ObservedAt)
{
    public bool IsEmpty => Approvals.Count == 0;
}

/// <summary>Approval inbox and explicit decision projection.</summary>
public class ApprovalViewModel : DesktopViewModel
{
    private readonly IApprovalGateway? _gateway;
    private readonly IApprovalInboxQuery? _inbox;
    private ApprovalProjection? _selectedApproval;
    private string? _lastActionMessage;

    public ApprovalViewModel(IApprovalGateway? gateway = null, IApprovalInboxQuery? inbox = null)
    {
        _gateway = gateway;
        _inbox = inbox;
        Approvals = new ObservableCollection<ApprovalProjection>();
        Diagnostics = new ObservableCollection<GatewayDiagnostic>();
    }

    public ObservableCollection<ApprovalProjection> Approvals { get; }
    public ObservableCollection<GatewayDiagnostic> Diagnostics { get; }

    public ApprovalProjection? SelectedApproval
    {
        get => _selectedApproval;
        set => SetProperty(ref _selectedApproval, value);
    }

    public string? LastActionMessage
    {
        get => _lastActionMessage;
        private set => SetProperty(ref _lastActionMessage, value);
    }

    public bool HasApprovals => Approvals.Count > 0;
    public int PendingCount => Approvals.Count(x => x.IsPending);

    public Task<ApprovalInboxSnapshot?> LoadAsync(CancellationToken cancellationToken = default)
        => LoadCoreAsync(
            LoadSnapshotAsync,
            ApplySnapshot,
            snapshot => snapshot.IsEmpty ? DesktopLoadState.Empty : DesktopLoadState.Ready,
            _ => false,
            snapshot => snapshot.ObservedAt,
            cancellationToken);

    public Task<ApprovalInboxSnapshot?> RefreshAsync(CancellationToken cancellationToken = default)
        => LoadAsync(cancellationToken);

    public Task<ApprovalPreview?> PreviewAsync(
        ApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return OperationCoreAsync(
            token => _gateway is null
                ? Task.FromResult(new ApprovalPreview
                {
                    State = LibKolibri.ApprovalState.Failed,
                    ApprovalId = string.Empty,
                    OperationId = request.OperationId,
                    Summary = "No approval gateway is configured.",
                    Diagnostics = new[] { new GatewayDiagnostic("gateway.missing", "No approval gateway is configured.", true) }
                })
                : _gateway.PreviewAsync(request, token),
            ApplyPreview,
            cancellationToken,
            preview => preview.State == LibKolibri.ApprovalState.Failed ? DesktopLoadState.Error : DesktopLoadState.Ready);
    }

    public Task<ApprovalResult?> DecideAsync(
        ApprovalDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return OperationCoreAsync(
            token => _gateway is null
                ? Task.FromResult(new ApprovalResult(request.ApprovalId, LibKolibri.ApprovalState.Failed)
                {
                    Message = "No approval gateway is configured.",
                    Diagnostics = new[] { new GatewayDiagnostic("gateway.missing", "No approval gateway is configured.", true) }
                })
                : _gateway.ApproveAsync(request, token),
            ApplyResult,
            cancellationToken,
            result => result.State == LibKolibri.ApprovalState.Failed ? DesktopLoadState.Error : DesktopLoadState.Ready);
    }

    public Task<ApprovalResult?> ApproveAsync(
        string approvalId,
        string? decidedBy = null,
        string? comment = null,
        CancellationToken cancellationToken = default)
        => DecideAsync(new ApprovalDecisionRequest(approvalId, true, decidedBy, comment), cancellationToken);

    public Task<ApprovalResult?> RejectAsync(
        string approvalId,
        string? decidedBy = null,
        string? comment = null,
        CancellationToken cancellationToken = default)
        => DecideAsync(new ApprovalDecisionRequest(approvalId, false, decidedBy, comment), cancellationToken);

    public Task<ApprovalResult?> ExpireAsync(
        string approvalId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var request = new ApprovalExpirationRequest(approvalId, reason);
        return OperationCoreAsync(
            token => _gateway is null
                ? Task.FromResult(new ApprovalResult(approvalId, LibKolibri.ApprovalState.Failed)
                {
                    Message = "No approval gateway is configured.",
                    Diagnostics = new[] { new GatewayDiagnostic("gateway.missing", "No approval gateway is configured.", true) }
                })
                : _gateway.ExpireAsync(request, token),
            ApplyResult,
            cancellationToken,
            result => result.State == LibKolibri.ApprovalState.Failed ? DesktopLoadState.Error : DesktopLoadState.Ready);
    }

    public Task<ApprovalResult?> CommitAsync(
        ApprovalCommitRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return OperationCoreAsync(
            token => _gateway is null
                ? Task.FromResult(new ApprovalResult(request.ApprovalId, LibKolibri.ApprovalState.Failed)
                {
                    Message = "No approval gateway is configured.",
                    Diagnostics = new[] { new GatewayDiagnostic("gateway.missing", "No approval gateway is configured.", true) }
                })
                : _gateway.CommitAsync(request, token),
            ApplyResult,
            cancellationToken,
            result => result.State == LibKolibri.ApprovalState.Failed ? DesktopLoadState.Error : DesktopLoadState.Ready);
    }

    public Task<ApprovalVerification?> VerifyAsync(
        ApprovalVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return OperationCoreAsync(
            token => _gateway is null
                ? Task.FromResult(new ApprovalVerification(request.OperationId, LibKolibri.ApprovalState.Failed, false)
                {
                    Diagnostics = new[] { new GatewayDiagnostic("gateway.missing", "No approval gateway is configured.", true) }
                })
                : _gateway.VerifyAsync(request, token),
            ApplyVerification,
            cancellationToken,
            verification => verification.State == LibKolibri.ApprovalState.Failed ? DesktopLoadState.Error : DesktopLoadState.Ready);
    }

    private async Task<ApprovalInboxSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        var approvals = _inbox is null
            ? Array.Empty<ApprovalPreview>()
            : await _inbox.GetPendingAsync(cancellationToken).ConfigureAwait(false);
        return new ApprovalInboxSnapshot(approvals ?? Array.Empty<ApprovalPreview>(), DateTimeOffset.UtcNow);
    }

    private void ApplySnapshot(ApprovalInboxSnapshot snapshot)
    {
        Approvals.Clear();
        foreach (var approval in snapshot.Approvals)
            Approvals.Add(ApprovalProjection.From(approval));
        Diagnostics.Clear();
        foreach (var approval in snapshot.Approvals)
            foreach (var diagnostic in approval.Diagnostics)
                Diagnostics.Add(diagnostic);
        OnPropertyChanged(nameof(HasApprovals));
        OnPropertyChanged(nameof(PendingCount));
        if (SelectedApproval is not null)
            SelectedApproval = Approvals.FirstOrDefault(x => x.ApprovalId == SelectedApproval.ApprovalId);
    }

    private void ApplyPreview(ApprovalPreview preview)
    {
        Upsert(ApprovalProjection.From(preview));
        LastActionMessage = preview.Summary;
        foreach (var diagnostic in preview.Diagnostics)
            Diagnostics.Add(diagnostic);
    }

    private void ApplyResult(ApprovalResult result)
    {
        var current = Approvals.FirstOrDefault(x => x.ApprovalId == result.ApprovalId);
        if (current is not null)
        {
            var updated = current with
            {
                State = result.State,
                LastMessage = result.Message,
                Diagnostics = result.Diagnostics.ToArray()
            };
            var index = Approvals.IndexOf(current);
            Approvals[index] = updated;
            if (SelectedApproval?.ApprovalId == updated.ApprovalId)
                SelectedApproval = updated;
        }
        LastActionMessage = result.Message;
        foreach (var diagnostic in result.Diagnostics)
            Diagnostics.Add(diagnostic);
        OnPropertyChanged(nameof(PendingCount));
    }

    private void ApplyVerification(ApprovalVerification verification)
    {
        var current = Approvals.FirstOrDefault(x => x.OperationId == verification.OperationId);
        if (current is not null)
        {
            var updated = current with
            {
                State = verification.State,
                Checks = verification.Checks.ToArray(),
                Diagnostics = verification.Diagnostics.ToArray()
            };
            var index = Approvals.IndexOf(current);
            Approvals[index] = updated;
            if (SelectedApproval?.ApprovalId == updated.ApprovalId)
                SelectedApproval = updated;
        }
        LastActionMessage = verification.Verified ? "Operation verified." : "Operation verification failed.";
        foreach (var diagnostic in verification.Diagnostics)
            Diagnostics.Add(diagnostic);
        OnPropertyChanged(nameof(PendingCount));
    }

    private void Upsert(ApprovalProjection projection)
    {
        var existing = Approvals.FirstOrDefault(x => x.ApprovalId == projection.ApprovalId);
        if (existing is null) Approvals.Add(projection);
        else Approvals[Approvals.IndexOf(existing)] = projection;
        if (SelectedApproval?.ApprovalId == projection.ApprovalId)
            SelectedApproval = projection;
        OnPropertyChanged(nameof(HasApprovals));
        OnPropertyChanged(nameof(PendingCount));
    }
}

public sealed class ApprovalStateModel : ApprovalViewModel
{
    public ApprovalStateModel(IApprovalGateway? gateway = null, IApprovalInboxQuery? inbox = null)
        : base(gateway, inbox) { }
}

public sealed class ApprovalInboxViewModel : ApprovalViewModel
{
    public ApprovalInboxViewModel(IApprovalGateway? gateway = null, IApprovalInboxQuery? inbox = null)
        : base(gateway, inbox) { }
}
