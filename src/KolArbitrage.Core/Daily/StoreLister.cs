using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using KolArbitrage.Core.Accounts;

namespace KolArbitrage.Core.Daily;

/// <summary>One item the store can list.</summary>
public sealed record StoreItemOption
{
    /// <summary>The item's id.</summary>
    public required int ItemId { get; init; }
    /// <summary>The item's name.</summary>
    public required string Name { get; init; }
    /// <summary>How many the account holds.</summary>
    public required int InStock { get; init; }
}

/// <summary>What the store answered to a listing.</summary>
public sealed record ListItemResult
{
    public required bool Accepted { get; init; }
    public required int ItemId { get; init; }
    public required int Price { get; init; }
    public required int Quantity { get; init; }
    public required string Message { get; init; }
    /// <summary>Whether the store took the listing on.</summary>
    public bool IsListed => Message.Contains("Stock", StringComparison.OrdinalIgnoreCase)
        && !Message.Contains("error", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Lists items on the account's own store.
///
/// KoL's store is the backoffice: `managestore.php` redirects to it. The
/// page carries the items the account can sell in a select box and a form
/// that takes the item, the price, the amount and the daily limit.
/// </summary>
public sealed class StoreLister
{
    private readonly HttpClient _http;
    private readonly Uri _baseUri;

    public StoreLister(HttpClient http, Uri? baseUri = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _baseUri = baseUri ?? new Uri("https://www.kingdomofloathing.com/");
    }

    private static readonly Regex ItemOptionPattern = new(
        @"<option value=""(?<id>\d+)""[^>]*>(?<name>[^<]+)</option>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex StockPattern = new(
        @"\((?<stock>[\d,]+)\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PasswordPattern = new(
        @"name=""pwd"" value=""(?<pwd>[^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex OwnPricePattern = new(
        @"name=""price\[(?<id>\d+)\]"" value=""(?<price>[\d,]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex OwnLimitPattern = new(
        @"name=""limit\[(?<id>\d+)\]"" value=""(?<limit>\d*)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>One listing on the account's own store.</summary>
    public sealed record OwnListing
    {
        public required int ItemId { get; init; }
        public required int Price { get; init; }
        public int DailyLimit { get; init; }
    }

    /// <summary>
    /// Reads the account's own listings out of the store page. The
    /// listings live in the price fields of the store's table.
    /// </summary>
    public static IReadOnlyList<OwnListing> ReadOwnListings(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return Array.Empty<OwnListing>();
        var limits = new Dictionary<int, int>();
        foreach (Match match in OwnLimitPattern.Matches(html))
        {
            if (!int.TryParse(match.Groups["id"].Value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var itemId)) continue;
            int.TryParse(match.Groups["limit"].Value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var limit);
            limits[itemId] = limit;
        }
        var listings = new List<OwnListing>();
        foreach (Match match in OwnPricePattern.Matches(html))
        {
            if (!int.TryParse(match.Groups["id"].Value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var itemId)) continue;
            var priceText = match.Groups["price"].Value.Replace(",", string.Empty);
            if (!int.TryParse(priceText, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var price)) continue;
            listings.Add(new OwnListing
            {
                ItemId = itemId,
                Price = price,
                DailyLimit = limits.GetValueOrDefault(itemId)
            });
        }
        return listings;
    }

    /// <summary>
    /// Reads the items the account can list. The select's options carry
    /// the item id and its name with the amount in stock in brackets.
    /// </summary>
    public static IReadOnlyList<StoreItemOption> ReadItemsFromPage(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return Array.Empty<StoreItemOption>();
        var items = new List<StoreItemOption>();
        foreach (Match option in ItemOptionPattern.Matches(html))
        {
            if (!int.TryParse(option.Groups["id"].Value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var itemId)) continue;
            var name = WebUtility.HtmlDecode(option.Groups["name"].Value).Trim();
            var stockText = StockPattern.Match(name).Groups["stock"].Value.Replace(",", string.Empty);
            int.TryParse(stockText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stock);
            items.Add(new StoreItemOption
            {
                ItemId = itemId,
                Name = name,
                InStock = stock
            });
        }
        return items;
    }

    /// <summary>
    /// Reads the items the account can list from the store page.
    /// </summary>
    public async Task<IReadOnlyList<StoreItemOption>> ReadItemsAsync(
        AccountCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var html = await ReadBackofficeAsync(credentials, cancellationToken).ConfigureAwait(false);
        return ReadItemsFromPage(html);
    }

    /// <summary>
    /// Lists an item on the account's store.
    /// </summary>
    public async Task<ListItemResult> ListAsync(
        AccountCredentials credentials,
        int itemId,
        int price,
        int quantity,
        int dailyLimit = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (price <= 0) throw new ArgumentOutOfRangeException(nameof(price));
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));

        var html = await ReadBackofficeAsync(credentials, cancellationToken).ConfigureAwait(false);
        var password = PasswordPattern.Match(html).Groups["pwd"].Value;
        if (string.IsNullOrWhiteSpace(password))
        {
            return new ListItemResult
            {
                Accepted = false,
                ItemId = itemId,
                Price = price,
                Quantity = quantity,
                Message = "The store page carried no password hash."
            };
        }

        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["itemid"] = itemId.ToString(CultureInfo.InvariantCulture),
            ["price"] = price.ToString(CultureInfo.InvariantCulture),
            ["quantity"] = quantity.ToString(CultureInfo.InvariantCulture),
            ["limit"] = dailyLimit.ToString(CultureInfo.InvariantCulture),
            ["pwd"] = password,
            ["neveragain"] = "0",
            ["priceok"] = "0",
            ["action"] = "additem"
        };
        var uri = new Uri(_baseUri, "backoffice.php");
        using var content = new FormUrlEncodedContent(form);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Add("Cookie", credentials.CookieHeader);
        request.Content = content;
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        // The store's answer carries no confirmation text — the store page
        // itself shows what was stocked. So the listing is read back.
        var listing = ReadOwnListings(body).FirstOrDefault(x => x.ItemId == itemId);
        var listed = listing is not null && listing.Price == price;
        return new ListItemResult
        {
            Accepted = response.IsSuccessStatusCode && listed,
            ItemId = itemId,
            Price = price,
            Quantity = quantity,
            Message = listed
                ? $"The store lists the item at {price:N0} meat"
                : ExtractMessage(body)
        };
    }

