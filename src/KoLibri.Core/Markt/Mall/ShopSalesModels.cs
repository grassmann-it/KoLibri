using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Markt;

/// <summary>
/// A single sale from the authenticated character's own-store activity log.
/// BuyerKey is an HMAC-derived local identifier; raw buyer names are never
/// returned by the read client. ItemName is the display name from KoL because
/// the sales activity page does not include a numeric item id.
/// </summary>
public sealed record ShopSale(
    string BuyerKey,
    string ItemName,
    int Quantity,
    long TotalPrice,
    long UnitPrice,
    DateTimeOffset? SoldAt,
    DateTimeOffset ObservedAt,
    // Kept only in memory for the local CRM sync. JSON reports deliberately
    // omit it so a normal analytics export still contains no raw buyer name.
    [property: JsonIgnore] string? RawBuyerName = null);

public sealed record ShopSalesSnapshot(
    IReadOnlyList<ShopSale> Sales,
    DateTimeOffset FetchedAt,
    Uri SourceUri,
    bool IsStale = false);

public sealed record ShopSalesReadPolicy
{
    public TimeSpan CacheTtl { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan MaxStale { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan MinimumRequestInterval { get; init; } = TimeSpan.FromSeconds(1);
    public int MaxEntries { get; init; } = 2_000;

    public void Validate()
    {
        if (CacheTtl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(CacheTtl));
        if (MaxStale < CacheTtl) throw new ArgumentException("MaxStale must be at least CacheTtl.", nameof(MaxStale));
        if (MinimumRequestInterval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(MinimumRequestInterval));
        if (MaxEntries <= 0) throw new ArgumentOutOfRangeException(nameof(MaxEntries));
    }
}

public interface IShopSalesReadClient
{
    Task<ShopSalesSnapshot> FetchSalesAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);
}

/// <summary>
/// Generates stable-in-process, non-reversible buyer identifiers. Pass a
/// caller-owned secret to retain the same identifiers between processes; when
/// omitted, a random process key is used so names cannot be correlated across
/// separate runs by accident.
/// </summary>
public sealed class BuyerIdentityHasher
{
    private static readonly Lazy<byte[]> ProcessKey = new(CreateProcessKey, LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly byte[] _key;

    public BuyerIdentityHasher(byte[]? key = null)
    {
        _key = key is { Length: > 0 } ? key.ToArray() : ProcessKey.Value.ToArray();
    }

    public string Hash(string buyerName)
    {
        var normalized = Normalize(buyerName);
        if (normalized.Length == 0) throw new ArgumentException("Buyer name is required.", nameof(buyerName));

        using var hmac = new HMACSHA256(_key);
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(normalized));
        // 96 bits is enough for a local report while avoiding raw names.
        return $"buyer_{Convert.ToHexString(digest.AsSpan(0, 12)).ToLowerInvariant()}";
    }

    public static string Normalize(string buyerName)
        => (buyerName ?? string.Empty).Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant();

    private static byte[] CreateProcessKey()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        return key;
    }
}

/// <summary>Read-only adapter for KoL's backoffice sales activity page.</summary>
public sealed class KoLShopSalesReadClient : IShopSalesReadClient
{
    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly ShopSalesReadPolicy _policy;
    private readonly TimeProvider _clock;
    private readonly BuyerIdentityHasher _buyerHasher;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly object _cacheGate = new();
    private Lazy<Task<ShopSalesSnapshot>>? _inflight;
    private ShopSalesSnapshot? _cache;
    private DateTimeOffset? _lastRequest;

    public KoLShopSalesReadClient(
        HttpClient httpClient,
        Uri? baseUri = null,
        ShopSalesReadPolicy? policy = null,
        TimeProvider? clock = null,
        BuyerIdentityHasher? buyerHasher = null)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _baseUri = baseUri ?? new Uri("https://www.kingdomofloathing.com/", UriKind.Absolute);
        if (!_baseUri.IsAbsoluteUri || _baseUri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("KoL sales log requires an HTTPS base URI.", nameof(baseUri));
        _policy = policy ?? new ShopSalesReadPolicy();
        _policy.Validate();
        _clock = clock ?? TimeProvider.System;
        _buyerHasher = buyerHasher ?? new BuyerIdentityHasher();
    }

