using Xunit;
using KolArbitrage.Core.Spiel;

namespace KolArbitrage.Core.Tests;

public sealed class AdventureExecutionGatewayTests
{
    private static AdventureActionAllowlist SkeletonAllowlist()
        => new AdventureActionAllowlist()
            .AddAdventure("skeleton")
            .AddEncounter("skeleton", "skeleton")
            .AddFightAction("skeleton", "attack", "skeleton", round: 1, monsterId: "skeleton");

    private static AdventureRunRequest Request(string id = "gateway-fixture")
        => new()
        {
            RunId = id,
            AdventureId = "skeleton",
            DailyLimit = new AdventureDailyLimitContract { MaxTurns = 1, MaxRuns = 1 }
        };

    [Fact]
    public async Task StartPersistsCatalogSnarfblatAndBindsItToAdventureForm()
    {
        var transport = new AdventureExecutionFixtureTransport();
        transport.EnqueueObservation(
            AdventureTransportOperation.Adventure,
            new AdventureKernelObservation
            {
                AdventureId = "skeleton",
                Kind = AdventureEncounterKind.Reward,
                CanContinue = false,
                ResponseHash = "snarfblat-page"
            });

        var request = Request("snarfblat-gateway") with { Snarfblat = 123 };
        await using var gateway = new AdventureExecutionGateway(
            transport, new InMemoryAdventureRunCheckpointStore());

        var result = await gateway.StartAsync(request, SkeletonAllowlist());

        Assert.Equal(AdventureExecutionStatus.Complete, result.Status);
        var post = Assert.Single(transport.Requests);
        Assert.Equal(123, post.Snarfblat);
        Assert.Equal("123", post.Fields["snarfblat"]);
        Assert.Equal("skeleton", post.Fields["adventure"]);
        Assert.Equal("skeleton", post.Fields["location"]);
    }

    [Fact]
    public async Task StartAndFightUseCorrelatedFixtureRequestsAndCasCheckpoint()
    {
        var transport = new AdventureExecutionFixtureTransport();
        transport.EnqueueObservation(
            AdventureTransportOperation.Adventure,
            new AdventureKernelObservation
            {
                AdventureId = "skeleton",
                Kind = AdventureEncounterKind.Fight,
                EncounterId = "skeleton",
                MonsterId = "skeleton",
                FightRound = 1,
                CanContinue = true,
                ResponseHash = "fight-page"
            });
        transport.EnqueueObservation(
            AdventureTransportOperation.Fight,
            new AdventureKernelObservation
            {
                AdventureId = "skeleton",
                Kind = AdventureEncounterKind.Reward,
                CanContinue = false,
                ResponseHash = "reward-page"
            });

        var store = new InMemoryAdventureRunCheckpointStore();
        await using var gateway = new AdventureExecutionGateway(transport, store);

        var started = await gateway.StartAsync(Request(), SkeletonAllowlist());
        Assert.Equal(AdventureExecutionStatus.Advanced, started.Status);
        Assert.IsType<AdventureFightAction>(started.NextAction);
        Assert.Equal(AdventureTransportOperation.Adventure, transport.Requests[0].Operation);
        Assert.Equal(transport.Requests[0].RequestId, started.RequestId);

        var completed = await gateway.ExecutePendingAsync("gateway-fixture", SkeletonAllowlist());
        Assert.Equal(AdventureExecutionStatus.Complete, completed.Status);
        Assert.True(completed.IsComplete);
        Assert.Equal(AdventureTransportOperation.Fight, transport.Requests[1].Operation);

        var checkpoint = await store.LoadAsync("gateway-fixture");
        Assert.NotNull(checkpoint);
        Assert.Equal(AdventureRunPhase.Complete, checkpoint!.Phase);
        Assert.Null(checkpoint.InFlightRequestId);
        Assert.True(checkpoint.Revision >= 3);
    }

