using Xunit;

namespace KoLibri.Tests;

public sealed class CombatStrategyTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fight", name));

    private static string RepoData()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "external", "kolmafia-data");
            if (File.Exists(Path.Combine(path, "skills.jsonl"))) return path;
        }
        throw new InvalidOperationException("external/kolmafia-data not found");
    }

    private static GameData Data(string? directory = null)
    {
        var client = KolibriClient.Create(new KolibriOptions
        {
            HttpHandler = new FakeKol(),
            AccountStore = MemoryAccountStore.WithMain(),
            StateDirectory = Directory.CreateTempSubdirectory("kolibri-tests-").FullName,
            DisableLimiterForTests = true,
            DataDirectory = directory ?? RepoData()
        });
        return client.Data;
    }

    private static FightEncounter Fight(string monster = "skeleton", int? id = 123)
        => new(new Page(new Uri("https://www.kingdomofloathing.com/fight.php"), 200, "", PageKind.Fight, DateTimeOffset.UtcNow), null, monster, monster, id, 1);

    private static CombatContext Context(string monster = "skeleton", int mp = 40, int round = 1, IReadOnlyList<FightRound>? history = null,
        IReadOnlyList<CombatOption>? skills = null, IReadOnlyList<CombatOption>? items = null)
        => new(Fight(monster), round, 80, 100, mp, 50, history ?? Array.Empty<FightRound>())
        {
            AvailableSkills = skills ?? Array.Empty<CombatOption>(),
            AvailableItems = items ?? Array.Empty<CombatOption>()
        };

    private static readonly CombatOption Toss = new(2023, "Toss", 1);

    [Fact]
    public void Names_resolve_to_ids_and_unknown_names_fail_before_any_request()
    {
        var data = Data();
        var rules = CombatStrategies.FromRules("if monster \"skeleton\" then skill \"Toss\"; item \"seal tooth\"", data);
        var action = rules.Next(Context());
        Assert.Equal(new CombatAction(CombatActionKind.Skill, 2023), action);

        var unknown = Assert.Throws<ArgumentException>(() => CombatStrategies.FromRules("skill \"Gibt es nicht\"; attack", data));
        Assert.Contains("Unbekannter Skill", unknown.Message);
        Assert.Throws<ArgumentException>(() => CombatStrategies.FromRules("item \"Gibt es nicht\"", data));
    }

    [Fact]
    public void Item_names_resolve_too()
    {
        var rules = CombatStrategies.FromRules("item \"seal tooth\"", Data());
        Assert.Equal(new CombatAction(CombatActionKind.Item, 2), rules.Next(Context()));
    }

    [Fact]
    public void Missing_game_data_is_reported_as_such_not_as_unknown_skill()
    {
        var empty = Data(Path.Combine(Path.GetTempPath(), "kolibri-no-such-data-" + Guid.NewGuid()));
        var error = Assert.Throws<ArgumentException>(() => CombatStrategies.FromRules("skill \"Toss\"; attack", empty));
        Assert.Contains("Spieldaten fehlen", error.Message);
        Assert.DoesNotContain("Unbekannter", error.Message);
        // Ids need no data.
        CombatStrategies.FromRules("skill 2023; attack", empty);
    }

    [Fact]
    public void A_skill_the_page_does_not_offer_falls_back_to_the_next_rule_or_stops()
    {
        var data = Data();
        var withFallback = CombatStrategies.FromRules("skill \"Toss\"; attack", data);
        var offered = new[] { new CombatOption(3004, "Entangling Noodles", 4) };
        Assert.Equal(CombatAction.Attack, withFallback.Next(Context(skills: offered)));
        Assert.Equal(CombatActionKind.Skill, withFallback.Next(Context(skills: new[] { Toss })).Kind);

        var onlySkill = CombatStrategies.FromRules("if monster skeleton then skill 2023", data);
        Assert.Equal(CombatAction.Stop, onlySkill.Next(Context(skills: offered)));
        // An empty list means "page showed no list": nothing is excluded.
        Assert.Equal(CombatActionKind.Skill, onlySkill.Next(Context()).Kind);
    }

    [Fact]
    public void Too_little_mp_falls_back()
    {
        var rules = CombatStrategies.FromRules("skill 3004; attack", Data());
        var noodles = new[] { new CombatOption(3004, "Entangling Noodles", 4) };
        Assert.Equal(CombatAction.Attack, rules.Next(Context(mp: 3, skills: noodles)));
        Assert.Equal(CombatActionKind.Skill, rules.Next(Context(mp: 4, skills: noodles)).Kind);
        // Cost from the game data when the page has none (Toss costs 1).
        var toss = CombatStrategies.FromRules("skill \"Toss\"; attack", Data());
        Assert.Equal(CombatAction.Attack, toss.Next(Context(mp: 0, skills: new[] { new CombatOption(2023, "Toss", null) })));
    }

    [Fact]
    public void An_unavailable_item_is_skipped_too()
    {
        var rules = CombatStrategies.FromRules("item 2; attack", Data());
        Assert.Equal(CombatAction.Attack, rules.Next(Context(items: new[] { new CombatOption(9, "other", null) })));
        Assert.Equal(CombatActionKind.Item, rules.Next(Context(items: new[] { new CombatOption(2, "seal tooth", null) })).Kind);
    }

    [Fact]
    public void Turtle_monsters_stop_and_others_pass_through()
    {
        var strategy = CombatStrategies.StopOnTurtle(CombatStrategies.AttackOnly);
        Assert.Equal(CombatAction.Stop, strategy.Next(Context("mud turtle")));
        Assert.Equal(CombatAction.Attack, strategy.Next(Context("skeleton")));
        Assert.True(Context("mud turtle").IsTurtleMonster);
        Assert.False(Context("skeleton").IsTurtleMonster);
        Assert.Equal(123, Context().MonsterId);
    }

    [Fact]
    public async Task FightToEnd_passes_the_dropdowns_of_the_fight_page_to_the_strategy()
    {
        var kol = new FakeKol()
            .On("GET main.php", (_, _) => FakeKol.Redirect("https://www.kingdomofloathing.com/fight.php"))
            .On("GET fight.php", Fixture("skeleton-with-skills.html"))
            .On("POST fight.php", ActionTests.FightWon);
        await using var client = TestClient.Create(kol);
        await using var session = await client.OpenAsync("main");
        CombatContext? seen = null;

        var result = await session.Adventure.FightToEndAsync(CombatStrategies.FromDelegate(context =>
        {
            seen = context;
            return CombatAction.Attack;
        }));

        Assert.Equal(FightOutcome.Won, result.Outcome);
        Assert.NotNull(seen);
        Assert.Equal(new[] { 2023, 3004 }, seen!.AvailableSkills.Select(s => s.Id));
        Assert.Equal("Toss", seen.AvailableSkills[0].Name);
        Assert.Equal(1, seen.AvailableSkills[0].MpCost);
        Assert.Equal(4, seen.AvailableSkills[1].MpCost);
        var item = Assert.Single(seen.AvailableItems);
        Assert.Equal(new CombatOption(2, "seal tooth", null), item);
        Assert.Equal(123, seen.MonsterId);
        Assert.False(seen.IsTurtleMonster);
    }

    [Fact]
    public async Task A_rule_with_a_name_sends_the_skill_from_the_page_and_never_a_missing_one()
    {
        var kol = new FakeKol()
            .On("GET main.php", (_, _) => FakeKol.Redirect("https://www.kingdomofloathing.com/fight.php"))
            .On("GET fight.php", Fixture("skeleton-with-skills.html"))
            .On("POST fight.php", ActionTests.FightWon);
        await using var client = TestClient.Create(kol);
        await using var session = await client.OpenAsync("main");
        var rules = CombatStrategies.FromRules("if monster \"skeleton\" then skill \"Toss\"; attack", Data());

        await session.Adventure.FightToEndAsync(rules);

        Assert.Contains(kol.Requests, r => r.Method == "POST" && r.Path == "/fight.php" && r.Form.StartsWith("action=skill") && r.Form.Contains("whichskill=2023"));
    }

    [Fact]
    public async Task A_turtle_fight_is_handed_back_without_a_request()
    {
        var kol = new FakeKol()
            .On("GET main.php", (_, _) => FakeKol.Redirect("https://www.kingdomofloathing.com/fight.php"))
            .On("GET fight.php", Fixture("with-skills.html"));
        await using var client = TestClient.Create(kol);
        await using var session = await client.OpenAsync("main");

        var result = await session.Adventure.FightToEndAsync(CombatStrategies.StopOnTurtle(CombatStrategies.AttackOnly));

        Assert.Equal(FightOutcome.Stopped, result.Outcome);
        Assert.Equal(0, kol.Count("POST", "fight.php"));
    }
}
