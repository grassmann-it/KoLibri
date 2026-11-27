using Xunit;

namespace KoLibri.Tests;

/// <summary>Bugs from the 0.5 live acceptance on 2026-11-27 (Main account, zone 439 and Haunted Pantry 113).</summary>
public sealed class LiveAcceptanceFixesTests
{
    private static string Fixture(string folder, string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", folder, name));

    private sealed class Recording(int option) : IChoiceResolver
    {
        public List<ChoiceContext> Seen { get; } = new();
        public ChoiceDecision Resolve(ChoiceContext context)
        {
            Seen.Add(context);
            return new ChoiceDecision(option, ChoiceDecisionSource.Configured, "test");
        }
    }

    [Fact]
    public async Task Choice_title_is_the_blue_header_of_a_real_choice_page()
    {
        var kol = new FakeKol()
            .On("POST adventure.php", (_, _) => FakeKol.Redirect("https://www.kingdomofloathing.com/choice.php"))
            .On("GET choice.php", Fixture("choice", "choice-1060.html"));
        await using var client = TestClient.Create(kol);
        await using var session = await client.OpenAsync("main");

        var encounter = Assert.IsType<ChoiceEncounter>(await session.Adventure.AdventureAsync(113));

        Assert.Equal("Temporarily Out of Skeletons", encounter.Title);
        Assert.Equal(1060, encounter.ChoiceId);
        Assert.Equal(new[] { 1, 2, 3, 5 }, encounter.Options.Select(o => o.Option));
    }

    [Fact]
    public async Task Run_hands_the_real_choice_title_to_the_resolver()
    {
        var kol = new FakeKol { StatusAdventures = 10 };
        kol.On("POST adventure.php", (_, _) => FakeKol.Redirect("https://www.kingdomofloathing.com/choice.php"))
           .On("GET choice.php", Fixture("choice", "choice-1060.html"))
           .On("POST choice.php", (_, _) => { kol.StatusAdventures--; return FakeKol.Html(ActionTests.ChoiceDone); });
        await using var client = TestClient.Create(kol);
        await using var session = await client.OpenAsync("main");
        var resolver = new Recording(2);

        await session.Adventure.RunAsync(new RunRequest(439, 1) { Choices = resolver });

        Assert.Equal("Temporarily Out of Skeletons", Assert.Single(resolver.Seen).Title);
    }

    // ------------------------------------------------------------------ auto-attack (fight ended on the first page)

    private static FakeKol AutoAttackKol()
    {
        var kol = new FakeKol { StatusAdventures = 10, StatusHp = 504, StatusMaxHp = 504 };
        kol.On("POST adventure.php", (_, _) => { kol.StatusAdventures--; return FakeKol.Redirect("https://www.kingdomofloathing.com/fight.php"); })
           .On("GET fight.php", Fixture("fight", "autoattack-won.html"));
        return kol;
    }

    [Fact]
    public async Task A_fight_won_by_auto_attack_is_a_finished_fight_not_a_noncombat()
    {
        var kol = AutoAttackKol();
        await using var client = TestClient.Create(kol);
        await using var session = await client.OpenAsync("main");
        var ended = new List<FightResult>();
        var acquired = new List<InventoryItem>();
        client.Events.FightEnded += (_, e) => ended.Add(e.Result);
        session.Events.ItemAcquired += (_, e) => acquired.Add(e.Item);

        var encounter = await session.Adventure.AdventureAsync(113);

        var fight = Assert.IsType<FinishedFightEncounter>(encounter);
        Assert.Equal("a Knob Goblin Assistant Chef", fight.MonsterName);
        Assert.Equal("a Knob Goblin Assistant Chef", fight.Title); // never combat text such as "CRITICAL HIT!"
        Assert.Equal(153, fight.MonsterId);
        Assert.Equal(FightOutcome.Won, fight.Result.Outcome);
        Assert.Equal(12, fight.Result.Meat);
        Assert.Contains(fight.Result.Items, i => i.Name == "chef's hat");
        Assert.Contains(fight.Result.Items, i => i.Name == "knucklebone");
        Assert.Same(fight.Result, Assert.Single(ended));
        Assert.Contains(acquired, i => i.Name == "knucklebone");
        Assert.Equal(0, kol.Count("POST", "fight.php"));
        Assert.Null(await session.Adventure.GetOpenEncounterAsync());
    }

    [Fact]
    public async Task Run_sees_the_monster_of_an_auto_attack_fight_and_sends_no_fight_action()
    {
        var kol = AutoAttackKol();
        await using var client = TestClient.Create(kol);
        await using var session = await client.OpenAsync("main");
        var ended = 0;
        client.Events.FightEnded += (_, _) => ended++;

        var result = await session.Adventure.RunAsync(new RunRequest(113, 5)
        {
            StopWhen = new[] { StopCondition.MonsterSeen("a Knob Goblin Assistant Chef") }
        });

        Assert.Equal(RunStopReason.Condition, result.StopReason);
        Assert.Equal("Monster seen: a Knob Goblin Assistant Chef.", result.Detail);
        Assert.Equal(1, result.TurnsSpent);
        Assert.IsType<FinishedFightEncounter>(Assert.Single(result.Encounters));
        Assert.Equal(12, result.MeatGained);
        Assert.Contains(result.Items, i => i.Name == "chef's hat");
        Assert.Equal(1, ended);
        Assert.Equal(0, kol.Count("POST", "fight.php"));
    }

    [Fact]
    public async Task Run_counts_auto_attack_fights_as_turns_and_keeps_going()
    {
        var kol = AutoAttackKol();
        await using var client = TestClient.Create(kol);
        await using var session = await client.OpenAsync("main");

        var result = await session.Adventure.RunAsync(new RunRequest(113, 3));

        Assert.Equal(RunStopReason.TurnsDone, result.StopReason);
        Assert.Equal(3, result.TurnsSpent);
        Assert.Equal(36, result.MeatGained);
        Assert.All(result.Encounters, e => Assert.IsType<FinishedFightEncounter>(e));
    }

    // ------------------------------------------------------------------ player HP of a round

    [Fact]
    public async Task A_round_reports_the_player_hp_from_status_not_a_number_from_the_combat_text()
    {
        var kol = new FakeKol { StatusHp = 321, StatusMaxHp = 504 }
            .On("GET main.php", (_, _) => FakeKol.Redirect("https://www.kingdomofloathing.com/fight.php"))
            .On("GET fight.php", """
                <html><head><script>var onturn = 1;</script></head><body><span id='monname'>a swarm of skulls</span><!-- MONSTERID: 1 -->
                <form name=attack action=fight.php method=post><input type=hidden name=action value="attack"><input class=button type=submit value="Attack!"></form></body></html>
                """)
            .On("POST fight.php", """
                <html><head><script>var onturn = 2;</script></head><body><span id='monname'>a swarm of skulls</span>
                <p>You hit it for 8 damage. Your hit points: none of your business. It hits you for 1 damage.</p>
                <form name=attack action=fight.php method=post><input type=hidden name=action value="attack"><input class=button type=submit value="Attack!"></form></body></html>
                """);
        await using var client = TestClient.Create(kol);
        await using var session = await client.OpenAsync("main");
        await session.Adventure.GetOpenEncounterAsync();

        var round = await session.Adventure.FightAsync(CombatAction.Attack);

        Assert.Equal(FightOutcome.Ongoing, round.Outcome);
        Assert.Equal(321, round.Hp);
        Assert.Equal(40, round.Mp);
    }
}
