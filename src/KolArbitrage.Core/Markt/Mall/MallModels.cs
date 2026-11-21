using System.Net;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using KolArbitrage.Core.Basis;
using KolArbitrage.Core.Spiel;

namespace KolArbitrage.Core.Markt;

public sealed record MallListing(
    string ItemId,
    string StoreId,
    string? StoreName,
    long Price,
    int Quantity,
    int? PurchaseLimit,
    DateTimeOffset ObservedAt,
    Uri SourceUri)
{
    /// <summary>
    /// Numeric seller id when the Mall row exposes a showplayer/owner link.
    /// Older KoL responses do not include this field, so it remains optional.
    /// </summary>
    public string? SellerId { get; init; }

    /// <summary>Seller display name when it is present in the Mall row.</summary>
    public string? SellerName { get; init; }

    /// <summary>Compatibility alias used by purchase planners.</summary>
    public string? Seller => SellerId;

    /// <summary>Compatibility alias for owner-oriented Mall terminology.</summary>
    public string? OwnerId => SellerId;
}

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
    /// <summary>Endpoint and redirect boundary for Mall HTTP requests.</summary>
    public KoLSecurityPolicy Security { get; init; } = new();
    /// <summary>Compatibility alias for callers that use the longer name.</summary>
    public KoLSecurityPolicy SecurityPolicy
    {
        get => Security;
        init => Security = value;
    }
    /// <summary>Convenience forwarding property for simple host overrides.</summary>
    public IReadOnlyCollection<string> AllowedHosts
    {
        get => Security.AllowedHosts;
        init => Security = Security with { AllowedHosts = value };
    }

    public void Validate()
    {
        if (CacheTtl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(CacheTtl));
        if (MaxStale < CacheTtl) throw new ArgumentException("MaxStale must be at least CacheTtl.", nameof(MaxStale));
        if (MinimumRequestInterval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(MinimumRequestInterval));
        if (MaxListingsPerItem <= 0) throw new ArgumentOutOfRangeException(nameof(MaxListingsPerItem));
        (Security ?? throw new ArgumentException("A Mall HTTP security policy is required.", nameof(Security))).Validate();
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
    /// <summary>KoL marks the searched item with an anchor of the shape
    /// <c>results_&lt;numeric id&gt;</c> and a row with the matching
    /// <c>item_&lt;numeric id&gt;</c>. Those ids tell us which listing rows
    /// belong to the searched item.</summary>
    private static readonly Regex SearchedItemAnchor = new(
        "<a name=[\\\"']results_(?<item>\\d+)[\\\"']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MeatPrice = new("<td[^>]*class=[\\\"']small price[\\\"'][^>]*>.*?>(?<price>\\d[\\d,]*)\\s*(?:&nbsp;)?Meat", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex Quantity = new("<td[^>]*class=[\\\"']small stock[\\\"'][^>]*>(?<quantity>[\\d,]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex StoreName = new("<td[^>]*class=[\\\"']small store[\\\"'][^>]*>.*?<b>(?<name>.*?)</b>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex SellerId = new(
        "(?:showplayer\\.php\\?who=|(?:seller|owner)(?:id)?=|whichplayer=)(?<seller>\\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SellerName = new(
        "<a[^>]*href=[\\\"'][^\\\"']*(?:showplayer\\.php\\?who=|(?:seller|owner)(?:id)?=|whichplayer=)[^\\\"']*[\\\"'][^>]*>(?<name>.*?)</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex PurchaseLimit = new(
        "(?:<(?:td|span|div)\\b[^>]*\\bclass=[\\\"'][^\\\"']*(?:limit|buylimit|purchase_limit|purchaselimit)[^\\\"']*[\\\"'][^>]*>(?<body>.*?)(?:</(?:td|span|div)>|$)|\\b(?:data-limit|data-purchase-limit)=[\\\"'](?<body>\\d+))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
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
    private readonly KoLSecurityPolicy _security;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly Dictionary<string, MallSnapshot> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheGate = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<MallSnapshot>>> _inflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<OwnShopListing> _ownShopCache = [];
    private Lazy<Task<IReadOnlyList<OwnShopListing>>>? _ownShopInflight;
    private DateTimeOffset? _lastRequest;
    private DateTimeOffset? _ownShopFetchedAt;

    public KoLMallReadClient(
        HttpClient httpClient,
        Uri? baseUri = null,
        MallReadPolicy? policy = null,
        TimeProvider? clock = null,
        KoLSecurityPolicy? securityPolicy = null)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _baseUri = baseUri ?? new Uri("https://www.kingdomofloathing.com/", UriKind.Absolute);
        _policy = policy ?? new MallReadPolicy();
        if (securityPolicy is not null)
            _policy = _policy with { Security = securityPolicy };
        _policy.Validate();
        _security = _policy.Security;
        _security.EnsureAllowed(_baseUri, "KoL Mall base");
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
            _security.EnsureAllowed(uri);
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
        _security.EnsureAllowed(uri);
        try
        {
            // KoL redirects managestore.php to backoffice.php — the store
            // management lives there now. The client does not follow
            // redirects (so cookies cannot leak cross-host), so we follow the
            // location ourselves.
            var html = await GetStringFollowingRedirectAsync(uri, CancellationToken.None).ConfigureAwait(false);
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

    /// <summary>
    /// Reads a page and follows KoL's own redirect when it sends us to
    /// another page of the same host. KoL moved the store management from
    /// managestore.php to backoffice.php and answers the old URL with a
    /// redirect; the client never follows redirects on its own.
    /// </summary>
    private async Task<string> GetStringFollowingRedirectAsync(
        Uri uri, CancellationToken cancellationToken)
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
            _security.EnsureResponseUri(uri, response.RequestMessage?.RequestUri);
            if (response.Headers.Location is { } location && !response.IsSuccessStatusCode)
            {
                _security.ResolveRedirect(uri, location.ToString());
                var target = new Uri(uri, location);
                using var follow = await _http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                _security.EnsureResponseUri(target, follow.RequestMessage?.RequestUri);
                follow.EnsureSuccessStatusCode();
                var followBody = await follow.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                KoLHtmlResponseClassifier.EnsureContent(followBody, target);
                _lastRequest = _clock.GetUtcNow();
                return followBody;
            }
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            KoLHtmlResponseClassifier.EnsureContent(body, uri);
            _lastRequest = _clock.GetUtcNow();
            return body;
        }
        finally { _requestGate.Release(); }
    }

    private async Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken)    {
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lastRequest is { } last)
            {
                var wait = _policy.MinimumRequestInterval - (_clock.GetUtcNow() - last);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            _security.EnsureResponseUri(uri, response.RequestMessage?.RequestUri);
            if (response.Headers.Location is { } location && !response.IsSuccessStatusCode)
                _security.ResolveRedirect(uri, location.ToString());
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            KoLHtmlResponseClassifier.EnsureContent(body, uri);
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
            _security.EnsureResponseUri(uri, response.RequestMessage?.RequestUri);
            if (response.Headers.Location is { } location && !response.IsSuccessStatusCode)
            {
                // KoL redirects the mall search to the result page. The
                // client never follows redirects (so cookies can't leak
                // cross-host), so we follow the location ourselves.
                _security.ResolveRedirect(uri, location.ToString());
                var target = new Uri(uri, location);
                using var follow = new HttpRequestMessage(HttpMethod.Get, target);
                using var followed = await _http.SendAsync(follow, cancellationToken).ConfigureAwait(false);
                _security.EnsureResponseUri(target, followed.RequestMessage?.RequestUri);
                followed.EnsureSuccessStatusCode();
                var followBody = await followed.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                KoLHtmlResponseClassifier.EnsureContent(followBody, target);
                _lastRequest = _clock.GetUtcNow();
                return followBody;
            }
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            KoLHtmlResponseClassifier.EnsureContent(body, uri);
            _lastRequest = _clock.GetUtcNow();
            return body;
        }
        finally { _requestGate.Release(); }
    }

    /// <summary>The numeric ids of the item the search actually matched.
    /// Empty when the page carries no search-result anchors.</summary>
    private static HashSet<string> SearchedItemIds(string html)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in SearchedItemAnchor.Matches(html))
            ids.Add(match.Groups["item"].Value);
        return ids;
    }

    private IReadOnlyList<MallListing> ParseListings(string itemId, string html, Uri source, DateTimeOffset? observedAt = null)
    {
        var result = new List<MallListing>();
        // The search term can be a name ("11-leaf clover") or a numeric id.
        // KoL marks the rows of the searched item with an id of the shape
        // item_<numeric id>; the advertisement rows use a different shape.
        // Collect the ids of the searched item first, then keep only the
        // listing rows that belong to one of them.
        var searchedItemIds = SearchedItemIds(html);
        foreach (Match match in ListingRow.Matches(html))
        {
            // A Mall search can contain recommendation/advertising rows for
            // other items. Never let one of those rows enter a snapshot for
            // the requested item.
            var listedItemId = match.Groups["item"].Value;
            if (searchedItemIds.Count > 0 && !searchedItemIds.Contains(listedItemId))
                continue;
            if (searchedItemIds.Count == 0
                && !string.Equals(listedItemId, itemId, StringComparison.Ordinal))
                continue;
            var block = match.Groups["row"].Value;
            var priceMatch = MeatPrice.Match(block);
            if (!priceMatch.Success || !long.TryParse(priceMatch.Groups["price"].Value.Replace(",", ""), out var price)) continue;
            var qty = Quantity.Match(block);
            var quantity = qty.Success
                && int.TryParse(qty.Groups["quantity"].Value.Replace(",", ""), out var q)
                ? Math.Max(1, q)
                : 1;
            var store = StoreName.Match(block);
            var storeName = store.Success ? WebUtility.HtmlDecode(store.Groups["name"].Value).Trim() : null;
            var sellerIdMatch = SellerId.Match(block);
            var sellerId = sellerIdMatch.Success ? sellerIdMatch.Groups["seller"].Value : null;
            var sellerNameMatch = SellerName.Match(block);
            var sellerName = sellerNameMatch.Success
                ? WebUtility.HtmlDecode(StripTags(sellerNameMatch.Groups["name"].Value)).Trim()
                : null;
            var limitMatch = PurchaseLimit.Match(block);
            int? purchaseLimit = null;
            if (limitMatch.Success)
            {
                var limitText = limitMatch.Groups["limit"].Success
                    ? limitMatch.Groups["limit"].Value
                    : StripTags(limitMatch.Groups["body"].Value);
                var number = Regex.Match(limitText, "\\d+");
                if (number.Success && int.TryParse(number.Value, out var parsedLimit))
                    purchaseLimit = parsedLimit;
            }
            result.Add(new MallListing(listedItemId, match.Groups["store"].Value, storeName, price, quantity, purchaseLimit, observedAt ?? _clock.GetUtcNow(), source)
            {
                SellerId = sellerId,
                SellerName = sellerName
            });
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
