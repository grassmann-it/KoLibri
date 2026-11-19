using KolArbitrage.Core.Adventure;
using KolArbitrage.Core.Accounts;
using KolArbitrage.Core.Data;
using Xunit;

namespace KolArbitrage.Core.Tests;

public sealed class AdventureManualDriverTests
{
    [Fact]
    public async Task Manual_driver_walks_skeleton_choice_chain_via_mock_gateway()
    {
        var transport = new AdventureExecutionFixtureTransport();
        transport.EnqueueObservation(AdventureTransportOperation.Adventure,
            new AdventureKernelObservation
            {
                AdventureId = "skeleton-of-crimbos-past",
                Kind = AdventureEncounterKind.Choice,
                EncounterId = "skeleton",
                ChoiceId = "1003",
                AvailableChoices = new[] { "1", "2" },
                CanContinue = true,
                ResponseHash = "c1"
            });
        transport.EnqueueObservation(AdventureTransportOperation.Choice,
            new AdventureKernelObservation
            {
                AdventureId = "skeleton-of-crimbos-past",
                Kind = AdventureEncounterKind.Reward,
                CanContinue = false,
                ResponseHash = "reward"
            });

        var store = new InMemoryAdventureRunCheckpointStore();
        await using var gateway = new AdventureExecutionGateway(transport, store);
        var driver = new AdventureManualDriver(AccountRole.Main, gateway, "allow-token");

        var allowlist = new AdventureActionAllowlist()
            .AddAdventure("skeleton-of-crimbos-past")
            .AddEncounter("skeleton-of-crimbos-past", "skeleton")
            .AddChoiceOption("skeleton-of-crimbos-past", "1003", "2");

        var start = await driver.StartAsync(new AdventureRunRequest
        {
            RunId = "manual-1",
            AdventureId = "skeleton-of-crimbos-past",
            Role = AccountRole.Main
        }, allowlist);

        Assert.False(start.IsComplete);
        Assert.IsType<AdventureChoiceAction>(start.NextAction);

        var step = await driver.ExecuteAsync("manual-1", allowlist,
            new ChoiceStepSpec("1003", "2"));

        Assert.True(step.IsComplete);
        Assert.Null(step.NextAction);
        Assert.Equal(2, transport.Count(AdventureTransportOperation.Choice) + transport.Count(AdventureTransportOperation.Adventure));
    }

    [Fact]
    public async Task Attack_step_creates_fight_action_via_allowlist()
    {
        var transport = new AdventureExecutionFixtureTransport();
        transport.EnqueueObservation(AdventureTransportOperation.Adventure,
            new AdventureKernelObservation
            {
                AdventureId = "skeleton",
                Kind = AdventureEncounterKind.Fight,
                EncounterId = "skeleton",
                MonsterId = "skeleton",
                FightRound = 1,
                CanContinue = true,
                ResponseHash = "f1"
            });
        transport.EnqueueObservation(AdventureTransportOperation.Fight,
            new AdventureKernelObservation
            {
                AdventureId = "skeleton",
                Kind = AdventureEncounterKind.Reward,
                CanContinue = false,
                ResponseHash = "reward"
            });

        var store = new InMemoryAdventureRunCheckpointStore();
        await using var gateway = new AdventureExecutionGateway(transport, store);
        var driver = new AdventureManualDriver(AccountRole.Main, gateway, "allow-token");

        var allowlist = new AdventureActionAllowlist()
            .AddAdventure("skeleton")
            .AddEncounter("skeleton", "skeleton")
            .AddFightAction("skeleton", "attack", "skeleton");
        var start = await driver.StartAsync(new AdventureRunRequest
        {
            RunId = "attack-1",
            AdventureId = "skeleton"
        }, allowlist);

        Assert.IsType<AdventureFightAction>(start.NextAction);

        var step = await driver.ExecuteAsync("attack-1", allowlist, new AttackStepSpec());
        Assert.True(step.IsComplete);
        Assert.Null(step.NextAction);
    }

    [Fact]
    public void AutoCombat_skill_action_maps_to_skill_step_spec()
    {
        var action = new AutoCombatAction(AutoCombatActionKind.Skill, "sauceror-paste", 1, "Sauceror Paste");
        var spec = AutoCombatStepSpecMapper.TryMap("skeleton", action);
        Assert.IsType<SkillStepSpec>(spec);
        Assert.Equal("sauceror-paste", ((SkillStepSpec)spec!).SkillId);
    }

    [Fact]
    public void AutoCombat_run_action_maps_to_run_step_spec()
    {
        var action = new AutoCombatAction(AutoCombatActionKind.Run, null, 1, "Run away");
        var spec = AutoCombatStepSpecMapper.TryMap("skeleton", action);
        Assert.IsType<RunStepSpec>(spec);
    }

    [Fact]
    public void AutoCombat_unknown_kind_returns_null()
    {
        var action = new AutoCombatAction(AutoCombatActionKind.Manual, null, 1, "manual");
        var spec = AutoCombatStepSpecMapper.TryMap("skeleton", action);
        Assert.Null(spec);
    }
}
