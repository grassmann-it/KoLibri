using System.Text.Json;
using System.Text.Json.Serialization;

namespace KolArbitrage.Core.Markt;

/// <summary>
/// Persists seller/store exclusions in a local JSON file. Writes are atomic,
/// matching the account store behavior, and no KoL or other network calls are
/// made by this type.
/// </summary>
public sealed class JsonFileSellerBlacklistStore : ISellerBlacklistRepository
{
    private readonly string _path;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonFileSellerBlacklistStore(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? DefaultPath() : path;
        _jsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public string Path => _path;

    public async Task<SellerBlacklistDatabase> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
                return new SellerBlacklistDatabase();

            await using var stream = File.OpenRead(_path);
            var database = await JsonSerializer.DeserializeAsync<SellerBlacklistDatabase>(
                    stream, _jsonOptions, cancellationToken)
                .ConfigureAwait(false) ?? new SellerBlacklistDatabase();
            SellerBlacklistStoreValidation.Validate(database);
            return database;
        }
        catch (JsonException exception)
        {
            throw new SellerBlacklistConfigurationException(
                $"The seller blacklist file '{_path}' is not valid JSON: {exception.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        SellerBlacklistDatabase database,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        SellerBlacklistStoreValidation.Validate(database);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
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
                    await JsonSerializer.SerializeAsync(stream, database, _jsonOptions, cancellationToken)
                        .ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporaryPath, _path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public static string DefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var root = string.IsNullOrWhiteSpace(appData)
            ? System.IO.Path.Combine(Environment.CurrentDirectory, ".kolarbitrage")
            : System.IO.Path.Combine(appData, "KolArbitrage");
        return System.IO.Path.Combine(root, "seller-blacklist.json");
    }
}

/// <summary>
/// Short alias retained for callers that refer to the persisted file simply
/// as a blacklist store.
/// </summary>
public sealed class JsonFileBlacklistStore : ISellerBlacklistRepository
{
    private readonly JsonFileSellerBlacklistStore _inner;

    public JsonFileBlacklistStore(string? path = null)
    {
        _inner = new JsonFileSellerBlacklistStore(path);
    }

    public string Path => _inner.Path;

    public static string DefaultPath() => JsonFileSellerBlacklistStore.DefaultPath();

    public Task<SellerBlacklistDatabase> LoadAsync(CancellationToken cancellationToken = default) =>
        _inner.LoadAsync(cancellationToken);

    public Task SaveAsync(SellerBlacklistDatabase database, CancellationToken cancellationToken = default) =>
        _inner.SaveAsync(database, cancellationToken);
}

/// <summary>Alias for code that calls the persisted repository a blacklist store.</summary>
public sealed class SellerBlacklistStore : ISellerBlacklistRepository
{
    private readonly JsonFileSellerBlacklistStore _inner;

    public SellerBlacklistStore(string? path = null)
    {
        _inner = new JsonFileSellerBlacklistStore(path);
    }

    public string Path => _inner.Path;

    public static string DefaultPath() => JsonFileSellerBlacklistStore.DefaultPath();

    public Task<SellerBlacklistDatabase> LoadAsync(CancellationToken cancellationToken = default) =>
        _inner.LoadAsync(cancellationToken);

    public Task SaveAsync(SellerBlacklistDatabase database, CancellationToken cancellationToken = default) =>
        _inner.SaveAsync(database, cancellationToken);
}

/// <summary>Explicitly named alias for the JSON-backed seller repository.</summary>
public sealed class JsonSellerBlacklistStore : ISellerBlacklistRepository
{
    private readonly JsonFileSellerBlacklistStore _inner;

    public JsonSellerBlacklistStore(string? path = null)
    {
        _inner = new JsonFileSellerBlacklistStore(path);
    }

    public string Path => _inner.Path;

    public static string DefaultPath() => JsonFileSellerBlacklistStore.DefaultPath();

    public Task<SellerBlacklistDatabase> LoadAsync(CancellationToken cancellationToken = default) =>
        _inner.LoadAsync(cancellationToken);

    public Task SaveAsync(SellerBlacklistDatabase database, CancellationToken cancellationToken = default) =>
        _inner.SaveAsync(database, cancellationToken);
}
