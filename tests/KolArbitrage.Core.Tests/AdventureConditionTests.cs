using Xunit;
using KolArbitrage.Core.Spiel;

namespace KolArbitrage.Core.Tests;

public sealed class AdventureConditionTests
{
    [Fact]
    public void EvaluatesTypedItemStatAndResourceConditions()
    {
        var snapshot = new AdventureConditionSnapshot
        {
            Items = new Dictionary<string, int> { ["knucklebone"] = 97 },
            Stats = new Dictionary<string, int> { ["muscle"] = 120 },
            HitPoints = 80,
            ManaPoints = 20,
            TurnsRemaining = 5
        };

        var results = AdventureConditionEvaluator.EvaluateAll(new[]
        {
            new AdventureCondition { Kind = AdventureConditionKind.ItemCount, Key = "knucklebone", Target = 100 },
            new AdventureCondition { Kind = AdventureConditionKind.Stat, Key = "muscle", Target = 100 },
            new AdventureCondition { Kind = AdventureConditionKind.ManaPoints, Target = 25 }
        }, snapshot);

        Assert.False(results[0].IsSatisfied);
        Assert.True(results[1].IsSatisfied);
        Assert.False(results[2].IsSatisfied);
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void MissingKeysAreZeroAndAllRequiresEveryCondition()
    {
        var snapshot = new AdventureConditionSnapshot { TurnsRemaining = 2 };
        var conditions = new[]
        {
            new AdventureCondition { Kind = AdventureConditionKind.ItemCount, Key = "missing", Target = 0 },
            new AdventureCondition { Kind = AdventureConditionKind.TurnsRemaining, Target = 3 }
        };

        Assert.False(AdventureConditionEvaluator.IsSatisfied(conditions, snapshot));
        Assert.True(AdventureConditionEvaluator.Evaluate(conditions[0], snapshot).IsSatisfied);
    }
}
