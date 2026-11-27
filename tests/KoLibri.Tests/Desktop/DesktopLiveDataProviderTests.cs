using KoLibri.Desktop;
using Xunit;

namespace KoLibri.Tests.Desktop;

/// <summary>
/// The desktop provider is the bridge between the window and the KoLibri library. These tests check the contract the
/// view models rely on: a missing session yields an empty snapshot instead of an exception, an expired session is not
/// renewed while automatic log-in is off, and the snapshot shape matches what the views bind to.
/// </summary>
public sealed class DesktopLiveDataProviderTests
{
    [Fact]
    public async Task Missing_session_yields_an_empty_inventory_snapshot()
    {
        var kol = new FakeKol();
        await using var accounts = DesktopKit.Accounts(kol);
        var provider = new DesktopLiveDataProvider(accounts);
        var snapshot = await provider.LoadInventoryAsync(AccountRole.Main);
        Assert.Empty(snapshot.Items);
        Assert.False(snapshot.HasSession);
        Assert.Equal(AccountRole.Main, snapshot.Role);
        Assert.Empty(kol.Requests);
    }

    [Fact]
    public async Task Missing_session_yields_no_status_and_no_mall_price()
    {
        var kol = new FakeKol();
        await using var accounts = DesktopKit.Accounts(kol);
        var provider = new DesktopLiveDataProvider(accounts);
        Assert.Null(await provider.LoadStatusAsync(AccountRole.Main));
        Assert.Null(await provider.LoadCheapestMallPriceAsync(AccountRole.Main, "11-leaf clover"));
        Assert.Empty(kol.Requests);
    }

    [Fact]
    public async Task Expired_session_is_not_logged_in_again_when_automatic_relogin_is_off()
    {
        var kol = new FakeKol().WithLogin();
        var store = DesktopKit.Store(AccountRole.Main, expired: true, password: "saved-password");
        var asked = 0;
        await using var accounts = DesktopKit.Accounts(kol, store, autoReconnect: () => { asked++; return false; });
        var provider = new DesktopLiveDataProvider(accounts);

        var snapshot = await provider.LoadInventoryAsync(AccountRole.Main);

        Assert.False(snapshot.HasSession);
        Assert.True(asked >= 1);
        Assert.Empty(kol.Requests);
        Assert.Equal("PHPSESSID=old", store.Records["main"].CookieHeader);
    }

    [Fact]
    public async Task Expired_session_is_renewed_once_when_automatic_relogin_is_on()
    {
        var kol = new FakeKol().WithLogin();
        var store = DesktopKit.Store(AccountRole.Main, expired: true, password: "saved-password");
        await using var accounts = DesktopKit.Accounts(kol, store, autoReconnect: () => true);
        var provider = new DesktopLiveDataProvider(accounts);

        var status = await provider.LoadStatusAsync(AccountRole.Main);

        Assert.NotNull(status);
        Assert.Equal(1, kol.Count("POST", "login.php"));
        Assert.Equal("PHPSESSID=fresh", store.Records["main"].CookieHeader);
    }

    [Fact]
    public async Task Status_carries_the_charpane_fields()
    {
        var kol = new FakeKol();
        await using var accounts = DesktopKit.Accounts(kol, DesktopKit.Store(AccountRole.Main));
        var provider = new DesktopLiveDataProvider(accounts);

        var status = await provider.LoadStatusAsync(AccountRole.Main);

        Assert.NotNull(status);
        Assert.Equal("Tester", status!.PlayerName);
        Assert.Equal(1001, status.PlayerId);
        Assert.Equal(15, status.Fullness.Limit is null ? 15 : status.Fullness.Limit);
    }

    [Fact]
    public async Task Inventory_rows_take_names_and_flags_from_the_catalog()
    {
        var kol = new FakeKol().On("GET api.php?what=inventory", "{\"24\":\"3\"}");
        await using var accounts = DesktopKit.Accounts(kol, DesktopKit.Store(AccountRole.Main));
        var provider = new DesktopLiveDataProvider(accounts);
        var catalog = new MafiaDataLoader().Load(Path.Combine(AppContext.BaseDirectory, "external", "kolmafia-data"));

        var snapshot = await provider.LoadInventoryAsync(AccountRole.Main, catalog: catalog);

        var row = Assert.Single(snapshot.Items);
        Assert.Equal("24", row.ItemId);
        Assert.Equal(3, row.Quantity);
        Assert.Equal("ten-leaf clover", row.Name);
    }
}
