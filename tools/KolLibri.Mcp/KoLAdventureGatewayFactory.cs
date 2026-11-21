using System.Collections.Concurrent;
using KolArbitrage.Core.Basis;
using KolArbitrage.Core.Spiel;

namespace KolLibri.Mcp;

/// <summary>
/// Live adventure gateways for the MCP bridge: one authenticated KoL session per role, kept for the
/// life of the host. The access gate in front of the tools is the approval; the token handed in here
/// is only recorded on the requests.
/// </summary>
public sealed class KoLAdventureGatewayFactory : IAdventureExecutionGatewayFactory, IAsyncDisposable
{
    private readonly AccountSessionManager _accounts;
    private readonly ConcurrentDictionary<AccountRole, Lazy<Task<KoLAdventureSession>>> _sessions = new();

    public KoLAdventureGatewayFactory(AccountSessionManager? accounts = null)
        => _accounts = accounts ?? AccountSessionManager.CreateDefault();

    public async ValueTask<AdventureExecutionGateway?> CreateAsync(
        AccountRole role, string approvalToken, CancellationToken cancellationToken)
    {
        var lazy = _sessions.GetOrAdd(role, r => new Lazy<Task<KoLAdventureSession>>(() =>
            KoLAdventureSessionFactory.CreateAsync(
                r, new TokenAdventureApprovalHook(approvalToken), new KnownEncounterVerificationHook(),
                _accounts, new InMemoryAdventureRunCheckpointStore(), cancellationToken: CancellationToken.None)));
        try
        {
            return (await lazy.Value.ConfigureAwait(false)).Gateway;
        }
        catch
        {
            _sessions.TryRemove(role, out _);   // let the next call retry (for example after a login)
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var lazy in _sessions.Values)
            if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully)
                await lazy.Value.Result.DisposeAsync().ConfigureAwait(false);
    }
}
