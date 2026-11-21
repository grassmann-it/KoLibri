using System.Globalization;
using KolArbitrage.Core.Basis;

namespace KolArbitrage.Core.Spiel;

/// <summary>
/// Single-step manual combat coordinator. A mutating action is marked in-flight
/// in the checkpoint before its POST, so a timeout can only be reconciled by a
/// refresh and is never blindly replayed.
/// </summary>
public sealed class ManualCombatGateway : IManualCombatGateway, IAsyncDisposable, IDisposable
{
    private readonly IManualCombatTransport _transport;
    private readonly IManualCombatCheckpointStore _checkpoints;
    private readonly ManualCombatPolicy _policy;
    private readonly IManualCombatApprovalHook _approval;
    private readonly IOperationApprovalLedger? _ledger;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _lane = new(1, 1);
    private bool _disposed;

    public ManualCombatGateway(
        IManualCombatTransport transport,
        ManualCombatPolicy? policy = null,
        IManualCombatApprovalHook? approval = null,
        IManualCombatCheckpointStore? checkpoints = null,
        IOperationApprovalLedger? ledger = null,
        TimeProvider? clock = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _policy = policy ?? new ManualCombatPolicy();
        _policy.Validate();
        _approval = approval ?? new AllowManualCombatApproval();
        _checkpoints = checkpoints ?? new InMemoryManualCombatCheckpointStore();
        _ledger = ledger;
        _clock = clock ?? TimeProvider.System;
    }

    public IManualCombatTransport Transport => _transport;
    public IManualCombatCheckpointStore CheckpointStore => _checkpoints;
    public ManualCombatPolicy Policy => _policy;
    public IOperationApprovalLedger? Ledger => _ledger;
    public TimeProvider Clock => _clock;

    public Task<ManualCombatInspectionResult> InspectAsync(
        string combatId,
        CancellationToken cancellationToken = default)
        => InspectAsync(new ManualCombatInspectRequest(combatId), cancellationToken);

    public Task<ManualCombatInspectionResult> InspectAsync(
        ManualCombatInspectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InLaneAsync(token => InspectCoreAsync(request, token), cancellationToken);
    }

    public async Task<ManualCombatObservation?> InspectObservationAsync(
        string combatId,
        CancellationToken cancellationToken = default)
        => (await InspectAsync(combatId, cancellationToken).ConfigureAwait(false)).Observation;

    public Task<ManualCombatPreview> PreviewAsync(
        ManualCombatRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InLaneAsync(token => PreviewCoreAsync(request, token), cancellationToken);
    }

    public Task<ManualCombatPreview> PreviewAsync(
        string combatId,
        ManualCombatAction action,
        int? expectedRound = null,
        string? expectedResponseHash = null,
        string? adventureId = null,
        string accountId = "default",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        var request = new ManualCombatRequest
        {
            CombatId = combatId,
            AdventureId = adventureId,
            Action = action,
            ExpectedRound = expectedRound ?? 0,
            ExpectedResponseHash = expectedResponseHash ?? string.Empty,
            AccountId = accountId
        };
        return InLaneAsync(token => PreviewCoreAsync(request, token, expectedRound, expectedResponseHash), cancellationToken);
    }

    /// <summary>Shared read-model overload for CLI/Desktop callers.</summary>
    public Task<ManualCombatPreview> PreviewAsync(
        string combatId,
        CombatAction action,
        int? expectedRound = null,
        string? expectedResponseHash = null,
        string? adventureId = null,
        string accountId = "default",
        CancellationToken cancellationToken = default)
        => PreviewAsync(combatId, ManualCombatAction.From(action), expectedRound,
            expectedResponseHash, adventureId, accountId, cancellationToken);

    public Task<ManualCombatExecutionResult> ExecuteAsync(
        ManualCombatPreview preview,
        string? approvalToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return InLaneAsync(token => ExecuteCoreAsync(preview, approvalToken, token), cancellationToken);
    }

