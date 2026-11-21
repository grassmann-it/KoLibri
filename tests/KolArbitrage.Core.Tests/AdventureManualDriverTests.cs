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
        var rewardHtml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Html", "SkeletonOfCrimbosPast", "reward.html"));
        transport.EnqueueHtml(AdventureTransportOperation.Choice,
            "skeleton-of-crimbos-past", rewardHtml, new Uri("https://example.test/adventure.php"));

        var store = new InMemoryAdventureRunCheckpointStore();
        await using var gateway = new AdventureExecutionGateway(transport, store);
        var driver = new AdventureManualDriver(AccountRole.Main, gateway, "allow-token");

        var allowlist = new AdventureActionAllowlist { WaitForManualChoice = true }
            .AddAdventure("skeleton-of-crimbos-past")
            .AddEncounter("skeleton-of-crimbos-past", "skeleton");

        var start = await driver.StartAsync(new AdventureRunRequest
        {
            RunId = "manual-1",
            AdventureId = "skeleton-of-crimbos-past",
            Role = AccountRole.Main
        }, allowlist);

        Assert.False(start.IsComplete);
        Assert.Null(start.NextAction);
        Assert.Equal(new[] { "1", "2" }, start.Encounter!.AvailableChoices);
        var invalid = await driver.ExecuteAsync("manual-1", allowlist, new ChoiceStepSpec("1003", "3"));
        Assert.Equal("ActionUnavailable", invalid.StopReason);
        Assert.Single(transport.Requests);

        var step = await driver.ExecuteAsync("manual-1", allowlist,
            new ChoiceStepSpec("1003", "2"));

        Assert.True(step.IsComplete);
        var drop = Assert.Single(step.Drops);
        Assert.Equal("knucklebone", drop.ItemName);
        Assert.Equal(1, drop.Quantity);
        Assert.Null(step.NextAction);
        Assert.Equal("2", transport.Requests[1].Fields["option"]);
        Assert.Equal(2, transport.Count(AdventureTransportOperation.Choice) + transport.Count(AdventureTransportOperation.Adventure));
    }

    [Fact]
    public async Task Manual_selection_dispatches_the_selected_skill_instead_of_pending_attack()
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
                ResponseHash = "f1",
                Combat = new CombatObservation
                {
                    AvailableActions = new[] { new CombatAction(CombatActionKind.Skill, "123") }
                }
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

        var mismatched = await driver.ExecuteAsync("attack-1", allowlist, new SkillStepSpec("123", Round: 2));
        Assert.Equal("ActionUnavailable", mismatched.StopReason);
        var unavailable = await driver.ExecuteAsync("attack-1", allowlist, new AttackStepSpec());
        Assert.Equal("ActionUnavailable", unavailable.StopReason);
        Assert.Single(transport.Requests);

        var step = await driver.ExecuteAsync("attack-1", allowlist, new SkillStepSpec("123", Round: 1));
        Assert.Equal("skill:123", transport.Requests[1].Fields["action"]);
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
