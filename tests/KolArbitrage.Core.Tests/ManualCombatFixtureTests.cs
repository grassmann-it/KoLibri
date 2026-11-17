using KolArbitrage.Core.Adventure;
using Xunit;

namespace KolArbitrage.Core.Tests;

public sealed class ManualCombatFixtureTests
{
    private static ManualCombatObservation Active(
        string hash,
        int round = 1,
        params ManualCombatAction[] actions)
        => new()
        {
            CombatId = "combat-1",
            AdventureId = "skeleton",
            MonsterId = "123",
            MonsterName = "Skeleton",
            Round = round,
            PlayerHp = 80,
            PlayerMaxHp = 100,
            PlayerMp = 40,
            PlayerMaxMp = 50,
            EnemyHp = 90,
            EnemyMaxHp = 120,
            AvailableActions = actions,
            Outcome = ManualCombatOutcome.Active,
            ResponseHash = hash
        };

    private static ManualCombatObservation Won(string hash, int round = 2)
        => new()
        {
            CombatId = "combat-1",
            AdventureId = "skeleton",
            MonsterId = "123",
            MonsterName = "Skeleton",
            Round = round,
            Outcome = ManualCombatOutcome.Won,
            ResponseHash = hash
        };

    [Fact]
    public async Task BoundedRunnerRefreshesBetweenApprovedRoundsAndStopsOnVictory()
    {
        var transport = new ManualCombatFixtureTransport();
        transport.EnqueueObservation(
            ManualCombatTransportOperation.Inspect,
            Active("hash-1", actions: LegalActions()));
        transport.EnqueueObservation(
            ManualCombatTransportOperation.Inspect,
            Active("hash-1", actions: LegalActions()));
        transport.EnqueueObservation(
            ManualCombatTransportOperation.Submit,
            Active("hash-2", round: 2, actions: LegalActions()));
        transport.EnqueueObservation(
            ManualCombatTransportOperation.Refresh,
            Won("hash-3", round: 3));
        await using var gateway = new ManualCombatGateway(transport);
        var runner = new BoundedManualCombatRunner(gateway);

        var result = await runner.RunAsync(
            "combat-1",
            (_, _) => ValueTask.FromResult<ManualCombatAction?>(ManualCombatAction.Attack()),
            options: new BoundedManualCombatRunnerOptions { MaxActions = 3 });

        Assert.Equal(BoundedManualCombatStopReason.Complete, result.StopReason);
        Assert.Single(result.Steps);
        Assert.Equal(ManualCombatStatus.Committed, result.Steps[0].Status);
        Assert.Equal(2, transport.Count(ManualCombatTransportOperation.Inspect));
        Assert.Equal(1, transport.Count(ManualCombatTransportOperation.Submit));
        Assert.Equal(1, transport.Count(ManualCombatTransportOperation.Refresh));
    }

    [Fact]
    public async Task BoundedRunnerStopsOnRecoveryGateBeforePreviewOrPost()
    {
        var transport = new ManualCombatFixtureTransport();
        transport.EnqueueObservation(
            ManualCombatTransportOperation.Inspect,
            Active("recovery-hash", actions: LegalActions()));
        await using var gateway = new ManualCombatGateway(transport);
        var runner = new BoundedManualCombatRunner(gateway);
        var recovery = new RecoveryPlan
        {
            Status = RecoveryPlanStatus.Blocked,
            CanEnterCombat = false,
            StopReasons = new[] { RecoveryStopReason.StaleSnapshot }
        };

        var result = await runner.RunAsync(
            "combat-1",
            (_, _) => ValueTask.FromResult<ManualCombatAction?>(ManualCombatAction.Attack()),
            recoveryProvider: (_, _) => ValueTask.FromResult<RecoveryPlan?>(recovery));

        Assert.Equal(BoundedManualCombatStopReason.RecoveryBlocked, result.StopReason);
        Assert.Same(recovery, result.RecoveryPlan);
        Assert.Equal(1, transport.Count(ManualCombatTransportOperation.Inspect));
        Assert.Equal(0, transport.Count(ManualCombatTransportOperation.Submit));
    }

