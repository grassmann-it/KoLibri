using System.Net;
using KolArbitrage.Core.Accounts;
using KolArbitrage.Core.Requests;
using Xunit;

namespace KolArbitrage.Core.Tests.Requests;

public sealed class KoLRequestOrchestratorTests
{
    [Fact]
    public void RetryPolicyUsesBoundedExponentialBackoffAndServerDelay()
    {
        var policy = new KoLRequestRetryPolicy
        {
            InitialDelay = TimeSpan.FromSeconds(10),
            MaximumDelay = TimeSpan.FromSeconds(45),
            BackoffMultiplier = 2
        };

        Assert.Equal(TimeSpan.FromSeconds(10), policy.GetDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(20), policy.GetDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(40), policy.GetDelay(3));
        Assert.Equal(TimeSpan.FromSeconds(45), policy.GetDelay(4));
        Assert.Equal(TimeSpan.FromSeconds(90), policy.GetDelay(1, TimeSpan.FromSeconds(90)));
    }

    [Theory]
    [InlineData("status", 200, "<html><form action=\"login.php\"><input name=\"loggingin\"></form></html>", KoLResponseKind.Login)]
    [InlineData("choice.php?whichchoice=123", 200, "<form action=\"choice.php\"><input name=\"whichchoice\" value=\"123\"></form>", KoLResponseKind.Choice)]
    [InlineData("fight.php", 200, "<form action=\"fight.php\"><input name=\"whichskill\"></form>", KoLResponseKind.Fight)]
    [InlineData("adventure.php?snarfblat=123", 200, "<html>adventure reward</html>", KoLResponseKind.Adventure)]
    [InlineData("status", 429, "rate limit", KoLResponseKind.RateLimited)]
    [InlineData("status", 503, "temporarily unavailable", KoLResponseKind.Transient)]
    public void ClassifierRecognizesKoLPageKinds(
        string route,
        int status,
        string body,
        KoLResponseKind expected)
    {
        var request = KoLRequest.Get("main", route);
        var response = KoLResponseClassifier.ClassifyResponse(request, new KoLResponse(status, body));

        Assert.Equal(expected, response.Classification);
    }

    [Fact]
    public async Task RetriesTransientIdempotentRequestButNotNonIdempotentPost()
    {
        var calls = 0;
        using var orchestrator = new KoLRequestOrchestrator(
            (request, _) =>
            {
                calls++;
                return Task.FromResult(calls < 3
                    ? new KoLResponse(503)
                    : new KoLResponse(200, "ok"));
            },
            new KoLRequestOrchestratorOptions
            {
                RetryPolicy = new KoLRequestRetryPolicy
                {
                    MaxAttempts = 3,
                    InitialDelay = TimeSpan.Zero,
                    MaximumDelay = TimeSpan.Zero
                },
                CircuitBreaker = new KoLCircuitBreakerOptions { FailureThreshold = 10 },
                DelayAsync = static (_, _) => Task.CompletedTask
            });

        var read = await orchestrator.SendAsync(KoLRequest.Get("main", "status"));
        Assert.True(read.Succeeded);
        Assert.Equal(3, read.Attempts);

        calls = 0;
        var mutation = await orchestrator.SendAsync(KoLRequest.Post("main", "fight.php", "action=attack"));
        Assert.False(mutation.Succeeded);
        Assert.Equal(1, mutation.Attempts);
        Assert.Equal(KoLResponseKind.Transient, mutation.Classification);
    }

    [Fact]
    public async Task CircuitOpensAfterRepeatedTransientResponses()
    {
        var calls = 0;
        using var orchestrator = new KoLRequestOrchestrator(
            (_, _) =>
            {
                calls++;
                return Task.FromResult(new KoLResponse(503));
            },
            new KoLRequestOrchestratorOptions
            {
                RetryPolicy = new KoLRequestRetryPolicy
                {
                    MaxAttempts = 1,
                    InitialDelay = TimeSpan.Zero,
                    MaximumDelay = TimeSpan.Zero
                },
                CircuitBreaker = new KoLCircuitBreakerOptions
                {
                    FailureThreshold = 2,
                    OpenDuration = TimeSpan.FromHours(1)
                }
            });

        await orchestrator.SendAsync(KoLRequest.Get("main", "status"));
        await orchestrator.SendAsync(KoLRequest.Get("main", "status"));

        await Assert.ThrowsAsync<KoLCircuitOpenException>(() =>
            orchestrator.SendAsync(KoLRequest.Get("main", "status")));
        Assert.Equal(KoLCircuitState.Open, orchestrator.CircuitBreaker.State);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task MutationsForOneAccountRunSerially()
    {
        var concurrent = 0;
        var peak = 0;
        var calls = 0;
        using var orchestrator = new KoLRequestOrchestrator(
            async (_, token) =>
            {
                Interlocked.Increment(ref calls);
                var active = Interlocked.Increment(ref concurrent);
                InterlockedExtensions.Max(ref peak, active);
                await Task.Delay(25, token);
                Interlocked.Decrement(ref concurrent);
                return new KoLResponse(200, "ok");
            },
            new KoLRequestOrchestratorOptions
            {
                CircuitBreaker = new KoLCircuitBreakerOptions { FailureThreshold = 10 }
            });

        var first = orchestrator.SendAsync(KoLRequest.Post("mallbot", "shop.php", "a=1"));
        var second = orchestrator.SendAsync(KoLRequest.Post("mallbot", "shop.php", "a=2"));
        await Task.WhenAll(first, second);

        Assert.Equal(2, calls);
        Assert.Equal(1, peak);
    }

    [Fact]
    public async Task GlobalAbortCancelsQueuedMutation()
    {
        using var abort = new KoLGlobalAbort();
        using var orchestrator = new KoLRequestOrchestrator(
            async (_, token) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), token);
                return new KoLResponse(200);
            },
            globalAbort: abort);

        var request = orchestrator.SendAsync(KoLRequest.Post("main", "shop.php", "a=1"));
        await Task.Delay(20);
        Assert.True(orchestrator.AbortAll("operator stop"));

        var exception = await Assert.ThrowsAsync<KoLRequestAbortedException>(() => request);
        Assert.Equal("operator stop", exception.Reason);
    }

    [Fact]
    public async Task SuccessfulRequestUpdatesReusableSessionHealth()
    {
        var at = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        using var orchestrator = new KoLRequestOrchestrator(
            (_, _) => Task.FromResult(new KoLResponse
            {
                StatusCode = (int)HttpStatusCode.OK,
                ReceivedAt = at,
                HasSessionCookie = true
            }),
            new KoLRequestOrchestratorOptions
            {
                SessionHealth = new KoLSessionHealthPolicyAdapter
                {
                    RetryPolicy = new KoLSessionRetryPolicy { ExpiringSoonThreshold = TimeSpan.Zero }
                }
            });

        var result = await orchestrator.SendAsync(KoLRequest.Get("main", "api.php"));

        Assert.True(result.Succeeded);
        Assert.Equal(KoLSessionHealthState.Healthy, orchestrator.GetSessionHealth("main", at).State);
        Assert.Equal(result.RequestId, result.Request.RequestId);
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int location, int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref location);
                if (current >= value) return;
                if (Interlocked.CompareExchange(ref location, value, current) == current) return;
            }
        }
    }
}
