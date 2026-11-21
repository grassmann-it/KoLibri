using KoLibri.Core.Spiel;
namespace KoLibri.Core.Markt;

/// <summary>
/// The exact listing a human approved.  A commit must still compare this
/// contract with a fresh Mall read; a preview is never an order by itself.
/// </summary>
public sealed record PurchaseRequest(
    string ItemId,
    string StoreId,
    long ExpectedUnitPrice,
    int Quantity,
    string? ExpectedStoreName = null)
{
    /// <summary>Alias used by clients which call the amount a unit price.</summary>
    public long UnitPrice => ExpectedUnitPrice;

    /// <summary>Alias used by serializers that prefix values with Expected.</summary>
    public long ExpectedPrice => ExpectedUnitPrice;

    /// <summary>Alias used by serializers that prefix values with Expected.</summary>
    public int ExpectedQuantity => Quantity;

    /// <summary>Alias used by UI clients when they display the expected seller.</summary>
    public string? StoreName => ExpectedStoreName;

    public long TotalCost => checked(ExpectedUnitPrice * (long)Quantity);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ItemId))
            throw new ArgumentException("An item id is required.", nameof(ItemId));
        if (string.IsNullOrWhiteSpace(StoreId))
            throw new ArgumentException("A store id is required.", nameof(StoreId));
        if (ExpectedUnitPrice < 0)
            throw new ArgumentOutOfRangeException(nameof(ExpectedUnitPrice));
        if (Quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(Quantity));
        _ = TotalCost; // force overflow to be reported at the boundary
    }
}

/// <summary>Meat and quantity limits evaluated while creating a preview.</summary>
public sealed record PurchaseBudget
{
    public PurchaseBudget()
    {
    }

    public PurchaseBudget(
        long availableMeat,
        long dailySpent,
        long dailyLimit,
        long meatReserve = 0,
        long? maxMeatPerPurchase = null,
        int? maxQuantityPerPurchase = null)
    {
        AvailableMeat = availableMeat;
        DailySpent = dailySpent;
        DailyLimit = dailyLimit;
        MeatReserve = meatReserve;
        MaxMeatPerPurchase = maxMeatPerPurchase;
        MaxQuantityPerPurchase = maxQuantityPerPurchase;
    }

    public long AvailableMeat { get; init; }
    public long DailySpent { get; init; }
    public long DailyLimit { get; init; } = 500_000;
    public long MeatReserve { get; init; }
    public long? MaxMeatPerPurchase { get; init; } = 250_000;
    public int? MaxQuantityPerPurchase { get; init; } = 10;

    public long RemainingDaily
        => DailyLimit <= DailySpent ? 0 : DailyLimit - DailySpent;

    public long SpendableMeat
        => AvailableMeat <= MeatReserve ? 0 : AvailableMeat - MeatReserve;

    public void Validate()
    {
        if (AvailableMeat < 0) throw new ArgumentOutOfRangeException(nameof(AvailableMeat));
        if (DailySpent < 0) throw new ArgumentOutOfRangeException(nameof(DailySpent));
        if (DailyLimit < 0) throw new ArgumentOutOfRangeException(nameof(DailyLimit));
        if (MeatReserve < 0) throw new ArgumentOutOfRangeException(nameof(MeatReserve));
        if (MaxMeatPerPurchase is < 0) throw new ArgumentOutOfRangeException(nameof(MaxMeatPerPurchase));
        if (MaxQuantityPerPurchase is <= 0) throw new ArgumentOutOfRangeException(nameof(MaxQuantityPerPurchase));
    }

    public static PurchaseBudget FromAutomationPolicy(
        AutomationPolicy policy,
        long availableMeat,
        long dailySpent = 0,
        long meatReserve = 0)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        return new PurchaseBudget
        {
            AvailableMeat = availableMeat,
            DailySpent = dailySpent,
            DailyLimit = policy.MaxDailyMeatSpend,
            MeatReserve = meatReserve,
            MaxMeatPerPurchase = policy.MaxMeatPerPurchase,
            MaxQuantityPerPurchase = policy.MaxQuantityPerPurchase
        };
    }
}

