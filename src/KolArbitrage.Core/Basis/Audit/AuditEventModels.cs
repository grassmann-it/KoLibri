using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace KolArbitrage.Core.Basis;

/// <summary>
/// The three stages shared by read-only planning and approval-gated changes.
/// A journal never infers a later stage from an earlier one; callers append an
/// explicit event for each stage they perform.
/// </summary>
public enum AuditEventPhase
{
    Preview,
    Commit,
    Verify
}

/// <summary>Compatibility name for callers that refer to the phase as a lifecycle.</summary>
public enum AuditLifecyclePhase
{
    Preview = AuditEventPhase.Preview,
    Commit = AuditEventPhase.Commit,
    Verify = AuditEventPhase.Verify
}

/// <summary>Compatibility name for callers that refer to an audit row as an event type.</summary>
public enum AuditEventType
{
    Preview = AuditEventPhase.Preview,
    Commit = AuditEventPhase.Commit,
    Verify = AuditEventPhase.Verify
}

/// <summary>Compatibility name for hosts that call the phase a kind.</summary>
public enum AuditEventKind
{
    Preview = AuditEventPhase.Preview,
    Commit = AuditEventPhase.Commit,
    Verify = AuditEventPhase.Verify
}

/// <summary>
/// An immutable, credential-free audit row.  IDs are deliberately strings:
/// hosts can use UUIDs, request IDs from an HTTP adapter, or stable local
/// identifiers without the journal having to know their format.
/// </summary>
public sealed record AuditEvent
{
    /// <summary>
    /// Constructs an event with its complete identity.  The JSON constructor
    /// is public so persisted JSONL rows can be read without a mutable DTO.
    /// </summary>
    [JsonConstructor]
    public AuditEvent(
        string eventId,
        DateTimeOffset occurredAt,
        AuditEventPhase phase,
        string operationId,
        string sourceId,
        string requestId,
        string action,
        string? status = null,
        string? message = null,
        IReadOnlyDictionary<string, string?>? data = null,
        string? accountId = null,
        bool isStale = false)
    {
        EventId = RequireId(eventId, nameof(eventId));
        OccurredAt = occurredAt;
        if (!Enum.IsDefined(phase))
            throw new ArgumentOutOfRangeException(nameof(phase));
        Phase = phase;
        OperationId = RequireId(operationId, nameof(operationId));
        SourceId = RequireId(sourceId, nameof(sourceId));
        RequestId = RequireId(requestId, nameof(requestId));
        Action = RequireText(action, nameof(action));
        Status = NormalizeOptional(status);
        Message = NormalizeOptional(message);
        Data = Freeze(data);
        AccountId = NormalizeOptional(accountId);
        IsStale = isStale;
    }

    /// <summary>Creates an event and generates only its event ID and timestamp.</summary>
    public AuditEvent(
        AuditEventPhase phase,
        string operationId,
        string sourceId,
        string requestId,
        string action,
        string? status = null,
        string? message = null,
        IReadOnlyDictionary<string, string?>? data = null,
        DateTimeOffset? occurredAt = null,
        string? eventId = null,
        string? accountId = null,
        bool isStale = false)
        : this(
            eventId ?? AuditIds.NewEventId(),
            occurredAt ?? DateTimeOffset.UtcNow,
            phase,
            operationId,
            sourceId,
            requestId,
            action,
            status,
            message,
            data,
            accountId,
            isStale)
    {
    }

    /// <summary>IDs-first constructor used by integration adapters.</summary>
    public AuditEvent(
        string operationId,
        string sourceId,
        string requestId,
        AuditEventPhase phase,
        string action,
        string? status = null,
        string? message = null,
        IReadOnlyDictionary<string, string?>? data = null,
        DateTimeOffset? occurredAt = null,
        string? eventId = null,
        string? accountId = null,
        bool isStale = false)
        : this(
            phase,
            operationId,
            sourceId,
            requestId,
            action,
            status,
            message,
            data,
            occurredAt,
            eventId,
            accountId,
            isStale)
    {
    }

    /// <summary>Creates an event using the lifecycle compatibility enum.</summary>
    public AuditEvent(
        AuditLifecyclePhase phase,
        string operationId,
        string sourceId,
        string requestId,
        string action,
        string? status = null,
        string? message = null,
        IReadOnlyDictionary<string, string?>? data = null,
        DateTimeOffset? occurredAt = null,
        string? eventId = null,
        string? accountId = null,
        bool isStale = false)
        : this(
            (AuditEventPhase)phase,
            operationId,
            sourceId,
            requestId,
            action,
            status,
            message,
            data,
            occurredAt,
            eventId,
            accountId,
            isStale)
    {
    }

