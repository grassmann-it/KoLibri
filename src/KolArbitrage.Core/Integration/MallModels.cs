using System.Net;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using KolArbitrage.Core.Pricing;

namespace KolArbitrage.Core.Integration;

public sealed record MallListing(
    string ItemId,
    string StoreId,
    string? StoreName,
    long Price,
    int Quantity,
    int? PurchaseLimit,
    DateTimeOffset ObservedAt,
    Uri SourceUri);

public sealed record MallSnapshot(
    string ItemId,
    IReadOnlyList<MallListing> Listings,
    DateTimeOffset FetchedAt,
    Uri SourceUri,
    bool IsStale = false);

public sealed record OwnShopListing(
    string ItemId,
    long Price,
    int Quantity,
    int? PurchaseLimit,
    DateTimeOffset ObservedAt,
    string? DisplayName = null);

public sealed record MallReadPolicy
{
    public TimeSpan CacheTtl { get; init; } = TimeSpan.FromMinutes(20);
    public TimeSpan MaxStale { get; init; } = TimeSpan.FromHours(12);
    public TimeSpan MinimumRequestInterval { get; init; } = TimeSpan.FromSeconds(1);
    public int MaxListingsPerItem { get; init; } = 100;

    public void Validate()
    {
        if (CacheTtl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(CacheTtl));
        if (MaxStale < CacheTtl) throw new ArgumentException("MaxStale must be at least CacheTtl.", nameof(MaxStale));
        if (MinimumRequestInterval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(MinimumRequestInterval));
        if (MaxListingsPerItem <= 0) throw new ArgumentOutOfRangeException(nameof(MaxListingsPerItem));
    }
}

public interface IMallReadClient
{
    Task<MallSnapshot> SearchAsync(string itemId, bool forceRefresh = false, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OwnShopListing>> FetchOwnShopAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);
}