/// <summary>Controls freshness and lifetime of purchase approvals.</summary>
public sealed record PurchaseApprovalPolicy
{
    public bool DryRun { get; init; } = true;
    public bool RequireApproval { get; init; } = true;
    public TimeSpan MaxObservationAge { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan PreviewLifetime { get; init; } = TimeSpan.FromMinutes(5);
    public long? MaxMeatPerPurchase { get; init; } = 250_000;
    public int? MaxQuantityPerPurchase { get; init; } = 10;

    public void Validate()
    {
        if (MaxObservationAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(MaxObservationAge));
        if (PreviewLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(PreviewLifetime));
        if (MaxMeatPerPurchase is < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxMeatPerPurchase));
        if (MaxQuantityPerPurchase is <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxQuantityPerPurchase));
    }

    public static PurchaseApprovalPolicy FromAutomationPolicy(AutomationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        return new PurchaseApprovalPolicy
        {
            DryRun = policy.DryRun,
            RequireApproval = policy.RequireApprovalForPurchases,
            MaxMeatPerPurchase = policy.MaxMeatPerPurchase,
            MaxQuantityPerPurchase = policy.MaxQuantityPerPurchase
        };
    }
}

/// <summary>
/// State captured in a preview.  It is deliberately small and serializable so
/// it can be shown in an approval UI without exposing cookies or credentials.
/// </summary>
public sealed record PurchaseExpectedState(
    string ItemId,
    string StoreId,
    string? StoreName,
    long UnitPrice,
    int RequestedQuantity,
    int AvailableQuantity,
    long MeatBefore,
    int InventoryQuantityBefore,
    DateTimeOffset ObservedAt,
    bool ListingIsStale = false,
    bool StatusIsStale = false,
    bool InventoryIsStale = false)
{
    public long ExpectedPrice => UnitPrice;
    public int ExpectedQuantity => RequestedQuantity;
    public long TotalCost => checked(UnitPrice * (long)RequestedQuantity);
    public long ExpectedMeatAfter => MeatBefore - TotalCost;
    public int ExpectedInventoryQuantityAfter => checked(InventoryQuantityBefore + RequestedQuantity);
}

/// <summary>Read-only observations supplied to the preview/verification layer.</summary>
public sealed record PurchaseObservation(
    MallListing Listing,
    KoLStatusSnapshot Status,
    KoLInventorySnapshot Inventory,
    DateTimeOffset ObservedAt)
{
    public PurchaseObservation(
        MallListing listing,
        KoLStatusSnapshot status,
        KoLInventorySnapshot inventory)
        : this(listing, status, inventory,
            new[] { listing.ObservedAt, status.FetchedAt, inventory.FetchedAt }.Max())
    {
    }

    public bool IsStale
        => Status.IsStale
           || Inventory.IsStale;

    public int InventoryQuantity(string itemId)
        => Inventory.Items
            .Where(item => string.Equals(item.ItemId, itemId, StringComparison.Ordinal))
            .Sum(item => Math.Max(0, item.Quantity));
}

public enum PurchaseAbortReason
{
    None = 0,
    InvalidRequest,
    MissingListing,
    ItemChanged,
    StoreChanged,
    StoreNameChanged,
    PriceChanged,
    QuantityChanged,
    SoldOut,
    PurchaseLimitExceeded,
    StaleListing,
    SnapshotTooOld = StaleListing,
    StaleStatus,
    StaleInventory,
    InsufficientMeat,
    MeatReserveViolation,
    PerPurchaseBudgetExceeded,
    DailyBudgetExceeded,
    QuantityLimitExceeded,
    BudgetExceeded = DailyBudgetExceeded,
    PreviewExpired,
    MissingApproval,
    DryRun,
    ActionChanged,
    SessionInvalid,
    LoginRedirect = SessionInvalid,
    CommitRejected,
    ExpectedStateChanged,
    StatusVerificationFailed,
    InventoryVerificationFailed,
    MeatVerificationFailed,
    ItemNotReceived,
    UnexpectedQuantity,
    VerificationFailed
}

public enum PurchaseVerificationStatus
{
    NotAttempted,
    Verified,
    Failed,
    Aborted
}

/// <summary>Review object returned by a pure preview operation.</summary>
public sealed record PurchasePreview
{
    public PurchasePreview()
    {
    }

