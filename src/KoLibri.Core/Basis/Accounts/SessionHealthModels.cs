using System.Net;
using System.Text.Json.Serialization;

namespace KoLibri.Core.Basis;

/// <summary>
/// The states that can be reported for a KoL session.
///
/// A transient failure carries a <see cref="KoLSessionHealth.RetryAt"/>
/// value.  The <see cref="Backoff"/>, <see cref="Unavailable"/> and
/// <see cref="ServerUnavailable"/> names are aliases for that state so
/// callers can use the terminology that matches their transport adapter.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KoLSessionHealthState
{
    Unknown = 0,
    Healthy = 1,
    ExpiringSoon = 2,
    Expiring = ExpiringSoon,
    MissingCookie = 3,
    Unauthenticated = 4,
    InvalidCookie = Unauthenticated,
    InvalidCredentials = Unauthenticated,
    Expired = 5,
    RateLimited = 6,
    TransientFailure = 7,
    Backoff = TransientFailure,
    Unavailable = TransientFailure,
    ServerUnavailable = TransientFailure
}

/// <summary>
/// Coarse classification of the most recent response received from KoL.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KoLServerResponseKind
{
    Unknown = 0,
    Success = 1,
    Authenticated = Success,
    Unauthenticated = 2,
    Unauthorized = Unauthenticated,
    RateLimited = 3,
    TransientFailure = 4,
    NetworkFailure = TransientFailure,
    ServerError = TransientFailure,
    Unavailable = TransientFailure,
    ClientError = 5,
    /// <summary>The endpoint returned no usable HTML body.</summary>
    EmptyResponse = 6
}

/// <summary>
/// The response facts needed to classify a KoL session check.  Adapters can
/// construct this record without exposing response bodies or cookies to the
/// session-health layer.
/// </summary>
public record KoLServerResponse
{
    /// <summary>HTTP status returned by KoL, when an HTTP response exists.</summary>
    public int? StatusCode { get; init; }

    /// <summary>Equivalent status property for adapters that use HttpStatusCode.</summary>
    public HttpStatusCode? HttpStatusCode { get; init; }

    /// <summary>
    /// Explicit classification.  When left as <see cref="KoLServerResponseKind.Unknown"/>,
    /// <see cref="Classify"/> infers it from the status and flags below.
    /// </summary>
    public KoLServerResponseKind Kind { get; init; }

    /// <summary>True when the endpoint returned KoL's login page.</summary>
    public bool IsLoginPage { get; init; }

    /// <summary>
    /// Whether the endpoint confirmed an authenticated session.  A false
    /// value takes precedence over a successful HTTP status.
    /// </summary>
    public bool? IsAuthenticated { get; init; }

    /// <summary>
    /// Whether a session cookie was present in the response.  This is useful
    /// for a login/check adapter that captures a renewed cookie.
    /// </summary>
    public bool? HasSessionCookie { get; init; }

    /// <summary>Alias for <see cref="HasSessionCookie"/>.</summary>
    public bool? SessionCookiePresent
    {
        get => HasSessionCookie;
        init => HasSessionCookie = value;
    }

    /// <summary>Server-provided retry delay, such as a Retry-After value.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>When this response was observed by the adapter.</summary>
    public DateTimeOffset? ObservedAt { get; init; }

    /// <summary>Alias used by adapters that call the timestamp ReceivedAt.</summary>
    public DateTimeOffset? ReceivedAt { get; init; }

    /// <summary>Alias for <see cref="ObservedAt"/>.</summary>
    public DateTimeOffset? Timestamp
    {
        get => ObservedAt;
        init => ObservedAt = value;
    }

    /// <summary>An optional transport-level error label; response bodies are not retained.</summary>
    public string? Error { get; init; }

    /// <summary>Effective status code, if either status representation was supplied.</summary>
    public int? EffectiveStatusCode => StatusCode ?? (HttpStatusCode is { } status ? (int)status : null);

    /// <summary>Timestamp used for ordering this observation.</summary>
    public DateTimeOffset? EffectiveObservedAt => ObservedAt ?? ReceivedAt;

