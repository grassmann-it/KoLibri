using KolArbitrage.Core.Accounts;
using Xunit;

namespace KolArbitrage.Core.Tests;

public sealed class SessionHealthTests
{
    private static readonly DateTimeOffset At =
        new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MissingCookieIsNotUsableEvenWhenAnOldCheckSucceeded()
    {
        var health = KoLSessionHealthEvaluator.Evaluate(
            new KoLSessionObservation
            {
                LastSuccessAt = At.AddHours(-1),
                HasCookie = false
            },
            now: At);

        Assert.Equal(KoLSessionHealthState.MissingCookie, health.State);
        Assert.False(health.IsUsable);
        Assert.False(health.IsRetryable);
        Assert.Null(health.RetryAt);
    }

    [Fact]
    public void SuccessfulResponseProducesHealthySession()
    {
        var health = KoLSessionHealthEvaluator.Evaluate(
            new KoLSessionObservation
            {
                HasCookie = true,
                LastServerResponse = KoLServerResponse.Success(At)
            },
            now: At);

        Assert.Equal(KoLSessionHealthState.Healthy, health.State);
        Assert.True(health.IsHealthy);
        Assert.True(health.IsUsable);
        Assert.Equal(At, health.LastSuccessAt);
        Assert.False(health.ShouldRetry);
    }

    [Fact]
    public void NearExpiryIsReportedBeforeTheSessionExpires()
    {
        var health = KoLSessionHealthEvaluator.Evaluate(
            new KoLSessionObservation
            {
                HasCookie = true,
                LastSuccessAt = At,
                ExpiresAt = At.AddMinutes(2)
            },
            new KoLSessionRetryPolicy { ExpiringSoonThreshold = TimeSpan.FromMinutes(5) },
            At);

        Assert.Equal(KoLSessionHealthState.ExpiringSoon, health.State);
        Assert.True(health.IsExpiring);
        Assert.True(health.IsUsable);
        Assert.False(health.IsExpired);
    }

    [Fact]
    public void ExpiryAndUnauthorizedResponseAreTerminalUntilRelogin()
    {
        var expired = KoLSessionHealthEvaluator.Evaluate(
            new KoLSessionObservation
            {
                HasCookie = true,
                LastSuccessAt = At.AddMinutes(-10),
                ExpiresAt = At
            },
            now: At);
        var unauthorized = KoLSessionHealthEvaluator.Evaluate(
            new KoLSessionObservation
            {
                HasCookie = true,
                LastServerResponse = KoLServerResponse.Unauthorized(At)
            },
            now: At);

        Assert.Equal(KoLSessionHealthState.Expired, expired.State);
        Assert.Equal(KoLSessionHealthState.Unauthenticated, unauthorized.State);
        Assert.True(expired.IsExpired);
        Assert.True(unauthorized.IsExpired);
        Assert.False(unauthorized.IsRetryable);
    }

    [Fact]
    public void TransientFailuresUseDeterministicExponentialBackoff()
    {
        var policy = new KoLSessionRetryPolicy
        {
            InitialDelay = TimeSpan.FromSeconds(10),
            MaximumDelay = TimeSpan.FromSeconds(45),
            BackoffMultiplier = 2
        };
        var health = KoLSessionHealthEvaluator.Evaluate(
            new KoLSessionObservation
            {
                HasCookie = true,
                LastFailureAt = At,
                ConsecutiveFailures = 3
            },
            policy,
            At);

        Assert.Equal(KoLSessionHealthState.TransientFailure, health.State);
        Assert.Equal(At.AddSeconds(40), health.RetryAt);
        Assert.True(health.IsInBackoff);
        Assert.False(health.CanRetry(At.AddSeconds(39)));
        Assert.True(health.CanRetry(At.AddSeconds(40)));
        Assert.Equal(TimeSpan.FromSeconds(40), health.RetryDelay);
    }

    [Fact]
    public void ServerRetryAfterIsHonoredForRateLimits()
    {
        var response = KoLServerResponse.RateLimit(TimeSpan.FromSeconds(90), At);
        var health = KoLSessionHealthEvaluator.Evaluate(
            new KoLSessionObservation
            {
                HasCookie = true,
                LastServerResponse = response
            },
            new KoLSessionRetryPolicy
            {
                InitialDelay = TimeSpan.FromSeconds(5),
                MaximumDelay = TimeSpan.FromSeconds(30)
            },
            At);

        Assert.Equal(KoLSessionHealthState.RateLimited, health.State);
        Assert.Equal(At.AddSeconds(90), health.RetryAt);
        Assert.False(health.CanRetry(At.AddSeconds(89)));
        Assert.True(health.CanRetry(At.AddSeconds(90)));
    }

    [Fact]
    public void FixedInputAndClockProduceTheSameResult()
    {
        var observation = new KoLSessionObservation
        {
            HasCookie = true,
            LastFailureAt = At.AddSeconds(-2),
            ConsecutiveFailures = 2,
            LastFailureReason = "timeout"
        };
        var policy = new KoLSessionRetryPolicy { InitialDelay = TimeSpan.FromSeconds(5) };

        var first = KoLSessionHealthEvaluator.Evaluate(observation, policy, At);
        var second = KoLSessionHealthEvaluator.Evaluate(observation, policy, At);

        Assert.Equal(first, second);
    }

    [Fact]
    public void CredentialsHelperUsesCookiePresenceWithoutCopyingSecrets()
    {
        var credentials = new AccountCredentials
        {
            AccessToken = "opaque-token",
            CookieHeader = "charpwd=opaque-cookie",
            ExpiresAt = At.AddHours(1)
        };

        var health = KoLSessionHealthEvaluator.FromCredentials(
            credentials,
            new KoLSessionObservation { LastSuccessAt = At },
            now: At);

        Assert.Equal(KoLSessionHealthState.Healthy, health.State);
        Assert.True(health.HasCookie);
        Assert.DoesNotContain("opaque", health.Reason ?? string.Empty);
    }
}
