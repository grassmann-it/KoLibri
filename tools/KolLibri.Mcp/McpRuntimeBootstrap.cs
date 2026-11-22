using LibKolibri;
using KoLibri.Core.Basis;
using KoLibri.Core.Markt;
using KoLibri.Core.Spiel;

namespace KolLibri.Mcp;

/// <summary>
/// Builds the MCP read runtime from the encrypted role sessions configured by
/// the host. Tool arguments carry only the role name; cookies and tokens stay
/// inside the authenticated HTTP clients created here. The returned binder keeps
/// each role's clients bound to its stored player and session: after a login,
/// logout or renewal the clients are rebuilt, and a role that was empty at
/// start gets its clients as soon as it signs in.
/// </summary>
public static class McpRuntimeBootstrap
{
    /// <summary>
    /// Loads configured Main and Mallbot sessions and registers one account and
    /// Mall reader per role. Missing profiles or credentials are skipped so an
    /// MCP process can still serve local planning tools.
    /// </summary>
    public static async Task<RoleScopedRuntime> CreateConfiguredAsync(
        AccountSessionManager? sessions = null,
        CancellationToken cancellationToken = default)
        => (await CreateBinderAsync(sessions, cancellationToken).ConfigureAwait(false)).Runtime;

    /// <summary>Like <see cref="CreateConfiguredAsync"/> but returns the binder that refreshes the runtime.</summary>
    public static async Task<McpRuntimeBinder> CreateBinderAsync(
        AccountSessionManager? sessions = null,
        CancellationToken cancellationToken = default)
    {
        sessions ??= AccountSessionManager.CreateDefault();
        foreach (var role in Enum.GetValues<AccountRole>())
        {
            // Renew an expired session with the saved password before the readers capture its cookie.
            try { using var _ = await KolibriClient.OpenAsync(role, sessions, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is KolibriLoginRequiredException or HttpRequestException or KoLHtmlResponseException) { }
        }

        var binder = new McpRuntimeBinder(sessions);
        await binder.RefreshAsync(cancellationToken).ConfigureAwait(false);
        return binder;
    }

    /// <summary>Compatibility spelling for hosts that call this a factory.</summary>
    public static Task<RoleScopedRuntime> CreateAsync(
        AccountSessionManager? sessions = null,
        CancellationToken cancellationToken = default)
        => CreateConfiguredAsync(sessions, cancellationToken);
}

/// <summary>
/// Owns the per-role read clients of an MCP runtime and rebuilds them (status readers, mall readers,
/// gateways) whenever the role's stored session or player changes. Old HTTP clients are disposed, so a
/// replaced account's cookie is never used again.
/// </summary>
public sealed class McpRuntimeBinder : IDisposable
{
    private static readonly Uri DefaultEndpoint = new("https://www.kingdomofloathing.com/", UriKind.Absolute);
    private readonly AccountSessionManager _sessions;
    private readonly Func<AccountSession, Uri, HttpClient> _createHttp;
    private readonly Dictionary<AccountRole, (string Key, HttpClient Http)> _bound = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    public McpRuntimeBinder(AccountSessionManager sessions, RoleScopedRuntime? runtime = null,
        Func<AccountSession, Uri, HttpClient>? createHttp = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        Runtime = runtime ?? new RoleScopedRuntime();
        _createHttp = createHttp ?? ((session, endpoint) => KoLLoginClient.CreateAuthenticatedClient(session.Credentials, endpoint));
    }

    public RoleScopedRuntime Runtime { get; }

    /// <summary>Raised after a role's clients were rebuilt or removed (role, hasSession).</summary>
    public event Action<AccountRole, bool>? RoleRebound;

    /// <summary>Compares each role's current session with the bound one and rebuilds what changed.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var role in Enum.GetValues<AccountRole>())
            {
                var session = await _sessions.GetSessionAsync(role, cancellationToken: cancellationToken).ConfigureAwait(false);
                var usable = session is not null && !string.IsNullOrWhiteSpace(session.Credentials.CookieHeader)
                    && (session.Profile.Endpoint ?? DefaultEndpoint) is { IsAbsoluteUri: true, Scheme: "https" };
                var key = usable ? session!.BindingKey() : null;
                var hadBinding = _bound.TryGetValue(role, out var old);
                if (hadBinding ? key == old.Key : key is null) continue;

                if (hadBinding)
                {
                    Runtime.ClearRole(role);
                    old.Http.Dispose();
                    _bound.Remove(role);
                }
                if (key is not null)
                {
                    var endpoint = session!.Profile.Endpoint ?? DefaultEndpoint;
                    var http = _createHttp(session, endpoint);
                    var account = new KoLAccountReadClient(http, endpoint);
                    var mall = new KoLMallReadClient(http, endpoint, itemNameResolver: BundledItemNames.Resolver);
                    Runtime.Register<IAccountReadClient>(role, account);
                    Runtime.Register<IMallReadClient>(role, mall);
                    Runtime.Register<IAccountGateway>(role, new CoreAccountGateway(account));
                    Runtime.Register<IMallGateway>(role, new CoreMallGateway(mall));
                    _bound[role] = (key, http);
                }
                RoleRebound?.Invoke(role, key is not null);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        foreach (var (_, http) in _bound.Values) http.Dispose();
        _bound.Clear();
        _gate.Dispose();
    }
}
