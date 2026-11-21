using Xunit;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Tests;

public sealed class CombatObservationFixtureTests
{
    private static readonly string FixtureRoot = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "Adventure");

    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(FixtureRoot, name));

    [Fact]
    public void FightFixtureParsesMonsterResourcesEffectsAndManualControls()
    {
        var parser = new KoLCombatObservationParser();
        var observation = parser.Parse(
            Fixture("combat-round-1.html"),
            new Uri("https://example.invalid/fight.php"));

        Assert.Equal("Skeleton", observation.MonsterName);
        Assert.Equal("123", observation.MonsterId);
        Assert.Equal(1, observation.Round);
        Assert.Equal(80, observation.PlayerHp);
        Assert.Equal(100, observation.PlayerMaxHp);
        Assert.Equal(40, observation.PlayerMp);
        Assert.Equal(50, observation.PlayerMaxMp);
        Assert.Equal(90, observation.EnemyHp);
        Assert.Equal(120, observation.EnemyMaxHp);
        Assert.Equal("Ghost", observation.Familiar);
        Assert.Equal(CombatOutcome.Ongoing, observation.Outcome);
        Assert.Equal("/fight.php", observation.SourcePath);
        Assert.False(string.IsNullOrWhiteSpace(observation.ResponseHash));
        Assert.Contains(observation.Effects, effect =>
            effect.Name.Contains("Poison", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(observation.AvailableActions, action => action.Kind == CombatActionKind.Attack);
        Assert.Contains(observation.AvailableActions, action => action.Kind == CombatActionKind.Skill);
        Assert.Contains(observation.AvailableActions, action => action.Kind == CombatActionKind.Item);
    }

    [Theory]
    [InlineData("combat-round-1.html", CombatOutcome.Ongoing)]
    [InlineData("combat-round-2.html", CombatOutcome.Ongoing)]
    [InlineData("combat-round-3.html", CombatOutcome.Ongoing)]
    [InlineData("combat-reward.html", CombatOutcome.Victory)]
    [InlineData("combat-death.html", CombatOutcome.Defeat)]
    [InlineData("combat-unknown.html", CombatOutcome.Unknown)]
    public void ReplayFixturesClassifyOutcomeWithoutSubmitting(string fixture, CombatOutcome expected)
    {
        var observation = new KoLCombatObservationParser().Parse(
            Fixture(fixture),
            new Uri("https://example.invalid/fight.php"));

        Assert.Equal(expected, observation.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(observation.ResponseHash));
        Assert.Equal(observation.Outcome is CombatOutcome.Ongoing, observation.IsInProgress);
        Assert.Equal(
            observation.Outcome is CombatOutcome.Victory
                or CombatOutcome.Defeat
                or CombatOutcome.Fled,
            observation.IsTerminal);
    }

    [Fact]
    public void ManualActionContractsBindAttackSkillItemAndRunToDistinctFingerprints()
    {
        var actions = new ManualCombatAction[]
        {
            ManualCombatAction.Attack(),
            ManualCombatAction.Skill("1001"),
            ManualCombatAction.Item("2002"),
            ManualCombatAction.Run()
        };

        Assert.Equal(ManualCombatActionKind.Attack, actions[0].Kind);
        Assert.Equal(ManualCombatActionKind.Skill, actions[1].Kind);
        Assert.Equal("1001", actions[1].SkillId);
        Assert.Equal(ManualCombatActionKind.Item, actions[2].Kind);
        Assert.Equal("2002", actions[2].ItemId);
        Assert.Equal(ManualCombatActionKind.Run, actions[3].Kind);
        Assert.Equal(actions.Length, actions.Select(x => x.Fingerprint("combat-1", 1, "hash")).Distinct().Count());
        Assert.All(actions, action => action.Validate());
    }
}
