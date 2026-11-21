using Xunit;
using KolArbitrage.Core.Spiel;

namespace KolArbitrage.Core.Tests;

public class StoreListerTests
{
    private const string StorePage = """
        <html><body>
        <form method="post" action="backoffice.php" id="stock">
        <select name="itemid" id="itemid" tabindex="1">
          <option value="10881" rel="11leafclover,477842994,100,16">11-leaf clover (16)</option>
          <option value="8691" rel="foreignbribes,477842994,100,143">bag of foreign bribes (143)</option>
        </select>
        <input type="hidden" name="pwd" value="732481f53839a3505f9b8c763fa641e9" />
        <input type="hidden" name="action" value="additem" />
        </form>
        <table id="inv">
          <tr class="deets"><td><b>11-leaf clover</b></td><td>16</td>
            <td><input type="text" class="hideit price" name="price[10881]" value="32,995" />
                <input type="text" class="hideit lim" name="limit[10881]" value="3" /></td></tr>
          <tr class="deets"><td><b>old dry bone</b></td><td>1</td>
            <td><input type="text" class="hideit price" name="price[6540]" value="1,000" />
                <input type="text" class="hideit lim" name="limit[6540]" value="1" /></td></tr>
        </table>
        </body></html>
        """;

    [Fact]
    public void ReadsTheItemsTheAccountCanListWithTheirStock()
    {
        var options = StoreLister.ReadItemsFromPage(StorePage);

        Assert.Equal(2, options.Count);
        var clover = options.Single(x => x.ItemId == 10881);
        Assert.Equal("11-leaf clover (16)", clover.Name);
        Assert.Equal(16, clover.InStock);
    }

    [Fact]
    public void ReadsTheOwnListingsWithTheirPricesAndLimits()
    {
        var listings = StoreLister.ReadOwnListings(StorePage);

        Assert.Equal(2, listings.Count);
        var clover = listings.Single(x => x.ItemId == 10881);
        Assert.Equal(32_995, clover.Price);
        Assert.Equal(3, clover.DailyLimit);
    }

    [Fact]
    public void AnItemThatIsNotListedHasNoListing()
    {
        var listings = StoreLister.ReadOwnListings(StorePage);

        Assert.DoesNotContain(listings, x => x.ItemId == 8691);
    }

    [Fact]
    public void AnEmptyPageYieldsNothing()
    {
        Assert.Empty(StoreLister.ReadOwnListings(""));
        Assert.Empty(StoreLister.ReadItemsFromPage(""));
    }
}
