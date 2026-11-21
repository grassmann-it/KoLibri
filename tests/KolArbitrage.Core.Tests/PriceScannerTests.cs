using Xunit;
using KolArbitrage.Core.Spiel;

namespace KolArbitrage.Core.Tests;

public class PriceScannerTests
{
    private const string MallPage = """
        <html><body>
        <a name="results_10881"></a>
        <table>
        <tr id="stock_56226_10881">
          <td class="small store"><a href="mallstore.php?whichstore=56226&searchitem=10881&searchprice=25000"><b>CrazyGordon's Discount Outlet</b></a></td>
          <td class="small stock">1,591</td>
          <td class="small">1&nbsp;/&nbsp;day</td>
          <td class="small price"><a href="x">25,000&nbsp;Meat</a></td>
        </tr>
        <tr id="stock_999_8691">
          <td class="small store"><a href="mallstore.php?whichstore=999&searchitem=8691&searchprice=15000"><b>EBT Accepted</b></a></td>
          <td class="small stock">1</td>
          <td class="small"></td>
          <td class="small price"><a href="x">15,000&nbsp;Meat</a></td>
        </tr>
        </table>
        </body></html>
        """;

    [Fact]
    public void ReadsTheListingsBehindTheSearchAnchor()
    {
        var rows = PriceScanner.ReadMallListings(MallPage, 10881);

        var row = Assert.Single(rows);
        Assert.Equal(56226, row.StoreId);
        Assert.Equal(10881, row.ItemId);
        Assert.Equal(1591, row.Stock);
        Assert.Equal(25000, row.UnitPrice);
        Assert.Equal("CrazyGordon's Discount Outlet", row.StoreName);
    }

    [Fact]
    public void ReadsTheDailyLimitAndBoundsTheAmountAvailableToday()
    {
        var row = Assert.Single(PriceScanner.ReadMallListings(MallPage, 10881));

        Assert.True(row.HasDailyLimit);
        Assert.Equal(1, row.DailyLimit);
        // The seller has 1.591 in stock but sells one per day.
        Assert.Equal(1, row.AvailableToday);
    }

    [Fact]
    public void AListingWithoutALimitIsBoundedByItsStock()
    {
        var row = Assert.Single(PriceScanner.ReadMallListings(MallPage, 8691));

        Assert.False(row.HasDailyLimit);
        Assert.Equal(1, row.AvailableToday);
    }

    [Fact]
    public void ReadsThePriceOutOfTheStoreLinkAndNotOutOfTheRowId()
    {
        // The store id is 56226 and the price is 25.000 — a reader that
        // took the first number of the row would report 56226.
        var row = Assert.Single(PriceScanner.ReadMallListings(MallPage, 10881));

        Assert.Equal(25000, row.UnitPrice);
        Assert.NotEqual(56226, row.UnitPrice);
    }

    [Fact]
    public void SkipsTheRowsBeforeTheAnchor()
    {
        var page = """
            <html><body>
            <table><tr id="stock_1_555"><td class="small price">9,999 Meat</td></tr></table>
            <a name="results_10881"></a>
            <table><tr id="stock_2_10881"><td class="small store"><a href="mallstore.php?whichstore=2&searchitem=10881&searchprice=25000"><b>Store</b></a></td><td class="small stock">5</td><td class="small price">25,000 Meat</td></tr></table>
            </body></html>
            """;

        var rows = PriceScanner.ReadMallListings(page, 10881);

        var row = Assert.Single(rows);
        Assert.Equal(2, row.StoreId);
    }

    [Fact]
    public void ReadsThePricegunHistoryOutOfTheTurboStream()
    {
        var page = """
            <script>self.__next_f.push([1,"...\"history\",[115,121],{\"_15\":16},[\"D\",1793923200000],2233,\"price\",{\"_9\":120},\"30302.27\",{\"_15\":16},[\"D\",1794009600000],5363,\"price\",{\"_9\":125},\"32181.43\"..."])</script>
            """;

        var prices = PriceScanner.ReadPricegunHistory(page);

        Assert.Equal(2, prices.Count);
        Assert.Equal(30302, prices[0]);
        Assert.Equal(32181, prices[1]);
    }

    [Fact]
    public void AnEmptyPageYieldsNoListings()
    {
        Assert.Empty(PriceScanner.ReadMallListings("", 10881));
        Assert.Empty(PriceScanner.ReadMallListings("<html>no anchor</html>", 10881));
        Assert.Empty(PriceScanner.ReadPricegunHistory(""));
    }

