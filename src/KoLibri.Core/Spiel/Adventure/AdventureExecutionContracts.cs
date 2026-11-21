using System.Net;
using KoLibri.Core.Basis;

namespace KoLibri.Core.Spiel;

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

/// <summary>
/// The only KoL pages an adventure transport may address.  These values are
/// deliberately constants instead of caller supplied paths; an adapter must
/// never turn a server supplied link into a new route.
/// </summary>
public static class AdventureHttpRoutes
{
    public static AdventureHttpRouteMap Map => AdventureHttpRouteMap.Default;
    public const string Adventure = "adventure.php";
    public const string Fight = "fight.php";
    public const string Choice = "choice.php";
    /// <summary>
    /// Refresh reads the current adventure page.  It is a GET to the same
    /// fixed page and never a replay of the previous mutating request.
    /// </summary>
    public const string Refresh = Adventure;

    public static string For(AdventureTransportOperation operation)
        => operation switch
        {
            AdventureTransportOperation.Adventure => Adventure,
            AdventureTransportOperation.Fight => Fight,
            AdventureTransportOperation.Choice => Choice,
            AdventureTransportOperation.Refresh => Refresh,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    public static string For(AdventureTransportKind operation)
        => For((AdventureTransportOperation)operation);

    public static string Path(AdventureTransportOperation operation) => For(operation);
    public static string GetRoute(AdventureTransportOperation operation) => For(operation);

    public static Uri UriFor(Uri baseUri, AdventureTransportOperation operation)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        return new Uri(baseUri, For(operation));
    }
}

/// <summary>
/// Immutable route map exposed to hosts that prefer a value over static route
/// constants.  Its values are fixed by the contract and cannot be overridden.
/// </summary>
public record AdventureHttpRouteMap
{
    public static AdventureHttpRouteMap Default { get; } = new();
    public static AdventureHttpRouteMap Fixed => Default;

    public string Adventure => AdventureHttpRoutes.Adventure;
    public string Fight => AdventureHttpRoutes.Fight;
    public string Choice => AdventureHttpRoutes.Choice;
    public string Refresh => AdventureHttpRoutes.Refresh;

    public string AdventurePath => Adventure;
    public string FightPath => Fight;
    public string ChoicePath => Choice;
    public string RefreshPath => Refresh;

    public string this[AdventureTransportOperation operation] => For(operation);

    public IReadOnlyDictionary<AdventureTransportOperation, string> Entries =>
        new Dictionary<AdventureTransportOperation, string>
        {
            [AdventureTransportOperation.Adventure] = Adventure,
            [AdventureTransportOperation.Fight] = Fight,
            [AdventureTransportOperation.Choice] = Choice,
            [AdventureTransportOperation.Refresh] = Refresh
        };

    public string For(AdventureTransportOperation operation)
        => AdventureHttpRoutes.For(operation);

    public string For(AdventureTransportKind operation)
        => AdventureHttpRoutes.For(operation);

    public string Path(AdventureTransportOperation operation) => For(operation);
    public string GetRoute(AdventureTransportOperation operation) => For(operation);

    public Uri BuildUri(Uri baseUri, AdventureTransportOperation operation)
        => AdventureHttpRoutes.UriFor(baseUri, operation);

    public void Validate()
    {
        if (!string.Equals(Adventure, AdventureHttpRoutes.Adventure, StringComparison.Ordinal)
            || !string.Equals(Fight, AdventureHttpRoutes.Fight, StringComparison.Ordinal)
            || !string.Equals(Choice, AdventureHttpRoutes.Choice, StringComparison.Ordinal)
            || !string.Equals(Refresh, AdventureHttpRoutes.Refresh, StringComparison.Ordinal))
            throw new ArgumentException("Adventure routes are fixed to the KoL adventure pages.", nameof(AdventureHttpRouteMap));
    }
}

/// <summary>Compatibility alias for hosts that call the map AdventureRouteMap.</summary>
public static class AdventureRouteMap
{
    public const string Adventure = AdventureHttpRoutes.Adventure;
    public const string Fight = AdventureHttpRoutes.Fight;
    public const string Choice = AdventureHttpRoutes.Choice;
    public const string Refresh = AdventureHttpRoutes.Refresh;

    public static AdventureHttpRouteMap Default => AdventureHttpRouteMap.Default;
    public static string For(AdventureTransportOperation operation) => AdventureHttpRoutes.For(operation);
    public static string Path(AdventureTransportOperation operation) => AdventureHttpRoutes.For(operation);
}

public record AdventureTransportRouteMap : AdventureHttpRouteMap
{
}

/// <summary>Stable account/profile binding for an adventure HTTP session.</summary>
public record AdventureTransportAccountBinding
{
    public AdventureTransportAccountBinding()
    {
    }

    public AdventureTransportAccountBinding(
        AccountRole role,
        string accountId,
        string? displayName = null)
    {
        Role = role;
        AccountId = accountId;
        DisplayName = displayName;
    }

    public AccountRole Role { get; init; }
    public AccountRole AccountRole
    {
        get => Role;
        init => Role = value;
    }
    public string AccountId { get; init; } = string.Empty;
    public string ProfileId
    {
        get => AccountId;
        init => AccountId = value;
    }
    public string? DisplayName { get; init; }

