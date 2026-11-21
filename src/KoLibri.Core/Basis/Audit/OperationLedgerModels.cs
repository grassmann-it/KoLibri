using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KoLibri.Core.Basis;

/// <summary>Lifecycle state retained for an operation in the approval ledger.</summary>
public enum OperationLedgerState
{
    Unknown = 0,
    Previewed,
    AwaitingApproval,
    Pending = AwaitingApproval,
    ApprovalRequired = AwaitingApproval,
    Approved,
    Committing,
    Committed,
    Verified,
    Rejected,
    Expired,
    Failed
}

/// <summary>
/// Result of trying to commit an operation.  <see cref="Commit"/> and
/// <see cref="Committed"/> intentionally have the same value for clients that
/// use either the verb or the lifecycle adjective.
/// </summary>
public enum OperationCommitState
{
    Unknown = 0,
    Commit = 1,
    Committed = Commit,
    Replay = 2,
    Rejected = 3,
    Conflict = 4,
    Expired = 5
}

/// <summary>Result of an approval request.</summary>
public enum OperationApprovalState
{
    Unknown = 0,
    Approved = 1,
    Pending = Approved,
    AlreadyApproved = 2,
    Rejected = 3,
    Expired = 4,
    Committed = 5
}

/// <summary>Entries emitted by the operation ledger audit stream.</summary>
public enum OperationAuditEventType
{
    PreviewRegistered = 0,
    PreviewReplay,
    ApprovalIssued,
    ApprovalReplay,
    ApprovalRejected,
    ApprovalExpired,
    CommitAccepted,
    CommitReplay,
    CommitRejected,
    CommitConflict,
    CommitUnknown,
    OperationExpired,
    OperationFailed
}

/// <summary>Options controlling preview and token lifetimes.</summary>
public sealed record OperationLedgerOptions
{
    public TimeSpan PreviewLifetime { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan ApprovalLifetime { get; init; } = TimeSpan.FromMinutes(5);
    public int MaxAuditEvents { get; init; } = 10_000;
    public int MaxPreviewBytes { get; init; } = 1_048_576;

    public void Validate()
    {
        if (PreviewLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(PreviewLifetime));
        if (ApprovalLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ApprovalLifetime));
        if (MaxAuditEvents <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxAuditEvents));
        if (MaxPreviewBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxPreviewBytes));
    }
}

