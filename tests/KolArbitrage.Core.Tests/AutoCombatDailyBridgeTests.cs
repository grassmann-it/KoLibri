using KolArbitrage.Core.Accounts;
using KolArbitrage.Core.Adventure;
using KolArbitrage.Core.Daily;
using KolArbitrage.Core.Data;
using KolArbitrage.Core.Integration;
using Xunit;

namespace KolArbitrage.Core.Tests;

public sealed class AutoCombatDailyBridgeTests
{
    private static KoLStatusSnapshot Status(string? classId, params string[] effects)
        => new KoLStatusSnapshot("tester", Adventures: 100, Meat: 5_000_000, Level: 30, DateTimeOffset.UtcNow)
        {
            ClassId = classId,
            Effects = effects
        };

    private static KoLInventorySnapshot Inventory(params (string id, int qty)[] items)
        => new KoLInventorySnapshot(
            items.Select(i => new KoLInventoryItem(i.id, i.id, i.qty)).ToList(),
            DateTimeOffset.UtcNow);

    private static AccountDailyState State(string? classId, params (string id, int qty)[] items)
        => new()
        {
            Role = AccountRole.Main,
            Status = Status(classId),
            Inventory = Inventory(items),
            ObservedAt = DateTimeOffset.UtcNow
        };

    [Fact]
    public void Burn_leaf_when_reserve_is_healthy()
    {
        var bridge = new AutoCombatDailyBridge();
        var snapshot = new DailyRoutineSnapshot { CampfireLeaves = 7, CampfireReserve = 3, MainMeat = 1_000_000 };
        var steps = bridge.BuildSteps(State(null), snapshot);
        Assert.Contains(steps, s => s is ChoiceStepSpec c && c.ChoiceId == "choice-campfire");
    }

    [Fact]
    public void Skip_burn_when_reserve_is_too_low()
    {
        var bridge = new AutoCombatDailyBridge();
        var snapshot = new DailyRoutineSnapshot { CampfireLeaves = 1, CampfireReserve = 3 };
        var steps = bridge.BuildSteps(State(null), snapshot);
        Assert.DoesNotContain(steps, s => s is ChoiceStepSpec c && c.ChoiceId == "choice-campfire");
    }

    [Fact]
    public void Archaeologist_without_shovel_emits_buy_step()
    {
        var bridge = new AutoCombatDailyBridge();
        var steps = bridge.BuildSteps(
            State("archaeologist"),
            new DailyRoutineSnapshot());
        Assert.Contains(steps, s => s is ItemStepSpec i && i.ItemId == "shovel");
    }

    [Fact]
    public void Archaeologist_with_shovel_skips_buy()
    {
        var bridge = new AutoCombatDailyBridge();
        var steps = bridge.BuildSteps(
            State("archaeologist", ("shovel", 1)),
            new DailyRoutineSnapshot());
        Assert.DoesNotContain(steps, s => s is ItemStepSpec i && i.ItemId == "shovel");
    }

    [Fact]
    public void Mystic_does_not_emit_shovel_step()
    {
        var bridge = new AutoCombatDailyBridge();
        var steps = bridge.BuildSteps(State("pastamancer"), new DailyRoutineSnapshot());
        Assert.DoesNotContain(steps, s => s is ItemStepSpec i && i.ItemId == "shovel");
    }

    [Fact]
    public void Decide_for_surface_campfire_returns_steps()
    {
        var bridge = new AutoCombatDailyBridge();
        var decision = bridge.Decide("campfire",
            State(null), new DailyRoutineSnapshot { CampfireLeaves = 7, MainMeat = 1_000_000 });
        Assert.True(decision.ShouldAct);
        Assert.Equal("choice-campfire", decision.ChoiceId);
        Assert.Single(decision.Steps);
    }

    [Fact]
    public void Bridge_does_not_emit_invented_free_pull_choices()
    {
        // KoL has no "choice-lucky-surprise-egg" / "choice-internet-bacon" /
        // "choice-bacon-machine" choices — those were guesses. The Lucky
        // Surprise Egg (995), the Infinite BACON machine (9025) and BACON
        // (8763) are ordinary mall items now, so the bridge must not try to
        // claim them through a choice.
        var bridge = new AutoCombatDailyBridge();
        var steps = bridge.BuildSteps(
            State(null),
            new DailyRoutineSnapshot
            {
                AlreadyPulledItemIds = new[] { "Lucky Surprise Egg", "Internet Bacon" }
            });
        Assert.DoesNotContain(steps, s => s is ChoiceStepSpec c
            && (c.ChoiceId == "choice-lucky-surprise-egg"
                || c.ChoiceId == "choice-internet-bacon"
                || c.ChoiceId == "choice-bacon-machine"));
    }

