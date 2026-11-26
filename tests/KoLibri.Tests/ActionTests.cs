using Xunit;

namespace KoLibri.Tests;

public sealed class ActionTests
{
    internal const string FightRound1 = """
        <html><body><span id="monname" monsterid="123">Skeleton</span>
        <span id="playerhp">80/100</span><span id="playermp">40/50</span>
        <form action="fight.php" method="post"><input type="hidden" name="onturn" value="1">
        <button name="action" value="attack">Attack</button></form></body></html>
        """;

    internal const string FightWon = """
        <html><body><!--WINWINWIN--><p>You win the fight!</p>
        <p>You acquire an item: <b>seal tooth</b></p><p>You gain 15 Meat.</p></body></html>
        """;

    internal const string ChoicePage = """
        <html><body><b>A Fork in the Road</b>
        <form action="choice.php" method="post"><input type="hidden" name="whichchoice" value="1234">
        <input type="hidden" name="pwd" value="0123456789abcdef">
        <input type="submit" name="option" value="1"> <input type="submit" name="option" value="2"></form></body></html>
        """;

    internal const string ChoiceDone = """
        <html><body><b>Down the Road</b><p>You acquire an item: <b>seal tooth</b></p></body></html>
        """;

    [Fact]
    public async Task One_adventure_into_a_fight_then_fight_to_the_end()
    {
        var kol = new FakeKol()
            .On("POST adventure.php", (_, form) =>
            {
                Assert.Equal("snarfblat=113", form);
                return FakeKol.Redirect("https://www.kingdomofloathing.com/fight.php");
            })
            .On("GET fight.php", FightRound1)
            .On("POST fight.php", FightWon);
        await using var client = TestClient.Create(kol);
        await using var session = await client.OpenAsync("main");
        var acquired = new List<InventoryItem>();
        var ended = new List<FightResult>();
        session.Events.ItemAcquired += (_, e) => acquired.Add(e.Item);
        client.Events.FightEnded += (_, e) => ended.Add(e.Result);

        var encounter = await session.Adventure.AdventureAsync(113);
        var fight = Assert.IsType<FightEncounter>(encounter);
        Assert.Equal("Skeleton", fight.MonsterName);
        var result = await session.Adventure.FightToEndAsync(CombatStrategies.AttackOnly);

        Assert.Equal(FightOutcome.Won, result.Outcome);
        Assert.Equal(15, result.Meat);
        Assert.Equal("seal tooth", Assert.Single(result.Items).Name);
        Assert.Equal(new ItemRef(2), result.Items[0].Item);
        Assert.Contains(kol.Requests, r => r.Method == "POST" && r.Path == "/fight.php" && r.Form == "action=attack");
        Assert.Single(acquired);
        Assert.Single(ended);
    }

    [Fact]
    public async Task Run_answers_a_choice_from_the_table_and_stops_after_the_turns()
    {
        var kol = new FakeKol { StatusAdventures = 10 };
        kol.On("POST adventure.php", (_, _) => FakeKol.Redirect("https://www.kingdomofloathing.com/choice.php"))
           .On("GET choice.php", ChoicePage)
           .On("POST choice.php", (_, form) =>
           {
               Assert.Contains("option=2", form);
               Assert.Contains("whichchoice=1234", form);
               kol.StatusAdventures--;
               return FakeKol.Html(ChoiceDone);
           });
        await using var client = TestClient.Create(kol);
        await using var session = await client.OpenAsync("main");

        var result = await session.Adventure.RunAsync(new RunRequest(113, 2)
        {
            Choices = ChoiceResolvers.FromTable(new Dictionary<int, int> { [1234] = 2 })
        });

        Assert.Equal(RunStopReason.TurnsDone, result.StopReason);
        Assert.Equal(2, result.TurnsSpent);
        Assert.Equal(2, kol.Count("POST", "adventure.php"));
        Assert.Equal(2, result.Items.Single().Quantity);
    }

    [Fact]
    public async Task Run_stops_at_an_unknown_choice_and_hands_it_back()
    {
        var kol = new FakeKol()
            .On("POST adventure.php", (_, _) => FakeKol.Redirect("https://www.kingdomofloathing.com/choice.php"))
            .On("GET choice.php", ChoicePage);
        await using var client = TestClient.Create(kol);
        await using var session = await client.OpenAsync("main");

        var result = await session.Adventure.RunAsync(new RunRequest(113, 5));

        Assert.Equal(RunStopReason.NeedsChoice, result.StopReason);
        var open = Assert.IsType<ChoiceEncounter>(result.Open);
        Assert.Equal(1234, open.ChoiceId);
        Assert.Equal(new[] { 1, 2 }, open.Options.Select(o => o.Option));
        Assert.Equal(0, kol.Count("POST", "choice.php"));
    }

