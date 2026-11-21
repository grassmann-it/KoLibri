using Xunit;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Tests;

public sealed class RecoveryPlannerTests
{
    [Fact]
    public void PlansHpAndMpRecoveryInStableBoundedOrderWithoutTransport()
    {
        var snapshot = new RecoverySnapshot
        {
            CurrentHp = 35,
            MaximumHp = 100,
            CurrentMp = 5,
            MaximumMp = 50,
            ItemQuantities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["red"] = 3,
                ["blue"] = 2
            }
        };
        var policy = new RecoveryPolicy
        {
            MinimumHp = 80,
            MinimumMp = 25,
            RecoveryItems = new RecoveryConsumable[]
            {
                new("red", hpRestore: 30, availableQuantity: 3),
                new("blue", mpRestore: 20, availableQuantity: 2)
            },
            MaxSteps = 8,
            MaxRecoveryActions = 8
        };

        var planner = new RecoveryPlanner(policy);
        var first = planner.Plan(snapshot, now: new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
        var second = planner.Plan(snapshot, now: first.GeneratedAt);

        Assert.Equal(RecoveryPlanStatus.RecoveryRequired, first.Status);
        Assert.False(first.CanEnterCombat);
        Assert.True(first.ProjectedCanEnterCombat);
        Assert.Equal(new[] { "red", "red", "blue" },
            first.Steps.Where(x => x.Action == RecoveryActionKind.UseItem).Select(x => x.ItemId));
        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.CanEnterCombat, second.CanEnterCombat);
        Assert.Equal(first.StopReasons, second.StopReasons);
        Assert.Equal(first.Steps.Select(x => (x.Action, x.ItemId, x.ExpectedHpDelta, x.ExpectedMpDelta)),
            second.Steps.Select(x => (x.Action, x.ItemId, x.ExpectedHpDelta, x.ExpectedMpDelta)));
        Assert.True(first.Steps.Count <= policy.MaxSteps);
        Assert.True(first.IsPreviewOnly);
    }

    [Fact]
    public void MissingEffectFamiliarAndEquipmentProduceStopAndNeverPassCombatGate()
    {
        var snapshot = new RecoverySnapshot
        {
            CurrentHp = 100,
            MaximumHp = 100,
            Effects = new[] { "other" },
            FamiliarId = "wrong",
            Equipment = new Dictionary<string, string> { ["weapon"] = "old" }
        };
        var policy = new RecoveryPolicy
        {
            RequiredEffects = new[] { "ode" },
            RequiredFamiliarId = "combat-familiar",
            RequiredEquipment = new Dictionary<string, string> { ["weapon"] = "sword" }
        };

        var plan = new RecoveryPlanner(policy).Plan(snapshot);

        Assert.False(plan.CanEnterCombat);
        Assert.True(plan.IsBlocked);
        Assert.Contains(RecoveryStopReason.MissingEffect, plan.StopReasons);
        Assert.Contains(RecoveryStopReason.MissingFamiliar, plan.StopReasons);
        Assert.Contains(RecoveryStopReason.MissingEquipment, plan.StopReasons);
        Assert.Contains(plan.Steps, x => x.Action == RecoveryActionKind.Stop);
    }

    [Fact]
    public void StaleSnapshotIsVisibleButCannotEnterCombat()
    {
        var snapshot = new RecoverySnapshot
        {
            CurrentHp = 100,
            MaximumHp = 100,
            IsStale = true
        };

        var planner = new RecoveryPlanner();
        var plan = planner.Plan(snapshot);

        Assert.False(planner.CanEnterCombat(snapshot));
        Assert.False(plan.CanEnterCombat);
        Assert.Contains(RecoveryStopReason.StaleSnapshot, plan.StopReasons);
        Assert.Equal(RecoveryPlanStatus.Blocked, plan.Status);
    }

    [Fact]
    public void MaxStepsBoundsThePlanAndAddsAStopReason()
    {
        var plan = new RecoveryPlanner(new RecoveryPolicy
        {
            MinimumHp = 90,
            RecoveryItems = new[] { new RecoveryConsumable("red", hpRestore: 1, availableQuantity: 100) },
            MaxSteps = 2,
            MaxRecoveryActions = 100
        }).Plan(new RecoverySnapshot { CurrentHp = 1, MaximumHp = 100 });

        Assert.True(plan.Steps.Count <= 2);
        Assert.Contains(plan.StopReasons, reason =>
            reason is RecoveryStopReason.PlanStepLimit or RecoveryStopReason.RecoveryActionLimit);
        Assert.False(plan.CanEnterCombat);
    }
}
