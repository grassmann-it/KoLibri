using System.Net;
using KoLibri.Core.Spiel;
using Xunit;

namespace KoLibri.Core.Tests;

public sealed class AdventureFightEndAndProbeTests
{
    private static CombatObservation Parse(string body)
        => new KoLCombatObservationParser().Parse(
            "<html><body><form action=\"fight.php\"><input name=\"whichskill\"/>" + body + "</form></body></html>",
            new Uri("https://example.invalid/fight.php"));

    [Theory]
    [InlineData("You lose 5 hit points.")]
    [InlineData("You lose 12 hit points. You defeated the monster's shield.")]
    [InlineData("The goblin gets a defeat-the-purpose joke in. You lose 3 hit points.")]
    public void HitPointLossAloneIsNotDefeat(string text)
    {
        var outcome = Parse("<p>" + text + "</p>").Outcome;
        Assert.NotEqual(CombatOutcome.Defeat, outcome);
        Assert.Equal(CombatOutcome.Ongoing, outcome);
    }

    [Fact]
    public void RealWinTextEndsTheFightOnlyWithoutActions()
    {
        var done = new KoLCombatObservationParser().Parse(
            "<html><body><p>You win the fight!</p></body></html>", new Uri("https://example.invalid/fight.php"));
        Assert.Equal(CombatOutcome.Victory, done.Outcome);
    }

    [Theory]
    [InlineData("You slink away, defeated.")]
    [InlineData("You have been defeated.")]
    public void ExplicitDefeatMarkersAreDefeat(string text)
        => Assert.Equal(CombatOutcome.Defeat, Parse("<p>" + text + "</p>").Outcome);

    private sealed class FakeAccount(bool stale) : IAccountReadClient
    {
        public bool Known { get; init; } = true;
        public Task<KoLStatusSnapshot> FetchStatusAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
            => Task.FromResult(new KoLStatusSnapshot("x", 40, 0, 1, DateTimeOffset.UtcNow, stale) { AdventuresKnown = Known });
        public Task<KoLInventorySnapshot> FetchInventoryAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class Handler(Func<string, HttpStatusCode> map) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var code = map(request.RequestUri!.AbsolutePath.TrimStart('/'));
            var response = new HttpResponseMessage(code);
            if ((int)code is >= 300 and < 400 && !NoLocation) response.Headers.Location = new Uri("/main.php", UriKind.Relative);
            return Task.FromResult(response);
        }
        public bool NoLocation { get; init; }
    }

    private static Task<AdventureStateSnapshot> Probe(Func<string, HttpStatusCode> map, bool stale = false)
    {
        var http = new HttpClient(new Handler(map) { });
        return KoLAdventureSessionFactory.ReadAdventureStateAsync(
            http, new FakeAccount(stale), new Uri("https://example.invalid/"), CancellationToken.None);
    }

    [Fact]
    public async Task RedirectsOnBothPagesMeanFree()
    {
        var state = await Probe(_ => HttpStatusCode.Found);
        Assert.False(state.EncounterOpen);
        Assert.True(state.Fresh);
    }

    [Fact]
    public async Task OkOnFightMeansOpen()
        => Assert.True((await Probe(p => p == "fight.php" ? HttpStatusCode.OK : HttpStatusCode.Found)).EncounterOpen);

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task HttpErrorsAreUnknownNotFree(HttpStatusCode code)
        => await Assert.ThrowsAsync<HttpRequestException>(() =>
            Probe(p => p == "choice.php" ? code : HttpStatusCode.Found));

    [Fact]
    public async Task StaleStatusIsMarkedNotFresh()
        => Assert.False((await Probe(_ => HttpStatusCode.Found, stale: true)).Fresh);

    [Fact]
    public async Task RedirectWithoutTargetIsUnknown()
    {
        var http = new HttpClient(new Handler(_ => HttpStatusCode.Found) { NoLocation = true });
        await Assert.ThrowsAsync<HttpRequestException>(() => KoLAdventureSessionFactory.ReadAdventureStateAsync(
            http, new FakeAccount(false), new Uri("https://example.invalid/"), CancellationToken.None));
    }

    [Fact]
    public async Task OpenFightIsKeptWhenChoiceProbeFails()
        => Assert.True((await Probe(p => p == "fight.php" ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)).EncounterOpen);

    [Fact]
    public async Task MissingAdventureCounterIsNotFresh()
    {
        var http = new HttpClient(new Handler(_ => HttpStatusCode.Found));
        var state = await KoLAdventureSessionFactory.ReadAdventureStateAsync(
            http, new FakeAccount(false) { Known = false }, new Uri("https://example.invalid/"), CancellationToken.None);
        Assert.False(state.Fresh);
    }
}
