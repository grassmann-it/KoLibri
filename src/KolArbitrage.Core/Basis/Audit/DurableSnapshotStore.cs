using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KolArbitrage.Core.Basis;

/// <summary>
/// Identifies one persisted read snapshot. ItemId is used by Mall and
/// Pricegun snapshots; AccountId is used by account and shop snapshots. A
/// key may contain either, or both, when a source needs that distinction.
/// </summary>
public sealed record SnapshotKey
{
    public SnapshotKey(string? itemId, string? accountId, string source)
    {
        ItemId = NormalizeOptional(itemId, nameof(itemId));
        AccountId = NormalizeOptional(accountId, nameof(accountId));
        Source = NormalizeRequired(source, nameof(source));
    }

    /// <summary>Convenience constructor for a source/item key.</summary>
    public SnapshotKey(string source, string itemId)
        : this(itemId, null, source)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            throw new ArgumentException("An item id is required.", nameof(itemId));
    }

    /// <summary>Creates an item-scoped key such as Mall or Pricegun.</summary>
    public static SnapshotKey ForItem(string source, string itemId, string? accountId = null)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            throw new ArgumentException("An item id is required.", nameof(itemId));
        return new(itemId, accountId, source);
    }

    /// <summary>Creates an account-scoped key such as status or inventory.</summary>
    public static SnapshotKey ForAccount(string source, string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            throw new ArgumentException("An account id is required.", nameof(accountId));
        return new(null, accountId, source);
    }

    /// <summary>Creates a source-level key for snapshots without an item/account.</summary>
    public static SnapshotKey ForSource(string source)
        => new(null, null, source);

    public string? ItemId { get; }
    public string? AccountId { get; }
    public string Source { get; }

    // Short aliases make the generic contract convenient for adapters whose
    // model calls the fields Item and Account rather than ItemId/AccountId.
    [JsonIgnore]
    public string? Item => ItemId;

    [JsonIgnore]
    public string? Account => AccountId;

    internal string Canonical
        => $"item:{CanonicalPart(ItemId)}\u001faccount:{CanonicalPart(AccountId)}\u001fsource:{CanonicalPart(Source)}";

    private static string CanonicalPart(string? value)
        => (value ?? string.Empty).Normalize(NormalizationForm.FormKC).ToUpperInvariant();

    private static string? NormalizeOptional(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        if (normalized.Any(char.IsControl))
            throw new ArgumentException("Snapshot key values cannot contain control characters.", parameterName);
        return normalized;
    }

    private static string NormalizeRequired(string value, string parameterName)
    {
        var normalized = NormalizeOptional(value, parameterName);
        return normalized ?? throw new ArgumentException("A snapshot source is required.", parameterName);
    }
}

/// <summary>Freshness policy shared by all durable snapshot adapters.</summary>
public record SnapshotCachePolicy
{
    public SnapshotCachePolicy()
    {
    }

    public SnapshotCachePolicy(TimeSpan cacheTtl, TimeSpan maxStale)
    {
        CacheTtl = cacheTtl;
        MaxStale = maxStale;
        Validate();
    }

    public TimeSpan CacheTtl { get; init; } = TimeSpan.FromMinutes(20);
    public TimeSpan MaxStale { get; init; } = TimeSpan.FromHours(12);

    // TTL and MaxStale are deliberately exposed with the shorter names used
    // by cache callers as well as the names used by existing read policies.
    public TimeSpan Ttl
    {
        get => CacheTtl;
        init => CacheTtl = value;
    }

    public TimeSpan TimeToLive
    {
        get => CacheTtl;
        init => CacheTtl = value;
    }

    public TimeSpan StaleTtl
    {
        get => MaxStale;
        init => MaxStale = value;
    }

    public TimeSpan MaxStaleAge
    {
        get => MaxStale;
        init => MaxStale = value;
    }

    public TimeSpan MaxAge
    {
        get => MaxStale;
        init => MaxStale = value;
    }

