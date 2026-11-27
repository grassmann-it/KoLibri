using KoLibri.Desktop;
using Xunit;

namespace KoLibri.Tests.Desktop;

/// <summary>The adventure tab end to end on KoLibri's Adventurer, against the fake KoL (no live requests).</summary>
public sealed class DesktopManualAdventureHttpTests
{
    /// <summary>The fight page after the win redirects to a choice (fight → choice chain of one turn).</summary>
    private const string ChoiceAfterWin = "<!--WINWINWIN--><p>You win the fight!</p>" + AdventureKit.Choice;

    [Fact]
    public async Task Desktop_completes_fight_and_choice_through_the_account_session()
    {
        var root = Directory.CreateTempSubdirectory("kolibri-http-");
        try
        {
            var kol = new FakeKol { StatusAdventures = 9 };
            kol.On("POST adventure.php", (_, _) => AdventureKit.To("fight.php")).On("GET fight.php", AdventureKit.Fight)
               .On("POST fight.php", (_, _) => AdventureKit.To("choice.php"))
               .On("GET choice.php", ChoiceAfterWin)
               .On("GET main.php", (_, _) => AdventureKit.To("choice.php"))
               .On("POST choice.php", (_, _) => { kol.StatusAdventures--; return FakeKol.Html(AdventureKit.ChoiceDone); });
            var session = AdventureKit.Session(kol, root.FullName, out var accounts, AccountRole.Mallbot);
            await using var _ = accounts;
            var browser = AdventureKit.Browser();

            var start = await session.StartAdventureAsync(AdventureKit.Garden, browser);
            Assert.IsType<FightEncounter>(start?.Encounter);
            Assert.Null(await session.StartAdventureAsync(AdventureKit.Garden, browser)); // one open encounter at a time
            Assert.Single(kol.Posts());

            Assert.Equal(new[] { "attack", "skill", "item", "run" }, browser.Actions.Select(action => action.Kind));
            Assert.Equal("2002", Assert.Single(browser.Actions, action => action.Kind == "item").Id);
            var rules = AutoCombatViewModel.BuildRules(new[] { "1001" }, Array.Empty<string>());
            var target = new CombatTarget { Id = "123", Name = "Skeleton", Zone = "", Detail = "" };
            Assert.Null(await session.FightCurrentAsync(browser, target with { Id = "999" }, rules, 2, 30));
            Assert.Contains("Expected", session.LastMessage);
            Assert.Single(kol.Posts());

            var fight = await session.FightCurrentAsync(browser, target, rules, 2, 30);
            Assert.IsType<ChoiceEncounter>(fight?.Encounter);
            Assert.Equal(new[] { "1234:1", "1234:2" }, browser.Actions.Select(action => action.Id));
            var option = Assert.Single(browser.Actions, action => action.Id == "1234:2");
            var finish = await session.SubmitStepAsync(browser.BuildStepSpec(option), browser);

            Assert.True(finish?.IsComplete, finish?.Message ?? finish?.StopReason);
            Assert.False(session.IsSessionActive);
            Assert.Contains("knucklebone", session.LastEncounterSummary);
            Assert.Contains("8 adventures left", session.LastEncounterSummary);
            var writes = kol.Posts().ToArray();
            Assert.Equal(new[] { "/adventure.php", "/fight.php", "/choice.php" }, writes.Select(request => request.Path));
            Assert.Contains("snarfblat=543", writes[0].Form);
            Assert.Contains("whichskill=1001", writes[1].Form);
            Assert.Contains("option=2", writes[2].Form);
            // The status shown after the adventure is read fresh (GET only) after the choice POST.
            var afterChoice = kol.Requests.SkipWhile(r => !(r.Method == "POST" && r.Path.StartsWith("/choice.php"))).Skip(1).ToArray();
            Assert.All(afterChoice, r => Assert.Equal("GET", r.Method));
            Assert.Contains(afterChoice, r => r.Path.StartsWith("/api.php?what=status"));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task An_unreadable_fight_page_needs_a_recheck_before_the_next_step()
    {
        var root = Directory.CreateTempSubdirectory("kolibri-http-");
        try
        {
            var kol = AdventureKit.FightKol("<html><body><p>A server page replaced the encounter.</p></body></html>");
            kol.On("GET main.php", (_, _) => AdventureKit.To("choice.php")).On("GET choice.php", AdventureKit.Choice);
            var session = AdventureKit.Session(kol, root.FullName, out var accounts);
            await using var _ = accounts;
            var browser = AdventureKit.Browser();
            await session.StartAdventureAsync(AdventureKit.Garden, browser);

            var attack = await session.SubmitStepAsync(new FightStep(CombatAction.Attack), browser);

            Assert.True(attack?.RequiresRefresh);
            Assert.True(session.IsSessionActive);
            Assert.Contains("Recheck", session.LastMessage);
            Assert.NotNull(attack!.SavedPagePath);
            Assert.StartsWith(AdventureKit.UnknownPages, attack.SavedPagePath);

            var posts = kol.Posts().Count();
            var recheck = await session.SubmitStepAsync(new FightStep(CombatAction.Attack), browser); // runs the recheck instead
            Assert.Equal(posts, kol.Posts().Count());
            Assert.IsType<ChoiceEncounter>(recheck?.Encounter);
            Assert.Equal(new[] { "1234:1", "1234:2" }, browser.Actions.Select(action => action.Id));
        }
        finally { root.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Switching_roles_discards_the_open_fight_and_surfaces_the_new_accounts_stop(bool ascended)
    {
        var root = Directory.CreateTempSubdirectory("kolibri-roles-");
        try
        {
            var kol = new FakeKol();
            kol.On("POST adventure.php", (_, _) => AdventureKit.To("fight.php"))
               .On("POST adventure.php", (_, _) => AdventureKit.To("login.php")) // the second account's cookie is gone
               .On("GET fight.php", AdventureKit.Fight);
            await using var accounts = DesktopKit.Accounts(kol, AdventureKit.BothRoles());
            var messages = new List<string>();
            using var pane = new EncounterBrowserPane(accounts, messages.Add);
            pane.Initialise(AdventureKit.CatalogDirectory, root.FullName);

            pane.SelectAccount(AccountRole.Main);
            pane.ApplyApprovalToken("main-approval");
            Assert.False(pane.ObserveAscension(AccountRole.Main, "Main", 4));
            var location = Assert.Single(pane.Browser!.Locations, row => row.Snarfblat == 543);
            await pane.StartAdventureAsync(location);
            Assert.True(pane.Session!.IsSessionActive, messages.LastOrDefault());
            var mainAction = Assert.Single(pane.Browser.Actions, action => action.Kind == "skill");
            Assert.False(pane.ObserveAscension(AccountRole.Mallbot, "Mallbot", 5)); // not the selected account
            Assert.True(pane.Session.IsSessionActive);
            if (ascended)
            {
                Assert.True(pane.ObserveAscension(AccountRole.Main, "Main", 5));
                Assert.False(pane.Session!.IsSessionActive);
            }

            pane.SelectAccount(AccountRole.Mallbot);
            Assert.False(pane.Session!.IsSessionActive);
            Assert.Empty(pane.Session.ApprovalToken);
            pane.ApplyApprovalToken("mallbot-approval");
            await pane.SubmitStepAsync(mainAction);
            Assert.Single(kol.Posts()); // the main account's fight action is not sent for the mallbot

            await pane.StartAdventureAsync(location);
            Assert.False(pane.Session.IsSessionActive);
            Assert.Contains("log in", messages[^1], StringComparison.OrdinalIgnoreCase);
            Assert.All(kol.Posts(), post => Assert.Equal("/adventure.php", post.Path));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task Turn_batch_preserves_the_budget_across_a_manual_choice_and_stops_at_the_item_goal()
    {
        var root = Directory.CreateTempSubdirectory("kolibri-turn-batch-");
        try
        {
            var kol = AdventureKit.ChoiceKol();
            kol.On("POST adventure.php", (_, _) => { kol.StatusAdventures--; return FakeKol.Html(AdventureKit.ChoiceDone); });
            kol.On("GET api.php?what=inventory", "{}").On("GET api.php?what=inventory", "{}")
               .On("GET api.php?what=inventory", """{"123":"1"}""");
            var session = AdventureKit.Session(kol, root.FullName, out var accounts);
            await using var _ = accounts;
            var browser = AdventureKit.Browser();

            await session.RunTurnsAsync(browser, AdventureKit.Garden, 10, "123");
            Assert.True(session.IsSessionActive, session.LastMessage);
            Assert.Contains("10 left", session.LastMessage); // the open choice has not cost a turn yet
            Assert.Equal("/adventure.php", Assert.Single(kol.Posts()).Path);
            var selected = Assert.Single(browser.Actions, row => row.Id == "1234:2");
            var manual = await session.SubmitStepAsync(browser.BuildStepSpec(selected), browser);
            Assert.True(manual?.IsComplete);
            await session.RunTurnsAsync(browser, null, 0, null);

            Assert.False(session.IsSessionActive);
            Assert.Equal(nameof(BoundedAdventureRunStopReason.ConditionsSatisfied), session.LastReport?.StopReason);
            Assert.Equal(new[] { "/adventure.php", "/choice.php", "/adventure.php" }, kol.Posts().Select(p => p.Path));
            Assert.Null(await session.RunTurnsAsync(browser, null, 0, null)); // the goal ended the budget
        }
        finally { root.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(0, 23)]
    public async Task Desktop_observes_path_unlocks_before_starting(int firstPath, int secondPath)
    {
        var root = Directory.CreateTempSubdirectory("kolibri-access-");
        try
        {
            var kol = AdventureKit.FightKol();
            string Status(int path) => kol.StatusJson().Replace("\"level\":", $"\"path\":{path},\"ascensions\":2,\"level\":");
            kol.On("GET api.php?what=status", Status(firstPath)).On("GET api.php?what=status", Status(secondPath));
            var session = AdventureKit.Session(kol, root.FullName, out var accounts);
            await using var _ = accounts;
            var browser = AdventureKit.Browser();
            var temple = new AdventureLocation("access-zone", "Fixture", "Fixture", 438, 1);

            Assert.Null(await session.StartAdventureAsync(temple, browser));
            Assert.Contains("Ed the Undying", session.LastMessage);
            Assert.False(session.IsSessionActive);
            Assert.Empty(kol.Posts());
            var started = await session.StartAdventureAsync(temple, browser);
            Assert.IsType<FightEncounter>(started?.Encounter);
            Assert.Contains("snarfblat=438", Assert.Single(kol.Posts()).Form);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task Desktop_observes_quest_unlocks_before_starting()
    {
        var root = Directory.CreateTempSubdirectory("kolibri-access-");
        try
        {
            static string Log(string? entry, string other) => $"<b>Your Quest Log</b><a href='questlog.php?which={other}'>x</a><blockquote>{entry}</blockquote>";
            const string title = "<b>The Goblin Who Wouldn't Be King</b><br>";
            var kol = AdventureKit.FightKol();
            kol.On("GET questlog.php", Log(title + "Find your way inside Cobb's Knob.", "2")).On("GET questlog.php", Log("", "1"))
               .On("GET questlog.php", Log(title + "Find some way to infiltrate the Throne Room.", "2")).On("GET questlog.php", Log("", "1"));
            var session = AdventureKit.Session(kol, root.FullName, out var accounts);
            await using var _ = accounts;
            var browser = AdventureKit.Browser();
            var knob = new AdventureLocation("access-zone", "Fixture", "Fixture", 257, 1);

            Assert.Null(await session.StartAdventureAsync(knob, browser));
            Assert.Contains("Cobb's Knob", session.LastMessage);
            Assert.Empty(kol.Posts());
            Assert.IsType<FightEncounter>((await session.StartAdventureAsync(knob, browser))?.Encounter);
            Assert.Contains("snarfblat=257", Assert.Single(kol.Posts()).Form);
        }
        finally { root.Delete(recursive: true); }
    }
}