    [Theory]
    [MemberData(nameof(ManualActions))]
    public async Task ManualActionPreviewAndCommitSendExactlyOneTypedPost(ManualCombatAction action)
    {
        var transport = new ManualCombatFixtureTransport();
        transport.EnqueueObservation(
            ManualCombatTransportOperation.Inspect,
            Active("hash-1", actions: LegalActions()));
        transport.EnqueueObservation(
            ManualCombatTransportOperation.Submit,
            Won("hash-2"));
        var store = new InMemoryManualCombatCheckpointStore();
        await using var gateway = new ManualCombatGateway(
            transport,
            checkpoints: store);

        var preview = await gateway.PreviewAsync(new ManualCombatRequest(
            "combat-1",
            action,
            expectedRound: 1,
            expectedResponseHash: "hash-1",
            adventureId: "skeleton"));

        Assert.True(preview.IsReady);
        Assert.Equal(action.Kind, preview.Request.Action.Kind);
        Assert.Equal("hash-1", preview.Request.ExpectedResponseHash);

        var result = await gateway.ExecuteAsync(preview);

        Assert.Equal(ManualCombatStatus.Complete, result.Status);
        Assert.True(result.Committed);
        Assert.Equal(1, transport.Count(ManualCombatTransportOperation.Inspect));
        Assert.Equal(1, transport.Count(ManualCombatTransportOperation.Submit));
        Assert.Equal(0, transport.Count(ManualCombatTransportOperation.Refresh));

        var post = Assert.Single(transport.Requests, x => x.IsMutating);
        Assert.Equal(action.Kind, post.Action!.Kind);
        Assert.Equal(action.Id, post.Action.Id);
        Assert.Equal("combat-1", post.Fields["combat"]);
        Assert.Equal("1", post.Fields["round"]);
        Assert.Equal("hash-1", post.Fields["response_hash"]);
        Assert.Equal(action.Kind.ToString().ToLowerInvariant(), post.Fields["action"]);
        if (action.Kind == ManualCombatActionKind.Skill)
            Assert.Equal(action.Id, post.Fields["skill"]);
        if (action.Kind == ManualCombatActionKind.Item)
        {
            Assert.Equal(action.Id, post.Fields["item"]);
            Assert.Equal(action.Quantity.ToString(), post.Fields["quantity"]);
        }
    }

    [Fact]
    public async Task TimeoutMarksInFlightAndRefreshClearsMarkerWithoutRetryingPost()
    {
        var transport = new ManualCombatFixtureTransport();
        transport.EnqueueObservation(
            ManualCombatTransportOperation.Inspect,
            Active("timeout-hash", actions: LegalActions()));
        transport.EnqueueFailure(
            ManualCombatTransportOperation.Submit,
            new TimeoutException("fixture timeout"));
        transport.EnqueueObservation(
            ManualCombatTransportOperation.Refresh,
            Active("refreshed-hash", round: 2, actions: LegalActions()));
        var store = new InMemoryManualCombatCheckpointStore();
        await using var gateway = new ManualCombatGateway(
            transport,
            checkpoints: store);

        var preview = await gateway.PreviewAsync(new ManualCombatRequest(
            "combat-1",
            ManualCombatAction.Attack(),
            expectedRound: 1,
            expectedResponseHash: "timeout-hash",
            adventureId: "skeleton"));
        var timedOut = await gateway.ExecuteAsync(preview);

        Assert.Equal(ManualCombatStatus.Indeterminate, timedOut.Status);
        Assert.True(timedOut.RequiresRefresh);
        Assert.Equal(1, transport.Count(ManualCombatTransportOperation.Submit));
        Assert.True((await store.LoadAsync("combat-1"))!.RequiresRefresh);

        var retry = await gateway.ExecuteAsync(preview);
        Assert.Equal(ManualCombatStatus.RequiresRefresh, retry.Status);
        Assert.Equal(1, transport.Count(ManualCombatTransportOperation.Submit));

        var refreshed = await gateway.RefreshAsync("combat-1");
        Assert.Equal(ManualCombatStatus.Inspected, refreshed.Status);
        Assert.Equal("refreshed-hash", refreshed.Observation!.ResponseHash);
        Assert.False((await store.LoadAsync("combat-1"))!.RequiresRefresh);
        Assert.Equal(1, transport.Count(ManualCombatTransportOperation.Refresh));

        var stalePreview = await gateway.ExecuteAsync(preview);
        Assert.Equal(ManualCombatStatus.Aborted, stalePreview.Status);
        Assert.Equal(ManualCombatAbortReason.StalePreview, stalePreview.PrimaryAbortReason);
        Assert.Equal(1, transport.Count(ManualCombatTransportOperation.Submit));
    }

