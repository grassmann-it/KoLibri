using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using KolArbitrage.Core.Basis;
using KolArbitrage.Core.Spiel;

namespace KolArbitrage.Core.Markt;

public enum TransferAbortReason
{
    None = 0,
    InvalidRequest,
    MissingSourceAccount,
    MissingDestinationAccount,
    UnsupportedAccount,
    SameAccount,
    MissingReason,
    NoItems,
    InvalidItem,
    InvalidQuantity,
    InvalidMeat,
    MissingIdempotencyKey,
    SourceInsufficientInventory,
    InsufficientMeat,
    DestinationRejected,
    StaleSource,
    StaleDestination,
    StaleSnapshot,
    ReservationUnavailable,
    ReservationExpired,
    ReservationConsumed,
    ReservationConflict,
    IdempotencyConflict,
    DuplicateOperation,
    PreviewExpired,
    MissingApproval,
    DryRun,
    ActionChanged,
    IdempotencyKeyChanged,
    SessionInvalid,
    CommitRejected,
    ExpectedStateChanged,
    SourceStateChanged,
    DestinationStateChanged,
    SourceVerificationFailed,
    DestinationVerificationFailed,
    ItemNotTransferred,
    UnexpectedQuantity,
    MeatVerificationFailed,
    PartialFailure,
    ReconciliationRequired,
    VerificationFailed
}

public enum TransferReservationStatus
{
    Active,
    Consumed,
    Released,
    Expired
}

public enum TransferVerificationStatus
{
    NotAttempted,
    Verified,
    Partial,
    Failed,
    Aborted
}

public enum TransferOperationStatus
{
    Previewed,
    AwaitingApproval,
    Approved,
    Committed,
    PartiallyCommitted,
    Verified,
    ReconciliationRequired,
    Failed,
    Aborted,
    Replayed
}

public enum TransferReconciliationAction
{
    None,
    RetryMissingDestination,
    RestoreSourceDebit,
    InvestigateUnexpectedCredit,
    ManualReview
}

/// <summary>A single item quantity in a Main/Mallbot transfer.</summary>
public sealed record TransferLine
{
    public TransferLine()
    {
    }

    public TransferLine(string itemId, int quantity)
    {
        ItemId = itemId;
        Quantity = quantity;
    }

    public required string ItemId { get; init; }
    public int Quantity { get; init; }
    public int Count => Quantity;
}

/// <summary>
/// Requested movement between the two configured accounts. Account names are
/// retained as strings so profile ids can be used; Main and Mallbot aliases
/// are accepted and exposed through the role properties.
/// </summary>
public sealed record TransferRequest
{
    public TransferRequest()
    {
    }

    public TransferRequest(string sourceAccount, string destinationAccount,
        IReadOnlyDictionary<string, int> items, string reason,
        string? idempotencyKey = null, long meat = 0)
    {
        SourceAccount = sourceAccount;
        DestinationAccount = destinationAccount;
        Items = items;
        Reason = reason;
        IdempotencyKey = idempotencyKey;
        Meat = meat;
    }

    public TransferRequest(AccountRole sourceRole, AccountRole destinationRole,
        IReadOnlyDictionary<string, int> items, string reason,
        string? idempotencyKey = null, long meat = 0)
        : this(sourceRole.ToString(), destinationRole.ToString(), items, reason, idempotencyKey, meat)
    {
    }

    public required string SourceAccount { get; init; }
    public required string DestinationAccount { get; init; }
    public IReadOnlyDictionary<string, int> Items { get; init; } =
        new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(StringComparer.Ordinal));
    public string Reason { get; init; } = string.Empty;
    public string? IdempotencyKey { get; init; }
    public long Meat { get; init; }
    public string Source => SourceAccount;
    public string Destination => DestinationAccount;
    public long MeatQuantity => Meat;
    public AccountRole? SourceRole => ParseRole(SourceAccount);
    public AccountRole? DestinationRole => ParseRole(DestinationAccount);
    public bool IsMainMallbotPair => SourceRole is not null && DestinationRole is not null && SourceRole != DestinationRole;

    public void Validate(bool requireIdempotencyKey = true)
    {
        var errors = CollectValidationErrors(requireIdempotencyKey);
        if (errors.Count > 0)
            throw new ArgumentException(string.Join(" ", errors), nameof(TransferRequest));
    }

    public IReadOnlyList<string> CollectValidationErrors(bool requireIdempotencyKey = true)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(SourceAccount))
            errors.Add("Source account is required.");
        if (string.IsNullOrWhiteSpace(DestinationAccount))
            errors.Add("Destination account is required.");
        if (!string.IsNullOrWhiteSpace(SourceAccount) && !string.IsNullOrWhiteSpace(DestinationAccount)
            && string.Equals(SourceAccount.Trim(), DestinationAccount.Trim(), StringComparison.OrdinalIgnoreCase))
            errors.Add("Source and destination accounts must differ.");
        if (SourceRole is null || DestinationRole is null)
            errors.Add("Only Main and Mallbot accounts are supported by this workflow.");
        if (SourceRole is not null && DestinationRole is not null && SourceRole == DestinationRole)
            errors.Add("Source and destination accounts must differ.");
        if (string.IsNullOrWhiteSpace(Reason))
            errors.Add("A transfer reason is required for the audit log.");
        if (requireIdempotencyKey && string.IsNullOrWhiteSpace(IdempotencyKey))
            errors.Add("An idempotency key is required for a transfer.");
        if (!string.IsNullOrWhiteSpace(IdempotencyKey) && IdempotencyKey.Trim().Length > 200)
            errors.Add("The idempotency key must be 200 characters or fewer.");
        if (Meat < 0)
            errors.Add("Meat quantity cannot be negative.");
        if (Items is null || Items.Count == 0)
            errors.Add("At least one item quantity is required.");
        else
        {
            foreach (var (itemId, quantity) in Items)
            {
                if (string.IsNullOrWhiteSpace(itemId))
                    errors.Add("Item ids must not be empty.");
                if (quantity <= 0)
                    errors.Add($"Item {itemId} must have a positive quantity.");
            }
        }
        return errors;
    }

    /// <summary>Stable fingerprint used to reject idempotency-key reuse for another operation.</summary>
    public string Fingerprint()
    {
        var canonical = string.Join("|",
            SourceAccount.Trim().ToUpperInvariant(), DestinationAccount.Trim().ToUpperInvariant(),
            Meat.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.Join(",", (Items ?? new Dictionary<string, int>()).OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => $"{x.Key.Trim()}:{x.Value}")), Reason.Trim());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static AccountRole? ParseRole(string? value)
        => Enum.TryParse<AccountRole>(value?.Trim(), ignoreCase: true, out var role) ? role : null;
}

