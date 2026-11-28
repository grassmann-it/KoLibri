using System.Net;
using System.Text.RegularExpressions;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Markt;

/// <summary>
/// A price change is always bound to the price observed by the caller. The
/// server-side adapter refuses to update an item when that expected value has
/// changed in the meantime.
/// </summary>
public sealed record ShopPriceUpdate(
    string ItemId,
    long ExpectedCurrentPrice,
    long NewPrice,
    int? ExpectedPurchaseLimit = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ItemId) || !ItemId.All(char.IsDigit))
            throw new ArgumentException("ItemId must be a numeric KoL item id.", nameof(ItemId));
        if (ExpectedCurrentPrice < 0)
            throw new ArgumentOutOfRangeException(nameof(ExpectedCurrentPrice));
        if (NewPrice < 100)
            throw new ArgumentOutOfRangeException(nameof(NewPrice), "KoL mall prices must be at least 100 Meat.");
        if (ExpectedPurchaseLimit is < 0)
            throw new ArgumentOutOfRangeException(nameof(ExpectedPurchaseLimit));
    }
}

public enum ShopPriceUpdateStatus
{
    Ready,
    Hold,
    Missing,
    Conflict,
    Updated,
    VerificationFailed
}

public sealed record ShopPriceUpdateResult(
    string ItemId,
    long ExpectedCurrentPrice,
    long? ObservedCurrentPrice,
    long NewPrice,
    int? ExpectedPurchaseLimit,
    int? ObservedPurchaseLimit,
    ShopPriceUpdateStatus Status,
    string? Message = null);

public sealed record ShopMutationResult(
    bool Committed,
    bool Succeeded,
    DateTimeOffset ObservedAt,
    IReadOnlyList<ShopPriceUpdateResult> Results);

