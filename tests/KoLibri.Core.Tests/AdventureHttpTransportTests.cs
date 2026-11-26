using System.Net;
using System.Text;
using Xunit;
using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Tests;

public sealed class AdventureHttpTransportTests
{
    private static AdventureHttpTransportOptions Options(int maximumBytes = 4096)
        => new()
        {
            BaseUri = new Uri("https://adventure.test/", UriKind.Absolute),
            AllowedHosts = new[] { "adventure.test" },
            MaximumResponseBodyBytes = maximumBytes
        };

    private static AdventureHttpTransportOptions ProbeOptions(params AdventureStateSnapshot[] states)
    {
        var queue = new Queue<AdventureStateSnapshot>(states);
        return Options() with { StateProbe = _ => Task.FromResult(queue.Dequeue()) };
    }

    private static AdventureTransportRequest Request(
        AdventureTransportOperation operation,
        AccountRole role = AccountRole.Main,
        string? accountId = null)
        => new()
        {
            RequestId = "request-1",
            RunId = "run-1",
            AdventureId = "123",
            Operation = operation,
            Role = role,
            AccountId = accountId,
            Action = operation == AdventureTransportOperation.Fight
                ? new AdventureFightAction("123", "attack", 1)
                : operation == AdventureTransportOperation.Choice
                    ? new AdventureChoiceAction("123", "7", "1")
                    : null,
            Fields = operation switch
            {
                AdventureTransportOperation.Fight => new Dictionary<string, string>
                {
                    ["action"] = "attack", ["round"] = "1"
                },
                AdventureTransportOperation.Choice => new Dictionary<string, string>
                {
                    ["whichchoice"] = "7", ["option"] = "1"
                },
                _ => new Dictionary<string, string>()
            }
        };

