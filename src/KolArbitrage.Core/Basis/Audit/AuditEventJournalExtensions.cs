namespace KolArbitrage.Core.Basis;

/// <summary>Small naming aliases for hosts that expose the journal as an audit log.</summary>
public static class AuditEventJournalExtensions
{
    public static Task<AuditEvent> AppendEventAsync(
        this IAuditEventJournal journal,
        AuditEvent auditEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        return journal.AppendAsync(auditEvent, cancellationToken);
    }

    public static Task<IReadOnlyList<AuditEvent>> ReadEventsAsync(
        this IAuditEventJournal journal,
        AuditEventQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        return journal.ReadAsync(query, cancellationToken);
    }

    public static Task<AuditEvent> RecordPreviewAsync(
        this IAuditEventJournal journal,
        string operationId,
        string sourceId,
        string requestId,
        string action,
        string? status = "created",
        string? message = null,
        IReadOnlyDictionary<string, string?>? data = null,
        DateTimeOffset? occurredAt = null,
        CancellationToken cancellationToken = default,
        string? accountId = null,
        bool isStale = false)
    {
        ArgumentNullException.ThrowIfNull(journal);
        return journal.AppendPreviewAsync(operationId, sourceId, requestId, action, status, message,
            data, occurredAt, cancellationToken, accountId, isStale);
    }

    public static Task<AuditEvent> RecordCommitAsync(
        this IAuditEventJournal journal,
        string operationId,
        string sourceId,
        string requestId,
        string action,
        string? status = "submitted",
        string? message = null,
        IReadOnlyDictionary<string, string?>? data = null,
        DateTimeOffset? occurredAt = null,
        CancellationToken cancellationToken = default,
        string? accountId = null,
        bool isStale = false)
    {
        ArgumentNullException.ThrowIfNull(journal);
        return journal.AppendCommitAsync(operationId, sourceId, requestId, action, status, message,
            data, occurredAt, cancellationToken, accountId, isStale);
    }

    public static Task<AuditEvent> RecordVerifyAsync(
        this IAuditEventJournal journal,
        string operationId,
        string sourceId,
        string requestId,
        string action,
        string? status = "verified",
        string? message = null,
        IReadOnlyDictionary<string, string?>? data = null,
        DateTimeOffset? occurredAt = null,
        CancellationToken cancellationToken = default,
        string? accountId = null,
        bool isStale = false)
    {
        ArgumentNullException.ThrowIfNull(journal);
        return journal.AppendVerifyAsync(operationId, sourceId, requestId, action, status, message,
            data, occurredAt, cancellationToken, accountId, isStale);
    }
}