/// <summary>
/// Explicitly approved own-shop price updates. It uses KoL's backoffice form,
/// but never accepts a pwd token from a caller; the token is read from a fresh
/// managestore page and is kept entirely inside this class.
/// </summary>
public sealed class KoLShopMutationClient
{
    private static readonly Regex InputTag = new(
        "<input\\b[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex NameAttribute = new(
        "\\bname\\s*=\\s*[\\\"'](?<name>[^\\\"']+)[\\\"']", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ValueAttribute = new(
        "\\bvalue\\s*=\\s*[\\\"'](?<value>[^\\\"']*)[\\\"']", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PriceName = new(
        "^price\\[(?<item>\\d+)\\]$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LimitName = new(
        "^limit\\[(?<item>\\d+)\\]$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public KoLShopMutationClient(HttpClient httpClient, Uri? baseUri = null)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _baseUri = baseUri ?? new Uri("https://www.kingdomofloathing.com/", UriKind.Absolute);
    }

    public async Task<ShopMutationResult> ApplyPriceUpdatesAsync(
        IEnumerable<ShopPriceUpdate> updates,
        bool commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updates);
        var requested = updates.ToArray();
        if (requested.Length == 0)
            throw new ArgumentException("At least one price update is required.", nameof(updates));
        foreach (var update in requested) update.Validate();
        if (requested.GroupBy(x => x.ItemId, StringComparer.Ordinal).Any(x => x.Count() > 1))
            throw new ArgumentException("Each item may appear only once in a price update batch.", nameof(updates));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = await GetStorePageAsync(cancellationToken).ConfigureAwait(false);
            var results = BuildResults(requested, before.Listings);
            var ready = results
                .Where(x => x.Status == ShopPriceUpdateStatus.Ready)
                .ToArray();

            if (!commit || ready.Length == 0 || results.Any(x =>
                    x.Status is ShopPriceUpdateStatus.Missing or ShopPriceUpdateStatus.Conflict))
            {
                return new ShopMutationResult(
                    Committed: false,
                    Succeeded: results.All(x => x.Status is ShopPriceUpdateStatus.Ready or ShopPriceUpdateStatus.Hold),
                    before.ObservedAt,
                    results);
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ajax"] = "1",
                ["action"] = "updateinv",
                ["pwd"] = before.Pwd
            };
            foreach (var item in ready)
            {
                var update = requested.Single(x => x.ItemId == item.ItemId);
                values[$"price[{update.ItemId}]"] = update.NewPrice.ToString(System.Globalization.CultureInfo.InvariantCulture);
                values[$"limit[{update.ItemId}]"] = (item.ObservedPurchaseLimit ?? update.ExpectedPurchaseLimit ?? 0)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            var response = await PostAsync(new Uri(_baseUri, KoLRoutes.Backoffice), values, cancellationToken)
                .ConfigureAwait(false);
            if (!response.Contains("<!-- U:", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("KoL did not acknowledge the shop price update.");

            var after = await GetStorePageAsync(cancellationToken).ConfigureAwait(false);
            var verified = VerifyResults(requested, results, ready, after.Listings);
            return new ShopMutationResult(
                Committed: true,
                Succeeded: verified.All(x => x.Status is ShopPriceUpdateStatus.Updated or ShopPriceUpdateStatus.Hold),
                after.ObservedAt,
                verified);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static IReadOnlyList<ShopPriceUpdateResult> BuildResults(
        IReadOnlyList<ShopPriceUpdate> requested,
        IReadOnlyDictionary<string, ShopInventoryState> observed)
    {
        return requested.Select(update =>
        {
            if (!observed.TryGetValue(update.ItemId, out var current))
            {
                return new ShopPriceUpdateResult(update.ItemId, update.ExpectedCurrentPrice, null, update.NewPrice,
                    update.ExpectedPurchaseLimit, null, ShopPriceUpdateStatus.Missing, "Item is not in the own shop.");
            }
            if (current.Price != update.ExpectedCurrentPrice ||
                (update.ExpectedPurchaseLimit is not null && current.Limit != update.ExpectedPurchaseLimit))
            {
                return new ShopPriceUpdateResult(update.ItemId, update.ExpectedCurrentPrice, current.Price, update.NewPrice,
                    update.ExpectedPurchaseLimit, current.Limit, ShopPriceUpdateStatus.Conflict,
                    "Own-shop price or limit changed since the plan was created.");
            }
            if (current.Price == update.NewPrice)
            {
                return new ShopPriceUpdateResult(update.ItemId, update.ExpectedCurrentPrice, current.Price, update.NewPrice,
                    update.ExpectedPurchaseLimit, current.Limit, ShopPriceUpdateStatus.Hold, "Already at requested price.");
            }
            return new ShopPriceUpdateResult(update.ItemId, update.ExpectedCurrentPrice, current.Price, update.NewPrice,
                update.ExpectedPurchaseLimit, current.Limit, ShopPriceUpdateStatus.Ready);
        }).ToArray();
    }

    private static IReadOnlyList<ShopPriceUpdateResult> VerifyResults(
        IReadOnlyList<ShopPriceUpdate> requested,
        IReadOnlyList<ShopPriceUpdateResult> results,
        IReadOnlyList<ShopPriceUpdateResult> ready,
        IReadOnlyDictionary<string, ShopInventoryState> observed)
    {
        var readyIds = ready.Select(x => x.ItemId).ToHashSet(StringComparer.Ordinal);
        return requested.Select(update =>
        {
            var previous = results.Single(x => x.ItemId == update.ItemId);
            if (!readyIds.Contains(update.ItemId)) return previous;
            if (!observed.TryGetValue(update.ItemId, out var current))
                return previous with
                {
                    ObservedCurrentPrice = null,
                    Status = ShopPriceUpdateStatus.VerificationFailed,
                    Message = "Item disappeared after the update."
                };
            return previous with
            {
                ObservedCurrentPrice = current.Price,
                ObservedPurchaseLimit = current.Limit,
                Status = current.Price == update.NewPrice ? ShopPriceUpdateStatus.Updated : ShopPriceUpdateStatus.VerificationFailed,
                Message = current.Price == update.NewPrice ? null : "Fresh shop read did not show the requested price."
            };
        }).ToArray();
    }

    private async Task<(string Pwd, DateTimeOffset ObservedAt, IReadOnlyDictionary<string, ShopInventoryState> Listings)> GetStorePageAsync(
        CancellationToken cancellationToken)
    {
        var uri = new Uri(_baseUri, KoLRoutes.StoreInventory);
        using var response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var pwd = ExtractInputValue(html, "pwd");
        if (string.IsNullOrWhiteSpace(pwd))
            throw new InvalidOperationException("KoL did not provide a shop form token.");
        return (pwd, DateTimeOffset.UtcNow, ParseListings(html));
    }

    private async Task<string> PostAsync(Uri uri, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(values);
        using var response = await _http.PostAsync(uri, content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyDictionary<string, ShopInventoryState> ParseListings(string html)
    {
        var prices = new Dictionary<string, long>(StringComparer.Ordinal);
        var limits = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match tag in InputTag.Matches(html))
        {
            var name = NameAttribute.Match(tag.Value);
            var value = ValueAttribute.Match(tag.Value);
            if (!name.Success || !value.Success) continue;
            var decoded = WebUtility.HtmlDecode(value.Groups["value"].Value);
            var price = PriceName.Match(name.Groups["name"].Value);
            if (price.Success && TryParseNumber(decoded, out var parsedPrice))
            {
                prices[price.Groups["item"].Value] = parsedPrice;
                continue;
            }
            var limit = LimitName.Match(name.Groups["name"].Value);
            if (limit.Success && int.TryParse(decoded, out var parsedLimit))
                limits[limit.Groups["item"].Value] = parsedLimit;
        }
        return prices.ToDictionary(x => x.Key,
            x => new ShopInventoryState(x.Value, limits.TryGetValue(x.Key, out var limit) ? limit : null),
            StringComparer.Ordinal);
    }

    private static string? ExtractInputValue(string html, string inputName)
    {
        foreach (Match tag in InputTag.Matches(html))
        {
            var name = NameAttribute.Match(tag.Value);
            if (!name.Success || !string.Equals(name.Groups["name"].Value, inputName, StringComparison.OrdinalIgnoreCase)) continue;
            var value = ValueAttribute.Match(tag.Value);
            if (value.Success) return WebUtility.HtmlDecode(value.Groups["value"].Value);
        }
        return null;
    }

    private static bool TryParseNumber(string value, out long parsed)
        => long.TryParse(value.Replace(",", string.Empty, StringComparison.Ordinal),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out parsed);

    private sealed record ShopInventoryState(long Price, int? Limit);
}