    public PurchasePreview(
        string actionId,
        PurchaseRequest request,
        PurchaseExpectedState expected,
        PurchaseBudget budget,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        bool requiresApproval = true,
        bool dryRun = true,
        IReadOnlyList<PurchaseAbortReason>? abortReasons = null,
        IReadOnlyList<string>? warnings = null)
    {
        ActionId = actionId;
        Request = request;
        Expected = expected;
        Budget = budget;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        RequiresApproval = requiresApproval;
        DryRun = dryRun;
        AbortReasons = abortReasons ?? Array.Empty<PurchaseAbortReason>();
        Warnings = warnings ?? Array.Empty<string>();
    }

    public required string ActionId { get; init; }
    public required PurchaseRequest Request { get; init; }
    public required PurchaseExpectedState Expected { get; init; }
    public required PurchaseBudget Budget { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public long EstimatedMeat => Expected.TotalCost;
    public bool DryRun { get; init; } = true;
    public bool RequiresApproval { get; init; } = true;
    /// <summary>Never populated by Core; the host supplies a short-lived token at commit.</summary>
    public string? ApprovalToken { get; init; }
    public IReadOnlyList<PurchaseAbortReason> AbortReasons { get; init; } = Array.Empty<PurchaseAbortReason>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public bool IsReady => AbortReasons.Count == 0;
    public IReadOnlyList<PurchaseAbortReason> Reasons => AbortReasons;
}

/// <summary>
/// Fresh reads and the adapter's acknowledgement supplied to verification.
/// A network adapter may populate these records, but this Core project does not
/// perform a mutating HTTP request.
/// </summary>
public sealed record PurchaseCommitRequest
{
    public PurchaseCommitRequest()
    {
    }

    public PurchaseCommitRequest(
        PurchasePreview preview,
        string actionId,
        PurchaseObservation before,
        PurchaseObservation after,
        string? approvalToken = null,
        bool commitAcknowledged = false,
        bool sessionValid = true,
        DateTimeOffset? committedAt = null)
    {
        Preview = preview;
        ActionId = actionId;
        Before = before;
        After = after;
        ApprovalToken = approvalToken;
        CommitAcknowledged = commitAcknowledged;
        SessionValid = sessionValid;
        CommittedAt = committedAt ?? DateTimeOffset.UtcNow;
    }

