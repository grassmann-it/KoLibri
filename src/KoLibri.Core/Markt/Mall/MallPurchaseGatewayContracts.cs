using KoLibri.Core.Spiel;
namespace KoLibri.Core.Markt;

/// <summary>
/// The seller, store, item, unit price and observed quantity that identify one
/// Mall offer.  A purchase preview binds to this value; a different offer is
/// never silently substituted during refresh or commit.
/// </summary>
public sealed record MallPurchaseOffer
{
    public MallPurchaseOffer()
    {
    }

    public MallPurchaseOffer(
        string sellerId,
        string storeId,
        string itemId,
        long unitPrice,
        int quantity,
        DateTimeOffset observedAt,
        int? purchaseLimit = null,
        string? sellerName = null,
        string? storeName = null)
    {
        SellerId = sellerId;
        StoreId = storeId;
        ItemId = itemId;
        UnitPrice = unitPrice;
        Quantity = quantity;
        ObservedAt = observedAt;
        PurchaseLimit = purchaseLimit;
        SellerName = sellerName;
        StoreName = storeName;
    }

    public string SellerId { get; init; } = string.Empty;
    public string StoreId { get; init; } = string.Empty;
    public string ItemId { get; init; } = string.Empty;
    public long UnitPrice { get; init; }
    public int Quantity { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
    public int? PurchaseLimit { get; init; }
    public string? SellerName { get; init; }
    public string? StoreName { get; init; }

    // Compatibility aliases keep the contract readable to callers that use
    // listing/price/qty terminology.
    public string Seller { get => SellerId; init => SellerId = value; }
    public string Store { get => StoreId; init => StoreId = value; }
    public string Item { get => ItemId; init => ItemId = value; }
    public long Price { get => UnitPrice; init => UnitPrice = value; }
    public long PricePerItem { get => UnitPrice; init => UnitPrice = value; }
    public int Qty { get => Quantity; init => Quantity = value; }
    public int AvailableQuantity { get => Quantity; init => Quantity = value; }
    public DateTimeOffset FetchedAt => ObservedAt;

    public long CostFor(int quantity) => checked(UnitPrice * (long)quantity);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SellerId))
            throw new ArgumentException("Seller id is required.", nameof(SellerId));
        if (string.IsNullOrWhiteSpace(StoreId))
            throw new ArgumentException("Store id is required.", nameof(StoreId));
        if (string.IsNullOrWhiteSpace(ItemId))
            throw new ArgumentException("Item id is required.", nameof(ItemId));
        if (UnitPrice < 0)
            throw new ArgumentOutOfRangeException(nameof(UnitPrice));
        if (Quantity < 0)
            throw new ArgumentOutOfRangeException(nameof(Quantity));
        if (PurchaseLimit is < 0)
            throw new ArgumentOutOfRangeException(nameof(PurchaseLimit));
    }
}

/// <summary>The account state read next to an exact Mall offer.</summary>
public sealed record MallPurchaseAccountState
{
    public MallPurchaseAccountState()
    {
    }

    public MallPurchaseAccountState(
        string accountId,
        long meat,
        long dailySpent,
        long dailyLimit,
        IReadOnlyDictionary<string, int>? inventory,
        DateTimeOffset observedAt,
        bool sessionValid = true,
        bool isStale = false)
    {
        AccountId = accountId;
        Meat = meat;
        DailySpent = dailySpent;
        DailyLimit = dailyLimit;
        Inventory = inventory ?? new Dictionary<string, int>(StringComparer.Ordinal);
        ObservedAt = observedAt;
        SessionValid = sessionValid;
        IsStale = isStale;
    }

    public string AccountId { get; init; } = "default";
    public long Meat { get; init; }
    public long DailySpent { get; init; }
    public long DailyLimit { get; init; }
    public IReadOnlyDictionary<string, int> Inventory { get; init; }
        = new Dictionary<string, int>(StringComparer.Ordinal);
    public DateTimeOffset ObservedAt { get; init; }
    public bool SessionValid { get; init; } = true;
    public bool IsStale { get; init; }

    public DateTimeOffset FetchedAt => ObservedAt;
    public long RemainingDaily => DailyLimit <= DailySpent ? 0 : DailyLimit - DailySpent;

