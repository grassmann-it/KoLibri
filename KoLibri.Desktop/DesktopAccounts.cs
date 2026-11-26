namespace KoLibri.Desktop;

/// <summary>The two accounts of the desktop. In 0.5 they are plain account ids ("main", "mallbot") of the KoLibri library.</summary>
public enum AccountRole { Main, Mallbot }

/// <summary>
/// The one <see cref="KolibriClient"/> of the desktop and one cached <see cref="KolibriSession"/> per role.
/// A missing or expired session is a normal state: <see cref="TryOpenAsync"/> returns null and the views say "sign in".
/// Automatic log-in with the stored password happens only while <see cref="KolibriOptions.AutoReconnect"/> allows it
/// (the Settings switch "Log in again automatically"); an explicit <see cref="ReconnectAsync"/> or <see cref="LoginAsync"/>
/// always sends.
/// </summary>
public sealed class DesktopAccounts : IAsyncDisposable
{
    private readonly Dictionary<AccountRole, KolibriSession> _sessions = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public DesktopAccounts(KolibriOptions options)
    {
        Client = KolibriClient.Create(options);
    }

    /// <summary>Real KoL, accounts in the 0.4 files, the bundled KoLmafia data next to the app.</summary>
    public static DesktopAccounts CreateDefault(LimiterOptions? limiter = null, Func<bool>? autoReconnect = null)
        => new(new KolibriOptions
        {
            Limiter = limiter ?? new LimiterOptions { ActionInterval = TimeSpan.FromSeconds(2), ReadInterval = TimeSpan.FromSeconds(1) },
            DataDirectory = BundledDataDirectory(),
            AutoReconnect = autoReconnect
        });

    public KolibriClient Client { get; }

    /// <summary>Raised after a login, logout or reconnect changed the session of a role.</summary>
    public event Action<AccountRole>? SessionChanged;

    public static string IdOf(AccountRole role) => role == AccountRole.Main ? "main" : "mallbot";

    public static AccountRole? RoleOf(AccountId id) => id.Value switch
    {
        "main" => AccountRole.Main,
        "mallbot" => AccountRole.Mallbot,
        _ => null
    };

