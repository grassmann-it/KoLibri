using System.Text.Json.Serialization;
using KolArbitrage.Core.Integration;

namespace KolArbitrage.Core.Safety;

/// <summary>
/// The stable identifiers that can be observed for a Mall seller. KoL's Mall
/// page normally exposes a store id and display name; callers that have a
/// character id or name can supply those too.
/// </summary>
public sealed record SellerStoreIdentity
{
    public string? SellerId { get; init; }
    public string? SellerName { get; init; }
    public string? StoreId { get; init; }
    public string? StoreName { get; init; }

    [JsonIgnore]
    public string? Seller
    {
        get => SellerName;
        init => SellerName = value;
    }

    [JsonIgnore]
    public string? Store
    {
        get => StoreName;
        init => StoreName = value;
    }

    public SellerStoreIdentity()
    {
    }

    public SellerStoreIdentity(
        string? sellerId = null,
        string? sellerName = null,
        string? storeId = null,
        string? storeName = null)
    {
        SellerId = sellerId;
        SellerName = sellerName;
        StoreId = storeId;
        StoreName = storeName;
    }

    public bool HasIdentifier =>
        !string.IsNullOrWhiteSpace(SellerId)
        || !string.IsNullOrWhiteSpace(SellerName)
        || !string.IsNullOrWhiteSpace(StoreId)
        || !string.IsNullOrWhiteSpace(StoreName);

    public static SellerStoreIdentity FromListing(MallListing listing)
    {
        ArgumentNullException.ThrowIfNull(listing);
        return new SellerStoreIdentity(storeId: listing.StoreId, storeName: listing.StoreName);
    }

    /// <summary>
    /// Returns a copy with whitespace and Unicode compatibility variants
    /// normalized. Matching still uses the original values when serializing.
    /// </summary>
    public SellerStoreIdentity Normalize() => this with
    {
        SellerId = SellerBlacklistValue.Normalize(SellerId),
        SellerName = SellerBlacklistValue.Normalize(SellerName),
        StoreId = SellerBlacklistValue.Normalize(StoreId),
        StoreName = SellerBlacklistValue.Normalize(StoreName)
    };
}

/// <summary>Which field caused a blacklist entry to match.</summary>
public enum SellerBlacklistMatchKind
{
    None,
    Key,
    SellerId,
    SellerName,
    StoreId,
    StoreName
}

/// <summary>
/// A local seller/store exclusion. A null expiry means the exclusion remains
/// active until removed. Multiple entries are allowed so each reason can have
/// its own expiry and audit trail.
/// </summary>
public sealed record SellerBlacklistEntry
{
    /// <summary>
    /// Generic identifier for integrations that only have one stable key. It
    /// is matched against all known identity fields.
    /// </summary>
    public string? Key { get; init; }
    public string? SellerId { get; init; }
    public string? SellerName { get; init; }
    public string? StoreId { get; init; }
    public string? StoreName { get; init; }
    public string Reason { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; init; }

    // Convenient aliases for callers that use one target name. They are not
    // serialized as additional JSON fields.
    [JsonIgnore]
    public string? Identifier
    {
        get => Key;
        init => Key = value;
    }

    [JsonIgnore]
    public string? Seller
    {
        get => SellerName;
        init => SellerName = value;
    }

    [JsonIgnore]
    public string? Store
    {
        get => StoreName;
        init => StoreName = value;
    }

    [JsonIgnore]
    public DateTimeOffset? Expires
    {
        get => ExpiresAt;
        init => ExpiresAt = value;
    }

    [JsonIgnore]
    public string? Name
    {
        get => SellerName ?? StoreName;
        init => SellerName = value;
    }

    public SellerBlacklistEntry()
    {
    }

    public SellerBlacklistEntry(string key, string reason, DateTimeOffset? expiresAt = null)
    {
        Key = key;
        Reason = reason;
        ExpiresAt = expiresAt;
    }

    public bool IsExpired(DateTimeOffset? now = null) =>
        ExpiresAt is { } expiry && expiry <= (now ?? DateTimeOffset.UtcNow);