    public Task<ShopSalesSnapshot> FetchSalesAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        lock (_cacheGate)
        {
            if (!forceRefresh && _cache is { } cached && now - cached.FetchedAt <= _policy.CacheTtl)
                return Task.FromResult(cached);
        }

        Lazy<Task<ShopSalesSnapshot>> shared;
        lock (_cacheGate)
        {
            if (!forceRefresh && _inflight is not null)
                shared = _inflight;
            else
                shared = _inflight = new Lazy<Task<ShopSalesSnapshot>>(FetchCoreAsync, LazyThreadSafetyMode.ExecutionAndPublication);
        }
        return AwaitSharedAsync(shared, cancellationToken);
    }

    private async Task<ShopSalesSnapshot> AwaitSharedAsync(
        Lazy<Task<ShopSalesSnapshot>> shared,
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
                    if (ReferenceEquals(_inflight, shared)) _inflight = null;
                }
            }
        }
    }

    private async Task<ShopSalesSnapshot> FetchCoreAsync()
    {
        var uri = new Uri(_baseUri, KoLRoutes.StoreSalesLog);
        try
        {
            var html = await GetStringAsync(uri, CancellationToken.None).ConfigureAwait(false);
            var fetchedAt = _clock.GetUtcNow();
            var sales = ShopSalesLogParser.Parse(html, fetchedAt, _buyerHasher, _policy.MaxEntries);
            var snapshot = new ShopSalesSnapshot(sales, fetchedAt, uri);
            lock (_cacheGate) _cache = snapshot;
            return snapshot;
        }
        catch
        {
            lock (_cacheGate)
            {
                if (_cache is { } stale && _clock.GetUtcNow() - stale.FetchedAt <= _policy.MaxStale)
                    return stale with { IsStale = true };
            }
            throw;
        }
    }

    public void Invalidate()
    {
        lock (_cacheGate)
        {
            _cache = null;
            _inflight = null;
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
        finally { _requestGate.Release(); }
    }
}