    [Fact]
    public void Campfire_burn_asks_for_exactly_one_leaf()
    {
        // KoL's burning-leaves choice burns exactly the number of leaves we
        // ask for and would happily take the whole pile.
        var bridge = new AutoCombatDailyBridge();
        var steps = bridge.BuildSteps(
            State(null),
            new DailyRoutineSnapshot { CampfireLeaves = 37_191, CampfireReserve = 3, MainMeat = 500_000 });
        var campfire = Assert.Single(steps.OfType<ChoiceStepSpec>()
            .Where(c => c.ChoiceId == "choice-campfire"));
        Assert.Equal("1", campfire.OptionId);
    }

    [Fact]
    public void Day_shortener_comes_from_burning_leaves_once_per_day()
    {
        var bridge = new AutoCombatDailyBridge();
        var fresh = bridge.BuildSteps(State(null),
            new DailyRoutineSnapshot { CampfireLeaves = 37_345, MainMeat = 100_000 });
        var shortener = Assert.Single(fresh.OfType<ChoiceStepSpec>()
            .Where(c => c.ChoiceId == "choice-day-shortner"));
        Assert.Equal(FreePullDispatcher.DayShortenerLeafCost.ToString(), shortener.OptionId);

        var done = bridge.BuildSteps(State(null),
            new DailyRoutineSnapshot { CampfireLeaves = 37_345, MainMeat = 100_000, DayShortenerDoneToday = true });
        Assert.DoesNotContain(done, s => s is ChoiceStepSpec c && c.ChoiceId == "choice-day-shortner");

        var few = bridge.BuildSteps(State(null),
            new DailyRoutineSnapshot { CampfireLeaves = 100, MainMeat = 100_000 });
        Assert.DoesNotContain(few, s => s is ChoiceStepSpec c && c.ChoiceId == "choice-day-shortner");
    }

    [Fact]
    public void Hermit_buys_requested_clovers_capped_by_daily_stock()
    {
        var bridge = new AutoCombatDailyBridge();
        var steps = bridge.BuildSteps(State(null),
            new DailyRoutineSnapshot { HermitPermits = 1, UselessItems = 500, HermitCloversWanted = 5 });
        Assert.Equal(AutoCombatDailyBridge.HermitDailyStock,
            steps.OfType<ChoiceStepSpec>().Count(c => c.ChoiceId == "choice-clover"));

        var none = bridge.BuildSteps(State(null),
            new DailyRoutineSnapshot { HermitPermits = 0, HermitCloversWanted = 3 });
        Assert.DoesNotContain(none, s => s is ChoiceStepSpec c && c.ChoiceId == "choice-clover");
    }

    [Fact]
    public void Meme_shop_pushes_machine_once_then_buys_affordable_daily_items()
    {
        var bridge = new AutoCombatDailyBridge();
        var steps = bridge.BuildSteps(State(null), new DailyRoutineSnapshot
        {
            Bacon = 355, BaconMachineOwned = true
        }).OfType<ChoiceStepSpec>().ToArray();

        Assert.Single(steps, s => s.ChoiceId == DailyInternetShopPlanner.MachineChoiceId);
        var bought = steps.Where(s => s.ChoiceId == DailyInternetShopPlanner.BuyChoiceId)
            .Select(s => s.OptionId).ToArray();
        Assert.Equal(new[] { "viral video", "plus one", "gallon of milk", "print screen button", "daily dungeon malware" }, bought);
    }

    [Fact]
    public void Meme_shop_skips_machine_when_used_and_items_when_bacon_is_short()
    {
        var bridge = new AutoCombatDailyBridge();
        var steps = bridge.BuildSteps(State(null), new DailyRoutineSnapshot
        {
            Bacon = 120, BaconMachineOwned = true, BaconMachineUsedToday = true,
            AlreadyPulledItemIds = new[] { "viral video" }
        }).OfType<ChoiceStepSpec>().ToArray();

        Assert.DoesNotContain(steps, s => s.ChoiceId == DailyInternetShopPlanner.MachineChoiceId);
        var bought = steps.Where(s => s.ChoiceId == DailyInternetShopPlanner.BuyChoiceId)
            .Select(s => s.OptionId).ToArray();
        // 120 BACON: plus one (74) fits, then 46 left cannot pay the rest.
        Assert.Equal(new[] { "plus one" }, bought);
    }

    [Fact]
    public void Meme_shop_page_parses_balance_and_rows_and_hides_bought_items()
    {
        const string html = """
            <table>
            <tr><td><form action="shop.php"><input type=hidden name=whichrow value="1001"><b>viral video</b> 20 BACON</form></td></tr>
            <tr><td><form action="shop.php"><input type=hidden name=whichrow value="1003"><b>gallon of milk</b> 100 BACON</form></td></tr>
            </table>
            <p>You have: <b>455</b> BACON</p>
            """;
        var page = MemeShopPage.Parse(html);
        Assert.Equal(455, page.Bacon);
        Assert.Equal(1001, page.Find("viral video")!.RowId);
        Assert.Equal(1003, page.Find("Gallon of Milk")!.RowId);
        Assert.Null(page.Find("plus one"));
    }
}
