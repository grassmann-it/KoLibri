using KolArbitrage.Core.Accounts;
using KoLibri.Desktop;
using Xunit;

namespace KolArbitrage.Core.Tests;

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
            AccountRole.Main, new KolArbitrage.Core.Daily.DailyRoutineSnapshot());
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
}