/// <summary>Parser for the text block emitted by backoffice.php?which=3.</summary>
public static class ShopSalesLogParser
{
    private static readonly Regex Span = new(
        "<span\\b(?<attributes>[^>]*)>(?<body>.*?)</span>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex Break = new("<br\\s*/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SaleLine = new(
        @"^(?:\d+\s*:\s*)?(?<date>\d{1,2}/\d{1,2}/\d{2,4})\s+(?<time>\d{1,2}:\d{2}:\d{2})\s+(?<buyer>.+?)\s+bought\s+(?<quantity>\d+)\s+\((?<item>.*)\)\s+for\s+(?<total>[\d,]+)\s+Meat\.?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled | RegexOptions.Singleline);

    public static IReadOnlyList<ShopSale> Parse(
        string html,
        DateTimeOffset observedAt,
        BuyerIdentityHasher? buyerHasher = null,
        int maxEntries = 2_000)
    {
        if (string.IsNullOrWhiteSpace(html)) return Array.Empty<ShopSale>();
        if (maxEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        buyerHasher ??= new BuyerIdentityHasher();

        var entries = new List<string>();
        var spanFound = false;
        foreach (Match span in Span.Matches(html))
        {
            if (!Regex.IsMatch(span.Groups["attributes"].Value, "\\bclass\\s*=\\s*(?:[\"'][^\"']*\\bsmall\\b[^\"']*[\"']|small(?:\\s|$))", RegexOptions.IgnoreCase))
                continue;
            spanFound = true;
            AddEntries(entries, span.Groups["body"].Value);
        }
        if (!spanFound) AddEntries(entries, html);

        var result = new List<ShopSale>(Math.Min(entries.Count, maxEntries));
        foreach (var entry in entries)
        {
            if (result.Count >= maxEntries) break;
            var match = SaleLine.Match(entry);
            if (!match.Success) continue;
            if (!int.TryParse(match.Groups["quantity"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var quantity)
                || quantity <= 0)
                continue;
            if (!long.TryParse(match.Groups["total"].Value.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.None, CultureInfo.InvariantCulture, out var total)
                || total < 0)
                continue;

            var buyer = WebUtility.HtmlDecode(match.Groups["buyer"].Value).Trim();
            var item = WebUtility.HtmlDecode(match.Groups["item"].Value).Trim();
            if (buyer.Length == 0 || item.Length == 0) continue;
            var soldAt = ParseTimestamp(match.Groups["date"].Value, match.Groups["time"].Value);
            result.Add(new ShopSale(
                buyerHasher.Hash(buyer),
                item,
                quantity,
                total,
                total / quantity,
                soldAt,
                observedAt,
                buyer));
        }
        return result;
    }

    private static void AddEntries(List<string> entries, string html)
    {
        var decoded = WebUtility.HtmlDecode(html);
        foreach (var part in Break.Split(decoded))
        {
            var text = Tags.Replace(part, " ");
            text = Regex.Replace(text, @"\s+", " ").Trim();
            if (text.Length > 0) entries.Add(text);
        }
    }

    private static DateTimeOffset? ParseTimestamp(string date, string time)
    {
        var value = $"{date} {time}";
        return DateTimeOffset.TryParseExact(
            value,
            ["M/d/yy H:mm:ss", "M/d/yyyy H:mm:ss", "MM/dd/yy HH:mm:ss", "MM/dd/yyyy HH:mm:ss"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }
}

public sealed record ShopBuyerAnalyticsOptions
{
    public long HighValueMeatThreshold { get; init; } = 100_000;
    public int BulkQuantityThreshold { get; init; } = 10;
    public int RepeatPurchaseThreshold { get; init; } = 2;
    public TimeSpan BurstWindow { get; init; } = TimeSpan.FromMinutes(5);
    public int BurstSaleThreshold { get; init; } = 3;
    /// <summary>Already-hashed keys for accounts the owner explicitly configured.</summary>
    public IReadOnlySet<string> KnownAccountBuyerKeys { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public void Validate()
    {
        if (HighValueMeatThreshold < 0) throw new ArgumentOutOfRangeException(nameof(HighValueMeatThreshold));
        if (BulkQuantityThreshold <= 0) throw new ArgumentOutOfRangeException(nameof(BulkQuantityThreshold));
        if (RepeatPurchaseThreshold <= 0) throw new ArgumentOutOfRangeException(nameof(RepeatPurchaseThreshold));
        if (BurstWindow <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(BurstWindow));
        if (BurstSaleThreshold <= 1) throw new ArgumentOutOfRangeException(nameof(BurstSaleThreshold));
    }
}

public sealed record ShopBuyerProfile(
    string BuyerKey,
    int SaleCount,
    int DistinctItemCount,
    int TotalQuantity,
    long TotalMeat,
    DateTimeOffset? FirstSeen,
    DateTimeOffset? LastSeen,
    decimal AverageUnitPrice,
    IReadOnlyList<string> ItemNames,
    IReadOnlyList<string> Flags);

public sealed record ShopBuyerLinkCandidate(
    string BuyerKeyA,
    string BuyerKeyB,
    decimal Score,
    string Confidence,
    IReadOnlyList<string> Signals);

public sealed record ShopBuyerAnalyticsReport(
    DateTimeOffset GeneratedAt,
    int SaleCount,
    IReadOnlyList<ShopBuyerProfile> Buyers,
    IReadOnlyList<ShopBuyerLinkCandidate> PossibleSharedOperators);

/// <summary>
/// Local CRM-style summaries for the store owner. Signals describe purchase
/// patterns only; they do not establish that characters share an account.
/// </summary>
public static class ShopBuyerAnalytics
{
    public static ShopBuyerAnalyticsReport Analyze(
        IEnumerable<ShopSale> sales,
        ShopBuyerAnalyticsOptions? options = null,
        DateTimeOffset? generatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(sales);
        options ??= new ShopBuyerAnalyticsOptions();
        options.Validate();
        var rows = sales.Where(x => !string.IsNullOrWhiteSpace(x.BuyerKey) && x.Quantity > 0).ToArray();
        var groups = rows.GroupBy(x => x.BuyerKey, StringComparer.Ordinal).ToArray();
        var profiles = groups
            .Select(group => BuildProfile(group.Key, group.ToArray(), options))
            .OrderByDescending(x => x.TotalMeat)
            .ThenBy(x => x.BuyerKey, StringComparer.Ordinal)
            .ToArray();

        return new ShopBuyerAnalyticsReport(
            generatedAt ?? DateTimeOffset.UtcNow,
            rows.Length,
            profiles,
            FindSharedOperatorCandidates(groups, options));
    }

    private static ShopBuyerProfile BuildProfile(string key, IReadOnlyList<ShopSale> sales, ShopBuyerAnalyticsOptions options)
    {
        var totalQuantity = sales.Sum(x => x.Quantity);
        var totalMeat = sales.Sum(x => x.TotalPrice);
        var timestamps = sales.Where(x => x.SoldAt.HasValue).Select(x => x.SoldAt!.Value).OrderBy(x => x).ToArray();
        var flags = new List<string>();
        if (sales.Count >= options.RepeatPurchaseThreshold) flags.Add("repeat-buyer");
        if (totalMeat >= options.HighValueMeatThreshold) flags.Add("high-value");
        if (totalQuantity >= options.BulkQuantityThreshold) flags.Add("bulk");
        if (options.KnownAccountBuyerKeys.Contains(key)) flags.Add("configured-account-match");
        if (HasBurst(timestamps, options.BurstWindow, options.BurstSaleThreshold)) flags.Add("burst-pattern-low-confidence");

        return new ShopBuyerProfile(
            key,
            sales.Count,
            sales.Select(x => x.ItemName).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            totalQuantity,
            totalMeat,
            timestamps.Length == 0 ? null : timestamps[0],
            timestamps.Length == 0 ? null : timestamps[^1],
            totalQuantity == 0 ? 0m : (decimal)totalMeat / totalQuantity,
            sales.Select(x => x.ItemName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(30).ToArray(),
            flags);
    }

    private static IReadOnlyList<ShopBuyerLinkCandidate> FindSharedOperatorCandidates(
        IReadOnlyList<IGrouping<string, ShopSale>> groups,
        ShopBuyerAnalyticsOptions options)
    {
        var candidates = new List<ShopBuyerLinkCandidate>();
        for (var i = 0; i < groups.Count; i++)
        for (var j = i + 1; j < groups.Count; j++)
        {
            var left = groups[i].ToArray();
            var right = groups[j].ToArray();
            if (left.Length < options.RepeatPurchaseThreshold || right.Length < options.RepeatPurchaseThreshold) continue;
            var leftItems = left.Select(x => x.ItemName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rightItems = right.Select(x => x.ItemName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var overlap = leftItems.Intersect(rightItems, StringComparer.OrdinalIgnoreCase).Count();
            var union = leftItems.Union(rightItems, StringComparer.OrdinalIgnoreCase).Count();
            if (union == 0) continue;
            var itemSimilarity = (decimal)overlap / union;
            var closeTimes = 0;
            foreach (var first in left.Where(x => x.SoldAt.HasValue))
                if (right.Where(x => x.SoldAt.HasValue).Any(second => Math.Abs((first.SoldAt!.Value - second.SoldAt!.Value).TotalMinutes) <= 2))
                    closeTimes++;
            var timingSimilarity = (decimal)closeTimes / Math.Max(left.Length, right.Length);
            var score = itemSimilarity * 0.55m + timingSimilarity * 0.45m;
            if (score < 0.8m) continue;
            candidates.Add(new ShopBuyerLinkCandidate(
                groups[i].Key,
                groups[j].Key,
                decimal.Round(score, 3),
                "weak",
                ["overlapping-items", "nearby-purchase-times", "pattern-only; manual review required"]));
        }
        return candidates.OrderByDescending(x => x.Score).ToArray();
    }

    private static bool HasBurst(IReadOnlyList<DateTimeOffset> timestamps, TimeSpan window, int threshold)
    {
        for (var i = 0; i < timestamps.Count; i++)
        {
            var end = timestamps[i] + window;
            var count = 0;
            for (var j = i; j < timestamps.Count && timestamps[j] <= end; j++) count++;
            if (count >= threshold) return true;
        }
        return false;
    }
}