    public int InventoryQuantity(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId)) return 0;
        return Inventory.TryGetValue(itemId, out var quantity) ? Math.Max(0, quantity) : 0;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(AccountId))
            throw new ArgumentException("Account id is required.", nameof(AccountId));
        if (Meat < 0)
            throw new ArgumentOutOfRangeException(nameof(Meat));
        if (DailySpent < 0)
            throw new ArgumentOutOfRangeException(nameof(DailySpent));
        if (DailyLimit < 0)
            throw new ArgumentOutOfRangeException(nameof(DailyLimit));
        if (Inventory.Any(x => x.Value < 0))
            throw new ArgumentException("Inventory quantities cannot be negative.", nameof(Inventory));
    }
}

/// <summary>One consistent read of the offer and the account buying it.</summary>
public sealed record MallPurchaseObservation(
    MallPurchaseOffer Offer,
    MallPurchaseAccountState Account,
    DateTimeOffset ObservedAt)
{
    public MallPurchaseObservation(MallPurchaseOffer offer, MallPurchaseAccountState account)
        : this(offer, account,
            new[] { offer.ObservedAt, account.ObservedAt }.Max())
    {
    }

    public MallPurchaseOffer Listing => Offer;
    public MallPurchaseAccountState Status => Account;
    public DateTimeOffset FetchedAt => ObservedAt;
    public bool IsStale => Account.IsStale;

    public int InventoryQuantity(string itemId) => Account.InventoryQuantity(itemId);

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Offer);
        ArgumentNullException.ThrowIfNull(Account);
        Offer.Validate();
        Account.Validate();
    }
}

/// <summary>
/// An exact-offer request.  The optional expected daily limit binds the
/// request to the account policy observed during preview.
/// </summary>
public sealed record MallPurchaseRequest
{
    public MallPurchaseRequest()
    {
    }

    public MallPurchaseRequest(
        string sellerId,
        string storeId,
        string itemId,
        long expectedUnitPrice,
        int quantity,
        long? expectedDailyLimit = null,
        string accountId = "default")
    {
        AccountId = accountId;
        SellerId = sellerId;
        StoreId = storeId;
        ItemId = itemId;
        ExpectedUnitPrice = expectedUnitPrice;
        Quantity = quantity;
        ExpectedDailyLimit = expectedDailyLimit;
    }

    /// <summary>Account-first form used by gateway callers.</summary>
    public MallPurchaseRequest(
        string accountId,
        string sellerId,
        string storeId,
        string itemId,
        long expectedUnitPrice,
        int quantity,
        long? expectedDailyLimit = null)
        : this(sellerId, storeId, itemId, expectedUnitPrice, quantity, expectedDailyLimit, accountId)
    {
    }

    public MallPurchaseRequest(
        string accountId,
        MallPurchaseOffer offer,
        int quantity,
        long? expectedDailyLimit = null)
        : this(offer.SellerId, offer.StoreId, offer.ItemId, offer.UnitPrice,
            quantity, expectedDailyLimit, accountId)
    {
    }

    public string AccountId { get; init; } = "default";
    public string SellerId { get; init; } = string.Empty;
    public string StoreId { get; init; } = string.Empty;
    public string ItemId { get; init; } = string.Empty;
    public long ExpectedUnitPrice { get; init; }
    public int Quantity { get; init; }
    public long? ExpectedDailyLimit { get; init; }
    public string? ExpectedSellerName { get; init; }
    public string? ExpectedStoreName { get; init; }

    public string Seller { get => SellerId; init => SellerId = value; }
    public string Store { get => StoreId; init => StoreId = value; }
    public string Item { get => ItemId; init => ItemId = value; }
    public long UnitPrice { get => ExpectedUnitPrice; init => ExpectedUnitPrice = value; }
    public long Price { get => ExpectedUnitPrice; init => ExpectedUnitPrice = value; }
    public long ExpectedPrice { get => ExpectedUnitPrice; init => ExpectedUnitPrice = value; }
    public int Qty { get => Quantity; init => Quantity = value; }
    public int ExpectedQuantity { get => Quantity; init => Quantity = value; }
    public long? DailyLimit { get => ExpectedDailyLimit; init => ExpectedDailyLimit = value; }
    public long TotalCost => checked(ExpectedUnitPrice * (long)Quantity);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(AccountId))
            throw new ArgumentException("Account id is required.", nameof(AccountId));
        if (string.IsNullOrWhiteSpace(SellerId))
            throw new ArgumentException("Seller id is required.", nameof(SellerId));
        if (string.IsNullOrWhiteSpace(StoreId))
            throw new ArgumentException("Store id is required.", nameof(StoreId));
        if (string.IsNullOrWhiteSpace(ItemId))
            throw new ArgumentException("Item id is required.", nameof(ItemId));
        if (ExpectedUnitPrice < 0)
            throw new ArgumentOutOfRangeException(nameof(ExpectedUnitPrice));
        if (Quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(Quantity));
        if (ExpectedDailyLimit is < 0)
            throw new ArgumentOutOfRangeException(nameof(ExpectedDailyLimit));
        _ = TotalCost;
    }
}

