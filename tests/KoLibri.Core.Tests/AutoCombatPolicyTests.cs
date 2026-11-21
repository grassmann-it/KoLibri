using System.Text.Json;
using Xunit;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Tests;

public sealed class AutoCombatPolicyTests
{
    [Fact]
    public void OrderedRulesSelectFirstMatchAndExplainIt()
    {
        var policy = new AutoCombatPolicy
        {
            Rules = new[]
            {
                new AutoCombatRule
                {
                    Id = "heal",
                    When = new AutoCombatPredicate { MaxHpPercent = .30m },
                    Action = AutoCombatAction.Item("healing potion")
                },
                new AutoCombatRule
                {
                    Id = "hit",
                    When = new AutoCombatPredicate { Monster = "Goblin" },
                    Action = AutoCombatAction.Attack()
                }
            }
        };

        var decision = policy.Evaluate(new AutoCombatContext
        {
            Round = 1,
            Hp = 20,
            MaxHp = 100,
            Monster = "goblin"
        });

        Assert.Equal(AutoCombatActionKind.Item, decision.Action.Kind);
        Assert.Equal("heal", decision.RuleId);
        Assert.Single(decision.EvaluatedRules);
        Assert.Contains("hp%", decision.Reason);
    }

    [Fact]
    public void BudgetIsHardAbortBeforeRules()
    {
        var policy = new AutoCombatPolicy
        {
            Budget = new AutoCombatBudget { MaxRounds = 2, MaxActions = 1 },
            Rules = new[]
            {
                new AutoCombatRule { When = AutoCombatPredicate.Any, Action = AutoCombatAction.Attack() }
            }
        };

        var decision = policy.Evaluate(new AutoCombatContext { Round = 3 });

        Assert.True(decision.IsAborted);
        Assert.True(decision.BudgetExceeded);
        Assert.Equal(AutoCombatActionKind.Abort, decision.Action.Kind);
    }

    [Fact]
    public void TextDslParsesPredicatesAndActions()
    {
        var policy = AutoCombatPolicy.Parse("""
            budget maxRounds=5 maxActions=10
            rule low-health: when hp < 40% and monster == goblin -> item healing-potion
            when round >= 3 and hasEffect(poison) -> skill Fireball
            default: manual
            """);

        var low = policy.Evaluate(new AutoCombatContext
        {
            Round = 1, Hp = 30, MaxHp = 100, Monster = "Goblin"
        });
        Assert.Equal(AutoCombatActionKind.Item, low.Action.Kind);
        Assert.Equal("healing-potion", low.Action.Id);

        var skill = policy.Evaluate(new AutoCombatContext
        {
            Round = 3, Hp = 100, MaxHp = 100, Monster = "other", Effects = new[] { "poison" }
        });
        Assert.Equal(AutoCombatActionKind.Skill, skill.Action.Kind);
        Assert.Equal("Fireball", skill.Action.Id);
    }

    [Fact]
    public void JsonRoundTripPreservesPolicyAndDecision()
    {
        var policy = new AutoCombatPolicy
        {
            Budget = new AutoCombatBudget { MaxRounds = 4 },
            Rules = new[]
            {
                new AutoCombatRule
                {
                    Id = "attack-goblin",
                    When = new AutoCombatPredicate
                    {
                        MinRound = 2,
                        Monster = "goblin",
                        MissingEffects = new[] { "stunned" }
                    },
                    Action = AutoCombatAction.Attack()
                }
            }
        };

        var json = policy.ToJson();
        var parsed = AutoCombatPolicy.FromJson(json);
        var decision = parsed.Evaluate(new AutoCombatContext
        {
            Round = 2, Hp = 10, MaxHp = 10, Monster = "goblin"
        });
        Assert.Equal(AutoCombatActionKind.Attack, decision.Action.Kind);
        Assert.Equal("attack-goblin", decision.RuleId);

        var decisionJson = decision.ToJson();
        Assert.Equal(AutoCombatActionKind.Attack,
            AutoCombatDecision.FromJson(decisionJson).Action.Kind);
        Assert.NotNull(JsonDocument.Parse(json));
    }

    [Fact]
    public void NoMatchRequiresManualReview()
    {
        var policy = new AutoCombatPolicy
        {
            Rules = new[]
            {
                new AutoCombatRule
                {
                    When = new AutoCombatPredicate { Monster = "known" },
                    Action = AutoCombatAction.Attack()
                }
            }
        };
        var decision = policy.Evaluate(new AutoCombatContext { Round = 1, Monster = "unknown" });
        Assert.True(decision.IsManual);
        Assert.Equal(AutoCombatActionKind.Manual, decision.Action.Kind);
        Assert.Single(decision.EvaluatedRules);
    }
}