    public required PurchasePreview Preview { get; init; }
    public required string ActionId { get; init; }
    public string? ApprovalToken { get; init; }
    public required PurchaseObservation Before { get; init; }
    public required PurchaseObservation After { get; init; }
    public bool CommitAcknowledged { get; init; }
    public bool SessionValid { get; init; } = true;
    public DateTimeOffset CommittedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record PurchaseCommitResult
{
    public required string ActionId { get; init; }
    public required PurchaseVerificationStatus Status { get; init; }
    public bool Committed { get; init; }
    public bool Succeeded => Status == PurchaseVerificationStatus.Verified;
    public bool Verified => Succeeded;
    public bool Aborted => Status == PurchaseVerificationStatus.Aborted;
    public IReadOnlyList<PurchaseAbortReason> AbortReasons { get; init; } = Array.Empty<PurchaseAbortReason>();
    public IReadOnlyList<string> Messages { get; init; } = Array.Empty<string>();
    public PurchaseObservation? Before { get; init; }
    public PurchaseObservation? After { get; init; }
    public long? ObservedMeatAfter => After?.Status.Meat;

    public PurchaseAbortReason? PrimaryAbortReason
        => AbortReasons.Count == 0 ? null : AbortReasons[0];
}

/// <summary>
/// Pure purchase preview and commit verification.  Implementations that know
/// how to submit KoL requests should call these methods around their own
/// adapter; no method here sends a request.
/// </summary>
public static class PurchaseApprovalService
{
    public static PurchasePreview Preview(
        PurchaseRequest request,
        PurchaseObservation observation,
        PurchaseBudget budget,
        PurchaseApprovalPolicy? policy = null,
        DateTimeOffset? now = null,
        string? actionId = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(budget);
        policy ??= new PurchaseApprovalPolicy();
        request.Validate();
        budget.Validate();
        policy.Validate();

        var at = now ?? DateTimeOffset.UtcNow;
        var reasons = new List<PurchaseAbortReason>();
        var warnings = new List<string>();
        var listing = observation.Listing;
        var staleListing = listing.ObservedAt < at - policy.MaxObservationAge;
        var staleStatus = observation.Status.IsStale || observation.Status.FetchedAt < at - policy.MaxObservationAge;
        var staleInventory = observation.Inventory.IsStale || observation.Inventory.FetchedAt < at - policy.MaxObservationAge;
        var expected = new PurchaseExpectedState(
            request.ItemId,
            request.StoreId,
            request.ExpectedStoreName,
            request.ExpectedUnitPrice,
            request.Quantity,
            Math.Max(0, listing.Quantity),
            observation.Status.Meat,
            observation.InventoryQuantity(request.ItemId),
            observation.ObservedAt,
            staleListing,
            staleStatus,
            staleInventory);

        CompareListing(request, listing, reasons);
        if (staleListing)
            Add(reasons, PurchaseAbortReason.StaleListing);
        if (staleStatus)
            Add(reasons, PurchaseAbortReason.StaleStatus);
        if (staleInventory)
            Add(reasons, PurchaseAbortReason.StaleInventory);

        var total = request.TotalCost;
        if (listing.Quantity < request.Quantity)
            Add(reasons, listing.Quantity <= 0 ? PurchaseAbortReason.SoldOut : PurchaseAbortReason.QuantityChanged);
        if (listing.PurchaseLimit is { } limit && limit < request.Quantity)
            Add(reasons, PurchaseAbortReason.PurchaseLimitExceeded);
        if (observation.Status.Meat < total || budget.AvailableMeat < total)
            Add(reasons, PurchaseAbortReason.InsufficientMeat);
        if (observation.Status.Meat - total < budget.MeatReserve
            || budget.AvailableMeat - total < budget.MeatReserve)
            Add(reasons, PurchaseAbortReason.MeatReserveViolation);
        if (total > budget.RemainingDaily)
            Add(reasons, PurchaseAbortReason.DailyBudgetExceeded);
        if (budget.MaxMeatPerPurchase is { } budgetMax && total > budgetMax)
            Add(reasons, PurchaseAbortReason.PerPurchaseBudgetExceeded);
        if (policy.MaxMeatPerPurchase is { } policyMax && total > policyMax)
            Add(reasons, PurchaseAbortReason.PerPurchaseBudgetExceeded);
        if (budget.MaxQuantityPerPurchase is { } budgetQty && request.Quantity > budgetQty)
            Add(reasons, PurchaseAbortReason.QuantityLimitExceeded);
        if (policy.MaxQuantityPerPurchase is { } policyQty && request.Quantity > policyQty)
            Add(reasons, PurchaseAbortReason.QuantityLimitExceeded);

        if (observation.Status.Meat != budget.AvailableMeat)
            warnings.Add("Budget.AvailableMeat differs from the fresh status snapshot; the snapshot is authoritative.");
        if (policy.DryRun)
            warnings.Add("Automation policy is dry-run; a commit adapter must refuse mutation.");

        return new PurchasePreview
        {
            ActionId = actionId ?? Guid.NewGuid().ToString("N"),
            Request = request,
            Expected = expected,
            Budget = budget,
            CreatedAt = at,
            ExpiresAt = at + policy.PreviewLifetime,
            DryRun = policy.DryRun,
            RequiresApproval = policy.RequireApproval,
            AbortReasons = reasons,
            Warnings = warnings
        };
    }

    public static PurchasePreview Preview(
        PurchaseRequest request,
        MallListing listing,
        KoLStatusSnapshot status,
        KoLInventorySnapshot inventory,
        PurchaseBudget budget,
        PurchaseApprovalPolicy? policy = null,
        DateTimeOffset? now = null,
        string? actionId = null)
        => Preview(request,
            new PurchaseObservation(listing, status, inventory),
            budget, policy, now, actionId);

    /// <summary>Alias matching the common gateway naming convention.</summary>
    public static PurchasePreview CreatePreview(
        PurchaseRequest request,
        PurchaseObservation observation,
        PurchaseBudget budget,
        PurchaseApprovalPolicy? policy = null,
        DateTimeOffset? now = null,
        string? actionId = null)
        => Preview(request, observation, budget, policy, now, actionId);