/// <summary>Fresh, read-only state supplied by both account adapters.</summary>
public sealed record TransferObservation
{
    public TransferObservation()
    {
    }

    public TransferObservation(KoLInventorySnapshot sourceInventory, KoLInventorySnapshot destinationInventory,
        KoLStatusSnapshot? sourceStatus = null, KoLStatusSnapshot? destinationStatus = null,
        DateTimeOffset? observedAt = null, string? sourceAccount = null, string? destinationAccount = null)
    {
        SourceInventory = sourceInventory;
        DestinationInventory = destinationInventory;
        SourceStatus = sourceStatus;
        DestinationStatus = destinationStatus;
        ObservedAt = observedAt ?? new[]
        {
            sourceInventory.FetchedAt, destinationInventory.FetchedAt,
            sourceStatus?.FetchedAt ?? DateTimeOffset.MinValue,
            destinationStatus?.FetchedAt ?? DateTimeOffset.MinValue
        }.Max();
        SourceAccount = sourceAccount;
        DestinationAccount = destinationAccount;
    }

    public required KoLInventorySnapshot SourceInventory { get; init; }
    public required KoLInventorySnapshot DestinationInventory { get; init; }
    public KoLStatusSnapshot? SourceStatus { get; init; }
    public KoLStatusSnapshot? DestinationStatus { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
    public string? SourceAccount { get; init; }
    public string? DestinationAccount { get; init; }
    public bool IsStale => SourceInventory.IsStale || DestinationInventory.IsStale
        || SourceStatus?.IsStale == true || DestinationStatus?.IsStale == true;

    public int SourceQuantity(string itemId) => Quantity(SourceInventory, itemId);
    public int DestinationQuantity(string itemId) => Quantity(DestinationInventory, itemId);
    public IReadOnlyDictionary<string, int> Quantities(IEnumerable<string> itemIds, bool source)
        => itemIds.Distinct(StringComparer.Ordinal).ToDictionary(itemId => itemId,
            itemId => source ? SourceQuantity(itemId) : DestinationQuantity(itemId), StringComparer.Ordinal);

    private static int Quantity(KoLInventorySnapshot snapshot, string itemId)
        => snapshot.Items.Where(item => string.Equals(item.ItemId, itemId, StringComparison.Ordinal))
            .Sum(item => Math.Max(0, item.Quantity));
}

/// <summary>Account/item quantities bound to a preview.</summary>
public sealed record TransferExpectedState
{
    public required string SourceAccount { get; init; }
    public required string DestinationAccount { get; init; }
    public IReadOnlyDictionary<string, int> SourceQuantities { get; init; } =
        new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(StringComparer.Ordinal));
    public IReadOnlyDictionary<string, int> DestinationQuantities { get; init; } =
        new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(StringComparer.Ordinal));
    public long? SourceMeat { get; init; }
    public long? DestinationMeat { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
    public bool SourceIsStale { get; init; }
    public bool DestinationIsStale { get; init; }
    public int SourceQuantity(string itemId) => SourceQuantities.TryGetValue(itemId, out var q) ? q : 0;
    public int DestinationQuantity(string itemId) => DestinationQuantities.TryGetValue(itemId, out var q) ? q : 0;
}

/// <summary>A quantity hold preventing two approved operations from spending the same stock.</summary>
public sealed record TransferReservation
{
    public TransferReservation()
    {
    }

    public TransferReservation(string reservationId, string idempotencyKey, TransferRequest request,
        DateTimeOffset createdAt, DateTimeOffset expiresAt, TransferReservationStatus status = TransferReservationStatus.Active)
    {
        ReservationId = reservationId;
        IdempotencyKey = idempotencyKey;
        Request = request;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        Status = status;
    }

    public required string ReservationId { get; init; }
    public required string IdempotencyKey { get; init; }
    public required TransferRequest Request { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public TransferReservationStatus Status { get; init; } = TransferReservationStatus.Active;
    public bool IsActive(DateTimeOffset now) => Status == TransferReservationStatus.Active && now < ExpiresAt;
    public bool IsExpired(DateTimeOffset now) => Status == TransferReservationStatus.Expired || now >= ExpiresAt;
    public IReadOnlyDictionary<string, int> Items => Request.Items;
    public long Meat => Request.Meat;
}

/// <summary>Bounds checked when building a transfer preview.</summary>
public sealed record TransferApprovalPolicy
{
    public bool DryRun { get; init; } = true;
    public bool RequireApproval { get; init; } = true;
    public bool RequireIdempotencyKey { get; init; } = true;
    public TimeSpan MaxObservationAge { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan PreviewLifetime { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan ReservationLifetime { get; init; } = TimeSpan.FromMinutes(5);
    public int? MaxQuantityPerItem { get; init; } = 100;
    public int? MaxItemLines { get; init; } = 50;
    public long? MaxMeat { get; init; }

    public void Validate()
    {
        if (MaxObservationAge <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(MaxObservationAge));
        if (PreviewLifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(PreviewLifetime));
        if (ReservationLifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ReservationLifetime));
        if (MaxQuantityPerItem is <= 0) throw new ArgumentOutOfRangeException(nameof(MaxQuantityPerItem));
        if (MaxItemLines is <= 0) throw new ArgumentOutOfRangeException(nameof(MaxItemLines));
        if (MaxMeat is < 0) throw new ArgumentOutOfRangeException(nameof(MaxMeat));
    }

    public static TransferApprovalPolicy FromAutomationPolicy(AutomationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        return new TransferApprovalPolicy
        {
            DryRun = policy.DryRun,
            RequireApproval = policy.RequireApprovalForTransfers
        };
    }
}

/// <summary>Review object returned before any transfer request can be submitted.</summary>
public sealed record TransferPreview
{
    public TransferPreview()
    {
    }