/// <summary>Limits enforced while constructing and committing a preview.</summary>
public sealed record MallPurchasePolicy
{
    public bool DryRun { get; init; } = true;
    public bool RequireApproval { get; init; } = true;
    public TimeSpan PreviewLifetime { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan MaxObservationAge { get; init; } = TimeSpan.FromMinutes(2);
    public long? MaxDailySpend { get; init; } = 500_000;
    public long? MaxMeatPerPurchase { get; init; } = 250_000;
    public int? MaxQuantityPerPurchase { get; init; } = 10;
    /// <summary>Meat that must stay on the account after the purchase, checked against the fresh commit-time reading (0 .. 1 000 000 000, default 0).</summary>
    public long MeatReserve { get; init; }

    public void Validate()
    {
        if (PreviewLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(PreviewLifetime));
        if (MaxObservationAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(MaxObservationAge));
        if (MaxDailySpend is < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxDailySpend));
        if (MaxMeatPerPurchase is < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxMeatPerPurchase));
        if (MeatReserve is < 0 or > 1_000_000_000)
            throw new ArgumentOutOfRangeException(nameof(MeatReserve));
        if (MaxQuantityPerPurchase is <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxQuantityPerPurchase));
    }

    public static MallPurchasePolicy FromAutomationPolicy(AutomationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        return new MallPurchasePolicy
        {
            DryRun = policy.DryRun,
            RequireApproval = policy.RequireApprovalForPurchases,
            MaxDailySpend = policy.MaxDailyMeatSpend,
            MaxMeatPerPurchase = policy.MaxMeatPerPurchase,
            MaxQuantityPerPurchase = policy.MaxQuantityPerPurchase
        };
    }
}

/// <summary>Reasons a refresh or commit is refused.</summary>
public enum MallPurchaseAbortReason
{
    None = 0,
    InvalidRequest,
    MissingOffer,
    SellerChanged,
    StoreChanged,
    ItemChanged,
    PriceChanged,
    QuantityChanged,
    SoldOut,
    PurchaseLimitExceeded,
    PurchaseLimitChanged,
    DailyLimitChanged,
    DailyLimitExceeded,
    DailyBudgetExceeded = DailyLimitExceeded,
    InsufficientMeat,
    PerPurchaseLimitExceeded,
    QuantityLimitExceeded,
    StaleOffer,
    StaleAccount,
    SessionInvalid,
    PreviewExpired,
    ApprovalRequired,
    DryRun,
    CommitRejected,
    TransportFailure,
    AccountChanged,
    MeatChanged,
    InventoryChanged,
    DailySpendChanged,
    VerificationFailed,
    MeatVerificationFailed,
    InventoryVerificationFailed,
    ItemNotReceived,
    UnexpectedQuantity
}

public enum MallPurchaseVerificationStatus
{
    NotAttempted,
    Verified,
    Failed,
    Aborted
}

/// <summary>Pure review object returned before any mutation is attempted.</summary>
public sealed record MallPurchasePreview
{
    public MallPurchasePreview()
    {
    }

    public MallPurchasePreview(
        string actionId,
        MallPurchaseRequest request,
        MallPurchaseObservation observation,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        bool requiresApproval,
        bool dryRun,
        IReadOnlyList<MallPurchaseAbortReason>? abortReasons = null,
        IReadOnlyList<string>? warnings = null)
    {
        ActionId = actionId;
        Request = request;
        Observation = observation;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        RequiresApproval = requiresApproval;
        DryRun = dryRun;
        AbortReasons = abortReasons ?? Array.Empty<MallPurchaseAbortReason>();
        Warnings = warnings ?? Array.Empty<string>();
    }

    public string ActionId { get; init; } = string.Empty;
    public MallPurchaseRequest Request { get; init; } = new();
    public MallPurchaseObservation Observation { get; init; } = new(
        new MallPurchaseOffer(), new MallPurchaseAccountState(), DateTimeOffset.MinValue);
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public bool RequiresApproval { get; init; } = true;
    public bool DryRun { get; init; } = true;
    public string? ApprovalToken { get; init; }
    public IReadOnlyList<MallPurchaseAbortReason> AbortReasons { get; init; }
        = Array.Empty<MallPurchaseAbortReason>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public MallPurchaseOffer Offer => Observation.Offer;
    public MallPurchaseAccountState Account => Observation.Account;
    public MallPurchaseObservation ExpectedState => Observation;
    public long TotalCost => Request.TotalCost;
    public long EstimatedMeat => TotalCost;
    public bool IsReady => AbortReasons.Count == 0;
    public IReadOnlyList<MallPurchaseAbortReason> Reasons => AbortReasons;
    public MallPurchaseAbortReason? PrimaryAbortReason
        => AbortReasons.Count == 0 ? null : AbortReasons[0];
}

