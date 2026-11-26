using System.Net;
using System.Text;
using KoLibri.Core.Spiel;
using Xunit;

namespace KoLibri.Core.Tests;

/// <summary>
/// Live 2026-11-26 17:21 (and 15:08, same page hash ba959fbb): a fresh `adventure run` stopped with
/// UnknownFight on KoL's fight.php page "Not in a Fight". fight.php answers that page with HTTP 200 when
/// no fight is open, and the encounter probe / open-encounter read took every 200 as "a fight is open".
/// </summary>
public sealed class AdventureNotInAFightTests
{
    private static readonly Uri Base = new("https://adventure.test/");
    private static string NotInAFight => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Adventure", "not-in-a-fight.html"));

    private const string Choice = "<html><body><form action=choice.php><input type=hidden name=whichchoice value=1060>"
        + "<input type=hidden name=option value=2><input type=submit value=\"Go on\"></form></body></html>";

    [Fact]
    public void Not_in_a_fight_page_is_no_open_fight()
    {
        var encounter = new KoLAdventureEncounterParser().Parse("439", new Uri(Base, "fight.php"), NotInAFight);
        Assert.Equal(AdventureEncounterKind.Complete, encounter.Kind);
        Assert.True(KoLAdventureEncounterParser.IsNotInAFightPage(NotInAFight));
    }

    [Fact]
    public async Task Probe_does_not_count_not_in_a_fight_as_an_open_encounter()
    {
        var handler = new RouteHandler()
            .On("GET /fight.php", () => Ok(NotInAFight))
            .On("GET /choice.php", () => Found("/main.php"));
        using var http = new HttpClient(handler);
        var state = await KoLAdventureSessionFactory.ReadAdventureStateAsync(http, new Account(50), Base, CancellationToken.None);
        Assert.False(state.EncounterOpen);
    }

    /// <summary>
    /// The live path (old behaviour): adventure.php -> 302 main.php; afterwards fight.php shows "Not in a Fight"
    /// with HTTP 200, which the probe took for an open fight, so fight.php was read and judged a fight without round.
    /// </summary>
    private static RouteHandler LivePathHandler()
    {
        var fightAnswers = new Queue<Func<HttpResponseMessage>>(new Func<HttpResponseMessage>[]
        {
            () => Found("/main.php"), // before the start: no fight
            () => Ok(NotInAFight)     // after the start: the fight is already over
        });
        return new RouteHandler()
            .On("POST /adventure.php", () => Found("/main.php"))
            .On("GET /main.php", () => Ok("<html><body>main map</body></html>"))
            .On("GET /fight.php", () => fightAnswers.Count > 0 ? fightAnswers.Dequeue()() : Ok(NotInAFight))
            .On("GET /choice.php", () => Found("/main.php"));
    }

    /// <summary>Gateway start (no pre-start counter): stops without a second start; the reserved turn stays used.</summary>
    [Fact]
    public async Task Gateway_start_redirected_to_main_without_an_encounter_stops_without_a_second_start()
    {
        var handler = LivePathHandler();
        using var http = new HttpClient(handler);
        using var transport = new HttpAdventureExecutionTransport(http, Options(http, new Account(49)));
        var checkpoints = new InMemoryAdventureRunCheckpointStore();
        await using var gateway = new AdventureExecutionGateway(transport, checkpoints);

        var result = await new BoundedAdventureRunner(gateway).RunAsync(
            Request("main-no-baseline"), Allowlist(), options: new BoundedAdventureRunnerOptions { MaxGatewaySteps = 3 });

        var last = result.LastStep!;
        Assert.NotEqual(AdventureAbortReason.UnknownFight, last.AbortReason);
        Assert.Equal(AdventureAbortReason.IndeterminatePost, last.AbortReason);
        Assert.Equal(1, handler.Count("POST /adventure.php"));
        Assert.Equal(1, handler.Count("GET /choice.php"));
        var message = string.Join(" ", last.Messages) + last.State.AbortMessage;
        Assert.Contains("no fight or choice is open", message);
        Assert.Contains("POST /adventure.php -> 302 /main.php; GET /main.php -> 200; state probe", message);
        Assert.Equal(1, (await checkpoints.LoadAsync("main-no-baseline"))!.DailyLimit.TurnsUsed);
    }