    [Fact]
    public async Task TerminalDeathObservationBlocksPreviewWithoutSubmitting()
    {
        var transport = new ManualCombatFixtureTransport();
        transport.EnqueueObservation(
            ManualCombatTransportOperation.Inspect,
            new ManualCombatObservation
            {
                CombatId = "combat-1",
                AdventureId = "skeleton",
                Round = 1,
                Outcome = ManualCombatOutcome.Lost,
                ResponseHash = "death-hash"
            });
        var store = new InMemoryManualCombatCheckpointStore();
        await using var gateway = new ManualCombatGateway(transport, checkpoints: store);

        var preview = await gateway.PreviewAsync(new ManualCombatRequest(
            "combat-1",
            ManualCombatAction.Attack(),
            expectedRound: 1,
            expectedResponseHash: "death-hash",
            adventureId: "skeleton"));

        Assert.False(preview.IsReady);
        Assert.Contains(ManualCombatAbortReason.TerminalCombat, preview.AbortReasons);
        var result = await gateway.ExecuteAsync(preview);
        Assert.Equal(ManualCombatStatus.Aborted, result.Status);
        Assert.Equal(ManualCombatAbortReason.TerminalCombat, result.PrimaryAbortReason);
        Assert.Equal(0, transport.Count(ManualCombatTransportOperation.Submit));
    }

    [Fact]
    public async Task ApprovalRefusalStopsManualActionBeforeTransportPost()
    {
        var transport = new ManualCombatFixtureTransport();
        transport.EnqueueObservation(
            ManualCombatTransportOperation.Inspect,
            Active("approval-hash", actions: LegalActions()));
        var store = new InMemoryManualCombatCheckpointStore();
        var approval = new DelegateManualCombatApprovalHook((_, _) =>
            ValueTask.FromResult(ManualCombatApprovalDecision.Deny("manual review required")));
        await using var gateway = new ManualCombatGateway(
            transport,
            checkpoints: store,
            approval: approval);

        var preview = await gateway.PreviewAsync(new ManualCombatRequest(
            "combat-1",
            ManualCombatAction.Attack(),
            expectedRound: 1,
            expectedResponseHash: "approval-hash",
            adventureId: "skeleton"));
        var result = await gateway.ExecuteAsync(preview);

        Assert.Equal(ManualCombatStatus.Aborted, result.Status);
        Assert.Equal(ManualCombatAbortReason.ApprovalRejected, result.PrimaryAbortReason);
        Assert.Equal(0, transport.Count(ManualCombatTransportOperation.Submit));
    }

    public static IEnumerable<object[]> ManualActions()
    {
        yield return new object[] { ManualCombatAction.Attack() };
        yield return new object[] { ManualCombatAction.Skill("1001") };
        yield return new object[] { ManualCombatAction.Item("2002", quantity: 2) };
        yield return new object[] { ManualCombatAction.Run() };
    }

    private static ManualCombatAction[] LegalActions()
        => new ManualCombatAction[]
        {
            ManualCombatAction.Attack(),
            ManualCombatAction.Skill("1001"),
            ManualCombatAction.Item("2002", quantity: 2),
            ManualCombatAction.Run()
        };
}
