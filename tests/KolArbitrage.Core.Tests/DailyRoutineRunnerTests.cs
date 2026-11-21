using Xunit;
using KolArbitrage.Core.Spiel;

namespace KolArbitrage.Core.Tests;

public sealed class DailyRoutineRunnerTests
{
    [Fact]
    public void Empty_snapshot_with_pulls_already_claimed_returns_no_op()
    {
        var report = new DailyRoutineRunner().Run(new DailyRoutineSnapshot
        {
            AlreadyPulledItemIds = new[] { "Lucky Surprise Egg", "Internet Bacon", "Infinite Bacon Machine" }
        });
        Assert.Equal("NoOp", report.Verdict);
        Assert.Empty(report.ApprovableSteps);
        Assert.NotEmpty(report.Advisories);
    }

    [Fact]
    public void Standard_morning_routine_emits_campfire_hermit_internet_bribes()
    {
        var report = new DailyRoutineRunner().Run(new DailyRoutineSnapshot
        {
            CampfireLeaves = 7,
            CampfireReserve = 3,
            DayShortnerPrice = 50_000,
            MainMeat = 1_000_000,
            Bacon = 355,
            BaconMachineOwned = true,
            HermitPermits = 3,
            Clovers = 1,
            AdventureReserveValue = 50_000,
            CloverFloorValue = 60_000,
            ExpectedCloverSaleValue = 200_000,
            ForeignBribeBagPrice = 8_500_000,
            ForeignBribeBagStock = 5,
            MallbotMeat = 12_000_000,
            MallbotMeatReserve = 1_000_000
        });

        Assert.True(report.Counts.ApprovableSteps > 0);
        Assert.Contains(report.ApprovableSteps, s => s.Contains("campfire", StringComparison.OrdinalIgnoreCase) || s.Contains("burn", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.ApprovableSteps, s => s.Contains("clover", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.ApprovableSteps, s => s.Contains("Internet Meme Shop", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.ApprovableSteps, s => s.Contains("bag(s) of foreign bribes", StringComparison.OrdinalIgnoreCase));
        Assert.True(report.Verdict is "LightRoutine" or "StandardRoutine" or "HeavyRoutine");
    }

    [Fact]
    public void Already_pulled_internet_items_are_skipped()
    {
        var report = new DailyRoutineRunner().Run(new DailyRoutineSnapshot
        {
            MainMeat = 0,
            AlreadyPulledItemIds = new[] { "Lucky Surprise Egg", "Internet Bacon", "Infinite Bacon Machine" }
        });

        // No internet-shop steps should appear because everything is claimed.
        Assert.DoesNotContain(report.ApprovableSteps, s => s.Contains("lucky surprise egg", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(report.ApprovableSteps, s => s.Contains("internet bacon", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(report.ApprovableSteps, s => s.Contains("infinite bacon machine", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Bag_of_bribes_skipped_when_price_unknown()
    {
        var report = new DailyRoutineRunner().Run(new DailyRoutineSnapshot
        {
            MainMeat = 1_000_000,
            MallbotMeat = 5_000_000
        });
        Assert.Equal("UnknownPrice", report.ForeignBribes.Verdict);
        Assert.DoesNotContain(report.ApprovableSteps, s => s.Contains("bag(s) of foreign bribes", StringComparison.OrdinalIgnoreCase));
    }
}