/// <summary>Typed registration request. Registration never performs a mutation.</summary>
public sealed record OperationPreviewRequest<TPreview>
{
    public string? OperationId { get; init; }
    public string Action { get; init; } = string.Empty;
    public string Fingerprint { get; init; } = string.Empty;
    public string AccountId { get; init; } = string.Empty;
    public TPreview Preview { get; init; } = default!;
    public string? IdempotencyKey { get; init; }
    public string? Actor { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public bool RequiresApproval { get; init; } = true;

    public OperationPreviewRequest()
    {
    }

    public OperationPreviewRequest(
        string action,
        string fingerprint,
        string accountId,
        TPreview preview,
        string? idempotencyKey = null,
        string? operationId = null,
        DateTimeOffset? expiresAt = null,
        string? actor = null,
        DateTimeOffset? createdAt = null,
        bool requiresApproval = true)
    {
        Action = action;
        Fingerprint = fingerprint;
        AccountId = accountId;
        Preview = preview;
        IdempotencyKey = idempotencyKey;
        OperationId = operationId;
        ExpiresAt = expiresAt;
        Actor = actor;
        CreatedAt = createdAt;
        RequiresApproval = requiresApproval;
    }
}

/// <summary>Typed, immutable representation returned after preview registration.</summary>
public sealed record OperationLedgerPreview<TPreview>
{
    public string OperationId { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string Fingerprint { get; init; } = string.Empty;
    public string AccountId { get; init; } = string.Empty;
    public required TPreview Preview { get; init; }
    public string? IdempotencyKey { get; init; }
    public string? Actor { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public OperationLedgerState State { get; init; }
    public bool RequiresApproval { get; init; } = true;
    public bool IsExisting { get; init; }
    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    // Names used by preview clients that distinguish an action id from an
    // operation id or call the preview value a payload.
    [JsonIgnore]
    public string ActionId => Action;

    [JsonIgnore]
    public string ActionFingerprint => Fingerprint;

    [JsonIgnore]
    public string Account => AccountId;

    [JsonIgnore]
    public string PreviewId => OperationId;

    [JsonIgnore]
    public TPreview Payload => Preview;
}

/// <summary>Registration outcome with conflict/replay information retained.</summary>
public sealed record OperationPreviewRegistrationResult<TPreview>
{
    public required OperationLedgerPreview<TPreview> Preview { get; init; }
    public bool Registered { get; init; }
    public bool Replayed { get; init; }
    public bool Existing => Replayed;
    public bool Conflict { get; init; }
    public string? Error { get; init; }
    public bool Succeeded => !Conflict && string.IsNullOrWhiteSpace(Error);

    [JsonIgnore]
    public OperationLedgerPreview<TPreview> Value => Preview;
}

/// <summary>Approval request that must repeat all operation binding fields.</summary>
public sealed record OperationApprovalRequest
{
    public string OperationId { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string Fingerprint { get; init; } = string.Empty;
    public string AccountId { get; init; } = string.Empty;
    public string? Actor { get; init; }
    public DateTimeOffset? ApprovedAt { get; init; }

    public OperationApprovalRequest()
    {
    }

    public OperationApprovalRequest(
        string operationId,
        string action,
        string fingerprint,
        string accountId,
        string? actor = null,
        DateTimeOffset? approvedAt = null)
    {
        OperationId = operationId;
        Action = action;
        Fingerprint = fingerprint;
        AccountId = accountId;
        Actor = actor;
        ApprovedAt = approvedAt;
    }
}

/// <summary>Approval response. The raw token is returned only when first issued.</summary>
public sealed record OperationApprovalResult
{
    public required string OperationId { get; init; }
    public required OperationApprovalState State { get; init; }
    public string? ApprovalToken { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string? Message { get; init; }
    public string? Actor { get; init; }

    public bool Approved => State is OperationApprovalState.Approved or OperationApprovalState.AlreadyApproved;
    public bool IsApproved => Approved;
    public bool Replayed => State == OperationApprovalState.AlreadyApproved;
    public bool Succeeded => Approved;

    [JsonIgnore]
    public string? Token => ApprovalToken;
}

/// <summary>Commit request. All binding values are checked before a token is consumed.</summary>
public sealed record OperationCommitRequest
{
    public string OperationId { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string Fingerprint { get; init; } = string.Empty;
    public string AccountId { get; init; } = string.Empty;
    public string? ApprovalToken { get; init; }
    public string? IdempotencyKey { get; init; }
    /// <summary>A short non-secret receipt or result marker, never a raw response.</summary>
    public string? Receipt { get; init; }
    public DateTimeOffset? CommittedAt { get; init; }

    public OperationCommitRequest()
    {
    }

    public OperationCommitRequest(
        string operationId,
        string action,
        string fingerprint,
        string accountId,
        string? approvalToken = null,
        string? idempotencyKey = null,
        string? receipt = null,
        DateTimeOffset? committedAt = null)
    {
        OperationId = operationId;
        Action = action;
        Fingerprint = fingerprint;
        AccountId = accountId;
        ApprovalToken = approvalToken;
        IdempotencyKey = idempotencyKey;
        Receipt = receipt;
        CommittedAt = committedAt;
    }
}

/// <summary>Commit/replay/unknown decision returned by the ledger.</summary>
public sealed record OperationCommitResult
{
    public required string OperationId { get; init; }
    public required OperationCommitState State { get; init; }
    public OperationLedgerState OperationState { get; init; }
    public string? Receipt { get; init; }
    public string? Message { get; init; }
    public DateTimeOffset? OccurredAt { get; init; }

    public bool Committed => State is OperationCommitState.Commit or OperationCommitState.Committed;
    public bool Succeeded => Committed;
    public bool Replayed => State == OperationCommitState.Replay;
    public bool IsReplay => Replayed;
    public bool Unknown => State == OperationCommitState.Unknown;
    public bool IsUnknown => Unknown;
    public bool Rejected => State == OperationCommitState.Rejected;
    public bool Conflict => State == OperationCommitState.Conflict;

    [JsonIgnore]
    public OperationCommitState Status => State;

    [JsonIgnore]
    public OperationCommitState CommitState => State;
}

/// <summary>A safe, redacted audit row. Raw tokens and preview payloads never appear.</summary>
public sealed record OperationAuditEvent
{
    public string EventId { get; init; } = string.Empty;
    public string OperationId { get; init; } = string.Empty;
    public OperationAuditEventType Type { get; init; }
    public string Action { get; init; } = string.Empty;
    public string AccountId { get; init; } = string.Empty;
    public string FingerprintHash { get; init; } = string.Empty;
    public string? Actor { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public string? Status { get; init; }
    public string? Message { get; init; }
    public IReadOnlyDictionary<string, string?> Data { get; init; } =
        new ReadOnlyDictionary<string, string?>(new Dictionary<string, string?>(StringComparer.Ordinal));

    [JsonIgnore]
    public OperationAuditEventType EventType => Type;

    [JsonIgnore]
    public string ActionFingerprint => FingerprintHash;

    [JsonIgnore]
    public string Fingerprint => FingerprintHash;

    [JsonIgnore]
    public OperationAuditEventType Kind => Type;

    [JsonIgnore]
    public DateTimeOffset Timestamp => OccurredAt;
}

/// <summary>Durable operation record. Secret material is represented only by hashes.</summary>
public sealed record OperationLedgerEntry
{
    public string OperationId { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string Fingerprint { get; init; } = string.Empty;
    public string AccountId { get; init; } = string.Empty;
    public string? IdempotencyKeyHash { get; init; }
    public string? Actor { get; init; }
    public JsonElement Preview { get; init; }
    public string? PreviewType { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public bool RequiresApproval { get; init; } = true;
    public OperationLedgerState State { get; init; }
    public string? ApprovalTokenHash { get; init; }
    public DateTimeOffset? ApprovalTokenExpiresAt { get; init; }
    public bool ApprovalTokenConsumed { get; init; }
    public DateTimeOffset? ApprovedAt { get; init; }
    public DateTimeOffset? CommittedAt { get; init; }
    public string? Receipt { get; init; }
}

/// <summary>Versioned JSON document used by in-memory and durable stores.</summary>
public sealed record OperationLedgerSnapshot
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public IReadOnlyList<OperationLedgerEntry> Operations { get; init; } = Array.Empty<OperationLedgerEntry>();
    public IReadOnlyList<OperationAuditEvent> AuditEvents { get; init; } = Array.Empty<OperationAuditEvent>();

    public static OperationLedgerSnapshot Empty => new();
}

/// <summary>Exception thrown when an operation/idempotency binding conflicts.</summary>
public sealed class OperationLedgerConflictException : InvalidOperationException
{
    public OperationLedgerConflictException(string message)
        : base(message)
    {
    }
}

/// <summary>Exception thrown when a persisted ledger document is malformed.</summary>
public sealed class OperationLedgerStoreException : InvalidOperationException
{
    public OperationLedgerStoreException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

internal static class OperationLedgerText
{
    public static string Required(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required.", name);
        var normalized = value.Trim();
        if (normalized.Any(char.IsControl))
            throw new ArgumentException($"{name} cannot contain control characters.", name);
        return normalized;
    }

    public static string Account(string? value) => Required(value, nameof(value));

    public static string? Optional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        if (normalized.Any(char.IsControl))
            throw new ArgumentException("Text cannot contain control characters.", nameof(value));
        return normalized;
    }

    public static string AccountKey(string value) => Account(value).ToUpperInvariant();

    public static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string TokenHash(string token)
        => Hash(token);

    public static bool EqualHash(string left, string right)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));

    public static string Token()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string Redact(string? value, int maxLength = 256)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var cleaned = new string(value.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (cleaned.Length <= maxLength) return cleaned;
        return cleaned[..maxLength] + "…";
    }

    public static IReadOnlyDictionary<string, string?> RedactData(
        IReadOnlyDictionary<string, string?>? data)
    {
        if (data is null || data.Count == 0)
            return new ReadOnlyDictionary<string, string?>(new Dictionary<string, string?>(StringComparer.Ordinal));

        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var pair in data)
        {
            var key = Redact(pair.Key, 80);
            if (string.IsNullOrWhiteSpace(key)) continue;
            var sensitive = key.Contains("token", StringComparison.OrdinalIgnoreCase)
                || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
                || key.Contains("password", StringComparison.OrdinalIgnoreCase)
                || key.Contains("cookie", StringComparison.OrdinalIgnoreCase)
                || key.Contains("credential", StringComparison.OrdinalIgnoreCase)
                || key.Contains("authorization", StringComparison.OrdinalIgnoreCase);
            result[key] = sensitive ? "[REDACTED]" : Redact(pair.Value);
        }

        return new ReadOnlyDictionary<string, string?>(result);
    }
}

/// <summary>Stable helper for callers that need a deterministic preview fingerprint.</summary>
public static class OperationLedgerFingerprint
{
    public static string For<TPreview>(string action, string accountId, TPreview preview)
    {
        var normalizedAction = OperationLedgerText.Required(action, nameof(action));
        var normalizedAccount = OperationLedgerText.AccountKey(accountId);
        var json = JsonSerializer.Serialize(preview, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return OperationLedgerText.Hash($"{normalizedAction}\u001f{normalizedAccount}\u001f{json}");
    }

    public static string Compute<TPreview>(string action, string accountId, TPreview preview)
        => For(action, accountId, preview);
}
