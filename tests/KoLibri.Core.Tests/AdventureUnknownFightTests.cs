using KoLibri.Core.Spiel;
using Xunit;

namespace KoLibri.Core.Tests;

/// <summary>
/// Live finding 2026-11-26: `adventure run --location 439 --fight-action attack` stopped on its first
/// fight with UnknownFight ("The fight response has no valid round."), while a second identical run
/// fought the same fight. The opening fight page after adventure.php had no onturn marker; a fight page
/// read mid-fight does.
/// </summary>
public sealed class AdventureUnknownFightTests
{
    private const string Zone = "439";
    private static readonly Uri FightUri = new("https://www.kingdomofloathing.com/fight.php");

    private const string PwdHash = "0123456789abcdef0123456789abcdef";

    /// <summary>KoL-shaped opening fight page against a monster that does not belong to the zone, no onturn.</summary>
    private static string OpeningFight(string monster, bool withRound = false) => $$"""
        <html><head><script>var pwdhash = "{{PwdHash}}"; document.cookie = "PHPSESSID=s3cr3tsession; path=/";</script></head>
        <body>
        <table><tr><td>You're fighting <span id='monname'>{{monster}}</span></td></tr></table>
        {{(withRound ? "<script>var onturn = 1;</script>" : "")}}
        <form name=attack action=fight.php method=post><input type=hidden name=action value="attack">
        <input type=hidden name=pwd value="{{PwdHash}}"><input class=button type=submit value="Attack with your weapon"></form>
        </body></html>
        """;

    private const string Win = "<html><body><p>You win the fight!</p><a href=\"adventure.php?snarfblat=439\">Adventure Again</a></body></html>";

    private static AdventureActionAllowlist AttackAllowlist()
        => new AdventureActionAllowlist().AddAdventure(Zone).AddFightAction(Zone, "attack");

    [Theory]
    [InlineData("mud turtle", false)]
    [InlineData("mud turtle", true)]
    [InlineData("wandering eye", false)]
    public async Task Fresh_run_fights_an_off_zone_monster_with_the_generic_attack(string monster, bool withRound)
    {
        using var transport = new AdventureExecutionFixtureTransport();
        transport.EnqueueHtml(AdventureTransportOperation.Adventure, Zone, OpeningFight(monster, withRound), FightUri);
        transport.EnqueueHtml(AdventureTransportOperation.Fight, Zone, Win, FightUri);
        await using var gateway = new AdventureExecutionGateway(transport, new InMemoryAdventureRunCheckpointStore());

        var result = await new BoundedAdventureRunner(gateway).RunAsync(
            new AdventureRunRequest { RunId = "fresh-" + monster.Replace(' ', '-') + withRound, AdventureId = Zone },
            AttackAllowlist(),
            options: new BoundedAdventureRunnerOptions { MaxGatewaySteps = 5, MaxFightRounds = 30 });

        Assert.Equal(BoundedAdventureRunStopReason.Complete, result.StopReason);
        Assert.All(result.Steps, step => Assert.Null(step.AbortReason));
        var fight = Assert.IsType<AdventureFightAction>(Assert.Single(transport.Requests,
            request => request.Operation == AdventureTransportOperation.Fight).Action);
        Assert.Equal("attack", fight.ActionId);
        Assert.Equal(monster, result.Steps[0].Response?.Observation?.Combat?.MonsterName);
        Assert.Equal(monster.Contains("turtle"), TurtleTamerEncounters.IsTurtleMonster(result.Steps[0].Response?.Observation?.Combat?.MonsterName));
    }

    [Fact]
    public async Task A_missing_round_mid_fight_still_stops_and_saves_the_redacted_page()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kolibri-unknown-pages-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var transport = new AdventureExecutionFixtureTransport();
            transport.EnqueueHtml(AdventureTransportOperation.Adventure, Zone, OpeningFight("mud turtle", withRound: true), FightUri);
            // Round 2 page without any round marker: the round check must not be weakened mid-fight.
            transport.EnqueueHtml(AdventureTransportOperation.Fight, Zone,
                OpeningFight("mud turtle").Replace("You're fighting", "Still fighting"), FightUri);
            await using var gateway = new AdventureExecutionGateway(transport, new InMemoryAdventureRunCheckpointStore())
            {
                UnknownPageDirectory = directory
            };

            var result = await new BoundedAdventureRunner(gateway).RunAsync(
                new AdventureRunRequest { RunId = "mid-fight-no-round", AdventureId = Zone },
                AttackAllowlist(),
                options: new BoundedAdventureRunnerOptions { MaxGatewaySteps = 5 });

            Assert.Equal(BoundedAdventureRunStopReason.GatewayStopped, result.StopReason);
            var last = result.LastStep!;
            Assert.Equal(AdventureAbortReason.UnknownFight, last.AbortReason);
            Assert.NotNull(last.UnknownPagePath);
            Assert.Null(result.Steps[0].UnknownPagePath);
            Assert.Equal(directory, Path.GetDirectoryName(last.UnknownPagePath));
            Assert.Matches(@"^\d{8}-\d{6}-UnknownFight-[0-9a-f]{8}\.html$", Path.GetFileName(last.UnknownPagePath));
            Assert.Contains(last.UnknownPagePath!, AdventureManualDriver.ToOutcome(last, "mid-fight-no-round", Zone).SavedPageNote);

            var saved = await File.ReadAllTextAsync(last.UnknownPagePath!);
            Assert.Contains("Still fighting", saved);
            Assert.DoesNotContain(PwdHash, saved);
            Assert.DoesNotContain("s3cr3tsession", saved);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Without_a_directory_nothing_is_saved()
    {
        using var transport = new AdventureExecutionFixtureTransport();
        transport.EnqueueHtml(AdventureTransportOperation.Adventure, Zone,
            "<html><body><a href=\"fight.php\">fight.php</a> nothing to do here</body></html>", FightUri);
        await using var gateway = new AdventureExecutionGateway(transport, new InMemoryAdventureRunCheckpointStore());
        var result = await gateway.StartAsync(new AdventureRunRequest { RunId = "no-dir", AdventureId = Zone }, AttackAllowlist());
        Assert.Equal(AdventureAbortReason.UnknownFight, result.AbortReason);
        Assert.Null(result.UnknownPagePath);
    }

    [Fact]
    public void Redact_removes_pwd_hashes_hex_strings_and_cookies()
    {
        var html = $"""
            <input name=pwd value="{PwdHash}"><a href="inv_use.php?pwd={PwdHash}&which=3">use</a>
            <script>var pwdhash = '{PwdHash}';</script>Set-Cookie: PHPSESSID=abc123; appserver=www7
            hash {new string('f', 64)} keep 1234abcd
            """;
        var redacted = UnknownPageArchive.Redact(html);
        Assert.DoesNotContain(PwdHash, redacted);
        Assert.DoesNotContain("abc123", redacted);
        Assert.DoesNotContain("www7", redacted);
        Assert.DoesNotContain(new string('f', 64), redacted);
        Assert.Contains("keep 1234abcd", redacted);
        Assert.Contains("which=3", redacted);
    }

    [Theory]
    [InlineData("mud turtle", true)]
    [InlineData("vengeful turtle spectre", true)]
    [InlineData("The Unknown Turtle Tamer", true)]
    [InlineData("tortoise", true)]
    [InlineData("Turtleneck Sweater Golem", false)]
    [InlineData("skeleton", false)]
    [InlineData(null, false)]
    public void Turtle_monsters_are_marked(string? name, bool expected)
        => Assert.Equal(expected, TurtleTamerEncounters.IsTurtleMonster(name));
}