    public bool IsActive(DateTimeOffset? now = null) => !IsExpired(now);

    public bool Matches(SellerStoreIdentity identity, DateTimeOffset? now = null) =>
        Matches(identity, now, out _);

    /// <summary>Checks identity fields without considering the entry expiry.</summary>
    public bool MatchesIdentity(SellerStoreIdentity identity) =>
        (this with { ExpiresAt = null }).Matches(identity, DateTimeOffset.UtcNow, out _);

    public bool Matches(
        SellerStoreIdentity identity,
        DateTimeOffset? now,
        out SellerBlacklistMatchKind matchKind)
    {
        ArgumentNullException.ThrowIfNull(identity);
        matchKind = SellerBlacklistMatchKind.None;
        if (IsExpired(now)) return false;

        var candidate = identity.Normalize();
        var entry = new SellerBlacklistEntry
        {
            Key = SellerBlacklistValue.Normalize(Key),
            SellerId = SellerBlacklistValue.Normalize(SellerId),
            SellerName = SellerBlacklistValue.Normalize(SellerName),
            StoreId = SellerBlacklistValue.Normalize(StoreId),
            StoreName = SellerBlacklistValue.Normalize(StoreName),
            Reason = Reason,
            CreatedAt = CreatedAt,
            ExpiresAt = ExpiresAt
        };

        if (!string.IsNullOrWhiteSpace(entry.Key)
            && (SellerBlacklistValue.Equals(entry.Key, candidate.SellerId)
                || SellerBlacklistValue.Equals(entry.Key, candidate.SellerName)
                || SellerBlacklistValue.Equals(entry.Key, candidate.StoreId)
                || SellerBlacklistValue.Equals(entry.Key, candidate.StoreName)))
        {
            matchKind = SellerBlacklistMatchKind.Key;
            return true;
        }

        if (SellerBlacklistValue.Equals(entry.SellerId, candidate.SellerId))
        {
            matchKind = SellerBlacklistMatchKind.SellerId;
            return true;
        }

        // Mall listings expose the store display name rather than a separate
        // seller name, so a seller-name rule also checks StoreName.
        if (SellerBlacklistValue.Equals(entry.SellerName, candidate.SellerName)
            || SellerBlacklistValue.Equals(entry.SellerName, candidate.StoreName))
        {
            matchKind = SellerBlacklistMatchKind.SellerName;
            return true;
        }

        if (SellerBlacklistValue.Equals(entry.StoreId, candidate.StoreId))
        {
            matchKind = SellerBlacklistMatchKind.StoreId;
            return true;
        }

        if (SellerBlacklistValue.Equals(entry.StoreName, candidate.StoreName))
        {
            matchKind = SellerBlacklistMatchKind.StoreName;
            return true;
        }

        return false;
    }

    internal SellerBlacklistEntry Normalize() => this with
    {
        Key = SellerBlacklistValue.Normalize(Key),
        SellerId = SellerBlacklistValue.Normalize(SellerId),
        SellerName = SellerBlacklistValue.Normalize(SellerName),
        StoreId = SellerBlacklistValue.Normalize(StoreId),
        StoreName = SellerBlacklistValue.Normalize(StoreName),
        Reason = Reason.Trim()
    };

    internal bool HasTarget =>
        !string.IsNullOrWhiteSpace(Key)
        || !string.IsNullOrWhiteSpace(SellerId)
        || !string.IsNullOrWhiteSpace(SellerName)
        || !string.IsNullOrWhiteSpace(StoreId)
        || !string.IsNullOrWhiteSpace(StoreName);
}

/// <summary>Versioned local database persisted by <see cref="ISellerBlacklistStore"/>.</summary>
public sealed record SellerBlacklistDatabase
{
    public int Version { get; init; } = 1;
    public IReadOnlyList<SellerBlacklistEntry> Entries { get; init; } = Array.Empty<SellerBlacklistEntry>();
}

