using KoLibri.Desktop;
using Xunit;

namespace KoLibri.Tests.Desktop;

/// <summary>
/// The arbitrage table (experimental) shows live numbers. The tests pin the projections so a change in the shape of
/// the data shows up here and not in the window.
/// </summary>
public sealed class LiveArbitrageViewModelTests
{
    [Fact]
    public async Task Without_a_session_the_table_stays_empty()
    {
        var kol = new FakeKol();
        await using var accounts = DesktopKit.Accounts(kol);
        var viewModel = new LiveArbitrageViewModel(AccountRole.Main, new DesktopLiveDataProvider(accounts), _ => Task.FromResult<long?>(null));
        Assert.Equal("No listings loaded.", viewModel.SummaryText);
        await viewModel.LoadAsync();
        Assert.Empty(viewModel.Rows);
        Assert.Empty(kol.Requests);
    }

    [Fact]
    public async Task Rows_show_the_spread_against_the_median_and_rules_plan_the_buys()
    {
        var kol = MallKit.Kol(25_000);
        kol.Meat = 1_100_000;
        await using var accounts = DesktopKit.Accounts(kol, DesktopKit.Store(AccountRole.Main));
        var viewModel = new LiveArbitrageViewModel(AccountRole.Main, new DesktopLiveDataProvider(accounts),
            name => Task.FromResult<long?>(name == "11-leaf clover" ? 30_000 : null));
        viewModel.WatchList.Clear();
        viewModel.WatchList.Add("11-leaf clover");

        await viewModel.LoadAsync();

        var row = Assert.Single(viewModel.Rows);
        Assert.Equal(("11-leaf clover", 25_000L, 30_000L, 1), (row.ItemName, row.CheapestPrice, row.MedianPrice, row.ListingCount));
        Assert.Equal("5,000 Meat", row.SpreadText);
        Assert.True(row.IsProfitable);
        Assert.Equal("1 items · 1 listings · 1 profitable", viewModel.SummaryText);
        // The clover rule (ceiling 26,900, resale 32,995) takes the 25,000 listing with the 100,000 Meat above its reserve.
        var clover = viewModel.Rules.Single(r => r.ItemName == "11-leaf clover");
        Assert.Equal(1, clover.CandidateCount);
        Assert.Equal(100_000, clover.PlannedCost);
        Assert.Equal(4 * (32_995 - 25_000), clover.PlannedProfit);
    }

    [Fact]
    public void Buy_planner_keeps_the_rule_ceiling_daily_cap_and_meat_reserve()
    {
        var rule = BuyPlanner.DefaultRules[0];
        var plan = new BuyPlanner().Plan(rule,
        [
            new MallListingRow { StoreId = 1, ItemId = rule.ItemId, StoreName = "a", Stock = 50, UnitPrice = 25_000, DailyLimit = 1 },
            new MallListingRow { StoreId = 2, ItemId = rule.ItemId, StoreName = "b", Stock = 50, UnitPrice = 26_000 },
            new MallListingRow { StoreId = 3, ItemId = rule.ItemId, StoreName = "c", Stock = 50, UnitPrice = 30_000 }
        ], availableMeat: rule.MeatReserve + 100_000);
        Assert.Equal(new[] { 1, 2 }, plan.Candidates.Select(c => c.StoreId).ToArray());
        Assert.Equal(1, plan.Candidates[0].Quantity);          // the daily limit of store 1
        Assert.Equal(2, plan.Candidates[1].Quantity);          // 75,000 left / 26,000
        Assert.Equal(25_000 + 2 * 26_000, plan.TotalCost);
    }
}