    public TransferPreview(string actionId, TransferRequest request, TransferExpectedState expected,
        TransferReservation reservation, DateTimeOffset createdAt, DateTimeOffset expiresAt,
        bool requiresApproval = true, bool dryRun = true,
        IReadOnlyList<TransferAbortReason>? abortReasons = null, IReadOnlyList<string>? warnings = null)
    {
        ActionId = actionId;
        Request = request;
        Expected = expected;
        Reservation = reservation;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        RequiresApproval = requiresApproval;
        DryRun = dryRun;
        AbortReasons = abortReasons ?? Array.Empty<TransferAbortReason>();
        Warnings = warnings ?? Array.Empty<string>();
    }

    public required string ActionId { get; init; }
    public required TransferRequest Request { get; init; }
    public required TransferExpectedState Expected { get; init; }
    public required TransferReservation Reservation { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public bool RequiresApproval { get; init; } = true;
    public bool DryRun { get; init; } = true;
    public string? ApprovalToken { get; init; }
    public IReadOnlyList<TransferAbortReason> AbortReasons { get; init; } = Array.Empty<TransferAbortReason>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public bool IsReady => AbortReasons.Count == 0;
    public bool RequiresReconciliation => AbortReasons.Contains(TransferAbortReason.ReconciliationRequired);
    public IReadOnlyList<TransferAbortReason> Reasons => AbortReasons;
}

/// <summary>Opaque, short-lived approval supplied by a human or host policy.</summary>
public sealed record TransferApproval
{
    public TransferApproval()
    {
    }

    public TransferApproval(string actionId, string approvalToken, string? approvedBy,
        DateTimeOffset approvedAt, DateTimeOffset expiresAt)
    {
        ActionId = actionId;
        ApprovalToken = approvalToken;
        ApprovedBy = approvedBy;
        ApprovedAt = approvedAt;
        ExpiresAt = expiresAt;
    }

    public required string ActionId { get; init; }
    public required string ApprovalToken { get; init; }
    public string? ApprovedBy { get; init; }
    public required DateTimeOffset ApprovedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public bool IsValid(DateTimeOffset now) => !string.IsNullOrWhiteSpace(ApprovalToken) && now < ExpiresAt;
}

public sealed record TransferApprovalRequest
{
    public required string ActionId { get; init; }
    public string? ApprovedBy { get; init; }
    public string? ApprovalToken { get; init; }
    public DateTimeOffset? ApprovedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>Per-item acknowledgement returned by a transport adapter.</summary>
public sealed record TransferItemCommit
{
    public TransferItemCommit()
    {
    }

    public TransferItemCommit(string itemId, int requestedQuantity, int transferredQuantity,
        bool acknowledged = true, string? error = null)
    {
        ItemId = itemId;
        RequestedQuantity = requestedQuantity;
        TransferredQuantity = transferredQuantity;
        Acknowledged = acknowledged;
        Error = error;
    }

    public required string ItemId { get; init; }
    public int RequestedQuantity { get; init; }
    public int TransferredQuantity { get; init; }
    public bool Acknowledged { get; init; } = true;
    public string? Error { get; init; }
    public bool IsPartial => TransferredQuantity > 0 && TransferredQuantity < RequestedQuantity;
}

/// <summary>Commit input containing fresh before/after reads supplied by an adapter.</summary>
public sealed record TransferCommitRequest
{
    public TransferCommitRequest()
    {
    }

    public TransferCommitRequest(TransferPreview preview, TransferObservation before, TransferObservation after,
        string? approvalToken = null, bool commitAcknowledged = false, bool sessionValid = true,
        DateTimeOffset? committedAt = null, IReadOnlyList<TransferItemCommit>? itemResults = null)
    {
        Preview = preview;
        ActionId = preview.ActionId;
        IdempotencyKey = preview.Request.IdempotencyKey;
        Before = before;
        After = after;
        ApprovalToken = approvalToken;
        CommitAcknowledged = commitAcknowledged;
        SessionValid = sessionValid;
        CommittedAt = committedAt ?? DateTimeOffset.UtcNow;
        ItemResults = itemResults ?? Array.Empty<TransferItemCommit>();
    }