public sealed record SellerBlacklistMatch(
    bool IsBlocked,
    SellerBlacklistEntry? Entry,
    SellerBlacklistMatchKind MatchKind,
    string? Reason)
{
    public bool IsMatch => IsBlocked;
    public bool IsAllowed => !IsBlocked;

    public static SellerBlacklistMatch Allowed { get; } =
        new(false, null, SellerBlacklistMatchKind.None, null);
}

public interface ISellerBlacklistStore
{
    Task<SellerBlacklistDatabase> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(SellerBlacklistDatabase database, CancellationToken cancellationToken = default);
}

public interface ISellerBlacklistRepository : ISellerBlacklistStore
{
}

public sealed class SellerBlacklistConfigurationException(string message) : Exception(message);

/// <summary>Pure matching helpers used by scanners and pricing code.</summary>
public static class SellerBlacklistMatcher
{
    public static IReadOnlyList<SellerBlacklistMatch> FindAll(
        SellerBlacklistDatabase database,
        SellerStoreIdentity identity,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(identity);
        return (database.Entries ?? Array.Empty<SellerBlacklistEntry>())
            .Where(entry => entry is not null && entry.Matches(identity, now, out _))
            .Select(entry =>
            {
                entry.Matches(identity, now, out var kind);
                return new SellerBlacklistMatch(true, entry, kind, entry.Reason);
            })
            .ToArray();
    }

    public static SellerBlacklistMatch Find(
        SellerBlacklistDatabase database,
        SellerStoreIdentity identity,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(identity);

        foreach (var entry in database.Entries ?? Array.Empty<SellerBlacklistEntry>())
        {
            if (entry is null || !entry.Matches(identity, now, out var kind)) continue;
            return new SellerBlacklistMatch(true, entry, kind, entry.Reason);
        }

        return SellerBlacklistMatch.Allowed;
    }

    public static bool IsBlocked(
        SellerBlacklistDatabase database,
        SellerStoreIdentity identity,
        DateTimeOffset? now = null) => Find(database, identity, now).IsBlocked;

    public static bool IsBlocked(
        SellerBlacklistDatabase database,
        MallListing listing,
        DateTimeOffset? now = null) => IsBlocked(database, SellerStoreIdentity.FromListing(listing), now);

    public static IReadOnlyList<SellerBlacklistEntry> ActiveEntries(
        SellerBlacklistDatabase database,
        DateTimeOffset? now = null) =>
        (database?.Entries ?? Array.Empty<SellerBlacklistEntry>())
            .Where(x => x is not null && x.IsActive(now))
            .ToArray();
}

/// <summary>Compatibility facade for callers that use the blacklist as a pure policy.</summary>
public static class SellerBlacklist
{
    public static IReadOnlyList<SellerBlacklistMatch> FindAll(
        SellerBlacklistDatabase database,
        SellerStoreIdentity identity,
        DateTimeOffset? now = null) =>
        SellerBlacklistMatcher.FindAll(database, identity, now);

    public static SellerBlacklistMatch Match(
        SellerBlacklistDatabase database,
        SellerStoreIdentity identity,
        DateTimeOffset? now = null) =>
        SellerBlacklistMatcher.Find(database, identity, now);

    public static bool IsBlocked(
        SellerBlacklistDatabase database,
        SellerStoreIdentity identity,
        DateTimeOffset? now = null) =>
        SellerBlacklistMatcher.IsBlocked(database, identity, now);

    public static bool IsBlocked(
        SellerBlacklistDatabase database,
        MallListing listing,
        DateTimeOffset? now = null) =>
        SellerBlacklistMatcher.IsBlocked(database, listing, now);
}

/// <summary>Combined seller/store naming alias for the matching facade.</summary>
public static class SellerStoreBlacklist
{
    public static IReadOnlyList<SellerBlacklistMatch> FindAll(
        SellerBlacklistDatabase database,
        SellerStoreIdentity identity,
        DateTimeOffset? now = null) =>
        SellerBlacklistMatcher.FindAll(database, identity, now);

