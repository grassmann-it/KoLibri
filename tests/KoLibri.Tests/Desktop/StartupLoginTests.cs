using KoLibri.Desktop;
using Xunit;

namespace KoLibri.Tests.Desktop;

public sealed class StartupLoginTests
{
    [Fact]
    public void Automatic_login_is_on_by_default_and_old_files_without_the_key_get_it()
    {
        Assert.True(new DesktopSettings().ReloginAutomatically);
        var path = Path.Combine(Path.GetTempPath(), "kolibri-g1-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"theme\":\"Dark\"}");
            Assert.True(new DesktopSettingsStore(path).Load().ReloginAutomatically);
            File.WriteAllText(path, "{\"reloginAutomatically\":false}");
            Assert.False(new DesktopSettingsStore(path).Load().ReloginAutomatically);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Valid_session_makes_no_request()
    {
        var kol = new FakeKol().WithLogin();
        await using var accounts = DesktopKit.Accounts(kol, DesktopKit.Store(AccountRole.Main));
        var r = await StartupLogin.RunAsync(accounts, AccountRole.Main, true);
        Assert.Equal(StartupLoginOutcome.AlreadySignedIn, r.Outcome);
        Assert.Empty(kol.Requests);
    }

    [Fact]
    public async Task Stale_session_with_password_logs_in_once_when_enabled()
    {
        var kol = new FakeKol().WithLogin();
        var store = DesktopKit.Store(AccountRole.Main, expired: true);
        await using var accounts = DesktopKit.Accounts(kol, store);
        var r = await StartupLogin.RunAsync(accounts, AccountRole.Main, true);
        Assert.Equal(StartupLoginOutcome.LoggedIn, r.Outcome);
        Assert.Equal(1, kol.Count("POST", "login.php"));
        Assert.Equal("PHPSESSID=fresh", store.Records["main"].CookieHeader);
    }

    [Fact]
    public async Task Stale_session_is_left_alone_when_disabled_or_without_password()
    {
        var kol = new FakeKol().WithLogin();
        await using var off = DesktopKit.Accounts(kol, DesktopKit.Store(AccountRole.Main, expired: true));
        Assert.Equal(StartupLoginOutcome.AutoLoginOff, (await StartupLogin.RunAsync(off, AccountRole.Main, false)).Outcome);
        await using var none = DesktopKit.Accounts(kol, DesktopKit.Store(AccountRole.Main, expired: true, password: null));
        Assert.Equal(StartupLoginOutcome.NothingStored, (await StartupLogin.RunAsync(none, AccountRole.Main, true)).Outcome);
        Assert.Equal(0, kol.Count("POST", "login.php"));
    }

    [Fact]
    public async Task Failed_login_is_one_attempt_and_the_message_hides_the_exception()
    {
        var kol = new FakeKol().WithLogin(fail: true);
        await using var accounts = DesktopKit.Accounts(kol, DesktopKit.Store(AccountRole.Main, expired: true));
        var r = await StartupLogin.RunAsync(accounts, AccountRole.Main, true);
        Assert.Equal(StartupLoginOutcome.Failed, r.Outcome);
        Assert.Equal(1, kol.Count("POST", "login.php"));
        Assert.DoesNotContain("secret detail", r.Message);
        Assert.Contains("Sign in", r.Message);
    }

    [Fact]
    public async Task RunAll_handles_selected_role_first_then_the_other()
    {
        var kol = new FakeKol().WithLogin();
        await using var accounts = DesktopKit.Accounts(kol, DesktopKit.Store(AccountRole.Mallbot, expired: true));
        var order = new List<AccountRole>();
        var all = await StartupLogin.RunAllAsync(accounts, AccountRole.Main, true, r => order.Add(r.Role));
        Assert.Equal([AccountRole.Main, AccountRole.Mallbot], order);
        Assert.Equal(StartupLoginOutcome.NothingStored, all[0].Outcome);
        Assert.Equal(StartupLoginOutcome.LoggedIn, all[1].Outcome);
    }

    [Fact]
    public async Task Browser_gate_offers_sign_in_without_an_account_and_renews_only_when_allowed()
    {
        var kol = new FakeKol().WithLogin();
        await using var empty = DesktopKit.Accounts(kol);
        var none = await BrowserGate.CheckAsync(empty, AccountRole.Main, true);
        Assert.False(none.CanStart);
        Assert.True(none.OfferSignIn);
        Assert.Contains("not signed in", none.Message);

        await using var accounts = DesktopKit.Accounts(kol, DesktopKit.Store(AccountRole.Main, expired: true));
        var off = await BrowserGate.CheckAsync(accounts, AccountRole.Main, false);
        Assert.False(off.CanStart);
        Assert.Equal(0, kol.Count("POST", "login.php"));
        var on = await BrowserGate.CheckAsync(accounts, AccountRole.Main, true);
        Assert.True(on.CanStart);
        Assert.Equal(1, kol.Count("POST", "login.php"));
    }
}
