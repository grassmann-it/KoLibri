using System.Globalization;
using Xunit;

namespace KoLibri.Tests;

/// <summary>Recovery, upkeep and stop rules of <see cref="Adventurer.RunAsync"/> against an offline KoL.</summary>
public sealed class RunRecoveryTests
{
    private const int Potion = 55;
    private const int PolkaId = 63;

    /// <summary>A small stateful KoL: every fight costs HP and one adventure, rests heal, potions heal, a cast adds an effect.</summary>
    private sealed class Farm
    {
        public int Hp = 100, MaxHp = 100, Mp = 40, MaxMp = 50, Adventures = 50, Damage = 40;
        public string Monster = "Skeleton";
        public bool RestWorks = true;
        /// <summary>Adventures one rest costs (0 = free rests left).</summary>
        public int RestCost;
        public readonly Dictionary<int, int> Inventory = new();
        public readonly List<(string Name, int Turns)> Effects = new();
        public readonly FakeKol Kol = new();

        public Farm()
        {
            Kol.On("GET api.php?what=status", (_, _) => FakeKol.Html(StatusJson()));
            Kol.On("GET api.php?what=inventory", (_, _) => FakeKol.Html(
                "{" + string.Join(",", Inventory.Where(i => i.Value > 0).Select(i => $"\"{i.Key}\":{i.Value}")) + "}"));
            Kol.On("POST adventure.php", (_, _) => FakeKol.Redirect("https://www.kingdomofloathing.com/fight.php"));
            Kol.On("GET fight.php", (_, _) => FakeKol.Html(ActionTests.FightRound1.Replace("Skeleton", Monster)));
            Kol.On("POST fight.php", (_, _) =>
            {
                Adventures--;
                Hp = Math.Max(1, Hp - Damage);
                for (var i = 0; i < Effects.Count; i++) Effects[i] = (Effects[i].Name, Math.Max(0, Effects[i].Turns - 1));
                Inventory[2] = Inventory.GetValueOrDefault(2) + 1;
                return FakeKol.Html(ActionTests.FightWon);
            });
            Kol.On("GET campground.php", (_, _) => FakeKol.Html(RestCost == 0
                ? "<a href='campground.php?action=rest'><img src='/otherimages/campground/rest4tp_free.gif'></a>"
                : "<a href='campground.php?action=rest'><img src='/otherimages/campground/rest4tp.gif'></a>"));
            Kol.On("POST campground.php", (_, _) =>
            {
                if (RestWorks) { Hp = MaxHp; Mp = MaxMp; Adventures -= RestCost; return FakeKol.Html("<html><body>You rest and feel much better.</body></html>"); }
                return FakeKol.Html("<html><body>Nothing happens.</body></html>");
            });
            Kol.On("POST inv_use.php", (_, form) =>
            {
                Inventory[Potion] = Inventory.GetValueOrDefault(Potion) - 1;
                Hp = Math.Min(MaxHp, Hp + 60);
                return FakeKol.Html("<html><body>You feel better.</body></html>");
            });
            Kol.On("POST runskillz.php", (_, _) =>
            {
                Mp -= 5;
                Effects.Add(("Polka of Plenty", 20));
                return FakeKol.Html("<html><body>You acquire an effect: Polka of Plenty</body></html>");
            });
        }

        private string StatusJson()
        {
            var effects = string.Join(",", Effects.Select((e, i) => $"\"e{i}\":[\"{e.Name}\",{e.Turns}]"));
            return string.Create(CultureInfo.InvariantCulture,
                $$"""{"name":"Tester","playerid":"1001","level":7,"meat":10000,"adventures":{{Adventures}},"hp":{{Hp}},"maxhp":{{MaxHp}},"mp":{{Mp}},"maxmp":{{MaxMp}},"full":0,"fullnessmax":15,"drunk":0,"drunkmax":14,"spleen":0,"spleenmax":15,"pwd":"0123456789abcdef","familiar":"0","equipment":{"hat":"3","weapon":"0"},"effects":{ {{effects}} } }""");
        }

        public async Task<RunResult> RunAsync(RunRequest request)
        {
            await using var client = TestClient.Create(Kol);
            await using var session = await client.OpenAsync("main");
            return await session.Adventure.RunAsync(request);
        }

