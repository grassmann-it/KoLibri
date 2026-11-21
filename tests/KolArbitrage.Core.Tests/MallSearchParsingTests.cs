using Xunit;
using KolArbitrage.Core.Markt;

namespace KolArbitrage.Core.Tests;

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
}
