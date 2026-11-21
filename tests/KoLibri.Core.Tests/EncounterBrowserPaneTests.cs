using KoLibri.Desktop;
using Xunit;
using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Tests;

public sealed class EncounterBrowserPaneTests
{
    private static string VendorPath() => Path.Combine(AppContext.BaseDirectory, "external", "kolmafia-data");

    private static string VendorCopy()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kolibri-pane-vendor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var file in Directory.GetFiles(VendorPath()))
        {
            File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
        }
        return dir;
    }

    private static EncounterBrowserPane BuildPane(AccountRole role = AccountRole.Main, AdventureExecutionGateway? gate = null)
    {
        var fallback = gate ?? new AdventureExecutionGateway(
            new AdventureExecutionFixtureTransport(),
            new InMemoryAdventureRunCheckpointStore());
        return new EncounterBrowserPane(
            role,
            _ => fallback,
            _ => { },
            _ => Array.Empty<string>(),
            _ => Array.Empty<MafiaMonsterRecord>());
    }

    [Fact]
    public void Pane_initialises_browser_and_session_against_vendor_catalog()
    {
        var pane = BuildPane();
        try
        {
            pane.Initialise(VendorPath());
            Assert.NotNull(pane.Browser);
            Assert.NotNull(pane.Session);
            Assert.True(pane.Browser!.Locations.Count > 100);
            Assert.NotEmpty(pane.Browser.Locations);
        }
        finally { pane.Dispose(); }
    }

    [Fact]
    public async Task Pane_persists_and_loads_token_via_its_own_store()
    {
        var dir = VendorCopy();
        try
        {
            var pane = BuildPane();
            pane.Initialise(dir);
            pane.Session!.ApprovalToken = "manual-token";
            await pane.PersistTokenAsync("pane-test");
            Assert.True(File.Exists(Path.Combine(dir, "manual-approval-token.json")));

            var pane2 = BuildPane();
            pane2.Initialise(dir);
            await pane2.LoadStoredTokenAsync();
            Assert.Equal("manual-token", pane2.Session!.ApprovalToken);

            pane.Dispose();
            pane2.Dispose();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Pane_start_adventure_submits_via_session()
    {
        var dir = VendorCopy();
        try
        {
            var transport = new AdventureExecutionFixtureTransport();
            transport.EnqueueObservation(AdventureTransportOperation.Adventure,
                new AdventureKernelObservation
                {
                    AdventureId = "adventure=543:Your Mushroom Garden",
                    Kind = AdventureEncounterKind.Fight,
                    EncounterId = "skeleton-of-crimbos-past",
                    MonsterId = "skeleton-of-crimbos-past",
                    FightRound = 1,
                    CanContinue = true,
                    ResponseHash = "f1"
                });

            var gate = new AdventureExecutionGateway(transport, new InMemoryAdventureRunCheckpointStore());
            var pane = BuildPane(AccountRole.Main, gate);
            pane.Initialise(dir);
            pane.ApplyApprovalToken("allow-token");
            var row = pane.Browser!.Locations.First();
            await pane.StartAdventureAsync(row);
            Assert.True(pane.Session!.IsSessionActive, pane.Session.LastMessage);
            Assert.Equal(1, transport.RequestCount);

            pane.Dispose();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Pane_submit_step_submits_action_via_session()
    {
        var dir = VendorCopy();
        try
        {
            var transport = new AdventureExecutionFixtureTransport();
            transport.EnqueueObservation(AdventureTransportOperation.Adventure,
                new AdventureKernelObservation
                {
                    AdventureId = "adventure=543:Your Mushroom Garden",
                    Kind = AdventureEncounterKind.Fight,
                    EncounterId = "skeleton-of-crimbos-past",
                    MonsterId = "skeleton-of-crimbos-past",
                    FightRound = 1,
                    CanContinue = true,
                    ResponseHash = "f1"
                });
            transport.EnqueueObservation(AdventureTransportOperation.Fight,
                new AdventureKernelObservation
                {
                    AdventureId = "adventure=543:Your Mushroom Garden",
                    Kind = AdventureEncounterKind.Reward,
                    CanContinue = false,
                    ResponseHash = "reward"
                });
            var gate = new AdventureExecutionGateway(transport, new InMemoryAdventureRunCheckpointStore());
            var pane = BuildPane(AccountRole.Main, gate);
            pane.Initialise(dir);
            pane.ApplyApprovalToken("allow-token");

            var location = pane.Browser!.Locations.First();
            await pane.StartAdventureAsync(location);

            pane.Browser.SelectedLocation = location;
            var monster = pane.Browser.Monsters.FirstOrDefault();
            if (monster is not null) pane.Browser.SelectedMonster = monster;
            var attack = pane.Browser.Actions.First(a => a.Kind == "attack");
            await pane.SubmitStepAsync(attack);

            Assert.True(pane.Session!.StepLog.Count >= 2, pane.Session.LastMessage);
            pane.Dispose();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
