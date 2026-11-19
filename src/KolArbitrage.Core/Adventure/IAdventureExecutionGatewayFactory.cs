using KolArbitrage.Core.Accounts;

namespace KolArbitrage.Core.Adventure;

/// <summary>
/// Host-supplied factory that produces an <see cref="AdventureExecutionGateway"/>
/// for a given role. Hosts that have not configured a live transport return
/// <c>null</c>, which keeps the bridge in preview-only mode.
/// </summary>
public interface IAdventureExecutionGatewayFactory
{
    ValueTask<AdventureExecutionGateway?> CreateAsync(
        AccountRole role,
        string approvalToken,
        CancellationToken cancellationToken);
}

/// <summary>
/// Convenience adapter that builds an <see cref="AdventureExecutionGateway"/>
/// from an already-running transport + checkpoint store. Hosts wire the
/// actual HTTP client; the bridge simply wires approval + verification hooks.
/// </summary>
public sealed class TransportAdventureExecutionGatewayFactory : IAdventureExecutionGatewayFactory
{
    private readonly IAdventureTransport _transport;
    private readonly IAdventureRunCheckpointStore _checkpointStore;
    private readonly string? _expectedToken;

    public TransportAdventureExecutionGatewayFactory(
        IAdventureTransport transport,
        IAdventureRunCheckpointStore checkpointStore,
        string? expectedToken = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
        _expectedToken = expectedToken;
    }

    public ValueTask<AdventureExecutionGateway?> CreateAsync(
        AccountRole role,
        string approvalToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_expectedToken is not null
            && !string.Equals(approvalToken, _expectedToken, StringComparison.Ordinal))
            return ValueTask.FromResult<AdventureExecutionGateway?>(null);
        var gateway = new AdventureExecutionGateway(
            _transport,
            _checkpointStore,
            approval: new TokenAdventureApprovalHook(approvalToken),
            verification: new KnownEncounterVerificationHook());
        return ValueTask.FromResult<AdventureExecutionGateway?>(gateway);
    }
}

/// <summary>
/// Per-step approval hook that approves every step. The factory has already
/// validated the supplied token against the host-bound one before the gateway
/// was constructed; the per-step hook simply records the token on the
/// transport request for audit purposes.
/// </summary>
public sealed class TokenAdventureApprovalHook : IAdventureApprovalHook
{
    private readonly string _expected;

    public TokenAdventureApprovalHook(string expected)
        => _expected = expected ?? throw new ArgumentNullException(nameof(expected));

    public ValueTask<AdventureApprovalDecision> ApproveAsync(
        AdventureApprovalContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(AdventureApprovalDecision.Allow(_expected));
    }
}

/// <summary>
/// Verification hook used by the bridge factories: accepts every non-Unknown
/// response unless the transport explicitly marked it as not accepted.
/// </summary>
public sealed class KnownEncounterVerificationHook : IAdventureVerificationHook
{
    public ValueTask<AdventureVerificationResult> VerifyAsync(
        AdventureVerificationContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var known = context.Observation.Kind is not AdventureEncounterKind.Unknown
                    && context.Response.Accepted != false;
        return known
            ? ValueTask.FromResult(AdventureVerificationResult.Success())
            : ValueTask.FromResult(AdventureVerificationResult.Failed(
                "The live response was not a known, accepted adventure encounter."));
    }
}