    public virtual void Validate()
    {
        if (CacheTtl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(CacheTtl), "Cache TTL must be positive.");
        if (MaxStale < CacheTtl)
            throw new ArgumentException("MaxStale must be at least CacheTtl.", nameof(MaxStale));
    }
}

/// <summary>Compatibility name for callers that refer to a snapshot policy.</summary>
public sealed record SnapshotPolicy : SnapshotCachePolicy
{
    public SnapshotPolicy()
    {
    }

    public SnapshotPolicy(TimeSpan cacheTtl, TimeSpan maxStale)
        : base(cacheTtl, maxStale)
    {
    }
}

/// <summary>Compatibility name for a policy owned by the snapshot store.</summary>
public sealed record SnapshotStorePolicy : SnapshotCachePolicy
{
    public SnapshotStorePolicy()
    {
    }

    public SnapshotStorePolicy(TimeSpan cacheTtl, TimeSpan maxStale)
        : base(cacheTtl, maxStale)
    {
    }
}

/// <summary>Options for the local JSON snapshot store.</summary>
public sealed record SnapshotStoreOptions
{
    public SnapshotStoreOptions()
    {
    }

    public SnapshotStoreOptions(
        string? directoryPath,
        SnapshotCachePolicy? policy = null,
        TimeProvider? clock = null,
        JsonSerializerOptions? jsonOptions = null)
    {
        DirectoryPath = directoryPath;
        Policy = policy ?? new SnapshotCachePolicy();
        Clock = clock ?? TimeProvider.System;
        JsonOptions = jsonOptions;
    }

    public string? DirectoryPath { get; init; }
    public SnapshotCachePolicy Policy { get; init; } = new();
    public TimeProvider Clock { get; init; } = TimeProvider.System;
    public JsonSerializerOptions? JsonOptions { get; init; }
}

/// <summary>
/// Versioned, self-validating JSON envelope used on disk. The checksum is a
/// SHA-256 digest of every field except Checksum, including the payload.
/// </summary>
public sealed record SnapshotEnvelope<T>
{
    public const int CurrentVersion = 1;

    [JsonConstructor]
    public SnapshotEnvelope(
        int version,
        string? itemId,
        string? accountId,
        string source,
        DateTimeOffset fetchedAt,
        bool stale,
        string checksum,
        T payload)
    {
        Version = version;
        ItemId = itemId;
        AccountId = accountId;
        Source = source;
        FetchedAt = fetchedAt;
        Stale = stale;
        Checksum = checksum ?? string.Empty;
        Payload = payload;
    }

    public SnapshotEnvelope(
        SnapshotKey key,
        T payload,
        DateTimeOffset fetchedAt,
        bool stale = false,
        int version = CurrentVersion,
        string? checksum = null)
        : this(version, key.ItemId, key.AccountId, key.Source, fetchedAt, stale, checksum ?? string.Empty, payload)
    {
    }

    public SnapshotEnvelope(
        string? itemKey,
        string? accountKey,
        string sourceName,
        T data,
        DateTimeOffset capturedAt,
        bool isStale = false,
        int schemaVersion = CurrentVersion,
        string? digest = null)
        : this(schemaVersion, itemKey, accountKey, sourceName, capturedAt, isStale, digest ?? string.Empty, data)
    {
    }

    public SnapshotEnvelope(
        string sourceName,
        T data,
        DateTimeOffset capturedAt,
        string? itemKey = null,
        string? accountKey = null,
        bool isStale = false,
        int schemaVersion = CurrentVersion,
        string? digest = null)
        : this(schemaVersion, itemKey, accountKey, sourceName, capturedAt, isStale, digest ?? string.Empty, data)
    {
    }

    public int Version { get; init; }
    public string? ItemId { get; init; }
    public string? AccountId { get; init; }
    public string Source { get; init; }
    public DateTimeOffset FetchedAt { get; init; }
    public bool Stale { get; init; }
    public string Checksum { get; init; }
    public T Payload { get; init; }

