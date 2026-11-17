using KolArbitrage.Core.Accounts;
using KolArbitrage.Core.Integration;

namespace KolArbitrage.Core.Adventure;

/// <summary>
/// Session-bound construction boundary for adventure execution. Construction
/// reads credentials and creates the guarded transport, but performs no HTTP
/// request. The caller must provide explicit approval and verification hooks.
/// </summary>
public static class KoLAdventureSessionFactory
{
    public static string DefaultCheckpointDirectory
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KoLibri",
            "adventure-checkpoints");

    public static string DefaultAuditPath
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KoLibri",
            "adventure-audit.jsonl");

    public static async Task<KoLAdventureSession> CreateAsync(
        AccountRole role,
        IAdventureApprovalHook approval,
        IAdventureVerificationHook verification,
        AccountSessionManager? accounts = null,
        IAdventureRunCheckpointStore? checkpoints = null,
        IAuditEventJournal? auditJournal = null,
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
            var accountReader = new KoLAccountReadClient(http);
            var journal = auditJournal ?? new AuditEventJournal(DefaultAuditPath);
            var hooks = new AuditedAdventureHooks(approval, verification, journal);
            var gateway = new AdventureExecutionGateway(
                transport,
                checkpoints ?? new JsonFileAdventureRunCheckpointStore(DefaultCheckpointDirectory),
                approval: hooks,
                verification: hooks);
            return new KoLAdventureSession(session, http, transport, accountReader, gateway, journal, auditJournal is null);
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
    private readonly IAuditEventJournal? _ownedAuditJournal;

    internal KoLAdventureSession(
        AccountSession account,
        HttpClient http,
        HttpAdventureExecutionTransport transport,
        IAccountReadClient accountReader,
        AdventureExecutionGateway gateway,
        IAuditEventJournal auditJournal,
        bool ownsAuditJournal)
    {
        Account = account ?? throw new ArgumentNullException(nameof(account));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        AccountReader = accountReader ?? throw new ArgumentNullException(nameof(accountReader));
        Gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        AuditJournal = auditJournal ?? throw new ArgumentNullException(nameof(auditJournal));
        if (ownsAuditJournal) _ownedAuditJournal = auditJournal;
    }

    public AccountSession Account { get; }
    public AccountRole Role => Account.Profile.Role;
    public string AccountId => Account.Profile.Id;
    public AdventureExecutionGateway Gateway { get; }
    public HttpAdventureExecutionTransport Transport => _transport;
    public IAccountReadClient AccountReader { get; }
    public IAuditEventJournal AuditJournal { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Gateway.Dispose();
        _transport.Dispose();
        _http.Dispose();
        _ownedAuditJournal?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await Gateway.DisposeAsync().ConfigureAwait(false);
        _transport.Dispose();
        _http.Dispose();
        if (_ownedAuditJournal is not null)
            await _ownedAuditJournal.DisposeAsync().ConfigureAwait(false);
    }
}