/// <summary>Result of a fresh pre-commit read.</summary>
public sealed record MallPurchaseRefreshResult
{
    public MallPurchaseRefreshResult()
    {
    }

    public MallPurchaseRefreshResult(
        MallPurchasePreview preview,
        MallPurchaseObservation observation,
        IReadOnlyList<MallPurchaseAbortReason>? abortReasons = null,
        IReadOnlyList<string>? warnings = null)
    {
        Preview = preview;
        Observation = observation;
        AbortReasons = abortReasons ?? Array.Empty<MallPurchaseAbortReason>();
        Warnings = warnings ?? Array.Empty<string>();
    }

    public MallPurchasePreview Preview { get; init; } = new();
    public MallPurchaseObservation Observation { get; init; } = new(
        new MallPurchaseOffer(), new MallPurchaseAccountState(), DateTimeOffset.MinValue);
    public IReadOnlyList<MallPurchaseAbortReason> AbortReasons { get; init; }
        = Array.Empty<MallPurchaseAbortReason>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public bool IsReady => AbortReasons.Count == 0;
    public bool Ready => IsReady;
    public MallPurchaseObservation Current => Observation;
    public MallPurchaseObservation Fresh => Observation;
    public MallPurchaseAbortReason? PrimaryAbortReason
        => AbortReasons.Count == 0 ? null : AbortReasons[0];
}

/// <summary>Low-level request supplied to a transport after a fresh refresh.</summary>
public sealed record MallPurchaseTransportCommit
{
    public MallPurchaseTransportCommit()
    {
    }

    public MallPurchaseTransportCommit(
        MallPurchasePreview preview,
        MallPurchaseObservation before,
        string? approvalToken,
        DateTimeOffset committedAt)
    {
        Preview = preview;
        Before = before;
        ApprovalToken = approvalToken;
        CommittedAt = committedAt;
    }

    public MallPurchasePreview Preview { get; init; } = new();
    public MallPurchaseObservation Before { get; init; } = new(
        new MallPurchaseOffer(), new MallPurchaseAccountState(), DateTimeOffset.MinValue);
    public string? ApprovalToken { get; init; }
    public DateTimeOffset CommittedAt { get; init; }
}

/// <summary>Transport acknowledgement and post-mutation observation.</summary>
public sealed record MallPurchaseTransportReceipt
{
    public MallPurchaseTransportReceipt()
    {
    }

    public MallPurchaseTransportReceipt(
        bool accepted,
        MallPurchaseObservation before,
        MallPurchaseObservation after,
        string? message = null,
        DateTimeOffset? committedAt = null)
    {
        Accepted = accepted;
        Before = before;
        After = after;
        Message = message;
        CommittedAt = committedAt ?? DateTimeOffset.UtcNow;
    }

    public bool Accepted { get; init; }
    public bool Committed => Accepted;
    /// <summary>
    /// Typed outcome flag: the POST may have been applied but could not be proven (timeout, 5xx, 429, unreadable
    /// answer, failed reconciliation). Not <see cref="Accepted"/> and not a certain rejection: the spend stays
    /// reserved until reconciled. Never derived from <see cref="Message"/>.
    /// </summary>
    public bool Indeterminate { get; init; }
    /// <summary>The purchase is confirmed or may have happened; only a receipt without this is a certain rejection.</summary>
    public bool MaybeSent => Accepted || Indeterminate;
    public MallPurchaseObservation Before { get; init; } = new(
        new MallPurchaseOffer(), new MallPurchaseAccountState(), DateTimeOffset.MinValue);
    public MallPurchaseObservation After { get; init; } = new(
        new MallPurchaseOffer(), new MallPurchaseAccountState(), DateTimeOffset.MinValue);
    public string? Message { get; init; }
    public DateTimeOffset CommittedAt { get; init; }
}

