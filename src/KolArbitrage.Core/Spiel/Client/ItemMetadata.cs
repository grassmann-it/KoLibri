using System.Net;
using System.Text.RegularExpressions;

namespace KolArbitrage.Core.Spiel;

public sealed record ItemMetadata(
    string ItemId,
    string Name,
    DateTimeOffset FetchedAt,
    Uri SourceUri,
    bool IsStale = false);

public sealed record ItemMetadataPolicy
{
    public TimeSpan CacheTtl { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan MaxStale { get; init; } = TimeSpan.FromDays(30);
    public TimeSpan MinimumRequestInterval { get; init; } = TimeSpan.FromSeconds(1);

    public void Validate()
    {
        if (CacheTtl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(CacheTtl));
        if (MaxStale < CacheTtl) throw new ArgumentException("MaxStale must be at least CacheTtl.", nameof(MaxStale));
        if (MinimumRequestInterval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(MinimumRequestInterval));
    }
}

public interface IItemMetadataClient
{
    Task<ItemMetadata> GetAsync(string itemId, bool forceRefresh = false, CancellationToken cancellationToken = default);
}

/// <summary>Read-only item-name lookup through desc_item.php.</summary>
public sealed class KoLItemMetadataClient : IItemMetadataClient
{
    private static readonly Regex Title = new("<title[^>]*>(?<value>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Bold = new("<b[^>]*>(?<value>[^<]{2,120})</b>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly ItemMetadataPolicy _policy;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly Dictionary<string, ItemMetadata> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheGate = new();
    private DateTimeOffset? _lastRequest;

    public KoLItemMetadataClient(HttpClient httpClient, Uri? baseUri = null, ItemMetadataPolicy? policy = null, TimeProvider? clock = null)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _baseUri = baseUri ?? new Uri("https://www.kingdomofloathing.com/", UriKind.Absolute);
        _policy = policy ?? new ItemMetadataPolicy();
        _policy.Validate();
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<ItemMetadata> GetAsync(string itemId, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeId(itemId);
        var now = _clock.GetUtcNow();
        lock (_cacheGate)
        {
            if (!forceRefresh && _cache.TryGetValue(normalized, out var cached) && now - cached.FetchedAt <= _policy.CacheTtl)
                return cached;
        }

        var uri = new Uri(_baseUri, $"desc_item.php?whichitem={Uri.EscapeDataString(normalized)}");
        try
        {
            var html = await GetStringAsync(uri, cancellationToken).ConfigureAwait(false);
            var name = ParseName(html, normalized);
            var result = new ItemMetadata(normalized, name, _clock.GetUtcNow(), uri);
            lock (_cacheGate) _cache[normalized] = result;
            return result;
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            lock (_cacheGate)
            {
                if (_cache.TryGetValue(normalized, out var stale) && now - stale.FetchedAt <= _policy.MaxStale)
                    return stale with { IsStale = true };
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

    private static string ParseName(string html, string itemId)
    {
        var title = Title.Match(html);
        if (title.Success)
        {
            var value = Clean(title.Groups["value"].Value);
            if (!string.IsNullOrWhiteSpace(value) && !value.Contains("Kingdom of Loathing", StringComparison.OrdinalIgnoreCase))
                return value;
        }

        foreach (Match match in Bold.Matches(html))
        {
            var value = Clean(match.Groups["value"].Value);
            if (!string.IsNullOrWhiteSpace(value) && !value.Contains("item description", StringComparison.OrdinalIgnoreCase))
                return value;
        }

        return $"Item {itemId}";
    }

    private static string Clean(string value) => WebUtility.HtmlDecode(Regex.Replace(value, "\\s+", " ")).Trim();

    private static string NormalizeId(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId)) throw new ArgumentException("Item id is required.", nameof(itemId));
        return itemId.Trim();
    }
}
