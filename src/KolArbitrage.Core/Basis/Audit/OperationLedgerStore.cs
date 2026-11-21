using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KolArbitrage.Core.Basis;

/// <summary>Read/write boundary for operation ledger snapshots.</summary>
public interface IOperationLedgerStore
{
    /// <summary>Stable key used by service instances to coordinate updates.</summary>
    string StorageId { get; }

    OperationLedgerSnapshot Read();

    void Write(OperationLedgerSnapshot snapshot);

    Task<OperationLedgerSnapshot> ReadAsync(CancellationToken cancellationToken = default);

    Task WriteAsync(OperationLedgerSnapshot snapshot, CancellationToken cancellationToken = default);
}

/// <summary>Compatibility name for hosts that call this an approval store.</summary>
public interface IApprovalLedgerStore : IOperationLedgerStore
{
}

/// <summary>Thread-safe process-local store useful for tests and offline hosts.</summary>
public sealed class InMemoryOperationLedgerStore : IApprovalLedgerStore
{
    private readonly object _gate = new();
    private OperationLedgerSnapshot _snapshot = OperationLedgerSnapshot.Empty;

    public InMemoryOperationLedgerStore(string? storageId = null)
        => StorageId = string.IsNullOrWhiteSpace(storageId)
            ? $"memory:{Guid.NewGuid():N}"
            : storageId.Trim();

    public string StorageId { get; }

    public OperationLedgerSnapshot Read()
    {
        lock (_gate)
            return Clone(_snapshot);
    }

    public void Write(OperationLedgerSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Validate(snapshot);
        lock (_gate)
            _snapshot = Clone(snapshot);
    }

    public Task<OperationLedgerSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Read());
    }

    public Task WriteAsync(OperationLedgerSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(snapshot);
        return Task.CompletedTask;
    }

    internal static OperationLedgerSnapshot Clone(OperationLedgerSnapshot snapshot)
        => new()
        {
            Version = snapshot.Version,
            Operations = snapshot.Operations.ToArray(),
            AuditEvents = snapshot.AuditEvents.ToArray()
        };

    internal static void Validate(OperationLedgerSnapshot snapshot)
    {
        if (snapshot.Version != OperationLedgerSnapshot.CurrentVersion)
            throw new OperationLedgerStoreException(
                $"Unsupported operation ledger version {snapshot.Version}.");
        if (snapshot.Operations is null)
            throw new OperationLedgerStoreException("Operation ledger operations cannot be null.");
        if (snapshot.AuditEvents is null)
            throw new OperationLedgerStoreException("Operation ledger audit events cannot be null.");
    }
}

/// <summary>
/// Durable JSON-backed operation store. Writes use a temporary file and an
/// atomic move, while a per-path gate prevents interleaved process-local
/// writers from producing a partial document.
/// </summary>
public class JsonOperationLedgerStore : IApprovalLedgerStore, IDisposable
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _path;
    private readonly SemaphoreSlim _gate;
    private readonly JsonSerializerOptions _jsonOptions;
    private bool _disposed;

    public JsonOperationLedgerStore(string path, JsonSerializerOptions? jsonOptions = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A ledger path is required.", nameof(path));

        _path = System.IO.Path.GetFullPath(path);
        _gate = Gates.GetOrAdd(_path, static _ => new SemaphoreSlim(1, 1));
        _jsonOptions = jsonOptions ?? CreateJsonOptions();
    }

    public string Path => _path;
    public string StorageId => $"json:{_path}";

    public static string DefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var root = string.IsNullOrWhiteSpace(appData)
            ? System.IO.Path.Combine(Environment.CurrentDirectory, ".kolarbitrage")
            : System.IO.Path.Combine(appData, "KolArbitrage");
        return System.IO.Path.Combine(root, "operations-ledger.json");
    }

    public OperationLedgerSnapshot Read()
    {
        ThrowIfDisposed();
        _gate.Wait();
        try
        {
            return ReadUnlocked();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Write(OperationLedgerSnapshot snapshot)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(snapshot);
        InMemoryOperationLedgerStore.Validate(snapshot);
        _gate.Wait();
        try
        {
            WriteUnlocked(snapshot);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationLedgerSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteAsync(OperationLedgerSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(snapshot);
        InMemoryOperationLedgerStore.Validate(snapshot);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteUnlockedAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        // Gates are shared by all instances for a path and intentionally remain
        // alive. Disposing one instance must not break a second instance.
    }

    private OperationLedgerSnapshot ReadUnlocked()
    {
        if (!File.Exists(_path))
            return OperationLedgerSnapshot.Empty;

        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 16 * 1024, FileOptions.SequentialScan);
            var snapshot = JsonSerializer.Deserialize<OperationLedgerSnapshot>(stream, _jsonOptions);
            if (snapshot is null)
                throw new OperationLedgerStoreException("The operation ledger file is empty.");
            InMemoryOperationLedgerStore.Validate(snapshot);
            return snapshot;
        }
        catch (OperationLedgerStoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new OperationLedgerStoreException(
                $"Could not read operation ledger '{_path}'.", exception);
        }
    }

    private async Task<OperationLedgerSnapshot> ReadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
            return OperationLedgerSnapshot.Empty;

        try
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var snapshot = await JsonSerializer.DeserializeAsync<OperationLedgerSnapshot>(
                stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
            if (snapshot is null)
                throw new OperationLedgerStoreException("The operation ledger file is empty.");
            InMemoryOperationLedgerStore.Validate(snapshot);
            return snapshot;
        }
        catch (OperationLedgerStoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new OperationLedgerStoreException(
                $"Could not read operation ledger '{_path}'.", exception);
        }
    }

    private void WriteUnlocked(OperationLedgerSnapshot snapshot)
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var temporary = _path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 16 * 1024, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, snapshot, _jsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new OperationLedgerStoreException(
                $"Could not write operation ledger '{_path}'.", exception);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch
            {
                // Preserve the original write exception if cleanup fails.
            }
        }
    }

    private async Task WriteUnlockedAsync(OperationLedgerSnapshot snapshot, CancellationToken cancellationToken)
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var temporary = _path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, _jsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, _path, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new OperationLedgerStoreException(
                $"Could not write operation ledger '{_path}'.", exception);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch
            {
                // Preserve the original write exception if cleanup fails.
            }
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
        => new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(JsonOperationLedgerStore));
    }
}

/// <summary>Compatibility name used by applications that call this a durable store.</summary>
public class DurableOperationLedgerStore : JsonOperationLedgerStore
{
    public DurableOperationLedgerStore(string path, JsonSerializerOptions? jsonOptions = null)
        : base(path, jsonOptions)
    {
    }
}

/// <summary>Compatibility name used by applications that call this a JSON file store.</summary>
public class JsonFileOperationLedgerStore : JsonOperationLedgerStore
{
    public JsonFileOperationLedgerStore(string path, JsonSerializerOptions? jsonOptions = null)
        : base(path, jsonOptions)
    {
    }
}