    /// <summary>
    /// Reads the account's own listings from the store page.
    /// </summary>
    public async Task<IReadOnlyList<OwnListing>> ReadOwnListingsAsync(
        AccountCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var html = await ReadBackofficeAsync(credentials, cancellationToken).ConfigureAwait(false);
        return ReadOwnListings(html);
    }

    /// <summary>
    /// Reads the store's own page. `managestore.php` answers with a
    /// redirect to the backoffice, so the backoffice is read directly.
    /// </summary>
    private async Task<string> ReadBackofficeAsync(
        AccountCredentials credentials,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(_baseUri, "backoffice.php");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("Cookie", credentials.CookieHeader);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The short answer out of the store's page. KoL answers with the
    /// whole page; the interesting part is the line about the item that
    /// was stocked.
    /// </summary>
    internal static string ExtractMessage(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var text = WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", "\n"));
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 2)
            .ToArray();
        foreach (var marker in new[]
        {
            "You put", "You stock", "Stocking", "stocked", "You have", "error", "Error"
        })
        {
            var line = lines.FirstOrDefault(x => x.Contains(marker, StringComparison.OrdinalIgnoreCase));
            if (line is not null) return line;
        }
        // The page's own confirmation sits in the alert box.
        var alert = Regex.Match(html, @"id=""min""[^>]*>(?<text>[^<]*)");
        if (alert.Success) return WebUtility.HtmlDecode(alert.Groups["text"].Value).Trim();
        return lines.Length > 0 ? string.Join(' ', lines.Take(4)) : string.Empty;
    }
}