    public static PurchasePreview CreatePreview(
        PurchaseRequest request,
        MallListing listing,
        KoLStatusSnapshot status,
        KoLInventorySnapshot inventory,
        PurchaseBudget budget,
        PurchaseApprovalPolicy? policy = null,
        DateTimeOffset? now = null,
        string? actionId = null)
        => Preview(request, listing, status, inventory, budget, policy, now, actionId);

    public static PurchasePreview PreviewWithAutomationPolicy(
        PurchaseRequest request,
        PurchaseObservation observation,
        PurchaseBudget budget,
        AutomationPolicy policy,
        DateTimeOffset? now = null,
        string? actionId = null)
        => Preview(request, observation, budget,
            PurchaseApprovalPolicy.FromAutomationPolicy(policy), now, actionId);

    public static PurchaseCommitResult VerifyCommit(PurchaseCommitRequest commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var preview = commit.Preview ?? throw new ArgumentException("Preview is required.", nameof(commit));
        var reasons = new List<PurchaseAbortReason>();
        var messages = new List<string>();
        var now = commit.CommittedAt;

        if (!string.Equals(commit.ActionId, preview.ActionId, StringComparison.Ordinal))
            Add(reasons, PurchaseAbortReason.ActionChanged);
        if (now > preview.ExpiresAt)
            Add(reasons, PurchaseAbortReason.PreviewExpired);
        if (preview.RequiresApproval && string.IsNullOrWhiteSpace(commit.ApprovalToken))
            Add(reasons, PurchaseAbortReason.MissingApproval);
        if (preview.DryRun)
            Add(reasons, PurchaseAbortReason.DryRun);
        if (!commit.SessionValid)
            Add(reasons, PurchaseAbortReason.SessionInvalid);
        if (!commit.CommitAcknowledged)
            Add(reasons, PurchaseAbortReason.CommitRejected);
        if (preview.AbortReasons.Count > 0)
            foreach (var reason in preview.AbortReasons) Add(reasons, reason);
        if (commit.Before.Status.IsStale)
            Add(reasons, PurchaseAbortReason.StaleStatus);
        if (commit.Before.Inventory.IsStale)
            Add(reasons, PurchaseAbortReason.StaleInventory);
        if (commit.Before.Status.Meat != preview.Expected.MeatBefore
            || commit.Before.InventoryQuantity(preview.Request.ItemId) != preview.Expected.InventoryQuantityBefore)
            Add(reasons, PurchaseAbortReason.ExpectedStateChanged);

        CompareListing(preview.Request, commit.Before.Listing, reasons);
        if (commit.Before.Listing.Quantity < preview.Request.Quantity)
            Add(reasons, commit.Before.Listing.Quantity <= 0 ? PurchaseAbortReason.SoldOut : PurchaseAbortReason.QuantityChanged);

        var expectedMeat = commit.Before.Status.Meat - preview.EstimatedMeat;
        var actualMeat = commit.After.Status.Meat;
        if (commit.After.Status.IsStale)
            Add(reasons, PurchaseAbortReason.StaleStatus);
        if (commit.After.Inventory.IsStale)
            Add(reasons, PurchaseAbortReason.StaleInventory);
        if (actualMeat != expectedMeat)
        {
            Add(reasons, PurchaseAbortReason.MeatVerificationFailed);
            Add(reasons, PurchaseAbortReason.StatusVerificationFailed);
            messages.Add($"Expected {expectedMeat} Meat after purchase but observed {actualMeat}.");
        }
        var beforeQuantity = commit.Before.InventoryQuantity(preview.Request.ItemId);
        var afterQuantity = commit.After.InventoryQuantity(preview.Request.ItemId);
        var delta = afterQuantity - beforeQuantity;
        if (delta < preview.Request.Quantity)
        {
            Add(reasons, PurchaseAbortReason.ItemNotReceived);
            Add(reasons, PurchaseAbortReason.InventoryVerificationFailed);
            messages.Add($"Expected at least {preview.Request.Quantity} new item(s) but observed {delta}.");
        }
        else if (delta != preview.Request.Quantity)
        {
            Add(reasons, PurchaseAbortReason.UnexpectedQuantity);
            Add(reasons, PurchaseAbortReason.InventoryVerificationFailed);
            messages.Add($"Expected exactly {preview.Request.Quantity} new item(s) but observed {delta}.");
        }

        if (reasons.Count > 0)
        {
            if (reasons.Any(reason => reason is PurchaseAbortReason.MeatVerificationFailed
                or PurchaseAbortReason.ItemNotReceived
                or PurchaseAbortReason.UnexpectedQuantity
                or PurchaseAbortReason.StatusVerificationFailed
                or PurchaseAbortReason.InventoryVerificationFailed))
                Add(reasons, PurchaseAbortReason.VerificationFailed);
            return new PurchaseCommitResult
            {
                ActionId = preview.ActionId,
                Status = PurchaseVerificationStatus.Aborted,
                Committed = commit.CommitAcknowledged && !reasons.Contains(PurchaseAbortReason.DryRun),
                AbortReasons = reasons,
                Messages = messages,
                Before = commit.Before,
                After = commit.After
            };
        }

        return new PurchaseCommitResult
        {
            ActionId = preview.ActionId,
            Status = PurchaseVerificationStatus.Verified,
            Committed = true,
            Before = commit.Before,
            After = commit.After
        };
    }

