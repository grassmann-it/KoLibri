using System.Text.Json.Serialization;

namespace KolArbitrage.Core.Accounts;

/// <summary>
/// The two roles used by the arbitrage workflow. A role is deliberately
/// separate from an account id so that the same account can be replaced
/// without changing automation settings.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AccountRole
{
    Main,
    Mallbot
}

/// <summary>
/// Non-secret information needed to identify an account. Credentials are
/// kept in <see cref="ISecretStore"/> and never serialized with this record.
/// </summary>
public sealed record AccountProfile
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required AccountRole Role { get; init; }

    /// <summary>
    /// Optional endpoint override. This lets a test or a future game client
    /// use a different host without baking it into the account store.
    /// </summary>
    public Uri? Endpoint { get; init; }
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// OAuth/session material kept in the secret store. Access and refresh tokens
/// are treated as opaque values by the core library.
/// </summary>
public sealed record AccountCredentials
{
    /// <summary>KoL's session/pwd value after a successful login, when available.</summary>
    public string? AccessToken { get; init; }
    /// <summary>Login name used by the KoL login form.</summary>
    public string? Username { get; init; }
    /// <summary>Password is encrypted by the secret store and never written to account metadata.</summary>
    public string? Password { get; init; }
    public string? RefreshToken { get; init; }
    /// <summary>
    /// Opaque cookie header captured after a KoL login. It is encrypted by
    /// the configured secret store together with the session token.
    /// </summary>
    public string? CookieHeader { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}

public sealed record AccountSession(AccountProfile Profile, AccountCredentials Credentials)
{
    public bool IsExpired(TimeSpan? clockSkew = null, DateTimeOffset? now = null)
    {
        if (Credentials.ExpiresAt is null)
            return false;

        var skew = clockSkew ?? TimeSpan.FromMinutes(1);
        return Credentials.ExpiresAt <= (now ?? DateTimeOffset.UtcNow).Add(skew);
    }
}

/// <summary>
/// Persisted account configuration. No token or password belongs in this
/// object; callers should use <see cref="ISecretStore"/> for those values.
/// </summary>
public sealed record AccountConfiguration
{
    public int Version { get; init; } = 1;
    public List<AccountProfile> Accounts { get; init; } = [];
}

public interface IAccountStore
{
    Task<AccountConfiguration> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AccountConfiguration configuration, CancellationToken cancellationToken = default);
}

public interface ISecretStore
{
    Task<AccountCredentials?> GetAsync(string accountId, CancellationToken cancellationToken = default);
    Task SetAsync(string accountId, AccountCredentials credentials, CancellationToken cancellationToken = default);
    Task RemoveAsync(string accountId, CancellationToken cancellationToken = default);
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class AccountConfigurationException(string message) : Exception(message);

