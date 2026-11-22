using System.Collections.Concurrent;
using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;

namespace KolLibri.Mcp;

/// <summary>
/// Live adventure gateways for the MCP bridge: one authenticated KoL session per role, kept for the
/// life of the host. The access gate in front of the tools is the approval; the token handed in here
/// is only recorded on the requests.
/// </summary>
public sealed class KoLAdventureGatewayFactory : IAdventureExecutionGatewayFactory, IOpenChoiceProbe, IAsyncDisposable
{
    private readonly AccountSessionManager _accounts;
    private sealed record Entry(string Key, Lazy<Task<KoLAdventureSession>> Session);
    private readonly ConcurrentDictionary<AccountRole, Entry> _sessions = new();
    private readonly object _gate = new();

    public KoLAdventureGatewayFactory(AccountSessionManager? accounts = null)
        => _accounts = accounts ?? AccountSessionManager.CreateDefault();

    /// <summary>
    /// The session for a role, bound to the stored player and login cookie. A login, logout or renewal
    /// (changed binding) drops the old session, its HTTP client and its checkpoints and builds a new one.
    /// </summary>
    private async Task<(Entry Entry, Lazy<Task<KoLAdventureSession>> Lazy)> GetEntryAsync(
        AccountRole role, string approvalToken, CancellationToken cancellationToken)
    {
        var current = await _accounts.GetRequiredSessionAsync(role, cancellationToken).ConfigureAwait(false);
        var key = current.BindingKey();
        Entry? stale = null;
        Entry entry;
        lock (_gate)
        {
            if (_sessions.TryGetValue(role, out var existing) && existing.Key == key)
                entry = existing;
            else
            {
                stale = existing;
                entry = new Entry(key, new Lazy<Task<KoLAdventureSession>>(() =>
                    KoLAdventureSessionFactory.CreateAsync(
                        role, new TokenAdventureApprovalHook(approvalToken), new KnownEncounterVerificationHook(),
                        _accounts, new InMemoryAdventureRunCheckpointStore(), cancellationToken: CancellationToken.None)));
                _sessions[role] = entry;
            }
        }
        if (stale is not null) await DisposeEntryAsync(stale).ConfigureAwait(false);
        return (entry, entry.Session);
    }

    private static async Task DisposeEntryAsync(Entry entry)
    {
        if (entry.Session.IsValueCreated && entry.Session.Value.IsCompletedSuccessfully)
            await entry.Session.Value.Result.DisposeAsync().ConfigureAwait(false);
    }

    private void Forget(AccountRole role, Entry entry)
    {
        lock (_gate)
            if (_sessions.TryGetValue(role, out var current) && ReferenceEquals(current, entry))
                _sessions.TryRemove(role, out _);
    }

    public async ValueTask<AdventureExecutionGateway?> CreateAsync(
        AccountRole role, string approvalToken, CancellationToken cancellationToken)
    {
        var (entry, lazy) = await GetEntryAsync(role, approvalToken, cancellationToken).ConfigureAwait(false);
        try
        {
            return (await lazy.Value.ConfigureAwait(false)).Gateway;
        }
        catch
        {
            Forget(role, entry);   // let the next call retry (for example after a login)
            throw;
        }
    }

    /// <summary>Read only (GET main.php / choice.php). Shares the per-role session; the token is only an audit tag.</summary>
    public async ValueTask<OpenChoiceDetection> DetectOpenChoiceAsync(AccountRole role, CancellationToken cancellationToken)
    {
        var (entry, lazy) = await GetEntryAsync(role, "kolibri-mcp-gated", cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await lazy.Value.ConfigureAwait(false);
            return await session.DetectOpenChoiceAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException && e is not OpenChoiceException)
        {
            if (lazy.Value.IsFaulted) Forget(role, entry);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var entry in _sessions.Values)
            await DisposeEntryAsync(entry).ConfigureAwait(false);
    }
}
