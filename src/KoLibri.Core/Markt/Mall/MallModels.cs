using System.Net;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Markt;

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

    /// <summary>Item name from the result header, when the page carries one.</summary>
    public string? ItemName { get; init; }

    /// <summary>False when the row showed no readable stock; <see cref="Quantity"/> is then 0, never a guessed 1.</summary>
    public bool QuantityKnown { get; init; } = true;

    /// <summary>
    /// Distinguishes "no daily limit" from "limit not readable".
    /// <see cref="PurchaseLimit"/> is only set for <see cref="MallLimitKind.Limited"/>.
    /// </summary>
    public MallLimitKind LimitKind { get; init; } = MallLimitKind.Unknown;

    /// <summary>Compatibility alias used by purchase planners.</summary>
    public string? Seller => SellerId;

    /// <summary>Compatibility alias for owner-oriented Mall terminology.</summary>
    public string? OwnerId => SellerId;
}

public enum MallLimitKind { Unknown, Unlimited, Limited }

/// <summary>All offers of one item found by a search, cheapest first.</summary>
public sealed record MallItemOffers(
    string ItemId,
    string? ItemName,
    IReadOnlyList<MallListing> Listings,
    int TotalOffers)
{
    /// <summary>True when more offers existed than were kept.</summary>
    public bool IsTruncated => TotalOffers > Listings.Count;
}

/// <summary>A search result with the offers kept per item (a partial name can match several).</summary>
public sealed record MallSearchPage(
    string Term,
    IReadOnlyList<MallItemOffers> Items,
    DateTimeOffset FetchedAt,
    Uri SourceUri,
    bool IsStale = false)
{
    /// <summary>True when the term matched more than one item.</summary>
    public bool IsPartial => Items.Count > 1;
}