    [JsonIgnore]
    public T Data => Payload;

    [JsonIgnore]
    public T Value => Payload;

    [JsonIgnore]
    public string? Item => ItemId;

    [JsonIgnore]
    public string? Account => AccountId;

    [JsonIgnore]
    public SnapshotKey Key => new(ItemId, AccountId, Source);

    public SnapshotEnvelope<T> WithChecksum(string checksum)
        => this with { Checksum = checksum ?? throw new ArgumentNullException(nameof(checksum)) };

    public static SnapshotEnvelope<T> Create(
        SnapshotKey key,
        T payload,
        DateTimeOffset? fetchedAt = null,
        bool stale = false,
        int version = CurrentVersion)
        => new(key, payload, fetchedAt ?? DateTimeOffset.UtcNow, stale, version);

    public static SnapshotEnvelope<T> Create(
        string source,
        T payload,
        string? itemId = null,
        string? accountId = null,
        DateTimeOffset? fetchedAt = null,
        bool stale = false,
        int version = CurrentVersion)
        => new(new SnapshotKey(itemId, accountId, source), payload, fetchedAt ?? DateTimeOffset.UtcNow, stale, version);

    public static SnapshotEnvelope<T> Create(
        string? itemId,
        string? accountId,
        string source,
        T payload,
        DateTimeOffset? fetchedAt = null,
        bool stale = false,
        int version = CurrentVersion)
        => new(new SnapshotKey(itemId, accountId, source), payload, fetchedAt ?? DateTimeOffset.UtcNow, stale, version);
}

/// <summary>Result of a freshness-aware read, when callers need age details.</summary>
public sealed record SnapshotReadResult<T>(SnapshotEnvelope<T> Snapshot, TimeSpan Age)
{
    public SnapshotEnvelope<T> Envelope => Snapshot;
    public T Payload => Snapshot.Payload;
    public bool IsStale => Snapshot.Stale;
    public bool IsFresh => !Snapshot.Stale;
}

/// <summary>Raised when an envelope is structurally valid JSON but unusable.</summary>
public sealed class SnapshotStoreException : Exception
{
    public SnapshotStoreException(string message)
        : base(message)
    {
    }