        public int InventoryRequests => Kol.Requests.Count(r => r.Method == "GET" && r.Path.Contains("what=inventory"));
    }

    [Fact]
    public async Task Item_heals_before_turn_three_instead_of_stopping()
    {
        var farm = new Farm();
        farm.Inventory[Potion] = 3;

        var result = await farm.RunAsync(new RunRequest(113, 3)
        {
            Recovery = RecoveryPolicies.Simple(50, 0, new[] { RecoveryStep.UseItem(Potion) }, Array.Empty<RecoveryStep>()),
            StopWhen = new[] { StopCondition.HpBelowPercent(30) }
        });

        Assert.Equal(RunStopReason.TurnsDone, result.StopReason);
        Assert.Equal(3, result.TurnsSpent);
        Assert.Equal(1, farm.Kol.Count("POST", "inv_use.php"));
        Assert.Single(result.RecoverySteps);
        var requests = farm.Kol.Requests.Where(r => r.Path is "/adventure.php" or "/inv_use.php").Select(r => r.Path).ToArray();
        Assert.Equal(new[] { "/adventure.php", "/adventure.php", "/inv_use.php", "/adventure.php" }, requests);
    }

    [Fact]
    public async Task Rest_heals_between_fights()
    {
        var farm = new Farm { Hp = 40 };

        var result = await farm.RunAsync(new RunRequest(113, 3)
        {
            Recovery = RecoveryPolicies.Simple(50, 0, new[] { RecoveryStep.Rest }, Array.Empty<RecoveryStep>()),
            StopWhen = new[] { StopCondition.HpBelowPercent(30) }
        });

        Assert.Equal(RunStopReason.TurnsDone, result.StopReason);
        Assert.Equal(3, result.TurnsSpent);
        Assert.True(farm.Kol.Count("POST", "campground.php") >= 1);
    }

    [Fact]
    public async Task Paid_rests_are_reported_and_bounded_by_max_recovery_adventures()
    {
        // A fresh session with zero budget must stop before the first paid rest.
        var farm = new Farm { Hp = 40, RestCost = 1 };
        var policy = RecoveryPolicies.Simple(50, 0, new[] { RecoveryStep.Rest }, Array.Empty<RecoveryStep>());

        var result = await farm.RunAsync(new RunRequest(113, 5) { Recovery = policy });

        Assert.Equal(RunStopReason.Recovery, result.StopReason);
        Assert.Contains("MaxRecoveryAdventures budget is used up", result.Detail);
        Assert.Equal(0, result.RecoveryAdventuresSpent);
        Assert.Equal(0, result.TurnsSpent);
        Assert.Equal(0, farm.Kol.Count("POST", "campground.php"));
        Assert.Equal(0, farm.Kol.Count("POST", "adventure.php"));
        Assert.Equal(50, farm.Adventures);

        // With a budget of 2, two paid rests are allowed; the third is refused before it is sent.
        var budgeted = new Farm { Hp = 40, RestCost = 1, Damage = 70 };
        var second = await budgeted.RunAsync(new RunRequest(113, 5) { Recovery = policy, MaxRecoveryAdventures = 2 });

        Assert.Equal(RunStopReason.Recovery, second.StopReason);
        Assert.Equal(2, second.RecoveryAdventuresSpent);
        Assert.Equal(2, budgeted.Kol.Count("POST", "campground.php"));
        Assert.Equal(50 - 2 - second.TurnsSpent, budgeted.Adventures);
    }

    [Fact]
    public async Task Rest_without_hp_gain_stops_with_recovery_after_one_try()
    {
        var farm = new Farm { Hp = 20, RestWorks = false };

        var result = await farm.RunAsync(new RunRequest(113, 5)
        {
            Recovery = RecoveryPolicies.Simple(50, 0, new[] { RecoveryStep.Rest }, Array.Empty<RecoveryStep>())
        });

        Assert.Equal(RunStopReason.Recovery, result.StopReason);
        Assert.Equal(1, farm.Kol.Count("POST", "campground.php"));
        Assert.Equal(0, farm.Kol.Count("POST", "adventure.php"));
        Assert.Equal(0, result.TurnsSpent);
    }

