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
    public void Internet_shop_pulls_skip_already_claimed_items()
    {
        var bridge = new AutoCombatDailyBridge();
        var steps = bridge.BuildSteps(
            State(null),
            new DailyRoutineSnapshot
            {
                AlreadyPulledItemIds = new[] { "Lucky Surprise Egg", "Internet Bacon" }
            });
        Assert.Contains(steps, s => s is ChoiceStepSpec c && c.ChoiceId == "choice-bacon-machine");
        Assert.DoesNotContain(steps, s => s is ChoiceStepSpec c && c.ChoiceId == "choice-lucky-surprise-egg");
    }

    [Fact]
    public void Bacon_buy_step_when_spread_positive_and_budget_fits()
    {
        var bridge = new AutoCombatDailyBridge();
        var bacon = new BaconSnapshot
        {
            MallPricePerMachine = 400_000,
            PricegunMedianSellPrice = 600_000,
            AvailableMeat = 12_000_000,
            MinimumProfitPerUnit = 50_000,
            MaximumBuyQuantity = 10
        };
        var steps = bridge.BuildSteps(State(null), new DailyRoutineSnapshot(), bacon);
        Assert.Contains(steps, s => s is ItemStepSpec i && i.ItemId == "bacon-machine");
    }
}