    /// <summary>Alias matching verifier-oriented callers.</summary>
    public static PurchaseCommitResult Verify(PurchaseCommitRequest commit)
        => VerifyCommit(commit);

    /// <summary>
    /// Verifies the fresh status/inventory reads returned by an adapter after a
    /// purchase.  The adapter still owns the actual request and acknowledgement.
    /// </summary>
    public static PurchaseCommitResult VerifyPostBuy(
        PurchasePreview preview,
        PurchaseObservation before,
        PurchaseObservation after,
        string? approvalToken,
        bool commitAcknowledged = true,
        bool sessionValid = true,
        DateTimeOffset? committedAt = null)
        => VerifyCommit(new PurchaseCommitRequest
        {
            Preview = preview ?? throw new ArgumentNullException(nameof(preview)),
            ActionId = preview.ActionId,
            ApprovalToken = approvalToken,
            Before = before ?? throw new ArgumentNullException(nameof(before)),
            After = after ?? throw new ArgumentNullException(nameof(after)),
            CommitAcknowledged = commitAcknowledged,
            SessionValid = sessionValid,
            CommittedAt = committedAt ?? DateTimeOffset.UtcNow
        });

    private static void CompareListing(PurchaseRequest request, MallListing listing, ICollection<PurchaseAbortReason> reasons)
    {
        if (!string.Equals(request.ItemId, listing.ItemId, StringComparison.Ordinal))
            Add(reasons, PurchaseAbortReason.ItemChanged);
        if (!string.Equals(request.StoreId, listing.StoreId, StringComparison.Ordinal))
            Add(reasons, PurchaseAbortReason.StoreChanged);
        if (request.ExpectedStoreName is not null &&
            !string.Equals(request.ExpectedStoreName, listing.StoreName, StringComparison.Ordinal))
            Add(reasons, PurchaseAbortReason.StoreNameChanged);
        if (request.ExpectedUnitPrice != listing.Price)
            Add(reasons, PurchaseAbortReason.PriceChanged);
    }

    private static void Add(ICollection<PurchaseAbortReason> reasons, PurchaseAbortReason reason)
    {
        if (!reasons.Contains(reason)) reasons.Add(reason);
    }
}

/// <summary>Convenience verifier name for hosts that keep mutation logic separate.</summary>
public static class PurchaseCommitVerifier
{
    public static PurchaseCommitResult Verify(PurchaseCommitRequest commit)
        => PurchaseApprovalService.VerifyCommit(commit);

    public static PurchaseCommitResult VerifyPostBuy(
        PurchasePreview preview,
        PurchaseObservation before,
        PurchaseObservation after,
        string? approvalToken,
        bool commitAcknowledged = true,
        bool sessionValid = true,
        DateTimeOffset? committedAt = null)
        => PurchaseApprovalService.VerifyPostBuy(
            preview, before, after, approvalToken, commitAcknowledged, sessionValid, committedAt);
}