    /// <summary>Role name used in audit and request correlation metadata.</summary>
    public string CanonicalName => Role.ToString();

    public bool Matches(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return false;
        var value = reference.Trim();
        return string.Equals(value, AccountId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, CanonicalName, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(DisplayName)
                && string.Equals(value, DisplayName, StringComparison.OrdinalIgnoreCase));
    }

    public bool Matches(AccountRole role, string? accountId = null)
        => Role == role && (string.IsNullOrWhiteSpace(accountId) || Matches(accountId));

    public void Validate()
    {
        if (!Enum.IsDefined(Role))
            throw new ArgumentOutOfRangeException(nameof(Role));
        if (string.IsNullOrWhiteSpace(AccountId))
            throw new ArgumentException("An adventure account id is required.", nameof(AccountId));
        if (AccountId.Length > 200)
            throw new ArgumentException("An adventure account id is too long.", nameof(AccountId));
    }
}

/// <summary>Short alias for account bindings used by HTTP transport hosts.</summary>
public record AdventureAccountBinding : AdventureTransportAccountBinding
{
    public AdventureAccountBinding()
    {
    }

    public AdventureAccountBinding(AccountRole role, string accountId, string? displayName = null)
        : base(role, accountId, displayName)
    {
    }
}

/// <summary>HTTP-prefixed alias for hosts that keep bindings beside options.</summary>
public record AdventureHttpAccountBinding : AdventureTransportAccountBinding
{
    public AdventureHttpAccountBinding()
    {
    }

    public AdventureHttpAccountBinding(AccountRole role, string accountId, string? displayName = null)
        : base(role, accountId, displayName)
    {
    }
}

/// <summary>Raised when an adventure request crosses its fixed HTTP boundary.</summary>
public class AdventureHttpTransportException : InvalidOperationException
{
    public AdventureHttpTransportException(string message) : base(message)
    {
    }

    public AdventureHttpTransportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Raised when an HTTP response is larger than the configured bound.</summary>
public sealed class AdventureResponseTooLargeException : AdventureHttpTransportException
{
    public AdventureResponseTooLargeException(int maximumBytes)
        : base($"The KoL adventure response exceeded the {maximumBytes} byte limit.")
        => MaximumBytes = maximumBytes;

    public int MaximumBytes { get; }
}

/// <summary>Options for the guarded adventure HTTP adapter.</summary>
public record AdventureHttpTransportOptions
{
    public Uri BaseUri { get; init; } =
        new("https://www.kingdomofloathing.com/", UriKind.Absolute);

    /// <summary>
    /// Exact host names accepted for the endpoint. A test endpoint must be
    /// added explicitly; HttpClient.BaseAddress is never an allowlist.
    /// </summary>
    public IReadOnlyCollection<string> AllowedHosts
    {
        get => Security.AllowedHosts;
        init => Security = (Security ?? new KoLSecurityPolicy()) with { AllowedHosts = value };
    }

    public KoLSecurityPolicy Security { get; init; } = new() { MaxRedirects = 0 };

    /// <summary>Compatibility alias for callers using the longer name.</summary>
    public KoLSecurityPolicy SecurityPolicy
    {
        get => Security;
        init => Security = value;
    }

    public int MaximumResponseBodyBytes { get; init; } = 1_048_576;

    /// <summary>Alias used by hosts that call the bound MaxResponseBytes.</summary>
    public int MaxResponseBytes
    {
        get => MaximumResponseBodyBytes;
        init => MaximumResponseBodyBytes = value;
    }

    public int MaximumBodyBytes
    {
        get => MaximumResponseBodyBytes;
        init => MaximumResponseBodyBytes = value;
    }

    public int MaximumResponseBytes
    {
        get => MaximumResponseBodyBytes;
        init => MaximumResponseBodyBytes = value;
    }

    public int MaxResponseBodyBytes
    {
        get => MaximumResponseBodyBytes;
        init => MaximumResponseBodyBytes = value;
    }

    public int MaxBodyBytes
    {
        get => MaximumResponseBodyBytes;
        init => MaximumResponseBodyBytes = value;
    }

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Alias for RequestTimeout.</summary>
    public TimeSpan Timeout
    {
        get => RequestTimeout;
        init => RequestTimeout = value;
    }

    /// <summary>The route map is fixed; this property is for inspection only.</summary>
    public AdventureHttpRouteMap Routes => AdventureHttpRouteMap.Default;

    public AdventureHttpRouteMap RouteMap => AdventureHttpRouteMap.Default;

    public string AdventurePath => AdventureHttpRoutes.Adventure;
    public string FightPath => AdventureHttpRoutes.Fight;
    public string ChoicePath => AdventureHttpRoutes.Choice;
    public string RefreshPath => AdventureHttpRoutes.Refresh;

    /// <summary>Redirects are intentionally never followed by this adapter.</summary>
    public bool AllowRedirects => false;
    public bool FollowRedirects => false;
    public bool NoRedirects => true;
    public int MaxRedirects => 0;

