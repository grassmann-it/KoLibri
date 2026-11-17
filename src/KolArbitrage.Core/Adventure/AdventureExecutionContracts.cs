namespace KolArbitrage.Core.Adventure;

/// <summary>
/// The four server boundaries used by the narrow adventure runner.  Adventure,
/// Fight and Choice are mutating requests; Refresh is an idempotent read.
/// </summary>
public enum AdventureTransportOperation
{
    Adventure,
    Fight,
    Choice,
    Refresh
}

/// <summary>Compatibility name for hosts that call a transport operation a kind.</summary>
public enum AdventureTransportKind
{
    Adventure,
    Fight,
    Choice,
    Refresh
}

/// <summary>Outcome returned by an execution gateway call.</summary>
public enum AdventureExecutionStatus
{
    Advanced,
    Complete,
    Aborted,
    RequiresRefresh,
    Indeterminate
}

/// <summary>One request handed to an injected adventure transport.</summary>
public sealed record AdventureTransportRequest
{
    public AdventureTransportRequest()
    {
    }

    public AdventureTransportRequest(
        string requestId,
        string runId,
        string adventureId,
        AdventureTransportOperation operation,
        AdventureKernelAction? action = null,
        AdventureCheckpoint? checkpoint = null)
    {
        RequestId = requestId;
        RunId = runId;
        AdventureId = adventureId;
        Operation = operation;
        Action = action;
        Checkpoint = checkpoint;
    }

    /// <summary>Opaque id generated once for this transport call.</summary>
    public string RequestId { get; init; } = string.Empty;

    /// <summary>Alias used by transports that call this value a correlation id.</summary>
    public string CorrelationId
    {
        get => RequestId;
        init => RequestId = value;
    }

    public string RunId { get; init; } = string.Empty;
    public string AdventureId { get; init; } = string.Empty;
    public AdventureTransportOperation Operation { get; init; }
    public AdventureTransportKind Kind
        => (AdventureTransportKind)Operation;
    public AdventureKernelAction? Action { get; init; }
    public AdventureCheckpoint? Checkpoint { get; init; }

    /// <summary>Short-lived approval material supplied by the host hook.</summary>
    public string? ApprovalToken { get; init; }

    public long CheckpointRevision => Checkpoint?.Revision ?? 0;

    public string? ExpectedResponseHash => Checkpoint?.LastResponseHash;

    /// <summary>
    /// Stable action binding sent alongside the opaque request id.  A transport
    /// may log or echo it without exposing cookies or HTML.
    /// </summary>
    public string ActionFingerprint
        => string.Join("|",
            Operation,
            RunId,
            AdventureId,
            ActionId ?? string.Empty,
            Round?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            ChoiceId ?? string.Empty,
            OptionId ?? string.Empty);

    /// <summary>
    /// Form fields that a host may need to submit.  They are generated from
    /// the allowlisted action and are never inferred from arbitrary HTML.
    /// </summary>
    public IReadOnlyDictionary<string, string> Fields { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    public string? ActionId
        => Action is AdventureFightAction fight ? fight.ActionId : null;

    public int? Round
        => Action is AdventureFightAction fight ? fight.Round : null;

    public string? ChoiceId
        => Action is AdventureChoiceAction choice ? choice.ChoiceId : null;

    public string? OptionId
        => Action is AdventureChoiceAction choice ? choice.OptionId : null;

    public bool IsPost
        => Operation is AdventureTransportOperation.Adventure
            or AdventureTransportOperation.Fight
            or AdventureTransportOperation.Choice;

    public bool IsMutating => IsPost;
}

/// <summary>
/// Raw response returned by a transport.  A host can provide parsed encounter
/// data directly or provide HTML for the injected parser.  Supplying both is
/// allowed; the explicit observation wins.
/// </summary>
public sealed record AdventureTransportResponse
{
    public AdventureTransportResponse()
    {
    }

