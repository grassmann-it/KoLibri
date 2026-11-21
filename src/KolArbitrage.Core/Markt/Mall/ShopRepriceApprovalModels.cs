using System.Security.Cryptography;
using System.Text;

namespace KolArbitrage.Core.Markt;

/// <summary>Reasons a shop reprice preview or commit is refused.</summary>
public enum ShopRepriceAbortReason
{
    None = 0,
    InvalidRequest,
    MissingAccount,
    MissingUpdates,
    MissingIdempotencyKey,
    IdempotencyConflict,
    DuplicateOperation,
    PreviewNotRegistered,
    PreviewExpired,
    MissingApproval,
    ApprovalActionChanged,
    ApprovalExpired,
    ActionChanged,
    IdempotencyKeyChanged,
    AccountChanged,
    DryRun,
    MissingListing,
    Conflict,
    FreshReadFailed,
    CommitRejected,
    TransportFailure,
    VerificationFailed,
    PartialFailure
}

/// <summary>Lifecycle state of an approval-bound shop reprice operation.</summary>
public enum ShopRepriceOperationStatus
{
    Unknown,
    Previewed,
    AwaitingApproval,
    Approved,
    Committed,
    Verified,
    Replayed,
    Conflict,
    Rejected,
    Failed,
    Aborted
}

/// <summary>Post-commit verification state.</summary>
public enum ShopRepriceVerificationStatus
{
    NotAttempted,
    Verified,
    Failed,
    Aborted
}

/// <summary>
/// Request for one expected-price-bound own-shop reprice batch.  The account
/// id is carried through the complete lifecycle so a preview made for one
/// character cannot be submitted on another character's lane.
/// </summary>
public sealed record ShopRepriceRequest
{
    public ShopRepriceRequest()
    {
    }

    public ShopRepriceRequest(
        IEnumerable<ShopPriceUpdate> updates,
        string accountId = "mallbot",
        string? reason = null,
        string? idempotencyKey = null)
    {
        Updates = updates?.ToArray() ?? throw new ArgumentNullException(nameof(updates));
        AccountId = accountId;
        Reason = reason;
        IdempotencyKey = idempotencyKey;
    }

    public ShopRepriceRequest(
        string accountId,
        IEnumerable<ShopPriceUpdate> updates,
        string? reason = null,
        string? idempotencyKey = null)
        : this(updates, accountId, reason, idempotencyKey)
    {
    }

    public string AccountId { get; init; } = "mallbot";
    public IReadOnlyList<ShopPriceUpdate> Updates { get; init; } = Array.Empty<ShopPriceUpdate>();
    public IReadOnlyList<ShopPriceUpdate> Changes => Updates;
    public IReadOnlyList<ShopPriceUpdate> PriceUpdates => Updates;
    public IReadOnlyList<ShopPriceUpdate> Items => Updates;
    public string? Reason { get; init; }
    public string? IdempotencyKey { get; init; }

    public void Validate(bool requireIdempotencyKey = true, int? maxUpdates = null)
    {
        if (string.IsNullOrWhiteSpace(AccountId))
            throw new ArgumentException("Account id is required.", nameof(AccountId));
        if (Updates is null || Updates.Count == 0)
            throw new ArgumentException("At least one shop price update is required.", nameof(Updates));
        if (maxUpdates is { } max && Updates.Count > max)
            throw new ArgumentException($"At most {max} shop price updates may be submitted.", nameof(Updates));
        foreach (var update in Updates)
            update.Validate();
        if (Updates.GroupBy(x => x.ItemId, StringComparer.Ordinal).Any(x => x.Count() > 1))
            throw new ArgumentException("Each item may appear only once in a reprice batch.", nameof(Updates));
        if (requireIdempotencyKey && string.IsNullOrWhiteSpace(IdempotencyKey))
            throw new ArgumentException("An idempotency key is required for a shop reprice.", nameof(IdempotencyKey));
        if (!string.IsNullOrWhiteSpace(IdempotencyKey) && IdempotencyKey.Trim().Length > 200)
            throw new ArgumentException("The idempotency key must be 200 characters or fewer.", nameof(IdempotencyKey));
    }