/// <summary>The mall page did not look like anything the parser knows.</summary>
public sealed class MallSearchParseException(string message) : Exception(message);

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

    /// <summary>
    /// Search that keeps the offers per item. Implementations without item
    /// grouping fall back to one group built from <see cref="SearchAsync"/>.
    /// </summary>
    async Task<MallSearchPage> SearchItemsAsync(string term, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var snapshot = await SearchAsync(term, forceRefresh, cancellationToken).ConfigureAwait(false);
        var groups = snapshot.Listings.GroupBy(l => l.ItemId)
            .Select(g => new MallItemOffers(g.Key, g.Select(l => l.ItemName).FirstOrDefault(n => n is not null),
                g.OrderBy(l => l.Price).ToArray(), g.Count()))
            .ToArray();
        return new MallSearchPage(term, groups, snapshot.FetchedAt, snapshot.SourceUri, snapshot.IsStale);
    }
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
    /// <summary>The "1&amp;nbsp;/&amp;nbsp;day" cell KoL prints for a per-day buy limit.</summary>
    private static readonly Regex DayLimit = new(
        "(?<limit>\\d[\\d,]*)\\s*(?:&nbsp;|\\s)*/\\s*(?:&nbsp;|\\s)*day",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SmallCell = new(
        "<td[^>]*class=[\\\"']small(?<cls>[^\\\"']*)[\\\"'][^>]*>(?<body>.*?)</td>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex ItemHeaderRow = new(
        "<tr[^>]*id=[\\\"']item_(?<item>\\d+)[\\\"'][^>]*>(?<row>.*?)</tr>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex HeaderName = new(
        "<b>\\s*(?:<a[^>]*>)?(?<name>[^<]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex NoResultsMarker = new(
        "searchresults|search results|no results|did not match|nothing (?:was )?found|no stores",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
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
    private readonly Func<string, string?>? _itemNameResolver;
    private readonly Dictionary<string, MallSearchPage> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheGate = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<MallSearchPage>>> _inflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<OwnShopListing> _ownShopCache = [];
    private Lazy<Task<IReadOnlyList<OwnShopListing>>>? _ownShopInflight;
    private DateTimeOffset? _lastRequest;
    private DateTimeOffset? _ownShopFetchedAt;

    public KoLMallReadClient(
        HttpClient httpClient,
        Uri? baseUri = null,
        MallReadPolicy? policy = null,
        TimeProvider? clock = null,
        KoLSecurityPolicy? securityPolicy = null,
        Func<string, string?>? itemNameResolver = null)
    {
        _itemNameResolver = itemNameResolver;
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

    public async Task<MallSnapshot> SearchAsync(string itemId, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var page = await SearchItemsAsync(itemId, forceRefresh, cancellationToken).ConfigureAwait(false);
        return ToSnapshot(NormalizeId(itemId), page);
    }

    /// <summary>Search that keeps offers per item and marks partial matches.</summary>
    public Task<MallSearchPage> SearchItemsAsync(string term, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeId(term);
        var now = _clock.GetUtcNow();
        lock (_cacheGate)
        {
            if (!forceRefresh && _cache.TryGetValue(normalized, out var hit) && now - hit.FetchedAt <= _policy.CacheTtl)
                return Task.FromResult(hit);
        }

        var shared = _inflight.GetOrAdd(normalized, key => new Lazy<Task<MallSearchPage>>(
            () => FetchAndCacheAsync(key), LazyThreadSafetyMode.ExecutionAndPublication));
        return AwaitSharedAsync(normalized, shared, cancellationToken);
    }

    /// <summary>
    /// Legacy single-list view of a page: the group of the searched id or
    /// exact name when there is one, otherwise every group merged and sorted
    /// by price before the per-item cap is applied.
    /// </summary>
    private MallSnapshot ToSnapshot(string term, MallSearchPage page)
    {
        var bare = term.Trim().Trim('"');
        var picked = page.Items.Where(g => g.ItemId == bare
                || string.Equals(g.ItemName, bare, StringComparison.OrdinalIgnoreCase)).ToArray();
        var groups = picked.Length > 0 ? picked : page.Items.ToArray();
        var listings = groups.SelectMany(g => g.Listings).OrderBy(l => l.Price)
            .Take(_policy.MaxListingsPerItem).ToArray();
        return new MallSnapshot(term, listings, page.FetchedAt, page.SourceUri, page.IsStale);
    }

    private async Task<MallSearchPage> AwaitSharedAsync(
        string normalized,
        Lazy<Task<MallSearchPage>> shared,
        CancellationToken cancellationToken)
    {
        try
        {
            return await shared.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (shared.IsValueCreated && shared.Value.IsCompleted)
                _inflight.TryRemove(new KeyValuePair<string, Lazy<Task<MallSearchPage>>>(normalized, shared));
        }
    }

    private async Task<MallSearchPage> FetchAndCacheAsync(string normalized)
    {
        var now = _clock.GetUtcNow();

        try
        {
            var uri = new Uri(_baseUri, KoLRoutes.MallSearch);
            _security.EnsureAllowed(uri);
            var html = await PostStringAsync(uri, new Dictionary<string, string>
            {
                ["pudnuggler"] = SearchTermFor(normalized),
                ["category"] = "allitems",
                ["justitems"] = "1",
                ["sortresultsby"] = "price",
                ["x_cheapest"] = "0",
                ["max_price"] = "0"
            }, CancellationToken.None).ConfigureAwait(false);
            // Unknown markup throws here and is never cached as "no offers".
            var page = ParsePage(normalized, html, uri, null);
            lock (_cacheGate) _cache[normalized] = page;
            return page;
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
        var uri = new Uri(_baseUri, KoLRoutes.StoreInventory);
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

    /// <summary>Flat, price-sorted list for the item(s) of a page (kept for callers that want one list).</summary>
    private IReadOnlyList<MallListing> ParseListings(string itemId, string html, Uri source, DateTimeOffset? observedAt = null)
        => ToSnapshot(itemId, ParsePage(itemId, html, source, observedAt)).Listings;

    private MallSearchPage ParsePage(string term, string html, Uri source, DateTimeOffset? observedAt)
    {
        var observed = observedAt ?? _clock.GetUtcNow();
        // KoL marks the searched items with results_<id> anchors; rows of
        // other items (advertisements) must not enter the result. Without
        // anchors a numeric term pins the id, a name term accepts all rows.
        var anchorIds = SearchedItemIds(html);
        var bare = term.Trim().Trim('"');
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match header in ItemHeaderRow.Matches(html))
        {
            var name = HeaderName.Match(header.Groups["row"].Value);
            if (name.Success)
                names.TryAdd(header.Groups["item"].Value, WebUtility.HtmlDecode(name.Groups["name"].Value).Trim());
        }

        var order = new List<string>();
        var byItem = new Dictionary<string, List<MallListing>>(StringComparer.Ordinal);
        var rowsSeen = 0;
        foreach (Match match in ListingRow.Matches(html))
        {
            var listedItemId = match.Groups["item"].Value;
            if (anchorIds.Count > 0 && !anchorIds.Contains(listedItemId)) continue;
            if (anchorIds.Count == 0 && bare.All(char.IsDigit) && bare.Length > 0 && listedItemId != bare) continue;
            rowsSeen++;
            var block = match.Groups["row"].Value;
            var priceMatch = MeatPrice.Match(block);
            if (!priceMatch.Success || !long.TryParse(priceMatch.Groups["price"].Value.Replace(",", ""), out var price)) continue;
            var qty = Quantity.Match(block);
            var quantityKnown = qty.Success
                && int.TryParse(qty.Groups["quantity"].Value.Replace(",", ""), out _);
            var quantity = quantityKnown ? Math.Max(0, int.Parse(qty.Groups["quantity"].Value.Replace(",", ""))) : 0;
            var store = StoreName.Match(block);
            var storeName = store.Success ? WebUtility.HtmlDecode(StripTags(store.Groups["name"].Value)).Trim() : null;
            var sellerIdMatch = SellerId.Match(block);
            var sellerId = sellerIdMatch.Success ? sellerIdMatch.Groups["seller"].Value : null;
            var sellerNameMatch = SellerName.Match(block);
            var sellerName = sellerNameMatch.Success
                ? WebUtility.HtmlDecode(StripTags(sellerNameMatch.Groups["name"].Value)).Trim()
                : null;
            var (limitKind, purchaseLimit) = ReadLimit(block);
            if (!byItem.TryGetValue(listedItemId, out var list))
            {
                byItem[listedItemId] = list = [];
                order.Add(listedItemId);
            }
            list.Add(new MallListing(listedItemId, match.Groups["store"].Value, storeName, price, quantity, purchaseLimit, observed, source)
            {
                SellerId = sellerId,
                SellerName = sellerName,
                ItemName = names.GetValueOrDefault(listedItemId),
                QuantityKnown = quantityKnown,
                LimitKind = limitKind
            });
        }

        if (byItem.Count == 0)
        {
            if (rowsSeen > 0)
                throw new MallSearchParseException(
                    "The mall page has offer rows but no price could be read. The markup may have changed.");
            if (!NoResultsMarker.IsMatch(html))
                throw new MallSearchParseException(
                    "The mall page is not a search result page the parser recognises. Not treated as 'no offers'.");
        }

        var items = order.Select(id =>
        {
            var sorted = byItem[id].OrderBy(l => l.Price).ToArray();
            return new MallItemOffers(id, names.GetValueOrDefault(id),
                sorted.Take(_policy.MaxListingsPerItem).ToArray(), sorted.Length);
        }).ToArray();
        return new MallSearchPage(term, items, observed, source);
    }

    private static (MallLimitKind Kind, int? Limit) ReadLimit(string block)
    {
        var classed = PurchaseLimit.Match(block);
        if (classed.Success)
        {
            var text = classed.Groups["limit"].Success ? classed.Groups["limit"].Value : StripTags(classed.Groups["body"].Value);
            var number = Regex.Match(text, "\\d+");
            if (number.Success && int.TryParse(number.Value, out var n)) return (MallLimitKind.Limited, n);
        }
        var cells = SmallCell.Matches(block)
            .Select(c => (Cls: c.Groups["cls"].Value, Body: c.Groups["body"].Value)).ToArray();
        foreach (var cell in cells)
        {
            var day = DayLimit.Match(WebUtility.HtmlDecode(cell.Body));
            if (day.Success && int.TryParse(day.Groups["limit"].Value.Replace(",", ""), out var limit))
                return (MallLimitKind.Limited, limit);
        }
        var fallback = DayLimit.Match(WebUtility.HtmlDecode(block));
        if (fallback.Success && int.TryParse(fallback.Groups["limit"].Value.Replace(",", ""), out var f))
            return (MallLimitKind.Limited, f);
        // A limit column that exists but is empty means "no limit"; a row
        // without such a column tells us nothing.
        var other = cells.Where(c => !c.Cls.Contains("store") && !c.Cls.Contains("stock") && !c.Cls.Contains("price")).ToArray();
        if (other.Length > 0 && other.All(c => string.IsNullOrWhiteSpace(StripTags(c.Body)))) return (MallLimitKind.Unlimited, null);
        return (MallLimitKind.Unknown, null);
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

    /// <summary>
    /// KoL's mall search is name based: posting a bare number searches for the
    /// text "10881" and finds nothing. A numeric term is therefore turned into
    /// the exact quoted item name; the parser still pins the numeric id.
    /// Without a resolvable name this throws instead of reporting "no offers".
    /// </summary>
    private string SearchTermFor(string term)
    {
        var bare = term.Trim().Trim('"');
        if (bare.Length == 0 || !bare.All(char.IsDigit)) return term;
        var name = _itemNameResolver?.Invoke(bare);
        if (string.IsNullOrWhiteSpace(name))
            throw new MallSearchParseException(
                $"Item id {bare} cannot be searched in the mall: its name is unknown (item data missing). Search by item name instead.");
        return "\"" + name.Trim() + "\"";
    }

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