    public AdventureTransportResponse(
        string requestId,
        string runId,
        string adventureId,
        AdventureEncounterState encounter)
    {
        RequestId = requestId;
        RunId = runId;
        AdventureId = adventureId;
        Encounter = encounter;
        ResponseHash = encounter.ResponseHash;
    }

    public AdventureTransportResponse(
        string requestId,
        string runId,
        string adventureId,
        string html,
        Uri? responseUri = null)
    {
        RequestId = requestId;
        RunId = runId;
        AdventureId = adventureId;
        Html = html;
        ResponseUri = responseUri;
    }

    public string RequestId { get; init; } = string.Empty;

    public string CorrelationId
    {
        get => RequestId;
        init => RequestId = value;
    }

    public string? RunId { get; init; }
    public string? AdventureId { get; init; }
    public Uri? ResponseUri { get; init; }
    public string? Html { get; init; }
    public AdventureEncounterState? Encounter { get; init; }
    public AdventureKernelObservation? Observation { get; init; }
    public string? ResponseHash { get; init; }
    public bool? Accepted { get; init; }
    public string? Message { get; init; }

    /// <summary>Optional echo of the request's action fingerprint.</summary>
    public string? RequestFingerprint { get; init; }

    public string? EffectiveResponseHash
        => ResponseHash
            ?? Observation?.ResponseHash
            ?? Encounter?.ResponseHash;
}

/// <summary>Injected boundary for the adventure/fight/choice/refresh calls.</summary>
public interface IAdventureExecutionTransport
{
    /// <summary>Starts one allowlisted adventure.</summary>
    Task<AdventureTransportResponse> StartAdventureAsync(
        AdventureTransportRequest request,
        CancellationToken cancellationToken = default)
        => AdventureAsync(request, cancellationToken);