    public static SellerBlacklistMatch Match(
        SellerBlacklistDatabase database,
        SellerStoreIdentity identity,
        DateTimeOffset? now = null) =>
        SellerBlacklistMatcher.Find(database, identity, now);

    public static bool IsBlocked(
        SellerBlacklistDatabase database,
        SellerStoreIdentity identity,
        DateTimeOffset? now = null) =>
        SellerBlacklistMatcher.IsBlocked(database, identity, now);

    public static bool IsBlocked(
        SellerBlacklistDatabase database,
        MallListing listing,
        DateTimeOffset? now = null) =>
        SellerBlacklistMatcher.IsBlocked(database, listing, now);
}

/// <summary>
/// Small local-domain facade for updating and querying the persisted blacklist.
/// It performs no network calls and serializes concurrent read/modify/write
/// operations so an update cannot overwrite a neighboring update.
/// </summary>
public sealed class SellerBlacklistService
{
    private readonly ISellerBlacklistStore _store;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SellerBlacklistService(ISellerBlacklistStore? store = null, TimeProvider? clock = null)
    {
        _store = store ?? new JsonFileSellerBlacklistStore();
        _clock = clock ?? TimeProvider.System;
    }

    public Task<SellerBlacklistDatabase> LoadAsync(CancellationToken cancellationToken = default) =>
        _store.LoadAsync(cancellationToken);