    [Fact]
    public async Task Missing_item_falls_through_to_the_next_step()
    {
        var farm = new Farm { Hp = 30 };

        var result = await farm.RunAsync(new RunRequest(113, 1)
        {
            Recovery = RecoveryPolicies.Simple(50, 0, new[] { RecoveryStep.UseItem(Potion), RecoveryStep.Rest }, Array.Empty<RecoveryStep>())
        });

        Assert.Equal(RunStopReason.TurnsDone, result.StopReason);
        Assert.Equal(0, farm.Kol.Count("POST", "inv_use.php"));
        Assert.Equal(1, farm.Kol.Count("POST", "campground.php"));
    }

    [Fact]
    public async Task No_usable_step_stops_with_recovery_without_any_request()
    {
        var farm = new Farm { Hp = 30 };

        var result = await farm.RunAsync(new RunRequest(113, 1)
        {
            Recovery = RecoveryPolicies.Simple(50, 0, new[] { RecoveryStep.UseItem(Potion) }, Array.Empty<RecoveryStep>())
        });

        Assert.Equal(RunStopReason.Recovery, result.StopReason);
        Assert.Equal(0, farm.Kol.Count("POST", "inv_use.php"));
        Assert.Equal(0, farm.Kol.Count("POST", "adventure.php"));
    }

    [Fact]
    public async Task Beaten_up_stops_instead_of_healing()
    {
        var farm = new Farm { Hp = 5 };
        farm.Effects.Add(("Beaten Up", 3));

        var result = await farm.RunAsync(new RunRequest(113, 2)
        {
            Recovery = RecoveryPolicies.Simple(50, 0, new[] { RecoveryStep.Rest }, Array.Empty<RecoveryStep>())
        });

        Assert.Equal(RunStopReason.Recovery, result.StopReason);
        Assert.Contains("Beaten Up", result.Detail);
        Assert.Equal(0, farm.Kol.Count("POST", "campground.php"));
    }

    [Fact]
    public async Task Upkeep_casts_exactly_once_while_the_effect_lasts()
    {
        var farm = new Farm();

        var result = await farm.RunAsync(new RunRequest(113, 3)
        {
            Recovery = RecoveryPolicies.Upkeep(PolkaId, 5, RecoveryStep.Cast(6016))
        });

        Assert.Equal(RunStopReason.TurnsDone, result.StopReason);
        Assert.Equal(1, farm.Kol.Count("POST", "runskillz.php"));
        Assert.Equal(3, result.TurnsSpent);
    }

    [Fact]
    public async Task Upkeep_does_not_cast_when_the_effect_is_long_enough()
    {
        var farm = new Farm();
        farm.Effects.Add(("Polka of Plenty", 30));

        var result = await farm.RunAsync(new RunRequest(113, 2)
        {
            Recovery = RecoveryPolicies.Upkeep(PolkaId, 5, RecoveryStep.Cast(6016))
        });

        Assert.Equal(RunStopReason.TurnsDone, result.StopReason);
        Assert.Equal(0, farm.Kol.Count("POST", "runskillz.php"));
    }

    [Fact]
    public async Task Combined_policy_heals_and_keeps_the_buff()
    {
        var farm = new Farm { Hp = 30 };

        var result = await farm.RunAsync(new RunRequest(113, 2)
        {
            Recovery = RecoveryPolicies.Combine(
                RecoveryPolicies.Simple(50, 0, new[] { RecoveryStep.Rest }, Array.Empty<RecoveryStep>()),
                RecoveryPolicies.Upkeep(PolkaId, 5, RecoveryStep.Cast(6016)))
        });

        Assert.Equal(RunStopReason.TurnsDone, result.StopReason);
        Assert.Equal(1, farm.Kol.Count("POST", "runskillz.php"));
        Assert.True(farm.Kol.Count("POST", "campground.php") >= 1);
    }

    [Fact]
    public async Task Item_count_uses_one_inventory_read_and_the_loot_of_the_run()
    {
        var farm = new Farm();
        farm.Inventory[2] = 1; // one seal tooth at the start; each fight drops another

        var result = await farm.RunAsync(new RunRequest(113, 10)
        {
            StopWhen = new[] { StopCondition.ItemCountAtLeast(2, 3) }
        });

        Assert.Equal(RunStopReason.Condition, result.StopReason);
        Assert.Equal(2, result.TurnsSpent);
        Assert.Equal(1, farm.InventoryRequests);
    }

