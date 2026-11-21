using System.Text.Json;
using System.Text.Json.Serialization;

namespace KolArbitrage.Core.Basis;

/// <summary>
/// Stores account metadata as a small JSON file. Writes are atomic so an
/// interrupted update cannot leave a half-written account list behind.
/// </summary>
public sealed class JsonFileAccountStore : IAccountStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonFileAccountStore(string? path = null)
    {
        _path = path ?? DefaultPath();
        _jsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public async Task<AccountConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
                return new AccountConfiguration();

            await using var stream = File.OpenRead(_path);
            var value = await JsonSerializer.DeserializeAsync<AccountConfiguration>(stream, _jsonOptions, cancellationToken)
                .ConfigureAwait(false);
            var configuration = value ?? new AccountConfiguration();
            Validate(configuration);
            return configuration;
        }
        catch (JsonException exception)
        {
            throw new AccountConfigurationException($"The account file '{_path}' is not valid JSON: {exception.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AccountConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Validate(configuration);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_path);
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
                    await JsonSerializer.SerializeAsync(stream, configuration, _jsonOptions, cancellationToken)
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
            ? Path.Combine(Environment.CurrentDirectory, ".kolarbitrage")
            : Path.Combine(appData, "KolArbitrage");
        return Path.Combine(root, "accounts.json");
    }

    internal static void Validate(AccountConfiguration configuration)
    {
        if (configuration.Version < 1)
            throw new AccountConfigurationException($"Unsupported account configuration version {configuration.Version}.");

        var duplicateId = configuration.Accounts
            .GroupBy(account => account.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1);
        if (duplicateId is not null)
            throw new AccountConfigurationException($"Account ids must be non-empty and unique: '{duplicateId.Key}'.");

        var duplicateRole = configuration.Accounts
            .GroupBy(account => account.Role)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateRole is not null)
            throw new AccountConfigurationException($"Only one account may have the '{duplicateRole.Key}' role.");

        foreach (var account in configuration.Accounts)
        {
            if (string.IsNullOrWhiteSpace(account.DisplayName))
                throw new AccountConfigurationException($"Account '{account.Id}' must have a display name.");
        }
    }
}