    public async Task<SellerBlacklistDatabase> AddAsync(
        SellerBlacklistEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var normalized = entry.Normalize();
        SellerBlacklistStoreValidation.ValidateEntry(normalized);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var database = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var entries = (database.Entries ?? Array.Empty<SellerBlacklistEntry>())
                .Where(existing => !SameTarget(existing, normalized))
                .Append(normalized)
                .ToArray();
            var updated = database with { Version = Math.Max(1, database.Version), Entries = entries };
            await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<SellerBlacklistDatabase> BlockSellerAsync(
        string sellerId,
        string reason,
        DateTimeOffset? expiresAt = null,
        CancellationToken cancellationToken = default) =>
        AddAsync(new SellerBlacklistEntry
        {
            SellerId = sellerId,
            Reason = reason,
            ExpiresAt = expiresAt
        }, cancellationToken);

    public Task<SellerBlacklistDatabase> BlockSellerNameAsync(
        string sellerName,
        string reason,
        DateTimeOffset? expiresAt = null,
        CancellationToken cancellationToken = default) =>
        AddAsync(new SellerBlacklistEntry
        {
            SellerName = sellerName,
            Reason = reason,
            ExpiresAt = expiresAt
        }, cancellationToken);

    public Task<SellerBlacklistDatabase> BlockStoreAsync(
        string storeId,
        string reason,
        DateTimeOffset? expiresAt = null,
        CancellationToken cancellationToken = default) =>
        AddAsync(new SellerBlacklistEntry
        {
            StoreId = storeId,
            Reason = reason,
            ExpiresAt = expiresAt
        }, cancellationToken);

    public Task<SellerBlacklistDatabase> BlockStoreNameAsync(
        string storeName,
        string reason,
        DateTimeOffset? expiresAt = null,
        CancellationToken cancellationToken = default) =>
        AddAsync(new SellerBlacklistEntry
        {
            StoreName = storeName,
            Reason = reason,
            ExpiresAt = expiresAt
        }, cancellationToken);

    public Task<SellerBlacklistDatabase> BlockAsync(
        SellerStoreIdentity identity,
        string reason,
        DateTimeOffset? expiresAt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return AddAsync(new SellerBlacklistEntry
        {
            SellerId = identity.SellerId,
            SellerName = identity.SellerName,
            StoreId = identity.StoreId,
            StoreName = identity.StoreName,
            Reason = reason,
            ExpiresAt = expiresAt
        }, cancellationToken);
    }

    public Task<SellerBlacklistDatabase> BlockKeyAsync(
        string key,
        string reason,
        DateTimeOffset? expiresAt = null,
        CancellationToken cancellationToken = default) =>
        AddAsync(new SellerBlacklistEntry
        {
            Key = key,
            Reason = reason,
            ExpiresAt = expiresAt
        }, cancellationToken);

    public async Task<int> RemoveAsync(
        SellerStoreIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var database = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var kept = (database.Entries ?? Array.Empty<SellerBlacklistEntry>())
                .Where(entry => !entry.MatchesIdentity(identity))
                .ToArray();
            var removed = (database.Entries?.Count ?? 0) - kept.Length;
            if (removed > 0)
                await _store.SaveAsync(database with { Entries = kept }, cancellationToken).ConfigureAwait(false);
            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<int> RemoveSellerAsync(
        string sellerId,
        CancellationToken cancellationToken = default) =>
        RemoveAsync(new SellerStoreIdentity(sellerId: sellerId), cancellationToken);

    public Task<int> RemoveStoreAsync(
        string storeId,
        CancellationToken cancellationToken = default) =>
        RemoveAsync(new SellerStoreIdentity(storeId: storeId), cancellationToken);

    public async Task<int> PruneExpiredAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var database = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var kept = (database.Entries ?? Array.Empty<SellerBlacklistEntry>())
                .Where(entry => !entry.IsExpired(_clock.GetUtcNow()))
                .ToArray();
            var removed = (database.Entries?.Count ?? 0) - kept.Length;
            if (removed > 0)
                await _store.SaveAsync(database with { Entries = kept }, cancellationToken).ConfigureAwait(false);
            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SellerBlacklistMatch> FindAsync(
        SellerStoreIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var database = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        return SellerBlacklistMatcher.Find(database, identity, _clock.GetUtcNow());
    }

    public Task<SellerBlacklistMatch> FindAsync(
        MallListing listing,
        CancellationToken cancellationToken = default) =>
        FindAsync(SellerStoreIdentity.FromListing(listing), cancellationToken);

    public async Task<bool> IsBlockedAsync(
        SellerStoreIdentity identity,
        CancellationToken cancellationToken = default) =>
        (await FindAsync(identity, cancellationToken).ConfigureAwait(false)).IsBlocked;

    public Task<bool> IsBlockedAsync(
        MallListing listing,
        CancellationToken cancellationToken = default) =>
        IsBlockedAsync(SellerStoreIdentity.FromListing(listing), cancellationToken);

    private static bool SameTarget(SellerBlacklistEntry left, SellerBlacklistEntry right)
    {
        var a = left.Normalize();
        var b = right.Normalize();
        return SellerBlacklistValue.SameValue(a.Key, b.Key)
            && SellerBlacklistValue.SameValue(a.SellerId, b.SellerId)
            && SellerBlacklistValue.SameValue(a.SellerName, b.SellerName)
            && SellerBlacklistValue.SameValue(a.StoreId, b.StoreId)
            && SellerBlacklistValue.SameValue(a.StoreName, b.StoreName);
    }
}

internal static class SellerBlacklistStoreValidation
{
    public static void Validate(SellerBlacklistDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (database.Version < 1)
            throw new SellerBlacklistConfigurationException($"Unsupported seller blacklist version {database.Version}.");

        foreach (var entry in database.Entries ?? Array.Empty<SellerBlacklistEntry>())
            ValidateEntry(entry);
    }

    public static void ValidateEntry(SellerBlacklistEntry entry)
    {
        if (entry is null)
            throw new SellerBlacklistConfigurationException("A seller blacklist entry cannot be null.");
        if (!entry.HasTarget)
            throw new SellerBlacklistConfigurationException("A seller blacklist entry needs a seller, store, or generic key.");
        if (string.IsNullOrWhiteSpace(entry.Reason))
            throw new SellerBlacklistConfigurationException("A seller blacklist entry needs a reason.");
    }
}

internal static class SellerBlacklistValue
{
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Normalize(System.Text.NormalizationForm.FormKC);
        return string.Join(' ', normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    public static bool Equals(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    public static bool SameValue(string? left, string? right) =>
        (string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right))
        || Equals(left, right);
}