    /// <summary>Creates an event using the event-type compatibility enum.</summary>
    public AuditEvent(
        AuditEventType phase,
        string operationId,
        string sourceId,
        string requestId,
        string action,
        string? status = null,
        string? message = null,
        IReadOnlyDictionary<string, string?>? data = null,
        DateTimeOffset? occurredAt = null,
        string? eventId = null,
        string? accountId = null,
        bool isStale = false)
        : this(
            (AuditEventPhase)phase,
            operationId,
            sourceId,
            requestId,
            action,
            status,
            message,
            data,
            occurredAt,
            eventId,
            accountId,
            isStale)
    {
    }

    /// <summary>Creates an event using the event-kind compatibility enum.</summary>
    public AuditEvent(
        AuditEventKind phase,
        string operationId,
        string sourceId,
        string requestId,
        string action,
        string? status = null,
        string? message = null,
        IReadOnlyDictionary<string, string?>? data = null,
        DateTimeOffset? occurredAt = null,
        string? eventId = null,
        string? accountId = null,
        bool isStale = false)
        : this(
            (AuditEventPhase)phase,
            operationId,
            sourceId,
            requestId,
            action,
            status,
            message,
            data,
            occurredAt,
            eventId,
            accountId,
            isStale)
    {
    }

    /// <summary>Stable row identifier, unique within normal journal usage.</summary>
    public string EventId { get; }

    /// <summary>UTC time at which the host observed this stage.</summary>
    public DateTimeOffset OccurredAt { get; }

    public AuditEventPhase Phase { get; }
    public string OperationId { get; }
    public string SourceId { get; }
    public string RequestId { get; }
    public string Action { get; }
    public string? Status { get; }
    public string? Message { get; }

    /// <summary>Optional account/character associated with an observation.</summary>
    public string? AccountId { get; }

    /// <summary>Whether the source snapshot was served from a stale fallback.</summary>
    public bool IsStale { get; }

    /// <summary>
    /// Small scalar details for a row.  Values are copied into a
    /// read-only dictionary at construction and never expose a caller-owned
    /// mutable collection.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Data { get; }

    // Common compatibility aliases.  They are ignored by JSON so each row
    // still has one canonical representation and remains easy to query.
    [JsonIgnore]
    public string Id => EventId;

    [JsonIgnore]
    public DateTimeOffset Timestamp => OccurredAt;

    [JsonIgnore]
    public AuditLifecyclePhase Lifecycle => (AuditLifecyclePhase)Phase;

    [JsonIgnore]
    public AuditEventType EventType => (AuditEventType)Phase;

    [JsonIgnore]
    public string Source => SourceId;

    [JsonIgnore]
    public string? Account => AccountId;

    [JsonIgnore]
    public IReadOnlyDictionary<string, string?> Metadata => Data;

    [JsonIgnore]
    public IReadOnlyDictionary<string, string?> Payload => Data;

    /// <summary>General factory for callers that select the lifecycle dynamically.</summary>
    public static AuditEvent Create(
        AuditEventPhase phase,
        string operationId,
        string sourceId,
        string requestId,
        string action,
        string? status = null,
        string? message = null,
        IReadOnlyDictionary<string, string?>? data = null,
        DateTimeOffset? occurredAt = null,
        string? eventId = null,
        string? accountId = null,
        bool isStale = false)
        => new(phase, operationId, sourceId, requestId, action, status, message, data,
            occurredAt, eventId, accountId, isStale);

    public static AuditEvent Create(
        AuditLifecyclePhase phase,
        string operationId,
        string sourceId,
        string requestId,
        string action,
        string? status = null,
        string? message = null,
        IReadOnlyDictionary<string, string?>? data = null,
        DateTimeOffset? occurredAt = null,
        string? eventId = null,
        string? accountId = null,
        bool isStale = false)
        => Create((AuditEventPhase)phase, operationId, sourceId, requestId, action,
            status, message, data, occurredAt, eventId, accountId, isStale);

    public static AuditEvent Create(
        AuditEventKind phase,
        string operationId,
        string sourceId,
        string requestId,
        string action,
        string? status = null,
        string? message = null,
        IReadOnlyDictionary<string, string?>? data = null,
        DateTimeOffset? occurredAt = null,
        string? eventId = null,
        string? accountId = null,
        bool isStale = false)
        => Create((AuditEventPhase)phase, operationId, sourceId, requestId, action,
            status, message, data, occurredAt, eventId, accountId, isStale);