    [Fact]
    public async Task Monster_seen_stops_after_the_fight()
    {
        var farm = new Farm();

        var result = await farm.RunAsync(new RunRequest(113, 5) { StopWhen = new[] { StopCondition.MonsterSeen("skeleton") } });

        Assert.Equal(RunStopReason.Condition, result.StopReason);
        Assert.Equal(1, result.TurnsSpent);
        Assert.Equal(1, farm.Kol.Count("POST", "fight.php"));
        Assert.Contains("Skeleton", result.Detail);
    }

    [Fact]
    public async Task Other_monsters_do_not_stop_the_run()
    {
        var farm = new Farm();

        var result = await farm.RunAsync(new RunRequest(113, 2) { StopWhen = new[] { StopCondition.MonsterSeen("Dragon") } });

        Assert.Equal(RunStopReason.TurnsDone, result.StopReason);
    }

    [Fact]
    public async Task Turtle_encounter_stops_after_the_turn()
    {
        var farm = new Farm { Monster = "Snapping Turtle" };

        var result = await farm.RunAsync(new RunRequest(113, 5) { StopWhen = new[] { StopCondition.TurtleEncounter() } });

        Assert.Equal(RunStopReason.Condition, result.StopReason);
        Assert.Equal(1, result.TurnsSpent);
        Assert.Contains("Turtle", result.Detail);
    }

    [Fact]
    public async Task Encounter_title_matches_a_choice_title()
    {
        var kol = new FakeKol { StatusAdventures = 10 };
        kol.On("POST adventure.php", (_, _) => FakeKol.Redirect("https://www.kingdomofloathing.com/choice.php"))
           .On("GET choice.php", ActionTests.ChoicePage)
           .On("POST choice.php", (_, _) => { kol.StatusAdventures--; return FakeKol.Html(ActionTests.ChoiceDone); });
        await using var client = TestClient.Create(kol);
        await using var session = await client.OpenAsync("main");

        var result = await session.Adventure.RunAsync(new RunRequest(113, 5)
        {
            Choices = ChoiceResolvers.FromTable(new Dictionary<int, int> { [1234] = 2 }),
            StopWhen = new[] { StopCondition.EncounterTitle("fork in the") }
        });

        Assert.Equal(RunStopReason.Condition, result.StopReason);
        Assert.Equal(1, result.TurnsSpent);
        Assert.ThrowsAny<ArgumentException>(() => StopCondition.EncounterTitle("("));
    }

    [Fact]
    public async Task Recovery_is_asked_before_turns_only_not_inside_a_chain()
    {
        // Each turn is a chain of two choices (the answer to the first is the second) and then a reward page.
        var kol = new FakeKol { StatusAdventures = 10 };
        var answers = 0;
        kol.On("POST adventure.php", (_, _) => FakeKol.Redirect("https://www.kingdomofloathing.com/choice.php"))
           .On("GET choice.php", (_, _) => FakeKol.Html(answers % 2 == 1 ? ActionTests.ChoicePage.Replace("1234", "1235") : ActionTests.ChoicePage))
           .On("POST choice.php", (_, _) =>
           {
               answers++;
               if (answers % 2 == 1) return FakeKol.Html(ActionTests.ChoicePage.Replace("1234", "1235"));
               kol.StatusAdventures--;
               return FakeKol.Html(ActionTests.ChoiceDone);
           });
        var policy = new CountingPolicy();
        await using var client = TestClient.Create(kol);
        await using var session = await client.OpenAsync("main");

        var result = await session.Adventure.RunAsync(new RunRequest(113, 2)
        {
            Recovery = policy,
            Choices = ChoiceResolvers.FromTable(new Dictionary<int, int> { [1234] = 2, [1235] = 2 })
        });

        Assert.True(result.StopReason == RunStopReason.TurnsDone, result.Detail);
        Assert.Equal(4, kol.Count("POST", "choice.php"));
        Assert.Equal(2, policy.Calls); // one per turn start, none between the choices of a chain
    }

    private sealed class CountingPolicy : IRecoveryPolicy
    {
        public int Calls;
        public RecoveryStep Next(RecoveryContext context) { Calls++; return RecoveryStep.None; }
    }
}
