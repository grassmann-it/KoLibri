using System.Net;
using KoLibri.Core.Markt;
using Xunit;

namespace KoLibri.Core.Tests;

/// <summary>
/// Live finding 2026-11-26: plain backoffice.php shows the tab opened last (the sales log
/// after a CRM read), so the own-shop read came back with 0 listings although the store
/// held 348. Reads must ask for the inventory tab, backoffice.php?which=1.
/// </summary>
public sealed class OwnShopInventoryTabTests
{
    // Two rows copied from a live backoffice.php?which=1 page (pwd removed).
    private const string InventoryTab = """
        <html><body><table id="inv">
        <tr class="deets" rel="624148858" data-after="202"><td valign="center"><img src="https://images.kingdomofloathing.com/itemimages/fig27.gif"></td><td valign="center"><b>tiny plastic bitchin' meatcar</b></td><td valign="center" align="center">73</td valign="center"><td align="center"><span class="tohide">17,999</span><input type="text" class="hideit price" rel="100" style="width:80px" name="price[989]" value="17,999" /></td><td valign="center" align="center"><span class="tohide">1/day</span><input type="text" class="hideit lim" style="width:24px" name="limit[989]" value="1" /><input type="submit" value="Save" class="button hideit pricejax" style="font-size: 8pt"/></td><td align="right" valign="center">[<a href="#" class="update">update</a>]</td></tr>
        <tr class="deets" rel="1" data-after="0"><td valign="center"><img src="https://images.kingdomofloathing.com/itemimages/goldnugget.gif"></td><td valign="center"><b>1,970 carat gold</b></td><td valign="center" align="center">14</td valign="center"><td align="center"><span class="tohide">39,400</span><input type="text" class="hideit price" rel="39400" style="width:80px" name="price[8424]" value="39,400" /></td><td valign="center" align="center"><span class="tohide">1/day</span><input type="text" class="hideit lim" style="width:24px" name="limit[8424]" value="1" /></td><td></td></tr>
        </table></body></html>
        """;

    // What plain backoffice.php returned live after a sales-log read: no listing rows at all.
    private const string SalesTab = """
        <html><body><b>Recent Store Activity (past 2 weeks)</b><br /><span class=small>11/26/26 08:26:42 someone bought 1 (tiny plastic bitchin' meatcar) for 17999 Meat.</span></body></html>
        """;

    private sealed class TabHandler : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            Requested.Add(uri.PathAndQuery);
            var body = uri.AbsolutePath.EndsWith("backoffice.php") && uri.Query.Contains("which=1") ? InventoryTab : SalesTab;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task Own_shop_read_asks_for_the_inventory_tab_and_parses_its_rows()
    {
        var handler = new TabHandler();
        var client = new KoLMallReadClient(new HttpClient(handler));

        var shop = await client.FetchOwnShopAsync(forceRefresh: true);

        Assert.Equal("/backoffice.php?which=1", Assert.Single(handler.Requested));
        Assert.Equal(2, shop.Count);
        var meatcar = Assert.Single(shop, l => l.ItemId == "989");
        Assert.Equal(17_999, meatcar.Price);
        Assert.Equal(73, meatcar.Quantity);
        Assert.Equal(1, meatcar.PurchaseLimit);
        Assert.Equal("tiny plastic bitchin' meatcar", meatcar.DisplayName);
        Assert.Equal(39_400, Assert.Single(shop, l => l.ItemId == "8424").Price);
    }
}

/// <summary>Live finding 2026-11-26: KoL accepts only the dotted price-bound whichitem.</summary>
public sealed class MallWhichItemFormatTests
{
    [Fact]
    public void Whichitem_is_item_dot_price_like_the_store_page_radio_buttons()
    {
        Assert.Equal("11389.194000", KoLibri.Core.Markt.MallBuyClient.FormatWhichItem(11389, 194_000));
        Assert.Equal("684.230", KoLibri.Core.Markt.MallBuyClient.FormatWhichItem(684, 230));
        var form = KoLibri.Core.Markt.MallBuyClient.BuildPurchaseForm(3871791, 11389, 194_000, 1, "HASH");
        Assert.Equal("11389.194000", form["whichitem"]);
    }
}