    [Fact]
    public async Task UnknownFixtureAbortsWithoutGuessingAnAction()
    {
        var transport = new AdventureExecutionFixtureTransport();
        transport.EnqueueHtml(
            AdventureTransportOperation.Adventure,
            "skeleton",
            "<html><body>an unfamiliar page</body></html>",
            new Uri("https://example.invalid/relay.php"));
        var store = new InMemoryAdventureRunCheckpointStore();
        await using var gateway = new AdventureExecutionGateway(transport, store);

        var result = await gateway.StartAsync(Request("unknown-gateway"), SkeletonAllowlist());

        Assert.Equal(AdventureExecutionStatus.Aborted, result.Status);
        Assert.Equal(AdventureAbortReason.UnknownEncounter, result.AbortReason);
        Assert.Empty(result.Decision.Actions);
        var checkpoint = await store.LoadAsync("unknown-gateway");
        Assert.Equal(AdventureRunPhase.Aborted, checkpoint!.Phase);
        Assert.Null(checkpoint.InFlightRequestId);
    }

    [Fact]
    public async Task ApprovalRefusalPreventsTransportPost()
    {
        var transport = new AdventureExecutionFixtureTransport();
        var store = new InMemoryAdventureRunCheckpointStore();
        var approval = new DelegateAdventureApprovalHook((_, _) =>
            ValueTask.FromResult(AdventureApprovalDecision.Deny("manual approval required")));
        await using var gateway = new AdventureExecutionGateway(
            transport, store, approval: approval);

        var result = await gateway.StartAsync(Request("approval-gateway"), SkeletonAllowlist());

        Assert.Equal(AdventureExecutionStatus.Aborted, result.Status);
        Assert.Equal(AdventureAbortReason.ApprovalRequired, result.AbortReason);
        Assert.Equal(0, transport.RequestCount);
        var checkpoint = await store.LoadAsync("approval-gateway");
        Assert.Equal(AdventureAbortReason.ApprovalRequired, checkpoint!.AbortReason);
    }

    [Fact]
    public async Task MismatchedCorrelationAbortsAndNeverRetriesPost()
    {
        var transport = new AdventureExecutionFixtureTransport();
        transport.Enqueue(AdventureTransportOperation.Adventure,
            new AdventureTransportResponse
            {
                RequestId = "some-other-request",
                RunId = "correlation-gateway",
                AdventureId = "skeleton",
                Html = "<html>unknown</html>",
                ResponseUri = new Uri("https://example.invalid/relay.php")
            });
        var store = new InMemoryAdventureRunCheckpointStore();
        await using var gateway = new AdventureExecutionGateway(transport, store);

        var result = await gateway.StartAsync(Request("correlation-gateway"), SkeletonAllowlist());

        Assert.Equal(AdventureAbortReason.ResponseCorrelationMismatch, result.AbortReason);
        Assert.Equal(1, transport.RequestCount);
        var resumed = await gateway.ResumeAsync("correlation-gateway", SkeletonAllowlist());
        Assert.True(resumed.IsAborted);
        Assert.Equal(1, transport.RequestCount);
    }

    [Fact]
    public async Task PostFailureLeavesInFlightMarkerAndRefreshReconcilesWithoutRetry()
    {
        var transport = new AdventureExecutionFixtureTransport();
        transport.EnqueueFailure(
            AdventureTransportOperation.Adventure,
            new TimeoutException("fixture timeout"));
        transport.EnqueueObservation(
            AdventureTransportOperation.Refresh,
            new AdventureKernelObservation
            {
                AdventureId = "skeleton",
                Kind = AdventureEncounterKind.Fight,
                EncounterId = "skeleton",
                MonsterId = "skeleton",
                FightRound = 1,
                CanContinue = true,
                ResponseHash = "reconciled-fight"
            });

        var store = new InMemoryAdventureRunCheckpointStore();
        await using var gateway = new AdventureExecutionGateway(transport, store);

        var timedOut = await gateway.StartAsync(Request("timeout-gateway"), SkeletonAllowlist());
        Assert.Equal(AdventureExecutionStatus.Indeterminate, timedOut.Status);
        Assert.True(timedOut.RequiresRefresh);
        Assert.Equal(1, transport.Count(AdventureTransportOperation.Adventure));
        Assert.NotNull((await store.LoadAsync("timeout-gateway"))!.InFlightRequestId);

        var reconciled = await gateway.RefreshAsync("timeout-gateway", SkeletonAllowlist());
        Assert.Equal(AdventureExecutionStatus.Advanced, reconciled.Status);
        Assert.IsType<AdventureFightAction>(reconciled.NextAction);
        Assert.Equal(1, transport.Count(AdventureTransportOperation.Adventure));
        Assert.Equal(1, transport.Count(AdventureTransportOperation.Refresh));
    }
}