    public SnapshotStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public interface IDurableSnapshotStore
{
    Task<SnapshotEnvelope<T>?> ReadAsync<T>(
        SnapshotKey key,
        SnapshotCachePolicy? policy = null,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default);

    Task<SnapshotEnvelope<T>?> ReadAsync<T>(
        string source,
        string? itemId = null,
        string? accountId = null,
        SnapshotCachePolicy? policy = null,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default);

    Task<SnapshotEnvelope<T>?> ReadAsync<T>(
        string source,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<SnapshotEnvelope<T>?> ReadAsync<T>(
        SnapshotKey key,
        CancellationToken cancellationToken);

    Task<SnapshotEnvelope<T>?> ReadRawAsync<T>(SnapshotKey key, CancellationToken cancellationToken = default);

    Task<SnapshotEnvelope<T>> WriteAsync<T>(
        SnapshotEnvelope<T> snapshot,
        CancellationToken cancellationToken = default);

    Task<SnapshotEnvelope<T>> WriteAsync<T>(
        SnapshotKey key,
        T payload,
        DateTimeOffset? fetchedAt = null,
        bool stale = false,
        int version = SnapshotEnvelope<T>.CurrentVersion,
        CancellationToken cancellationToken = default);

    Task<SnapshotEnvelope<T>> WriteAsync<T>(
        SnapshotKey key,
        T payload,
        CancellationToken cancellationToken);

    Task<SnapshotEnvelope<T>> WriteAsync<T>(
        string source,
        T payload,
        string? itemId = null,
        string? accountId = null,
        DateTimeOffset? fetchedAt = null,
        bool stale = false,
        int version = SnapshotEnvelope<T>.CurrentVersion,
        CancellationToken cancellationToken = default);

    Task<SnapshotEnvelope<T>> WriteAsync<T>(
        string source,
        T payload,
        DateTimeOffset fetchedAt,
        CancellationToken cancellationToken = default);

    Task<int> InvalidateAsync(SnapshotKey key, CancellationToken cancellationToken = default);
    Task<int> InvalidateAsync(
        string? itemId = null,
        string? accountId = null,
        string? source = null,
        CancellationToken cancellationToken = default);

    Task<int> InvalidateAsync(CancellationToken cancellationToken);
}

/// <summary>Alias used by hosts that treat persistence as a local adapter.</summary>
public interface ILocalSnapshotStore : IDurableSnapshotStore
{
}

/// <summary>General-purpose alias for consumers that do not care about persistence details.</summary>
public interface ISnapshotStore : IDurableSnapshotStore
{
}

/// <summary>
/// Durable local cache for read-only integration snapshots.
///
/// Every entry is one JSON document. Writes use a same-directory temporary
/// file, flush it to disk, and atomically replace the destination. A bad
/// checksum, unsupported version, malformed JSON, or key mismatch is treated
/// as a cache miss so an adapter can fetch a new snapshot.
/// </summary>
public class DurableSnapshotStore : IDurableSnapshotStore, ILocalSnapshotStore, ISnapshotStore, IDisposable, IAsyncDisposable
{
    public const int CurrentVersion = SnapshotEnvelope<object>.CurrentVersion;

    private readonly string _directoryPath;
    private readonly SnapshotCachePolicy _policy;
    private readonly TimeProvider _clock;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly JsonSerializerOptions _checksumJsonOptions;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public DurableSnapshotStore()
        : this(DefaultPath(), null, null, null)
    {
    }

    public DurableSnapshotStore(string directoryPath)
        : this(directoryPath, null, null, null)
    {
    }

    public DurableSnapshotStore(
        string directoryPath,
        TimeSpan cacheTtl,
        TimeSpan maxStale,
        TimeProvider? clock = null,
        JsonSerializerOptions? jsonOptions = null)
        : this(directoryPath, new SnapshotCachePolicy(cacheTtl, maxStale), clock, jsonOptions)
    {
    }

    public DurableSnapshotStore(
        string directoryPath,
        SnapshotCachePolicy? policy = null,
        TimeProvider? clock = null,
        JsonSerializerOptions? jsonOptions = null)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
            throw new ArgumentException("A snapshot directory is required.", nameof(directoryPath));

        _directoryPath = Path.GetFullPath(directoryPath);
        _policy = policy ?? new SnapshotCachePolicy();
        _policy.Validate();
        _clock = clock ?? TimeProvider.System;
        _jsonOptions = CreateJsonOptions(jsonOptions);
        _checksumJsonOptions = new JsonSerializerOptions(_jsonOptions) { WriteIndented = false };
    }

    public DurableSnapshotStore(SnapshotStoreOptions options)
        : this(
            options?.DirectoryPath ?? DefaultPath(),
            options?.Policy,
            options?.Clock,
            options?.JsonOptions)
    {
        ArgumentNullException.ThrowIfNull(options);
    }

    public string DirectoryPath => _directoryPath;
    public SnapshotCachePolicy Policy => _policy;

    public static string DefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var root = string.IsNullOrWhiteSpace(appData)
            ? Path.Combine(Environment.CurrentDirectory, ".kolarbitrage")
            : Path.Combine(appData, "KolArbitrage");
        return Path.Combine(root, "snapshots");
    }

