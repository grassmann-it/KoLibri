using Xunit;
using KolArbitrage.Core.Spiel;

namespace KolArbitrage.Core.Tests;

/// <summary>
/// Tests for the restock planner. The planner is the gate in front of a
/// mall purchase, so its verdicts are what keeps the operator's meat and
/// the item limits safe.
/// </summary>
public sealed class RestockPlannerTests
{
    private static RestockCandidate Candidate(
        long unitPrice = 25_000, int quantity = 5, int storeId = 42, int itemId = 10881)
        => new()
        {
            StoreId = storeId,
            ItemId = itemId,
            ItemName = "11-leaf clover",
            UnitPrice = unitPrice,
            AvailableQuantity = quantity
        };

    [Fact]
    public void No_listing_yields_no_buy()
    {
        var plan = new RestockPlanner().Plan(new RestockSnapshot
        {
            CheapestListing = null,
            AvailableMeat = 1_000_000,
            TargetQuantity = 3
        });
        Assert.Equal("NoListing", plan.Verdict);
        Assert.False(plan.ShouldBuy);
    }

    [Fact]
    public void Price_above_ceiling_yields_no_buy()
    {
        var plan = new RestockPlanner().Plan(new RestockSnapshot
        {
            CheapestListing = Candidate(unitPrice: 50_000),
            AvailableMeat = 10_000_000,
            TargetQuantity = 3,
            MaximumUnitPrice = 30_000
        });
        Assert.Equal("TooExpensive", plan.Verdict);
        Assert.False(plan.ShouldBuy);
    }

    [Fact]
    public void Target_already_met_yields_no_buy()
    {
        var plan = new RestockPlanner().Plan(new RestockSnapshot
        {
            CheapestListing = Candidate(),
            AvailableMeat = 10_000_000,
            OwnedQuantity = 3,
            TargetQuantity = 3
        });
        Assert.Equal("TargetMet", plan.Verdict);
        Assert.False(plan.ShouldBuy);
    }

    [Fact]
    public void Buys_what_is_missing_when_price_and_budget_allow()
    {
        var plan = new RestockPlanner().Plan(new RestockSnapshot
        {
            CheapestListing = Candidate(unitPrice: 25_000, quantity: 5),
            AvailableMeat = 1_000_000,
            OwnedQuantity = 1,
            TargetQuantity = 3,
            MaximumUnitPrice = 30_000
        });
        Assert.Equal("Buy", plan.Verdict);
        Assert.True(plan.ShouldBuy);
        Assert.Equal(2, plan.Quantity);
        Assert.Equal(50_000, plan.EstimatedSpend);
    }

    [Fact]
    public void Quantity_is_capped_by_the_listing()
    {
        var plan = new RestockPlanner().Plan(new RestockSnapshot
        {
            CheapestListing = Candidate(unitPrice: 25_000, quantity: 1),
            AvailableMeat = 10_000_000,
            OwnedQuantity = 0,
            TargetQuantity = 3,
            MaximumUnitPrice = 30_000
        });
        Assert.True(plan.ShouldBuy);
        Assert.Equal(1, plan.Quantity);
    }

    [Fact]
    public void Meat_reserve_is_respected()
    {
        // 150_000 meat above the reserve, 25_000 per clover → 6 would fit,
        // but the target is 3, so 3 is bought.
        var plan = new RestockPlanner().Plan(new RestockSnapshot
        {
            CheapestListing = Candidate(unitPrice: 25_000, quantity: 10),
            AvailableMeat = 250_000,
            MeatReserve = 100_000,
            OwnedQuantity = 0,
            TargetQuantity = 3,
            MaximumUnitPrice = 30_000
        });
        Assert.True(plan.ShouldBuy);
        Assert.Equal(3, plan.Quantity);
        Assert.Equal(75_000, plan.EstimatedSpend);
        Assert.True(250_000 - 75_000 >= 100_000);
    }

    [Fact]
    public void Purchase_below_the_reserve_yields_no_buy()
    {
        var plan = new RestockPlanner().Plan(new RestockSnapshot
        {
            CheapestListing = Candidate(unitPrice: 25_000, quantity: 10),
            AvailableMeat = 120_000,
            MeatReserve = 100_000,
            OwnedQuantity = 0,
            TargetQuantity = 3,
            MaximumUnitPrice = 30_000
        });
        Assert.Equal("BudgetTooLow", plan.Verdict);
        Assert.False(plan.ShouldBuy);
    }

    [Fact]
    public void Leaves_cannot_be_restocked()
    {
        // The inflammable leaf (11341) is only obtained from the campfire —
        // it cannot be bought on the mall, so a restock plan for it is
        // always refused.
        var plan = new RestockPlanner().Plan(new RestockSnapshot
        {
            CheapestListing = Candidate(itemId: 11341),
            AvailableMeat = 10_000_000,
            TargetQuantity = 3,
            MaximumUnitPrice = 1_000_000
        });
        Assert.Equal("NotBuyable", plan.Verdict);
        Assert.False(plan.ShouldBuy);
    }
}