    /// <summary>Creates a preview event with a conventional status.</summary>
    public static AuditEvent Preview(
        string operationId,
        string sourceId,
        string requestId,
        string action,
        string? status = "created",
        string? message = null,
        IReadOnlyDictionary<string, string?>? data = null,
        DateTimeOffset? occurredAt = null,
        string? eventId = null,
        string? accountId = null,
        bool isStale = false)
        => new(AuditEventPhase.Preview, operationId, sourceId, requestId, action,
            status, message, data, occurredAt, eventId, accountId, isStale);

    /// <summary>Creates a commit event with a conventional status.</summary>
    public static AuditEvent Commit(
        string operationId,
        string sourceId,
        string requestId,
        string action,
        string? status = "submitted",
        string? message = null,
        IReadOnlyDictionary<string, string?>? data = null,
        DateTimeOffset? occurredAt = null,
        string? eventId = null,
        string? accountId = null,
        bool isStale = false)
        => new(AuditEventPhase.Commit, operationId, sourceId, requestId, action,
            status, message, data, occurredAt, eventId, accountId, isStale);

    /// <summary>Creates a verification event with a conventional status.</summary>
    public static AuditEvent Verify(
        string operationId,
        string sourceId,
        string requestId,
        string action,
        string? status = "verified",
        string? message = null,
        IReadOnlyDictionary<string, string?>? data = null,
        DateTimeOffset? occurredAt = null,
        string? eventId = null,
        string? accountId = null,
        bool isStale = false)
        => new(AuditEventPhase.Verify, operationId, sourceId, requestId, action,
            status, message, data, occurredAt, eventId, accountId, isStale);

    internal AuditEvent ValidateForAppend()
    {
        // The constructor performs validation.  Keeping this method on the
        // model gives the journal one explicit boundary to call and makes
        // future validation additions local to this file.
        return this;
    }

    private static string RequireId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("An audit identifier is required.", parameterName);
        return value.Trim();
    }

    private static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Audit action is required.", parameterName);
        return value.Trim();
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyDictionary<string, string?> Freeze(
        IReadOnlyDictionary<string, string?>? data)
    {
        if (data is null || data.Count == 0)
            return EmptyData.Instance;

        var copy = new Dictionary<string, string?>(data.Count, StringComparer.Ordinal);
        foreach (var pair in data)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
                throw new ArgumentException("Audit data keys must not be empty.", nameof(data));
            copy[pair.Key] = pair.Value;
        }

        return new ReadOnlyDictionary<string, string?>(copy);
    }

    private sealed class EmptyData : ReadOnlyDictionary<string, string?>
    {
        public static readonly EmptyData Instance = new();

        private EmptyData()
            : base(new Dictionary<string, string?>(StringComparer.Ordinal))
        {
        }
    }
}

/// <summary>Identifier helpers used by journal adapters and tests.</summary>
public static class AuditIds
{
    public static string NewEventId() => Guid.NewGuid().ToString("N");
    public static string NewOperationId() => Guid.NewGuid().ToString("N");
    public static string NewRequestId() => Guid.NewGuid().ToString("N");
}

/// <summary>Read/query filters applied after rows are read from the JSONL file.</summary>
public sealed record AuditEventQuery
{
    public string? OperationId { get; init; }
    public string? AccountId { get; init; }
    public string? SourceId { get; init; }
    public string? RequestId { get; init; }
    public string? Action { get; init; }
    public AuditEventPhase? Phase { get; init; }
    public IReadOnlySet<AuditEventPhase>? Phases { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public DateTimeOffset? Since { get; init; }
    public DateTimeOffset? Until { get; init; }
    public int? Limit { get; init; }
    public bool Descending { get; init; }
    public bool? IsStale { get; init; }

    [JsonIgnore]
    public AuditLifecyclePhase? Lifecycle
    {
        get => Phase is null ? null : (AuditLifecyclePhase)Phase.Value;
        init => Phase = value is null ? null : (AuditEventPhase)value.Value;
    }

    [JsonIgnore]
    public AuditEventType? EventType
    {
        get => Phase is null ? null : (AuditEventType)Phase.Value;
        init => Phase = value is null ? null : (AuditEventPhase)value.Value;
    }

    // Compatibility aliases for query clients using timestamp/lifecycle names.
    [JsonIgnore]
    public DateTimeOffset? FromUtc => From ?? Since;

    [JsonIgnore]
    public DateTimeOffset? ToUtc => To ?? Until;

    [JsonIgnore]
    public string? Account => AccountId;

    [JsonIgnore]
    public string? Source
    {
        get => SourceId;
        init => SourceId = value;
    }

    public void Validate()
    {
        if (Limit is <= 0)
            throw new ArgumentOutOfRangeException(nameof(Limit), "A query limit must be positive.");
        var from = From ?? Since;
        var to = To ?? Until;
        if (from is not null && to is not null && from > to)
            throw new ArgumentException("The query start must not be after its end.");
    }
}