    public required TransferPreview Preview { get; init; }
    public required string ActionId { get; init; }
    public string? IdempotencyKey { get; init; }
    public string? ApprovalToken { get; init; }
    public required TransferObservation Before { get; init; }
    public required TransferObservation After { get; init; }
    public bool CommitAcknowledged { get; init; }
    public bool SessionValid { get; init; } = true;
    public DateTimeOffset CommittedAt { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<TransferItemCommit> ItemResults { get; init; } = Array.Empty<TransferItemCommit>();
}

/// <summary>Per-line delta used to explain a successful, partial or failed transfer.</summary>
public sealed record TransferLineVerification
{
    public required string ItemId { get; init; }
    public int RequestedQuantity { get; init; }
    public int SourceDecrease { get; init; }
    public int DestinationIncrease { get; init; }
    public int ConfirmedQuantity { get; init; }
    public int MissingAtDestination { get; init; }
    public int SourceDebitWithoutDestination { get; init; }
    public int DestinationCreditWithoutSource { get; init; }
    public string? Error { get; init; }
    public bool IsVerified => RequestedQuantity > 0 && SourceDecrease == RequestedQuantity
        && DestinationIncrease == RequestedQuantity && ConfirmedQuantity == RequestedQuantity
        && MissingAtDestination == 0 && SourceDebitWithoutDestination == 0 && DestinationCreditWithoutSource == 0;
    public bool IsPartial => ConfirmedQuantity > 0 && !IsVerified;
}

/// <summary>Recovery details for a partial transfer.</summary>
public sealed record TransferReconciliation
{
    public IReadOnlyList<TransferLineVerification> Lines { get; init; } = Array.Empty<TransferLineVerification>();
    public TransferReconciliationAction RecommendedAction { get; init; }
    public bool IsRequired { get; init; }
    public int ConfirmedQuantity(string itemId)
        => Lines.FirstOrDefault(x => string.Equals(x.ItemId, itemId, StringComparison.Ordinal))?.ConfirmedQuantity ?? 0;
    public IReadOnlyDictionary<string, int> MissingQuantities
        => new ReadOnlyDictionary<string, int>(Lines.Where(x => x.MissingAtDestination > 0)
            .ToDictionary(x => x.ItemId, x => x.MissingAtDestination, StringComparer.Ordinal));
}

/// <summary>Result returned after a commit acknowledgement and fresh verification reads.</summary>
public sealed record TransferCommitResult
{
    public required string ActionId { get; init; }
    public required string? IdempotencyKey { get; init; }
    public required TransferVerificationStatus Status { get; init; }
    public TransferOperationStatus OperationStatus { get; init; }
    public bool Committed { get; init; }
    public bool Succeeded => Status == TransferVerificationStatus.Verified;
    public bool Verified => Succeeded;
    public bool PartiallyCommitted => Status == TransferVerificationStatus.Partial;
    public bool ReconciliationRequired { get; init; }
    public bool IdempotencyReplayed { get; init; }
    public IReadOnlyList<TransferAbortReason> AbortReasons { get; init; } = Array.Empty<TransferAbortReason>();
    public IReadOnlyList<string> Messages { get; init; } = Array.Empty<string>();
    public IReadOnlyList<TransferLineVerification> Lines { get; init; } = Array.Empty<TransferLineVerification>();
    public TransferReconciliation? Reconciliation { get; init; }
    public TransferObservation? Before { get; init; }
    public TransferObservation? After { get; init; }
    public TransferAbortReason? PrimaryAbortReason => AbortReasons.Count == 0 ? null : AbortReasons[0];
}

/// <summary>Input for a verification-only pass after a transport commit.</summary>
public sealed record TransferVerificationRequest
{
    public required TransferPreview Preview { get; init; }
    public required TransferObservation Before { get; init; }
    public required TransferObservation After { get; init; }
    public string? ApprovalToken { get; init; }
    public bool CommitAcknowledged { get; init; } = true;
    public bool SessionValid { get; init; } = true;
    public DateTimeOffset? VerifiedAt { get; init; }
    public IReadOnlyList<TransferItemCommit> ItemResults { get; init; } = Array.Empty<TransferItemCommit>();
}

/// <summary>Alias for hosts that call the verification response a result.</summary>
public sealed record TransferVerificationResult
{
    public required TransferCommitResult Commit { get; init; }
    public TransferVerificationStatus Status => Commit.Status;
    public bool Verified => Commit.Verified;
    public bool ReconciliationRequired => Commit.ReconciliationRequired;
    public TransferReconciliation? Reconciliation => Commit.Reconciliation;
    public IReadOnlyList<TransferLineVerification> Lines => Commit.Lines;
    public static TransferVerificationResult From(TransferCommitResult commit) => new() { Commit = commit };
}

/// <summary>Immutable operation record suitable for an audit/idempotency ledger.</summary>
public sealed record TransferOperationRecord
{
    public required string IdempotencyKey { get; init; }
    public required string Fingerprint { get; init; }
    public required string ActionId { get; init; }
    public TransferOperationStatus Status { get; init; }
    public required DateTimeOffset RecordedAt { get; init; }
    public TransferCommitResult? Result { get; init; }
}

public interface ITransferIdempotencyStore
{
    bool TryGet(string idempotencyKey, out TransferOperationRecord? record);
    bool TryBegin(string idempotencyKey, string fingerprint, string actionId, DateTimeOffset now, out TransferOperationRecord? existing);
    void Complete(string idempotencyKey, TransferCommitResult result, DateTimeOffset now);
}

/// <summary>Thread-safe in-memory idempotency ledger for tests and offline hosts.</summary>
public sealed class InMemoryTransferIdempotencyStore : ITransferIdempotencyStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TransferOperationRecord> _records = new(StringComparer.Ordinal);

    public bool TryGet(string idempotencyKey, out TransferOperationRecord? record)
    {
        lock (_gate)
        {
            if (_records.TryGetValue(idempotencyKey, out var found))
            {
                record = found;
                return true;
            }
        }
        record = null;
        return false;
    }

    public bool TryBegin(string idempotencyKey, string fingerprint, string actionId, DateTimeOffset now, out TransferOperationRecord? existing)
    {
        lock (_gate)
        {
            if (_records.TryGetValue(idempotencyKey, out var found))
            {
                existing = found;
                return false;
            }
            _records[idempotencyKey] = new TransferOperationRecord
            {
                IdempotencyKey = idempotencyKey, Fingerprint = fingerprint, ActionId = actionId,
                Status = TransferOperationStatus.AwaitingApproval, RecordedAt = now
            };
        }
        existing = null;
        return true;
    }

    public void Complete(string idempotencyKey, TransferCommitResult result, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            if (!_records.TryGetValue(idempotencyKey, out var previous))
                throw new InvalidOperationException("The idempotency key must be started before it is completed.");
            _records[idempotencyKey] = previous with
            {
                Status = result.IdempotencyReplayed ? TransferOperationStatus.Replayed : result.OperationStatus,
                RecordedAt = now, Result = result
            };
        }
    }
}

public interface ITransferReservationStore
{
    bool TryAdd(TransferReservation reservation);
    bool TryGet(string reservationId, out TransferReservation? reservation);
    bool TryConsume(string reservationId, DateTimeOffset now);
    bool TryRelease(string reservationId, DateTimeOffset now);
}

/// <summary>Thread-safe reservation store used by offline previews and tests.</summary>
public sealed class InMemoryTransferReservationStore : ITransferReservationStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TransferReservation> _reservations = new(StringComparer.Ordinal);

