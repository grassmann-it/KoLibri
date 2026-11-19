using KolArbitrage.Core.Accounts;
using KolArbitrage.Core.Adventure;
using KolArbitrage.Core.Data;
using KolArbitrage.Core.Security;
using KoLibri.Desktop;
using Xunit;

namespace KolArbitrage.Core.Tests;

public sealed class EncounterBrowserViewModelTests
{
    private static KoLmafiaCatalog LoadCatalog()
        => new MafiaDataLoader().Load(Path.Combine(AppContext.BaseDirectory, "external", "kolmafia-data"));

    [Fact]
    public void Browser_loads_locations_from_vendor_catalog()
    {
        var vm = new EncounterBrowserViewModel(LoadCatalog());
        Assert.NotEmpty(vm.Locations);
        Assert.Contains(vm.Locations, l => l.LocationId == "skeleton-of-crimbos-past");
        var skeleton = vm.Locations.First(l => l.LocationId == "skeleton-of-crimbos-past");
        Assert.Equal("Crimbo", skeleton.Zone);
    }

    [Fact]
    public void Selecting_skeleton_location_shows_related_monsters()
    {
        var vm = new EncounterBrowserViewModel(LoadCatalog());
        vm.SelectedLocation = vm.Locations.First(l => l.LocationId == "skeleton-of-crimbos-past");
        Assert.NotEmpty(vm.Monsters);
        Assert.Contains(vm.Monsters, m => m.MonsterId == "skeleton-of-crimbos-past");
    }

    [Fact]
    public void Selecting_monster_builds_attack_skill_item_actions()
    {
        var vm = new EncounterBrowserViewModel(LoadCatalog());
        vm.SelectedLocation = vm.Locations.First(l => l.LocationId == "skeleton-of-crimbos-past");
        var monster = vm.Monsters.First(m => m.MonsterId == "skeleton-of-crimbos-past");
        vm.SelectedMonster = monster;
        Assert.Contains(vm.Actions, a => a.Kind == "attack" && a.Id == "attack");
        Assert.Contains(vm.Actions, a => a.Kind == "run");
    }

    [Fact]
    public void BuildStepSpec_returns_skill_step_for_skill_action()
    {
        var vm = new EncounterBrowserViewModel(LoadCatalog());
        vm.SelectedLocation = vm.Locations.First(l => l.LocationId == "skeleton-of-crimbos-past");
        vm.SelectedMonster = vm.Monsters.First();
        var skillAction = vm.Actions.First(a => a.Kind == "skill");
        var step = vm.BuildStepSpec(skillAction);
        var skill = Assert.IsType<SkillStepSpec>(step);
        Assert.Equal(skillAction.Id, skill.SkillId);
        Assert.Equal(vm.SelectedMonster!.MonsterId, skill.MonsterId);
    }

    [Fact]
    public void FilterText_narrows_visible_locations()
    {
        var vm = new EncounterBrowserViewModel(LoadCatalog());
        vm.FilterText = "skeleton";
        Assert.NotEmpty(vm.Locations);
        Assert.All(vm.Locations, l =>
        {
            Assert.True(
                l.DisplayName.Contains("Skeleton", StringComparison.OrdinalIgnoreCase)
                || l.Zone.Contains("skeleton", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public async Task Session_persists_and_loads_token_via_store()
    {
        var root = Path.Combine(Path.GetTempPath(), "kolibri-browser-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new ApprovalTokenStore(root);
            var transport = new AdventureExecutionFixtureTransport();
            await using var gateway = new AdventureExecutionGateway(transport, new InMemoryAdventureRunCheckpointStore());
            var driver = new AdventureManualDriver(AccountRole.Main, gateway, "allow-token");
            var session = new EncounterBrowserSessionViewModel(driver, store, AccountRole.Main)
            {
                ApprovalToken = "stored-secret"
            };
            await session.PersistTokenAsync("manual", CancellationToken.None);
            Assert.True(File.Exists(store.FilePath));

            var session2 = new EncounterBrowserSessionViewModel(driver, store, AccountRole.Main);
            await session2.LoadStoredTokenAsync(CancellationToken.None);
            Assert.Equal("stored-secret", session2.ApprovalToken);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Session_starts_adventure_when_token_present()
    {
        var root = Path.Combine(Path.GetTempPath(), "kolibri-browser-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new ApprovalTokenStore(root);
            var transport = new AdventureExecutionFixtureTransport();
            transport.EnqueueObservation(AdventureTransportOperation.Adventure,
                new AdventureKernelObservation
                {
                    AdventureId = "skeleton-of-crimbos-past",
                    Kind = AdventureEncounterKind.Fight,
                    EncounterId = "skeleton-of-crimbos-past",
                    MonsterId = "skeleton-of-crimbos-past",
                    FightRound = 1,
                    CanContinue = true,
                    ResponseHash = "f1"
                });
            await using var gateway = new AdventureExecutionGateway(transport, new InMemoryAdventureRunCheckpointStore());
            var driver = new AdventureManualDriver(AccountRole.Main, gateway, "allow-token");

            var session = new EncounterBrowserSessionViewModel(driver, store, AccountRole.Main)
            {
                ApprovalToken = "allow-token"
            };
            var browser = new EncounterBrowserViewModel(LoadCatalog());
            var location = new KoLibri.Desktop.AdventureLocation("skeleton-of-crimbos-past", "Skeleton of Crimbos Past",
                "Crimbo", Snarfblat: 88, TurnCost: 1,
                Encounters: Array.Empty<AdventureEncounterMetadata>());

            var start = await session.StartAdventureAsync(location, browser, CancellationToken.None);
            Assert.NotNull(start);
            Assert.True(session.IsSessionActive);
            Assert.NotEmpty(session.StepLog);
            Assert.Equal(1, transport.RequestCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
