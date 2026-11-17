using System.Text.Json;
using KolArbitrage.Core.Security;

namespace KolArbitrage.Core.Integration;

public sealed record KoLStatusSnapshot(
    string? PlayerName,
    int Adventures,
    long Meat,
    int Level,
    DateTimeOffset FetchedAt,
    bool IsStale = false);

public sealed record KoLInventoryItem(
    string ItemId,
    string Name,
    int Quantity);

public sealed record KoLInventorySnapshot(
    IReadOnlyList<KoLInventoryItem> Items,
    DateTimeOffset FetchedAt,
    bool IsStale = false);

public sealed record AccountReadPolicy
{
    public TimeSpan CacheTtl { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan MaxStale { get; init; } = TimeSpan.FromHours(12);
    public TimeSpan MinimumRequestInterval { get; init; } = TimeSpan.FromSeconds(1);
    public KoLSecurityPolicy Security { get; init; } = new();
    public KoLSecurityPolicy SecurityPolicy
    {
        get => Security;
        init => Security = value;
    }
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
        (Security ?? throw new ArgumentException("A KoL account HTTP security policy is required.", nameof(Security))).Validate();
    }
}

public interface IAccountReadClient
{
    Task<KoLStatusSnapshot> FetchStatusAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);
    Task<KoLInventorySnapshot> FetchInventoryAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);
}

/// <summary>
/// Read-only API adapter for status and inventory. It never sends a mutating
/// request, and it keeps the last good snapshot for short outages.
/// </summary>
public sealed class KoLAccountReadClient : IAccountReadClient
{
    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly AccountReadPolicy _policy;
    private readonly KoLSecurityPolicy _security;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly object _cacheGate = new();
    private DateTimeOffset? _lastRequest;
    private KoLStatusSnapshot? _status;
    private KoLInventorySnapshot? _inventory;

    public KoLAccountReadClient(
        HttpClient httpClient,
        Uri? baseUri = null,
        AccountReadPolicy? policy = null,
        TimeProvider? clock = null,
        KoLSecurityPolicy? securityPolicy = null)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _baseUri = baseUri ?? new Uri("https://www.kingdomofloathing.com/", UriKind.Absolute);
        _policy = policy ?? new AccountReadPolicy();
        if (securityPolicy is not null)
            _policy = _policy with { Security = securityPolicy };
        _policy.Validate();
        _security = _policy.Security;
        _security.EnsureAllowed(_baseUri, "KoL account base");
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<KoLStatusSnapshot> FetchStatusAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        lock (_cacheGate)
        {
            if (!forceRefresh && _status is { } cached && now - cached.FetchedAt <= _policy.CacheTtl)
                return cached;
        }

        try
        {
            var json = await GetJsonAsync(KoLRoutes.Status("status"), cancellationToken).ConfigureAwait(false);
            var snapshot = ParseStatus(json, _clock.GetUtcNow());
            lock (_cacheGate) _status = snapshot;
            return snapshot;
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            lock (_cacheGate)
            {
                if (_status is { } stale && now - stale.FetchedAt <= _policy.MaxStale)
                    return stale with { IsStale = true };
            }
            throw;
        }
    }

    public async Task<KoLInventorySnapshot> FetchInventoryAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        lock (_cacheGate)
        {
            if (!forceRefresh && _inventory is { } cached && now - cached.FetchedAt <= _policy.CacheTtl)
                return cached;
        }

        try
        {
            var json = await GetJsonAsync(KoLRoutes.Status("inventory"), cancellationToken).ConfigureAwait(false);
            var snapshot = ParseInventory(json, _clock.GetUtcNow());
            lock (_cacheGate) _inventory = snapshot;
            return snapshot;
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            lock (_cacheGate)
            {
                if (_inventory is { } stale && now - stale.FetchedAt <= _policy.MaxStale)
                    return stale with { IsStale = true };
            }
            throw;
        }
    }

    public void Invalidate()
    {
        lock (_cacheGate)
        {
            _status = null;
            _inventory = null;
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string relativePath, CancellationToken cancellationToken)
    {
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lastRequest is { } last)
            {
                var wait = _policy.MinimumRequestInterval - (_clock.GetUtcNow() - last);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            var uri = new Uri(_baseUri, relativePath);
            _security.EnsureAllowed(uri);
            using var response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            _security.EnsureResponseUri(uri, response.RequestMessage?.RequestUri);
            if (response.Headers.Location is { } location && !response.IsSuccessStatusCode)
                _security.ResolveRedirect(uri, location.ToString());
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var htmlKind = KoLHtmlResponseClassifier.Classify(body);
            if (htmlKind is KoLHtmlResponseKind.LoginPage or KoLHtmlResponseKind.Empty)
                throw new KoLHtmlResponseException(htmlKind,
                    htmlKind == KoLHtmlResponseKind.LoginPage
                        ? "KoL returned a login page; the session is not authenticated."
                        : "KoL returned an empty API response.");
            _lastRequest = _clock.GetUtcNow();
            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException("KoL API returned a non-JSON response; the session may have expired.", exception);
            }
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private static KoLStatusSnapshot ParseStatus(JsonDocument document, DateTimeOffset fetchedAt)
    {
        var root = document.RootElement;
        if (root.TryGetProperty("status", out var nested) && nested.ValueKind == JsonValueKind.Object)
            root = nested;
        return new KoLStatusSnapshot(
            StringValue(root, "name", "player", "username"),
            IntValue(root, "adventures", "adventure", "turns"),
            LongValue(root, "meat", "currency"),
            IntValue(root, "level"),
            fetchedAt);
    }

    private static KoLInventorySnapshot ParseInventory(JsonDocument document, DateTimeOffset fetchedAt)
    {
        var root = document.RootElement;
        if (root.TryGetProperty("inventory", out var nested)) root = nested;
        var items = new List<KoLInventoryItem>();
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object) continue;
                var quantity = IntValue(property.Value, "quantity", "count", "amount");
                if (quantity <= 0) continue;
                var name = StringValue(property.Value, "name", "itemname") ?? property.Name;
                var itemId = StringValue(property.Value, "itemid", "id") ?? property.Name;
                items.Add(new KoLInventoryItem(itemId, name, quantity));
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in root.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var quantity = IntValue(entry, "quantity", "count", "amount");
                if (quantity <= 0) continue;
                items.Add(new KoLInventoryItem(
                    StringValue(entry, "itemid", "id") ?? string.Empty,
                    StringValue(entry, "name", "itemname") ?? "Unknown item",
                    quantity));
            }
        }
        return new KoLInventorySnapshot(items.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray(), fetchedAt);
    }

    private static string? StringValue(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String) return value.GetString();
            if (value.ValueKind == JsonValueKind.Number) return value.GetRawText();
        }
        return null;
    }

    private static int IntValue(JsonElement element, params string[] names)
        => int.TryParse(StringValue(element, names), out var value) ? value : 0;

    private static long LongValue(JsonElement element, params string[] names)
        => long.TryParse(StringValue(element, names), out var value) ? value : 0L;
}
