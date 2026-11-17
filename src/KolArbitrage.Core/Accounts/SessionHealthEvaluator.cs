namespace KolArbitrage.Core.Accounts;

/// <summary>
/// Pure session-health evaluator.  It never mutates an observation and does
/// not retain account or transport state.  Callers should pass their clock's
/// value as <paramref name="now"/> so the same observation is reproducible in
/// tests and offline workflow plans.
/// </summary>
public static class KoLSessionHealthEvaluator
{
    public static KoLSessionHealth Evaluate(
        KoLSessionObservation observation,
        KoLSessionRetryPolicy? policy = null,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var retryPolicy = policy ?? new KoLSessionRetryPolicy();
        retryPolicy.Validate();
        var evaluatedAt = now ?? DateTimeOffset.UtcNow;
        var response = observation.LastServerResponse;
        var responseKind = response?.Classify() ?? KoLServerResponseKind.Unknown;
        var responseAt = response?.EffectiveObservedAt;

        // A response with an explicit timestamp is stale when it predates
        // either recorded event.  Untimestamped responses represent the
        // current check and therefore supersede older history.
        var latestHistoryAt = Latest(observation.LastSuccessAt, observation.LastFailureAt);
        var responseIsCurrent = response is not null &&
            (responseAt is null || latestHistoryAt is null || responseAt >= latestHistoryAt);
        if (!responseIsCurrent)
            responseKind = KoLServerResponseKind.Unknown;

        // A login/check endpoint may renew the cookie in its response.  The
        // credential itself remains opaque; only presence is carried forward.
        var hasCookie = observation.HasCookie ||
            (responseIsCurrent && response?.HasSessionCookie == true);
        var lastSuccessAt = observation.LastSuccessAt;
        var lastFailureAt = observation.LastFailureAt;
        if (responseIsCurrent && responseKind == KoLServerResponseKind.Success)
            lastSuccessAt = responseAt ?? evaluatedAt;
        else if (responseIsCurrent && responseKind is
                 KoLServerResponseKind.Unauthenticated or
                 KoLServerResponseKind.RateLimited or
                 KoLServerResponseKind.TransientFailure or
                 KoLServerResponseKind.EmptyResponse or
                 KoLServerResponseKind.ClientError)
            lastFailureAt = responseAt ?? evaluatedAt;

        var result = new KoLSessionHealth
        {
            EvaluatedAt = evaluatedAt,
            HasCookie = hasCookie,
            LastSuccessAt = lastSuccessAt,
            LastFailureAt = lastFailureAt,
            ExpiresAt = observation.ExpiresAt,
            LastServerResponse = response,
            ConsecutiveFailures = observation.ConsecutiveFailures,
            State = KoLSessionHealthState.Unknown
        };

        // No cookie means the caller cannot issue an authenticated request,
        // regardless of an old success timestamp.
        if (!hasCookie)
        {
            return result with
            {
                State = KoLSessionHealthState.MissingCookie,
                ConsecutiveFailures = 0,
                RetryAt = null,
                Reason = "No KoL session cookie is available."
            };
        }

        if (observation.ExpiresAt is { } expiresAt && expiresAt <= evaluatedAt)
        {
            return result with
            {
                State = KoLSessionHealthState.Expired,
                ConsecutiveFailures = 0,
                RetryAt = null,
                Reason = "The configured KoL session expiry has passed."
            };
        }

        if (responseIsCurrent)
        {
            switch (responseKind)
            {
                case KoLServerResponseKind.Unauthenticated:
                    return result with
                    {
                        State = KoLSessionHealthState.Unauthenticated,
                        ConsecutiveFailures = 0,
                        RetryAt = null,
                        Reason = response?.IsLoginPage == true
                            ? "KoL returned the login page for the session check."
                            : "KoL rejected the session cookie."
                    };

                case KoLServerResponseKind.RateLimited:
                    return BuildRetryable(
                        result,
                        observation,
                        response,
                        evaluatedAt,
                        retryPolicy,
                        KoLSessionHealthState.RateLimited,
                        "KoL rate-limited the session check.");

                case KoLServerResponseKind.TransientFailure:
                case KoLServerResponseKind.EmptyResponse:
                    return BuildRetryable(
                        result,
                        observation,
                        response,
                        evaluatedAt,
                        retryPolicy,
                        KoLSessionHealthState.TransientFailure,
                        response?.Error is { Length: > 0 } error
                            ? $"KoL session check failed: {error}."
                            : "KoL was temporarily unavailable during the session check.");

                case KoLServerResponseKind.ClientError:
                    // A 4xx that is not an auth response generally means the
                    // route/request was invalid.  It should not trigger an
                    // authentication retry loop, but it is not healthy.
                    return result with
                    {
                        State = KoLSessionHealthState.Unknown,
                        ConsecutiveFailures = 0,
                        RetryAt = null,
                        Reason = "KoL returned an unexpected client response."
                    };

                case KoLServerResponseKind.Success:
                    return BuildHealthy(result, retryPolicy);
            }
        }

        // With no current response, the most recent recorded event determines
        // whether this is a known-good session or a transient failure.
        if (lastFailureAt is { } failureAt &&
            (lastSuccessAt is null || failureAt >= lastSuccessAt))
        {
            return BuildRetryable(
                result,
                observation,
                response: null,
                evaluatedAt,
                retryPolicy,
                KoLSessionHealthState.TransientFailure,
                observation.LastFailureReason ?? "The last KoL session check failed.",
                failureAt);
        }

        if (lastSuccessAt is not null)
            return BuildHealthy(result, retryPolicy);

        return result with
        {
            State = KoLSessionHealthState.Unknown,
            ConsecutiveFailures = 0,
            RetryAt = null,
            Reason = "The session has a cookie but has not been checked yet."
        };
    }