    /// <summary>Classifies this response using explicit facts and HTTP conventions.</summary>
    public KoLServerResponseKind Classify()
    {
        if (Kind != KoLServerResponseKind.Unknown)
            return Kind;

        if (string.Equals(Error, "empty-html", StringComparison.Ordinal))
            return KoLServerResponseKind.EmptyResponse;

        var status = EffectiveStatusCode;
        if (status is 401 or 403 || IsLoginPage || IsAuthenticated == false)
            return KoLServerResponseKind.Unauthenticated;

        if (status == 429)
            return KoLServerResponseKind.RateLimited;

        if (status is >= 500 and <= 599 || status is 408 or 425 ||
            (!status.HasValue && !string.IsNullOrWhiteSpace(Error)))
            return KoLServerResponseKind.TransientFailure;

        if (status is >= 400 and <= 499)
            return KoLServerResponseKind.ClientError;

        if (status is >= 200 and <= 299)
            return KoLServerResponseKind.Success;

        if (IsAuthenticated == true)
            return KoLServerResponseKind.Success;

        return KoLServerResponseKind.Unknown;
    }

    /// <summary>Alias for <see cref="Classify"/>.</summary>
    public KoLServerResponseKind Classification => Classify();

    /// <summary>Alias for <see cref="Classify"/>.</summary>
    public KoLServerResponseKind ResponseKind => Classify();

    public bool IsSuccessful => Classify() == KoLServerResponseKind.Success;
    public bool IsTransient => Classify() == KoLServerResponseKind.TransientFailure;

    public static KoLServerResponse Success(
        DateTimeOffset? observedAt = null,
        bool? hasSessionCookie = null) => new()
        {
            StatusCode = 200,
            IsAuthenticated = true,
            HasSessionCookie = hasSessionCookie,
            ObservedAt = observedAt
        };

    public static KoLServerResponse Unauthorized(DateTimeOffset? observedAt = null) => new()
    {
        StatusCode = 401,
        IsAuthenticated = false,
        ObservedAt = observedAt
    };

    public static KoLServerResponse RateLimit(
        TimeSpan? retryAfter = null,
        DateTimeOffset? observedAt = null) => new()
        {
            StatusCode = 429,
            RetryAfter = retryAfter,
            ObservedAt = observedAt
        };

    public static KoLServerResponse Empty(DateTimeOffset? observedAt = null) => new()
    {
        StatusCode = 200,
        IsAuthenticated = false,
        Error = "empty-html",
        ObservedAt = observedAt
    };

    public static KoLServerResponse Failure(
        int? statusCode = null,
        string? error = null,
        DateTimeOffset? observedAt = null) => new()
        {
            StatusCode = statusCode,
            Error = error,
            ObservedAt = observedAt
        };

    /// <summary>
    /// Builds response facts from an HTML endpoint without retaining the
    /// response body. Login forms are explicit unauthenticated responses;
    /// empty bodies are treated as invalid responses rather than successful
    /// zero-content checks.
    /// </summary>
    public static KoLServerResponse FromHtml(
        string? html,
        int statusCode = 200,
        DateTimeOffset? observedAt = null)
    {
        var kind = KoLHtmlResponseClassifier.Classify(html);
        return new KoLServerResponse
        {
            StatusCode = statusCode,
            IsLoginPage = kind == KoLHtmlResponseKind.LoginPage,
            IsAuthenticated = kind == KoLHtmlResponseKind.Content,
            ObservedAt = observedAt,
            Error = kind == KoLHtmlResponseKind.Empty && statusCode is >= 200 and <= 299
                ? "empty-html"
                : null
        };
    }
}

