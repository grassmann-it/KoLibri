namespace KolArbitrage.Core.Markt;

/// <summary>
/// Deterministic in-memory transport for contract and workflow tests.  It
/// models one account and one exact offer, has no HttpClient dependency, and
/// never opens a network connection.
/// </summary>
public sealed class FixtureMallPurchaseTransport : IMallPurchaseTransport
{
    private readonly object _gate = new();
    private readonly Queue<MallPurchaseObservation> _refreshes = new();
    private MallPurchaseObservation _state;
    private bool _rejectNextCommit;
    private string? _rejectMessage;
    private bool _disposed;

    public FixtureMallPurchaseTransport(MallPurchaseObservation initialState)
    {
        initialState.Validate();
        _state = Clone(initialState);
    }

    public FixtureMallPurchaseTransport(
        MallPurchaseOffer offer,
        MallPurchaseAccountState account)
        : this(new MallPurchaseObservation(offer, account))
    {
    }

    public FixtureMallPurchaseTransport(
        string accountId,
        string sellerId,
        string storeId,
        string itemId,
        long unitPrice,
        int quantity,
        long meat,
        long dailySpent,
        long dailyLimit,
        DateTimeOffset? observedAt = null,
        IReadOnlyDictionary<string, int>? inventory = null,
        int? purchaseLimit = null)
        : this(
            new MallPurchaseOffer(
                sellerId, storeId, itemId, unitPrice, quantity,
                observedAt ?? DateTimeOffset.UtcNow, purchaseLimit),
            new MallPurchaseAccountState(
                accountId, meat, dailySpent, dailyLimit, inventory,
                observedAt ?? DateTimeOffset.UtcNow))
    {
    }

    public int RefreshCount { get; private set; }
    public int CommitCount { get; private set; }
    public int HttpRequestCount => 0;
    public bool UsedHttp => false;
    public MallPurchaseObservation State
    {
        get
        {
            lock (_gate) return Clone(_state);
        }
    }

    /// <summary>Next state returned by a refresh, useful for price/sold-out fixtures.</summary>
    public void EnqueueRefresh(MallPurchaseObservation observation)
    {
        observation.Validate();
        lock (_gate) _refreshes.Enqueue(Clone(observation));
    }

    public void SetState(MallPurchaseObservation observation)
    {
        observation.Validate();
        lock (_gate) _state = Clone(observation);
    }

    /// <summary>Make the next commit return a rejected acknowledgement.</summary>
    public void RejectNextCommit(string? message = null)
    {
        lock (_gate)
        {
            _rejectNextCommit = true;
            _rejectMessage = message;
        }
    }

    public Task<MallPurchaseObservation> RefreshAsync(
        MallPurchaseRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RefreshCount++;
            if (_refreshes.Count > 0)
                _state = Clone(_refreshes.Dequeue());
            return Task.FromResult(Clone(_state));
        }
    }

    public Task<MallPurchaseTransportReceipt> CommitAsync(
        MallPurchaseTransportCommit request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CommitCount++;
            var before = Clone(_state);

            if (_rejectNextCommit)
            {
                _rejectNextCommit = false;
                return Task.FromResult(new MallPurchaseTransportReceipt(
                    accepted: false,
                    before,
                    before,
                    _rejectMessage));
            }

            var preview = request.Preview.Request;
            var offer = before.Offer;
            var account = before.Account;
            if (!Matches(preview, before)
                || offer.Quantity < preview.Quantity
                || offer.PurchaseLimit is { } limit && limit < preview.Quantity
                || account.Meat < preview.TotalCost
                || account.DailySpent + preview.TotalCost > account.DailyLimit)
            {
                return Task.FromResult(new MallPurchaseTransportReceipt(
                    accepted: false,
                    before,
                    before,
                    "Fixture state no longer satisfies the exact-offer contract."));
            }

            var inventory = new Dictionary<string, int>(account.Inventory, StringComparer.Ordinal);
            inventory[preview.ItemId] = account.InventoryQuantity(preview.ItemId) + preview.Quantity;
            var afterOffer = offer with
            {
                Quantity = offer.Quantity - preview.Quantity,
                ObservedAt = request.CommittedAt
            };
            var afterAccount = account with
            {
                Meat = account.Meat - preview.TotalCost,
                DailySpent = account.DailySpent + preview.TotalCost,
                Inventory = inventory,
                ObservedAt = request.CommittedAt
            };
            var after = new MallPurchaseObservation(afterOffer, afterAccount, request.CommittedAt);
            _state = after;
            return Task.FromResult(new MallPurchaseTransportReceipt(
                accepted: true,
                before,
                after,
                committedAt: request.CommittedAt));
        }
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
    }

    private static bool Matches(MallPurchaseRequest request, MallPurchaseObservation observation)
        => string.Equals(request.AccountId, observation.Account.AccountId, StringComparison.OrdinalIgnoreCase)
           && string.Equals(request.SellerId, observation.Offer.SellerId, StringComparison.Ordinal)
           && string.Equals(request.StoreId, observation.Offer.StoreId, StringComparison.Ordinal)
           && string.Equals(request.ItemId, observation.Offer.ItemId, StringComparison.Ordinal)
           && request.ExpectedUnitPrice == observation.Offer.UnitPrice;

    private static MallPurchaseObservation Clone(MallPurchaseObservation observation)
    {
        var offer = observation.Offer with { };
        var account = observation.Account with
        {
            Inventory = new Dictionary<string, int>(observation.Account.Inventory, StringComparer.Ordinal)
        };
        return observation with { Offer = offer, Account = account };
    }
}

/// <summary>Short alias for callers that use the fixture transport name.</summary>
public sealed class MallPurchaseFixtureTransport : IMallPurchaseTransport
{
    private readonly FixtureMallPurchaseTransport _inner;

    public MallPurchaseFixtureTransport(MallPurchaseObservation initialState)
    {
        _inner = new FixtureMallPurchaseTransport(initialState);
    }

    public MallPurchaseFixtureTransport(MallPurchaseOffer offer, MallPurchaseAccountState account)
        : this(new MallPurchaseObservation(offer, account))
    {
    }

    public MallPurchaseObservation State => _inner.State;
    public int RefreshCount => _inner.RefreshCount;
    public int CommitCount => _inner.CommitCount;
    public int HttpRequestCount => _inner.HttpRequestCount;
    public bool UsedHttp => _inner.UsedHttp;

    public void EnqueueRefresh(MallPurchaseObservation observation) => _inner.EnqueueRefresh(observation);
    public void SetState(MallPurchaseObservation observation) => _inner.SetState(observation);
    public void RejectNextCommit(string? message = null) => _inner.RejectNextCommit(message);
    public Task<MallPurchaseObservation> RefreshAsync(MallPurchaseRequest request, CancellationToken cancellationToken = default)
        => _inner.RefreshAsync(request, cancellationToken);
    public Task<MallPurchaseTransportReceipt> CommitAsync(MallPurchaseTransportCommit request, CancellationToken cancellationToken = default)
        => _inner.CommitAsync(request, cancellationToken);
}
