using KolArbitrage.Core.Spiel;
using System.Globalization;
using System.Text.RegularExpressions;
using KolArbitrage.Core.Basis;
using KolArbitrage.Core.Markt;

namespace KolArbitrage.Core.Markt;

/// <summary>What the market looks like for one item, right now.</summary>
public sealed record MarketSnapshot
{
    public required int ItemId { get; init; }
    public required string ItemName { get; init; }
    /// <summary>The listings on the mall, cheapest first.</summary>
    public required IReadOnlyList<MallListingRow> MallListings { get; init; }
    /// <summary>The price history, newest first.</summary>
    public required IReadOnlyList<int> PriceHistory { get; init; }
    /// <summary>The cheapest price on the mall. Zero when there is none.</summary>
    public int CheapestMallPrice => BuyPlanner.CheapestPrice(MallListings);
    /// <summary>The median of the price history. Zero when there is none.</summary>
    public int MedianPrice => BuyPlanner.MedianPrice(PriceHistory);
    /// <summary>The price the item fetches today, from the newest sale.</summary>
    public int LastSalePrice => PriceHistory.Count > 0 ? PriceHistory[0] : 0;
}

/// <summary>
/// Scans the mall and the price history for the arbitrage items.
///
/// The scan is read-only. Buying is a separate step, so the operator can
/// see the plan before the meat is spent.
/// </summary>
public sealed class MarketScanner
{
    private readonly HttpClient _http;
    private readonly Uri _baseUri;

    public MarketScanner(HttpClient http, Uri? baseUri = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _baseUri = baseUri ?? new Uri("https://www.kingdomofloathing.com/");
    }

    /// <summary>
    /// Reads the mall's listings and the price history for one item.
    /// </summary>
    public async Task<MarketSnapshot> ScanAsync(
        int itemId,
        string itemName,
        string searchTerm,
        CancellationToken cancellationToken = default)
    {
        var mall = await ReadMallAsync(itemId, searchTerm, cancellationToken).ConfigureAwait(false);
        var history = await ReadPricegunAsync(itemId, cancellationToken).ConfigureAwait(false);
        return new MarketSnapshot
        {
            ItemId = itemId,
            ItemName = itemName,
            MallListings = mall,
            PriceHistory = history
        };
    }

    /// <summary>
    /// Reads the mall's search result. The search is a POST — KoL answers
    /// a GET with the start page.
    /// </summary>
    public async Task<IReadOnlyList<MallListingRow>> ReadMallAsync(
        int itemId,
        string searchTerm,
        CancellationToken cancellationToken = default)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["pudnuggler"] = searchTerm
        };
        var uri = new Uri(_baseUri, "mall.php");
        using var content = new FormUrlEncodedContent(form);
        using var response = await _http
            .PostAsync(uri, content, cancellationToken)
            .ConfigureAwait(false);
        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return PriceScanner.ReadMallListings(html, itemId);
    }

    /// <summary>
    /// Reads the price history. Pricegun is a separate site and needs no
    /// login.
    /// </summary>
    public async Task<IReadOnlyList<int>> ReadPricegunAsync(
        int itemId,
        CancellationToken cancellationToken = default)
    {
        var uri = new Uri($"https://pricegun.loathers.net/item/{itemId}");
        try
        {
            var html = await _http.GetStringAsync(uri, cancellationToken).ConfigureAwait(false);
            return PriceScanner.ReadPricegunHistory(html);
        }
        catch (HttpRequestException)
        {
            return Array.Empty<int>();
        }
    }

    /// <summary>
    /// Reads the buy password hash from the mall's page. The hash changes
    /// per page, so it is read for every purchase round.
    /// </summary>
    public async Task<string?> ReadPasswordHashAsync(
        AccountCredentials credentials,
        int storeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var uri = new Uri(_baseUri, $"mallstore.php?whichstore={storeId}");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("Cookie", credentials.CookieHeader);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return MallBuyClient.ExtractPasswordHash(html);
    }
}