/// <summary>
/// Immutable input to <see cref="KoLSessionHealthEvaluator"/>.  The caller
/// owns this small history record and can persist it beside account metadata
/// without persisting a cookie value.
/// </summary>
public sealed record KoLSessionObservation
{
    public bool HasCookie { get; init; }
    public DateTimeOffset? LastSuccessAt { get; init; }
    public DateTimeOffset? LastFailureAt { get; init; }
    public KoLServerResponse? LastServerResponse { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public int ConsecutiveFailures { get; init; }
    public string? LastFailureReason { get; init; }

    /// <summary>Aliases used by session adapters.</summary>
    public bool CookiePresent
    {
        get => HasCookie;
        init => HasCookie = value;
    }

    public bool HasSessionCookie
    {
        get => HasCookie;
        init => HasCookie = value;
    }

    public KoLServerResponse? ServerResponse
    {
        get => LastServerResponse;
        init => LastServerResponse = value;
    }

    public KoLServerResponse? LastResponse
    {
        get => LastServerResponse;
        init => LastServerResponse = value;
    }

    public DateTimeOffset? SuccessAt
    {
        get => LastSuccessAt;
        init => LastSuccessAt = value;
    }

    public DateTimeOffset? FailureAt
    {
        get => LastFailureAt;
        init => LastFailureAt = value;
    }

    public int FailureCount
    {
        get => ConsecutiveFailures;
        init => ConsecutiveFailures = value;
    }

    public KoLSessionObservation()
    {
    }

    public KoLSessionObservation(
        bool hasCookie,
        DateTimeOffset? lastSuccessAt = null,
        DateTimeOffset? lastFailureAt = null,
        KoLServerResponse? lastServerResponse = null,
        DateTimeOffset? expiresAt = null,
        int consecutiveFailures = 0,
        string? lastFailureReason = null)
    {
        if (consecutiveFailures < 0)
            throw new ArgumentOutOfRangeException(nameof(consecutiveFailures));

        HasCookie = hasCookie;
        LastSuccessAt = lastSuccessAt;
        LastFailureAt = lastFailureAt;
        LastServerResponse = lastServerResponse;
        ExpiresAt = expiresAt;
        ConsecutiveFailures = consecutiveFailures;
        LastFailureReason = lastFailureReason;
    }
}

/// <summary>
/// Deterministic retry policy for transient KoL failures.  No random jitter
/// is applied here; callers that need fleet-wide jitter can add it outside
/// this pure policy and retain the resulting RetryAt in their observation.
/// </summary>
public sealed record KoLSessionRetryPolicy
{
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaximumDelay { get; init; } = TimeSpan.FromMinutes(5);
    public double BackoffMultiplier { get; init; } = 2d;
    public TimeSpan ExpiringSoonThreshold { get; init; } = TimeSpan.FromMinutes(5);
    public bool RespectServerRetryAfter { get; init; } = true;

    /// <summary>Common terminology aliases.</summary>
    public TimeSpan InitialBackoff
    {
        get => InitialDelay;
        init => InitialDelay = value;
    }

    public TimeSpan MaxBackoff
    {
        get => MaximumDelay;
        init => MaximumDelay = value;
    }

    public TimeSpan ExpiringThreshold
    {
        get => ExpiringSoonThreshold;
        init => ExpiringSoonThreshold = value;
    }

    public void Validate()
    {
        if (InitialDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(InitialDelay));
        if (MaximumDelay < InitialDelay)
            throw new ArgumentOutOfRangeException(nameof(MaximumDelay),
                "Maximum delay cannot be shorter than the initial delay.");
        if (double.IsNaN(BackoffMultiplier) || double.IsInfinity(BackoffMultiplier) || BackoffMultiplier < 1d)
            throw new ArgumentOutOfRangeException(nameof(BackoffMultiplier));
        if (ExpiringSoonThreshold < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ExpiringSoonThreshold));
    }

    /// <summary>
    /// Computes the delay after a failure.  Failure number one uses the
    /// initial delay; later failures grow exponentially up to MaximumDelay.
    /// A server Retry-After value is honored when configured and is never
    /// shortened by the client maximum.
    /// </summary>
    public TimeSpan GetDelay(int consecutiveFailures, TimeSpan? serverRetryAfter = null)
    {
        Validate();
        if (consecutiveFailures < 1)
            consecutiveFailures = 1;

        var exponent = consecutiveFailures - 1;
        var factor = Math.Pow(BackoffMultiplier, exponent);
        TimeSpan delay;
        if (factor <= 1d)
        {
            delay = InitialDelay;
        }
        else if (double.IsInfinity(factor) || InitialDelay.Ticks > long.MaxValue / factor)
        {
            delay = MaximumDelay;
        }
        else
        {
            var ticks = (long)Math.Ceiling(InitialDelay.Ticks * factor);
            delay = TimeSpan.FromTicks(Math.Min(ticks, MaximumDelay.Ticks));
        }

        if (RespectServerRetryAfter && serverRetryAfter is { } retryAfter && retryAfter > delay)
            delay = retryAfter;

        return delay;
    }

    /// <summary>Alias for <see cref="GetDelay"/>.</summary>
    public TimeSpan ComputeDelay(int consecutiveFailures, TimeSpan? serverRetryAfter = null) =>
        GetDelay(consecutiveFailures, serverRetryAfter);

    /// <summary>Alias for <see cref="GetDelay"/>.</summary>
    public TimeSpan ComputeRetryDelay(int consecutiveFailures, TimeSpan? serverRetryAfter = null) =>
        GetDelay(consecutiveFailures, serverRetryAfter);

