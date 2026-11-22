using System.Net;
using KoLibri.Core.Basis;
using KoLibri.Core.Markt;
using KoLibri.Desktop;
using Xunit;

namespace KoLibri.Core.Tests;

/// <summary>Mall receipts, the daily-limit retry and mall read settings (money-path package W-D).</summary>
public sealed class MallReceiptAndBudgetTests
{
    private static readonly AccountCredentials Credentials = new() { CookieHeader = "PHPSESSID=x" };

    private static MallListing Listing(string store, long price, int quantity = 50, string itemId = "8763")
        => new(itemId, store, "Seller", price, quantity, null, DateTimeOffset.UtcNow, new Uri("https://kol.test/mall.php"));

    private sealed class Mall(params MallListing[] listings) : IMallReadClient
    {
        public Task<MallSnapshot> SearchAsync(string itemId, bool forceRefresh = false, CancellationToken cancellationToken = default)
            => Task.FromResult(new MallSnapshot(itemId, listings, DateTimeOffset.UtcNow, new Uri("https://kol.test/mall.php")));
        public Task<IReadOnlyList<OwnShopListing>> FetchOwnShopAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OwnShopListing>>(Array.Empty<OwnShopListing>());
    }

    /// <summary>Answers store pages with a hash and purchase POSTs from a script, one answer per POST.</summary>
    private sealed class Store(params Func<string, HttpResponseMessage>[] posts) : HttpMessageHandler
    {
        public List<string> Forms { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<input type=hidden name=\"pwd\" value=\"h\">") };
            var form = await request.Content!.ReadAsStringAsync(cancellationToken);
            Forms.Add(form);
            return posts[Math.Min(Forms.Count, posts.Length) - 1](form);
        }
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static BaconArbitrageExecutor Executor(Store store, params MallListing[] listings)
        => new(new MallBuyClient(new HttpClient(store), new Uri("https://kol.test/")), new Mall(listings));

    [Fact]
    public async Task A_503_on_the_reduced_retry_stops_the_run_and_keeps_the_spend_unclear()
    {
        var store = new Store(
            _ => Ok("You may only buy 2 of this item per day from this store."),
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("busy") });
        var outcome = await Executor(store, Listing("111", 1_000), Listing("222", 1_100)).TopUpItemAsync(
            Credentials, 8763, 5, 10_000_000, 1_500, 1_000, itemName: "BACON");

        Assert.True(outcome.Unclear);
        Assert.Equal(0, outcome.Quantity);
        Assert.Equal(2_000, outcome.UnclearSpend);              // worst case of the reduced POST
        Assert.Equal(2, store.Forms.Count);                      // the second merchant was never asked
    }

    [Fact]
    public async Task A_transport_failure_on_the_reduced_retry_is_unclear_too()
    {
        var store = new Store(
            _ => Ok("You may only buy 2 of this item per day from this store."),
            _ => throw new HttpRequestException("reset"));
        var outcome = await Executor(store, Listing("111", 1_000), Listing("222", 1_100)).TopUpItemAsync(
            Credentials, 8763, 5, 10_000_000, 1_500, 1_000, itemName: "BACON");
        Assert.True(outcome.Unclear);
        Assert.Equal(2, store.Forms.Count);
    }

    [Fact]
    public async Task A_partial_receipt_books_only_the_confirmed_part_and_moves_on()
    {
        var store = new Store(
            _ => Ok("You spent 2,000 Meat. You acquire an item: <b>BACON</b> (2)"),
            f => Ok("You spent 3,300 Meat. You acquire an item: <b>BACON</b> (3)"));
        var outcome = await Executor(store, Listing("111", 1_000), Listing("222", 1_100)).TopUpItemAsync(
            Credentials, 8763, 5, 10_000_000, 1_500, 1_000, itemName: "BACON");

        Assert.False(outcome.Unclear);
        Assert.Equal(5, outcome.Quantity);                       // 2 confirmed + 3 at the next store
        Assert.Equal(5_300, outcome.Spent);
        Assert.True(outcome.Purchases[0].Partial);
        Assert.Equal(5, outcome.Purchases[0].RequestedQuantity);
        Assert.Equal(2, outcome.Purchases[0].Quantity);
    }

    [Theory]
    [InlineData("You spent 1,500 Meat. You acquire an item: BACON (5)")]            // meat is not 5 x 1,000
    [InlineData("You spent 5,000 Meat. You acquire an item: BACON (3)")]            // meat and count disagree
    [InlineData("You spent 6,000 Meat. You acquire an item: BACON (6)")]            // more than ordered
    [InlineData("You acquire an item: <a href='x?itemid=999'>other</a> (5)")]       // a different item
    public async Task A_receipt_that_contradicts_the_order_is_unclear_not_accepted(string body)
    {
        var store = new Store(_ => Ok(body));
        var outcome = await Executor(store, Listing("111", 1_000)).TopUpItemAsync(
            Credentials, 8763, 5, 10_000_000, 1_500, 1_000, itemName: "BACON", singlePurchase: true);
        Assert.True(outcome.Unclear);
        Assert.False(outcome.Bought);
        Assert.Equal(5_000, outcome.UnclearSpend);
    }

    [Fact]
    public async Task Offers_of_another_item_from_a_partial_name_match_are_never_bought()
    {
        var store = new Store(f => Ok("You spent 2,000 Meat. You acquire an item: BACON (2)"));
        var cheaperOther = Listing("999", 10, itemId: "5555");
        var outcome = await Executor(store, cheaperOther, Listing("111", 1_000)).TopUpItemAsync(
            Credentials, 8763, 2, 10_000_000, 1_500, 1_000, itemName: "BACON");

        Assert.Equal(2, outcome.Quantity);
        Assert.Single(store.Forms);
        Assert.Contains("whichstore=111", store.Forms[0]);       // not the cheaper store of the other item
        Assert.Contains("whichitem=8763000001000", store.Forms[0]);
    }

    [Fact]
    public async Task Only_other_items_listed_means_no_listing()
    {
        var store = new Store(f => Ok("You acquire an item"));
        var outcome = await Executor(store, Listing("999", 10, itemId: "5555")).TopUpItemAsync(
            Credentials, 8763, 1, 10_000_000, 1_500, 1_000, itemName: "BACON");
        Assert.Equal("NotBought", outcome.Verdict);
        Assert.Empty(store.Forms);
    }

    [Fact]
    public void The_result_limit_setting_reaches_the_mall_policy()
    {
        var settings = new DesktopSettings();
        settings.Set("resultLimit", 7);
        settings.Set("cacheMinutes", 3);
        var policy = settings.ToMallPolicy();
        Assert.Equal(7, policy.MaxListingsPerItem);
        Assert.Equal(TimeSpan.FromMinutes(3), policy.CacheTtl);
        Assert.Equal(settings.MaxStale, policy.MaxStale);
        policy.Validate();
    }
}