    public bool TryAdd(TransferReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        lock (_gate)
        {
            if (_reservations.ContainsKey(reservation.ReservationId)) return false;
            _reservations[reservation.ReservationId] = reservation;
            return true;
        }
    }

    public bool TryGet(string reservationId, out TransferReservation? reservation)
    {
        lock (_gate)
        {
            if (_reservations.TryGetValue(reservationId, out var found))
            {
                if (found.Status == TransferReservationStatus.Active && DateTimeOffset.UtcNow >= found.ExpiresAt)
                {
                    found = found with { Status = TransferReservationStatus.Expired };
                    _reservations[reservationId] = found;
                }
                reservation = found;
                return true;
            }
        }
        reservation = null;
        return false;
    }

    public bool TryConsume(string reservationId, DateTimeOffset now) => TryChangeStatus(reservationId, TransferReservationStatus.Consumed, now);
    public bool TryRelease(string reservationId, DateTimeOffset now) => TryChangeStatus(reservationId, TransferReservationStatus.Released, now);

    private bool TryChangeStatus(string reservationId, TransferReservationStatus status, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_reservations.TryGetValue(reservationId, out var found) || !found.IsActive(now)) return false;
            _reservations[reservationId] = found with { Status = status };
            return true;
        }
    }
}

/// <summary>Pure Main/Mallbot preview, approval and post-commit verification.</summary>
public static class TransferApprovalService
{
    public static TransferPreview Preview(TransferRequest request, TransferObservation observation,
        TransferApprovalPolicy? policy = null, DateTimeOffset? now = null, string? actionId = null,
        ITransferReservationStore? reservations = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(observation);
        policy ??= new TransferApprovalPolicy();
        policy.Validate();
        var at = now ?? DateTimeOffset.UtcNow;
        var reasons = new List<TransferAbortReason>();
        var warnings = new List<string>();
        var errors = request.CollectValidationErrors(policy.RequireIdempotencyKey);
        if (errors.Count > 0) Add(reasons, TransferAbortReason.InvalidRequest);
        if (errors.Any(x => x.Contains("Source account", StringComparison.OrdinalIgnoreCase))) Add(reasons, TransferAbortReason.MissingSourceAccount);
        if (errors.Any(x => x.Contains("Destination account", StringComparison.OrdinalIgnoreCase))) Add(reasons, TransferAbortReason.MissingDestinationAccount);
        if (errors.Any(x => x.Contains("must differ", StringComparison.OrdinalIgnoreCase))) Add(reasons, TransferAbortReason.SameAccount);
        if (errors.Any(x => x.Contains("Main and Mallbot", StringComparison.OrdinalIgnoreCase))) Add(reasons, TransferAbortReason.UnsupportedAccount);
        if (errors.Any(x => x.Contains("reason", StringComparison.OrdinalIgnoreCase))) Add(reasons, TransferAbortReason.MissingReason);
        if (errors.Any(x => x.Contains("idempotency", StringComparison.OrdinalIgnoreCase))) Add(reasons, TransferAbortReason.MissingIdempotencyKey);
        if (errors.Any(x => x.Contains("positive", StringComparison.OrdinalIgnoreCase))) Add(reasons, TransferAbortReason.InvalidQuantity);
        else if (errors.Any(x => x.Contains("item", StringComparison.OrdinalIgnoreCase))) Add(reasons, TransferAbortReason.NoItems);
        if (errors.Any(x => x.Contains("negative", StringComparison.OrdinalIgnoreCase))) Add(reasons, TransferAbortReason.InvalidMeat);
        if (policy.MaxItemLines is { } maxLines && request.Items is { Count: > 0 } && request.Items.Count > maxLines) Add(reasons, TransferAbortReason.InvalidRequest);
        if (policy.MaxQuantityPerItem is { } maxQuantity && request.Items is not null && request.Items.Values.Any(quantity => quantity > maxQuantity)) Add(reasons, TransferAbortReason.InvalidQuantity);
        if (policy.MaxMeat is { } maxMeat && request.Meat > maxMeat) Add(reasons, TransferAbortReason.InvalidMeat);

        var sourceQuantities = request.Items is null ? new Dictionary<string, int>(StringComparer.Ordinal)
            : observation.Quantities(request.Items.Keys, source: true).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        var destinationQuantities = request.Items is null ? new Dictionary<string, int>(StringComparer.Ordinal)
            : observation.Quantities(request.Items.Keys, source: false).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        var expected = new TransferExpectedState
        {
            SourceAccount = request.SourceAccount,
            DestinationAccount = request.DestinationAccount,
            SourceQuantities = new ReadOnlyDictionary<string, int>(sourceQuantities),
            DestinationQuantities = new ReadOnlyDictionary<string, int>(destinationQuantities),
            SourceMeat = observation.SourceStatus?.Meat,
            DestinationMeat = observation.DestinationStatus?.Meat,
            ObservedAt = observation.ObservedAt,
            SourceIsStale = observation.SourceInventory.IsStale || observation.SourceStatus?.IsStale == true,
            DestinationIsStale = observation.DestinationInventory.IsStale || observation.DestinationStatus?.IsStale == true
        };

        var staleSource = observation.SourceInventory.IsStale || observation.SourceStatus?.IsStale == true
            || observation.SourceInventory.FetchedAt < at - policy.MaxObservationAge
            || (observation.SourceStatus is not null && observation.SourceStatus.FetchedAt < at - policy.MaxObservationAge);
        var staleDestination = observation.DestinationInventory.IsStale || observation.DestinationStatus?.IsStale == true
            || observation.DestinationInventory.FetchedAt < at - policy.MaxObservationAge
            || (observation.DestinationStatus is not null && observation.DestinationStatus.FetchedAt < at - policy.MaxObservationAge);
        if (staleSource) Add(reasons, TransferAbortReason.StaleSource);
        if (staleDestination) Add(reasons, TransferAbortReason.StaleDestination);
        if (request.Items is not null)
        {
            foreach (var (itemId, quantity) in request.Items)
                if (quantity > 0 && !string.IsNullOrWhiteSpace(itemId) && observation.SourceQuantity(itemId) < quantity)
                    Add(reasons, TransferAbortReason.SourceInsufficientInventory);
        }
        if (request.Meat > 0 && observation.SourceStatus is { } sourceStatus && sourceStatus.Meat < request.Meat) Add(reasons, TransferAbortReason.InsufficientMeat);

        var id = actionId ?? Guid.NewGuid().ToString("N");
        var reservation = new TransferReservation
        {
            ReservationId = $"res-{id}", IdempotencyKey = request.IdempotencyKey ?? string.Empty,
            Request = request, CreatedAt = at, ExpiresAt = at + policy.ReservationLifetime,
            Status = TransferReservationStatus.Active
        };
        if (reservations is not null && !reservations.TryAdd(reservation)) Add(reasons, TransferAbortReason.ReservationConflict);
        if (policy.DryRun) warnings.Add("Automation policy is dry-run; a commit adapter must refuse mutation.");
        if (observation.SourceStatus is null || observation.DestinationStatus is null) warnings.Add("A status snapshot was not supplied for one account; only inventory deltas can be verified.");

        return new TransferPreview
        {
            ActionId = id, Request = request, Expected = expected, Reservation = reservation,
            CreatedAt = at, ExpiresAt = at + policy.PreviewLifetime, RequiresApproval = policy.RequireApproval,
            DryRun = policy.DryRun, AbortReasons = reasons, Warnings = warnings
        };
    }