    public DateTimeOffset GetRetryAt(
        DateTimeOffset failureAt,
        int consecutiveFailures,
        TimeSpan? serverRetryAfter = null) =>
        failureAt + GetDelay(consecutiveFailures, serverRetryAfter);
}

/// <summary>
/// The pure result of a session-health evaluation.
/// </summary>
public sealed record KoLSessionHealth
{
    public KoLSessionHealthState State { get; init; }
    public DateTimeOffset EvaluatedAt { get; init; }
    public bool HasCookie { get; init; }
    public DateTimeOffset? LastSuccessAt { get; init; }
    public DateTimeOffset? LastFailureAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public KoLServerResponse? LastServerResponse { get; init; }
    public int ConsecutiveFailures { get; init; }
    public DateTimeOffset? RetryAt { get; init; }
    public string? Reason { get; init; }

    public KoLSessionHealthState HealthState
    {
        get => State;
        init => State = value;
    }

    public KoLSessionHealthState Status
    {
        get => State;
        init => State = value;
    }

    public KoLServerResponse? ServerResponse
    {
        get => LastServerResponse;
        init => LastServerResponse = value;
    }

    public KoLServerResponse? LastResponse
    {
        get => LastServerResponse;
        init => LastServerResponse = value;
    }

    public DateTimeOffset? NextRetryAt
    {
        get => RetryAt;
        init => RetryAt = value;
    }

    public bool CookiePresent
    {
        get => HasCookie;
        init => HasCookie = value;
    }

    public bool HasSessionCookie
    {
        get => HasCookie;
        init => HasCookie = value;
    }

    public DateTimeOffset? SuccessAt
    {
        get => LastSuccessAt;
        init => LastSuccessAt = value;
    }

    public DateTimeOffset? FailureAt
    {
        get => LastFailureAt;
        init => LastFailureAt = value;
    }

    public int FailureCount
    {
        get => ConsecutiveFailures;
        init => ConsecutiveFailures = value;
    }

    public bool IsHealthy => State == KoLSessionHealthState.Healthy;
    public bool IsExpiring => State == KoLSessionHealthState.ExpiringSoon;
    public bool IsExpired => State is KoLSessionHealthState.Expired or KoLSessionHealthState.Unauthenticated;
    public bool IsUsable => State is KoLSessionHealthState.Healthy or KoLSessionHealthState.ExpiringSoon;
    public bool IsRetryable => State is KoLSessionHealthState.RateLimited or KoLSessionHealthState.TransientFailure;
    public bool IsInBackoff => RetryAt is { } retryAt && retryAt > EvaluatedAt;

    /// <summary>Delay represented by RetryAt relative to this evaluation.</summary>
    public TimeSpan? RetryDelay => RetryAt is { } retryAt && retryAt > EvaluatedAt
        ? retryAt - EvaluatedAt
        : null;

    /// <summary>
    /// Whether a retry may be attempted at the supplied time.  This method is
    /// intentionally free of clocks; callers should pass their IClock value.
    /// </summary>
    public bool CanRetry(DateTimeOffset? now = null) =>
        IsRetryable && (RetryAt is null || RetryAt <= (now ?? EvaluatedAt));

    public bool ShouldRetry => CanRetry(EvaluatedAt);

    public KoLSessionHealth()
    {
    }

    public KoLSessionHealth(
        KoLSessionHealthState state,
        DateTimeOffset evaluatedAt,
        bool hasCookie,
        DateTimeOffset? lastSuccessAt = null,
        DateTimeOffset? lastFailureAt = null,
        DateTimeOffset? expiresAt = null,
        KoLServerResponse? lastServerResponse = null,
        int consecutiveFailures = 0,
        DateTimeOffset? retryAt = null,
        string? reason = null)
    {
        if (consecutiveFailures < 0)
            throw new ArgumentOutOfRangeException(nameof(consecutiveFailures));

        State = state;
        EvaluatedAt = evaluatedAt;
        HasCookie = hasCookie;
        LastSuccessAt = lastSuccessAt;
        LastFailureAt = lastFailureAt;
        ExpiresAt = expiresAt;
        LastServerResponse = lastServerResponse;
        ConsecutiveFailures = consecutiveFailures;
        RetryAt = retryAt;
        Reason = reason;
    }
}
