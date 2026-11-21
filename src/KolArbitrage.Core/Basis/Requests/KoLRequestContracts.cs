using System.Collections.ObjectModel;
using System.Net;
using System.Text.Json.Serialization;

namespace KolArbitrage.Core.Basis;

/// <summary>
/// The broad application-level shape of a KoL response.  HTTP status alone is
/// not enough to identify a KoL response: an expired session commonly returns
/// a 200 status containing the login page.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KoLResponseKind
{
    Unknown = 0,
    Success = 1,
    Login = 2,
    LoginPage = Login,
    Unauthenticated = Login,
    Choice = 3,
    Fight = 4,
    Adventure = 5,
    RateLimited = 6,
    RateLimit = RateLimited,
    Transient = 7,
    TransientFailure = Transient,
    ServerUnavailable = Transient,
    ClientError = 8
}

/// <summary>HTTP methods with a useful default retry/mutation meaning.</summary>
public static class KoLRequestMethods
{
    public static bool IsIdempotent(HttpMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return method == HttpMethod.Get ||
            method == HttpMethod.Head ||
            method == HttpMethod.Options ||
            method == HttpMethod.Put ||
            method == HttpMethod.Delete;
    }

    public static bool IsMutation(HttpMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return method != HttpMethod.Get && method != HttpMethod.Head && method != HttpMethod.Options;
    }
}

/// <summary>
/// A typed, account-scoped KoL request.  URI values may be relative routes
/// (for example, <c>adventure.php?snarfblat=123</c>) when the orchestrator or
/// transport supplies a base URI.
/// </summary>
public sealed record KoLRequest
{
    public KoLRequest()
    {
        Method = HttpMethod.Get;
        Headers = EmptyHeaders;
        IsIdempotent = true;
    }

    public KoLRequest(
        string accountId,
        HttpMethod method,
        Uri uri,
        string? body = null,
        IReadOnlyDictionary<string, string>? headers = null,
        bool? isMutation = null,
        bool? isIdempotent = null,
        bool requiresAuthentication = true,
        string? requestId = null)
    {
        AccountId = accountId;
        Method = method;
        Uri = uri;
        Body = body;
        Headers = CopyHeaders(headers);
        IsMutation = isMutation ?? KoLRequestMethods.IsMutation(method);
        IsIdempotent = isIdempotent ?? KoLRequestMethods.IsIdempotent(method);
        RequiresAuthentication = requiresAuthentication;
        RequestId = requestId;
    }

    public KoLRequest(
        string accountId,
        HttpMethod method,
        string route,
        string? body = null,
        IReadOnlyDictionary<string, string>? headers = null,
        bool? isMutation = null,
        bool? isIdempotent = null,
        bool requiresAuthentication = true,
        string? requestId = null)
        : this(accountId, method, ParseUri(route), body, headers, isMutation,
            isIdempotent, requiresAuthentication, requestId)
    {
    }

    public KoLRequest(
        string accountId,
        string route,
        HttpMethod method,
        string? body = null,
        IReadOnlyDictionary<string, string>? headers = null,
        bool? isMutation = null,
        bool? isIdempotent = null,
        bool requiresAuthentication = true,
        string? requestId = null)
        : this(accountId, method, route, body, headers, isMutation, isIdempotent,
            requiresAuthentication, requestId)
    {
    }

    /// <summary>Account key used to select the session and mutation lane.</summary>
    public string AccountId { get; init; } = string.Empty;

    public HttpMethod Method { get; init; } = HttpMethod.Get;

    /// <summary>Absolute URI or a relative KoL route.</summary>
    public Uri Uri { get; init; } = new("/", UriKind.Relative);

    /// <summary>Alias retained for adapters that call this value Route.</summary>
    public Uri Route => Uri;

    public HttpMethod HttpMethod => Method;

    public string Path => PathAndQuery;

    /// <summary>Optional request body.  Sensitive body values are never logged.</summary>
    public string? Body { get; init; }

    public string? BodyText => Body;
    public string? Payload => Body;

    public IReadOnlyDictionary<string, string> Headers { get; init; } = EmptyHeaders;

    /// <summary>Optional form fields when a transport builds the body.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = EmptyHeaders;
    public IReadOnlyDictionary<string, string> Form => Parameters;

    /// <summary>
    /// Whether this request changes account state.  Mutations are serialized
    /// through one lane for the account.
    /// </summary>
    public bool IsMutation { get; init; }