    public static TransferPreview CreatePreview(TransferRequest request, TransferObservation observation,
        TransferApprovalPolicy? policy = null, DateTimeOffset? now = null, string? actionId = null,
        ITransferReservationStore? reservations = null)
        => Preview(request, observation, policy, now, actionId, reservations);

    public static TransferPreview PreviewWithAutomationPolicy(TransferRequest request, TransferObservation observation,
        AutomationPolicy policy, DateTimeOffset? now = null, string? actionId = null,
        ITransferReservationStore? reservations = null)
        => Preview(request, observation, TransferApprovalPolicy.FromAutomationPolicy(policy), now, actionId, reservations);

    /// <summary>Creates an opaque approval record; it never performs a transfer.</summary>
    public static TransferApproval Approve(TransferPreview preview, string? approvedBy = null,
        string? approvalToken = null, DateTimeOffset? now = null, TimeSpan? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var at = now ?? DateTimeOffset.UtcNow;
        return new TransferApproval
        {
            ActionId = preview.ActionId,
            ApprovalToken = string.IsNullOrWhiteSpace(approvalToken) ? Guid.NewGuid().ToString("N") : approvalToken,
            ApprovedBy = approvedBy, ApprovedAt = at,
            ExpiresAt = at + (lifetime ?? (preview.ExpiresAt - preview.CreatedAt))
        };
    }

    public static TransferApproval Approve(TransferApprovalRequest request, TransferPreview preview, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(preview);
        if (!string.Equals(request.ActionId, preview.ActionId, StringComparison.Ordinal)) throw new ArgumentException("Approval action id does not match the preview.", nameof(request));
        var at = request.ApprovedAt ?? now ?? DateTimeOffset.UtcNow;
        return Approve(preview, request.ApprovedBy, request.ApprovalToken, at, request.ExpiresAt is { } expires ? expires - at : null);
    }

