using KoLibri.Desktop;
using Xunit;
using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Tests;

public sealed class DesktopAdventureRunBridgeTests
{
    [Fact]
    public async Task Run_without_execute_returns_preview_only()
    {
        var transport = new AdventureExecutionFixtureTransport();
        var store = new InMemoryAdventureRunCheckpointStore();
        var factory = new TransportAdventureExecutionGatewayFactory(transport, store, expectedToken: "allow-token");
        using var bridge = new DesktopAdventureRunBridge(
            factory, new FixedApprovalTokenValidator("allow-token"), auditPath: "<memory>");

        var result = await bridge.RunBoundedAsync(
            AccountRole.Main,
            new AdventureRunRequest { RunId = "p-1", AdventureId = "skeleton" },
            new AdventureActionAllowlist().AddAdventure("skeleton"),
            Array.Empty<AdventureCondition>(),
            new StaticConditionSnapshotProvider(new AdventureConditionSnapshot()),
            new BoundedAdventureRunnerOptions(),
            execute: false,
            approvalToken: "allow-token");

        Assert.False(result.Executed);
        Assert.Equal("ReadyToRun", result.StopReason);
        Assert.Equal(0, transport.RequestCount);
    }

    [Fact]
    public async Task Run_with_token_drives_bounded_runner_and_audits()
    {
        var transport = new AdventureExecutionFixtureTransport();
        transport.EnqueueObservation(AdventureTransportOperation.Adventure,
            new AdventureKernelObservation
            {
                AdventureId = "skeleton", Kind = AdventureEncounterKind.Reward,
                CanContinue = false, ResponseHash = "reward"
            });
        var store = new InMemoryAdventureRunCheckpointStore();
        var factory = new TransportAdventureExecutionGatewayFactory(transport, store, expectedToken: "allow-token");
        var journalPath = Path.Combine(Path.GetTempPath(),
            "kolibri-bridge-audit-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            using var journal = new JsonlAuditEventJournal(journalPath);
            using var bridge = new DesktopAdventureRunBridge(
                factory, new FixedApprovalTokenValidator("allow-token"), journal, journalPath);

            var result = await bridge.RunBoundedAsync(
                AccountRole.Main,
                new AdventureRunRequest
                {
                    RunId = "run-1",
                    AdventureId = "skeleton",
                    DailyLimit = new AdventureDailyLimitContract { MaxTurns = 1, MaxRuns = 1 }
                },
                new AdventureActionAllowlist().AddAdventure("skeleton"),
                Array.Empty<AdventureCondition>(),
                new StaticConditionSnapshotProvider(new AdventureConditionSnapshot()),
                new BoundedAdventureRunnerOptions(),
                execute: true,
                approvalToken: "allow-token");

            Assert.True(result.Executed);
            Assert.Equal("Complete", result.StopReason);
            Assert.Equal(1, transport.Count(AdventureTransportOperation.Adventure));
            Assert.Equal(journalPath, result.AuditPath);
            Assert.True(File.Exists(journalPath));
            var lines = await File.ReadAllLinesAsync(journalPath);
            Assert.NotEmpty(lines);
        }
        finally
        {
            if (File.Exists(journalPath)) File.Delete(journalPath);
        }
    }

    [Fact]
    public async Task Run_with_wrong_token_is_denied_and_audited()
    {
        var transport = new AdventureExecutionFixtureTransport();
        var store = new InMemoryAdventureRunCheckpointStore();
        var factory = new TransportAdventureExecutionGatewayFactory(transport, store, expectedToken: "allow-token");
        using var bridge = new DesktopAdventureRunBridge(
            factory, new FixedApprovalTokenValidator("allow-token"));

        var result = await bridge.RunBoundedAsync(
            AccountRole.Main,
            new AdventureRunRequest { RunId = "denied-1", AdventureId = "skeleton" },
            new AdventureActionAllowlist().AddAdventure("skeleton"),
            Array.Empty<AdventureCondition>(),
            new StaticConditionSnapshotProvider(new AdventureConditionSnapshot()),
            new BoundedAdventureRunnerOptions(),
            execute: true,
            approvalToken: "wrong-token");

        Assert.False(result.Executed);
        Assert.Equal("ApprovalDenied", result.StopReason);
        Assert.Equal(0, transport.RequestCount);
    }

    [Fact]
    public void ViewModel_prepare_preview_marks_approval_when_bridge_present()
    {
        var vm = new BoundedAdventureRunViewModel();
        vm.Preview("skeleton-of-crimbos-past");
        Assert.False(vm.RequiresApproval); // no bridge => preview only
        Assert.Equal("ReadyToRun", vm.StopReason);
        Assert.Equal(BoundedRunUiState.Preview, vm.State);
    }

    [Fact]
    public void ViewModel_apply_denied_result_reflects_bridge_decision()
    {
        var vm = new BoundedAdventureRunViewModel();
        vm.ApplyResult(new BoundedRunResult(false, "ApprovalDenied", null, "<memory>", 0));
        Assert.Equal(BoundedRunUiState.Denied, vm.State);
        Assert.False(vm.IsRunning);
    }

    [Fact]
    public void ViewModel_stop_is_no_op_for_idle_or_preview_state()
    {
        var vm = new BoundedAdventureRunViewModel();
        vm.Stop(); // Idle => no-op
        Assert.Equal(BoundedRunUiState.Idle, vm.State);

        vm.Preview("skeleton");
        vm.Stop();
        // Preview does not have a live operation yet; Stop remains a no-op.
        Assert.Equal(BoundedRunUiState.Preview, vm.State);
    }

    [Fact]
    public void Context_action_catalog_lists_bounded_actions()
    {
        var actions = BoundedRunContextActions.All;
        Assert.Contains(actions, a => a.CommandId == BoundedRunContextActions.RunBoundedAdventure);
        Assert.Contains(actions, a => a.CommandId == BoundedRunContextActions.RunBoundedChoiceChain);
        Assert.Contains(actions, a => a.CommandId == BoundedRunContextActions.CancelBoundedRun);
    }

    private sealed class StaticConditionSnapshotProvider : IAdventureConditionSnapshotProvider
    {
        private readonly AdventureConditionSnapshot _snapshot;
        public StaticConditionSnapshotProvider(AdventureConditionSnapshot snapshot)
            => _snapshot = snapshot;
        public ValueTask<AdventureConditionSnapshot> ReadAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_snapshot);
    }
}
