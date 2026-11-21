using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace KolArbitrage.Core.Spiel;

/// <summary>
/// One listing on the mall, with the seller's daily limit.
/// </summary>
public sealed record MallListingRow
{
    /// <summary>The seller's store id.</summary>
    public required int StoreId { get; init; }
    /// <summary>The item's id.</summary>
    public required int ItemId { get; init; }
    /// <summary>The store name.</summary>
    public required string StoreName { get; init; }
    /// <summary>How many the seller offers in total.</summary>
    public required int Stock { get; init; }
    /// <summary>The price per unit, in meat.</summary>
    public required int UnitPrice { get; init; }
    /// <summary>
    /// How many the seller sells per day. KoL writes "1 / day" in its own
    /// column; a seller without a limit sells as much as the stock holds.
    /// </summary>
    public int DailyLimit { get; init; }
    /// <summary>Whether the seller limits the amount per day.</summary>
    public bool HasDailyLimit => DailyLimit > 0;
    /// <summary>
    /// How many can actually be bought today: the smaller of the stock and
    /// the daily limit. A listing without a limit is bounded by its stock.
    /// </summary>
    public int AvailableToday => HasDailyLimit ? Math.Min(Stock, DailyLimit) : Stock;
}

/// <summary>
/// Reads the mall's search result and the price history.
///
/// The mall answers a search with the whole result table, and marks the
/// searched item with an anchor. Everything after that anchor belongs to
/// the search — the rows before it are advertisements for other items.
/// </summary>
public static class PriceScanner
{
    private static readonly Regex AnchorPattern = new(
        @"<a name=""results_(?<id>\d+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RowPattern = new(
        @"<tr[^>]*id=""stock_(?<store>\d+)_(?<item>\d+)""[^>]*>(?<body>.*?)</tr>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex StoreNamePattern = new(
        @"<b>(?<name>[^<]+)</b>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex StockPattern = new(
        @"class=""small stock""[^>]*>(?<qty>[\d,]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PricePattern = new(
        @"searchprice=(?<price>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LimitPattern = new(
        @"(?<limit>\d+)\s*&nbsp;/\s*&nbsp;day",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PricegunHistoryPattern = new(
        @"""(?<price>\d{4,6}(?:\.\d+)?)""",
        RegexOptions.Compiled);

    /// <summary>
    /// Reads the listings for the searched item out of a mall search page.
    /// The rows before the anchor belong to the page's advertisement
    /// columns and are skipped.
    /// </summary>
    public static IReadOnlyList<MallListingRow> ReadMallListings(string html, int itemId)
    {
        if (string.IsNullOrWhiteSpace(html)) return Array.Empty<MallListingRow>();
        var anchor = AnchorPattern.Match(html);
        if (!anchor.Success) return Array.Empty<MallListingRow>();
        var rows = new List<MallListingRow>();
        foreach (Match row in RowPattern.Matches(html[anchor.Index..]))
        {
            if (!int.TryParse(row.Groups["item"].Value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var parsedItem)) continue;
            if (parsedItem != itemId) continue;
            if (!int.TryParse(row.Groups["store"].Value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var store)) continue;
            var body = row.Groups["body"].Value;
            var name = StoreNamePattern.Match(body) is { Success: true } m
                ? WebUtility.HtmlDecode(m.Groups["name"].Value).Trim()
                : string.Empty;
            var stockText = StockPattern.Match(body).Groups["qty"].Value.Replace(",", string.Empty);
            int.TryParse(stockText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stock);
            var priceText = PricePattern.Match(body).Groups["price"].Value;
            int.TryParse(priceText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var price);
            var limitText = LimitPattern.Match(body).Groups["limit"].Value;
            int.TryParse(limitText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit);
            if (price <= 0) continue;
            rows.Add(new MallListingRow
            {
                StoreId = store,
                ItemId = parsedItem,
                StoreName = name,
                Stock = stock,
                UnitPrice = price,
                DailyLimit = limit
            });
        }
        return rows;
    }

    /// <summary>
    /// Reads the price history out of a pricegun page.
    ///
    /// Pricegun is a server-rendered React app: the history lives in the
    /// page's turbo-stream, where the quotes are escaped. The stream is
    /// unescaped and the numbers behind the history marker are read.
    /// </summary>
    public static IReadOnlyList<int> ReadPricegunHistory(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return Array.Empty<int>();
        var stream = html.Replace("\\\"", "\"");
        var history = stream.IndexOf("\"history\"", StringComparison.Ordinal);
        if (history < 0) return Array.Empty<int>();
        var prices = new List<int>();
        foreach (Match match in PricegunHistoryPattern.Matches(stream[history..]))
        {
            if (double.TryParse(match.Groups["price"].Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var value))
            {
                prices.Add((int)Math.Round(value));
            }
        }
        return prices;
    }

    /// <summary>
    /// The median of a price series — the price the item actually fetches.
    /// An even number of points takes the mean of the two middle ones.
    /// </summary>
    public static int Median(IEnumerable<int> prices)
    {
        var sorted = prices.OrderBy(x => x).ToArray();
        if (sorted.Length == 0) return 0;
        var middle = sorted.Length / 2;
        if (sorted.Length % 2 == 1) return sorted[middle];
        return (int)Math.Round((sorted[middle - 1] + sorted[middle]) / 2.0);
    }
}