    /// <summary>Alias for hosts that use the page name as the method name.</summary>
    Task<AdventureTransportResponse> AdventureAsync(
        AdventureTransportRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The adventure transport does not implement StartAdventureAsync.");

    /// <summary>Submits one explicitly allowlisted fight action.</summary>
    Task<AdventureTransportResponse> SubmitFightAsync(
        AdventureTransportRequest request,
        CancellationToken cancellationToken = default)
        => FightAsync(request, cancellationToken);

    Task<AdventureTransportResponse> FightAsync(
        AdventureTransportRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The adventure transport does not implement SubmitFightAsync.");

    /// <summary>Submits one explicitly allowlisted choice option.</summary>
    Task<AdventureTransportResponse> SubmitChoiceAsync(
        AdventureTransportRequest request,
        CancellationToken cancellationToken = default)
        => ChoiceAsync(request, cancellationToken);

    Task<AdventureTransportResponse> ChoiceAsync(
        AdventureTransportRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The adventure transport does not implement SubmitChoiceAsync.");

    /// <summary>Refreshes the current encounter without mutating it.</summary>
    Task<AdventureTransportResponse> RefreshAsync(
        AdventureTransportRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The adventure transport does not implement RefreshAsync.");
}

/// <summary>Short interface alias for callers that use an adventure transport.</summary>
public interface IAdventureTransport : IAdventureExecutionTransport
{
}

/// <summary>Information supplied to an approval hook before a mutating call.</summary>
public sealed record AdventureApprovalContext(
    AdventureRunState State,
    AdventureKernelAction Action,
    AdventureTransportRequest Request);

public sealed record AdventureApprovalDecision
{
    public bool Approved { get; init; }
    public string? Reason { get; init; }
    public string? ApprovalToken { get; init; }

    public static AdventureApprovalDecision Allow(string? token = null)
        => new() { Approved = true, ApprovalToken = token };

    public static AdventureApprovalDecision Deny(string reason)
        => new() { Approved = false, Reason = reason };
}

/// <summary>Approval callback for adventure/fight/choice POSTs.</summary>
public interface IAdventureApprovalHook
{
    ValueTask<AdventureApprovalDecision> ApproveAsync(
        AdventureApprovalContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Compatibility name for hosts that prefix the hook with Execution.</summary>
public interface IAdventureExecutionApprovalHook : IAdventureApprovalHook
{
}

public sealed class DelegateAdventureApprovalHook : IAdventureApprovalHook
{
    private readonly Func<AdventureApprovalContext, CancellationToken, ValueTask<AdventureApprovalDecision>> _callback;

    public DelegateAdventureApprovalHook(
        Func<AdventureApprovalContext, CancellationToken, ValueTask<AdventureApprovalDecision>> callback)
        => _callback = callback ?? throw new ArgumentNullException(nameof(callback));

    public ValueTask<AdventureApprovalDecision> ApproveAsync(
        AdventureApprovalContext context,
        CancellationToken cancellationToken = default)
        => _callback(context, cancellationToken);
}

/// <summary>Information supplied to a verification hook after parsing a response.</summary>
public sealed record AdventureVerificationContext(
    AdventureRunState Before,
    AdventureKernelAction Action,
    AdventureTransportRequest Request,
    AdventureTransportResponse Response,
    AdventureKernelObservation Observation,
    AdventureKernelDecision Decision);

public sealed record AdventureVerificationResult
{
    public bool Verified { get; init; }
    public string? Reason { get; init; }

    public static AdventureVerificationResult Success()
        => new() { Verified = true };

    public static AdventureVerificationResult Failed(string reason)
        => new() { Verified = false, Reason = reason };
}

/// <summary>Optional post-response verification callback.</summary>
public interface IAdventureVerificationHook
{
    ValueTask<AdventureVerificationResult> VerifyAsync(
        AdventureVerificationContext context,
        CancellationToken cancellationToken = default);
}

public interface IAdventureExecutionVerificationHook : IAdventureVerificationHook
{
}

public sealed class DelegateAdventureVerificationHook : IAdventureVerificationHook
{
    private readonly Func<AdventureVerificationContext, CancellationToken, ValueTask<AdventureVerificationResult>> _callback;

    public DelegateAdventureVerificationHook(
        Func<AdventureVerificationContext, CancellationToken, ValueTask<AdventureVerificationResult>> callback)
        => _callback = callback ?? throw new ArgumentNullException(nameof(callback));

    public ValueTask<AdventureVerificationResult> VerifyAsync(
        AdventureVerificationContext context,
        CancellationToken cancellationToken = default)
        => _callback(context, cancellationToken);
}

/// <summary>Gateway result with both the kernel decision and transport metadata.</summary>
public sealed record AdventureExecutionResult
{
    public required AdventureExecutionStatus Status { get; init; }
    public required AdventureKernelDecision Decision { get; init; }
    public string? RequestId { get; init; }
    public AdventureTransportResponse? Response { get; init; }
    public IReadOnlyList<string> Messages { get; init; } = Array.Empty<string>();

    public AdventureRunState State => Decision.State;
    public AdventureCheckpoint Checkpoint => Decision.Checkpoint;
    public AdventureKernelAction? NextAction => Decision.NextAction;
    public AdventureAbortReason? AbortReason => Decision.AbortReason;
    public bool IsAborted => Status == AdventureExecutionStatus.Aborted || Decision.IsAborted;
    public bool IsComplete => Status == AdventureExecutionStatus.Complete || Decision.IsComplete;
    public bool RequiresRefresh
        => Status == AdventureExecutionStatus.RequiresRefresh || Decision.RequiresRefresh;
    public bool IsIndeterminate => Status == AdventureExecutionStatus.Indeterminate;

    public AdventureAbortReason? PrimaryAbortReason => AbortReason;
}

/// <summary>Default execution hooks keep fixture/offline runs deterministic.</summary>
internal sealed class AllowAdventureExecution : IAdventureApprovalHook, IAdventureVerificationHook
{
    public ValueTask<AdventureApprovalDecision> ApproveAsync(
        AdventureApprovalContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(AdventureApprovalDecision.Allow());

    public ValueTask<AdventureVerificationResult> VerifyAsync(
        AdventureVerificationContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(AdventureVerificationResult.Success());
}