    /// <summary>Stable request fingerprint used by the idempotency ledger.</summary>
    public string Fingerprint()
    {
        var canonical = string.Join("|",
            AccountId.Trim().ToUpperInvariant(),
            string.Join(",", (Updates ?? Array.Empty<ShopPriceUpdate>())
                .OrderBy(x => x.ItemId, StringComparer.Ordinal)
                .Select(x => string.Join(":", x.ItemId,
                    x.ExpectedCurrentPrice.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    x.NewPrice.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    x.ExpectedPurchaseLimit?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ""))),
            (Reason ?? string.Empty).Trim());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

/// <summary>Bounds enforced before a shop mutation can be sent.</summary>
public sealed record ShopRepricePolicy
{
    /// <summary>Keep the live adapter disabled until a host opts in.</summary>
    public bool DryRun { get; init; } = true;
    public bool RequireApproval { get; init; } = true;
    public bool RequireIdempotencyKey { get; init; } = true;
    public TimeSpan PreviewLifetime { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan MaxObservationAge { get; init; } = TimeSpan.FromMinutes(2);
    public int? MaxUpdates { get; init; } = 100;

    public void Validate()
    {
        if (PreviewLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(PreviewLifetime));
        if (MaxObservationAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(MaxObservationAge));
        if (MaxUpdates is <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxUpdates));
    }
}

/// <summary>Review object registered by the reprice service before approval.</summary>
public sealed record ShopRepricePreview
{
    public string ActionId { get; init; } = string.Empty;
    public string PreviewId => ActionId;
    public ShopRepriceRequest Request { get; init; } = new();
    public string AccountId => Request.AccountId;
    public ShopMutationResult Observation { get; init; } = new(
        false, false, DateTimeOffset.MinValue, Array.Empty<ShopPriceUpdateResult>());
    public ShopMutationResult Expected => Observation;
    public ShopMutationResult Result => Observation;
    public IReadOnlyList<ShopPriceUpdateResult> Results => Observation.Results;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public bool RequiresApproval { get; init; } = true;
    public bool DryRun { get; init; } = true;
    public IReadOnlyList<ShopRepriceAbortReason> AbortReasons { get; init; }
        = Array.Empty<ShopRepriceAbortReason>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public bool IsReady => AbortReasons.Count == 0
        && Results.All(x => x.Status is ShopPriceUpdateStatus.Ready or ShopPriceUpdateStatus.Hold);
    public IReadOnlyList<ShopRepriceAbortReason> Reasons => AbortReasons;
    public ShopRepriceAbortReason? PrimaryAbortReason
        => AbortReasons.Count == 0 ? null : AbortReasons[0];
    public ShopRepriceOperationStatus State
        => !IsReady
            ? AbortReasons.Contains(ShopRepriceAbortReason.Conflict)
                ? ShopRepriceOperationStatus.Conflict
                : ShopRepriceOperationStatus.Aborted
            : RequiresApproval
                ? ShopRepriceOperationStatus.AwaitingApproval
                : ShopRepriceOperationStatus.Previewed;
    public ShopRepriceOperationStatus OperationStatus => State;

    public ShopRepricePreview()
    {
    }

    public ShopRepricePreview(
        string actionId,
        ShopRepriceRequest request,
        ShopMutationResult observation,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        bool requiresApproval = true,
        bool dryRun = true,
        IReadOnlyList<ShopRepriceAbortReason>? abortReasons = null,
        IReadOnlyList<string>? warnings = null)
    {
        ActionId = actionId;
        Request = request;
        Observation = observation;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        RequiresApproval = requiresApproval;
        DryRun = dryRun;
        AbortReasons = abortReasons ?? Array.Empty<ShopRepriceAbortReason>();
        Warnings = warnings ?? Array.Empty<string>();
    }
}

/// <summary>Opaque approval bound to exactly one registered action id.</summary>
public sealed record ShopRepriceApproval
{
    public string ApprovalId { get; init; } = string.Empty;
    public string ActionId { get; init; } = string.Empty;
    public string ApprovalToken { get; init; } = string.Empty;
    public string Token => ApprovalToken;
    public string? ApprovedBy { get; init; }
    public DateTimeOffset ApprovedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public bool IsValid(DateTimeOffset now)
        => !string.IsNullOrWhiteSpace(ApprovalToken) && now < ExpiresAt;
}

public sealed record ShopRepriceApprovalRequest
{
    public string ActionId { get; init; } = string.Empty;
    public string? ApprovedBy { get; init; }
    public string? ApprovalToken { get; init; }
    public DateTimeOffset? ApprovedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>Result of a fresh, read-only check against the registered preview.</summary>
public sealed record ShopRepriceRefreshResult
{
    public ShopRepricePreview Preview { get; init; } = new();
    public ShopMutationResult Fresh { get; init; } = new(
        false, false, DateTimeOffset.MinValue, Array.Empty<ShopPriceUpdateResult>());
    public ShopMutationResult Observation => Fresh;
    public IReadOnlyList<ShopRepriceAbortReason> AbortReasons { get; init; }
        = Array.Empty<ShopRepriceAbortReason>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public bool IsReady => AbortReasons.Count == 0
        && Fresh.Results.All(x => x.Status is ShopPriceUpdateStatus.Ready or ShopPriceUpdateStatus.Hold);
    public bool Ready => IsReady;
    public ShopRepriceOperationStatus State
        => IsReady ? ShopRepriceOperationStatus.Previewed : ShopRepriceOperationStatus.Conflict;
    public ShopRepriceAbortReason? PrimaryAbortReason
        => AbortReasons.Count == 0 ? null : AbortReasons[0];
}

/// <summary>Explicit commit input carrying the approval and action identity.</summary>
public sealed record ShopRepriceCommitRequest
{
    public string PreviewId { get; init; } = string.Empty;
    public string ActionId => PreviewId;
    public string ApprovalId { get; init; } = string.Empty;
    public string ApprovalToken { get; init; } = string.Empty;
    public string? IdempotencyKey { get; init; }

    public ShopRepriceCommitRequest()
    {
    }

    public ShopRepriceCommitRequest(
        string previewId,
        string approvalId,
        string approvalToken,
        string? idempotencyKey = null)
    {
        PreviewId = previewId;
        ApprovalId = approvalId;
        ApprovalToken = approvalToken;
        IdempotencyKey = idempotencyKey;
    }
}

/// <summary>Final commit and verification result.</summary>
public sealed record ShopRepriceCommitResult
{
    public string ActionId { get; init; } = string.Empty;
    public string PreviewId => ActionId;
    public ShopRepricePreview? Preview { get; init; }
    public string? AccountId { get; init; }
    public string? IdempotencyKey { get; init; }
    public ShopRepriceOperationStatus Status { get; init; }
    public ShopRepriceOperationStatus OperationStatus => Status;
    public ShopRepriceVerificationStatus VerificationStatus { get; init; }
        = ShopRepriceVerificationStatus.NotAttempted;
    public ShopRepriceVerificationStatus Verification => VerificationStatus;
    public bool Committed { get; init; }
    public bool Accepted => Committed;
    public bool Verified => VerificationStatus == ShopRepriceVerificationStatus.Verified;
    public bool Succeeded => Verified;
    public bool IdempotencyReplayed { get; init; }
    public IReadOnlyList<ShopPriceUpdateResult> Results { get; init; }
        = Array.Empty<ShopPriceUpdateResult>();
    public ShopMutationResult? Mutation { get; init; }
    public ShopMutationResult? RawResult => Mutation;
    public IReadOnlyList<ShopRepriceAbortReason> AbortReasons { get; init; }
        = Array.Empty<ShopRepriceAbortReason>();
    public IReadOnlyList<string> Messages { get; init; } = Array.Empty<string>();
    public DateTimeOffset ObservedAt { get; init; }
    public ShopRepriceAbortReason? PrimaryAbortReason
        => AbortReasons.Count == 0 ? null : AbortReasons[0];
}

/// <summary>Result returned by an explicit verification pass.</summary>
public sealed record ShopRepriceVerificationResult
{
    public ShopRepriceVerificationStatus Status { get; init; }
    public bool Verified => Status == ShopRepriceVerificationStatus.Verified;
    public bool Succeeded => Verified;
    public IReadOnlyList<ShopRepriceAbortReason> AbortReasons { get; init; }
        = Array.Empty<ShopRepriceAbortReason>();
    public IReadOnlyList<string> Messages { get; init; } = Array.Empty<string>();
    public ShopMutationResult? Observation { get; init; }
    public ShopRepriceAbortReason? PrimaryAbortReason
        => AbortReasons.Count == 0 ? null : AbortReasons[0];
}

/// <summary>Immutable idempotency record for one reprice operation.</summary>
public sealed record ShopRepriceOperationRecord
{
    public required string IdempotencyKey { get; init; }
    public required string Fingerprint { get; init; }
    public required string ActionId { get; init; }
    public ShopRepriceOperationStatus Status { get; init; }
    public required DateTimeOffset RecordedAt { get; init; }
    public ShopRepriceCommitResult? Result { get; init; }
}

public interface IShopRepriceIdempotencyStore
{
    bool TryGet(string idempotencyKey, out ShopRepriceOperationRecord? record);
    bool TryBegin(string idempotencyKey, string fingerprint, string actionId,
        DateTimeOffset now, out ShopRepriceOperationRecord? existing);
    void Complete(string idempotencyKey, ShopRepriceCommitResult result, DateTimeOffset now);
}

/// <summary>Thread-safe in-memory idempotency store for CLI and fixture hosts.</summary>
public sealed class InMemoryShopRepriceIdempotencyStore : IShopRepriceIdempotencyStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ShopRepriceOperationRecord> _records =
        new(StringComparer.Ordinal);

    public bool TryGet(string idempotencyKey, out ShopRepriceOperationRecord? record)
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

    public bool TryBegin(string idempotencyKey, string fingerprint, string actionId,
        DateTimeOffset now, out ShopRepriceOperationRecord? existing)
    {
        lock (_gate)
        {
            if (_records.TryGetValue(idempotencyKey, out existing)) return false;
            _records[idempotencyKey] = new ShopRepriceOperationRecord
            {
                IdempotencyKey = idempotencyKey,
                Fingerprint = fingerprint,
                ActionId = actionId,
                Status = ShopRepriceOperationStatus.Approved,
                RecordedAt = now
            };
            return true;
        }
    }

    public void Complete(string idempotencyKey, ShopRepriceCommitResult result, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            if (!_records.TryGetValue(idempotencyKey, out var previous))
                throw new InvalidOperationException("The idempotency key must be started before it is completed.");
            _records[idempotencyKey] = previous with
            {
                Status = result.IdempotencyReplayed
                    ? ShopRepriceOperationStatus.Replayed
                    : result.Status,
                RecordedAt = now,
                Result = result
            };
        }
    }
}