    [Fact]
    public void TakesTheMedianOfThePriceHistory()
    {
        Assert.Equal(20, PriceScanner.Median(new[] { 30, 10, 20 }));
        // An even number of points takes the mean of the middle two.
        Assert.Equal(15, PriceScanner.Median(new[] { 10, 20 }));
        Assert.Equal(0, PriceScanner.Median(Array.Empty<int>()));
    }
}

public class BuyPlannerTests
{
    private static MallListingRow Row(int store, int price, int stock, int limit = 0)
        => new()
        {
            StoreId = store,
            ItemId = 10881,
            StoreName = $"store {store}",
            Stock = stock,
            UnitPrice = price,
            DailyLimit = limit
        };

    [Fact]
    public void BuysTheCheapListingsAndLeavesTheExpensiveOnesAlone()
    {
        var rule = new BuyRule
        {
            ItemId = 10881,
            ItemName = "11-leaf clover",
            MaximumPrice = 26_900,
            ResalePrice = 32_995
        };
        var planner = new BuyPlanner();

        var plan = planner.Plan(rule, new[]
        {
            Row(1, 25_000, 1_591),
            Row(2, 30_000, 29),
            Row(3, 33_000, 134)
        }, 10_000_000);

        var candidate = Assert.Single(plan.Candidates);
        Assert.Equal(1, candidate.StoreId);
        Assert.Equal(25_000, candidate.UnitPrice);
        // The rule caps the amount per day, so a seller with 1.591 in
        // stock only sells 40.
        Assert.Equal(40, candidate.Quantity);
        Assert.Equal(7995, candidate.ProfitPerUnit);
    }

    [Fact]
    public void BuysAListingWithADailyLimitBecauseTheFlipIsWorthIt()
    {
        var rule = new BuyRule
        {
            ItemId = 10881,
            ItemName = "11-leaf clover",
            MaximumPrice = 26_900,
            ResalePrice = 32_995,
            BuyDespiteDailyLimit = true
        };
        var planner = new BuyPlanner();

        var plan = planner.Plan(rule, new[] { Row(1, 25_000, 1_591, 1) }, 10_000_000);

        var candidate = Assert.Single(plan.Candidates);
        Assert.Equal(1, candidate.Quantity);
        Assert.Equal(1, candidate.DailyLimit);
    }

    [Fact]
    public void RespectsTheMeatReserve()
    {
        var rule = new BuyRule
        {
            ItemId = 10881,
            ItemName = "11-leaf clover",
            MaximumPrice = 26_900,
            ResalePrice = 32_995,
            MeatReserve = 1_000_000
        };
        var planner = new BuyPlanner();

        // 1.020.000 meat, 1.000.000 of it reserved: 20.000 left, and a
        // clover at 25.000 does not fit into it.
        var plan = planner.Plan(rule, new[] { Row(1, 25_000, 1_591) }, 1_020_000);

        Assert.Empty(plan.Candidates);
    }

    [Fact]
    public void SpendsOnlyWhatIsLeftAfterTheReserve()
    {
        var rule = new BuyRule
        {
            ItemId = 10881,
            ItemName = "11-leaf clover",
            MaximumPrice = 26_900,
            ResalePrice = 32_995,
            MeatReserve = 1_000_000
        };
        var planner = new BuyPlanner();

        // 1.100.000 meat: 100.000 left over the reserve, four clovers fit.
        var plan = planner.Plan(rule, new[] { Row(1, 25_000, 1_591) }, 1_100_000);

        var candidate = Assert.Single(plan.Candidates);
        Assert.Equal(4, candidate.Quantity);
        Assert.Equal(100_000, plan.TotalCost);
    }

    [Fact]
    public void TheDefaultRulesMatchTheOperatorsIntent()
    {
        var clover = BuyPlanner.DefaultRules.Single(x => x.ItemId == 10881);
        var bribes = BuyPlanner.DefaultRules.Single(x => x.ItemId == 8691);

        // The clover is bought below the price the arbitrage runs to.
        Assert.Equal(26_900, clover.MaximumPrice);
        Assert.True(clover.BuyDespiteDailyLimit);
        // The bribes are bought under 20.000 meat.
        Assert.Equal(20_000, bribes.MaximumPrice);
    }
}
