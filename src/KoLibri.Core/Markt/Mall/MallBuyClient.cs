using System.Globalization;
using System.Text.RegularExpressions;
using KoLibri.Core.Basis;
using KoLibri.Core.Markt;

namespace KoLibri.Core.Markt;

/// <summary>
/// Writes to the mall: buys items from another player's store.
///
/// KoL's mall purchase is a POST to <c>mallstore.php</c> with the store id,
/// the item id, the price the buyer agreed to, the quantity and the store's
/// password hash. KoLmafia reads the hash out of the store page's HTML
/// (see <c>PasswordHashRequest</c>) and sends it back as <c>pwd</c>; the
/// hash is not the plaintext password and it is never logged.
///
/// The price must match the current listing exactly — <c>whichitem</c> is the
/// item id, a dot and the price, as the store page's radio buttons send it (see
/// <see cref="FormatWhichItem"/>), and KoL rejects the purchase when it differs
/// from the store's current price.
/// </summary>
public sealed class MallBuyClient
{
    // KoLmafia's PasswordHashRequest uses three patterns because the store
    // page has changed markup over the years.
    private static readonly Regex[] HashPatterns =
    {
        new(@"name=[""']?pwd[""']? value=[""']([^""']+)[""']", RegexOptions.Compiled),
        // The query-string form: pwd=<hash>&other=… — the hash ends at the
        // next separator, not at the end of the tag.
        new(@"pwd=([^&""'\s<>]+)", RegexOptions.Compiled),
        new(@"pwd = ""([^""]+)""", RegexOptions.Compiled)
    };

    private readonly HttpClient _http;
    private readonly Uri _baseUri;

