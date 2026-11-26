using System.Net;
using KoLibri.Core.Basis;

namespace KoLibri.Core.Spiel;

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
        var http = KoLLoginClient.CreateAuthenticatedClient(
            session.Credentials, new Uri("https://www.kingdomofloathing.com/"));
        return Create(session, http, approval, verification, checkpoints, auditJournal, options,
            UnknownPageArchive.DefaultDirectory);
    }

    /// <summary>
    /// Creates a session and takes ownership of the supplied HTTP client. Pass
    /// <paramref name="unknownPageDirectory"/> (usually <see cref="UnknownPageArchive.DefaultDirectory"/>)
    /// to keep redacted copies of pages a run could not handle; null keeps none.
    /// </summary>
    public static KoLAdventureSession Create(
        AccountSession session, HttpClient http,
        IAdventureApprovalHook approval, IAdventureVerificationHook verification,
        IAdventureRunCheckpointStore? checkpoints = null, IAuditEventJournal? auditJournal = null,
        AdventureHttpTransportOptions? options = null, string? unknownPageDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(verification);
        var binding = new AdventureTransportAccountBinding(
            session.Profile.Role, session.Profile.Id, session.Profile.DisplayName);
        try
        {
            var accountReader = new KoLAccountReadClient(http);
            var effective = options ?? new AdventureHttpTransportOptions();
            if (effective.StateProbe is null)
                effective = effective with { StateProbe = ct => ReadAdventureStateAsync(http, accountReader, effective.BaseUri, ct) };
            var transport = new HttpAdventureExecutionTransport(http, binding, effective);
            var journal = auditJournal ?? new AuditEventJournal(DefaultAuditPath);
            var hooks = new AuditedAdventureHooks(approval, verification, journal);
            var gateway = new AdventureExecutionGateway(
                transport,
                checkpoints ?? new JsonFileAdventureRunCheckpointStore(DefaultCheckpointDirectory),
                approval: hooks,
                verification: hooks)
            {
                UnknownPageDirectory = unknownPageDirectory
            };
            return new KoLAdventureSession(session, http, transport, accountReader, gateway, journal, auditJournal is null);
        }
        catch
        {
            http.Dispose();
            throw;
        }
    }

    /// <summary>Adventures from the status API; an encounter counts as open when fight.php or choice.php answers 200 instead of redirecting.</summary>
    internal static async Task<AdventureStateSnapshot> ReadAdventureStateAsync(
        HttpClient http, IAccountReadClient reader, Uri baseUri, CancellationToken ct)
    {
        var status = await reader.FetchStatusAsync(forceRefresh: true, ct).ConfigureAwait(false);
        foreach (var page in new[] { "fight.php", "choice.php" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, page));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            // First safely open encounter: return at once, a later probe failure must not lose it.
            // fight.php also answers 200 with "Not in a Fight" when nothing is open: that is no encounter.
            if (response.StatusCode == HttpStatusCode.OK
                && !(page == "fight.php" && KoLAdventureEncounterParser.IsNotInAFightPage(
                    await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false))))
                return new AdventureStateSnapshot(status.Adventures, true, Fresh: !status.IsStale && status.AdventuresKnown);
            if (response.StatusCode == HttpStatusCode.OK) continue;
            // Only a redirect with a usable target means "no encounter"; anything else is unknown.
            var freeRedirect = code is >= 300 and < 400 && response.Headers.Location is not null;
            if (!freeRedirect)
                throw new HttpRequestException(
                    $"Encounter probe {page} answered {code}; encounter state unknown.");
        }
        return new AdventureStateSnapshot(status.Adventures, false, Fresh: !status.IsStale && status.AdventuresKnown);
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

    /// <summary>Read only: is a choice (or fight) open on this account right now?</summary>
    public Task<OpenChoiceDetection> DetectOpenChoiceAsync(CancellationToken cancellationToken = default)
        => new OpenChoiceClient(_http).DetectAsync(cancellationToken);

    /// <summary>Submits one option to the open choice (one POST). Callers must have the user's approval.</summary>
    public Task<OpenChoiceSubmitResult> SubmitOpenChoiceAsync(string choiceId, int option, CancellationToken cancellationToken = default)
        => new OpenChoiceClient(_http).SubmitAsync(choiceId, option, cancellationToken);

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
