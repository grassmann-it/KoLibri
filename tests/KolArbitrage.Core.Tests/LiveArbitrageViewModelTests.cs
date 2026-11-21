using KoLibri.Desktop;
using Xunit;

namespace KolArbitrage.Core.Tests;

/// <summary>
/// The arbitrage table and the guts screen are the two panels that show
/// live numbers. The tests pin the projections so a change in the shape
/// of the data shows up here and not in the window.
/// </summary>
public sealed class LiveArbitrageViewModelTests
{
    private static LiveArbitrageViewModel Build(long? median, out LiveArbitrageViewModel captured)
    {
        captured = new LiveArbitrageViewModel(
            provider: new DesktopLiveDataProvider(
                new KolArbitrage.Core.Accounts.AccountSessionManager(
                    new InMemoryAccountStore(), new InMemorySecretStore())),
            medianLookup: _ => Task.FromResult(median));
        return captured;
    }

    [Fact]
    public void Without_a_median_the_spread_stays_empty()
    {
        var viewModel = Build(null, out _);
        Assert.Empty(viewModel.Rows);
    }

    [Fact]
    public void Summary_counts_items_and_profitable_ones()
    {
        var viewModel = Build(null, out _);
        Assert.Equal("No listings loaded.", viewModel.SummaryText);
    }
}

/// <summary>
/// The guts screen shows the account cards and the own store. Both come
/// from the same session, so a missing session means an empty screen
/// rather than a broken one.
/// </summary>
public sealed class LiveGutsViewModelTests
{
    [Fact]
    public async Task Missing_session_marks_both_accounts_signed_out_and_shows_no_listings()
    {
        var viewModel = new LiveGutsViewModel(
            provider: new DesktopLiveDataProvider(
                new KolArbitrage.Core.Accounts.AccountSessionManager(
                    new InMemoryAccountStore(), new InMemorySecretStore())));
        await viewModel.LoadAsync();
        Assert.Equal(2, viewModel.Accounts.Count);
        Assert.All(viewModel.Accounts, account => Assert.False(account.HasStatus));
        Assert.Empty(viewModel.StoreRows);
        Assert.Contains("0 of 2 accounts available", viewModel.GutsSummary);
    }
}