    [Fact]
    public async Task FixedRoutesUsePostForMutationsAndGetForRefresh()
    {
        var handler = new RecordingHandler(
            Response(Html()), Response(Html()), Response(Html()), Response(Html()));
        using var client = new HttpClient(handler);
        using var transport = new HttpAdventureExecutionTransport(
            client,
            new AdventureTransportAccountBinding(AccountRole.Main, "main"),
            Options());

        await transport.StartAdventureAsync(Request(AdventureTransportOperation.Adventure));
        await transport.SubmitFightAsync(Request(AdventureTransportOperation.Fight));
        await transport.SubmitChoiceAsync(Request(AdventureTransportOperation.Choice));
        await transport.RefreshAsync(Request(AdventureTransportOperation.Refresh));

        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal("/adventure.php", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("/fight.php", handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.Equal("/choice.php", handler.Requests[2].RequestUri!.AbsolutePath);
        Assert.Equal("/adventure.php", handler.Requests[3].RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.Equal(HttpMethod.Post, handler.Requests[2].Method);
        Assert.Equal(HttpMethod.Get, handler.Requests[3].Method);
        Assert.Contains("snarfblat=123", handler.Bodies[0], StringComparison.Ordinal);
        Assert.Contains("action=attack", handler.Bodies[1], StringComparison.Ordinal);
        Assert.Contains("whichchoice=7", handler.Bodies[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task BoundAccountRejectsWrongRoleOrAccountBeforeHttp()
    {
        using var client = new HttpClient(new RecordingHandler(Response(Html())));
        using var transport = new HttpAdventureExecutionTransport(
            client,
            new AdventureTransportAccountBinding(AccountRole.Main, "main"),
            Options());

        await Assert.ThrowsAsync<AdventureHttpTransportException>(() =>
            transport.StartAdventureAsync(Request(AdventureTransportOperation.Adventure,
                AccountRole.Mallbot, "main")));
        await Assert.ThrowsAsync<AdventureHttpTransportException>(() =>
            transport.StartAdventureAsync(Request(AdventureTransportOperation.Adventure,
                AccountRole.Main, "mallbot")));
        Assert.Equal(0, transport.HttpRequestCount);
    }

    [Fact]
    public void BaseUriMustUseHttpsAndAnAllowlistedHost()
    {
        using var client = new HttpClient(new RecordingHandler(Response(Html())));
        Assert.Throws<ArgumentException>(() => new HttpAdventureExecutionTransport(
            client,
            new AdventureHttpTransportOptions
            {
                BaseUri = new Uri("http://adventure.test/"),
                AllowedHosts = new[] { "adventure.test" }
            }));
        Assert.Throws<ArgumentException>(() => new HttpAdventureExecutionTransport(
            client,
            new AdventureHttpTransportOptions
            {
                BaseUri = new Uri("https://evil.test/"),
                AllowedHosts = new[] { "adventure.test" }
            }));
    }

    [Fact]
    public async Task RedirectIsRejectedAndNeverFollowed()
    {
        var redirect = new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("https://adventure.test/login.php") }
        };
        var handler = new RecordingHandler(redirect, Response(Html()));
        using var client = new HttpClient(handler);
        using var transport = new HttpAdventureExecutionTransport(client, Options());

        await Assert.ThrowsAsync<AdventureHttpTransportException>(() =>
            transport.StartAdventureAsync(Request(AdventureTransportOperation.Adventure)));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AdventureRedirectToFightPhpIsFollowedOnceWithGet()
    {
        var redirect = new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("https://adventure.test/fight.php?ireallymeanit=1") }
        };
        var handler = new RecordingHandler(redirect, Response(Html()));
        using var client = new HttpClient(handler);
        using var transport = new HttpAdventureExecutionTransport(client, Options());

        var result = await transport.StartAdventureAsync(Request(AdventureTransportOperation.Adventure));

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
        Assert.Equal("/fight.php", handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.True(result.Accepted);
    }

    private static HttpResponseMessage Found(string location)
        => new(HttpStatusCode.Found) { Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) } };

    private static AdventureTransportRequest StartWithFields()
        => Request(AdventureTransportOperation.Adventure) with
        {
            Fields = new Dictionary<string, string> { ["snarfblat"] = "123" }
        };

    private const string Interstitial = "<html><body><b>Daily Reminders</b> you have things to do</body></html>";

    [Theory]
    [InlineData("<html>ordinary main page</html>")]
    [InlineData("<html>Daily Reminders <form><input name=loginname></form></html>")]
    [InlineData("")]
    public async Task OrdinaryOrErrorMainPageDoesNotTriggerTheSecondStart(string mainBody)
    {
        var handler = new RecordingHandler(Found("/main.php"), Response(mainBody), Response(Html()));
        using var client = new HttpClient(handler);
        using var transport = new HttpAdventureExecutionTransport(client, Options());

        await Assert.ThrowsAsync<AdventureHttpTransportException>(() => transport.StartAdventureAsync(StartWithFields()));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task FirstStartInterstitialFetchesMainPhpOnceAndRetriesTheSameStart()
    {
        var handler = new RecordingHandler(Found("/main.php"), Response(Interstitial), Response(Html()));
        using var client = new HttpClient(handler);
        using var transport = new HttpAdventureExecutionTransport(client,
            ProbeOptions(new AdventureStateSnapshot(50, false), new AdventureStateSnapshot(50, false)));

        var result = await transport.StartAdventureAsync(StartWithFields());

        Assert.True(result.Accepted);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
        Assert.Equal("/main.php", handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Post, handler.Requests[2].Method);
        Assert.Equal(handler.Bodies[0], handler.Bodies[2]);
        Assert.Contains("snarfblat=123", handler.Bodies[2]);
        Assert.Equal(2, handler.Requests.Count(r => r.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task InterstitialIsNotRetriedWithoutAStateProbe()
    {
        var handler = new RecordingHandler(Found("/main.php"), Response(Interstitial), Response(Html()));
        using var client = new HttpClient(handler);
        using var transport = new HttpAdventureExecutionTransport(client, Options());

        await Assert.ThrowsAsync<AdventureHttpTransportException>(() => transport.StartAdventureAsync(StartWithFields()));
        Assert.Single(handler.Requests.Where(r => r.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task InterstitialIsNotRetriedWhenATurnWasSpent()
    {
        var handler = new RecordingHandler(Found("/main.php"), Response(Interstitial), Response(Html()));
        using var client = new HttpClient(handler);
        using var transport = new HttpAdventureExecutionTransport(client,
            ProbeOptions(new AdventureStateSnapshot(50, false), new AdventureStateSnapshot(49, false)));

        // The turn was spent and nothing is open: the adventure is over (reported complete), never started again.
        var result = await transport.StartAdventureAsync(StartWithFields());
        Assert.Equal(AdventureEncounterKind.Complete, result.Observation?.Kind);
        Assert.Contains("counter changed", result.Message);
        Assert.Single(handler.Requests.Where(r => r.Method == HttpMethod.Post));
    }

    [Theory]
    [InlineData("<html>ordinary main page</html>")]
    [InlineData(Interstitial)]
    public async Task RedirectToMainPhpWithOpenEncounterContinuesItWithoutASecondStart(string mainBody)
    {
        // POST -> 302 main.php; main.php; probe says a fight/choice is open; fight.php answers 200.
        var handler = new RecordingHandler(Found("/main.php"), Response(mainBody), Response(Html()));
        using var client = new HttpClient(handler);
        using var transport = new HttpAdventureExecutionTransport(client,
            ProbeOptions(new AdventureStateSnapshot(50, false), new AdventureStateSnapshot(50, true)));

        var result = await transport.StartAdventureAsync(StartWithFields());

        Assert.True(result.Accepted);
        Assert.Single(handler.Requests.Where(r => r.Method == HttpMethod.Post));
        Assert.Equal("/fight.php", handler.Requests[2].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task UnrecognisedMainPageWithUnchangedCounterRetriesOnce()
    {
        var handler = new RecordingHandler(Found("/main.php"), Response("<html>turtle notice</html>"), Response(Html()));
        using var client = new HttpClient(handler);
        using var transport = new HttpAdventureExecutionTransport(client,
            ProbeOptions(new AdventureStateSnapshot(50, false), new AdventureStateSnapshot(50, false)));

        var result = await transport.StartAdventureAsync(StartWithFields());

        Assert.True(result.Accepted);
        Assert.Equal(2, handler.Requests.Count(r => r.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task ResumedStartWithOpenEncounterAfterMainPhpRedirectContinuesIt()
    {
        var handler = new RecordingHandler(Found("/main.php"), Response("<html>main</html>"), Response(Html()));
        using var client = new HttpClient(handler);
        using var transport = new HttpAdventureExecutionTransport(client,
            ProbeOptions(new AdventureStateSnapshot(50, true)));
        var request = StartWithFields() with
        {
            Checkpoint = new AdventureCheckpoint
            {
                RunId = "run-1", AdventureId = "123", Phase = AdventureRunPhase.Ready,
                PendingActionKind = AdventureKernelActionKind.Encounter, ResponseHashes = ["abc"]
            }
        };

        var result = await transport.StartAdventureAsync(request);

        Assert.True(result.Accepted);
        Assert.Single(handler.Requests.Where(r => r.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task ResumedStartWithFreeStateAfterMainPhpRedirectStopsWithAPreciseMessageAndNoSecondPost()
    {
        var handler = new RecordingHandler(Found("/main.php"), Response("<html>main</html>"), Response(Html()));
        using var client = new HttpClient(handler);
        using var transport = new HttpAdventureExecutionTransport(client,
            ProbeOptions(new AdventureStateSnapshot(50, false)));
        var request = StartWithFields() with
        {
            Checkpoint = new AdventureCheckpoint
            {
                RunId = "run-1", AdventureId = "123", Phase = AdventureRunPhase.Ready, ResponseHashes = ["abc"]
            }
        };

        var ex = await Assert.ThrowsAsync<AdventureHttpTransportException>(() => transport.StartAdventureAsync(request));
        Assert.Contains("no fight or choice is open", ex.Message);
        Assert.Single(handler.Requests.Where(r => r.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task StaleCounterAfterInterstitialStopsWithoutRetry()
    {
        var handler = new RecordingHandler(Found("/main.php"), Response(Interstitial), Response(Html()));
        using var client = new HttpClient(handler);
        using var transport = new HttpAdventureExecutionTransport(client,
            ProbeOptions(new AdventureStateSnapshot(50, false), new AdventureStateSnapshot(50, false, Fresh: false)));

        await Assert.ThrowsAsync<AdventureHttpTransportException>(() => transport.StartAdventureAsync(StartWithFields()));
        Assert.Single(handler.Requests.Where(r => r.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task FailingStateProbeAfterInterstitialStopsWithoutRetry()
    {
        var calls = 0;
        var options = Options() with
        {
            StateProbe = _ => ++calls == 1 ? Task.FromResult(new AdventureStateSnapshot(50, false)) : throw new HttpRequestException("down")
        };
        var handler = new RecordingHandler(Found("/main.php"), Response(Interstitial), Response(Html()));
        using var client = new HttpClient(handler);
        using var transport = new HttpAdventureExecutionTransport(client, options);

        await Assert.ThrowsAsync<AdventureHttpTransportException>(() => transport.StartAdventureAsync(StartWithFields()));
        Assert.Single(handler.Requests.Where(r => r.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task DirectOkSendsExactlyOneStart()
    {
        var handler = new RecordingHandler(Response(Html()));
        using var client = new HttpClient(handler);
        using var transport = new HttpAdventureExecutionTransport(client, Options());

        await transport.StartAdventureAsync(StartWithFields());

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RepeatedInterstitialRedirectStopsAfterTheRetry()
    {
        var handler = new RecordingHandler(Found("/main.php"), Response(Interstitial), Found("/main.php"), Response(Html()));
        using var client = new HttpClient(handler);
        using var transport = new HttpAdventureExecutionTransport(client,
            ProbeOptions(new AdventureStateSnapshot(50, false), new AdventureStateSnapshot(50, false)));

        await Assert.ThrowsAsync<AdventureHttpTransportException>(() => transport.StartAdventureAsync(StartWithFields()));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData("/login.php?notloggedin=1")]
    [InlineData("https://evil.test/main.php")]
    [InlineData("/main.php?x=1")]
    [InlineData("/main.php#a")]
    public async Task OtherRedirectsOnFirstStartSendNothingFurther(string location)
    {
        var handler = new RecordingHandler(Found(location), Response(Html()));
        using var client = new HttpClient(handler);
        using var transport = new HttpAdventureExecutionTransport(client, Options());

        await Assert.ThrowsAnyAsync<Exception>(() => transport.StartAdventureAsync(StartWithFields()));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task MainPhpRedirectingToLoginStopsWithoutRetry()
    {
        var handler = new RecordingHandler(Found("/main.php"), Found("/login.php"), Response(Html()));
        using var client = new HttpClient(handler);
        using var transport = new HttpAdventureExecutionTransport(client, Options());

        await Assert.ThrowsAsync<AdventureHttpTransportException>(() => transport.StartAdventureAsync(StartWithFields()));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task InterstitialIsNotRecoveredOnASecondStartOfTheSameTransport()
    {
        var handler = new RecordingHandler(Response(Html()), Found("/main.php"), Response(Html()));
        using var client = new HttpClient(handler);
        using var transport = new HttpAdventureExecutionTransport(client, Options());

        await transport.StartAdventureAsync(StartWithFields());
        await Assert.ThrowsAsync<AdventureHttpTransportException>(() => transport.StartAdventureAsync(StartWithFields()));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task InterstitialIsNotRecoveredWhenResumingFromACheckpoint()
    {
        var handler = new RecordingHandler(Found("/main.php"), Response(Html()));
        using var client = new HttpClient(handler);
        using var transport = new HttpAdventureExecutionTransport(client, Options());
        var request = StartWithFields() with
        {
            Checkpoint = new AdventureCheckpoint
            {
                RunId = "run-1", AdventureId = "123", Phase = AdventureRunPhase.Ready,
                ResponseHashes = ["abc"]
            }
        };

        await Assert.ThrowsAsync<AdventureHttpTransportException>(() => transport.StartAdventureAsync(request));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task FightNeverRetriesAfterAMainPhpRedirect()
    {
        var handler = new RecordingHandler(Found("/main.php"), Response(Html()));
        using var client = new HttpClient(handler);
        using var transport = new HttpAdventureExecutionTransport(client, Options());

        await Assert.ThrowsAsync<AdventureHttpTransportException>(() =>
            transport.SubmitFightAsync(Request(AdventureTransportOperation.Fight)));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task OversizedResponseIsRejectedBeforeParserSeesIt()
    {
        using var client = new HttpClient(new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("123456789", Encoding.UTF8, "text/html")
            }));
        using var transport = new HttpAdventureExecutionTransport(client, Options(4));

        await Assert.ThrowsAsync<AdventureResponseTooLargeException>(() =>
            transport.StartAdventureAsync(Request(AdventureTransportOperation.Adventure)));
    }

    [Fact]
    public async Task FixtureTracksFixedRouteWithoutUsingHttp()
    {
        using var fixture = new AdventureHttpFixtureTransport(
            Options(),
            new AdventureTransportAccountBinding(AccountRole.Main, "main"));
        fixture.EnqueueHtml(AdventureTransportOperation.Adventure, "123", Html());

        var response = await fixture.StartAdventureAsync(
            Request(AdventureTransportOperation.Adventure));

        Assert.False(fixture.UsedHttp);
        Assert.Equal(0, fixture.HttpRequestCount);
        Assert.Equal("/adventure.php", Assert.Single(fixture.HttpRequests).Uri.AbsolutePath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static string Html() => "<html><body>fixture</body></html>";

    private static HttpResponseMessage Response(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/html")
        };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public RecordingHandler(params object[] responses)
        {
            foreach (var response in responses)
            {
                if (response is not HttpResponseMessage message)
                    throw new ArgumentException("An HTTP response fixture is required.", nameof(responses));
                _responses.Enqueue(message);
            }
        }

        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            return _responses.Count == 0
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Html(), Encoding.UTF8, "text/html")
                }
                : _responses.Dequeue();
        }
    }
}

public sealed class KoLUserAgentTests
{
    [Fact]
    public void User_agent_names_the_library_and_its_version_without_build_metadata()
    {
        Assert.StartsWith("LibKolibri/", KoLibri.Core.Basis.KoLUserAgent.Value);
        Assert.DoesNotContain('+', KoLibri.Core.Basis.KoLUserAgent.Version);
        Assert.Matches(@"^\d+\.\d+", KoLibri.Core.Basis.KoLUserAgent.Version);
        Assert.Contains("github.com/grassmann-it/KoLibri", KoLibri.Core.Basis.KoLUserAgent.Value);
    }

    [Fact]
    public void Sessions_keep_the_user_agent_they_were_created_with_and_old_ones_stay_legacy()
    {
        var uri = new Uri("https://www.kingdomofloathing.com/");
        var legacy = KoLibri.Core.Basis.KoLLoginClient.CreateAuthenticatedClient(
            new KoLibri.Core.Basis.AccountCredentials { CookieHeader = "a=b" }, uri);
        Assert.Equal(KoLibri.Core.Basis.KoLUserAgent.LegacyValue, legacy.DefaultRequestHeaders.UserAgent.ToString());

        var fresh = KoLibri.Core.Basis.KoLLoginClient.CreateAuthenticatedClient(
            new KoLibri.Core.Basis.AccountCredentials { CookieHeader = "a=b", UserAgent = KoLibri.Core.Basis.KoLUserAgent.Value }, uri);
        Assert.StartsWith("LibKolibri/", fresh.DefaultRequestHeaders.UserAgent.ToString());
    }

    [Fact]
    public void Authenticated_and_pricegun_clients_send_it_and_keep_a_caller_value()
    {
        var empty = new HttpClient();
        KoLibri.Core.Basis.KoLUserAgent.ApplyTo(empty);
        Assert.Contains("LibKolibri/", empty.DefaultRequestHeaders.UserAgent.ToString());

        var own = new HttpClient();
        own.DefaultRequestHeaders.UserAgent.ParseAdd("Mine/1");
        KoLibri.Core.Basis.KoLUserAgent.ApplyTo(own);
        Assert.Equal("Mine/1", own.DefaultRequestHeaders.UserAgent.ToString());
    }
}