    /// <summary>Returns the deterministic path used for a key.</summary>
    public string GetPath(SnapshotKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(key.Canonical));
        return Path.Combine(_directoryPath, $"{Convert.ToHexString(digest).ToLowerInvariant()}.json");
    }

    public Task<SnapshotEnvelope<T>?> ReadAsync<T>(
        SnapshotKey key,
        SnapshotCachePolicy? policy = null,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
        => ReadCoreAsync<T>(key, policy ?? _policy, now ?? _clock.GetUtcNow(), enforceFreshness: true, cancellationToken: cancellationToken);

    public Task<SnapshotEnvelope<T>?> ReadAsync<T>(
        SnapshotKey key,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
        => ReadAsync<T>(key, null, now, cancellationToken);

    public Task<SnapshotEnvelope<T>?> ReadAsync<T>(
        SnapshotKey key,
        CancellationToken cancellationToken)
        => ReadAsync<T>(key, null, null, cancellationToken);

    public Task<SnapshotEnvelope<T>?> ReadAsync<T>(
        string source,
        string? itemId = null,
        string? accountId = null,
        SnapshotCachePolicy? policy = null,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
        => ReadAsync<T>(new SnapshotKey(itemId, accountId, source), policy, now, cancellationToken);

    public Task<SnapshotEnvelope<T>?> ReadAsync<T>(
        string source,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
        => ReadAsync<T>(new SnapshotKey(null, null, source), null, now, cancellationToken);

    /// <summary>Reads and validates an entry without applying TTL or stale age.</summary>
    public Task<SnapshotEnvelope<T>?> ReadRawAsync<T>(SnapshotKey key, CancellationToken cancellationToken = default)
        => ReadCoreAsync<T>(key, _policy, _clock.GetUtcNow(), enforceFreshness: false, cancellationToken: cancellationToken);

    public Task<SnapshotEnvelope<T>?> TryGetAsync<T>(
        SnapshotKey key,
        SnapshotCachePolicy? policy = null,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
        => ReadAsync<T>(key, policy, now, cancellationToken);

    public Task<SnapshotEnvelope<T>?> GetAsync<T>(
        SnapshotKey key,
        SnapshotCachePolicy? policy = null,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
        => ReadAsync<T>(key, policy, now, cancellationToken);

    public Task<SnapshotEnvelope<T>?> LoadAsync<T>(
        SnapshotKey key,
        SnapshotCachePolicy? policy = null,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
        => ReadAsync<T>(key, policy, now, cancellationToken);

    public Task<SnapshotEnvelope<T>?> LoadAsync<T>(
        string source,
        string? itemId = null,
        string? accountId = null,
        SnapshotCachePolicy? policy = null,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
        => ReadAsync<T>(source, itemId, accountId, policy, now, cancellationToken);

    public async Task<SnapshotReadResult<T>?> ReadWithStatusAsync<T>(
        SnapshotKey key,
        SnapshotCachePolicy? policy = null,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        var at = now ?? _clock.GetUtcNow();
        var snapshot = await ReadAsync<T>(key, policy, at, cancellationToken).ConfigureAwait(false);
        return snapshot is null ? null : new SnapshotReadResult<T>(snapshot, PositiveAge(at - snapshot.FetchedAt));
    }

    public async Task<SnapshotEnvelope<T>> WriteAsync<T>(
        SnapshotEnvelope<T> snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateEnvelope(snapshot);
        var normalized = NormalizeEnvelope(snapshot);
        var checksum = ComputeChecksum(normalized);
        var persisted = normalized with { Checksum = checksum };
        var path = GetPath(persisted.Key);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directoryPath);
            var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 16 * 1024,
                    options: FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, persisted, _jsonOptions, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                // Both files live in the same directory, so the rename is
                // atomic on the filesystems supported by .NET.
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
        finally
        {
            _gate.Release();
        }

        return persisted;
    }

    public Task<SnapshotEnvelope<T>> WriteAsync<T>(
        SnapshotKey key,
        T payload,
        DateTimeOffset? fetchedAt = null,
        bool stale = false,
        int version = CurrentVersion,
        CancellationToken cancellationToken = default)
        => WriteAsync(
            new SnapshotEnvelope<T>(key, payload, fetchedAt ?? _clock.GetUtcNow(), stale, version),
            cancellationToken);

    public Task<SnapshotEnvelope<T>> WriteAsync<T>(
        string source,
        T payload,
        string? itemId = null,
        string? accountId = null,
        DateTimeOffset? fetchedAt = null,
        bool stale = false,
        int version = CurrentVersion,
        CancellationToken cancellationToken = default)
        => WriteAsync(
            new SnapshotKey(itemId, accountId, source),
            payload,
            fetchedAt,
            stale,
            version,
            cancellationToken);

    public Task<SnapshotEnvelope<T>> WriteAsync<T>(
        SnapshotKey key,
        T payload,
        CancellationToken cancellationToken)
        => WriteAsync(key, payload, fetchedAt: null, stale: false, version: CurrentVersion, cancellationToken);

    public Task<SnapshotEnvelope<T>> WriteAsync<T>(
        string source,
        T payload,
        DateTimeOffset fetchedAt,
        CancellationToken cancellationToken = default)
        => WriteAsync(
            new SnapshotKey(null, null, source),
            payload,
            fetchedAt,
            stale: false,
            version: CurrentVersion,
            cancellationToken);

    public Task<SnapshotEnvelope<T>> SaveAsync<T>(
        SnapshotKey key,
        T payload,
        DateTimeOffset? fetchedAt = null,
        bool stale = false,
        int version = CurrentVersion,
        CancellationToken cancellationToken = default)
        => WriteAsync(key, payload, fetchedAt, stale, version, cancellationToken);

    public Task<SnapshotEnvelope<T>> SaveAsync<T>(
        SnapshotEnvelope<T> snapshot,
        CancellationToken cancellationToken = default)
        => WriteAsync(snapshot, cancellationToken);

    public Task<SnapshotEnvelope<T>> SaveAsync<T>(
        string source,
        T payload,
        string? itemId = null,
        string? accountId = null,
        DateTimeOffset? fetchedAt = null,
        bool stale = false,
        int version = CurrentVersion,
        CancellationToken cancellationToken = default)
        => WriteAsync(source, payload, itemId, accountId, fetchedAt, stale, version, cancellationToken);

    public Task<SnapshotEnvelope<T>> SaveAsync<T>(
        string source,
        string? itemId,
        string? accountId,
        T payload,
        DateTimeOffset? fetchedAt = null,
        bool stale = false,
        int version = CurrentVersion,
        CancellationToken cancellationToken = default)
        => WriteAsync(source, payload, itemId, accountId, fetchedAt, stale, version, cancellationToken);

    public async Task<int> InvalidateAsync(SnapshotKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = GetPath(key);
            if (!File.Exists(path)) return 0;
            File.Delete(path);
            return 1;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> InvalidateAsync(
        string? itemId = null,
        string? accountId = null,
        string? source = null,
        CancellationToken cancellationToken = default)
    {
        var item = NormalizeFilter(itemId);
        var account = NormalizeFilter(accountId);
        var sourceValue = NormalizeFilter(source);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(_directoryPath)) return 0;

            var deleted = 0;
            foreach (var path in Directory.EnumerateFiles(_directoryPath, "*.json", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!MatchesFilter(path, item, account, sourceValue)) continue;
                try
                {
                    File.Delete(path);
                    deleted++;
                }
                catch (FileNotFoundException)
                {
                    // Another process may have invalidated the same entry.
                }
            }
            return deleted;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<int> InvalidateAsync(CancellationToken cancellationToken)
        => InvalidateAsync(itemId: null, accountId: null, source: null, cancellationToken: cancellationToken);

    public Task<int> InvalidateAllAsync(CancellationToken cancellationToken = default)
        => InvalidateAsync(cancellationToken: cancellationToken);

    public Task<int> InvalidateItemAsync(string itemId, CancellationToken cancellationToken = default)
        => InvalidateAsync(itemId: itemId, cancellationToken: cancellationToken);

    public Task<int> InvalidateAccountAsync(string accountId, CancellationToken cancellationToken = default)
        => InvalidateAsync(accountId: accountId, cancellationToken: cancellationToken);

    public Task<int> InvalidateSourceAsync(string source, CancellationToken cancellationToken = default)
        => InvalidateAsync(source: source, cancellationToken: cancellationToken);

    /// <summary>Synchronous convenience for startup/shutdown paths.</summary>
    public int Invalidate(SnapshotKey key) => InvalidateAsync(key).GetAwaiter().GetResult();

    /// <summary>Synchronous convenience for startup/shutdown paths.</summary>
    public int Invalidate(string? itemId = null, string? accountId = null, string? source = null)
        => InvalidateAsync(itemId, accountId, source).GetAwaiter().GetResult();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<SnapshotEnvelope<T>?> ReadCoreAsync<T>(
        SnapshotKey key,
        SnapshotCachePolicy policy,
        DateTimeOffset now,
        bool enforceFreshness,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        policy.Validate();
        var path = GetPath(key);
        if (!File.Exists(path)) return null;

        SnapshotEnvelope<T>? snapshot;
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 16 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            snapshot = await JsonSerializer.DeserializeAsync<SnapshotEnvelope<T>>(stream, _jsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }

        if (snapshot is null || !IsValid(snapshot, key)) return null;
        if (!string.Equals(snapshot.Checksum, ComputeChecksum(snapshot), StringComparison.OrdinalIgnoreCase))
            return null;

        if (!enforceFreshness) return snapshot;

        var age = PositiveAge(now - snapshot.FetchedAt);
        if (age <= policy.CacheTtl) return snapshot;
        if (age <= policy.MaxStale) return snapshot with { Stale = true };
        return null;
    }

    private static bool IsValid<T>(SnapshotEnvelope<T> snapshot, SnapshotKey key)
    {
        if (snapshot.Version != CurrentVersion) return false;
        if (!string.Equals(snapshot.Source, key.Source, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(snapshot.ItemId, key.ItemId, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(snapshot.AccountId, key.AccountId, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(snapshot.Source)) return false;
        return true;
    }

    private static void ValidateEnvelope<T>(SnapshotEnvelope<T> snapshot)
    {
        if (snapshot.Version != CurrentVersion)
            throw new SnapshotStoreException($"Unsupported snapshot version {snapshot.Version}; expected {CurrentVersion}.");
        _ = new SnapshotKey(snapshot.ItemId, snapshot.AccountId, snapshot.Source);
    }

    private static SnapshotEnvelope<T> NormalizeEnvelope<T>(SnapshotEnvelope<T> snapshot)
        => snapshot with
        {
            ItemId = NormalizeFilter(snapshot.ItemId),
            AccountId = NormalizeFilter(snapshot.AccountId),
            Source = snapshot.Source.Trim(),
            FetchedAt = snapshot.FetchedAt.ToUniversalTime(),
            Checksum = string.Empty
        };

    private string ComputeChecksum<T>(SnapshotEnvelope<T> snapshot)
    {
        var material = new ChecksumMaterial<T>(
            snapshot.Version,
            snapshot.ItemId,
            snapshot.AccountId,
            snapshot.Source,
            snapshot.FetchedAt,
            snapshot.Stale,
            snapshot.Payload);
        var json = JsonSerializer.Serialize(material, _checksumJsonOptions);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private bool MatchesFilter(string path, string? item, string? account, string? source)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            var candidateItem = ReadString(root, "itemId", "item");
            var candidateAccount = ReadString(root, "accountId", "account");
            var candidateSource = ReadString(root, "source");
            return (item is null || string.Equals(item, candidateItem, StringComparison.OrdinalIgnoreCase))
                && (account is null || string.Equals(account, candidateAccount, StringComparison.OrdinalIgnoreCase))
                && (source is null || string.Equals(source, candidateSource, StringComparison.OrdinalIgnoreCase));
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (JsonException)
        {
            // Invalidation is also the repair path for unreadable entries.
            return item is null && account is null && source is null;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
            var property = root.EnumerateObject().FirstOrDefault(x =>
                string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (property.Value.ValueKind == JsonValueKind.String) return property.Value.GetString();
        }
        return null;
    }

    private static string? NormalizeFilter(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static TimeSpan PositiveAge(TimeSpan age) => age < TimeSpan.Zero ? TimeSpan.Zero : age;

    private static JsonSerializerOptions CreateJsonOptions(JsonSerializerOptions? source)
    {
        var options = source is null ? new JsonSerializerOptions(JsonSerializerDefaults.Web) : new JsonSerializerOptions(source);
        options.PropertyNameCaseInsensitive = true;
        options.WriteIndented = true;
        return options;
    }

    private sealed record ChecksumMaterial<T>(
        int Version,
        string? ItemId,
        string? AccountId,
        string Source,
        DateTimeOffset FetchedAt,
        bool Stale,
        T Payload);
}

/// <summary>Short compatibility name for the durable local implementation.</summary>
public sealed class LocalSnapshotStore : DurableSnapshotStore
{
    public LocalSnapshotStore()
        : base()
    {
    }

    public LocalSnapshotStore(string directoryPath)
        : base(directoryPath)
    {
    }

    public LocalSnapshotStore(
        string directoryPath,
        SnapshotCachePolicy? policy = null,
        TimeProvider? clock = null,
        JsonSerializerOptions? jsonOptions = null)
        : base(directoryPath, policy, clock, jsonOptions)
    {
    }

    public LocalSnapshotStore(SnapshotStoreOptions options)
        : base(options)
    {
    }
}

/// <summary>Compatibility name for callers that emphasize the JSON format.</summary>
public sealed class JsonFileSnapshotStore : DurableSnapshotStore
{
    public JsonFileSnapshotStore()
        : base()
    {
    }

    public JsonFileSnapshotStore(string directoryPath)
        : base(directoryPath)
    {
    }

    public JsonFileSnapshotStore(
        string directoryPath,
        SnapshotCachePolicy? policy = null,
        TimeProvider? clock = null,
        JsonSerializerOptions? jsonOptions = null)
        : base(directoryPath, policy, clock, jsonOptions)
    {
    }

    public JsonFileSnapshotStore(SnapshotStoreOptions options)
        : base(options)
    {
    }
}

/// <summary>Short alias for the JSON-backed implementation.</summary>
public sealed class JsonSnapshotStore : DurableSnapshotStore
{
    public JsonSnapshotStore()
        : base()
    {
    }

    public JsonSnapshotStore(string directoryPath)
        : base(directoryPath)
    {
    }

    public JsonSnapshotStore(
        string directoryPath,
        SnapshotCachePolicy? policy = null,
        TimeProvider? clock = null,
        JsonSerializerOptions? jsonOptions = null)
        : base(directoryPath, policy, clock, jsonOptions)
    {
    }

    public JsonSnapshotStore(SnapshotStoreOptions options)
        : base(options)
    {
    }
}

/// <summary>Short alias for hosts that call persisted entries file snapshots.</summary>
public sealed class FileSnapshotStore : DurableSnapshotStore
{
    public FileSnapshotStore()
        : base()
    {
    }

    public FileSnapshotStore(string directoryPath)
        : base(directoryPath)
    {
    }

    public FileSnapshotStore(
        string directoryPath,
        SnapshotCachePolicy? policy = null,
        TimeProvider? clock = null,
        JsonSerializerOptions? jsonOptions = null)
        : base(directoryPath, policy, clock, jsonOptions)
    {
    }

    public FileSnapshotStore(SnapshotStoreOptions options)
        : base(options)
    {
    }
}