/// <summary>
/// Read-only adapter for mall.php/managestore.php. It never submits a buy,
/// sell, transfer or other mutating request.
/// </summary>
public sealed class KoLMallReadClient : IMallReadClient
{
    private static readonly Regex ListingRow = new(
        "<tr[^>]*class=[\\\"'](?:graybelow|grayabove)[\\\"'][^>]*id=[\\\"']stock_(?<store>\\d+)_(?<item>\\d+)[\\\"'][^>]*>(?<row>.*?)</tr>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex MeatPrice = new("<td[^>]*class=[\\\"']small price[\\\"'][^>]*>.*?>(?<price>\\d[\\d,]*)\\s*(?:&nbsp;)?Meat", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex Quantity = new("<td[^>]*class=[\\\"']small stock[\\\"'][^>]*>(?<quantity>\\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex StoreName = new("<td[^>]*class=[\\\"']small store[\\\"'][^>]*>.*?<b>(?<name>.*?)</b>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex OwnRow = new(
        "<tr\\s+class=[\\\"']deets[\\\"'][^>]*>(?<row>.*?)</tr>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex OwnItemId = new("name=[\\\"']price\\[(?<item>\\d+)\\][\\\"']", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex OwnName = new("<b>(?<name>.*?)</b>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex OwnQuantity = new("</b>.*?<td[^>]*>(?<quantity>\\d+)</td", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex OwnPrice = new("<span[^>]*class=[\\\"']tohide[\\\"'][^>]*>(?<price>[\\d,]+)</span>\\s*<input[^>]*class=[\\\"']hideit price[\\\"']", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex OwnLimit = new("name=[\\\"']limit\\[\\d+\\][\\\"'][^>]*value=[\\\"'](?<limit>\\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly MallReadPolicy _policy;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly Dictionary<string, MallSnapshot> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheGate = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<MallSnapshot>>> _inflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<OwnShopListing> _ownShopCache = [];
    private Lazy<Task<IReadOnlyList<OwnShopListing>>>? _ownShopInflight;
    private DateTimeOffset? _lastRequest;
    private DateTimeOffset? _ownShopFetchedAt;

    public KoLMallReadClient(HttpClient httpClient, Uri? baseUri = null, MallReadPolicy? policy = null, TimeProvider? clock = null)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _baseUri = baseUri ?? new Uri("https://www.kingdomofloathing.com/", UriKind.Absolute);
        _policy = policy ?? new MallReadPolicy();
        _policy.Validate();
        _clock = clock ?? TimeProvider.System;
    }

    public Task<MallSnapshot> SearchAsync(string itemId, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeId(itemId);
        var now = _clock.GetUtcNow();
        lock (_cacheGate)
        {
            if (!forceRefresh && _cache.TryGetValue(normalized, out var hit) && now - hit.FetchedAt <= _policy.CacheTtl)
                return Task.FromResult(hit);
        }

        var shared = _inflight.GetOrAdd(normalized, key => new Lazy<Task<MallSnapshot>>(
            () => FetchAndCacheAsync(key), LazyThreadSafetyMode.ExecutionAndPublication));
        return AwaitSharedAsync(normalized, shared, cancellationToken);
    }

    private async Task<MallSnapshot> AwaitSharedAsync(
        string normalized,
        Lazy<Task<MallSnapshot>> shared,
        CancellationToken cancellationToken)
    {
        try
        {
            return await shared.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (shared.IsValueCreated && shared.Value.IsCompleted)
                _inflight.TryRemove(new KeyValuePair<string, Lazy<Task<MallSnapshot>>>(normalized, shared));
        }
    }

    private async Task<MallSnapshot> FetchAndCacheAsync(string normalized)
    {
        var now = _clock.GetUtcNow();

        try
        {
            var uri = new Uri(_baseUri, KoLRoutes.MallSearch);
            var html = await PostStringAsync(uri, new Dictionary<string, string>
            {
                ["pudnuggler"] = normalized,
                ["category"] = "allitems",
                ["justitems"] = "1",
                ["sortresultsby"] = "price",
                ["x_cheapest"] = "0",
                ["max_price"] = "0"
            }, CancellationToken.None).ConfigureAwait(false);
            var snapshot = new MallSnapshot(normalized, ParseListings(normalized, html, uri), _clock.GetUtcNow(), uri);
            lock (_cacheGate) _cache[normalized] = snapshot;
            return snapshot;
        }
        catch
        {
            lock (_cacheGate)
            {
                if (_cache.TryGetValue(normalized, out var stale) && now - stale.FetchedAt <= _policy.MaxStale)
                    return stale with { IsStale = true };
            }
            throw;
        }
    }

    public Task<IReadOnlyList<OwnShopListing>> FetchOwnShopAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        lock (_cacheGate)
        {
            if (!forceRefresh && _ownShopFetchedAt is { } fetched && now - fetched <= _policy.CacheTtl)
                return Task.FromResult<IReadOnlyList<OwnShopListing>>(_ownShopCache.ToArray());
        }

        Lazy<Task<IReadOnlyList<OwnShopListing>>> shared;
        lock (_cacheGate)
        {
            if (!forceRefresh && _ownShopInflight is not null)
                shared = _ownShopInflight;
            else
                shared = _ownShopInflight = new Lazy<Task<IReadOnlyList<OwnShopListing>>>(
                    FetchOwnShopCoreAsync, LazyThreadSafetyMode.ExecutionAndPublication);
        }
        return AwaitOwnShopSharedAsync(shared, cancellationToken);
    }

    private async Task<IReadOnlyList<OwnShopListing>> AwaitOwnShopSharedAsync(
        Lazy<Task<IReadOnlyList<OwnShopListing>>> shared,
        CancellationToken cancellationToken)
    {
        try
        {
            return await shared.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (shared.IsValueCreated && shared.Value.IsCompleted)
            {
                lock (_cacheGate)
                {
                    if (ReferenceEquals(_ownShopInflight, shared)) _ownShopInflight = null;
                }
            }
        }
    }

    private async Task<IReadOnlyList<OwnShopListing>> FetchOwnShopCoreAsync()
    {
        var uri = new Uri(_baseUri, KoLRoutes.ManageStore);
        try
        {
            var html = await GetStringAsync(uri, CancellationToken.None).ConfigureAwait(false);
            var parsed = ParseOwnShop(html);
            lock (_cacheGate)
            {
                _ownShopCache.Clear();
                _ownShopCache.AddRange(parsed);
                _ownShopFetchedAt = _clock.GetUtcNow();
                return _ownShopCache.ToArray();
            }
        }
        catch
        {
            lock (_cacheGate)
            {
                if (_ownShopFetchedAt is { } fetched
                    && _clock.GetUtcNow() - fetched <= _policy.MaxStale)
                    return _ownShopCache.ToArray();
            }
            throw;
        }
    }

    public void Invalidate(string? itemId = null)
    {
        lock (_cacheGate)
        {
            if (string.IsNullOrWhiteSpace(itemId)) _cache.Clear();
            else _cache.Remove(itemId.Trim());
            _ownShopFetchedAt = null;
            _ownShopInflight = null;
        }
        if (string.IsNullOrWhiteSpace(itemId)) _inflight.Clear();
        else _inflight.TryRemove(itemId.Trim(), out _);
    }

    private async Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken)
    {
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lastRequest is { } last)
            {
                var wait = _policy.MinimumRequestInterval - (_clock.GetUtcNow() - last);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _lastRequest = _clock.GetUtcNow();
            return body;
        }
        finally { _requestGate.Release(); }
    }

    private async Task<string> PostStringAsync(Uri uri, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lastRequest is { } last)
            {
                var wait = _policy.MinimumRequestInterval - (_clock.GetUtcNow() - last);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
            using var content = new FormUrlEncodedContent(values);
            using var response = await _http.PostAsync(uri, content, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _lastRequest = _clock.GetUtcNow();
            return body;
        }
        finally { _requestGate.Release(); }
    }

    private IReadOnlyList<MallListing> ParseListings(string itemId, string html, Uri source, DateTimeOffset? observedAt = null)
    {
        var result = new List<MallListing>();
        foreach (Match match in ListingRow.Matches(html))
        {
            var block = match.Groups["row"].Value;
            var priceMatch = MeatPrice.Match(block);
            if (!priceMatch.Success || !long.TryParse(priceMatch.Groups["price"].Value.Replace(",", ""), out var price)) continue;
            var qty = Quantity.Match(block);
            var quantity = qty.Success && int.TryParse(qty.Groups["quantity"].Value, out var q) ? Math.Max(1, q) : 1;
            var store = StoreName.Match(block);
            var storeName = store.Success ? WebUtility.HtmlDecode(store.Groups["name"].Value).Trim() : null;
            result.Add(new MallListing(match.Groups["item"].Value, match.Groups["store"].Value, storeName, price, quantity, null, observedAt ?? _clock.GetUtcNow(), source));
            if (result.Count >= _policy.MaxListingsPerItem) break;
        }
        return result.OrderBy(x => x.Price).ToArray();
    }

    private static IReadOnlyList<OwnShopListing> ParseOwnShop(string html)
    {
        var result = new List<OwnShopListing>();
        foreach (Match rowMatch in OwnRow.Matches(html))
        {
            var row = rowMatch.Groups["row"].Value;
            var item = OwnItemId.Match(row);
            var price = OwnPrice.Match(row);
            var quantity = OwnQuantity.Match(row);
            if (!item.Success || !price.Success || !quantity.Success) continue;
            if (!long.TryParse(price.Groups["price"].Value.Replace(",", ""), out var parsedPrice)
                || !int.TryParse(quantity.Groups["quantity"].Value, out var parsedQuantity)) continue;
            var limit = OwnLimit.Match(row);
            int? parsedLimit = limit.Success && int.TryParse(limit.Groups["limit"].Value, out var limitValue) ? limitValue : null;
            var name = OwnName.Match(row).Success ? WebUtility.HtmlDecode(OwnName.Match(row).Groups["name"].Value).Trim() : null;
            result.Add(new OwnShopListing(item.Groups["item"].Value, parsedPrice, Math.Max(1, parsedQuantity), parsedLimit, DateTimeOffset.UtcNow, name));
        }
        return result;
    }

    private static string StripTags(string value) => WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]+>", " "));

    private static string NormalizeId(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId)) throw new ArgumentException("Item id is required.", nameof(itemId));
        return itemId.Trim();
    }
}

/// <summary>Exposes mall ask prices through the existing pricing engine.</summary>
public sealed class KoLMallPriceProvider : IPriceProvider
{
    private readonly IMallReadClient _mall;
    public KoLMallPriceProvider(IMallReadClient mall) => _mall = mall ?? throw new ArgumentNullException(nameof(mall));

    public async Task<IReadOnlyCollection<PriceObservation>> FetchAsync(string itemId, CancellationToken cancellationToken = default)
    {
        var snapshot = await _mall.SearchAsync(itemId, cancellationToken: cancellationToken).ConfigureAwait(false);
        return snapshot.Listings.Select(x => new PriceObservation(
            snapshot.ItemId,
            PriceSource.Mall,
            QuoteSide.Sell,
            x.Price,
            x.ObservedAt,
            quantity: x.Quantity,
            confidence: snapshot.IsStale ? 0.45m : 0.8m,
            listingId: $"{x.StoreId}:{x.ItemId}")).ToArray();
    }
}
