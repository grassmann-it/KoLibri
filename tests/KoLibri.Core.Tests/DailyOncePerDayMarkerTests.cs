using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;
using Xunit;

namespace KoLibri.Core.Tests;

/// <summary>Live 2026-11-26: after a full daily run a second run planned the campfire leaf and 3 Hermit clovers again.</summary>
public sealed class DailyOncePerDayMarkerTests
{
    private static AccountDailyState State() => new()
    {
        Role = AccountRole.Main,
        Status = new KoLStatusSnapshot("main", 100, 58_340, 23, DateTimeOffset.UtcNow) with { ClassId = "" },
        Inventory = new KoLInventorySnapshot(Array.Empty<KoLInventoryItem>(), DateTimeOffset.UtcNow),
        ObservedAt = DateTimeOffset.UtcNow
    };

    private static DailyRoutineSnapshot Snapshot() => new()
    {
        CampfireLeaves = 37_000, MainMeat = 58_340, HermitPermits = 2, HermitCloversWanted = 3, UselessItems = 1_700
    };

    [Fact]
    public void Fresh_day_plans_campfire_leaf_and_hermit_clovers()
    {
        var steps = new AutoCombatDailyBridge().BuildSteps(State(), Snapshot()).OfType<ChoiceStepSpec>().ToArray();
        Assert.Contains(steps, s => s.ChoiceId == "choice-campfire");
        Assert.Equal(3, steps.Count(s => s.ChoiceId == "choice-clover"));
    }

    [Fact]
    public void Done_markers_skip_campfire_leaf_and_hermit()
    {
        var snapshot = Snapshot() with { CampfireLeafDoneToday = true, HermitDoneToday = true, DayShortenerDoneToday = true };
        var steps = new AutoCombatDailyBridge().BuildSteps(State(), snapshot).OfType<ChoiceStepSpec>().ToArray();
        Assert.DoesNotContain(steps, s => s.ChoiceId is "choice-campfire" or "choice-clover" or "choice-day-shortner");
    }
}