    public static string? BundledDataDirectory()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "external", "kolmafia-data");
        return Directory.Exists(path) ? path : null;
    }

    /// <summary>The stored account of a role (with or without a usable session), null when none is stored.</summary>
    public Task<Account?> FindAsync(AccountRole role, CancellationToken cancellationToken = default)
        => Client.Accounts.FindAsync(IdOf(role), cancellationToken);

    /// <summary>
    /// The session of a role, or null without a stored account, with an expired session and no permitted automatic
    /// log-in, or when the automatic log-in failed. Never throws for those normal states.
    /// </summary>
    public async Task<KolibriSession?> TryOpenAsync(AccountRole role, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var account = await Client.Accounts.FindAsync(IdOf(role), cancellationToken).ConfigureAwait(false);
            var mayReconnect = account is { HasPassword: true } && (Client.Options.AutoReconnect?.Invoke() ?? true);
            if (_sessions.TryGetValue(role, out var cached))
            {
                if (!cached.IsExpired) return cached;
                // KoL refused the cookie earlier: renew it only when allowed, otherwise stay signed out (no request).
                if (!mayReconnect) return null;
                try
                {
                    await cached.ReconnectAsync(cancellationToken).ConfigureAwait(false);
                    return cached;
                }
                catch (KolibriException) { return null; }
            }
            if (account is null) return null;
            if (!account.HasSession && !mayReconnect) return null;
            try
            {
                var session = await Client.OpenAsync(IdOf(role), cancellationToken).ConfigureAwait(false);
                _sessions[role] = session;
                return session;
            }
            catch (SessionExpiredException) { return null; }
            catch (KolibriException) when (!account.HasSession) { return null; }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Like <see cref="TryOpenAsync"/>, but a missing session is an <see cref="InvalidOperationException"/> with a sign-in hint.</summary>
    public async Task<KolibriSession> RequireAsync(AccountRole role, CancellationToken cancellationToken = default)
        => await TryOpenAsync(role, cancellationToken).ConfigureAwait(false)
           ?? throw new InvalidOperationException($"{role} is not signed in. Sign in under Settings → Accounts.");

    /// <summary>Signs a role in with name and password; the role keeps its id even when a different player signs in.</summary>
    public async Task<KolibriSession> LoginAsync(AccountRole role, string userName, string password, bool rememberPassword,
        CancellationToken cancellationToken = default)
    {
        var session = await Client.LoginAsync(userName, password, new LoginOptions
        {
            AccountId = IdOf(role),
            RememberPassword = rememberPassword,
            // The desktop user typed this account into this role's fields on purpose (0.4 overwrote the profile too).
            AllowIdentityChange = true
        }, cancellationToken).ConfigureAwait(false);
        await ReplaceAsync(role, session).ConfigureAwait(false);
        return session;
    }

    /// <summary>Logs in again with the stored password (an explicit user or startup decision). Null when no password is stored or KoL refused.</summary>
    public async Task<KolibriSession?> ReconnectAsync(AccountRole role, CancellationToken cancellationToken = default)
    {
        var account = await FindAsync(role, cancellationToken).ConfigureAwait(false);
        if (account is null || !account.HasPassword) return null;
        KolibriSession? session = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { _sessions.TryGetValue(role, out session); }
        finally { _gate.Release(); }
        if (session is null)
        {
            // OpenAsync with a cookie never sends; the reconnect below is the one login request.
            try { session = await Client.OpenAsync(IdOf(role), cancellationToken).ConfigureAwait(false); }
            catch (SessionExpiredException) { return null; }
        }
        await session.ReconnectAsync(cancellationToken).ConfigureAwait(false);
        await ReplaceAsync(role, session).ConfigureAwait(false);
        return session;
    }

    /// <summary>Forgets cookie and stored password of a role; the account entry stays.</summary>
    public async Task LogoutAsync(AccountRole role, CancellationToken cancellationToken = default)
    {
        await Client.Accounts.LogoutAsync(new AccountId(IdOf(role)), cancellationToken).ConfigureAwait(false);
        await ReplaceAsync(role, null).ConfigureAwait(false);
    }

    /// <summary>True when an account is stored for the role but its session is gone (no automatic log-in was attempted or it failed).</summary>
    public async Task<bool> HasExpiredSessionAsync(AccountRole role, CancellationToken cancellationToken = default)
    {
        var account = await FindAsync(role, cancellationToken).ConfigureAwait(false);
        if (account is null) return false;
        if (!account.HasSession) return true;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return _sessions.TryGetValue(role, out var s) && s.IsExpired; }
        finally { _gate.Release(); }
    }

    private async Task ReplaceAsync(AccountRole role, KolibriSession? session)
    {
        KolibriSession? old;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _sessions.TryGetValue(role, out old);
            if (session is null) _sessions.Remove(role); else _sessions[role] = session;
        }
        finally { _gate.Release(); }
        if (old is not null && !ReferenceEquals(old, session)) await old.DisposeAsync().ConfigureAwait(false);
        SessionChanged?.Invoke(role);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await Client.DisposeAsync().ConfigureAwait(false);
        _sessions.Clear();
    }
}

/// <summary>Accounts kept in memory only (offline screenshots and tests): nothing touches the real account files.</summary>
public sealed class InMemoryAccountStore : IAccountStore
{
    private readonly Dictionary<string, AccountRecord> _records = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<AccountRecord>> LoadAsync(CancellationToken cancellationToken)
    {
        lock (_records) return Task.FromResult<IReadOnlyList<AccountRecord>>(_records.Values.ToArray());
    }

    public Task SaveAsync(AccountRecord record, CancellationToken cancellationToken)
    {
        lock (_records) _records[record.Account.Id.Value] = record;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(AccountId id, CancellationToken cancellationToken)
    {
        lock (_records) _records.Remove(id.Value);
        return Task.CompletedTask;
    }
}
