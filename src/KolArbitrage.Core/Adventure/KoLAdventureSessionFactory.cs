using KolArbitrage.Core.Accounts;

namespace KolArbitrage.Core.Adventure;

/// <summary>
/// Session-bound construction boundary for adventure execution. Construction
/// reads credentials and creates the guarded transport, but performs no HTTP
/// request. The caller must provide explicit approval and verification hooks.
/// </summary>
public static class KoLAdventureSessionFactory
{
    public static async Task<KoLAdventureSession> CreateAsync(
        AccountRole role,
        IAdventureApprovalHook approval,
        IAdventureVerificationHook verification,
        AccountSessionManager? accounts = null,
        IAdventureRunCheckpointStore? checkpoints = null,
        AdventureHttpTransportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(verification);
        var manager = accounts ?? AccountSessionManager.CreateDefault();
        var session = await manager.GetRequiredSessionAsync(role, cancellationToken).ConfigureAwait(false);
        var binding = new AdventureTransportAccountBinding(
            session.Profile.Role,
            session.Profile.Id,
            session.Profile.DisplayName);
        var http = KoLLoginClient.CreateAuthenticatedClient(
            session.Credentials,
            new Uri("https://www.kingdomofloathing.com/"));
        try
        {
            var transport = new HttpAdventureExecutionTransport(http, binding, options);
            var gateway = new AdventureExecutionGateway(
                transport,
                checkpoints ?? new InMemoryAdventureRunCheckpointStore(),
                approval: approval,
                verification: verification);
            return new KoLAdventureSession(session, http, transport, gateway);
        }
        catch
        {
            http.Dispose();
            throw;
        }
    }
}

/// <summary>Owns the account-bound HTTP client and adventure gateway lifetime.</summary>
public sealed class KoLAdventureSession : IAsyncDisposable, IDisposable
{
    private readonly HttpClient _http;
    private readonly HttpAdventureExecutionTransport _transport;
    private bool _disposed;

    internal KoLAdventureSession(
        AccountSession account,
        HttpClient http,
        HttpAdventureExecutionTransport transport,
        AdventureExecutionGateway gateway)
    {
        Account = account ?? throw new ArgumentNullException(nameof(account));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        Gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
    }

    public AccountSession Account { get; }
    public AccountRole Role => Account.Profile.Role;
    public string AccountId => Account.Profile.Id;
    public AdventureExecutionGateway Gateway { get; }
    public HttpAdventureExecutionTransport Transport => _transport;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Gateway.Dispose();
        _transport.Dispose();
        _http.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await Gateway.DisposeAsync().ConfigureAwait(false);
        _transport.Dispose();
        _http.Dispose();
    }
}
