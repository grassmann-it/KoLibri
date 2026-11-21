using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KoLibri.Core.Basis;

/// <summary>
/// Small local secret store for access/refresh tokens. Values are encrypted
/// with AES-GCM and stored in per-account files. The generated key is kept in
/// the same application-data directory with owner-only permissions on Unix.
/// A production integration can replace this class with an OS keychain
/// implementation without changing the session layer.
/// </summary>
public sealed class FileSecretStore : ISecretStore
{
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly string _directory;
    private readonly string _keyPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private byte[]? _key;

    public FileSecretStore(string? directory = null, string? keyPath = null)
    {
        _directory = directory ?? DefaultDirectory();
        _keyPath = keyPath ?? Path.Combine(_directory, ".secret-key");
    }

    public async Task<AccountCredentials?> GetAsync(string accountId, CancellationToken cancellationToken = default)
    {
        ValidateAccountId(accountId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = SecretPath(accountId);
            if (!File.Exists(path))
                return null;

            try
            {
                await using var stream = File.OpenRead(path);
                var envelope = await JsonSerializer.DeserializeAsync<Envelope>(stream, _jsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                if (envelope is null)
                    throw new CryptographicException("Secret envelope is empty.");
                return Decrypt(accountId, envelope);
            }
            catch (JsonException exception)
            {
                throw new CryptographicException("Secret envelope is malformed.", exception);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAsync(string accountId, AccountCredentials credentials, CancellationToken cancellationToken = default)
    {
        ValidateAccountId(accountId);
        ArgumentNullException.ThrowIfNull(credentials);
        if (string.IsNullOrEmpty(credentials.AccessToken) && string.IsNullOrEmpty(credentials.Password))
            throw new ArgumentException("An access token or password is required.", nameof(credentials));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);
            var path = SecretPath(accountId);
            var envelope = Encrypt(accountId, credentials);
            var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 8 * 1024,
                    options: FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, envelope, _jsonOptions, cancellationToken)
                        .ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                RestrictFile(temporaryPath);
                File.Move(temporaryPath, path, overwrite: true);
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

    public async Task RemoveAsync(string accountId, CancellationToken cancellationToken = default)
    {
        ValidateAccountId(accountId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = SecretPath(accountId);
            if (File.Exists(path))
                File.Delete(path);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static string DefaultDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrWhiteSpace(appData)
            ? Path.Combine(Environment.CurrentDirectory, ".kolarbitrage", "secrets")
            : Path.Combine(appData, "KolArbitrage", "secrets");
    }

    private string SecretPath(string accountId)
    {
        // Hashing keeps arbitrary account ids (including separators) out of
        // the filesystem path while still giving each account a stable file.
        var idHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountId)));
        return Path.Combine(_directory, $"{idHash}.json");
    }

    private Envelope Encrypt(string accountId, AccountCredentials credentials)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(credentials, _jsonOptions);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(GetKey(), TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(accountId));
        CryptographicOperations.ZeroMemory(plaintext);
        return new Envelope
        {
            Version = 1,
            Nonce = Convert.ToBase64String(nonce),
            Ciphertext = Convert.ToBase64String(ciphertext),
            Tag = Convert.ToBase64String(tag)
        };
    }

    private AccountCredentials Decrypt(string accountId, Envelope envelope)
    {
        if (envelope.Version != 1)
            throw new CryptographicException($"Unsupported secret envelope version {envelope.Version}.");

        byte[] nonce;
        byte[] ciphertext;
        byte[] tag;
        try
        {
            nonce = Convert.FromBase64String(envelope.Nonce);
            ciphertext = Convert.FromBase64String(envelope.Ciphertext);
            tag = Convert.FromBase64String(envelope.Tag);
        }
        catch (FormatException exception)
        {
            throw new CryptographicException("Secret envelope contains invalid binary data.", exception);
        }

        if (nonce.Length != NonceSize || tag.Length != TagSize)
            throw new CryptographicException("Secret envelope has invalid nonce or tag length.");

        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(GetKey(), TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(accountId));
            var credentials = JsonSerializer.Deserialize<AccountCredentials>(plaintext, _jsonOptions);
            return credentials ?? throw new CryptographicException("Secret envelope contains no credentials.");
        }
        catch (JsonException exception)
        {
            throw new CryptographicException("Secret envelope contains invalid credentials.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private byte[] GetKey()
    {
        if (_key is not null)
            return _key;

        Directory.CreateDirectory(_directory);
        if (File.Exists(_keyPath))
        {
            var existing = File.ReadAllBytes(_keyPath);
            if (existing.Length != KeySize)
                throw new CryptographicException("The local secret key has an invalid length.");
            _key = existing;
            return _key;
        }

        var key = RandomNumberGenerator.GetBytes(KeySize);
        var temporaryPath = $"{_keyPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, key);
            RestrictFile(temporaryPath);
            File.Move(temporaryPath, _keyPath, overwrite: false);
            _key = key;
            return _key;
        }
        catch (IOException) when (File.Exists(_keyPath))
        {
            // Another process initialized the key first. Use its key so all
            // processes can read the same account files.
            CryptographicOperations.ZeroMemory(key);
            var existing = File.ReadAllBytes(_keyPath);
            if (existing.Length != KeySize)
                throw new CryptographicException("The local secret key has an invalid length.");
            _key = existing;
            return _key;
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static void ValidateAccountId(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            throw new ArgumentException("An account id is required.", nameof(accountId));
    }

    private static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsFreeBSD())
            return;

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (PlatformNotSupportedException)
        {
            // Windows ACLs are inherited from the application-data directory.
        }
        catch (UnauthorizedAccessException)
        {
            // Keep the store usable on filesystems that do not expose modes.
        }
    }

    private sealed record Envelope
    {
        public int Version { get; init; }
        public required string Nonce { get; init; }
        public required string Ciphertext { get; init; }
        public required string Tag { get; init; }
    }
}