    public bool Mutation => IsMutation;
    public bool IsWrite => IsMutation;

    /// <summary>
    /// Whether retrying after a transport failure is safe.  POST is false by
    /// default; callers must explicitly opt in for an idempotent POST.
    /// </summary>
    public bool IsIdempotent { get; init; }

    public bool Idempotent => IsIdempotent;

    public bool RequiresAuthentication { get; init; } = true;

    /// <summary>Optional caller correlation id. A new id is generated otherwise.</summary>
    public string? RequestId { get; init; }

    public string EffectiveRequestId => string.IsNullOrWhiteSpace(RequestId)
        ? string.Empty
        : RequestId!;

    /// <summary>Convenience route string used by response classifiers.</summary>
    public string PathAndQuery => Uri.IsAbsoluteUri ? Uri.PathAndQuery : Uri.OriginalString;

    public KoLRequest WithRequestId(string requestId) => this with { RequestId = requestId };

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(AccountId))
            throw new ArgumentException("An account id is required.", nameof(AccountId));
        if (Method is null)
            throw new ArgumentNullException(nameof(Method));
        if (Uri is null)
            throw new ArgumentNullException(nameof(Uri));
        if (string.IsNullOrWhiteSpace(Uri.OriginalString))
            throw new ArgumentException("A KoL route is required.", nameof(Uri));
        if (RequestId is { Length: > 0 } id && id.Length > 200)
            throw new ArgumentException("Request id is too long.", nameof(RequestId));
    }

    public static KoLRequest Get(
        string accountId,
        string route,
        bool requiresAuthentication = true,
        string? requestId = null) =>
        new(accountId, HttpMethod.Get, route, requiresAuthentication: requiresAuthentication,
            requestId: requestId);

    public static KoLRequest Post(
        string accountId,
        string route,
        string? body = null,
        bool idempotent = false,
        bool requiresAuthentication = true,
        string? requestId = null) =>
        new(accountId, HttpMethod.Post, route, body, isMutation: true,
            isIdempotent: idempotent, requiresAuthentication: requiresAuthentication,
            requestId: requestId);

    public static KoLRequest ForGet(string accountId, string route, bool requiresAuthentication = true) =>
        Get(accountId, route, requiresAuthentication);

    public static KoLRequest ForPost(string accountId, string route, string? body = null, bool idempotent = false) =>
        Post(accountId, route, body, idempotent);

    private static Uri ParseUri(string route)
    {
        if (string.IsNullOrWhiteSpace(route))
            throw new ArgumentException("A KoL route is required.", nameof(route));
        return Uri.TryCreate(route, UriKind.Absolute, out var absolute)
            ? absolute
            : new Uri(route, UriKind.RelativeOrAbsolute);
    }

    private static IReadOnlyDictionary<string, string> CopyHeaders(
        IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
            return EmptyHeaders;
        return new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase));
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>Bounded exponential retry policy for request-level failures.</summary>
public sealed record KoLRequestRetryPolicy
{
    public int MaxAttempts { get; init; } = 3;
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaximumDelay { get; init; } = TimeSpan.FromMinutes(1);
    public double BackoffMultiplier { get; init; } = 2d;
    public bool RetryTransient { get; init; } = true;
    public bool RetryRateLimited { get; init; } = true;
    public bool RespectServerRetryAfter { get; init; } = true;

    // Session-health terminology aliases.
    public TimeSpan InitialBackoff { get => InitialDelay; init => InitialDelay = value; }
    public TimeSpan MaxBackoff { get => MaximumDelay; init => MaximumDelay = value; }

