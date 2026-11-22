using Xunit;
using KoLibri.Core.Markt;

namespace KoLibri.Core.Tests;

/// <summary>
/// The mall parser has to keep up with KoL's markup. The rows carry the
/// store and the item id, and the searched item is marked with a
/// "results_<id>" anchor. A search by name used to drop every row,
/// because the filter compared the numeric row id against the search
/// term.
/// </summary>
public sealed class MallSearchParsingTests
{
    private const string SearchPage = """
        <center><b>Search Results:</b> <b>(Items 1-2 of 2)</b></center>
        <div id='searchresults'>
        <a name="searchresults"></a>
        <div class="jumplinks"><a href="#results_10881">11-leaf clover Results</a></div>
        <br /><a name="results_10881"></a>
        <table class="itemtable" summary="Stores selling 11-leaf clover">
        <tr class="blackabove" id="item_10881">
          <td><img src='x.gif' alt="11-leaf clover" title="11-leaf clover"></td>
          <td><b><a href="/mall.php?pudnuggler=%2211-leaf+clover%22">11-leaf clover</a></b></td>
        </tr>
        <tr class="graybelow" id="stock_56226_10881">
          <td style="border:none"></td>
          <td class="small store"><a href="mallstore.php?whichstore=56226"><b>Crazy Gordon</b></a></td>
          <td class="small stock">1,606</td>
          <td class="small">1&nbsp;/&nbsp;day</td>
          <td class="small price"><a href="mallstore.php?whichstore=56226">25,000&nbsp;Meat</a></td>
        </tr>
        <tr class="graybelow" id="stock_3566900_10881">
          <td style="border:none"></td>
          <td class="small store"><a href="mallstore.php?whichstore=3566900"><b>Brad's gut</b></a></td>
          <td class="small stock">110</td>
          <td class="small">1&nbsp;/&nbsp;day</td>
          <td class="small price"><a href="mallstore.php?whichstore=3566900">29,029&nbsp;Meat</a></td>
        </tr>
        </table>
        </div>
        """;

