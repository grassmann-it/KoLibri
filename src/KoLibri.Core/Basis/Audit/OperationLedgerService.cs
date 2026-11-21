using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KoLibri.Core.Basis;

/// <summary>Minimal service boundary for hosts that want to inject the ledger.</summary>
public interface IOperationApprovalLedger
{
    OperationLedgerPreview<TPreview> RegisterPreview<TPreview>(OperationPreviewRequest<TPreview> request);
    OperationApprovalResult Approve(OperationApprovalRequest request);
    OperationCommitResult Commit(OperationCommitRequest request);
    OperationCommitResult MarkUnknown(OperationCommitRequest request, string? message = null);
    IReadOnlyList<OperationAuditEvent> ReadAuditEvents(string? operationId = null);
}

/// <summary>Compatibility name for callers that call the service an operation ledger.</summary>
public interface IOperationLedger : IOperationApprovalLedger
{
}

/// <summary>
/// Operation-bound approval and idempotency ledger. This service only records
/// authorization state; it never calls a KoL endpoint or performs a mutation.
/// </summary>
public class OperationLedgerService : IOperationLedger
{
    private static readonly ConcurrentDictionary<string, object> ServiceGates = new(StringComparer.Ordinal);

    private readonly IOperationLedgerStore _store;
    private readonly object _gate;
    private readonly TimeProvider _clock;
    private readonly OperationLedgerOptions _options;
    private readonly JsonSerializerOptions _jsonOptions;

    public OperationLedgerService(
        IOperationLedgerStore? store = null,
        TimeProvider? clock = null,
        OperationLedgerOptions? options = null)
    {
        _store = store ?? new InMemoryOperationLedgerStore();
        _clock = clock ?? TimeProvider.System;
        _options = options ?? new OperationLedgerOptions();
        _options.Validate();
        _jsonOptions = CreateJsonOptions();
        _gate = ServiceGates.GetOrAdd(_store.StorageId, static _ => new object());
    }

    public IOperationLedgerStore Store => _store;
    public OperationLedgerOptions Options => _options;

    /// <summary>
    /// Registers a typed, immutable preview. Re-registering the same operation
    /// or idempotency key with the same binding returns the original preview.
    /// A different binding throws <see cref="OperationLedgerConflictException"/>.
    /// </summary>
    public OperationLedgerPreview<TPreview> RegisterPreview<TPreview>(
        OperationPreviewRequest<TPreview> request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var action = OperationLedgerText.Required(request.Action, nameof(request.Action));
        var fingerprint = OperationLedgerText.Required(request.Fingerprint, nameof(request.Fingerprint));
        var account = OperationLedgerText.Account(request.AccountId);
        var accountKey = OperationLedgerText.AccountKey(account);
        var operationId = string.IsNullOrWhiteSpace(request.OperationId)
            ? Guid.NewGuid().ToString("N")
            : OperationLedgerText.Required(request.OperationId, nameof(request.OperationId));
        var idempotency = OperationLedgerText.Optional(request.IdempotencyKey);
        var actor = OperationLedgerText.Optional(request.Actor);
        var now = (request.CreatedAt ?? _clock.GetUtcNow()).ToUniversalTime();
        var expiresAt = (request.ExpiresAt ?? now + _options.PreviewLifetime).ToUniversalTime();
        if (expiresAt <= now)
            throw new ArgumentOutOfRangeException(nameof(request.ExpiresAt), "Preview expiry must be in the future.");

        JsonElement payload;
        try
        {
            payload = JsonSerializer.SerializeToElement(request.Preview, _jsonOptions);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new ArgumentException("The preview payload could not be serialized.", nameof(request.Preview), exception);
        }
        if (payload.GetRawText().Length > _options.MaxPreviewBytes)
            throw new ArgumentException(
                $"The serialized preview exceeds {_options.MaxPreviewBytes} bytes.", nameof(request.Preview));

        lock (_gate)
        {
            var snapshot = _store.Read();
            var operations = snapshot.Operations.ToList();
            var existing = operations.FirstOrDefault(entry =>
                string.Equals(entry.OperationId, operationId, StringComparison.Ordinal));
            if (existing is not null)
            {
                EnsureSameBinding(existing, action, fingerprint, accountKey, idempotency, operationId);
                if (!JsonEquals(existing.Preview, payload)
                    || existing.RequiresApproval != request.RequiresApproval)
                    throw new OperationLedgerConflictException(
                        $"Operation '{operationId}' is already bound to a different preview.");

                return ToTypedPreview<TPreview>(existing, isExisting: true, idempotency);
            }

            var idemHash = idempotency is null ? null : OperationLedgerText.Hash(idempotency);
            if (idemHash is not null)
            {
                existing = operations.FirstOrDefault(entry =>
                    string.Equals(entry.IdempotencyKeyHash, idemHash, StringComparison.Ordinal));
                if (existing is not null)
                {
                    EnsureSameBinding(existing, action, fingerprint, accountKey, idempotency, operationId);
                    if (!JsonEquals(existing.Preview, payload)
                        || existing.RequiresApproval != request.RequiresApproval)
                        throw new OperationLedgerConflictException(
                            $"Idempotency key is already bound to operation '{existing.OperationId}'.");
                    return ToTypedPreview<TPreview>(existing, isExisting: true, idempotency);
                }
            }

            var entry = new OperationLedgerEntry
            {
                OperationId = operationId,
                Action = action,
                Fingerprint = fingerprint,
                AccountId = account,
                IdempotencyKeyHash = idemHash,
                Actor = actor,
                Preview = payload.Clone(),
                PreviewType = typeof(TPreview).AssemblyQualifiedName,
                CreatedAt = now,
                ExpiresAt = expiresAt,
                RequiresApproval = request.RequiresApproval,
                State = request.RequiresApproval
                    ? OperationLedgerState.AwaitingApproval
                    : OperationLedgerState.Previewed
            };
            operations.Add(entry);
            var audits = snapshot.AuditEvents.ToList();
            audits.Add(CreateAudit(entry,
                OperationAuditEventType.PreviewRegistered,
                now,
                "registered",
                actor,
                "Preview registered.",
                new Dictionary<string, string?>
                {
                    ["requiresApproval"] = request.RequiresApproval.ToString(),
                    ["expiresAt"] = expiresAt.ToString("O")
                }));
            Write(snapshot with { Operations = operations, AuditEvents = TrimAudits(audits) });
            return ToTypedPreview<TPreview>(entry, isExisting: false, idempotency);
        }
    }