    public void Validate()
    {
        if (MaxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts));
        if (InitialDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(InitialDelay));
        if (MaximumDelay < InitialDelay)
            throw new ArgumentOutOfRangeException(nameof(MaximumDelay));
        if (double.IsNaN(BackoffMultiplier) || double.IsInfinity(BackoffMultiplier) || BackoffMultiplier < 1d)
            throw new ArgumentOutOfRangeException(nameof(BackoffMultiplier));
    }

    public bool CanRetry(KoLRequest request, KoLResponse response, int completedAttempts)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);
        Validate();
        if (completedAttempts >= MaxAttempts || !request.IsIdempotent)
            return false;
        return response.Classification switch
        {
            KoLResponseKind.RateLimited => RetryRateLimited,
            KoLResponseKind.Transient => RetryTransient,
            _ => false
        };
    }

    public bool ShouldRetry(KoLRequest request, KoLResponse response, int completedAttempts) =>
        CanRetry(request, response, completedAttempts);

    public TimeSpan GetDelay(int completedAttempts, TimeSpan? serverRetryAfter = null)
    {
        Validate();
        var failureNumber = Math.Max(1, completedAttempts);
        var exponent = failureNumber - 1;
        var factor = Math.Pow(BackoffMultiplier, exponent);
        TimeSpan delay;
        if (factor <= 1d || double.IsInfinity(factor) || InitialDelay.Ticks > long.MaxValue / factor)
            delay = double.IsInfinity(factor) || InitialDelay.Ticks > long.MaxValue / Math.Max(1d, factor)
                ? MaximumDelay
                : InitialDelay;
        else
            delay = TimeSpan.FromTicks(Math.Min(MaximumDelay.Ticks,
                (long)Math.Ceiling(InitialDelay.Ticks * factor)));

        if (RespectServerRetryAfter && serverRetryAfter is { } server && server > delay)
            delay = server;
        return delay;
    }

    public TimeSpan ComputeDelay(int completedAttempts, TimeSpan? serverRetryAfter = null) =>
        GetDelay(completedAttempts, serverRetryAfter);
}

/// <summary>Options for the request orchestrator.</summary>
public sealed record KoLRequestOrchestratorOptions
{
    public Uri? BaseUri { get; init; }
    public KoLRequestRetryPolicy RetryPolicy { get; init; } = new();
    public KoLCircuitBreakerOptions CircuitBreaker { get; init; } = new();
    public KoLSessionHealthPolicyAdapter SessionHealth { get; init; } = new();
    public int MaximumResponseBodyBytes { get; init; } = 1_048_576;
    public TimeSpan RequestTimeout { get; init; } = Timeout.InfiniteTimeSpan;

    /// <summary>
    /// Delay hook used by deterministic hosts and tests.  Production defaults
    /// to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
    /// </summary>
    public Func<TimeSpan, CancellationToken, Task>? DelayAsync { get; init; }

    public void Validate()
    {
        RetryPolicy.Validate();
        CircuitBreaker.Validate();
        SessionHealth.Validate();
        if (MaximumResponseBodyBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumResponseBodyBytes));
        if (RequestTimeout != Timeout.InfiniteTimeSpan && RequestTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
    }
}

/// <summary>
/// Small adapter around the existing SessionHealth policy.  Keeping this
/// record in Requests makes orchestrator configuration explicit while all
/// state evaluation remains in <c>KoLSessionHealthEvaluator</c>.
/// </summary>
public sealed record KoLSessionHealthPolicyAdapter
{
    public KoLSessionRetryPolicy RetryPolicy { get; init; } = new();

    public void Validate() => RetryPolicy.Validate();
}

/// <summary>Transport result consumed by the classifier and orchestrator.</summary>
public sealed record KoLResponse
{
    public KoLResponse()
    {
        Headers = EmptyHeaders;
        ReceivedAt = DateTimeOffset.UtcNow;
    }

    public KoLResponse(
        int statusCode,
        string? body = null,
        Uri? requestUri = null,
        IReadOnlyDictionary<string, string>? headers = null,
        DateTimeOffset? receivedAt = null,
        TimeSpan? retryAfter = null,
        KoLResponseKind classification = KoLResponseKind.Unknown,
        string? error = null,
        bool? hasSessionCookie = null)
    {
        StatusCode = statusCode;
        Body = body;
        RequestUri = requestUri;
        Headers = CopyHeaders(headers);
        ReceivedAt = receivedAt ?? DateTimeOffset.UtcNow;
        RetryAfter = retryAfter;
        Classification = classification;
        Error = error;
        HasSessionCookie = hasSessionCookie;
    }

    public int? StatusCode { get; init; }
    public int? Status => StatusCode;
    public HttpStatusCode? HttpStatusCode
    {
        get => StatusCode is { } status ? (System.Net.HttpStatusCode)status : null;
        init => StatusCode = value is { } status ? (int)status : null;
    }