    public void Validate()
    {
        if (BaseUri is null || !BaseUri.IsAbsoluteUri)
            throw new ArgumentException("Adventure base URI must be absolute.", nameof(BaseUri));
        if (!string.Equals(BaseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Live adventure requests require HTTPS.", nameof(BaseUri));
        if (!string.IsNullOrEmpty(BaseUri.UserInfo))
            throw new ArgumentException("Adventure base URI cannot contain user information.", nameof(BaseUri));
        if (!string.IsNullOrEmpty(BaseUri.Query)
            || !string.IsNullOrEmpty(BaseUri.Fragment))
            throw new ArgumentException("Adventure base URI cannot contain a query or fragment.", nameof(BaseUri));
        if (string.IsNullOrWhiteSpace(BaseUri.Host))
            throw new ArgumentException("Adventure base URI must contain a host.", nameof(BaseUri));
        (Security ?? throw new ArgumentException(
            "An adventure HTTP security policy is required.", nameof(Security))).Validate();
        if (!Security.RequireHttps)
            throw new ArgumentException("Adventure HTTP security policy must require HTTPS.", nameof(Security));
        if (!Security.AllowedHosts.Contains(BaseUri.Host, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("The adventure base host is not allowlisted.", nameof(BaseUri));
        if (MaximumResponseBodyBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumResponseBodyBytes));
        if (RequestTimeout != System.Threading.Timeout.InfiniteTimeSpan
            && RequestTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        AdventureHttpRouteMap.Default.Validate();
    }
}

/// <summary>Short options aliases used by existing KoL adapter hosts.</summary>
public record AdventureTransportOptions : AdventureHttpTransportOptions
{
}

public record KoLAdventureHttpOptions : AdventureHttpTransportOptions
{
}

public record AdventureExecutionHttpOptions : AdventureHttpTransportOptions
{
}

public record HttpAdventureTransportOptions : AdventureHttpTransportOptions
{
}

public record HttpAdventureExecutionTransportOptions : AdventureHttpTransportOptions
{
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
        AdventureCheckpoint? checkpoint = null,
        AccountRole role = AccountRole.Main,
        string? accountId = null)
    {
        RequestId = requestId;
        RunId = runId;
        AdventureId = adventureId;
        Operation = operation;
        Action = action;
        Checkpoint = checkpoint;
        Role = role;
        AccountId = accountId;
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
    public int? Snarfblat { get; init; }
    /// <summary>
    /// Optional profile id for hosts that bind a transport to one account.
    /// The gateway can leave this empty; a configured transport then uses its
    /// immutable account binding and the checkpoint role.
    /// </summary>
    public string? AccountId { get; init; }

    /// <summary>Role metadata carried with the request, defaulting to Main.</summary>
    public AccountRole Role { get; init; } = AccountRole.Main;

    /// <summary>Alias used by callers that name the property AccountRole.</summary>
    public AccountRole AccountRole
    {
        get => Role;
        init => Role = value;
    }

    public string? ProfileId
    {
        get => AccountId;
        init => AccountId = value;
    }

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

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RequestId))
            throw new ArgumentException("An adventure request id is required.", nameof(RequestId));
        if (RequestId.Length > 200)
            throw new ArgumentException("An adventure request id is too long.", nameof(RequestId));
        if (string.IsNullOrWhiteSpace(RunId))
            throw new ArgumentException("An adventure run id is required.", nameof(RunId));
        if (RunId.Length > 200)
            throw new ArgumentException("An adventure run id is too long.", nameof(RunId));
        if (string.IsNullOrWhiteSpace(AdventureId))
            throw new ArgumentException("An adventure id is required.", nameof(AdventureId));
        if (Snarfblat is <= 0)
            throw new ArgumentOutOfRangeException(nameof(Snarfblat));
        if (!Enum.IsDefined(Operation))
            throw new ArgumentOutOfRangeException(nameof(Operation));
        if (!Enum.IsDefined(Role))
            throw new ArgumentOutOfRangeException(nameof(Role));
    }
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
    /// <summary>HTTP status when this response came from an HTTP adapter.</summary>
    public HttpStatusCode? StatusCode { get; init; }

    public int? HttpStatusCode
    {
        get => StatusCode is { } status ? (int)status : null;
        init => StatusCode = value is { } status
            ? (System.Net.HttpStatusCode)status
            : null;
    }

    public IReadOnlyDictionary<string, string> Headers { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string? Html { get; init; }
    public AdventureEncounterState? Encounter { get; init; }
    public AdventureKernelObservation? Observation { get; init; }
    public string? ResponseHash { get; init; }
    public bool? Accepted { get; init; }
    public string? Message { get; init; }

    /// <summary>Number of response bytes consumed by the bounded adapter.</summary>
    public int? BodyBytes { get; init; }

    public bool IsSuccessStatusCode
        => StatusCode is { } status && ((int)status is >= 200 and <= 299);

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

/// <summary>Marker interface for guarded live/fixture HTTP adapters.</summary>
public interface IAdventureHttpTransport : IAdventureTransport
{
}

public interface IAdventureExecutionHttpTransport : IAdventureHttpTransport
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
