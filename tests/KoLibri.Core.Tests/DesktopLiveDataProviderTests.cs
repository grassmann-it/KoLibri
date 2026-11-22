using KoLibri.Desktop;
using Xunit;
using KoLibri.Core.Basis;

namespace KoLibri.Core.Tests;

/// <summary>
/// The desktop provider is the bridge between the window and KoL. It owns
/// the session handling, so these tests check the contract the view models
/// rely on: a missing session yields an empty snapshot instead of an
/// exception, and the snapshot shape matches what the views bind to.
/// </summary>
public sealed class DesktopLiveDataProviderTests
{
    [Fact]
    public async Task Missing_session_yields_an_empty_inventory_snapshot()
    {
        // An account store without any profile has no session, so the
        // provider must answer with an empty snapshot rather than throw.
        var provider = new DesktopLiveDataProvider(new AccountSessionManager(
            new InMemoryAccountStore(), new InMemorySecretStore()));
        var snapshot = await provider.LoadInventoryAsync(AccountRole.Main);
        Assert.Empty(snapshot.Items);
        Assert.Equal(AccountRole.Main, snapshot.Role);
    }

    [Fact]
    public async Task Missing_session_yields_no_daily_snapshot()
    {
        var provider = new DesktopLiveDataProvider(new AccountSessionManager(
            new InMemoryAccountStore(), new InMemorySecretStore()));
        var snapshot = await provider.LoadDailySnapshotAsync(
            AccountRole.Main, new KoLibri.Core.Spiel.DailyRoutineSnapshot());
        Assert.Null(snapshot);
    }

    [Fact]
    public async Task Missing_session_yields_no_mall_price()
    {
        var provider = new DesktopLiveDataProvider(new AccountSessionManager(
            new InMemoryAccountStore(), new InMemorySecretStore()));
        var price = await provider.LoadCheapestMallPriceAsync(AccountRole.Main, "11-leaf clover");
        Assert.Null(price);
    }

    [Fact]
    public async Task Expired_session_is_not_logged_in_again_when_automatic_relogin_is_off()
    {
        var accounts = new AccountSessionManager(new InMemoryAccountStore(), new InMemorySecretStore());
        await accounts.SetProfileAsync(new AccountProfile { Id = "main", DisplayName = "Fixture", Role = AccountRole.Main });
        await accounts.SetCredentialsAsync(AccountRole.Main, new AccountCredentials
        {
            Username = "Fixture", Password = "saved-password", CookieHeader = "old=1",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1)
        });
        var asked = 0;
        // Off: the reader is consulted and no login is attempted (a login would reach the network).
        using var provider = new DesktopLiveDataProvider(accounts, reloginAutomatically: () => { asked++; return false; });

        var snapshot = await provider.LoadInventoryAsync(AccountRole.Main);

        Assert.False(snapshot.HasSession);
        Assert.Equal(1, asked);
        Assert.Equal("old=1", (await accounts.GetSessionAsync(AccountRole.Main, includeExpired: true))!.Credentials.CookieHeader);
    }

    [Fact]
    public async Task Provider_without_a_relogin_reader_never_logs_in_again()
    {
        var accounts = new AccountSessionManager(new InMemoryAccountStore(), new InMemorySecretStore());
        await accounts.SetProfileAsync(new AccountProfile { Id = "main", DisplayName = "Fixture", Role = AccountRole.Main });
        await accounts.SetCredentialsAsync(AccountRole.Main, new AccountCredentials
        {
            Username = "Fixture", Password = "saved-password", CookieHeader = "old=1",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1)
        });
        using var provider = new DesktopLiveDataProvider(accounts);
        Assert.False((await provider.LoadInventoryAsync(AccountRole.Main)).HasSession);
    }
}
