using Xunit;
using KolArbitrage.Core.Spiel;

namespace KolArbitrage.Core.Tests;

public sealed class AutoCombatAdventureAdapterTests
{
    [Fact]
    public void ConvertsLegalPolicyAttackToAllowlistedFightAction()
    {
        var policy = new AutoCombatPolicy { DefaultAction = AutoCombatAction.Attack() };
        var observation = new CombatObservation("skeleton", 2, playerHp: 80,
            availableActions: new[] { new CombatAction(CombatActionKind.Attack, "attack") },
            outcome: CombatOutcome.Ongoing)
        { PlayerMaxHp = 100 };

        var result = AutoCombatAdventureAdapter.Evaluate(policy, observation, "skeleton", "encounter");

        Assert.True(result.Allowed);
        Assert.Equal("attack", result.Action?.ActionId);
        Assert.Equal(2, result.Action?.Round);
    }

    [Fact]
    public void BlocksPolicyActionMissingFromFightPage()
    {
        var policy = new AutoCombatPolicy { DefaultAction = AutoCombatAction.Skill("736") };
        var observation = new CombatObservation("skeleton", 1,
            availableActions: new[] { new CombatAction(CombatActionKind.Attack, "attack") },
            outcome: CombatOutcome.Ongoing);

        var result = AutoCombatAdventureAdapter.Evaluate(policy, observation, "skeleton");

        Assert.False(result.Allowed);
        Assert.Null(result.Action);
        Assert.Contains("not present", result.BlockReason);
    }

    [Fact]
    public void BlocksLegalPolicyActionWhenRecoveryGateIsUnsafe()
    {
        var policy = new AutoCombatPolicy { DefaultAction = AutoCombatAction.Attack() };
        var observation = new CombatObservation("skeleton", 1, playerHp: 80,
            availableActions: new[] { new CombatAction(CombatActionKind.Attack, "attack") },
            outcome: CombatOutcome.Ongoing)
        { PlayerMaxHp = 100 };
        var recovery = new RecoveryPlan
        {
            Status = RecoveryPlanStatus.Blocked,
            CanEnterCombat = false,
            StopReasons = new[] { RecoveryStopReason.StaleSnapshot }
        };

        var result = AutoCombatAdventureAdapter.Evaluate(
            policy, observation, "skeleton", recoveryPlan: recovery);

        Assert.False(result.Allowed);
        Assert.Null(result.Action);
        Assert.Same(recovery, result.RecoveryPlan);
        Assert.Contains("StaleSnapshot", result.BlockReason);
    }
}