    public Uri? RequestUri { get; init; }
    public Uri? Uri => RequestUri;
    public string? Body { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } = EmptyHeaders;
    public DateTimeOffset ReceivedAt { get; init; }
    public DateTimeOffset ObservedAt => ReceivedAt;
    public TimeSpan? RetryAfter { get; init; }
    public KoLResponseKind Classification { get; init; }
    public KoLResponseKind Kind { get => Classification; init => Classification = value; }
    public KoLResponseKind ResponseKind => Classification;
    public string? Error { get; init; }
    public bool? HasSessionCookie { get; init; }
    public bool? SessionCookiePresent { get => HasSessionCookie; init => HasSessionCookie = value; }
    public bool IsLoginPage { get; init; }
    public bool IsTransient => Classification is KoLResponseKind.Transient or KoLResponseKind.RateLimited;
    public bool IsSuccessful => Classification is KoLResponseKind.Success or KoLResponseKind.Choice
        or KoLResponseKind.Fight or KoLResponseKind.Adventure;

    public KoLResponse WithClassification(KoLResponseKind kind) => this with { Classification = kind };

    public KoLServerResponse ToServerResponse()
    {
        var kind = Classification;
        var serverKind = kind switch
        {
            KoLResponseKind.Login => KoLServerResponseKind.Unauthenticated,
            KoLResponseKind.RateLimited => KoLServerResponseKind.RateLimited,
            KoLResponseKind.Transient => KoLServerResponseKind.TransientFailure,
            KoLResponseKind.ClientError => KoLServerResponseKind.ClientError,
            KoLResponseKind.Success or KoLResponseKind.Choice or KoLResponseKind.Fight or KoLResponseKind.Adventure
                => KoLServerResponseKind.Success,
            _ => KoLServerResponseKind.Unknown
        };
        return new KoLServerResponse
        {
            StatusCode = StatusCode,
            Kind = serverKind,
            IsLoginPage = IsLoginPage || kind == KoLResponseKind.Login,
            IsAuthenticated = kind == KoLResponseKind.Login ? false :
                IsSuccessful ? true : null,
            HasSessionCookie = HasSessionCookie,
            RetryAfter = RetryAfter,
            ObservedAt = ReceivedAt,
            Error = Error
        };
    }

    public static KoLResponse TransientFailure(
        Exception exception,
        Uri? requestUri = null,
        DateTimeOffset? receivedAt = null) => new()
    {
        RequestUri = requestUri,
        ReceivedAt = receivedAt ?? DateTimeOffset.UtcNow,
        Classification = KoLResponseKind.Transient,
        Error = exception.Message
    };

    private static IReadOnlyDictionary<string, string> CopyHeaders(
        IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
            return EmptyHeaders;
        return new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase));
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>Transport boundary for the request/session orchestrator.</summary>
public interface IKoLRequestTransport
{
    Task<KoLResponse> SendAsync(KoLRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Delegate adapter for tests and small hosts.</summary>
public sealed class DelegateKoLRequestTransport : IKoLRequestTransport
{
    private readonly Func<KoLRequest, CancellationToken, Task<KoLResponse>> _sender;

    public DelegateKoLRequestTransport(Func<KoLRequest, CancellationToken, Task<KoLResponse>> sender)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
    }

    public Task<KoLResponse> SendAsync(KoLRequest request, CancellationToken cancellationToken = default) =>
        _sender(request, cancellationToken);
}

/// <summary>Result with attempt/request metadata, preserving the classified response.</summary>
public sealed record KoLRequestResult
{
    public required KoLRequest Request { get; init; }
    public required string RequestId { get; init; }
    public KoLResponse? Response { get; init; }
    public int Attempts { get; init; }
    public Exception? Error { get; init; }
    public bool Aborted { get; init; }
    public KoLResponseKind Classification => Response?.Classification ?? KoLResponseKind.Unknown;
    public KoLResponseKind ResponseKind => Classification;
    public bool Succeeded => Response?.IsSuccessful == true && Error is null && !Aborted;
    public bool RequiresLogin => Classification == KoLResponseKind.Login;
    public bool IsRetryable => Classification is KoLResponseKind.RateLimited or KoLResponseKind.Transient;
    public KoLResponse? RawResponse => Response;
    public KoLSessionHealth? SessionHealth { get; init; }
}

public static class KoLResponseResultExtensions
{
    public static bool IsLogin(this KoLRequestResult result) => result.RequiresLogin;
    public static bool IsRateLimited(this KoLRequestResult result) =>
        result.Classification == KoLResponseKind.RateLimited;
}
