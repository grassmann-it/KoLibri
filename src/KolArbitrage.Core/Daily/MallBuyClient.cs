using System.Globalization;
using System.Text.RegularExpressions;
using KolArbitrage.Core.Accounts;

namespace KolArbitrage.Core.Daily;

/// <summary>
/// Writes to the mall: buys items from another player's store.
///
/// KoL's mall purchase is a POST to <c>mallstore.php</c> with the store id,
/// the item id, the price the buyer agreed to, the quantity and the store's
/// password hash. KoLmafia reads the hash out of the store page's HTML
/// (see <c>PasswordHashRequest</c>) and sends it back as <c>pwd</c>; the
/// hash is not the plaintext password and it is never logged.
///
/// The price must match the current listing exactly — KoL rejects the
/// purchase when <c>whichitem</c> carries a different price than the listing
/// the buyer saw.
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

        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["whichstore"] = storeId.ToString(CultureInfo.InvariantCulture),
            ["buying"] = "Yep.",
            // KoL encodes the agreed price in the item field: the item id, a
            // dot, and the price in meat as a whole number. The radio
            // button for a clover at 25.000 meat reads whichitem=10881.25000
            // and for a troll doll at 12.345.678 meat whichitem=5433.12345678.
            // Sending a scaled price makes KoL answer "This store doesn't
            // have that item at that price".
            ["whichitem"] = itemId.ToString(CultureInfo.InvariantCulture)
                + "." + unitPrice.ToString(CultureInfo.InvariantCulture),
            ["quantity"] = quantity.ToString(CultureInfo.InvariantCulture),
            ["pwd"] = passwordHash
        };
        var uri = new Uri(_baseUri, "mallstore.php");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Add("Cookie", credentials.CookieHeader);
        request.Content = new FormUrlEncodedContent(form);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var snippet = ExtractMeaningfulSnippet(body);
        return new MallBuyResult
        {
            RequestId = Guid.NewGuid().ToString("N"),
            StatusCode = (int)response.StatusCode,
            Accepted = IsConfirmedPurchase((int)response.StatusCode, body),
            StoreId = storeId,
            ItemId = itemId,
            UnitPrice = unitPrice,
            Quantity = quantity,
            TotalPrice = unitPrice * quantity,
            Message = snippet
        };
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
}