    /// <summary>With a pre-start counter (pristine start) a spent turn and nothing open means: adventure over.</summary>
    [Fact]
    public async Task Pristine_start_with_spent_turn_and_nothing_open_completes_without_a_second_start()
    {
        var handler = LivePathHandler();
        using var http = new HttpClient(handler);
        using var transport = new HttpAdventureExecutionTransport(http, Options(http, new Account(50, 49)));

        var response = await transport.StartAdventureAsync(new AdventureTransportRequest
        {
            RequestId = "r1", RunId = "pristine", AdventureId = "439", Snarfblat = 439,
            Operation = AdventureTransportOperation.Adventure
        });

        Assert.Equal(AdventureEncounterKind.Complete, response.Observation?.Kind);
        Assert.Contains("counts as done", response.Message);
        Assert.Equal(1, handler.Count("POST /adventure.php"));
        Assert.Contains("POST /adventure.php -> 302 /main.php", response.Trail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Start_redirected_to_fight_php_without_a_fight_rechecks_choice_php(bool choiceOpen)
    {
        var handler = new RouteHandler()
            .On("POST /adventure.php", () => Found("/fight.php"))
            .On("GET /fight.php", () => Ok(NotInAFight))
            .On("GET /choice.php", () => choiceOpen ? Ok(Choice) : Found("/main.php"));
        using var http = new HttpClient(handler);
        using var transport = new HttpAdventureExecutionTransport(http, Options(http, new Account(50, 49)));
        await using var gateway = new AdventureExecutionGateway(transport, new InMemoryAdventureRunCheckpointStore());

        var step = await gateway.StartAsync(Request("fight-redirect-" + choiceOpen), Allowlist());

        Assert.Null(step.AbortReason);
        Assert.Equal(1, handler.Count("POST /adventure.php"));
        Assert.Equal(1, handler.Count("GET /choice.php"));
        Assert.Equal(choiceOpen ? AdventureRunPhase.Choice : AdventureRunPhase.Complete, step.Checkpoint.Phase);
        Assert.Equal(new[] { "POST /adventure.php -> 302 /fight.php", "GET /fight.php -> 200",
            choiceOpen ? "GET /choice.php -> 200" : "GET /choice.php -> 302 /main.php" }, step.Response!.Trail);
    }

    [Fact]
    public async Task Archived_unknown_page_carries_the_request_trail()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kolibri-trail-" + Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new RouteHandler()
                .On("POST /adventure.php", () => Found("/fight.php"))
                .On("GET /fight.php", () => Ok("<html><body>something about fight.php, no round, no form</body></html>"));
            using var http = new HttpClient(handler);
            using var transport = new HttpAdventureExecutionTransport(http, Options(http, new Account(50)));
            await using var gateway = new AdventureExecutionGateway(transport, new InMemoryAdventureRunCheckpointStore())
            {
                UnknownPageDirectory = directory
            };

            var step = await gateway.StartAsync(Request("trail"), Allowlist());

            Assert.Equal(AdventureAbortReason.UnknownFight, step.AbortReason);
            var saved = await File.ReadAllTextAsync(step.UnknownPagePath!);
            Assert.StartsWith("<!-- KoLibri request trail:", saved);
            Assert.Contains("POST /adventure.php -> 302 /fight.php", saved);
            Assert.Contains("GET /fight.php -> 200", saved);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Archive_writes_the_preceding_page_redacted_next_to_the_page()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kolibri-prev-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = UnknownPageArchive.TrySave(directory, AdventureAbortReason.UnknownEncounter, "<p>page</p>",
                DateTimeOffset.UnixEpoch, new[] { "POST /adventure.php -> 302 /main.php" },
                "<p>main</p><input name=pwd value=0123456789abcdef0123456789abcdef>");
            var prev = Path.ChangeExtension(path!, null) + ".prev.html";
            Assert.True(File.Exists(prev));
            Assert.Contains(Path.GetFileName(prev), File.ReadAllText(path!));
            Assert.DoesNotContain("0123456789abcdef0123456789abcdef", File.ReadAllText(prev));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static AdventureRunRequest Request(string runId)
        => new() { RunId = runId, AdventureId = "439", Snarfblat = 439 };

    private static AdventureActionAllowlist Allowlist()
        => new AdventureActionAllowlist().AddAdventure("439").AddFightAction("439", "attack").AddChoiceOption("439", "1060", "2");

    private static AdventureHttpTransportOptions Options(HttpClient http, Account account)
        => new()
        {
            BaseUri = Base,
            AllowedHosts = new[] { Base.Host },
            StateProbe = ct => KoLAdventureSessionFactory.ReadAdventureStateAsync(http, account, Base, ct)
        };

    private static HttpResponseMessage Ok(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    private static HttpResponseMessage Found(string location)
        => new(HttpStatusCode.Found) { Headers = { Location = new Uri(location, UriKind.Relative) } };

    /// <summary>Status API: each read returns the next adventure count (the last one repeats).</summary>
    private sealed class Account(params int[] adventures) : IAccountReadClient
    {
        private int _reads;
        public Task<KoLStatusSnapshot> FetchStatusAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            var value = adventures[Math.Min(_reads++, adventures.Length - 1)];
            return Task.FromResult(new KoLStatusSnapshot("x", value, 0, 1, DateTimeOffset.UtcNow, false) { AdventuresKnown = true });
        }
        public Task<KoLInventorySnapshot> FetchInventoryAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class RouteHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);
        private readonly List<string> _seen = new();

        public RouteHandler On(string route, Func<HttpResponseMessage> answer)
        {
            _routes[route] = answer;
            return this;
        }

        public int Count(string route) => _seen.Count(seen => seen == route);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var route = $"{request.Method} {request.RequestUri!.AbsolutePath}";
            _seen.Add(route);
            if (!_routes.TryGetValue(route, out var answer))
                throw new InvalidOperationException($"Unexpected request {route}.");
            var response = answer();
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