    [Fact]
    public async Task Run_checks_stop_conditions_before_each_turn()
    {
        var kol = new FakeKol { StatusHp = 10 };
        await using var client = TestClient.Create(kol);
        await using var session = await client.OpenAsync("main");

        var result = await session.Adventure.RunAsync(new RunRequest(113, 3) { StopWhen = new[] { StopCondition.HpBelowPercent(20) } });

        Assert.Equal(RunStopReason.Condition, result.StopReason);
        Assert.Equal(0, kol.Count("POST", "adventure.php"));
    }

    [Fact]
    public async Task Rules_strategy_and_stop_hand_the_fight_back()
    {
        var kol = new FakeKol().On("GET fight.php", FightRound1).On("GET main.php", (_, _) => FakeKol.Redirect("https://www.kingdomofloathing.com/fight.php"));
        await using var client = TestClient.Create(kol);
        await using var session = await client.OpenAsync("main");

        var rules = CombatStrategies.FromRules("if hp < 60% then run\ndefault: attack");
        var result = await session.Adventure.FightToEndAsync(CombatStrategies.FromDelegate(_ => CombatAction.Stop));

        Assert.Equal(FightOutcome.Stopped, result.Outcome);
        Assert.Empty(result.Rounds);
        Assert.Equal(0, kol.Count("POST", "fight.php"));
        var fight = new FightEncounter(new Page(new Uri("https://www.kingdomofloathing.com/fight.php"), 200, "", PageKind.Fight, DateTimeOffset.UtcNow),
            null, "Skeleton", "Skeleton", 123, 1);
        Assert.Equal(CombatActionKind.RunAway, rules.Next(new CombatContext(fight, 1, 50, 100, 10, 50, Array.Empty<FightRound>())).Kind);
        Assert.Equal(CombatActionKind.Attack, rules.Next(new CombatContext(fight, 1, 90, 100, 10, 50, Array.Empty<FightRound>())).Kind);
    }

    [Fact]
    public async Task Combat_macros_are_refused_in_0_5()
    {
        await using var client = TestClient.Create(new FakeKol());
        await using var session = await client.OpenAsync("main");
#pragma warning disable KOLIBRI0001
        await Assert.ThrowsAsync<NotSupportedException>(() => session.Adventure.FightAsync(CombatAction.FromMacro("attack; repeat")));
#pragma warning restore KOLIBRI0001
    }

    [Fact]
    public async Task Choose_checks_the_open_choice_before_sending()
    {
        var kol = new FakeKol().On("GET choice.php", ChoicePage);
        await using var client = TestClient.Create(kol);
        await using var session = await client.OpenAsync("main");

        var wrong = await Assert.ThrowsAsync<KolibriException>(() => session.Adventure.ChooseAsync(999, 1));
        Assert.Contains("Nothing was sent", wrong.Message);
        Assert.Equal(0, kol.Count("POST", "choice.php"));
    }

    [Fact]
    public async Task Consume_checks_the_organ_before_any_request()
    {
        var kol = new FakeKol { Fullness = 14, FullnessLimit = 15 };
        await using var client = TestClient.Create(kol);
        await using var session = await client.OpenAsync("main");

        // Fortune cookie: food of size 1 in the bundled data; 14/15 leaves no room for two.
        var food = client.Data.Item(61)!;
        Assert.Equal(ItemUse.Food, food.Use);
        Assert.Equal(1, food.OrganSize);
        var result = await session.Character.ConsumeAsync(food.Item, ConsumeKind.Eat, 2);

        Assert.Equal(ActionStatus.Rejected, result.Status);
        Assert.Contains("Nothing was sent", result.Rejection);
        Assert.Equal(0, kol.Count("POST", "inv_eat.php"));
    }

    [Fact]
    public void Mall_plan_respects_price_limit_daily_limit_and_meat_reserve()
    {
        var offers = new MallOffers(new ItemRef(194), "11-leaf clover", new[]
        {
            new MallListing(1, "cheap", 194, 100, 5, 2),
            new MallListing(2, "mid", 194, 200, 10, null),
            new MallListing(3, "pricey", 194, 900, 10, null),
        }, DateTimeOffset.UtcNow);

        var quote = Mall.Plan(new PurchaseOrder(194, 6, 500) { MeatReserve = 500 }, offers, meat: 1_500);

        // 2 from store 1 (daily limit), then 1000 meat budget - 200 = 800 → 4 from store 2; store 3 is over the limit.
        Assert.Equal(new[] { (1, 2), (2, 4) }, quote.Plan.Select(l => (l.StoreId, l.Quantity)));
        Assert.Equal(6, quote.PlannedQuantity);
        Assert.Equal(1000, quote.TotalCost);
        var tight = Mall.Plan(new PurchaseOrder(194, 6, 500) { MeatReserve = 1_400 }, offers, meat: 1_500);
        Assert.Equal(1, tight.PlannedQuantity);
        Assert.NotNull(tight.Shortfall);
    }
}
