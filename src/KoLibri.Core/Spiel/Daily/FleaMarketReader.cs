using System.Globalization;
using System.Text.RegularExpressions;

namespace KoLibri.Core.Spiel;

/// <summary>
/// Reads and writes KoL's flea market.
///
/// The flea market is the second marketplace: the seller names a price and
/// a 48-hour timer, and the item comes back when it does not sell. The
/// mall, by contrast, holds permanent listings whose price the seller can
/// change at any time. The two price curves move independently, so the
/// arbitrage has to compare them against each other.
///
/// KoL's flea market pages live under <c>town_fleamarket.php</c> (buying)
/// and <c>town_sellflea.php</c> (selling).
/// </summary>
public static class FleaMarketReader
{
    private static readonly Regex RowPattern = new(
        @"<tr[^>]*>(?<row>.*?)</tr>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex ItemIdPattern = new(
        @"whichitem=(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PricePattern = new(
        @"howmuch=(\d+)|sellprice=(\d+)|(?<![\d,])(\d[\d,]*)\s*meat",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex QuantityPattern = new(
        @"(?:quantity|qty)=(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TagPattern = new(
        @"<[^>]+>",
        RegexOptions.Compiled);

    /// <summary>
    /// Reads the listings from a flea market page. The page lists items
    /// with their id, the asking price and how many are on offer.
    /// </summary>
    public static IReadOnlyList<MarketListing> ReadListings(string html, string itemName = "")
    {
        if (string.IsNullOrWhiteSpace(html)) return Array.Empty<MarketListing>();
        var listings = new List<MarketListing>();
        var seen = new HashSet<int>();
        foreach (Match row in RowPattern.Matches(html))
        {
            var block = row.Groups["row"].Value;
            var idMatch = ItemIdPattern.Match(block);
            if (!idMatch.Success) continue;
            if (!int.TryParse(idMatch.Groups[1].Value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var itemId)) continue;
            if (!seen.Add(itemId)) continue;
            var priceMatch = PricePattern.Match(block);
            if (!priceMatch.Success) continue;
            var priceText = priceMatch.Groups[1].Success
                ? priceMatch.Groups[1].Value
                : priceMatch.Groups[2].Success
                    ? priceMatch.Groups[2].Value
                    : priceMatch.Groups[3].Value;
            if (!long.TryParse(priceText.Replace(",", string.Empty), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var price)) continue;
            var quantityMatch = QuantityPattern.Match(block);
            var quantity = quantityMatch.Success
                && int.TryParse(quantityMatch.Groups[1].Value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var parsed)
                ? Math.Max(1, parsed)
                : 1;
            listings.Add(new MarketListing
            {
                Market = MarketKind.FleaMarket,
                ItemId = itemId,
                ItemName = itemName,
                UnitPrice = price,
                Quantity = quantity
            });
        }
        return listings;
    }

    /// <summary>Reads the asking price of a single item from a flea market
    /// page, or null when the page does not offer it.</summary>
    public static long? ReadAskingPrice(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var match = PricePattern.Match(html);
        if (!match.Success) return null;
        var text = match.Groups[1].Success
            ? match.Groups[1].Value
            : match.Groups[2].Success
                ? match.Groups[2].Value
                : match.Groups[3].Value;
        return long.TryParse(text.Replace(",", string.Empty), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var price)
            ? price
            : null;
    }

    /// <summary>Reads the plain text of a page, with the markup removed.
    /// Useful for the operator-facing report.</summary>
    public static string ToPlainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var withoutScripts = Regex.Replace(html,
            @"<script.*?</script>", " ",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var text = TagPattern.Replace(withoutScripts, " ");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }
}
