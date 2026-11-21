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
