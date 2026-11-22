using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace KoLibri.Core.Markt;

public sealed record PricegunSale(
    DateTimeOffset SoldAt,
    long UnitPrice,
    int Quantity);

public sealed record PricegunSnapshot(
    string ItemId,
    string? DisplayName,
    IReadOnlyList<PricegunSale> Sales,
    DateTimeOffset FetchedAt,
    Uri SourceUri,
    bool IsStale = false);

public sealed record PricegunReadPolicy
{
    public TimeSpan CacheTtl { get; init; } = TimeSpan.FromMinutes(20);
    public TimeSpan MaxStale { get; init; } = TimeSpan.FromHours(12);
    public TimeSpan MinimumRequestInterval { get; init; } = TimeSpan.FromSeconds(1);
    public int MaxSales { get; init; } = 100;

    public void Validate()
    {
        if (CacheTtl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(CacheTtl));
        if (MaxStale < CacheTtl) throw new ArgumentException("MaxStale must be at least CacheTtl.", nameof(MaxStale));
        if (MinimumRequestInterval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(MinimumRequestInterval));
        if (MaxSales <= 0) throw new ArgumentOutOfRangeException(nameof(MaxSales));
    }
}

/// <summary>
/// Read-only adapter for Pricegun's public item history pages. Pricegun is a
/// historical transaction source, so its observations are sell-side quotes;
/// it is never used to place a game request.
/// </summary>
public sealed class PricegunReadClient
{
    private static readonly Regex SaleRow = new(
        "<li[^>]*>(?<row>.*?)</li>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex DateSpan = new(
        "<span[^>]*>(?<date>.*?)</span>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex QuantityPrice = new(
        "(?<quantity>[0-9][0-9,]*)\\s*@\\s*(?<price>[0-9][0-9,]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Title = new(
        "<title>(?<name>.*?)\\s+-\\s+Pricegun",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly PricegunReadPolicy _policy;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, PricegunSnapshot> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<PricegunSnapshot>>> _inflight = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _lastRequest;

    public PricegunReadClient(
        HttpClient httpClient,
        Uri? baseUri = null,
        PricegunReadPolicy? policy = null,
        TimeProvider? clock = null)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        KoLibri.Core.Basis.KoLUserAgent.ApplyTo(_http);
        _baseUri = baseUri ?? new Uri("https://pricegun.loathers.net/", UriKind.Absolute);
        if (_baseUri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Pricegun base URI must use HTTPS.", nameof(baseUri));
        _policy = policy ?? new PricegunReadPolicy();
        _policy.Validate();
        _clock = clock ?? TimeProvider.System;
    }

    public Task<PricegunSnapshot> FetchAsync(
        string itemId,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeItemId(itemId);
        var now = _clock.GetUtcNow();
        lock (_cacheGate)
        {
            if (!forceRefresh && _cache.TryGetValue(normalized, out var hit)
                && now - hit.FetchedAt <= _policy.CacheTtl)
                return Task.FromResult(hit);
        }

        var shared = _inflight.GetOrAdd(normalized, key => new Lazy<Task<PricegunSnapshot>>(
            () => FetchAndCacheAsync(key), LazyThreadSafetyMode.ExecutionAndPublication));
        return AwaitSharedAsync(normalized, shared, cancellationToken);
    }

    public void Invalidate(string? itemId = null)
    {
        lock (_cacheGate)
        {
            if (string.IsNullOrWhiteSpace(itemId)) _cache.Clear();
            else _cache.Remove(NormalizeItemId(itemId));
        }
        if (string.IsNullOrWhiteSpace(itemId)) _inflight.Clear();
        else _inflight.TryRemove(NormalizeItemId(itemId), out _);
    }

    internal static PricegunSnapshot Parse(string itemId, string html, Uri source, DateTimeOffset fetchedAt, int maxSales)
    {
        var displayName = Title.Match(html) is { Success: true } title
            ? WebUtility.HtmlDecode(title.Groups["name"].Value).Trim()
            : null;
        var sales = new List<PricegunSale>();
        foreach (Match match in SaleRow.Matches(html))
        {
            var row = match.Groups["row"].Value;
            var date = DateSpan.Match(row);
            var quote = QuantityPrice.Match(StripTags(row));
            if (!date.Success || !quote.Success
                || !long.TryParse(quote.Groups["price"].Value.Replace(",", ""), NumberStyles.None, CultureInfo.InvariantCulture, out var price)
                || !int.TryParse(quote.Groups["quantity"].Value.Replace(",", ""), NumberStyles.None, CultureInfo.InvariantCulture, out var quantity)
                || price < 0 || quantity <= 0)
                continue;

            var dateText = WebUtility.HtmlDecode(StripTags(date.Groups["date"].Value)).Trim();
            var soldAt = DateTimeOffset.TryParse(
                dateText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var parsed)
                ? parsed
                : fetchedAt;
            sales.Add(new PricegunSale(soldAt, price, quantity));
            if (sales.Count >= maxSales) break;
        }
        return new PricegunSnapshot(itemId, displayName, sales, fetchedAt, source);
    }

    private async Task<PricegunSnapshot> FetchAndCacheAsync(string itemId)
    {
        var source = new Uri(_baseUri, $"item/{Uri.EscapeDataString(itemId)}");
        var now = _clock.GetUtcNow();
        try
        {
            var html = await GetStringAsync(source, CancellationToken.None).ConfigureAwait(false);
            var snapshot = Parse(itemId, html, source, _clock.GetUtcNow(), _policy.MaxSales);
            lock (_cacheGate) _cache[itemId] = snapshot;
            return snapshot;
        }
        catch
        {
            lock (_cacheGate)
            {
                if (_cache.TryGetValue(itemId, out var stale)
                    && now - stale.FetchedAt <= _policy.MaxStale)
                    return stale with { IsStale = true };
            }
            throw;
        }
    }

    private async Task<PricegunSnapshot> AwaitSharedAsync(
        string itemId,
        Lazy<Task<PricegunSnapshot>> shared,
        CancellationToken cancellationToken)
    {
        try
        {
            return await shared.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (shared.IsValueCreated && shared.Value.IsCompleted)
                _inflight.TryRemove(new KeyValuePair<string, Lazy<Task<PricegunSnapshot>>>(itemId, shared));
        }
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
        finally
        {
            _requestGate.Release();
        }
    }

    private static string NormalizeItemId(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId)) throw new ArgumentException("Item id is required.", nameof(itemId));
        var normalized = itemId.Trim();
        if (!normalized.All(char.IsDigit)) throw new ArgumentException("Pricegun item id must be numeric.", nameof(itemId));
        return normalized;
    }

    private static string StripTags(string value)
    {
        var withoutComments = Regex.Replace(value, "<!--.*?-->", "", RegexOptions.Singleline);
        return WebUtility.HtmlDecode(Regex.Replace(withoutComments, "<[^>]+>", " "));
    }
}

/// <summary>Exposes Pricegun's recent transaction history to the pricing engine.</summary>
public sealed class PricegunPriceProvider : IPriceProvider
{
    private readonly PricegunReadClient _client;
    private readonly decimal _confidence;

    public PricegunPriceProvider(PricegunReadClient client, decimal confidence = 0.65m)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        if (confidence is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(confidence));
        _confidence = confidence;
    }

    public async Task<IReadOnlyCollection<PriceObservation>> FetchAsync(
        string itemId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _client.FetchAsync(itemId, cancellationToken: cancellationToken).ConfigureAwait(false);
        return snapshot.Sales.Select((sale, index) => new PriceObservation(
            snapshot.ItemId,
            PriceSource.Pricegun,
            QuoteSide.Sell,
            sale.UnitPrice,
            sale.SoldAt,
            quantity: sale.Quantity,
            confidence: snapshot.IsStale ? _confidence * 0.7m : _confidence,
            listingId: $"pricegun:{snapshot.ItemId}:{index}")).ToArray();
    }
}
