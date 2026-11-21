using Xunit;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Tests;

public sealed class HermitPlannerTests
{
    [Fact]
    public void No_permits_means_no_op()
    {
        var plan = new HermitPlanner().Plan(new HermitSnapshot
        {
            Permits = 0,
            Clovers = 3,
            ExpectedCloverSaleValue = 100_000
        });
        Assert.Equal("NoPermits", plan.Verdict);
        Assert.Empty(plan.Steps);
        Assert.False(plan.ShouldUseClover);
    }

    [Fact]
    public void Clover_is_kept_when_spread_is_not_positive()
    {
        var plan = new HermitPlanner().Plan(new HermitSnapshot
        {
            Permits = 3,
            Clovers = 5,
            ReserveAdventureValue = 80_000,
            MinimumCloverSaleValue = 100_000,
            ExpectedCloverSaleValue = 100_000
        });
        Assert.False(plan.ShouldUseClover);
        Assert.Equal("NoOp", plan.Verdict);
    }

    [Fact]
    public void Clover_is_used_when_sale_beats_reserve()
    {
        var plan = new HermitPlanner().Plan(new HermitSnapshot
        {
            Permits = 3,
            Clovers = 5,
            ReserveAdventureValue = 50_000,
            MinimumCloverSaleValue = 60_000,
            ExpectedCloverSaleValue = 200_000
        });
        Assert.True(plan.ShouldUseClover);
        Assert.Equal("UseClover", plan.Verdict);
        Assert.Single(plan.Steps);
        Assert.Equal("clover", plan.Steps[0].Kind);
    }

    [Fact]
    public void Wish_buy_refused_when_above_budget()
    {
        var plan = new HermitPlanner().Plan(new HermitSnapshot
        {
            Permits = 3,
            Clovers = 0,
            WishesAvailable = 5,
            WishBuyQuantity = 50,
            WishUnitPrice = 50_000
        });
        Assert.False(plan.ShouldBuyWishes);
        Assert.Equal("NoOp", plan.Verdict);
    }

    [Fact]
    public void Wish_buy_prepared_when_affordable()
    {
        var plan = new HermitPlanner().Plan(new HermitSnapshot
        {
            Permits = 3,
            Clovers = 0,
            WishesAvailable = 5,
            WishBuyQuantity = 2,
            WishUnitPrice = 50_000
        });
        Assert.True(plan.ShouldBuyWishes);
        Assert.Single(plan.Steps);
        Assert.Equal("wish-buy", plan.Steps[0].Kind);
    }

    [Fact]
    public void Combined_use_and_buy()
    {
        var plan = new HermitPlanner().Plan(new HermitSnapshot
        {
            Permits = 3,
            Clovers = 2,
            WishesAvailable = 3,
            ReserveAdventureValue = 10_000,
            MinimumCloverSaleValue = 20_000,
            ExpectedCloverSaleValue = 80_000,
            WishBuyQuantity = 1,
            WishUnitPrice = 100_000
        });
        Assert.Equal("UseCloverAndBuyWishes", plan.Verdict);
        Assert.Equal(2, plan.Steps.Length);
    }
}
