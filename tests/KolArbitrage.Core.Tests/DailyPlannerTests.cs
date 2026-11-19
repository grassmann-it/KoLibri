using KolArbitrage.Core.Daily;
using Xunit;

namespace KolArbitrage.Core.Tests;

public sealed class CampfireLeavesPlannerTests
{
    [Fact]
    public void Burn_only_when_reserve_and_useful_day()
    {
        var planner = new CampfireLeavesPlanner();
        var plan = planner.Plan(new CampfireLeavesSnapshot
        {
            CampfireLeaves = 10,
            MinimumReserve = 3,
            IsUsefulDay = true,
            Knucklebones = 50
        });
        Assert.True(plan.ShouldBurn);
        Assert.Equal("BurnOnly", plan.Verdict);
        Assert.Single(plan.Steps);
        Assert.Equal("burn", plan.Steps[0].Kind);
    }

    [Fact]
    public void Refuse_burn_when_reserve_is_low()
    {
        var plan = new CampfireLeavesPlanner().Plan(new CampfireLeavesSnapshot
        {
            CampfireLeaves = 2,
            MinimumReserve = 3,
            IsUsefulDay = true
        });
        Assert.False(plan.ShouldBurn);
        Assert.Equal("NoOp", plan.Verdict);
        Assert.Empty(plan.Steps);
    }

    [Fact]
    public void Refuse_burn_when_day_is_not_useful_and_target_already_reached()
    {
        var plan = new CampfireLeavesPlanner().Plan(new CampfireLeavesSnapshot
        {
            CampfireLeaves = 8,
            MinimumReserve = 3,
            IsUsefulDay = false,
            Knucklebones = 100
        });
        Assert.False(plan.ShouldBurn);
        Assert.Equal("NoOp", plan.Verdict);
    }

    [Fact]
    public void Buy_day_shortner_when_affordable_and_useful()
    {
        var plan = new CampfireLeavesPlanner().Plan(new CampfireLeavesSnapshot
        {
            CampfireLeaves = 2,
            MinimumReserve = 3,
            DayShortnerPrice = 50_000,
            AvailableMeat = 1_000_000,
            IsUsefulDay = false,
            Knucklebones = 100
        });
        Assert.True(plan.ShouldBuyDayShortner);
        Assert.Equal("BuyOnly", plan.Verdict);
        Assert.Single(plan.Steps);
        Assert.Equal("day-shortner", plan.Steps[0].Kind);
    }

    [Fact]
    public void Refuse_shortner_when_too_expensive()
    {
        var plan = new CampfireLeavesPlanner().Plan(new CampfireLeavesSnapshot
        {
            CampfireLeaves = 5,
            MinimumReserve = 3,
            DayShortnerPrice = 1_000_000,
            AvailableMeat = 100_000,
            IsUsefulDay = false
        });
        Assert.False(plan.ShouldBuyDayShortner);
    }

    [Fact]
    public void Burn_and_buy_when_reserve_keeps_shortner_useful()
    {
        var plan = new CampfireLeavesPlanner().Plan(new CampfireLeavesSnapshot
        {
            CampfireLeaves = 4,
            MinimumReserve = 3,
            DayShortnerPrice = 50_000,
            AvailableMeat = 1_000_000,
            IsUsefulDay = true,
            Knucklebones = 50
        });
        // Reserve equals one leaf: the planner burns the spare leaf and still
        // wants a shortner to top up the reserve for tomorrow.
        Assert.True(plan.ShouldBurn);
        Assert.True(plan.ShouldBuyDayShortner);
        Assert.Equal("BurnAndBuy", plan.Verdict);
        Assert.Equal(2, plan.Steps.Length);
    }
}
