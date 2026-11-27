using KoLibri.Desktop;
using KoLibri.Desktop.ContextMenus;
using Xunit;

namespace KoLibri.Tests.Desktop;

/// <summary>Package W-B, desktop part: End run, answered choices, failed reads, one job definition — against the fake KoL.</summary>
public sealed class AdventureChoiceHardeningTests
{
    [Fact]
    public async Task Unavailable_keeps_the_choice_panel_and_says_so()
    {
        var root = Directory.CreateTempSubdirectory("kolibri-wb-");
        try
        {
            var kol = new FakeKol().On("GET main.php", (_, _) => FakeKol.Html("busy", System.Net.HttpStatusCode.ServiceUnavailable));
            var session = AdventureKit.Session(kol, root.FullName, out var accounts);
            await using var _ = accounts;
            session.Decisions.Show(new NeedsDecisionInfo
            {
                RunId = "r", ChoiceId = "9001", Options = new[] { new ChoiceOptionInfo(1, "a") }
            });

            var message = await session.Decisions.RefreshAsync();

            Assert.Contains("open encounter", message);
            Assert.Equal("9001", session.Decisions.ChoiceId); // never wiped by a failed read
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task A_refused_option_is_not_a_success_and_shows_what_is_open()
    {
        var root = Directory.CreateTempSubdirectory("kolibri-wb-");
        try
        {
            var kol = new FakeKol()
                .On("GET main.php", (_, _) => AdventureKit.To("choice.php"))
                .On("GET choice.php", AdventureKit.Choice)
                .On("POST choice.php", AdventureKit.Choice); // KoL shows the same choice again
            var session = AdventureKit.Session(kol, root.FullName, out var accounts);
            await using var _ = accounts;
            await session.Decisions.RefreshAsync();

            var message = await session.Decisions.SubmitAsync(2);

            Assert.StartsWith("Not sent", message);
            Assert.Equal("1234", session.Decisions.ChoiceId);
            Assert.Equal(1, kol.Count("POST", "choice.php"));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void Menu_and_form_describe_the_same_job_with_one_default()
    {
        Assert.Equal(AdventureJob.DefaultTurns, new LocationMenuTarget("1", "x").RunTurns);
        var job = AdventureJob.TryCreate(3, " 123 ", 15, 40, out var error);
        Assert.Null(error);
        Assert.Equal(new AdventureJob(3, "123", 15, 40), job);
        Assert.Null(AdventureJob.TryCreate(3, "abc", 0, 0, out error));
        Assert.NotNull(error);
        Assert.Equal(AdventureJob.DefaultTurns, AdventureJob.TryCreate(null, null, 0, 0, out _)!.Turns);

        var withItem = new LocationContextMenu(new MenuHost()).Build(new LocationMenuTarget("1", "x", RunTurns: 3, StopItemId: "123"));
        Assert.Contains(withItem, i => i.Label == "Run 3 turns (until item 123)");
    }

    private sealed class MenuHost : IShellHost
    {
        public AccountRole ActiveRole => AccountRole.Main;
        public SessionLogViewModel SessionLog { get; } = new(post: action => action());
        public void ShowMessage(string message) { }
        public Task NavigateToAsync(string section) => Task.CompletedTask;
        public Task RefreshStatusAsync(bool forceRefresh) => Task.CompletedTask;
        public Task SearchMallAsync(string term) => Task.CompletedTask;
        public Task CopyTextAsync(string text) => Task.CompletedTask;
        public Task OpenUrlAsync(Uri uri) => Task.CompletedTask;
    }

    [Fact]
    public async Task End_run_cancels_a_running_batch_and_sends_no_more_turns()
    {
        var root = Directory.CreateTempSubdirectory("kolibri-wb-end-");
        try
        {
            var kol = new FakeKol();
            var slow = new Blocking(kol, "adventure.php");
            await using var accounts = AdventureKit.Accounts(slow, DesktopKit.Store(AccountRole.Main));
            var statuses = new List<string>();
            using var pane = new EncounterBrowserPane(accounts, statuses.Add);
            pane.Initialise(AdventureKit.CatalogDirectory, root.FullName);
            pane.SelectAccount(AccountRole.Main);
            pane.ApplyApprovalToken("fixture");
            var row = pane.Browser!.Locations.First(r => r.Snarfblat == 543);

            var running = pane.RunJobAsync(row, new AdventureJob(10, null, 0, 0)).AsTask();
            await slow.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            pane.EndSession();
            await running.WaitAsync(TimeSpan.FromSeconds(10)); // returns instead of burning the budget

            Assert.Equal(1, kol.Count("POST", "adventure.php"));
            Assert.False(pane.Session!.IsSessionActive);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task An_answered_choice_updates_the_run_so_the_remaining_turns_continue()
    {
        var root = Directory.CreateTempSubdirectory("kolibri-wb-choice-");
        try
        {
            var kol = AdventureKit.ChoiceKol();
            kol.On("POST adventure.php", (_, _) => { kol.StatusAdventures--; return FakeKol.Html(AdventureKit.ChoiceDone); });
            var session = AdventureKit.Session(kol, root.FullName, out var accounts);
            await using var _ = accounts;
            var browser = AdventureKit.Browser();

            await session.RunTurnsAsync(browser, AdventureKit.Garden, 1, null);
            Assert.True(session.IsSessionActive, session.LastMessage);
            Assert.Equal("1234", session.Decisions.ChoiceId);

            var message = await session.Decisions.SubmitAsync(2);

            Assert.StartsWith("Chose option 2", message);
            Assert.False(session.IsSessionActive, message); // RunId follows the answered choice
            Assert.Null(session.Decisions.ChoiceId);
            await session.RunTurnsAsync(browser, null, 0, null);
            Assert.Contains("left", session.LastMessage);
            Assert.Equal(new[] { "/adventure.php", "/choice.php", "/adventure.php" }, kol.Posts().Select(p => p.Path));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task A_role_switch_during_a_decision_is_not_an_unhandled_exception()
    {
        var root = Directory.CreateTempSubdirectory("kolibri-wb-switch-");
        try
        {
            var kol = new FakeKol();
            var slow = new Blocking(kol, "main.php");
            await using var accounts = AdventureKit.Accounts(slow, AdventureKit.BothRoles());
            var statuses = new List<string>();
            using var pane = new EncounterBrowserPane(accounts, statuses.Add);
            pane.Initialise(AdventureKit.CatalogDirectory, root.FullName);
            pane.SelectAccount(AccountRole.Main);

            var refresh = pane.RefreshDecisionAsync().AsTask();
            await slow.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            pane.SelectAccount(AccountRole.Mallbot);

            await refresh.WaitAsync(TimeSpan.FromSeconds(10)); // completes normally, no OperationCanceledException
            Assert.DoesNotContain(statuses, s => s.Contains("canceled"));
        }
        finally { root.Delete(recursive: true); }
    }
}