/// <summary>Result returned by a pure post-commit verifier.</summary>
public sealed record MallPurchaseVerificationResult
{
    public MallPurchaseVerificationStatus Status { get; init; }
    public bool Verified => Status == MallPurchaseVerificationStatus.Verified;
    public bool Succeeded => Verified;
    public IReadOnlyList<MallPurchaseAbortReason> AbortReasons { get; init; }
        = Array.Empty<MallPurchaseAbortReason>();
    public IReadOnlyList<string> Messages { get; init; } = Array.Empty<string>();
    public MallPurchaseObservation? Before { get; init; }
    public MallPurchaseObservation? After { get; init; }
    public MallPurchaseAbortReason? PrimaryAbortReason
        => AbortReasons.Count == 0 ? null : AbortReasons[0];
}

/// <summary>Final gateway result including transport acknowledgement and verification.</summary>
public sealed record MallPurchaseCommitResult
{
    public string ActionId { get; init; } = string.Empty;
    public MallPurchaseVerificationStatus Status { get; init; }
    public bool Committed { get; init; }
    public bool Accepted { get; init; }
    public bool Verified => Status == MallPurchaseVerificationStatus.Verified;
    public bool Succeeded => Verified;
    public bool Aborted => Status == MallPurchaseVerificationStatus.Aborted;
    public IReadOnlyList<MallPurchaseAbortReason> AbortReasons { get; init; }
        = Array.Empty<MallPurchaseAbortReason>();
    public IReadOnlyList<string> Messages { get; init; } = Array.Empty<string>();
    public MallPurchaseObservation? Before { get; init; }
    public MallPurchaseObservation? After { get; init; }
    public MallPurchaseTransportReceipt? Receipt { get; init; }
    public MallPurchaseAbortReason? PrimaryAbortReason
        => AbortReasons.Count == 0 ? null : AbortReasons[0];
}

/// <summary>
/// Transport boundary for a future live Mall adapter.  The Core project ships
/// only an in-memory fixture implementation; no HTTP implementation is wired.
/// </summary>
public interface IMallPurchaseTransport
{
    Task<MallPurchaseObservation> RefreshAsync(
        MallPurchaseRequest request,
        CancellationToken cancellationToken = default);

    Task<MallPurchaseTransportReceipt> CommitAsync(
        MallPurchaseTransportCommit request,
        CancellationToken cancellationToken = default);
}

/// <summary>One serial mutation lane per account.</summary>
public interface IAccountExecutionLane
{
    string AccountId { get; }

    Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default);

    Task ExecuteAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default);
}

/// <summary>Short compatibility name for account-scoped serial execution.</summary>
public interface IAccountLane : IAccountExecutionLane
{
}

/// <summary>
/// A small semaphore-backed account lane.  Reads and mutations sent through a
/// lane execute in order, preventing two purchase commits for one account from
/// racing each other.
/// </summary>
public sealed class SerialAccountLane : IAccountLane, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public SerialAccountLane(string accountId = "default")
    {
        AccountId = string.IsNullOrWhiteSpace(accountId) ? "default" : accountId.Trim();
    }

    public string AccountId { get; }

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(operation, cancellationToken);

    public Task<T> EnqueueAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(operation, cancellationToken);

    public Task ExecuteAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return ExecuteAsync<object?>(async token =>
        {
            await operation(token).ConfigureAwait(false);
            return null;
        }, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }
}

/// <summary>Convenience provider when a host owns more than one account lane.</summary>
public sealed class SerialAccountLanePool : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SerialAccountLane> _lanes = new(StringComparer.OrdinalIgnoreCase);

    public IAccountExecutionLane Get(string accountId)
    {
        var key = string.IsNullOrWhiteSpace(accountId) ? "default" : accountId.Trim();
        lock (_gate)
        {
            if (_lanes.TryGetValue(key, out var lane)) return lane;
            lane = new SerialAccountLane(key);
            _lanes[key] = lane;
            return lane;
        }
    }

    public IAccountExecutionLane For(string accountId) => Get(accountId);

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var lane in _lanes.Values) lane.Dispose();
            _lanes.Clear();
        }
    }
}

/// <summary>Preview/refresh/commit/verify boundary for exact Mall offers.</summary>
public interface IMallPurchaseGateway
{
    Task<MallPurchasePreview> PreviewAsync(
        MallPurchaseRequest request,
        CancellationToken cancellationToken = default);

    Task<MallPurchaseRefreshResult> RefreshAsync(
        MallPurchasePreview preview,
        CancellationToken cancellationToken = default);

    Task<MallPurchaseCommitResult> CommitAsync(
        MallPurchasePreview preview,
        string? approvalToken = null,
        CancellationToken cancellationToken = default);

    MallPurchaseVerificationResult Verify(
        MallPurchasePreview preview,
        MallPurchaseObservation before,
        MallPurchaseObservation after,
        bool commitAcknowledged = true);
}
