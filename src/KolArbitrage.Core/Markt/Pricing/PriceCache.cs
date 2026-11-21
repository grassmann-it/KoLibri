using System.Collections.Concurrent;

namespace KolArbitrage.Core.Markt;

public sealed record CachedPrices(
    string ItemId,
    IReadOnlyCollection<PriceObservation> Observations,
    DateTimeOffset FetchedAt);

/// <summary>
/// A deliberately simple in-memory cache. It avoids repeated detail lookups while preserving a stale
/// snapshot for short outages. A later persistence layer can serialize CachedPrices without changing the engine.
/// </summary>
public sealed class PriceCache
{
    private readonly ConcurrentDictionary<string, CachedPrices> _entries = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGet(string itemId, DateTimeOffset now, TimeSpan ttl, TimeSpan maxStale, out CachedPrices value)
    {
        if (_entries.TryGetValue(itemId, out var candidate))
        {
            var age = now - candidate.FetchedAt;
            if (age <= ttl || age <= maxStale)
            {
                value = candidate;
                return true;
            }
        }

        value = default!;
        return false;
    }

    public bool TryGetFresh(string itemId, DateTimeOffset now, TimeSpan ttl, out CachedPrices value)
    {
        if (_entries.TryGetValue(itemId, out var candidate))
        {
            if (now - candidate.FetchedAt <= ttl)
            {
                value = candidate;
                return true;
            }
        }

        value = default!;
        return false;
    }

    public void Set(string itemId, IEnumerable<PriceObservation> observations, DateTimeOffset fetchedAt)
    {
        var normalized = itemId.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) throw new ArgumentException("Item id is required.", nameof(itemId));
        _entries[normalized] = new CachedPrices(normalized, observations.ToArray(), fetchedAt);
    }

    public void Invalidate(string? itemId = null)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            _entries.Clear();
            return;
        }

        _entries.TryRemove(itemId.Trim(), out _);
    }

    public int Count => _entries.Count;
}
