using System.Net;
using KolArbitrage.Core.Accounts;
using KolArbitrage.Core.Daily;
using KolArbitrage.Core.Integration;
using Xunit;

namespace KolArbitrage.Core.Tests;

public sealed class ItemArbitrageTests
{
    [Fact]
    public void Max_price_is_the_median_minus_the_margin_and_needs_enough_sales()
    {
        long[] sales = { 25_000, 30_000, 30_000, 32_500, 32_500 };
        Assert.Equal(30_000, ArbitrageRule.Median(sales));
        Assert.Equal(27_000, ArbitrageRule.MaxBuyPrice(sales, 0.10));
        Assert.Null(ArbitrageRule.MaxBuyPrice(new long[] { 1, 2, 3, 4 }, 0.10));
        Assert.Null(ArbitrageRule.MaxBuyPrice(sales, 1.0));
        Assert.Null(ArbitrageRule.MaxBuyPrice(sales, -0.1));
        Assert.Equal(25_000, ArbitrageRule.Median(new long[] { 10, 20, 30, 40, 50, 60 }) is 35 ? 25_000 : 0);
    }

    private static readonly AccountCredentials Credentials = new() { CookieHeader = "session=1" };

    private sealed class FakeMall(params MallListing[] listings) : IMallReadClient
    {
        public Task<MallSnapshot> SearchAsync(string itemId, bool forceRefresh = false, CancellationToken cancellationToken = default)
            => Task.FromResult(new MallSnapshot(itemId, listings, DateTimeOffset.UtcNow, new Uri("https://kol.test/mall.php")));
        public Task<IReadOnlyList<OwnShopListing>> FetchOwnShopAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OwnShopListing>>(Array.Empty<OwnShopListing>());
    }

    private static MallListing Listing(long price, int quantity = 10)
        => new("10881", "4711", "Seller", price, quantity, null, DateTimeOffset.UtcNow, new Uri("https://kol.test/mall.php"));

    private sealed class FakeStore(string buyResponse) : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.Method + " " + request.RequestUri!.PathAndQuery);
            var body = request.Method == HttpMethod.Get
                ? "<input type=hidden name=\"pwd\" value=\"storehash\">"
                : buyResponse;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private static BaconArbitrageExecutor Executor(FakeStore store, params MallListing[] listings)
        => new(new MallBuyClient(new HttpClient(store), new Uri("https://kol.test/")), new FakeMall(listings));

    [Fact]
    public async Task A_confirmed_buy_below_the_ceiling_counts_and_reports_the_spend()
    {
        var store = new FakeStore("You spent 25,000 Meat. You acquire an item: 11-leaf clover");
        var outcome = await Executor(store, Listing(25_000)).TopUpItemAsync(
            Credentials, 10881, 1, availableMeat: 100_000, maxPricePerUnit: 27_000, meatReserve: 10_000, itemName: "11-leaf clover");

        Assert.True(outcome.Bought);
        Assert.Equal(1, outcome.Quantity);
        Assert.Equal(25_000, outcome.Spent);
        Assert.Contains(store.Requests, r => r.StartsWith("POST /mallstore.php"));
    }

    [Fact]
    public async Task A_refused_buy_reports_nothing_bought_and_never_pretends_otherwise()
    {
        var store = new FakeStore("<html>This store doesn't have that item at that price.</html>");
        var outcome = await Executor(store, Listing(25_000)).TopUpItemAsync(
            Credentials, 10881, 2, 100_000, 27_000, 10_000, itemName: "11-leaf clover");

        Assert.False(outcome.Bought);
        Assert.Equal(0, outcome.Quantity);
        Assert.Equal(0, outcome.Spent);
        Assert.Equal("NotBought", outcome.Verdict);
    }

    [Fact]
    public async Task A_listing_above_the_ceiling_is_never_bought()
    {
        var store = new FakeStore("You acquire an item");
        var outcome = await Executor(store, Listing(29_029)).TopUpItemAsync(
            Credentials, 10881, 1, 100_000, 27_000, 10_000, itemName: "11-leaf clover");

        Assert.False(outcome.Bought);
        Assert.DoesNotContain(store.Requests, r => r.StartsWith("POST"));
    }

    [Fact]
    public async Task The_meat_reserve_limits_the_quantity()
    {
        var store = new FakeStore("You acquire an item");
        var outcome = await Executor(store, Listing(25_000)).TopUpItemAsync(
            Credentials, 10881, 5, availableMeat: 129_000, maxPricePerUnit: 27_000, meatReserve: 50_000, itemName: "11-leaf clover", maxRounds: 1);

        // 129,000 - 50,000 reserve leaves room for three clovers at 25,000.
        Assert.Equal(3, outcome.Quantity);
        Assert.Equal(75_000, outcome.Spent);
    }
}
