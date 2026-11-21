using Xunit;
using KolArbitrage.Core.Spiel;

namespace KolArbitrage.Core.Tests;

/// <summary>
/// Replay tests for the transport-neutral adventure runner.  They deliberately
/// use the fixture transport so that combat/choice tests cannot accidentally
/// create an HTTP client or retry a mutating request.
/// </summary>
public sealed class AdventureCombatFixtureTests
{
    private static readonly string FixtureRoot = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "Adventure");

    private static AdventureRunRequest Request(string runId)
        => new()
        {
            RunId = runId,
            AdventureId = "skeleton",
            DailyLimit = new AdventureDailyLimitContract
            {
                MaxTurns = 1,
                MaxRuns = 1
            }
        };

    private static AdventureActionAllowlist FightAllowlist(
        params (string ActionId, int Round)[] actions)
    {
        var allowlist = new AdventureActionAllowlist()
            .AddAdventure("skeleton")
            .AddEncounter("skeleton", "skeleton");
        foreach (var (actionId, round) in actions)
        {
            allowlist.AddFightAction(
                "skeleton",
                actionId,
                "skeleton",
                round,
                "skeleton");
        }

        return allowlist;
    }

    private static AdventureKernelObservation FightObservation(
        int round,
        string hash,
        string? actionFixture = null)
    {
        if (actionFixture is not null)
        {
            var parser = new KoLAdventureEncounterParser();
            var encounter = parser.Parse(
                "skeleton",
                new Uri("https://example.invalid/fight.php"),
                File.ReadAllText(Path.Combine(FixtureRoot, actionFixture)));
            Assert.Equal(AdventureEncounterKind.Fight, encounter.Kind);
            Assert.Equal(round, encounter.FightRound);
        }

        return new AdventureKernelObservation
        {
            AdventureId = "skeleton",
            Kind = AdventureEncounterKind.Fight,
            EncounterId = "skeleton",
            MonsterId = "skeleton",
            FightRound = round,
            CanContinue = true,
            ResponseHash = hash
        };
    }

    private static AdventureKernelObservation RewardObservation(string hash)
        => new()
        {
            AdventureId = "skeleton",
            Kind = AdventureEncounterKind.Reward,
            CanContinue = false,
            ResponseHash = hash
        };

    [Theory]
    [InlineData("attack")]
    [InlineData("skill:1001")]
    [InlineData("item:2002")]
    public async Task ManualAttackSkillAndItemActionsAreBoundToOneApprovedPost(
        string actionId)
    {
        var transport = new AdventureExecutionFixtureTransport();
        transport.EnqueueObservation(
            AdventureTransportOperation.Adventure,
            FightObservation(1, $"manual-{actionId}-fight", "combat-round-1.html"));
        transport.EnqueueObservation(
            AdventureTransportOperation.Fight,
            RewardObservation($"manual-{actionId}-reward"));

        var allowlist = FightAllowlist((actionId, 1));
        var store = new InMemoryAdventureRunCheckpointStore();
        await using var gateway = new AdventureExecutionGateway(transport, store);

        var started = await gateway.StartAsync(
            Request($"manual-{actionId}"),
            allowlist);

        var pending = Assert.IsType<AdventureFightAction>(started.NextAction);
        Assert.Equal(actionId, pending.ActionId);
        Assert.Equal(1, pending.Round);

        var completed = await gateway.ExecutePendingAsync(
            $"manual-{actionId}",
            allowlist);

        Assert.Equal(AdventureExecutionStatus.Complete, completed.Status);
        Assert.True(completed.IsComplete);
        Assert.Equal(2, transport.RequestCount);
        var post = transport.Requests[1];
        Assert.Equal(AdventureTransportOperation.Fight, post.Operation);
        Assert.Equal(actionId, post.Fields["action"]);
        Assert.Equal("1", post.Fields["round"]);
        Assert.Equal("skeleton", post.Fields["encounter"]);
        Assert.Equal("skeleton", post.Fields["monster"]);
        Assert.Equal(pending, post.Action);
    }

    [Fact]
    public async Task ThreeRoundReplayAdvancesOneAllowlistedActionPerRoundAndCompletes()
    {
        var transport = new AdventureExecutionFixtureTransport();
        transport.EnqueueObservation(
            AdventureTransportOperation.Adventure,
            FightObservation(1, "three-round-1", "combat-round-1.html"));
        transport.EnqueueObservation(
            AdventureTransportOperation.Fight,
            FightObservation(2, "three-round-2", "combat-round-2.html"));
        transport.EnqueueObservation(
            AdventureTransportOperation.Fight,
            FightObservation(3, "three-round-3", "combat-round-3.html"));
        transport.EnqueueObservation(
            AdventureTransportOperation.Fight,
            RewardObservation("three-round-reward"));

        var allowlist = FightAllowlist(
            ("attack", 1),
            ("skill:1001", 2),
            ("item:2002", 3));
        var store = new InMemoryAdventureRunCheckpointStore();
        await using var gateway = new AdventureExecutionGateway(transport, store);

        var first = await gateway.StartAsync(Request("three-round"), allowlist);
        Assert.Equal(AdventureExecutionStatus.Advanced, first.Status);
        Assert.Equal("attack", Assert.IsType<AdventureFightAction>(first.NextAction).ActionId);

        var second = await gateway.ExecutePendingAsync("three-round", allowlist);
        Assert.Equal(AdventureExecutionStatus.Advanced, second.Status);
        Assert.Equal("skill:1001", Assert.IsType<AdventureFightAction>(second.NextAction).ActionId);

        var third = await gateway.ExecutePendingAsync("three-round", allowlist);
        Assert.Equal(AdventureExecutionStatus.Advanced, third.Status);
        Assert.Equal("item:2002", Assert.IsType<AdventureFightAction>(third.NextAction).ActionId);

        var complete = await gateway.ExecutePendingAsync("three-round", allowlist);
        Assert.Equal(AdventureExecutionStatus.Complete, complete.Status);
        Assert.True(complete.IsComplete);

        Assert.Equal(4, transport.RequestCount);
        Assert.Equal(
            new[] { AdventureTransportOperation.Adventure,
                AdventureTransportOperation.Fight,
                AdventureTransportOperation.Fight,
                AdventureTransportOperation.Fight },
            transport.Requests.Select(x => x.Operation));
        Assert.Equal(
            new[] { "attack", "skill:1001", "item:2002" },
            transport.Requests.Skip(1).Select(x => x.Fields["action"]));
        Assert.Equal(
            new[] { "1", "2", "3" },
            transport.Requests.Skip(1).Select(x => x.Fields["round"]));

        var checkpoint = await store.LoadAsync("three-round");
        Assert.NotNull(checkpoint);
        Assert.Equal(AdventureRunPhase.Complete, checkpoint!.Phase);
        Assert.Equal(3, checkpoint.Counters.FightActions);
        Assert.Equal(4, checkpoint.Counters.ResponsesObserved);
        Assert.Equal(
            new[] { "three-round-1", "three-round-2", "three-round-3", "three-round-reward" },
            checkpoint.ResponseHashes);
    }

    [Fact]
    public async Task TimeoutLeavesFightInFlightAndRefreshReconcilesWithoutReposting()
    {
        var transport = new AdventureExecutionFixtureTransport();
        transport.EnqueueObservation(
            AdventureTransportOperation.Adventure,
            FightObservation(1, "timeout-start", "combat-round-1.html"));
        transport.EnqueueFailure(
            AdventureTransportOperation.Fight,
            new TimeoutException("fixture timeout"));
        transport.EnqueueObservation(
            AdventureTransportOperation.Refresh,
            FightObservation(2, "timeout-refresh", "combat-round-2.html"));
        transport.EnqueueObservation(
            AdventureTransportOperation.Fight,
            RewardObservation("timeout-reward"));

        var allowlist = FightAllowlist(("attack", 1), ("skill:1001", 2));
        var store = new InMemoryAdventureRunCheckpointStore();
        await using var gateway = new AdventureExecutionGateway(transport, store);

        var started = await gateway.StartAsync(Request("timeout-replay"), allowlist);
        Assert.Equal(AdventureExecutionStatus.Advanced, started.Status);

        var timedOut = await gateway.ExecutePendingAsync("timeout-replay", allowlist);
        Assert.Equal(AdventureExecutionStatus.Indeterminate, timedOut.Status);
        Assert.True(timedOut.RequiresRefresh);
        Assert.Equal(1, transport.Count(AdventureTransportOperation.Fight));

        var marked = await store.LoadAsync("timeout-replay");
        Assert.NotNull(marked);
        Assert.False(string.IsNullOrWhiteSpace(marked!.InFlightRequestId));

        var reconciled = await gateway.RefreshAsync("timeout-replay", allowlist);
        Assert.Equal(AdventureExecutionStatus.Advanced, reconciled.Status);
        Assert.Equal("skill:1001", Assert.IsType<AdventureFightAction>(reconciled.NextAction).ActionId);
        Assert.Equal(1, transport.Count(AdventureTransportOperation.Fight));
        Assert.Equal(1, transport.Count(AdventureTransportOperation.Refresh));

        var completed = await gateway.ExecutePendingAsync("timeout-replay", allowlist);
        Assert.Equal(AdventureExecutionStatus.Complete, completed.Status);
        Assert.Equal(2, transport.Count(AdventureTransportOperation.Fight));
    }

    [Fact]
    public async Task UnknownEncounterFixtureStopsWithoutGuessingAnAction()
    {
        var transport = new AdventureExecutionFixtureTransport();
        transport.EnqueueHtml(
            AdventureTransportOperation.Adventure,
            "skeleton",
            File.ReadAllText(Path.Combine(FixtureRoot, "combat-unknown.html")),
            new Uri("https://example.invalid/relay.php"));
        var store = new InMemoryAdventureRunCheckpointStore();
        await using var gateway = new AdventureExecutionGateway(transport, store);

        var result = await gateway.StartAsync(
            Request("unknown-combat"),
            new AdventureActionAllowlist().AddAdventure("skeleton"));

        Assert.Equal(AdventureExecutionStatus.Aborted, result.Status);
        Assert.Equal(AdventureAbortReason.UnknownEncounter, result.AbortReason);
        Assert.Empty(result.Decision.Actions);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task DeathLikeFixtureStopsWithoutGuessingARecoveryOrFightAction()
    {
        var transport = new AdventureExecutionFixtureTransport();
        transport.EnqueueHtml(
            AdventureTransportOperation.Adventure,
            "skeleton",
            File.ReadAllText(Path.Combine(FixtureRoot, "combat-death.html")),
            new Uri("https://example.invalid/relay.php"));
        var store = new InMemoryAdventureRunCheckpointStore();
        await using var gateway = new AdventureExecutionGateway(transport, store);

        var result = await gateway.StartAsync(
            Request("death-combat"),
            new AdventureActionAllowlist().AddAdventure("skeleton"));

        Assert.Equal(AdventureExecutionStatus.Aborted, result.Status);
        Assert.Equal(AdventureAbortReason.UnknownEncounter, result.AbortReason);
        Assert.Null(result.NextAction);
        Assert.Equal(AdventureRunPhase.Aborted, (await store.LoadAsync("death-combat"))!.Phase);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task ApprovedChoiceCarriesOnlyAllowlistedOptionAndSafeHiddenField()
    {
        var transport = new AdventureExecutionFixtureTransport();
        transport.EnqueueObservation(
            AdventureTransportOperation.Adventure,
            new AdventureKernelObservation
            {
                AdventureId = "skeleton",
                Kind = AdventureEncounterKind.Choice,
                EncounterId = "skeleton",
                ChoiceId = "1234",
                AvailableChoices = new[] { "1", "2" },
                ExtraFields = new Dictionary<string, string>
                {
                    ["pwd"] = "fixture-choice-token"
                },
                CanContinue = true,
                ResponseHash = "choice-page"
            });
        transport.EnqueueObservation(
            AdventureTransportOperation.Choice,
            RewardObservation("choice-reward"));

        var allowlist = new AdventureActionAllowlist()
            .AddAdventure("skeleton")
            .AddEncounter("skeleton", "skeleton")
            .AddChoiceOption("skeleton", "1234", "2", "skeleton");
        var store = new InMemoryAdventureRunCheckpointStore();
        await using var gateway = new AdventureExecutionGateway(transport, store);

        var started = await gateway.StartAsync(Request("choice-replay"), allowlist);
        var pending = Assert.IsType<AdventureChoiceAction>(started.NextAction);
        Assert.Equal("1234", pending.ChoiceId);
        Assert.Equal("2", pending.OptionId);
        Assert.Equal("fixture-choice-token", pending.ExtraFields["pwd"]);

        var completed = await gateway.ExecutePendingAsync("choice-replay", allowlist);
        Assert.Equal(AdventureExecutionStatus.Complete, completed.Status);
        var post = Assert.Single(transport.Requests.Skip(1));
        Assert.Equal(AdventureTransportOperation.Choice, post.Operation);
        Assert.Equal("1234", post.Fields["whichchoice"]);
        Assert.Equal("2", post.Fields["option"]);
        Assert.Equal("fixture-choice-token", post.Fields["pwd"]);
    }

    [Theory]
    [InlineData("combat-round-1.html", 1)]
    [InlineData("combat-round-2.html", 2)]
    [InlineData("combat-round-3.html", 3)]
    public void CombatRoundFixturesRemainParseable(string fixture, int expectedRound)
    {
        var parser = new KoLAdventureEncounterParser();
        var encounter = parser.Parse(
            "skeleton",
            new Uri("https://example.invalid/fight.php"),
            File.ReadAllText(Path.Combine(FixtureRoot, fixture)));

        Assert.Equal(AdventureEncounterKind.Fight, encounter.Kind);
        Assert.Equal(expectedRound, encounter.FightRound);
        Assert.True(encounter.CanContinue);
        Assert.False(string.IsNullOrWhiteSpace(encounter.ResponseHash));
    }

    [Fact]
    public void ChoiceFixtureExtractsChoiceOptionsAndSafeToken()
    {
        var parser = new KoLAdventureEncounterParser();
        var encounter = parser.Parse(
            "skeleton",
            new Uri("https://example.invalid/choice.php"),
            File.ReadAllText(Path.Combine(FixtureRoot, "choice-success.html")));

        Assert.Equal(AdventureEncounterKind.Choice, encounter.Kind);
        Assert.Equal("1234", encounter.ChoiceId);
        Assert.Equal(new[] { "1", "2" }, encounter.AvailableChoices);
        Assert.Equal("fixture-choice-token", encounter.ExtraFields["pwd"]);
        Assert.True(encounter.CanContinue);
    }

    [Fact]
    public void AutoCombatPolicyReplaysThreeRoundsInDeclaredPriorityOrder()
    {
        var policy = new AutoCombatPolicy
        {
            Rules = new[]
            {
                new AutoCombatRule
                {
                    Id = "round-one-skill",
                    When = new AutoCombatPredicate { Round = 1 },
                    Action = AutoCombatAction.Skill("1001")
                },
                new AutoCombatRule
                {
                    Id = "round-two-item",
                    When = new AutoCombatPredicate { Round = 2 },
                    Action = AutoCombatAction.Item("2002")
                },
                new AutoCombatRule
                {
                    Id = "round-three-attack",
                    When = new AutoCombatPredicate { Round = 3 },
                    Action = AutoCombatAction.Attack()
                }
            },
            Budget = new AutoCombatBudget { MaxRounds = 3 }
        };

        var first = policy.Evaluate(new AutoCombatContext
        {
            Round = 1,
            Hp = 100,
            MaxHp = 100,
            Mp = 50,
            MaxMp = 50,
            Monster = "skeleton"
        });
        var second = policy.Evaluate(new AutoCombatContext
        {
            Round = 2,
            Hp = 80,
            MaxHp = 100,
            Mp = 40,
            MaxMp = 50,
            Monster = "skeleton"
        });
        var third = policy.Evaluate(new AutoCombatContext
        {
            Round = 3,
            Hp = 60,
            MaxHp = 100,
            Mp = 20,
            MaxMp = 50,
            Monster = "skeleton"
        });

        Assert.Equal(AutoCombatDecisionStatus.Action, first.Status);
        Assert.Equal(AutoCombatActionKind.Skill, first.Action.Kind);
        Assert.Equal("1001", first.Action.Id);
        Assert.Equal("round-one-skill", first.RuleId);

        Assert.Equal(AutoCombatDecisionStatus.Action, second.Status);
        Assert.Equal(AutoCombatActionKind.Item, second.Action.Kind);
        Assert.Equal("2002", second.Action.Id);
        Assert.Equal("round-two-item", second.RuleId);

        Assert.Equal(AutoCombatDecisionStatus.Action, third.Status);
        Assert.Equal(AutoCombatActionKind.Attack, third.Action.Kind);
        Assert.Equal("round-three-attack", third.RuleId);
    }

    [Fact]
    public void AutoCombatPolicyAbortsAtRoundBudgetAndFallsBackToManualForUnknownMonster()
    {
        var policy = new AutoCombatPolicy
        {
            Rules = new[]
            {
                new AutoCombatRule
                {
                    Id = "known-monster",
                    When = new AutoCombatPredicate { Monster = "skeleton" },
                    Action = AutoCombatAction.Attack()
                }
            },
            Budget = new AutoCombatBudget { MaxRounds = 3 }
        };

        var unknown = policy.Evaluate(new AutoCombatContext
        {
            Round = 1,
            Hp = 100,
            MaxHp = 100,
            Mp = 10,
            MaxMp = 10,
            Monster = "unrecognised-monster"
        });
        Assert.Equal(AutoCombatDecisionStatus.Manual, unknown.Status);
        Assert.True(unknown.IsManual);
        Assert.Contains("manual", unknown.Reason, StringComparison.OrdinalIgnoreCase);

        var overBudget = policy.Evaluate(new AutoCombatContext
        {
            Round = 4,
            Hp = 100,
            MaxHp = 100,
            Mp = 10,
            MaxMp = 10,
            Monster = "skeleton"
        });
        Assert.Equal(AutoCombatDecisionStatus.Aborted, overBudget.Status);
        Assert.True(overBudget.BudgetExceeded);
        Assert.True(overBudget.IsAborted);
    }
}
