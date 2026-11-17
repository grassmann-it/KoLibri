namespace KolArbitrage.Core.Integration;

/// <summary>
/// Provenance for a dynamically supplied Mr. Store catalog.
///
/// The manifest describes where and when the catalog was obtained; it does
/// not contain a list of current IotMs.  That list belongs to the source
/// payload and can therefore change without a code change or release.
/// </summary>
public sealed record MrStoreCatalogManifest
{
    public const string SchemaV1 = "kol-arbitrage.mrstore-catalog/v1";

    public string Schema { get; init; } = SchemaV1;
    public string SourceName { get; init; } = "MrStore";
    public Uri? SourceUri { get; init; }
    public string? Version { get; init; }
    public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; init; }
    public string? ETag { get; init; }
    public string? ContentHash { get; init; }
    public bool IsStale { get; init; }

    // Friendly aliases used by source adapters and serializers.
    public DateTimeOffset RetrievedAt => FetchedAt;
    public Uri? CatalogUri => SourceUri;
    public string? CatalogVersion => Version;
    public bool IsDynamic => SourceUri is not null || !string.IsNullOrWhiteSpace(Version);

    public MrStoreCatalogManifest()
    {
    }

    public MrStoreCatalogManifest(
        Uri? sourceUri,
        string? version = null,
        DateTimeOffset? fetchedAt = null,
        DateTimeOffset? expiresAt = null,
        string? sourceName = null,
        bool isStale = false)
    {
        SourceUri = sourceUri;
        Version = string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        FetchedAt = fetchedAt ?? DateTimeOffset.UtcNow;
        ExpiresAt = expiresAt;
        SourceName = string.IsNullOrWhiteSpace(sourceName) ? "MrStore" : sourceName.Trim();
        IsStale = isStale;
    }

    public MrStoreCatalogManifest(
        Uri? sourceUri,
        DateTimeOffset fetchedAt,
        string? version = null,
        DateTimeOffset? expiresAt = null,
        string? sourceName = null,
        bool isStale = false)
        : this(sourceUri, version, fetchedAt, expiresAt, sourceName, isStale)
    {
    }

    public bool IsFresh(DateTimeOffset now, TimeSpan maxAge)
    {
        if (IsStale || maxAge < TimeSpan.Zero || now - FetchedAt > maxAge) return false;
        return ExpiresAt is null || now <= ExpiresAt.Value;
    }

    public bool IsValid(out string error)
    {
        if (string.IsNullOrWhiteSpace(Schema))
        {
            error = "Catalog manifest schema is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(SourceName))
        {
            error = "Catalog manifest source name is required.";
            return false;
        }

        if (ExpiresAt is not null && ExpiresAt < FetchedAt)
        {
            error = "Catalog manifest expiry cannot precede its fetch time.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static MrStoreCatalogManifest From(IotMCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new MrStoreCatalogManifest(snapshot.SourceUri, snapshot.Version,
            snapshot.FetchedAt, isStale: snapshot.IsStale);
    }
}

/// <summary>One dynamic Mr. Store catalog payload and its provenance.</summary>
public sealed record MrStoreCatalogSnapshot
{
    public IReadOnlyList<IotMCatalogEntry> Entries { get; init; } = Array.Empty<IotMCatalogEntry>();
    public MrStoreCatalogManifest Manifest { get; init; } = new();

    public IReadOnlyList<IotMCatalogEntry> Items => Entries;
    public Uri? SourceUri => Manifest.SourceUri;
    public string? Version => Manifest.Version;
    public DateTimeOffset FetchedAt => Manifest.FetchedAt;
    public bool IsStale => Manifest.IsStale;

    public MrStoreCatalogSnapshot()
    {
    }

    public MrStoreCatalogSnapshot(
        IEnumerable<IotMCatalogEntry> entries,
        MrStoreCatalogManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(manifest);
        Entries = entries.ToArray();
        Manifest = manifest;
    }

    public MrStoreCatalogSnapshot(
        IReadOnlyList<IotMCatalogEntry> entries,
        DateTimeOffset fetchedAt,
        Uri? sourceUri = null,
        string? version = null,
        bool isStale = false)
        : this(entries, new MrStoreCatalogManifest(sourceUri, version, fetchedAt,
            isStale: isStale))
    {
    }

    public bool IsValid(out string error)
    {
        if (!Manifest.IsValid(out error)) return false;
        if (Entries.Any(x => !x.IsValid(out _)))
        {
            error = "Catalog contains an invalid item row.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public IotMCatalogSnapshot ToIotMCatalogSnapshot()
        => new(Entries, Manifest.FetchedAt, Manifest.SourceUri, Manifest.Version, Manifest.IsStale);

    public static MrStoreCatalogSnapshot From(
        IotMCatalogSnapshot snapshot,
        MrStoreCatalogManifest? manifest = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new MrStoreCatalogSnapshot(snapshot.Entries,
            manifest ?? MrStoreCatalogManifest.From(snapshot));
    }
}

/// <summary>
/// Dynamic Mr. Store catalog contract. Implementations may read an export,
/// an HTTP response, or another maintained registry; no item names or IDs are
/// embedded in this interface.
/// </summary>
public interface IMrStoreCatalogSource
{
    Task<MrStoreCatalogSnapshot> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>Convenience source implementation for a caller-provided reader.</summary>
public sealed class MrStoreCatalogSource : IMrStoreCatalogSource
{
    private readonly Func<CancellationToken, Task<MrStoreCatalogSnapshot>> _reader;

    public MrStoreCatalogSource(Func<CancellationToken, Task<MrStoreCatalogSnapshot>> reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public MrStoreCatalogSource(Func<CancellationToken, Task<IotMCatalogSnapshot>> reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = async cancellationToken => MrStoreCatalogSnapshot.From(
            await reader(cancellationToken).ConfigureAwait(false));
    }

    public async Task<MrStoreCatalogSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = await _reader(cancellationToken).ConfigureAwait(false);
        return snapshot ?? throw new InvalidOperationException("Mr. Store catalog source returned no snapshot.");
    }
}

/// <summary>Adapts the explicit Mr. Store contract to the IotM scanner source.</summary>
public sealed class MrStoreCatalogSourceAdapter : IIotMCatalogSource
{
    private readonly IMrStoreCatalogSource _source;

    public MrStoreCatalogSourceAdapter(IMrStoreCatalogSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public async Task<IotMCatalogSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        => (await _source.ReadAsync(cancellationToken).ConfigureAwait(false)).ToIotMCatalogSnapshot();
}

public static class MrStoreCatalogSourceExtensions
{
    public static async Task<IotMCatalogSnapshot> ReadIotMCatalogAsync(
        this IMrStoreCatalogSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return (await source.ReadAsync(cancellationToken).ConfigureAwait(false)).ToIotMCatalogSnapshot();
    }
}