    /// <summary>
    /// Convenience overload for adapters that have not materialized an
    /// observation record yet.
    /// </summary>
    public static KoLSessionHealth Evaluate(
        DateTimeOffset? lastSuccessAt,
        DateTimeOffset? lastFailureAt,
        bool hasCookie,
        KoLServerResponse? serverResponse = null,
        DateTimeOffset? expiresAt = null,
        int consecutiveFailures = 0,
        KoLSessionRetryPolicy? policy = null,
        DateTimeOffset? now = null)
    {
        var observation = new KoLSessionObservation(
            hasCookie,
            lastSuccessAt,
            lastFailureAt,
            serverResponse,
            expiresAt,
            consecutiveFailures);
        return Evaluate(observation, policy, now);
    }

    /// <summary>
    /// Convenience overload using cookie presence as the first argument,
    /// which mirrors the order used by most HTTP session adapters.
    /// </summary>
    public static KoLSessionHealth Evaluate(
        bool hasCookie,
        DateTimeOffset? lastSuccessAt = null,
        DateTimeOffset? lastFailureAt = null,
        KoLServerResponse? serverResponse = null,
        DateTimeOffset? expiresAt = null,
        int consecutiveFailures = 0,
        KoLSessionRetryPolicy? policy = null,
        DateTimeOffset? now = null) =>
        Evaluate(
            lastSuccessAt,
            lastFailureAt,
            hasCookie,
            serverResponse,
            expiresAt,
            consecutiveFailures,
            policy,
            now);

    /// <summary>
    /// Builds the cookie/expiry portion of an observation from credentials
    /// while leaving request history supplied by the caller intact.  The
    /// credential value itself is never copied into the health result.
    /// </summary>
    public static KoLSessionHealth FromCredentials(
        AccountCredentials credentials,
        KoLSessionObservation? observation = null,
        KoLSessionRetryPolicy? policy = null,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var baseObservation = observation ?? new KoLSessionObservation();
        var merged = baseObservation with
        {
            HasCookie = baseObservation.HasCookie || !string.IsNullOrWhiteSpace(credentials.CookieHeader),
            ExpiresAt = baseObservation.ExpiresAt ?? credentials.ExpiresAt
        };
        return Evaluate(merged, policy, now);
    }

    /// <summary>Alias for <see cref="Evaluate(KoLSessionObservation, KoLSessionRetryPolicy?, DateTimeOffset?)"/>.</summary>
    public static KoLSessionHealth Assess(
        KoLSessionObservation observation,
        KoLSessionRetryPolicy? policy = null,
        DateTimeOffset? now = null) => Evaluate(observation, policy, now);

    /// <summary>Alias for <see cref="Evaluate(KoLSessionObservation, KoLSessionRetryPolicy?, DateTimeOffset?)"/>.</summary>
    public static KoLSessionHealth EvaluateHealth(
        KoLSessionObservation observation,
        KoLSessionRetryPolicy? policy = null,
        DateTimeOffset? now = null) => Evaluate(observation, policy, now);

    private static KoLSessionHealth BuildHealthy(
        KoLSessionHealth result,
        KoLSessionRetryPolicy policy)
    {
        var state = KoLSessionHealthState.Healthy;
        if (result.ExpiresAt is { } expiresAt)
        {
            // The expiry threshold is deliberately evaluated against the
            // supplied evaluation timestamp, not wall-clock time.
            var expiringAt = result.EvaluatedAt + policy.ExpiringSoonThreshold;
            if (expiresAt <= expiringAt)
                state = KoLSessionHealthState.ExpiringSoon;
        }

        return result with
        {
            State = state,
            ConsecutiveFailures = 0,
            RetryAt = null,
            Reason = state == KoLSessionHealthState.ExpiringSoon
                ? "The KoL session is valid but will expire soon."
                : "The KoL session check succeeded."
        };
    }

    private static KoLSessionHealth BuildRetryable(
        KoLSessionHealth result,
        KoLSessionObservation observation,
        KoLServerResponse? response,
        DateTimeOffset evaluatedAt,
        KoLSessionRetryPolicy policy,
        KoLSessionHealthState state,
        string reason,
        DateTimeOffset? failureAtOverride = null)
    {
        var failures = Math.Max(1, observation.ConsecutiveFailures);
        var failureAt = failureAtOverride ?? response?.EffectiveObservedAt ?? evaluatedAt;
        var retryAt = policy.GetRetryAt(failureAt, failures, response?.RetryAfter);
        return result with
        {
            State = state,
            ConsecutiveFailures = failures,
            RetryAt = retryAt,
            Reason = reason
        };
    }

    private static DateTimeOffset? Latest(DateTimeOffset? first, DateTimeOffset? second) =>
        first is null ? second : second is null ? first : first >= second ? first : second;
}
