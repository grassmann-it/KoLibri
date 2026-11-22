namespace KoLibri.Core.Basis;

/// <summary>
/// Coordinates the main and mallbot account metadata with their encrypted
/// credentials. The manager intentionally exposes role-based operations so a
/// transfer or pricing job cannot accidentally use the wrong account.
/// </summary>
public sealed class AccountSessionManager
{
    private readonly IAccountStore _accountStore;
    private readonly ISecretStore _secretStore;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _reconnectGate = new(1, 1);
    private AccountConfiguration? _configuration;

    public AccountSessionManager(
        IAccountStore accountStore,
        ISecretStore secretStore,
        IClock? clock = null)
    {
        _accountStore = accountStore ?? throw new ArgumentNullException(nameof(accountStore));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _clock = clock ?? new SystemClock();
    }

    /// <summary>
    /// Creates a manager backed by the per-user application-data files used
    /// by the desktop app.
    /// </summary>
    public static AccountSessionManager CreateDefault(IClock? clock = null) =>
        new(new JsonFileAccountStore(), new FileSecretStore(), clock);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _configuration ??= await _accountStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            JsonFileAccountStore.Validate(_configuration);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<AccountProfile>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _configuration!.Accounts.ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AccountProfile?> GetProfileAsync(AccountRole role, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return FindProfile(role);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AccountSession?> GetSessionAsync(
        AccountRole role,
        bool includeExpired = false,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profile = FindProfile(role);
            if (profile is null || !profile.Enabled)
                return null;

            var credentials = await _secretStore.GetAsync(profile.Id, cancellationToken).ConfigureAwait(false);
            if (credentials is null)
                return null;

            var session = new AccountSession(profile, credentials);
            return !includeExpired && session.IsExpired(now: _clock.UtcNow) ? null : session;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AccountSession> GetRequiredSessionAsync(
        AccountRole role,
        CancellationToken cancellationToken = default)
    {
        var session = await GetSessionAsync(role, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (session is null)
            throw new AccountConfigurationException(
                $"No valid credentials are configured for the {role} account.");
        return session;
    }

    /// <summary>Reconnects the same profile after a confirmed session expiry.</summary>
    public async Task<AccountSession?> ReconnectAsync(
        AccountSession expired,
        Func<string, string, CancellationToken, Task<KoLLoginResult>> login,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expired);
        ArgumentNullException.ThrowIfNull(login);
        await _reconnectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await GetSessionAsync(expired.Profile.Role, includeExpired: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (current is null || current.Profile != expired.Profile)
                return null;
            if (current.Credentials != expired.Credentials)
                return current.IsExpired(now: _clock.UtcNow) ? null : current;

            var username = current.Credentials.Username ?? current.Profile.DisplayName;
            var password = current.Credentials.Password;
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
                return null;

            var loginResult = await login(username, password, cancellationToken).ConfigureAwait(false);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (FindProfile(expired.Profile.Role) != current.Profile ||
                    await _secretStore.GetAsync(current.Profile.Id, cancellationToken).ConfigureAwait(false) != current.Credentials)
                    return null;

                var renewed = loginResult.Credentials with { Password = password, Username = username };
                await _secretStore.SetAsync(current.Profile.Id, renewed, cancellationToken).ConfigureAwait(false);
                return current with { Credentials = renewed };
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            _reconnectGate.Release();
        }
    }

    public async Task SetProfileAsync(AccountProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.Id))
            throw new ArgumentException("An account id is required.", nameof(profile));
        if (string.IsNullOrWhiteSpace(profile.DisplayName))
            throw new ArgumentException("A display name is required.", nameof(profile));

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var accounts = _configuration!.Accounts.ToList();
            var byRole = accounts.FindIndex(account => account.Role == profile.Role);
            var byId = accounts.FindIndex(account =>
                string.Equals(account.Id, profile.Id, StringComparison.OrdinalIgnoreCase));

            if (byId >= 0 && byRole >= 0 && byId != byRole)
                throw new AccountConfigurationException(
                    $"Account id '{profile.Id}' is already assigned to the {accounts[byId].Role} role.");

            string? replacedId = null;
            if (byRole >= 0)
            {
                replacedId = accounts[byRole].Id;
                accounts[byRole] = profile;
            }
            else
            {
                accounts.Add(profile);
            }

            var updated = _configuration with { Accounts = accounts };
            JsonFileAccountStore.Validate(updated);
            await _accountStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            _configuration = updated;

            // Do not leave credentials attached to a profile that was
            // replaced by another account in the same role.
            if (replacedId is not null &&
                !string.Equals(replacedId, profile.Id, StringComparison.OrdinalIgnoreCase))
            {
                await _secretStore.RemoveAsync(replacedId, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetCredentialsAsync(
        AccountRole role,
        AccountCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profile = FindProfile(role)
                ?? throw new AccountConfigurationException($"No profile is configured for the {role} account.");
            await _secretStore.SetAsync(profile.Id, credentials, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stores a fresh login for a role after the live session's player was verified. The profile
    /// (DisplayName, PlayerId) and the credentials change together: when the verified player differs
    /// from the stored one and <paramref name="allowAccountChange"/> is false nothing is written and
    /// <see cref="AccountIdentityMismatchException"/> is thrown.
    /// </summary>
    public async Task<AccountIdentityChange> ApplyVerifiedLoginAsync(
        AccountRole role, KoLPlayerIdentity identity, AccountCredentials credentials,
        bool allowAccountChange, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(credentials);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var accounts = _configuration!.Accounts.ToList();
            var index = accounts.FindIndex(account => account.Role == role);
            var change = AccountIdentityChange.Unchanged;
            AccountProfile profile;
            if (index < 0)
            {
                profile = new AccountProfile
                {
                    Id = role == AccountRole.Main ? "main" : "mallbot",
                    DisplayName = identity.Name, Role = role, PlayerId = identity.PlayerId, Enabled = true
                };
                accounts.Add(profile);
                change = AccountIdentityChange.Created;
            }
            else
            {
                var current = accounts[index];
                var same = current.PlayerId is { Length: > 0 } stored
                    ? string.Equals(stored, identity.PlayerId, StringComparison.Ordinal)
                    : string.Equals(current.DisplayName, identity.Name, StringComparison.OrdinalIgnoreCase);
                if (!same)
                {
                    if (!allowAccountChange)
                        throw new AccountIdentityMismatchException(
                            $"The {role} role belongs to '{current.DisplayName}', but this login is '{identity.Name}'. " +
                            "Nothing was saved. Use the explicit account switch to replace the role.");
                    change = AccountIdentityChange.Switched;
                }
                else if (current.PlayerId is null || !string.Equals(current.DisplayName, identity.Name, StringComparison.Ordinal))
                    change = AccountIdentityChange.Adopted;
                profile = current with { DisplayName = identity.Name, PlayerId = identity.PlayerId };
                accounts[index] = profile;
            }

            var previousConfiguration = _configuration;
            var updated = previousConfiguration with { Accounts = accounts };
            JsonFileAccountStore.Validate(updated);
            await _accountStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            try
            {
                await _secretStore.SetAsync(profile.Id, credentials, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await _accountStore.SaveAsync(previousConfiguration, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            _configuration = updated;
            return change;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearCredentialsAsync(AccountRole role, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profile = FindProfile(role);
            if (profile is not null)
                await _secretStore.RemoveAsync(profile.Id, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_configuration is not null)
            return;
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    private AccountProfile? FindProfile(AccountRole role) =>
        _configuration!.Accounts.FirstOrDefault(account => account.Role == role);
}

