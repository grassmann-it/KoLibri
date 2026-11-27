using System.Globalization;
using KoLibri.Desktop;

namespace KoLibri.Tests.Desktop;

/// <summary>A signed-in Mallbot against the fake KoL, with a mall page for the 11-leaf clover (item 10881).</summary>
internal static class MallKit
{
    public const int Clover = 10881;
    public const int Store = 56226;

    /// <summary>The mall search answer: one store selling the clover at <paramref name="price"/>.</summary>
    public static string SearchPage(long price, int stock = 1_606) => $"""
        <center><b>Search Results:</b> <b>(Items 1-1 of 1)</b></center>
        <div id='searchresults'>
        <a name="searchresults"></a>
        <br /><a name="results_10881"></a>
        <table class="itemtable" summary="Stores selling 11-leaf clover">
        <tr class="blackabove" id="item_10881">
          <td><img src='x.gif' alt="11-leaf clover" title="11-leaf clover"></td>
          <td><b><a href="/mall.php?pudnuggler=%2211-leaf+clover%22">11-leaf clover</a></b></td>
        </tr>
        <tr class="graybelow" id="stock_56226_10881">
          <td style="border:none"></td>
          <td class="small store"><a href="mallstore.php?whichstore=56226"><b>Crazy Gordon</b></a></td>
          <td class="small stock">{stock.ToString("N0", CultureInfo.InvariantCulture)}</td>
          <td class="small">&nbsp;</td>
          <td class="small price"><a href="mallstore.php?whichstore=56226">{price.ToString("N0", CultureInfo.InvariantCulture)}&nbsp;Meat</a></td>
        </tr>
        </table>
        </div>
        """;

    /// <summary>A fake KoL whose mall lists the clover at <paramref name="price"/> and whose inventory answers in order.</summary>
    public static FakeKol Kol(long price, params string[] inventories)
    {
        var kol = new FakeKol { Meat = 1_000_000 }.On("POST mall.php", SearchPage(price));
        foreach (var inventory in inventories.DefaultIfEmpty("{}"))
            kol.On("GET api.php?what=inventory", inventory);
        return kol;
    }

    public static DesktopAccounts Accounts(FakeKol kol) => DesktopKit.Accounts(kol, DesktopKit.Store(AccountRole.Mallbot));

    public static DesktopMallPurchaseService Service(DesktopAccounts accounts, DesktopMallPurchaseOptions? options = null)
        => DesktopMallPurchaseService.ForRole(new DesktopLiveDataProvider(accounts), AccountRole.Mallbot, options);

    /// <summary>The purchase POSTs the fake KoL received (form bodies).</summary>
    public static IReadOnlyList<string> Purchases(FakeKol kol)
        => kol.Requests.Where(r => r.Method == "POST" && r.Path.TrimStart('/').StartsWith("mallstore.php", StringComparison.Ordinal))
            .Select(r => Uri.UnescapeDataString(r.Form)).ToArray();

    /// <summary>A clock fixed at noon UTC of one KoL day.</summary>
    public sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Value = now;
        public override DateTimeOffset GetUtcNow() => Value;
    }

    public static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
}