    /// <summary>Convenience overload for typed registration without a request record.</summary>
    public OperationLedgerPreview<TPreview> RegisterPreview<TPreview>(
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
        => RegisterPreview(new OperationPreviewRequest<TPreview>(
            action, fingerprint, accountId, preview, idempotencyKey, operationId,
            expiresAt, actor, createdAt, requiresApproval));

    /// <summary>Alias for callers that use Register rather than RegisterPreview.</summary>
    public OperationLedgerPreview<TPreview> Register<TPreview>(OperationPreviewRequest<TPreview> request)
        => RegisterPreview(request);

    /// <summary>
    /// Try-style registration. Binding conflicts are returned in the result and
    /// do not alter the existing operation.
    /// </summary>
    public OperationPreviewRegistrationResult<TPreview> TryRegisterPreview<TPreview>(
        OperationPreviewRequest<TPreview> request)
    {
        try
        {
            var preview = RegisterPreview(request);
            return new OperationPreviewRegistrationResult<TPreview>
            {
                Preview = preview,
                Registered = !preview.IsExisting,
                Replayed = preview.IsExisting
            };
        }
        catch (OperationLedgerConflictException exception)
        {
            var now = _clock.GetUtcNow();
            var fallback = new OperationLedgerPreview<TPreview>
            {
                OperationId = request.OperationId ?? string.Empty,
                Action = request.Action,
                Fingerprint = request.Fingerprint,
                AccountId = request.AccountId,
                Preview = request.Preview,
                IdempotencyKey = request.IdempotencyKey,
                CreatedAt = now,
                ExpiresAt = now,
                State = OperationLedgerState.Rejected,
                RequiresApproval = request.RequiresApproval
            };
            return new OperationPreviewRegistrationResult<TPreview>
            {
                Preview = fallback,
                Conflict = true,
                Error = exception.Message
            };
        }
    }

    /// <summary>Gets a typed preview without issuing or accepting an approval token.</summary>
    public OperationLedgerPreview<TPreview>? GetPreview<TPreview>(string operationId)
    {
        var id = OperationLedgerText.Required(operationId, nameof(operationId));
        lock (_gate)
        {
            var entry = _store.Read().Operations.FirstOrDefault(x =>
                string.Equals(x.OperationId, id, StringComparison.Ordinal));
            return entry is null ? null : ToTypedPreview<TPreview>(entry, isExisting: true, null);
        }
    }

    /// <summary>Issues a one-time token bound to the full operation identity.</summary>
    public OperationApprovalResult Approve(OperationApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var operationId = OperationLedgerText.Required(request.OperationId, nameof(request.OperationId));
        var action = OperationLedgerText.Required(request.Action, nameof(request.Action));
        var fingerprint = OperationLedgerText.Required(request.Fingerprint, nameof(request.Fingerprint));
        var accountKey = OperationLedgerText.AccountKey(request.AccountId);
        var actor = OperationLedgerText.Optional(request.Actor);
        var now = (request.ApprovedAt ?? _clock.GetUtcNow()).ToUniversalTime();

        lock (_gate)
        {
            var snapshot = _store.Read();
            var operations = snapshot.Operations.ToList();
            var entryIndex = operations.FindIndex(x =>
                string.Equals(x.OperationId, operationId, StringComparison.Ordinal));
            if (entryIndex < 0)
            {
                return new OperationApprovalResult
                {
                    OperationId = operationId,
                    State = OperationApprovalState.Unknown,
                    Actor = actor,
                    Message = "The operation is not present in the approval ledger."
                };
            }

            var entry = operations[entryIndex];
            if (!BindingMatches(entry, action, fingerprint, accountKey))
            {
                AppendAuditAndWrite(snapshot, operations, entry,
                    OperationAuditEventType.ApprovalRejected, now, "rejected", actor,
                    "Approval binding did not match the registered operation.");
                return new OperationApprovalResult
                {
                    OperationId = operationId,
                    State = OperationApprovalState.Rejected,
                    Actor = actor,
                    Message = "Action, fingerprint, or account does not match the preview."
                };
            }

            if (entry.State == OperationLedgerState.Committed || entry.State == OperationLedgerState.Verified)
            {
                return new OperationApprovalResult
                {
                    OperationId = operationId,
                    State = OperationApprovalState.Committed,
                    ExpiresAt = entry.ApprovalTokenExpiresAt,
                    Actor = actor,
                    Message = "The operation has already committed."
                };
            }

            if (now >= entry.ExpiresAt)
            {
                entry = entry with { State = OperationLedgerState.Expired };
                operations[entryIndex] = entry;
                AppendAuditAndWrite(snapshot, operations, entry,
                    OperationAuditEventType.ApprovalExpired, now, "expired", actor,
                    "The preview expired before approval.");
                return new OperationApprovalResult
                {
                    OperationId = operationId,
                    State = OperationApprovalState.Expired,
                    ExpiresAt = entry.ExpiresAt,
                    Actor = actor,
                    Message = "The preview has expired."
                };
            }

            if (entry.State == OperationLedgerState.Approved && entry.ApprovalTokenHash is not null)
            {
                AppendAuditAndWrite(snapshot, operations, entry,
                    OperationAuditEventType.ApprovalReplay, now, "already-approved", actor,
                    "Approval was already issued; the token is not reissued.");
                return new OperationApprovalResult
                {
                    OperationId = operationId,
                    State = OperationApprovalState.AlreadyApproved,
                    ExpiresAt = entry.ApprovalTokenExpiresAt,
                    Actor = actor,
                    Message = "Approval was already issued; the original token is not retrievable."
                };
            }

            var token = OperationLedgerText.Token();
            var tokenExpiry = new[]
            {
                entry.ExpiresAt,
                now + _options.ApprovalLifetime
            }.Min();
            entry = entry with
            {
                State = OperationLedgerState.Approved,
                ApprovalTokenHash = OperationLedgerText.TokenHash(token),
                ApprovalTokenExpiresAt = tokenExpiry,
                ApprovalTokenConsumed = false,
                ApprovedAt = now,
                Actor = actor ?? entry.Actor
            };
            operations[entryIndex] = entry;
            AppendAuditAndWrite(snapshot, operations, entry,
                OperationAuditEventType.ApprovalIssued, now, "approved", actor,
                "A one-time approval token was issued.");
            return new OperationApprovalResult
            {
                OperationId = operationId,
                State = OperationApprovalState.Approved,
                ApprovalToken = token,
                ExpiresAt = tokenExpiry,
                Actor = actor,
                Message = "Approval token issued."
            };
        }
    }

    public OperationApprovalResult Approve(
        string operationId,
        string action,
        string fingerprint,
        string accountId,
        string? actor = null,
        DateTimeOffset? approvedAt = null)
        => Approve(new OperationApprovalRequest(operationId, action, fingerprint, accountId, actor, approvedAt));

    /// <summary>Approval convenience overload that takes the typed preview returned by registration.</summary>
    public OperationApprovalResult Approve<TPreview>(
        OperationLedgerPreview<TPreview> preview,
        string? actor = null,
        DateTimeOffset? approvedAt = null)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return Approve(preview.OperationId, preview.Action, preview.Fingerprint, preview.AccountId,
            actor, approvedAt);
    }

