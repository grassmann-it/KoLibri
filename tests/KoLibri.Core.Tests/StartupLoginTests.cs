using KoLibri.Core.Basis;
using KoLibri.Desktop;
using Xunit;

namespace KoLibri.Core.Tests;

public sealed class StartupLoginTests
{
    private static async Task<AccountSessionManager> Accounts(AccountRole role, AccountCredentials? credentials)
    {
        var accounts = new AccountSessionManager(new InMemoryAccountStore(), new InMemorySecretStore());
        if (credentials is null) return accounts;
        await accounts.SetProfileAsync(new AccountProfile { Id = role.ToString().ToLowerInvariant(), DisplayName = "Fixture", Role = role });
        await accounts.SetCredentialsAsync(role, credentials);
        return accounts;
    }

    private static AccountCredentials Expired(string? password = "pw") => new()
        { Username = "Fixture", Password = password, CookieHeader = "old=1", ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1) };

    private static (Func<string, string, CancellationToken, Task<KoLLoginResult>> Login, Func<int> Calls) FakeLogin(bool fail = false)
    {
        var calls = 0;
        return ((u, p, _) =>
        {
            Interlocked.Increment(ref calls);
            if (fail) throw new HttpRequestException("secret detail https://x");
            return Task.FromResult(new KoLLoginResult(u, new AccountCredentials
            { Username = u, CookieHeader = "new=1", ExpiresAt = DateTimeOffset.UtcNow.AddHours(5) }, DateTimeOffset.UtcNow));
        }, () => calls);
    }

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
        var accounts = await Accounts(AccountRole.Main, new AccountCredentials
            { Username = "Fixture", Password = "pw", CookieHeader = "ok=1", ExpiresAt = DateTimeOffset.UtcNow.AddHours(3) });
        var (login, calls) = FakeLogin();
        var r = await StartupLogin.RunAsync(accounts, AccountRole.Main, true, login);
        Assert.Equal(StartupLoginOutcome.AlreadySignedIn, r.Outcome);
        Assert.Equal(0, calls());
    }

    [Fact]
    public async Task Stale_session_with_password_logs_in_once_when_enabled()
    {
        var accounts = await Accounts(AccountRole.Main, Expired());
        var (login, calls) = FakeLogin();
        var r = await StartupLogin.RunAsync(accounts, AccountRole.Main, true, login);
        Assert.Equal(StartupLoginOutcome.LoggedIn, r.Outcome);
        Assert.Equal(1, calls());
        Assert.Equal("new=1", (await accounts.GetSessionAsync(AccountRole.Main))!.Credentials.CookieHeader);
    }

    [Fact]
    public async Task Stale_session_is_left_alone_when_disabled_or_without_password()
    {
        var (login, calls) = FakeLogin();
        var off = await StartupLogin.RunAsync(await Accounts(AccountRole.Main, Expired()), AccountRole.Main, false, login);
        Assert.Equal(StartupLoginOutcome.AutoLoginOff, off.Outcome);
        var none = await StartupLogin.RunAsync(await Accounts(AccountRole.Main, Expired(password: null)), AccountRole.Main, true, login);
        Assert.Equal(StartupLoginOutcome.NothingStored, none.Outcome);
        Assert.Equal(0, calls());
    }

    [Fact]
    public async Task Failed_login_is_one_attempt_and_the_message_hides_the_exception()
    {
        var accounts = await Accounts(AccountRole.Main, Expired());
        var (login, calls) = FakeLogin(fail: true);
        var r = await StartupLogin.RunAsync(accounts, AccountRole.Main, true, login);
        Assert.Equal(StartupLoginOutcome.Failed, r.Outcome);
        Assert.Equal(1, calls());
        Assert.DoesNotContain("secret detail", r.Message);
        Assert.Contains("Sign in", r.Message);
    }

    [Fact]
    public async Task RunAll_handles_selected_role_first_then_the_other()
    {
        var accounts = await Accounts(AccountRole.Mallbot, Expired());
        var (login, _) = FakeLogin();
        var order = new List<AccountRole>();
        var all = await StartupLogin.RunAllAsync(accounts, AccountRole.Main, true, login, r => order.Add(r.Role));
        Assert.Equal([AccountRole.Main, AccountRole.Mallbot], order);
        Assert.Equal(StartupLoginOutcome.NothingStored, all[0].Outcome);
        Assert.Equal(StartupLoginOutcome.LoggedIn, all[1].Outcome);
    }
}