    public Task<ManualCombatExecutionResult> CommitAsync(
        ManualCombatPreview preview,
        string? approvalToken = null,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(preview, approvalToken, cancellationToken);

    public Task<ManualCombatInspectionResult> RefreshAsync(
        string combatId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(combatId);
        return InLaneAsync(token => RefreshCoreAsync(combatId.Trim(), token), cancellationToken);
    }

    public Task<ManualCombatInspectionResult> RefreshAsync(
        ManualCombatPreview preview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return RefreshAsync(preview.Request.CombatId, cancellationToken);
    }

    public Task<ManualCombatApprovalResult> ApproveAsync(
        ManualCombatPreview preview,
        string? actor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return InLaneAsync(token => ApproveCoreAsync(preview, actor, token), cancellationToken);
    }

    private async Task<ManualCombatInspectionResult> InspectCoreAsync(
        ManualCombatInspectRequest request,
        CancellationToken cancellationToken)
    {
        var value = request.Normalize();
        try { value.Validate(); }
        catch (Exception exception) { return InspectionFailure(ManualCombatAbortReason.InvalidRequest, exception.Message); }

        var checkpoint = await _checkpoints.LoadAsync(value.CombatId, cancellationToken).ConfigureAwait(false);
        var transportRequest = BuildReadRequest(value, checkpoint, checkpoint?.RequiresRefresh == true);
        ManualCombatTransportResponse? response;
        try { response = await _transport.InspectAsync(transportRequest, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            return InspectionFailure(ManualCombatAbortReason.TransportFailure,
                $"Combat inspect failed: {exception.Message}", checkpoint);
        }
        var correlation = ValidateResponse(transportRequest, response, false);
        if (correlation is not null) return InspectionFailure(correlation.Value.Reason, correlation.Value.Message, checkpoint, response);
        var observation = ObservationFor(response!, value.CombatId);
        if (observation is null) return InspectionFailure(ManualCombatAbortReason.MissingObservation,
            "The combat transport returned no observation.", checkpoint, response);
        if (string.IsNullOrWhiteSpace(observation.ResponseHash)) return InspectionFailure(ManualCombatAbortReason.InvalidObservation,
            "The combat observation has no response hash.", checkpoint, response);
        if (!string.Equals(observation.CombatId, value.CombatId, StringComparison.Ordinal)) return InspectionFailure(
            ManualCombatAbortReason.ResponseCorrelationMismatch, "The observation combat id does not match the request.", checkpoint, response);
        if (value.ExpectedRound.HasValue && observation.Round != value.ExpectedRound.Value) return InspectionFailure(
            ManualCombatAbortReason.ExpectedRoundMismatch, $"Expected combat round {value.ExpectedRound.Value} but observed {observation.Round}.", checkpoint, response);
        if (!string.IsNullOrWhiteSpace(value.ExpectedResponseHash)
            && !string.Equals(observation.ResponseHash, value.ExpectedResponseHash, StringComparison.Ordinal)) return InspectionFailure(
                ManualCombatAbortReason.ExpectedResponseHashMismatch, "The observed response hash does not match the expected binding.", checkpoint, response);

        var next = checkpoint is null
            ? ManualCombatCheckpoint.FromObservation(observation)
            : checkpoint with
            {
                CombatId = observation.CombatId,
                AdventureId = observation.AdventureId,
                Round = observation.Round,
                ResponseHash = observation.ResponseHash,
                Observation = observation,
                InFlightRequestId = null,
                InFlightFingerprint = null,
                AbortReason = null,
                AbortMessage = null,
                ResponseHashes = AppendHash(checkpoint.ResponseHashes, observation.ResponseHash)
            };
        next = await SaveCheckpointAsync(checkpoint, next, cancellationToken).ConfigureAwait(false);
        if (next is null) return InspectionFailure(ManualCombatAbortReason.CheckpointConflict,
            "The combat checkpoint changed while it was being inspected.", checkpoint, response);
        return new ManualCombatInspectionResult
        {
            Status = observation.IsTerminal ? ManualCombatStatus.Complete : ManualCombatStatus.Inspected,
            Observation = observation,
            Checkpoint = next
        };
    }

    private async Task<ManualCombatPreview> PreviewCoreAsync(
        ManualCombatRequest request,
        CancellationToken cancellationToken,
        int? expectedRoundOverride = null,
        string? expectedHashOverride = null)
    {
        var value = request.Normalize();
        try
        {
            if (string.IsNullOrWhiteSpace(value.CombatId)) throw new ArgumentException("Combat id is required.", nameof(request));
            ArgumentNullException.ThrowIfNull(value.Action);
            value.Action.Validate();
            if (expectedRoundOverride is < 0) throw new ArgumentOutOfRangeException(nameof(expectedRoundOverride));
            if (expectedHashOverride is not null && string.IsNullOrWhiteSpace(expectedHashOverride)) throw new ArgumentException("Expected response hash cannot be empty when supplied.", nameof(expectedHashOverride));
        }
        catch (Exception exception) { return InvalidPreview(value, exception.Message); }

        var inspection = await InspectCoreAsync(new ManualCombatInspectRequest
        {
            CombatId = value.CombatId,
            AdventureId = value.AdventureId,
            ExpectedRound = expectedRoundOverride,
            ExpectedResponseHash = expectedHashOverride
        }, cancellationToken).ConfigureAwait(false);
        var observation = inspection.Observation;
        if (observation is null) return InvalidPreview(value,
            inspection.Messages.FirstOrDefault() ?? "Combat inspection failed.", inspection.AbortReasons);

        var bound = value with
        {
            AdventureId = value.AdventureId ?? observation.AdventureId,
            ExpectedRound = expectedRoundOverride ?? observation.Round,
            ExpectedResponseHash = expectedHashOverride ?? observation.ResponseHash ?? string.Empty
        };
        try { bound.Validate(); }
        catch (Exception exception) { return InvalidPreview(bound, exception.Message); }

        var reasons = new List<ManualCombatAbortReason>();
        var messages = new List<string>();
        if (observation.IsTerminal) { reasons.Add(ManualCombatAbortReason.TerminalCombat); messages.Add("The combat is already terminal."); }
        if (observation.Round != bound.ExpectedRound) { reasons.Add(ManualCombatAbortReason.ExpectedRoundMismatch); messages.Add($"Expected combat round {bound.ExpectedRound} but observed {observation.Round}."); }
        if (!string.Equals(observation.ResponseHash, bound.ExpectedResponseHash, StringComparison.Ordinal)) { reasons.Add(ManualCombatAbortReason.ExpectedResponseHashMismatch); messages.Add("The combat response hash changed while the preview was being built."); }
        if (!IsLegal(observation, bound.Action)) { reasons.Add(ManualCombatAbortReason.ActionNotLegal); messages.Add("The requested action is not present in the observed legal action set."); }

        var now = _clock.GetUtcNow();
        var operationId = bound.OperationId ?? Guid.NewGuid().ToString("N");
        var preview = new ManualCombatPreview
        {
            OperationId = operationId,
            Request = bound with { OperationId = operationId },
            Observation = observation,
            CreatedAt = now,
            ExpiresAt = now + _policy.PreviewLifetime,
            RequiresApproval = _policy.RequireApproval,
            AbortReasons = reasons,
            Messages = messages
        };
        if (reasons.Count > 0 || _ledger is null) return preview;

        try
        {
            var registered = _ledger.RegisterPreview(new OperationPreviewRequest<ManualCombatLedgerPayload>(
                action: "manual-combat", fingerprint: bound.ActionFingerprint, accountId: bound.AccountId,
                preview: new ManualCombatLedgerPayload(bound.CombatId, bound.AdventureId, bound.ExpectedRound,
                    bound.ExpectedResponseHash, bound.Action.Kind.ToString(), bound.Action.Id, bound.Action.Quantity),
                idempotencyKey: bound.IdempotencyKey, operationId: operationId, expiresAt: preview.ExpiresAt,
                requiresApproval: _policy.RequireApproval));
            return preview with { IsExisting = registered.IsExisting };
        }
        catch (Exception exception)
        {
            return preview with { AbortReasons = new[] { ManualCombatAbortReason.LedgerRejected }, Messages = new[] { $"Approval ledger rejected the preview: {exception.Message}" } };
        }
    }

    private async Task<ManualCombatExecutionResult> ExecuteCoreAsync(
        ManualCombatPreview preview,
        string? approvalToken,
        CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        if (preview.IsExpired(now)) return Failure(ManualCombatStatus.Aborted, preview, ManualCombatAbortReason.PreviewExpired, "The combat preview has expired.");
        if (!preview.IsReady) return Failure(ManualCombatStatus.Aborted, preview, preview.PrimaryAbortReason ?? ManualCombatAbortReason.InvalidRequest, preview.Messages.FirstOrDefault() ?? "The combat preview is not executable.");
        if (_policy.DryRun) return Failure(ManualCombatStatus.Aborted, preview, ManualCombatAbortReason.DryRun, "Manual combat execution is disabled by the dry-run policy.");

        var request = preview.Request.Normalize();
        var checkpoint = await _checkpoints.LoadAsync(request.CombatId, cancellationToken).ConfigureAwait(false);
        if (checkpoint is null) return Failure(ManualCombatStatus.Aborted, preview, ManualCombatAbortReason.InvalidObservation, "No combat checkpoint exists; inspect before submitting an action.");
        if (checkpoint.RequiresRefresh) return Failure(ManualCombatStatus.RequiresRefresh, preview, ManualCombatAbortReason.InFlightRequest, "A previous combat POST has no correlated response; refresh before retrying.", checkpoint);
        if (!BindingMatches(checkpoint, request)) return Failure(ManualCombatStatus.Aborted, preview, ManualCombatAbortReason.StalePreview, "The combat round or response hash changed after this preview was created.", checkpoint);
        if (checkpoint.Observation is null || !IsLegal(checkpoint.Observation, request.Action)) return Failure(ManualCombatStatus.Aborted, preview, ManualCombatAbortReason.ActionNotLegal, "The requested action is no longer legal for the checkpointed combat page.", checkpoint);

        var transportRequest = BuildActionRequest(request, checkpoint, approvalToken);
        if (_policy.RequireApproval)
        {
            ManualCombatApprovalDecision? decision;
            try { decision = await _approval.ApproveAsync(new ManualCombatApprovalContext(preview, transportRequest), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { return Failure(ManualCombatStatus.Aborted, preview, ManualCombatAbortReason.ApprovalRequired, $"Approval hook failed: {exception.Message}", checkpoint); }
            if (decision is null || !decision.Approved) return Failure(ManualCombatStatus.Aborted, preview, ManualCombatAbortReason.ApprovalRejected, decision?.Reason ?? "Manual combat action was not approved.", checkpoint);
            if (!string.IsNullOrWhiteSpace(decision.ApprovalToken)) transportRequest = transportRequest with { ApprovalToken = decision.ApprovalToken };
        }

        var marked = checkpoint with
        {
            InFlightRequestId = transportRequest.RequestId,
            InFlightFingerprint = transportRequest.ActionFingerprint,
            LastRequestId = transportRequest.RequestId,
            AbortReason = null,
            AbortMessage = null
        };
        marked = await SaveCheckpointAsync(checkpoint, marked, cancellationToken).ConfigureAwait(false);
        if (marked is null) return Failure(ManualCombatStatus.Aborted, preview, ManualCombatAbortReason.CheckpointConflict, "The combat checkpoint changed before the action could be sent.", checkpoint);

        ManualCombatTransportResponse? response;
        try { response = await _transport.SubmitAsync(transportRequest, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            return new ManualCombatExecutionResult
            {
                Status = ManualCombatStatus.Indeterminate, Preview = preview, Before = checkpoint.Observation,
                Checkpoint = marked, RequestId = transportRequest.RequestId,
                AbortReasons = new[] { ManualCombatAbortReason.IndeterminatePost },
                Messages = new[] { $"The combat action may have reached the server: {exception.Message}. Refresh before retrying." }
            };
        }

        var correlation = ValidateResponse(transportRequest, response, true);
        if (correlation is not null) return await AbortAndPersistAsync(marked, preview, correlation.Value.Reason, correlation.Value.Message, response, cancellationToken).ConfigureAwait(false);
        if (response!.Accepted == false) return await AbortAndPersistAsync(marked, preview, ManualCombatAbortReason.CommitRejected, response.Message ?? "The combat server rejected the action.", response, cancellationToken).ConfigureAwait(false);
        var after = ObservationFor(response, request.CombatId);
        if (after is null || string.IsNullOrWhiteSpace(after.ResponseHash)) return await AbortAndPersistAsync(marked, preview, ManualCombatAbortReason.InvalidTransportResponse, "The combat action response contained no hashed observation.", response, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(after.CombatId, request.CombatId, StringComparison.Ordinal)) return await AbortAndPersistAsync(marked, preview, ManualCombatAbortReason.ResponseCorrelationMismatch, "The action response combat id does not match the preview.", response, cancellationToken).ConfigureAwait(false);
        if (string.Equals(after.ResponseHash, request.ExpectedResponseHash, StringComparison.Ordinal)) return await AbortAndPersistAsync(marked, preview, ManualCombatAbortReason.DuplicateResponse, "The action response hash is unchanged; the action was not reconciled.", response, cancellationToken).ConfigureAwait(false);
        if (!after.IsTerminal && after.Round <= request.ExpectedRound) return await AbortAndPersistAsync(marked, preview, ManualCombatAbortReason.InvalidObservation, "The active combat response did not advance the round.", response, cancellationToken).ConfigureAwait(false);

        if (_ledger is not null)
        {
            var ledgerResult = _ledger.Commit(new OperationCommitRequest(preview.OperationId, "manual-combat", request.ActionFingerprint, request.AccountId, approvalToken, request.IdempotencyKey, after.ResponseHash, now));
            if (!ledgerResult.Committed && !ledgerResult.Replayed)
            {
                var saved = await CompleteCheckpointAsync(marked, after, cancellationToken).ConfigureAwait(false);
                return new ManualCombatExecutionResult
                {
                    Status = ManualCombatStatus.Aborted, Preview = preview, Before = checkpoint.Observation, After = after,
                    Response = response, Checkpoint = saved ?? marked, RequestId = transportRequest.RequestId,
                    AbortReasons = new[] { ManualCombatAbortReason.LedgerRejected },
                    Messages = new[] { ledgerResult.Message ?? "The approval ledger rejected the committed action." }
                };
            }
        }
        var completed = await CompleteCheckpointAsync(marked, after, cancellationToken).ConfigureAwait(false);
        if (completed is null) return Failure(ManualCombatStatus.Aborted, preview, ManualCombatAbortReason.CheckpointConflict, "The combat checkpoint changed after the action response.", marked);
        return new ManualCombatExecutionResult
        {
            Status = after.IsTerminal ? ManualCombatStatus.Complete : ManualCombatStatus.Committed,
            Preview = preview, Before = checkpoint.Observation, After = after, Response = response,
            Checkpoint = completed, RequestId = transportRequest.RequestId
        };
    }

    private async Task<ManualCombatInspectionResult> RefreshCoreAsync(string combatId, CancellationToken cancellationToken)
    {
        var checkpoint = await _checkpoints.LoadAsync(combatId, cancellationToken).ConfigureAwait(false);
        var request = new ManualCombatTransportRequest
        {
            RequestId = NewRequestId(), CombatId = combatId, AdventureId = checkpoint?.AdventureId,
            ExpectedRound = checkpoint?.Round, ExpectedResponseHash = checkpoint?.ResponseHash,
            IsRefresh = true, Fields = new Dictionary<string, string> { ["combat"] = combatId }
        };
        ManualCombatTransportResponse? response;
        try { response = await _transport.RefreshAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { return InspectionFailure(ManualCombatAbortReason.TransportFailure, $"Combat refresh failed: {exception.Message}.", checkpoint); }
        var correlation = ValidateResponse(request, response, false);
        if (correlation is not null) return InspectionFailure(correlation.Value.Reason, correlation.Value.Message, checkpoint, response);
        var observation = response is null ? null : ObservationFor(response, combatId);
        if (observation is null || string.IsNullOrWhiteSpace(observation.ResponseHash)) return InspectionFailure(ManualCombatAbortReason.InvalidTransportResponse, "The combat refresh contained no hashed observation.", checkpoint, response);
        var next = checkpoint is null
            ? ManualCombatCheckpoint.FromObservation(observation)
            : checkpoint with
            {
                CombatId = observation.CombatId, AdventureId = observation.AdventureId, Round = observation.Round,
                ResponseHash = observation.ResponseHash, Observation = observation, InFlightRequestId = null,
                InFlightFingerprint = null, AbortReason = null, AbortMessage = null,
                ResponseHashes = AppendHash(checkpoint.ResponseHashes, observation.ResponseHash)
            };
        next = await SaveCheckpointAsync(checkpoint, next, cancellationToken).ConfigureAwait(false);
        if (next is null) return InspectionFailure(ManualCombatAbortReason.CheckpointConflict, "The combat checkpoint changed while it was being refreshed.", checkpoint, response);
        return new ManualCombatInspectionResult
        {
            Status = observation.IsTerminal ? ManualCombatStatus.Complete : ManualCombatStatus.Inspected,
            Observation = observation, Checkpoint = next
        };
    }

    private Task<ManualCombatApprovalResult> ApproveCoreAsync(ManualCombatPreview preview, string? actor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_ledger is null) return Task.FromResult(new ManualCombatApprovalResult { OperationId = preview.OperationId, Approved = true, Message = "No approval ledger is configured; the gateway approval hook is used." });
        var request = preview.Request;
        var result = _ledger.Approve(new OperationApprovalRequest(preview.OperationId, "manual-combat", request.ActionFingerprint, request.AccountId, actor, _clock.GetUtcNow()));
        return Task.FromResult(new ManualCombatApprovalResult { OperationId = preview.OperationId, Approved = result.Approved, ApprovalToken = result.ApprovalToken, Message = result.Message, ExpiresAt = result.ExpiresAt });
    }

    private static ManualCombatTransportRequest BuildReadRequest(ManualCombatInspectRequest request, ManualCombatCheckpoint? checkpoint, bool isRefresh)
        => new()
        {
            RequestId = NewRequestId(), CombatId = request.CombatId.Trim(), AdventureId = request.AdventureId ?? checkpoint?.AdventureId,
            ExpectedRound = request.ExpectedRound ?? checkpoint?.Round, ExpectedResponseHash = request.ExpectedResponseHash ?? checkpoint?.ResponseHash,
            IsRefresh = isRefresh, Fields = new Dictionary<string, string>(StringComparer.Ordinal) { ["combat"] = request.CombatId.Trim() }
        };

    private static ManualCombatTransportRequest BuildActionRequest(ManualCombatRequest request, ManualCombatCheckpoint checkpoint, string? approvalToken)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["combat"] = request.CombatId, ["round"] = request.ExpectedRound.ToString(CultureInfo.InvariantCulture),
            ["response_hash"] = request.ExpectedResponseHash, ["action"] = request.Action.Kind.ToString().ToLowerInvariant()
        };
        if (!string.IsNullOrWhiteSpace(request.Action.Id)) fields[request.Action.Kind == ManualCombatActionKind.Skill ? "skill" : "item"] = request.Action.Id!;
        if (request.Action.Quantity != 1) fields["quantity"] = request.Action.Quantity.ToString(CultureInfo.InvariantCulture);
        return new ManualCombatTransportRequest
        {
            RequestId = NewRequestId(), CombatId = request.CombatId, AdventureId = request.AdventureId ?? checkpoint.AdventureId,
            Action = request.Action, ExpectedRound = request.ExpectedRound, ExpectedResponseHash = request.ExpectedResponseHash,
            ActionFingerprint = request.ActionFingerprint, ApprovalToken = approvalToken, Fields = fields
        };
    }

    private static (ManualCombatAbortReason Reason, string Message)? ValidateResponse(ManualCombatTransportRequest request, ManualCombatTransportResponse? response, bool requireFingerprint)
    {
        if (response is null) return (ManualCombatAbortReason.InvalidTransportResponse, "The combat transport returned no response.");
        if (!string.Equals(request.RequestId, response.RequestId, StringComparison.Ordinal)) return (ManualCombatAbortReason.ResponseCorrelationMismatch, "The combat response correlation id does not match the request.");
        if (requireFingerprint && !string.IsNullOrWhiteSpace(response.RequestFingerprint) && !string.Equals(request.ActionFingerprint, response.RequestFingerprint, StringComparison.Ordinal)) return (ManualCombatAbortReason.ResponseCorrelationMismatch, "The combat response action fingerprint does not match the request.");
        if (!string.IsNullOrWhiteSpace(response.CombatId) && !string.Equals(request.CombatId, response.CombatId, StringComparison.Ordinal)) return (ManualCombatAbortReason.ResponseCorrelationMismatch, "The combat response id does not match the request.");
        return null;
    }

    private static bool BindingMatches(ManualCombatCheckpoint checkpoint, ManualCombatRequest request)
        => checkpoint.Round == request.ExpectedRound && string.Equals(checkpoint.ResponseHash, request.ExpectedResponseHash, StringComparison.Ordinal);

    private static bool IsLegal(ManualCombatObservation observation, ManualCombatAction requested)
        => observation.AvailableActions is { Count: > 0 } && observation.AvailableActions.Any(candidate => candidate is not null && candidate.Kind == requested.Kind && string.Equals(candidate.Id, requested.Id, StringComparison.Ordinal) && (requested.Kind != ManualCombatActionKind.Item || candidate.Quantity >= requested.Quantity));

    private async Task<ManualCombatExecutionResult> AbortAndPersistAsync(ManualCombatCheckpoint marked, ManualCombatPreview preview, ManualCombatAbortReason reason, string message, ManualCombatTransportResponse? response, CancellationToken cancellationToken)
    {
        var aborted = marked with { InFlightRequestId = null, InFlightFingerprint = null, AbortReason = reason, AbortMessage = message, ResponseHash = response?.EffectiveResponseHash ?? marked.ResponseHash };
        var saved = await SaveCheckpointAsync(marked, aborted, cancellationToken).ConfigureAwait(false) ?? marked;
        return new ManualCombatExecutionResult { Status = ManualCombatStatus.Aborted, Preview = preview, Before = marked.Observation, Response = response, Checkpoint = saved, RequestId = marked.InFlightRequestId, AbortReasons = new[] { reason }, Messages = new[] { message } };
    }

    private async Task<ManualCombatCheckpoint?> CompleteCheckpointAsync(ManualCombatCheckpoint marked, ManualCombatObservation observation, CancellationToken cancellationToken)
    {
        var next = marked with { Round = observation.Round, ResponseHash = observation.ResponseHash, Observation = observation, InFlightRequestId = null, InFlightFingerprint = null, AbortReason = null, AbortMessage = null, ResponseHashes = AppendHash(marked.ResponseHashes, observation.ResponseHash) };
        return await SaveCheckpointAsync(marked, next, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ManualCombatCheckpoint?> SaveCheckpointAsync(ManualCombatCheckpoint? expected, ManualCombatCheckpoint replacement, CancellationToken cancellationToken)
    {
        var value = replacement.Normalize();
        if (_checkpoints is IManualCombatCheckpointCasStore cas)
        {
            var result = await cas.CompareAndSwapAsync(value.CombatId, expected, value, cancellationToken).ConfigureAwait(false);
            return result.Succeeded ? result.Current : null;
        }
        var current = await _checkpoints.LoadAsync(value.CombatId, cancellationToken).ConfigureAwait(false);
        if (!SameCheckpoint(current, expected)) return null;
        await _checkpoints.SaveAsync(value, cancellationToken).ConfigureAwait(false);
        return value;
    }

    private static bool SameCheckpoint(ManualCombatCheckpoint? left, ManualCombatCheckpoint? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        var a = left.Normalize() with { Revision = 0 };
        var b = right.Normalize() with { Revision = 0 };
        return a.Equals(b);
    }

    private static string[] AppendHash(IReadOnlyList<string>? hashes, string? hash)
    {
        var values = (hashes ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        if (!string.IsNullOrWhiteSpace(hash) && !values.Contains(hash, StringComparer.OrdinalIgnoreCase)) values.Add(hash);
        return values.ToArray();
    }

    private static ManualCombatPreview InvalidPreview(ManualCombatRequest request, string message, IReadOnlyList<ManualCombatAbortReason>? reasons = null)
    {
        var now = DateTimeOffset.UtcNow;
        var value = request with { CombatId = request.CombatId?.Trim() ?? string.Empty, ExpectedResponseHash = request.ExpectedResponseHash?.Trim() ?? string.Empty };
        return new ManualCombatPreview
        {
            OperationId = value.OperationId ?? string.Empty, Request = value,
            Observation = new ManualCombatObservation { CombatId = value.CombatId, Round = value.ExpectedRound, ResponseHash = value.ExpectedResponseHash },
            CreatedAt = now, ExpiresAt = now, RequiresApproval = true,
            AbortReasons = reasons ?? new[] { ManualCombatAbortReason.InvalidRequest }, Messages = new[] { message }
        };
    }

    private static ManualCombatInspectionResult InspectionFailure(ManualCombatAbortReason reason, string message, ManualCombatCheckpoint? checkpoint = null, ManualCombatTransportResponse? response = null)
        => new() { Status = reason == ManualCombatAbortReason.InFlightRequest ? ManualCombatStatus.RequiresRefresh : ManualCombatStatus.Aborted, Observation = response is null ? null : ObservationFor(response, response.CombatId ?? checkpoint?.CombatId ?? string.Empty), Checkpoint = checkpoint, AbortReasons = new[] { reason }, Messages = new[] { message } };

    private static ManualCombatExecutionResult Failure(ManualCombatStatus status, ManualCombatPreview preview, ManualCombatAbortReason reason, string message, ManualCombatCheckpoint? checkpoint = null)
        => new() { Status = status, Preview = preview, Before = checkpoint?.Observation ?? preview.Observation, Checkpoint = checkpoint, AbortReasons = new[] { reason }, Messages = new[] { message } };

    private static ManualCombatObservation? ObservationFor(ManualCombatTransportResponse response, string combatId)
    {
        if (response.Observation is not null) return response.Observation.Normalize();
        return response.Combat is null ? null : ManualCombatObservation.From(response.Combat, combatId).Normalize();
    }

    private async Task<T> InLaneAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await operation(cancellationToken).ConfigureAwait(false); }
        finally { _lane.Release(); }
    }

    private static string NewRequestId() => Guid.NewGuid().ToString("N");

    public void Dispose() { if (_disposed) return; _disposed = true; _lane.Dispose(); }
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}

public sealed record ManualCombatApprovalResult
{
    public string OperationId { get; init; } = string.Empty;
    public bool Approved { get; init; }
    public string? ApprovalToken { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string? Message { get; init; }
    public bool Succeeded => Approved;
}

public sealed record ManualCombatLedgerPayload(
    string CombatId,
    string? AdventureId,
    int Round,
    string ResponseHash,
    string ActionKind,
    string? ActionId,
    int Quantity);

internal sealed class AllowManualCombatApproval : IManualCombatApprovalHook
{
    public ValueTask<ManualCombatApprovalDecision> ApproveAsync(ManualCombatApprovalContext context, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(ManualCombatApprovalDecision.Allow());
}