    public OperationApprovalResult RequestApproval(OperationApprovalRequest request)
        => Approve(request);

    public Task<OperationApprovalResult> ApproveAsync(
        OperationApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Approve(request));
    }

    /// <summary>
    /// Consumes the token and records a commit. A repeated, correctly bound
    /// request returns Replay and never consumes or issues another token.
    /// </summary>
    public OperationCommitResult Commit(OperationCommitRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var operationId = OperationLedgerText.Required(request.OperationId, nameof(request.OperationId));
        var action = OperationLedgerText.Required(request.Action, nameof(request.Action));
        var fingerprint = OperationLedgerText.Required(request.Fingerprint, nameof(request.Fingerprint));
        var accountKey = OperationLedgerText.AccountKey(request.AccountId);
        var idempotency = OperationLedgerText.Optional(request.IdempotencyKey);
        var now = (request.CommittedAt ?? _clock.GetUtcNow()).ToUniversalTime();

        lock (_gate)
        {
            var snapshot = _store.Read();
            var operations = snapshot.Operations.ToList();
            var entryIndex = operations.FindIndex(x =>
                string.Equals(x.OperationId, operationId, StringComparison.Ordinal));
            if (entryIndex < 0)
            {
                return Unknown(operationId, now, "The operation is not present in the approval ledger.");
            }

            var entry = operations[entryIndex];
            if (!BindingMatches(entry, action, fingerprint, accountKey))
            {
                AppendAuditAndWrite(snapshot, operations, entry,
                    OperationAuditEventType.CommitRejected, now, "rejected", null,
                    "Commit binding did not match the registered operation.");
                return Result(operationId, OperationCommitState.Rejected, entry.State, now,
                    "Action, fingerprint, or account does not match the preview.");
            }

            if (entry.IdempotencyKeyHash is not null
                && (idempotency is null
                    || !OperationLedgerText.EqualHash(entry.IdempotencyKeyHash, OperationLedgerText.Hash(idempotency))))
            {
                AppendAuditAndWrite(snapshot, operations, entry,
                    OperationAuditEventType.CommitConflict, now, "conflict", null,
                    "Idempotency key did not match the registered operation.");
                return Result(operationId, OperationCommitState.Conflict, entry.State, now,
                    "Idempotency key does not match the preview.");
            }

            if (entry.State is OperationLedgerState.Committed or OperationLedgerState.Verified)
            {
                AppendAuditAndWrite(snapshot, operations, entry,
                    OperationAuditEventType.CommitReplay, now, "replay", null,
                    "The operation was already committed.");
                return Result(operationId, OperationCommitState.Replay, entry.State, now,
                    "The operation was already committed.", entry.Receipt);
            }

            if (entry.State == OperationLedgerState.Failed && entry.ApprovalTokenConsumed)
            {
                AppendAuditAndWrite(snapshot, operations, entry,
                    OperationAuditEventType.CommitUnknown, now, "unknown", null,
                    "The prior commit outcome is unknown; reconciliation is required.");
                return Result(operationId, OperationCommitState.Unknown, entry.State, now,
                    "The prior commit outcome is unknown; reconciliation is required.");
            }

            if (entry.State == OperationLedgerState.Expired || now >= entry.ExpiresAt)
            {
                entry = entry with { State = OperationLedgerState.Expired };
                operations[entryIndex] = entry;
                AppendAuditAndWrite(snapshot, operations, entry,
                    OperationAuditEventType.OperationExpired, now, "expired", null,
                    "The preview expired before commit.");
                return Result(operationId, OperationCommitState.Expired, entry.State, now,
                    "The preview has expired.");
            }

            if (entry.RequiresApproval)
            {
                if (string.IsNullOrWhiteSpace(request.ApprovalToken))
                {
                    AppendAuditAndWrite(snapshot, operations, entry,
                        OperationAuditEventType.CommitRejected, now, "approval-required", null,
                        "A one-time approval token is required.");
                    return Result(operationId, OperationCommitState.Rejected, entry.State, now,
                        "A one-time approval token is required.");
                }

                if (entry.ApprovalTokenHash is null || entry.State != OperationLedgerState.Approved)
                {
                    AppendAuditAndWrite(snapshot, operations, entry,
                        OperationAuditEventType.CommitRejected, now, "approval-invalid", null,
                        "The operation has no usable approval token.");
                    return Result(operationId, OperationCommitState.Rejected, entry.State, now,
                        "The operation has no usable approval token.");
                }

                if (entry.ApprovalTokenConsumed)
                {
                    AppendAuditAndWrite(snapshot, operations, entry,
                        OperationAuditEventType.CommitRejected, now, "approval-consumed", null,
                        "The approval token has already been consumed.");
                    return Result(operationId, OperationCommitState.Rejected, entry.State, now,
                        "The approval token has already been consumed.");
                }

                if (entry.ApprovalTokenExpiresAt is null || now >= entry.ApprovalTokenExpiresAt.Value)
                {
                    entry = entry with { State = OperationLedgerState.Expired };
                    operations[entryIndex] = entry;
                    AppendAuditAndWrite(snapshot, operations, entry,
                        OperationAuditEventType.OperationExpired, now, "approval-expired", null,
                        "The approval token expired before commit.");
                    return Result(operationId, OperationCommitState.Expired, entry.State, now,
                        "The approval token has expired.");
                }

                var suppliedHash = OperationLedgerText.TokenHash(request.ApprovalToken);
                if (!OperationLedgerText.EqualHash(entry.ApprovalTokenHash, suppliedHash))
                {
                    AppendAuditAndWrite(snapshot, operations, entry,
                        OperationAuditEventType.CommitRejected, now, "approval-invalid", null,
                        "The approval token did not match this operation.");
                    return Result(operationId, OperationCommitState.Rejected, entry.State, now,
                        "The approval token did not match this operation.");
                }
            }

            var receipt = OperationLedgerText.Optional(request.Receipt);
            entry = entry with
            {
                State = OperationLedgerState.Committed,
                ApprovalTokenConsumed = entry.RequiresApproval,
                CommittedAt = now,
                Receipt = receipt
            };
            operations[entryIndex] = entry;
            AppendAuditAndWrite(snapshot, operations, entry,
                OperationAuditEventType.CommitAccepted, now, "committed", null,
                "Operation commit was accepted.");
            return Result(operationId, OperationCommitState.Commit, entry.State, now,
                "Operation commit was accepted.", receipt);
        }
    }

    public OperationCommitResult Commit(
        string operationId,
        string action,
        string fingerprint,
        string accountId,
        string? approvalToken = null,
        string? idempotencyKey = null,
        string? receipt = null,
        DateTimeOffset? committedAt = null)
        => Commit(new OperationCommitRequest(operationId, action, fingerprint, accountId,
            approvalToken, idempotencyKey, receipt, committedAt));

    /// <summary>Commit convenience overload that takes the typed preview returned by registration.</summary>
    public OperationCommitResult Commit<TPreview>(
        OperationLedgerPreview<TPreview> preview,
        string? approvalToken = null,
        string? idempotencyKey = null,
        string? receipt = null,
        DateTimeOffset? committedAt = null)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return Commit(preview.OperationId, preview.Action, preview.Fingerprint, preview.AccountId,
            approvalToken, idempotencyKey ?? preview.IdempotencyKey, receipt, committedAt);
    }

    public Task<OperationCommitResult> CommitAsync(
        OperationCommitRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Commit(request));
    }

    /// <summary>
    /// Marks a mutation whose external outcome cannot be determined as unknown.
    /// Callers can retain this state for reconciliation instead of retrying a
    /// potentially duplicated mutation.
    /// </summary>
    public OperationCommitResult MarkUnknown(OperationCommitRequest request, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var operationId = OperationLedgerText.Required(request.OperationId, nameof(request.OperationId));
        var action = OperationLedgerText.Required(request.Action, nameof(request.Action));
        var fingerprint = OperationLedgerText.Required(request.Fingerprint, nameof(request.Fingerprint));
        var accountKey = OperationLedgerText.AccountKey(request.AccountId);
        var now = (request.CommittedAt ?? _clock.GetUtcNow()).ToUniversalTime();
        lock (_gate)
        {
            var snapshot = _store.Read();
            var operations = snapshot.Operations.ToList();
            var index = operations.FindIndex(x => string.Equals(x.OperationId, operationId, StringComparison.Ordinal));
            if (index < 0)
                return Unknown(operationId, now, "The operation is not present in the approval ledger.");

            var entry = operations[index];
            if (!BindingMatches(entry, action, fingerprint, accountKey))
                return Result(operationId, OperationCommitState.Rejected, entry.State, now,
                    "Action, fingerprint, or account does not match the preview.");
            if (entry.State is OperationLedgerState.Committed or OperationLedgerState.Verified)
                return Result(operationId, OperationCommitState.Replay, entry.State, now,
                    "The operation was already committed.", entry.Receipt);

            entry = entry with
            {
                State = OperationLedgerState.Failed,
                ApprovalTokenConsumed = entry.RequiresApproval,
                CommittedAt = now
            };
            operations[index] = entry;
            AppendAuditAndWrite(snapshot, operations, entry,
                OperationAuditEventType.CommitUnknown, now, "unknown", null,
                message ?? "The external mutation outcome could not be determined.");
            return Result(operationId, OperationCommitState.Unknown, entry.State, now,
                message ?? "The external mutation outcome could not be determined.");
        }
    }

    public OperationCommitResult CommitUnknown(OperationCommitRequest request, string? message = null)
        => MarkUnknown(request, message);

    /// <summary>Reads redacted audit events, optionally scoped to one operation.</summary>
    public IReadOnlyList<OperationAuditEvent> ReadAuditEvents(string? operationId = null)
    {
        lock (_gate)
        {
            var events = _store.Read().AuditEvents;
            if (string.IsNullOrWhiteSpace(operationId))
                return events.Select(FreezeAudit).ToArray();
            var id = OperationLedgerText.Required(operationId, nameof(operationId));
            return events.Where(x => string.Equals(x.OperationId, id, StringComparison.Ordinal))
                .Select(FreezeAudit)
                .ToArray();
        }
    }

    public Task<IReadOnlyList<OperationAuditEvent>> ReadAuditEventsAsync(
        string? operationId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ReadAuditEvents(operationId));
    }

    public OperationLedgerEntry? GetEntry(string operationId)
    {
        var id = OperationLedgerText.Required(operationId, nameof(operationId));
        lock (_gate)
            return _store.Read().Operations.FirstOrDefault(x =>
                string.Equals(x.OperationId, id, StringComparison.Ordinal));
    }

    private void EnsureSameBinding(
        OperationLedgerEntry existing,
        string action,
        string fingerprint,
        string accountKey,
        string? idempotency,
        string operationId)
    {
        if (!BindingMatches(existing, action, fingerprint, accountKey))
            throw new OperationLedgerConflictException(
                $"Operation '{operationId}' is bound to a different action, fingerprint, or account.");

        if (existing.IdempotencyKeyHash is not null
            && (idempotency is null
                || !OperationLedgerText.EqualHash(existing.IdempotencyKeyHash, OperationLedgerText.Hash(idempotency))))
            throw new OperationLedgerConflictException(
                $"Operation '{operationId}' is bound to a different idempotency key.");
    }

    private static bool BindingMatches(
        OperationLedgerEntry entry,
        string action,
        string fingerprint,
        string accountKey)
        => string.Equals(entry.Action, action, StringComparison.Ordinal)
           && string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal)
           && string.Equals(OperationLedgerText.AccountKey(entry.AccountId), accountKey, StringComparison.Ordinal);

    private OperationLedgerPreview<TPreview> ToTypedPreview<TPreview>(
        OperationLedgerEntry entry,
        bool isExisting,
        string? idempotencyKey)
    {
        TPreview payload;
        try
        {
            payload = entry.Preview.Deserialize<TPreview>(_jsonOptions)
                ?? throw new OperationLedgerStoreException(
                    $"Preview payload for operation '{entry.OperationId}' was null.");
        }
        catch (OperationLedgerStoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new OperationLedgerStoreException(
                $"Preview payload for operation '{entry.OperationId}' could not be read as {typeof(TPreview).Name}.", exception);
        }

        return new OperationLedgerPreview<TPreview>
        {
            OperationId = entry.OperationId,
            Action = entry.Action,
            Fingerprint = entry.Fingerprint,
            AccountId = entry.AccountId,
            Preview = payload,
            IdempotencyKey = idempotencyKey,
            Actor = entry.Actor,
            CreatedAt = entry.CreatedAt,
            ExpiresAt = entry.ExpiresAt,
            State = entry.State,
            RequiresApproval = entry.RequiresApproval,
            IsExisting = isExisting
        };
    }

    private static OperationCommitResult Result(
        string operationId,
        OperationCommitState state,
        OperationLedgerState operationState,
        DateTimeOffset occurredAt,
        string? message,
        string? receipt = null)
        => new()
        {
            OperationId = operationId,
            State = state,
            OperationState = operationState,
            OccurredAt = occurredAt,
            Message = message,
            Receipt = receipt
        };

    private static OperationCommitResult Unknown(string operationId, DateTimeOffset now, string message)
        => Result(operationId, OperationCommitState.Unknown, OperationLedgerState.Unknown, now, message);

    private void AppendAuditAndWrite(
        OperationLedgerSnapshot original,
        List<OperationLedgerEntry> operations,
        OperationLedgerEntry entry,
        OperationAuditEventType type,
        DateTimeOffset at,
        string status,
        string? actor,
        string message)
    {
        var audits = original.AuditEvents.ToList();
        audits.Add(CreateAudit(entry, type, at, status, actor, message, null));
        Write(original with { Operations = operations, AuditEvents = TrimAudits(audits) });
    }

    private void Write(OperationLedgerSnapshot snapshot)
        => _store.Write(snapshot with { Version = OperationLedgerSnapshot.CurrentVersion });

    private IReadOnlyList<OperationAuditEvent> TrimAudits(List<OperationAuditEvent> audits)
    {
        if (audits.Count <= _options.MaxAuditEvents)
            return audits.ToArray();
        return audits.Skip(audits.Count - _options.MaxAuditEvents).ToArray();
    }

    private static OperationAuditEvent CreateAudit(
        OperationLedgerEntry entry,
        OperationAuditEventType type,
        DateTimeOffset occurredAt,
        string status,
        string? actor,
        string? message,
        IReadOnlyDictionary<string, string?>? data)
        => new()
        {
            EventId = Guid.NewGuid().ToString("N"),
            OperationId = entry.OperationId,
            Type = type,
            Action = OperationLedgerText.Redact(entry.Action, 120),
            AccountId = OperationLedgerText.Redact(entry.AccountId, 120),
            FingerprintHash = OperationLedgerText.Hash(entry.Fingerprint),
            Actor = OperationLedgerText.Redact(actor, 120),
            OccurredAt = occurredAt,
            Status = OperationLedgerText.Redact(status, 80),
            Message = OperationLedgerText.Redact(message, 512),
            Data = OperationLedgerText.RedactData(data)
        };

    private static OperationAuditEvent FreezeAudit(OperationAuditEvent audit)
        => audit with { Data = OperationLedgerText.RedactData(audit.Data) };

    private static JsonSerializerOptions CreateJsonOptions()
        => new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

    private static bool JsonEquals(JsonElement left, JsonElement right)
        => string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);
}

/// <summary>Compatibility service name for approval-focused hosts.</summary>
public class ApprovalLedgerService : OperationLedgerService
{
    public ApprovalLedgerService(
        IOperationLedgerStore? store = null,
        TimeProvider? clock = null,
        OperationLedgerOptions? options = null)
        : base(store, clock, options)
    {
    }
}

/// <summary>Compatibility service name for callers that use operation approval.</summary>
public class OperationApprovalLedger : OperationLedgerService
{
    public OperationApprovalLedger(
        IOperationLedgerStore? store = null,
        TimeProvider? clock = null,
        OperationLedgerOptions? options = null)
        : base(store, clock, options)
    {
    }
}
