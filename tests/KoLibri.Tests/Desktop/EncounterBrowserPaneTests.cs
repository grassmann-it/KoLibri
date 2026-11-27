using KoLibri.Desktop;
using Xunit;

namespace KoLibri.Tests.Desktop;

public sealed class EncounterBrowserPaneTests
{
    private static EncounterBrowserPane Pane(DesktopAccounts accounts, string tokenDirectory, List<string>? statuses = null)
    {
        var pane = new EncounterBrowserPane(accounts, message => statuses?.Add(message));
        pane.Initialise(AdventureKit.CatalogDirectory, tokenDirectory);
        pane.SelectAccount(AccountRole.Main);
        pane.Session!.ChoiceResolverFactory = () => ChoiceResolvers.AlwaysAsk;
        return pane;
    }

    [Fact]
    public async Task Pane_initialises_browser_and_session_against_vendor_catalog()
    {
        var root = Directory.CreateTempSubdirectory("kolibri-pane-");
        try
        {
            await using var accounts = DesktopKit.Accounts(new FakeKol());
            var pane = new EncounterBrowserPane(accounts, _ => { });
            pane.Initialise(AdventureKit.CatalogDirectory, root.FullName);
            Assert.True(pane.Browser!.Locations.Count > 100);
            Assert.Null(pane.Session); // a session exists once an account is selected
            pane.SelectAccount(AccountRole.Main);
            Assert.NotNull(pane.Session);
            pane.Dispose();
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task Pane_persists_and_loads_token_via_its_own_store()
    {
        var root = Directory.CreateTempSubdirectory("kolibri-pane-");
        try
        {
            await using var accounts = DesktopKit.Accounts(new FakeKol());
            using (var pane = Pane(accounts, root.FullName))
            {
                pane.Session!.ApprovalToken = "manual-token";
                await pane.PersistTokenAsync("pane-test");
            }
            Assert.True(File.Exists(Path.Combine(root.FullName, "manual-approval-token.json")));

            using var pane2 = Pane(accounts, root.FullName);
            await pane2.LoadStoredTokenAsync();
            Assert.Equal("manual-token", pane2.Session!.ApprovalToken);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task Pane_start_and_attack_go_through_the_session()
    {
        var root = Directory.CreateTempSubdirectory("kolibri-pane-");
        try
        {
            var kol = AdventureKit.FightKol(AdventureKit.FightWon);
            await using var accounts = DesktopKit.Accounts(kol, DesktopKit.Store(AccountRole.Main));
            var statuses = new List<string>();
            using var pane = Pane(accounts, root.FullName, statuses);
            pane.ApplyApprovalToken("allow-token");
            var row = pane.Browser!.Locations.First(r => r.Snarfblat == 543);

            await pane.StartAdventureAsync(row);
            Assert.True(pane.Session!.IsSessionActive, pane.Session.LastMessage);
            var attack = pane.Browser.Actions.First(a => a.Kind == "attack");
            await pane.SubmitStepAsync(attack);

            Assert.Equal(2, pane.Session.StepLog.Count);
            Assert.False(pane.Session.IsSessionActive);
            Assert.StartsWith("Adventure complete (Won)", statuses[^1]);
            Assert.Equal(new[] { "/adventure.php", "/fight.php" }, kol.Posts().Select(p => p.Path));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task Special_route_locations_are_refused_without_a_request()
    {
        var root = Directory.CreateTempSubdirectory("kolibri-pane-");
        try
        {
            var kol = new FakeKol();
            await using var accounts = DesktopKit.Accounts(kol, DesktopKit.Store(AccountRole.Main));
            var statuses = new List<string>();
            using var pane = Pane(accounts, root.FullName, statuses);
            pane.ApplyApprovalToken("allow-token");

            await pane.StartAdventureAsync(new EncounterBrowserLocationRow("friars", "Friars", "Zone", 0, 1, 0, 0));

            Assert.Contains("special route", statuses[^1]);
            Assert.Empty(kol.Requests);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task A_signed_out_account_reports_instead_of_sending()
    {
        var root = Directory.CreateTempSubdirectory("kolibri-pane-");
        try
        {
            var kol = AdventureKit.FightKol();
            await using var accounts = DesktopKit.Accounts(kol); // no stored account
            var statuses = new List<string>();
            using var pane = Pane(accounts, root.FullName, statuses);
            pane.ApplyApprovalToken("allow-token");

            await pane.StartAdventureAsync(pane.Browser!.Locations.First(r => r.Snarfblat == 543));

            Assert.Contains("Log in again", statuses[^1]);
            Assert.Empty(kol.Requests);
        }
        finally { root.Delete(recursive: true); }
    }
}