    private static IReadOnlyList<MallListing> Parse(string html, string searchTerm)
    {
        var method = typeof(KoLMallReadClient).GetMethod("ParseListings",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var client = new KoLMallReadClient(new HttpClient());
        return (IReadOnlyList<MallListing>)method.Invoke(client,
            new object?[] { searchTerm, html, new Uri("https://www.kingdomofloathing.com/mall.php"), null })!;
    }

    [Fact]
    public void A_search_by_name_keeps_the_rows_of_the_searched_item()
    {
        var listings = Parse(SearchPage, "11-leaf clover");
        Assert.Equal(2, listings.Count);
        Assert.All(listings, listing => Assert.Equal("10881", listing.ItemId));
    }

    [Fact]
    public void The_price_and_quantity_are_read_from_the_row()
    {
        var listings = Parse(SearchPage, "11-leaf clover");
        var cheapest = listings.OrderBy(l => l.Price).First();
        Assert.Equal(25_000, cheapest.Price);
        Assert.Equal("56226", cheapest.StoreId);
        Assert.Equal("Crazy Gordon", cheapest.StoreName);
        Assert.Equal(1606, cheapest.Quantity);
    }

    [Fact]
    public void The_listings_come_out_sorted_by_price()
    {
        var listings = Parse(SearchPage, "11-leaf clover");
        Assert.Equal(25_000, listings[0].Price);
        Assert.Equal(29_029, listings[1].Price);
    }

    private static MallSearchPage ParsePage(string html, string term, MallReadPolicy? policy = null)
    {
        var method = typeof(KoLMallReadClient).GetMethod("ParsePage",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var client = new KoLMallReadClient(new HttpClient(), policy: policy);
        try
        {
            return (MallSearchPage)method.Invoke(client,
                new object?[] { term, html, new Uri("https://www.kingdomofloathing.com/mall.php"), null })!;
        }
        catch (System.Reflection.TargetInvocationException e) { throw e.InnerException!; }
    }

    private static string Row(string store, string item, string stock, string limitCell, long price) => $"""
        <tr class="graybelow" id="stock_{store}_{item}"><td></td>
          <td class="small store"><a href="mallstore.php?whichstore={store}"><b>Shop {store}</b></a></td>
          {(stock.Length == 0 ? "" : $"<td class=\"small stock\">{stock}</td>")}
          {limitCell}
          <td class="small price"><a href="x">{price:N0}&nbsp;Meat</a></td></tr>
        """;

    private static string Header(string item, string name) =>
        $"""<a name="results_{item}"></a><table><tr class="blackabove" id="item_{item}"><td><b><a href="/mall.php">{name}</a></b></td></tr>""";

    [Fact]
    public void The_daily_limit_is_read_from_the_plain_small_cell()
    {
        var listings = Parse(SearchPage, "11-leaf clover");
        Assert.All(listings, l =>
        {
            Assert.Equal(1, l.PurchaseLimit);
            Assert.Equal(MallLimitKind.Limited, l.LimitKind);
        });
    }

    [Fact]
    public void An_empty_limit_cell_means_unlimited_and_a_missing_one_means_unknown()
    {
        var html = Header("5", "Thing") + Row("1", "5", "10", "<td class=\"small\"></td>", 100)
            + Row("2", "5", "10", "", 200) + "</table>";
        var items = ParsePage(html, "Thing").Items.Single().Listings;
        Assert.Equal(MallLimitKind.Unlimited, items[0].LimitKind);
        Assert.Null(items[0].PurchaseLimit);
        Assert.Equal(MallLimitKind.Unknown, items[1].LimitKind);
    }

    [Fact]
    public void Unknown_stock_stays_unknown_instead_of_one()
    {
        var html = Header("5", "Thing") + Row("1", "5", "", "<td class=\"small\"></td>", 100) + "</table>";
        var listing = ParsePage(html, "Thing").Items.Single().Listings.Single();
        Assert.False(listing.QuantityKnown);
        Assert.Equal(0, listing.Quantity);
    }

    [Fact]
    public void A_partial_name_keeps_the_offers_per_item_with_names()
    {
        var html = Header("1", "Alpha clover") + Row("10", "1", "5", "", 300) + "</table>"
            + Header("2", "Beta clover") + Row("11", "2", "5", "", 50) + "</table>";
        var page = ParsePage(html, "clover");
        Assert.True(page.IsPartial);
        Assert.Equal(new[] { "Alpha clover", "Beta clover" }, page.Items.Select(i => i.ItemName));
        Assert.Equal("1", page.Items[0].Listings.Single().ItemId);
    }

    [Fact]
    public void The_cap_applies_after_sorting_so_cheap_late_offers_survive()
    {
        var rows = string.Concat(Enumerable.Range(1, 5).Select(i => Row(i.ToString(), "7", "1", "", 1000 - i)));
        var page = ParsePage(Header("7", "Thing") + rows + "</table>", "Thing",
            new MallReadPolicy { MaxListingsPerItem = 2 });
        var group = page.Items.Single();
        Assert.Equal(new long[] { 995, 996 }, group.Listings.Select(l => l.Price));
        Assert.True(group.IsTruncated);
        Assert.Equal(5, group.TotalOffers);
    }

    [Fact]
    public void Unknown_markup_is_an_error_not_an_empty_result()
    {
        Assert.Throws<MallSearchParseException>(() => ParsePage("<html><body>Maintenance</body></html>", "x"));
        Assert.Throws<MallSearchParseException>(() => ParsePage(
            Header("7", "Thing") + "<tr class=\"graybelow\" id=\"stock_1_7\"><td>garbled</td></tr></table>", "Thing"));
    }

    [Fact]
    public void A_real_empty_result_page_is_an_empty_page()
    {
        var page = ParsePage("<div id='searchresults'>Nothing here</div>", "zzz");
        Assert.Empty(page.Items);
    }

    private sealed class CapturingHandler(string html) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(html) };
        }
    }

    [Fact]
    public async Task A_search_by_item_id_posts_the_quoted_name_and_returns_the_real_offers()
    {
        var handler = new CapturingHandler(SearchPage);
        var client = new KoLMallReadClient(new HttpClient(handler),
            itemNameResolver: id => id == "10881" ? "11-leaf clover" : null);

        var snapshot = await client.SearchAsync("10881");

        Assert.Equal(2, snapshot.Listings.Count);
        Assert.All(snapshot.Listings, l => Assert.Equal("10881", l.ItemId));
        Assert.Equal(25_000, snapshot.Listings[0].Price);
        Assert.Equal("Crazy Gordon", snapshot.Listings[0].StoreName);
        Assert.Equal(1606, snapshot.Listings[0].Quantity);
        Assert.Equal(1, snapshot.Listings[0].PurchaseLimit);
        var body = Uri.UnescapeDataString(handler.Bodies.Single().Replace('+', ' '));
        Assert.Contains("pudnuggler=\"11-leaf clover\"", body);
        Assert.DoesNotContain("pudnuggler=10881", body);
    }

    [Fact]
    public async Task A_search_by_unknown_item_id_is_an_error_not_an_empty_result()
    {
        var handler = new CapturingHandler(SearchPage);
        var client = new KoLMallReadClient(new HttpClient(handler));
        await Assert.ThrowsAsync<MallSearchParseException>(() => client.SearchAsync("10881"));
        Assert.Empty(handler.Bodies);
    }
}