    public MallBuyClient(HttpClient http, Uri baseUri)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _baseUri = baseUri ?? throw new ArgumentNullException(nameof(baseUri));
    }

    public static Uri DefaultBaseUri => new("https://www.kingdomofloathing.com/");

    /// <summary>Reads the store's password hash from the store page.
    /// Returns <c>null</c> when the page has no hash (an unreadable or
    /// password-protected store).</summary>
    public async Task<string?> ReadPasswordHashAsync(
        AccountCredentials credentials,
        int storeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (string.IsNullOrWhiteSpace(credentials.CookieHeader))
            throw new InvalidOperationException(
                "Reading a store hash requires a logged-in account.");
        var uri = new Uri(_baseUri, $"mallstore.php?whichstore={storeId}");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("Cookie", credentials.CookieHeader);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ExtractPasswordHash(body);
    }

    /// <summary>Extracts the store password hash from a store page's HTML.
    /// Mirrors KoLmafia's PasswordHashRequest patterns.</summary>
    public static string? ExtractPasswordHash(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        foreach (var pattern in HashPatterns)
        {
            var match = pattern.Match(html);
            if (!match.Success) continue;
            var value = match.Groups[1].Value.Trim();
            if (value.Length == 0) continue;
            return value;
        }
        return null;
    }

    /// <summary>Highest unit price the <c>whichitem</c> encoding can carry (nine digits).</summary>
    public const long MaxBindablePrice = 999_999_999;

    /// <summary>
    /// KoL's price-bound item field, exactly as the store page's radio buttons
    /// send it: item id, a dot, unit price (item 11389 at 194,000 meat is
    /// <c>11389.194000</c>). Live 2026-11-26 KoL refused the old zero-padded
    /// form (<c>11389000194000</c>) with "This store doesn't have that item at
    /// that price" and accepted the dotted one. KoL refuses the purchase when the
    /// store's current price differs, so this is the price binding.
    /// </summary>
    public static string FormatWhichItem(long itemId, long unitPrice)
    {
        if (itemId <= 0) throw new ArgumentOutOfRangeException(nameof(itemId));
        if (unitPrice <= 0 || unitPrice > MaxBindablePrice)
            throw new ArgumentOutOfRangeException(nameof(unitPrice));
        return itemId.ToString(CultureInfo.InvariantCulture)
            + "." + unitPrice.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The single mallstore.php purchase form, shared by this client and the
    /// purchase gateway's HTTP transport. Never logged: it carries the hash.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildPurchaseForm(
        long storeId, long itemId, long unitPrice, int quantity, string? passwordHash)
    {
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["whichstore"] = storeId.ToString(CultureInfo.InvariantCulture),
            ["buying"] = "1",
            ["whichitem"] = FormatWhichItem(itemId, unitPrice),
            ["quantity"] = quantity.ToString(CultureInfo.InvariantCulture)
        };
        if (!string.IsNullOrWhiteSpace(passwordHash)) form["pwd"] = passwordHash;
        return form;
    }

    /// <summary>
    /// KoL's answer to a purchase that broke a daily limit. The message
    /// names the limit, so the caller can skip the listing for today.
    /// </summary>
    public const string DailyLimitRefusal = "You may only buy";

    /// <summary>Buys <paramref name="quantity"/> units of an item from a
    /// store at an agreed price. The caller must have read the listing
    /// first — the price has to match the listing exactly.</summary>
    public async Task<MallBuyResult> BuyAsync(
        AccountCredentials credentials,
        int storeId,
        int itemId,
        long unitPrice,
        int quantity,
        string passwordHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (string.IsNullOrWhiteSpace(credentials.CookieHeader))
            throw new InvalidOperationException(
                "Buying requires a logged-in account.");
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity));
        if (unitPrice <= 0)
            throw new ArgumentOutOfRangeException(nameof(unitPrice));
        if (string.IsNullOrWhiteSpace(passwordHash))
            throw new ArgumentException(
                "The store password hash is required for a mall purchase.", nameof(passwordHash));

        var form = BuildPurchaseForm(storeId, itemId, unitPrice, quantity, passwordHash);
        var uri = new Uri(_baseUri, "mallstore.php");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Add("Cookie", credentials.CookieHeader);
        request.Content = new FormUrlEncodedContent(form);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var snippet = ExtractMeaningfulSnippet(body);
        var status = (int)response.StatusCode;
        var confirmed = IsConfirmedPurchase(status, body);
        if (!confirmed)
        {
            // A 2xx page that neither confirms nor names a known refusal (empty, garbled, truncated) says nothing:
            // the POST may have bought. That is Unverified (indeterminate), never a rejection.
            var unreadable = status is >= 200 and < 300 && !IsKnownRefusal(body);
            return new MallBuyResult
            {
                RequestId = Guid.NewGuid().ToString("N"), StatusCode = status, Accepted = false,
                Unverified = unreadable,
                StoreId = storeId, ItemId = itemId, UnitPrice = unitPrice, Quantity = quantity,
                TotalPrice = unitPrice * quantity, RequestedQuantity = quantity,
                Message = unreadable ? $"Unverified receipt: the answer neither confirms nor refuses the purchase. {snippet}" : snippet
            };
        }
        var receipt = ReadReceipt(body, itemId, unitPrice, quantity);
        return new MallBuyResult
        {
            RequestId = Guid.NewGuid().ToString("N"),
            StatusCode = status,
            // Only what the receipt proves counts: an inconsistent receipt is not accepted (Unverified).
            Accepted = !receipt.Unverified && receipt.Quantity > 0,
            Unverified = receipt.Unverified,
            Partial = !receipt.Unverified && receipt.Quantity is > 0 && receipt.Quantity < quantity,
            StoreId = storeId,
            ItemId = itemId,
            UnitPrice = unitPrice,
            Quantity = receipt.Unverified ? quantity : receipt.Quantity,
            TotalPrice = unitPrice * (receipt.Unverified ? quantity : receipt.Quantity),
            RequestedQuantity = quantity,
            Message = receipt.Unverified ? $"Unverified receipt: {receipt.Problem} {snippet}" : snippet
        };
    }

    private static readonly Regex SpentPattern = new(@"You spent\s+([\d,\.]+)\s+Meat", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ItemIdPattern = new(@"(?:[?&;\s](?:itemid|item)=|descitem\([^)]*\)[^>]*?whichitem=)(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal readonly record struct Receipt(int Quantity, bool Unverified, string? Problem);

    /// <summary>
    /// Reads what KoL's purchase page proves: the Meat spent (a multiple of the unit price) and the
    /// quantity acquired. A receipt that names another item id, or whose numbers disagree or exceed the
    /// order, is Unverified; a smaller confirmed quantity is a partial purchase.
    /// </summary>
    internal static Receipt ReadReceipt(string body, int itemId, long unitPrice, int requested)
    {
        // Item id: only checked when the page names one (KoL's receipt usually shows the item name only).
        var ids = ItemIdPattern.Matches(body).Select(m => m.Groups[1].Value).Where(v => v.Length <= 9).ToList();
        if (ids.Count > 0 && ids.Any(v => v != itemId.ToString(CultureInfo.InvariantCulture)))
            return new(0, true, "the receipt names a different item.");

        int? byMeat = null;
        var spent = SpentPattern.Match(body);
        if (spent.Success)
        {
            var digits = spent.Groups[1].Value.Replace(",", "").Replace(".", "");
            if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var meat) || unitPrice <= 0
                || meat % unitPrice != 0 || meat / unitPrice > int.MaxValue)
                return new(0, true, $"the Meat spent ({spent.Groups[1].Value}) is not a multiple of the unit price {unitPrice}.");
            byMeat = (int)(meat / unitPrice);
        }

        int? byItems = null;
        var text = System.Text.RegularExpressions.Regex.Replace(body, "<[^>]+>", " ");
        var at = text.IndexOf("You acquire", StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
        {
            var tail = text[at..Math.Min(text.Length, at + 240)];
            var n = System.Text.RegularExpressions.Regex.Match(tail, @"\((\d[\d,]*)\)");
            if (n.Success && int.TryParse(n.Groups[1].Value.Replace(",", ""), NumberStyles.None, CultureInfo.InvariantCulture, out var count))
                byItems = count;
            else if (requested == 1)
                byItems = 1;
            else if (byMeat is null)
                // A bare/truncated "You acquire" proves no count, and no Meat line backs it up:
                // for a multi-unit order that is indeterminate, never one confirmed unit.
                return new(0, true, "the receipt is truncated after 'You acquire' and shows no count.");
            else
                byItems = byMeat;   // the Meat line decides; the bare text adds nothing
        }

        if (byMeat is null && byItems is null)
            return new(0, true, "the receipt shows neither Meat spent nor an item count.");
        if (byMeat is { } a && byItems is { } b && a != b)
            return new(0, true, $"Meat spent implies {a} items but the receipt shows {b}.");
        var confirmed = byMeat ?? byItems!.Value;
        if (confirmed > requested)
            return new(0, true, $"the receipt shows {confirmed} items for an order of {requested}.");
        // A bare "You acquire an item" cannot show more than one unit: for a larger order that is a partial one.
        return new(confirmed, false, null);
    }

    /// <summary>
    /// A mall purchase only counts when KoL says so ("You acquire ..." or "You spent ... Meat").
    /// An HTTP 200 alone is not enough: KoL answers a refused buy ("doesn't have that item at that
    /// price", sold out) with a normal page.
    /// </summary>
    public static bool IsConfirmedPurchase(int statusCode, string body)
        => statusCode is >= 200 and < 300 && !string.IsNullOrEmpty(body)
           && (body.Contains("You acquire", StringComparison.OrdinalIgnoreCase)
               || body.Contains("You spent", StringComparison.OrdinalIgnoreCase));

    private static readonly string[] RefusalMarkers =
    {
        DailyLimitRefusal, "doesn't have", "does not have", "don't have enough", "not enough meat",
        "sold out", "no longer", "out of stock", "can't afford", "cannot afford", "too expensive"
    };

    /// <summary>True when a 2xx page names a refusal KoL is known to give. Anything else that is not a
    /// confirmation is unreadable, so the outcome is indeterminate.</summary>
    public static bool IsKnownRefusal(string body)
        => !string.IsNullOrEmpty(body)
           && RefusalMarkers.Any(m => body.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>KoL's mall rows expose the store id as a string; the
    /// purchase needs the numeric form.</summary>
    public static bool TryParseStoreId(string? value, out int storeId)
    {
        storeId = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        return int.TryParse(value.Trim(), out storeId);
    }

    /// <summary>Surfaces the part of KoL's response that says what happened
    /// instead of the leading script block.</summary>
    internal static string ExtractMeaningfulSnippet(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "(empty body)";
        var markers = new[]
        {
            "You acquire", "You don't have enough", "not enough meat",
            "The store owner", "You put", "You get", "You lose", "purchase"
        };
        foreach (var marker in markers)
        {
            var index = body.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;
            var start = Math.Max(0, index - 60);
            var length = Math.Min(400, body.Length - start);
            return body.Substring(start, length);
        }
        return body.Length > 300 ? body.Substring(0, 300) : body;
    }
}

/// <summary>Outcome of one mall purchase.</summary>
public sealed record MallBuyResult
{
    public required string RequestId { get; init; }
    public required int StatusCode { get; init; }
    public required bool Accepted { get; init; }
    public required int StoreId { get; init; }
    public required int ItemId { get; init; }
    public required long UnitPrice { get; init; }
    public required int Quantity { get; init; }
    public required long TotalPrice { get; init; }
    public required string Message { get; init; }
    /// <summary>What was ordered; <see cref="Quantity"/>/<see cref="TotalPrice"/> are what the receipt confirmed once <see cref="Accepted"/>.</summary>
    public int RequestedQuantity { get; init; }
    /// <summary>KoL confirmed less than was ordered.</summary>
    public bool Partial { get; init; }
    /// <summary>The page claimed a purchase but its numbers or item contradict the order: outcome unknown.</summary>
    public bool Unverified { get; init; }
}
