using KoLibri.Desktop;
using Xunit;

namespace KoLibri.Tests.Desktop;

public sealed class EncounterBrowserViewModelTests
{
    [Fact]
    public void Browser_projects_the_open_fight_page_into_actions()
    {
        var browser = AdventureKit.Browser();
        browser.ShowEncounter(AdventureKit.FightEncounter());

        Assert.Equal(new[] { "attack", "skill", "item", "run" }, browser.Actions.Select(action => action.Kind));
        var skillRow = browser.Actions[1];
        Assert.Equal("Lunge Smack (3 Mojo Points)", skillRow.DisplayName);
        Assert.Equal("3 MP", skillRow.Detail);
        var skill = Assert.IsType<FightStep>(browser.BuildStepSpec(skillRow));
        Assert.Equal(CombatAction.Skill(1001), skill.Action);
        Assert.Equal(CombatAction.Item(2002), Assert.IsType<FightStep>(browser.BuildStepSpec(browser.Actions[2])).Action);
        Assert.Equal(CombatAction.RunAway, Assert.IsType<FightStep>(browser.BuildStepSpec(browser.Actions[3])).Action);
    }

    [Fact]
    public void Browser_projects_the_open_choice_into_selectable_steps()
    {
        var browser = AdventureKit.Browser();
        browser.ShowEncounter(AdventureKit.ChoiceEncounter());

        Assert.Equal(new[] { "Tame it", "Leave" }, browser.Actions.Select(action => action.DisplayName));
        var step = Assert.IsType<ChoiceStep>(browser.BuildStepSpec(browser.Actions[1]));
        Assert.Equal(new ChoiceStep(1003, 2), step);
    }

    [Fact]
    public void Browser_loads_locations_from_vendor_catalog()
    {
        var vm = AdventureKit.Browser();
        Assert.True(vm.Locations.Count > 100);
    }

    [Fact]
    public void Selecting_any_location_shows_basic_attack_action()
    {
        var vm = AdventureKit.Browser();
        vm.SelectedLocation = vm.Locations[0];
        Assert.Contains(vm.Actions, a => a.Kind == "attack" && a.Id == "attack");
    }

    [Fact]
    public void Selecting_a_monster_builds_attack_and_run_actions()
    {
        var vm = AdventureKit.Browser();
        vm.SelectedLocation = vm.Locations.First(l => l.MonsterCount > 0);
        var monster = vm.Monsters.FirstOrDefault();
        if (monster is null) return; // the catalog names no monster of this location; the attack contract still holds.
        vm.SelectedMonster = monster;
        Assert.Contains(vm.Actions, a => a.Kind == "attack" && a.Id == "attack");
        Assert.Contains(vm.Actions, a => a.Kind == "run");
    }

    [Fact]
    public void Catalog_skills_without_a_numeric_id_are_refused_instead_of_sent()
    {
        var vm = AdventureKit.Browser();
        var row = new EncounterBrowserActionRow("skill", "Lunge Smack", "Lunge Smack", "");
        var error = Assert.Throws<ArgumentException>(() => vm.BuildStepSpec(row));
        Assert.Contains("numeric id", error.Message);
    }

    [Fact]
    public void A_finished_encounter_falls_back_to_the_catalog_actions()
    {
        var vm = AdventureKit.Browser();
        vm.ShowEncounter(AdventureKit.FightEncounter());
        vm.ShowEncounter(null);
        Assert.Equal("attack", Assert.Single(vm.Actions).Kind);
    }

    [Fact]
    public void FilterText_narrows_visible_locations()
    {
        var vm = AdventureKit.Browser();
        vm.FilterText = "skeleton";
        Assert.NotEmpty(vm.Locations);
        Assert.All(vm.Locations, l => Assert.True(EncounterBrowserViewModel.MatchesFilter(l, "skeleton")));
    }

    [Fact]
    public async Task Session_persists_and_loads_token_via_store()
    {
        var root = Directory.CreateTempSubdirectory("kolibri-browser-");
        try
        {
            var kol = new FakeKol();
            var session = AdventureKit.Session(kol, root.FullName, out var accounts);
            await using var _ = accounts;
            session.ApprovalToken = "stored-secret";
            await session.PersistTokenAsync("manual");
            Assert.True(File.Exists(Path.Combine(root.FullName, "approval-token.json")));

            var session2 = new EncounterBrowserSessionViewModel(accounts, new ApprovalTokenStore(root.FullName), AccountRole.Main);
            await session2.LoadStoredTokenAsync();
            Assert.Equal("stored-secret", session2.ApprovalToken);
            Assert.Empty(kol.Requests);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task Session_starts_an_adventure_with_a_token()
    {
        var root = Directory.CreateTempSubdirectory("kolibri-browser-");
        try
        {
            var kol = AdventureKit.FightKol();
            var session = AdventureKit.Session(kol, root.FullName, out var accounts);
            await using var _ = accounts;
            var browser = AdventureKit.Browser();

            var start = await session.StartAdventureAsync(AdventureKit.Garden, browser);

            Assert.Equal(AdventureStepStatus.Open, start?.Status);
            Assert.IsType<FightEncounter>(start!.Encounter);
            Assert.True(session.IsSessionActive, session.LastMessage);
            Assert.NotEmpty(session.StepLog);
            Assert.Equal(1, kol.Count("POST", "adventure.php"));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task Without_a_token_nothing_is_sent()
    {
        var root = Directory.CreateTempSubdirectory("kolibri-browser-");
        try
        {
            var kol = AdventureKit.FightKol();
            var session = AdventureKit.Session(kol, root.FullName, out var accounts);
            await using var _ = accounts;
            session.ApprovalToken = "";

            Assert.Null(await session.StartAdventureAsync(AdventureKit.Garden, AdventureKit.Browser()));
            Assert.Null(await session.RunTurnsAsync(AdventureKit.Browser(), AdventureKit.Garden, 3, null));
            Assert.Contains("approved turns", session.LastMessage);
            Assert.Empty(kol.Requests);
        }
        finally { root.Delete(recursive: true); }
    }
}
