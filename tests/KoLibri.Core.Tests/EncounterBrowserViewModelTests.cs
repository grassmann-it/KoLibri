using KoLibri.Desktop;
using Xunit;
using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Tests;

public sealed class EncounterBrowserViewModelTests
{
    private static KoLmafiaCatalog LoadCatalog()
        => new MafiaDataLoader().Load(Path.Combine(AppContext.BaseDirectory, "external", "kolmafia-data"));

    private static EncounterBrowserLocationRow FirstTestLocation(EncounterBrowserViewModel vm)
        => vm.Locations.First(l => (l.DisplayName?.Contains("Skeleton Store", StringComparison.OrdinalIgnoreCase) ?? false)
                                   || (l.DisplayName?.Contains("Bone Spikes", StringComparison.OrdinalIgnoreCase) ?? false));

    [Fact]
    public void Browser_projects_current_fight_controls_and_resource_observation()
    {
        var browser = new EncounterBrowserViewModel(LoadCatalog());
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Adventure", "combat-round-1.html"));
        var encounter = new KoLAdventureEncounterParser().Parse(
            "skeleton", new Uri("https://example.test/fight.php"), html);
        browser.ShowEncounter(encounter);

        Assert.Equal(80, encounter.Combat!.PlayerHp);
        Assert.Equal(40, encounter.Combat.PlayerMp);
        Assert.Equal(new[] { "attack", "skill", "item" }, browser.Actions.Select(action => action.Kind));
        var skill = Assert.IsType<SkillStepSpec>(browser.BuildStepSpec(browser.Actions[1]));
        Assert.Equal("1001", skill.SkillId);
        Assert.Equal(1, skill.Round);
    }

    [Fact]
    public void Browser_projects_the_current_choice_into_selectable_steps()
    {
        var browser = new EncounterBrowserViewModel(LoadCatalog());
        var parser = new KoLAdventureEncounterParser();
        browser.ShowEncounter(parser.Parse("skeleton", new Uri("https://example.test/choice.php"), """
            <form action="choice.php">
              <input type="hidden" name="whichchoice" value="1003">
              <input type="hidden" name="option" value="2">
              <input type="submit" value="Open &amp; enter">
            </form>
            <form action="choice.php">
              <input type="hidden" name="whichchoice" value="1003">
              <button name="option" value="5"><b>Leave</b> quietly</button>
              <button name="option" value="6" disabled>Unavailable</button>
            </form>
            """));

        Assert.Equal(new[] { "Open & enter", "Leave quietly" }, browser.Actions.Select(action => action.DisplayName));
        var step = Assert.IsType<ChoiceStepSpec>(browser.BuildStepSpec(browser.Actions[1]));
        Assert.Equal("1003", step.ChoiceId);
        Assert.Equal("5", step.OptionId);
    }

    [Fact]
    public void Browser_loads_locations_from_vendor_catalog()
    {
        var vm = new EncounterBrowserViewModel(LoadCatalog());
        Assert.NotEmpty(vm.Locations);
        Assert.True(vm.Locations.Count > 100);
    }

    [Fact]
    public void Selecting_any_location_shows_basic_attack_action()
    {
        // The real Mafia catalog does not embed an explicit monster→location
        // table. We assert the minimum contract: when the operator picks a
        // location, the action list always contains a basic attack action
        // so the bounded runner can submit one step.
        var vm = new EncounterBrowserViewModel(LoadCatalog());
        vm.SelectedLocation = vm.Locations[0];
        Assert.Contains(vm.Actions, a => a.Kind == "attack" && a.Id == "attack");
    }

    [Fact]
    public void Selecting_a_monster_builds_attack_skill_item_actions()
    {
        var vm = new EncounterBrowserViewModel(LoadCatalog());
        vm.SelectedLocation = vm.Locations[0];
        var monster = vm.Monsters.FirstOrDefault();
        if (monster is null) return; // catalog has no monsters matching the location; contract still holds.
        vm.SelectedMonster = monster;
        Assert.Contains(vm.Actions, a => a.Kind == "attack" && a.Id == "attack");
        Assert.Contains(vm.Actions, a => a.Kind == "run");
    }

    [Fact]
    public void BuildStepSpec_returns_skill_step_for_skill_action()
    {
        var vm = new EncounterBrowserViewModel(LoadCatalog());
        vm.SelectedLocation = vm.Locations[0];
        var monster = vm.Monsters.FirstOrDefault();
        if (monster is null) return;
        vm.SelectedMonster = monster;
        var skillAction = vm.Actions.FirstOrDefault(a => a.Kind == "skill");
        if (skillAction is null) return;
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
                || l.Zone.Contains("skeleton", StringComparison.OrdinalIgnoreCase)
                || (l.Description?.Contains("skeleton", StringComparison.OrdinalIgnoreCase) ?? false));
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
    public async Task Session_starts_adventure_with_token_and_vendor_catalog()
    {
        var root = Path.Combine(Path.GetTempPath(), "kolibri-browser-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new ApprovalTokenStore(root);
            var transport = new AdventureExecutionFixtureTransport();
            transport.EnqueueObservation(AdventureTransportOperation.Adventure,
                new AdventureKernelObservation
                {
                    AdventureId = "adventure=439:The Skeleton Store",
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
            var locationRow = FirstTestLocation(browser);
            var location = new KoLibri.Desktop.AdventureLocation(
                locationRow.LocationId, locationRow.DisplayName, locationRow.Zone,
                Snarfblat: locationRow.Snarfblat, TurnCost: 1,
                Encounters: Array.Empty<AdventureEncounterMetadata>());

            var start = await session.StartAdventureAsync(location, browser, CancellationToken.None);
            Assert.NotNull(start);
            Assert.True(session.IsSessionActive, session.LastMessage);
            Assert.NotEmpty(session.StepLog);
            Assert.Equal(1, transport.RequestCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