    public static TransferCommitResult Commit(TransferCommitRequest commit, ITransferIdempotencyStore? idempotency = null,
        ITransferReservationStore? reservations = null)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(commit.Preview);
        var preview = commit.Preview;
        var key = commit.IdempotencyKey ?? string.Empty;
        if (idempotency is not null && !string.IsNullOrWhiteSpace(key))
        {
            if (!idempotency.TryBegin(key, preview.Request.Fingerprint(), preview.ActionId, commit.CommittedAt, out var existing))
            {
                if (existing is not null && !string.Equals(existing.Fingerprint, preview.Request.Fingerprint(), StringComparison.Ordinal))
                    return Failure(preview, commit, TransferVerificationStatus.Aborted, TransferOperationStatus.Aborted,
                        [TransferAbortReason.IdempotencyConflict], ["The idempotency key is already bound to a different transfer."]);
                if (existing?.Result is { } replay) return replay with { IdempotencyReplayed = true, OperationStatus = TransferOperationStatus.Replayed };
                return Failure(preview, commit, TransferVerificationStatus.Aborted, TransferOperationStatus.Aborted,
                    [TransferAbortReason.DuplicateOperation], ["A transfer with this idempotency key is already in progress."]);
            }
        }
        var result = VerifyCommit(commit, reservations);
        if (idempotency is not null && !string.IsNullOrWhiteSpace(key)) idempotency.Complete(key, result, commit.CommittedAt);
        return result;
    }

    public static TransferCommitResult VerifyCommit(TransferCommitRequest commit, ITransferReservationStore? reservations = null)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var preview = commit.Preview ?? throw new ArgumentException("Preview is required.", nameof(commit));
        var before = commit.Before ?? throw new ArgumentException("Before observation is required.", nameof(commit));
        var after = commit.After ?? throw new ArgumentException("After observation is required.", nameof(commit));
        var reasons = new List<TransferAbortReason>();
        var messages = new List<string>();
        var at = commit.CommittedAt;
        if (!string.Equals(commit.ActionId, preview.ActionId, StringComparison.Ordinal)) Add(reasons, TransferAbortReason.ActionChanged);
        if (!string.Equals(commit.IdempotencyKey, preview.Request.IdempotencyKey, StringComparison.Ordinal)) Add(reasons, TransferAbortReason.IdempotencyKeyChanged);
        if (at >= preview.ExpiresAt) Add(reasons, TransferAbortReason.PreviewExpired);
        if (preview.RequiresApproval && string.IsNullOrWhiteSpace(commit.ApprovalToken)) Add(reasons, TransferAbortReason.MissingApproval);
        if (preview.DryRun) Add(reasons, TransferAbortReason.DryRun);
        if (!commit.SessionValid) Add(reasons, TransferAbortReason.SessionInvalid);
        if (!commit.CommitAcknowledged) Add(reasons, TransferAbortReason.CommitRejected);
        foreach (var reason in preview.AbortReasons) Add(reasons, reason);

        if (reservations is not null)
        {
            if (!reservations.TryGet(preview.Reservation.ReservationId, out var reservation) || reservation is null) Add(reasons, TransferAbortReason.ReservationUnavailable);
            else if (!reservation.IsActive(at)) Add(reasons, reservation.Status == TransferReservationStatus.Consumed ? TransferAbortReason.ReservationConsumed : TransferAbortReason.ReservationExpired);
        }
        else if (!preview.Reservation.IsActive(at)) Add(reasons, TransferAbortReason.ReservationExpired);
        CompareExpected(preview, before, reasons);
        if (before.IsStale)
        {
            if (before.SourceInventory.IsStale || before.SourceStatus?.IsStale == true) Add(reasons, TransferAbortReason.StaleSource);
            if (before.DestinationInventory.IsStale || before.DestinationStatus?.IsStale == true) Add(reasons, TransferAbortReason.StaleDestination);
        }
        if (after.IsStale) Add(reasons, TransferAbortReason.StaleSnapshot);

        var lines = VerifyLines(preview.Request, before, after, commit.ItemResults, messages);
        var reconciliationRequired = lines.Any(line => !line.IsVerified);
        var reconciliation = reconciliationRequired ? BuildReconciliation(lines) : null;
        if (reconciliationRequired)
        {
            Add(reasons, TransferAbortReason.PartialFailure);
            Add(reasons, TransferAbortReason.ReconciliationRequired);
        }
        VerifyMeat(preview.Request, before, after, reasons, messages);
        if (reasons.Count > 0)
        {
            var partial = reconciliationRequired && commit.CommitAcknowledged
                && !reasons.Contains(TransferAbortReason.DryRun) && !reasons.Contains(TransferAbortReason.CommitRejected)
                && !reasons.Contains(TransferAbortReason.ExpectedStateChanged) && !reasons.Contains(TransferAbortReason.ActionChanged)
                && !reasons.Contains(TransferAbortReason.IdempotencyKeyChanged) && !reasons.Contains(TransferAbortReason.PreviewExpired)
                && !reasons.Contains(TransferAbortReason.MissingApproval);
            return new TransferCommitResult
            {
                ActionId = preview.ActionId, IdempotencyKey = preview.Request.IdempotencyKey,
                Status = partial ? TransferVerificationStatus.Partial : TransferVerificationStatus.Aborted,
                OperationStatus = partial ? TransferOperationStatus.ReconciliationRequired : TransferOperationStatus.Aborted,
                Committed = commit.CommitAcknowledged && !reasons.Contains(TransferAbortReason.DryRun),
                ReconciliationRequired = reconciliationRequired, AbortReasons = reasons, Messages = messages,
                Lines = lines, Reconciliation = reconciliation, Before = before, After = after
            };
        }
        if (reservations is not null) reservations.TryConsume(preview.Reservation.ReservationId, at);
        return new TransferCommitResult
        {
            ActionId = preview.ActionId, IdempotencyKey = preview.Request.IdempotencyKey,
            Status = TransferVerificationStatus.Verified, OperationStatus = TransferOperationStatus.Verified,
            Committed = true, Lines = lines, Before = before, After = after
        };
    }

    public static TransferCommitResult Verify(TransferCommitRequest commit, ITransferReservationStore? reservations = null) => VerifyCommit(commit, reservations);

    public static TransferVerificationResult Verify(TransferVerificationRequest request, ITransferReservationStore? reservations = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var commit = new TransferCommitRequest
        {
            Preview = request.Preview, ActionId = request.Preview.ActionId,
            IdempotencyKey = request.Preview.Request.IdempotencyKey, Before = request.Before, After = request.After,
            ApprovalToken = request.ApprovalToken, CommitAcknowledged = request.CommitAcknowledged,
            SessionValid = request.SessionValid, CommittedAt = request.VerifiedAt ?? DateTimeOffset.UtcNow,
            ItemResults = request.ItemResults
        };
        return TransferVerificationResult.From(VerifyCommit(commit, reservations));
    }

    public static TransferCommitResult VerifyPostTransfer(TransferPreview preview, TransferObservation before,
        TransferObservation after, string? approvalToken, bool commitAcknowledged = true, bool sessionValid = true,
        DateTimeOffset? committedAt = null, IReadOnlyList<TransferItemCommit>? itemResults = null,
        ITransferReservationStore? reservations = null)
        => VerifyCommit(new TransferCommitRequest
        {
            Preview = preview ?? throw new ArgumentNullException(nameof(preview)), ActionId = preview.ActionId,
            IdempotencyKey = preview.Request.IdempotencyKey, ApprovalToken = approvalToken,
            Before = before ?? throw new ArgumentNullException(nameof(before)), After = after ?? throw new ArgumentNullException(nameof(after)),
            CommitAcknowledged = commitAcknowledged, SessionValid = sessionValid,
            CommittedAt = committedAt ?? DateTimeOffset.UtcNow, ItemResults = itemResults ?? Array.Empty<TransferItemCommit>()
        }, reservations);

    private static void CompareExpected(TransferPreview preview, TransferObservation before, ICollection<TransferAbortReason> reasons)
    {
        if (preview.Expected.SourceAccount.Length > 0 && before.SourceAccount is not null && !string.Equals(preview.Expected.SourceAccount, before.SourceAccount, StringComparison.OrdinalIgnoreCase)) Add(reasons, TransferAbortReason.SourceStateChanged);
        if (preview.Expected.DestinationAccount.Length > 0 && before.DestinationAccount is not null && !string.Equals(preview.Expected.DestinationAccount, before.DestinationAccount, StringComparison.OrdinalIgnoreCase)) Add(reasons, TransferAbortReason.DestinationStateChanged);
        foreach (var (itemId, expectedQuantity) in preview.Expected.SourceQuantities) if (before.SourceQuantity(itemId) != expectedQuantity) Add(reasons, TransferAbortReason.ExpectedStateChanged);
        foreach (var (itemId, expectedQuantity) in preview.Expected.DestinationQuantities) if (before.DestinationQuantity(itemId) != expectedQuantity) Add(reasons, TransferAbortReason.ExpectedStateChanged);
        if (preview.Expected.SourceMeat is { } sourceMeat && before.SourceStatus?.Meat is { } currentSourceMeat && currentSourceMeat != sourceMeat) Add(reasons, TransferAbortReason.ExpectedStateChanged);
        if (preview.Expected.DestinationMeat is { } destinationMeat && before.DestinationStatus?.Meat is { } currentDestinationMeat && currentDestinationMeat != destinationMeat) Add(reasons, TransferAbortReason.ExpectedStateChanged);
    }

    private static IReadOnlyList<TransferLineVerification> VerifyLines(TransferRequest request, TransferObservation before,
        TransferObservation after, IReadOnlyList<TransferItemCommit> acknowledgements, ICollection<string> messages)
    {
        var lines = new List<TransferLineVerification>();
        foreach (var (itemId, requested) in request.Items ?? new Dictionary<string, int>())
        {
            var sourceDecrease = before.SourceQuantity(itemId) - after.SourceQuantity(itemId);
            var destinationIncrease = after.DestinationQuantity(itemId) - before.DestinationQuantity(itemId);
            var confirmed = Math.Min(requested, Math.Min(Math.Max(0, sourceDecrease), Math.Max(0, destinationIncrease)));
            var missing = Math.Max(0, requested - confirmed);
            var sourceDebitWithoutDestination = Math.Max(0, sourceDecrease - destinationIncrease);
            var destinationCreditWithoutSource = Math.Max(0, destinationIncrease - sourceDecrease);
            var acknowledgement = acknowledgements.FirstOrDefault(x => string.Equals(x.ItemId, itemId, StringComparison.Ordinal));
            var error = acknowledgement?.Error;
            if (acknowledgement is { Acknowledged: false }) error ??= "The transport did not acknowledge this item line.";
            var line = new TransferLineVerification
            {
                ItemId = itemId, RequestedQuantity = requested, SourceDecrease = sourceDecrease,
                DestinationIncrease = destinationIncrease, ConfirmedQuantity = confirmed, MissingAtDestination = missing,
                SourceDebitWithoutDestination = sourceDebitWithoutDestination,
                DestinationCreditWithoutSource = destinationCreditWithoutSource, Error = error
            };
            lines.Add(line);
            if (!line.IsVerified) messages.Add($"Item {itemId}: expected {requested}, source decrease {sourceDecrease}, destination increase {destinationIncrease}.");
        }
        return lines;
    }

    private static TransferReconciliation BuildReconciliation(IReadOnlyList<TransferLineVerification> lines)
    {
        var action = lines.Any(x => x.SourceDebitWithoutDestination > 0) ? TransferReconciliationAction.RestoreSourceDebit
            : lines.Any(x => x.DestinationCreditWithoutSource > 0) ? TransferReconciliationAction.InvestigateUnexpectedCredit
            : TransferReconciliationAction.RetryMissingDestination;
        return new TransferReconciliation { Lines = lines, RecommendedAction = action, IsRequired = true };
    }

    private static void VerifyMeat(TransferRequest request, TransferObservation before, TransferObservation after,
        ICollection<TransferAbortReason> reasons, ICollection<string> messages)
    {
        if (request.Meat <= 0) return;
        if (before.SourceStatus is null || after.SourceStatus is null || before.DestinationStatus is null || after.DestinationStatus is null)
        {
            Add(reasons, TransferAbortReason.MeatVerificationFailed);
            messages.Add("Meat transfer requires fresh status snapshots for both accounts.");
            return;
        }
        var sourceDelta = before.SourceStatus.Meat - after.SourceStatus.Meat;
        var destinationDelta = after.DestinationStatus.Meat - before.DestinationStatus.Meat;
        if (sourceDelta != request.Meat || destinationDelta != request.Meat)
        {
            Add(reasons, TransferAbortReason.MeatVerificationFailed);
            messages.Add($"Expected {request.Meat} Meat on both sides, observed source decrease {sourceDelta} and destination increase {destinationDelta}.");
        }
    }

    private static TransferCommitResult Failure(TransferPreview preview, TransferCommitRequest commit,
        TransferVerificationStatus status, TransferOperationStatus operationStatus,
        IReadOnlyList<TransferAbortReason> reasons, IReadOnlyList<string> messages)
        => new()
        {
            ActionId = preview.ActionId, IdempotencyKey = preview.Request.IdempotencyKey,
            Status = status, OperationStatus = operationStatus, Committed = false,
            AbortReasons = reasons, Messages = messages, Before = commit.Before, After = commit.After
        };

    private static void Add(ICollection<TransferAbortReason> reasons, TransferAbortReason reason)
    {
        if (!reasons.Contains(reason)) reasons.Add(reason);
    }
}

public static class TransferCommitVerifier
{
    public static TransferCommitResult Verify(TransferCommitRequest commit, ITransferReservationStore? reservations = null) => TransferApprovalService.VerifyCommit(commit, reservations);

    public static TransferCommitResult VerifyPostTransfer(TransferPreview preview, TransferObservation before,
        TransferObservation after, string? approvalToken, bool commitAcknowledged = true, bool sessionValid = true,
        DateTimeOffset? committedAt = null, IReadOnlyList<TransferItemCommit>? itemResults = null,
        ITransferReservationStore? reservations = null)
        => TransferApprovalService.VerifyPostTransfer(preview, before, after, approvalToken, commitAcknowledged, sessionValid, committedAt, itemResults, reservations);
}
