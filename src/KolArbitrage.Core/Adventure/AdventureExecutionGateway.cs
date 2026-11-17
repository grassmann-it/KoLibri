using System.Security.Cryptography;
using System.Text;

namespace KolArbitrage.Core.Adventure;

/// <summary>
/// Narrow coordinator around <see cref="AdventureRunKernel"/>.  The gateway
/// owns no HTTP client and cannot invent an action: every POST comes from the
/// kernel's allowlist decision, is approved before dispatch, and is sent once.
/// A response is correlated and checkpointed with CAS before the next action is
/// exposed to the caller.
/// </summary>
public sealed class AdventureExecutionGateway : IAsyncDisposable, IDisposable
{
    private readonly IAdventureExecutionTransport _transport;
    private readonly IAdventureRunCheckpointStore _checkpoints;
    private readonly AdventureRunKernel _kernel;
    private readonly IAdventureEncounterParser _parser;
    private readonly IAdventureApprovalHook _approval;
    private readonly IAdventureVerificationHook _verification;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _lane = new(1, 1);
    private bool _disposed;

    public AdventureExecutionGateway(
        IAdventureExecutionTransport transport,
        IAdventureRunCheckpointStore checkpoints,
        AdventureRunKernel? kernel = null,
        IAdventureEncounterParser? parser = null,
        IAdventureApprovalHook? approval = null,
        IAdventureVerificationHook? verification = null,
        TimeProvider? clock = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _checkpoints = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
        _kernel = kernel ?? new AdventureRunKernel();
        _parser = parser ?? new KoLAdventureEncounterParser();
        _approval = approval ?? new AllowAdventureExecution();
        _verification = verification ?? new AllowAdventureExecution();
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Transport supplied by the host; Core never creates an HTTP client.</summary>
    public IAdventureExecutionTransport Transport => _transport;

    public IAdventureRunCheckpointStore CheckpointStore => _checkpoints;

    public AdventureRunKernel Kernel => _kernel;

    public TimeProvider Clock => _clock;

    /// <summary>
    /// Starts one run and dispatches its initial adventure request.  If a
    /// checkpoint already exists for the run id, this method resumes read-first
    /// and never starts a second adventure.
    /// </summary>
    public Task<AdventureExecutionResult> StartAsync(
        AdventureRunRequest request,
        AdventureActionAllowlist allowlist,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(allowlist);
        return InLaneAsync(
            token => StartCoreAsync(request, allowlist, token),
            cancellationToken);
    }

    public Task<AdventureExecutionResult> BeginAsync(
        AdventureRunRequest request,
        AdventureActionAllowlist allowlist,
        CancellationToken cancellationToken = default)
        => StartAsync(request, allowlist, cancellationToken);

    /// <summary>
    /// Resumes a checkpoint read-first.  A checkpoint with an in-flight request
    /// is deliberately returned as <see cref="AdventureExecutionStatus.RequiresRefresh"/>
    /// instead of re-emitting the POST.
    /// </summary>
    public Task<AdventureExecutionResult> ResumeAsync(
        string runId,
        AdventureActionAllowlist allowlist,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(allowlist);
        return InLaneAsync(
            token => ResumeCoreAsync(runId.Trim(), allowlist, token),
            cancellationToken);
    }

    /// <summary>Dispatches the pending allowlisted action exactly once.</summary>
    public Task<AdventureExecutionResult> ExecutePendingAsync(
        string runId,
        AdventureActionAllowlist allowlist,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(allowlist);
        return InLaneAsync(
            token => ExecutePendingCoreAsync(runId.Trim(), allowlist, token),
            cancellationToken);
    }

    public Task<AdventureExecutionResult> AdvanceAsync(
        string runId,
        AdventureActionAllowlist allowlist,
        CancellationToken cancellationToken = default)
        => ExecutePendingAsync(runId, allowlist, cancellationToken);

    public Task<AdventureExecutionResult> StepAsync(
        string runId,
        AdventureActionAllowlist allowlist,
        CancellationToken cancellationToken = default)
        => ExecutePendingAsync(runId, allowlist, cancellationToken);

    /// <summary>
    /// Performs one idempotent refresh and feeds its observation through the
    /// kernel.  This is the only reconciliation operation exposed after a
    /// request has an in-flight marker.
    /// </summary>
    public Task<AdventureExecutionResult> RefreshAsync(
        string runId,
        AdventureActionAllowlist allowlist,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(allowlist);
        return InLaneAsync(
            token => RefreshCoreAsync(runId.Trim(), allowlist, token),
            cancellationToken);
    }

    public Task<AdventureExecutionResult> RefreshEncounterAsync(
        string runId,
        AdventureActionAllowlist allowlist,
        CancellationToken cancellationToken = default)
        => RefreshAsync(runId, allowlist, cancellationToken);

    /// <summary>Convenience alias for callers that model one gateway step as Execute.</summary>
    public Task<AdventureExecutionResult> ExecuteAsync(
        AdventureRunRequest request,
        AdventureActionAllowlist allowlist,
        CancellationToken cancellationToken = default)
        => StartAsync(request, allowlist, cancellationToken);

    private async Task<AdventureExecutionResult> StartCoreAsync(
        AdventureRunRequest request,
        AdventureActionAllowlist allowlist,
        CancellationToken cancellationToken)
    {
        var value = request.Normalize();
        if (string.IsNullOrWhiteSpace(value.RunId))
        {
            var invalidDecision = AbortForRequest(value, AdventureAbortReason.InvalidObservation, "A run id is required.");
            return Result(AdventureExecutionStatus.Aborted, invalidDecision);
        }

        var existing = await _checkpoints.LoadAsync(value.RunId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return await ResumeLoadedAsync(existing, allowlist, cancellationToken).ConfigureAwait(false);

        var decision = _kernel.Begin(value, allowlist);
        var initial = PrepareCheckpoint(decision.Checkpoint, expected: null);
        if (!await TryCasAsync(null, initial, cancellationToken).ConfigureAwait(false))
        {
            var conflict = AbortForRequest(value, AdventureAbortReason.CheckpointConflict,
                "Another worker created this adventure checkpoint.");
            return Result(AdventureExecutionStatus.Aborted, conflict);
        }

        if (decision.IsAborted || decision.NextAction is null)
            return Result(AdventureExecutionStatus.Aborted, decision with { Checkpoint = initial });

        return await DispatchAsync(initial, decision.State, decision.NextAction,
            AdventureTransportOperation.Adventure, allowlist, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AdventureExecutionResult> ResumeCoreAsync(
        string runId,
        AdventureActionAllowlist allowlist,
        CancellationToken cancellationToken)
    {
        var checkpoint = await _checkpoints.LoadAsync(runId, cancellationToken).ConfigureAwait(false);
        if (checkpoint is null)
        {
            var missingRequest = new AdventureRunRequest { RunId = runId };
            return Result(AdventureExecutionStatus.Aborted,
                AbortForRequest(missingRequest, AdventureAbortReason.InvalidCheckpoint,
                    "No adventure checkpoint exists for this run."));
        }

        return await ResumeLoadedAsync(checkpoint, allowlist, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AdventureExecutionResult> ResumeLoadedAsync(
        AdventureCheckpoint checkpoint,
        AdventureActionAllowlist allowlist,
        CancellationToken cancellationToken)
    {
        var decision = _kernel.Resume(checkpoint, allowlist);
        if (!string.IsNullOrWhiteSpace(checkpoint.InFlightRequestId))
        {
            // Rebuild from the stored checkpoint so the in-flight marker is
            // visible to the caller even though the pure kernel does not know
            // about transport metadata.
            var state = checkpoint.ToState();
            var readFirst = new AdventureKernelDecision
            {
                State = state,
                Checkpoint = checkpoint,
                Actions = Array.Empty<AdventureKernelAction>(),
                RequiresRefresh = true,
                WasResumed = true,
                AbortReason = AdventureAbortReason.IndeterminatePost
            };
            return Result(AdventureExecutionStatus.RequiresRefresh, readFirst,
                "A previous request may have reached the server; refresh before submitting another POST.");
        }

        var status = decision.IsAborted
            ? AdventureExecutionStatus.Aborted
            : decision.IsComplete
                ? AdventureExecutionStatus.Complete
                : decision.RequiresRefresh
                    ? AdventureExecutionStatus.RequiresRefresh
                    : AdventureExecutionStatus.Advanced;
        return Result(status, decision);
    }

    private async Task<AdventureExecutionResult> ExecutePendingCoreAsync(
        string runId,
        AdventureActionAllowlist allowlist,
        CancellationToken cancellationToken)
    {
        var checkpoint = await _checkpoints.LoadAsync(runId, cancellationToken).ConfigureAwait(false);
        if (checkpoint is null)
        {
            var missingRequest = new AdventureRunRequest { RunId = runId };
            return Result(AdventureExecutionStatus.Aborted,
                AbortForRequest(missingRequest, AdventureAbortReason.InvalidCheckpoint,
                    "No adventure checkpoint exists for this run."));
        }

        if (!string.IsNullOrWhiteSpace(checkpoint.InFlightRequestId))
        {
            var decision = new AdventureKernelDecision
            {
                State = checkpoint.ToState(),
                Checkpoint = checkpoint,
                Actions = Array.Empty<AdventureKernelAction>(),
                RequiresRefresh = true,
                AbortReason = AdventureAbortReason.IndeterminatePost,
                WasResumed = true
            };
            return Result(AdventureExecutionStatus.RequiresRefresh, decision,
                "The pending POST has no correlated response; refresh before retrying.");
        }

        var state = checkpoint.ToState();
        if (state.IsTerminal)
        {
            var terminal = new AdventureKernelDecision
            {
                State = state,
                Checkpoint = checkpoint,
                Actions = Array.Empty<AdventureKernelAction>()
            };
            return Result(state is AdventureCompleteRunState
                ? AdventureExecutionStatus.Complete
                : AdventureExecutionStatus.Aborted, terminal);
        }

        if (state.PendingAction is null)
        {
            var decision = AbortExisting(state, AdventureAbortReason.InvalidCheckpoint,
                "The checkpoint has no pending action to execute.");
            return await PersistAbortAsync(checkpoint, decision, cancellationToken).ConfigureAwait(false);
        }

        var operation = OperationFor(state.PendingAction);
        return await DispatchAsync(checkpoint, state, state.PendingAction,
            operation, allowlist, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AdventureExecutionResult> RefreshCoreAsync(
        string runId,
        AdventureActionAllowlist allowlist,
        CancellationToken cancellationToken)
    {
        var checkpoint = await _checkpoints.LoadAsync(runId, cancellationToken).ConfigureAwait(false);
        if (checkpoint is null)
        {
            var missingRefreshRequest = new AdventureRunRequest { RunId = runId };
            return Result(AdventureExecutionStatus.Aborted,
                AbortForRequest(missingRefreshRequest, AdventureAbortReason.InvalidCheckpoint,
                    "No adventure checkpoint exists for this run."));
        }

        var state = checkpoint.ToState();
        if (state.IsTerminal)
        {
            var terminal = new AdventureKernelDecision
            {
                State = state,
                Checkpoint = checkpoint,
                Actions = Array.Empty<AdventureKernelAction>()
            };
            return Result(state is AdventureCompleteRunState
                ? AdventureExecutionStatus.Complete
                : AdventureExecutionStatus.Aborted, terminal);
        }

        var request = BuildRequest(checkpoint, state, action: null,
            AdventureTransportOperation.Refresh);
        var marked = PrepareCheckpoint(checkpoint, request.RequestId);
        if (!await TryCasAsync(checkpoint, marked, cancellationToken).ConfigureAwait(false))
            return CheckpointConflict(state, checkpoint);

        AdventureTransportResponse? response;
        try
        {
            response = await _transport.RefreshAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Indeterminate(marked, state, request.RequestId,
                AdventureAbortReason.TransportFailure,
                "The encounter refresh timed out; retry the refresh explicitly.");
        }
        catch (Exception exception)
        {
            return Indeterminate(marked, state, request.RequestId,
                AdventureAbortReason.TransportFailure,
                $"Encounter refresh failed: {exception.Message}");
        }

        return await HandleResponseAsync(marked, state, request, response,
            allowlist, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AdventureExecutionResult> DispatchAsync(
        AdventureCheckpoint checkpoint,
        AdventureRunState state,
        AdventureKernelAction action,
        AdventureTransportOperation operation,
        AdventureActionAllowlist allowlist,
        CancellationToken cancellationToken)
    {
        var request = BuildRequest(checkpoint, state, action, operation);
        if (operation != AdventureTransportOperation.Refresh)
        {
            AdventureApprovalDecision approval;
            try
            {
                approval = await _approval.ApproveAsync(
                    new AdventureApprovalContext(state, action, request),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var denied = AbortExisting(state, AdventureAbortReason.ApprovalRequired,
                    $"Approval hook failed: {exception.Message}");
                return await PersistAbortAsync(checkpoint, denied, cancellationToken).ConfigureAwait(false);
            }

            if (approval is null || !approval.Approved)
            {
                var reason = string.IsNullOrWhiteSpace(approval?.Reason)
                    ? "The mutating adventure request was not approved."
                    : approval.Reason!;
                var denied = AbortExisting(state, AdventureAbortReason.ApprovalRequired, reason);
                return await PersistAbortAsync(checkpoint, denied, cancellationToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(approval.ApprovalToken))
                request = request with { ApprovalToken = approval.ApprovalToken };
        }

        // Mark before the call.  A process crash after the server accepted a
        // POST therefore resumes as read-first and cannot blindly replay it.
        var marked = PrepareCheckpoint(checkpoint, request.RequestId);
        if (!await TryCasAsync(checkpoint, marked, cancellationToken).ConfigureAwait(false))
            return CheckpointConflict(state, checkpoint);

        AdventureTransportResponse? response;
        try
        {
            response = await InvokeAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Indeterminate(marked, state, request.RequestId,
                AdventureAbortReason.IndeterminatePost,
                "The mutating request timed out without a correlated response; refresh before retrying.");
        }
        catch (Exception exception)
        {
            return Indeterminate(marked, state, request.RequestId,
                operation == AdventureTransportOperation.Refresh
                    ? AdventureAbortReason.TransportFailure
                    : AdventureAbortReason.IndeterminatePost,
                operation == AdventureTransportOperation.Refresh
                    ? $"Transport failed: {exception.Message}"
                    : $"The mutating request failed without a correlated response: {exception.Message}");
        }

        return await HandleResponseAsync(marked, state, request, response,
            allowlist, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AdventureExecutionResult> HandleResponseAsync(
        AdventureCheckpoint marked,
        AdventureRunState state,
        AdventureTransportRequest request,
        AdventureTransportResponse? response,
        AdventureActionAllowlist allowlist,
        CancellationToken cancellationToken)
    {
        if (response is null)
            return await AbortAndPersistAsync(marked, state,
                AdventureAbortReason.InvalidTransportResponse,
                "The transport returned no response.", cancellationToken).ConfigureAwait(false);

        if (!string.Equals(request.RequestId, response.RequestId, StringComparison.Ordinal))
            return await AbortAndPersistAsync(marked, state,
                AdventureAbortReason.ResponseCorrelationMismatch,
                "The response correlation id does not match the request.", cancellationToken,
                response).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(response.RequestFingerprint)
            && !string.Equals(request.ActionFingerprint, response.RequestFingerprint, StringComparison.Ordinal))
            return await AbortAndPersistAsync(marked, state,
                AdventureAbortReason.ResponseCorrelationMismatch,
                "The response action fingerprint does not match the request.", cancellationToken,
                response).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(response.RunId)
            && !string.Equals(request.RunId, response.RunId, StringComparison.Ordinal))
            return await AbortAndPersistAsync(marked, state,
                AdventureAbortReason.ResponseCorrelationMismatch,
                "The response run id does not match the checkpoint.", cancellationToken,
                response).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(response.AdventureId)
            && !string.Equals(request.AdventureId, response.AdventureId, StringComparison.Ordinal))
            return await AbortAndPersistAsync(marked, state,
                AdventureAbortReason.ResponseCorrelationMismatch,
                "The response adventure id does not match the checkpoint.", cancellationToken,
                response).ConfigureAwait(false);

        if (LooksLikeSessionBoundary(response))
            return await AbortAndPersistAsync(marked, state,
                AdventureAbortReason.SessionInvalid,
                "The response reached a login or expired-session boundary.", cancellationToken,
                response).ConfigureAwait(false);

        var observation = ParseObservation(request, state, response);
        if (observation is null)
            return await AbortAndPersistAsync(marked, state,
                AdventureAbortReason.InvalidTransportResponse,
                "The transport response contained no parseable encounter.", cancellationToken,
                response).ConfigureAwait(false);

        var hash = observation.ResponseHash;
        if (string.IsNullOrWhiteSpace(hash))
            return await AbortAndPersistAsync(marked, state,
                AdventureAbortReason.InvalidTransportResponse,
                "The encounter response has no response hash.", cancellationToken,
                response).ConfigureAwait(false);
        if (marked.ResponseHashes.Any(existing =>
                string.Equals(existing, hash, StringComparison.OrdinalIgnoreCase))
            || string.Equals(hash, marked.LastResponseHash, StringComparison.OrdinalIgnoreCase))
            return await AbortAndPersistAsync(marked, state,
                AdventureAbortReason.DuplicateResponse,
                "The response hash was already checkpointed for this run.", cancellationToken,
                response).ConfigureAwait(false);

        var validationError = ValidateObservation(state, observation);
        if (validationError is not null)
            return await AbortAndPersistAsync(marked, state,
                validationError.Value.Reason,
                validationError.Value.Message, cancellationToken, response).ConfigureAwait(false);

        var decision = _kernel.Observe(state, observation, allowlist);
        if (request.Operation != AdventureTransportOperation.Refresh)
        {
            AdventureVerificationResult verification;
            try
            {
                verification = await _verification.VerifyAsync(
                    new AdventureVerificationContext(state, request.Action!, request,
                        response, observation, decision),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return await AbortAndPersistAsync(marked, state,
                    AdventureAbortReason.VerificationFailed,
                    $"Verification hook failed: {exception.Message}", cancellationToken,
                    response).ConfigureAwait(false);
            }

            if (verification is null || !verification.Verified)
            {
                var message = string.IsNullOrWhiteSpace(verification?.Reason)
                    ? "The response verification hook rejected the result."
                    : verification.Reason!;
                return await AbortAndPersistAsync(marked, state,
                    AdventureAbortReason.VerificationFailed,
                    message, cancellationToken, response).ConfigureAwait(false);
            }
        }

        var withHistory = decision.Checkpoint with
        {
            ResponseHashes = AppendResponseHash(marked.ResponseHashes, hash)
        };
        var nextCheckpoint = PrepareCheckpoint(withHistory, expected: marked);
        if (!await TryCasAsync(marked, nextCheckpoint, cancellationToken).ConfigureAwait(false))
            return CheckpointConflict(state, marked, response);

        var status = decision.IsAborted
            ? AdventureExecutionStatus.Aborted
            : decision.IsComplete
                ? AdventureExecutionStatus.Complete
                : AdventureExecutionStatus.Advanced;
        return Result(status, decision with { Checkpoint = nextCheckpoint },
            response: response, requestId: request.RequestId);
    }

    private async Task<AdventureExecutionResult> AbortAndPersistAsync(
        AdventureCheckpoint expected,
        AdventureRunState state,
        AdventureAbortReason reason,
        string message,
        CancellationToken cancellationToken,
        AdventureTransportResponse? response = null)
    {
        var decision = AbortExisting(state, reason, message, response?.EffectiveResponseHash);
        return await PersistAbortAsync(expected, decision, cancellationToken, response).ConfigureAwait(false);
    }

    private async Task<AdventureExecutionResult> PersistAbortAsync(
        AdventureCheckpoint expected,
        AdventureKernelDecision decision,
        CancellationToken cancellationToken,
        AdventureTransportResponse? response = null)
    {
        var abortCheckpoint = decision.Checkpoint;
        if (!string.IsNullOrWhiteSpace(abortCheckpoint.LastResponseHash))
        {
            abortCheckpoint = abortCheckpoint with
            {
                ResponseHashes = AppendResponseHash(
                    expected.ResponseHashes,
                    abortCheckpoint.LastResponseHash!)
            };
        }
        var next = PrepareCheckpoint(abortCheckpoint, expected);
        if (!await TryCasAsync(expected, next, cancellationToken).ConfigureAwait(false))
            return CheckpointConflict(decision.State, expected, response);
        return Result(AdventureExecutionStatus.Aborted,
            decision with { Checkpoint = next }, response: response);
    }

    private AdventureExecutionResult CheckpointConflict(
        AdventureRunState state,
        AdventureCheckpoint checkpoint,
        AdventureTransportResponse? response = null)
    {
        var decision = AbortExisting(state, AdventureAbortReason.CheckpointConflict,
            "The adventure checkpoint changed while this worker was executing.");
        return Result(AdventureExecutionStatus.Aborted, decision,
            response: response,
            messages: ["The checkpoint compare-and-swap failed; the newer checkpoint was preserved."]);
    }

    private AdventureExecutionResult Indeterminate(
        AdventureCheckpoint marked,
        AdventureRunState state,
        string requestId,
        AdventureAbortReason reason,
        string message)
    {
        var decision = new AdventureKernelDecision
        {
            State = state,
            Checkpoint = marked,
            Actions = Array.Empty<AdventureKernelAction>(),
            RequiresRefresh = true,
            AbortReason = reason
        };
        return Result(AdventureExecutionStatus.Indeterminate, decision, requestId, messages: [message]);
    }

    private async Task<AdventureTransportResponse> InvokeAsync(
        AdventureTransportRequest request,
        CancellationToken cancellationToken)
        => request.Operation switch
        {
            AdventureTransportOperation.Adventure
                => await _transport.StartAdventureAsync(request, cancellationToken).ConfigureAwait(false),
            AdventureTransportOperation.Fight
                => await _transport.SubmitFightAsync(request, cancellationToken).ConfigureAwait(false),
            AdventureTransportOperation.Choice
                => await _transport.SubmitChoiceAsync(request, cancellationToken).ConfigureAwait(false),
            AdventureTransportOperation.Refresh
                => await _transport.RefreshAsync(request, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(request.Operation))
        };

    private AdventureTransportRequest BuildRequest(
        AdventureCheckpoint checkpoint,
        AdventureRunState state,
        AdventureKernelAction? action,
        AdventureTransportOperation operation)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        switch (action)
        {
            case AdventureEncounterAction:
                fields["adventure"] = state.AdventureId;
                fields["location"] = state.AdventureId;
                fields["snarfblat"] = state.AdventureId;
                break;
            case AdventureFightAction fight:
                fields["action"] = fight.ActionId;
                fields["round"] = fight.Round.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(fight.EncounterId)) fields["encounter"] = fight.EncounterId!;
                if (!string.IsNullOrWhiteSpace(fight.MonsterId)) fields["monster"] = fight.MonsterId!;
                break;
            case AdventureChoiceAction choice:
                fields["whichchoice"] = choice.ChoiceId;
                fields["option"] = choice.OptionId;
                if (!string.IsNullOrWhiteSpace(choice.EncounterId)) fields["encounter"] = choice.EncounterId!;
                foreach (var field in choice.ExtraFields)
                {
                    if (string.IsNullOrWhiteSpace(field.Key)
                        || string.Equals(field.Key, "whichchoice", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(field.Key, "option", StringComparison.OrdinalIgnoreCase))
                        continue;
                    fields[field.Key] = field.Value ?? string.Empty;
                }
                break;
        }

        return new AdventureTransportRequest
        {
            RequestId = NewRequestId(),
            RunId = state.RunId,
            AdventureId = state.AdventureId,
            Operation = operation,
            Action = action,
            Checkpoint = checkpoint,
            Fields = fields
        };
    }

    private AdventureKernelObservation? ParseObservation(
        AdventureTransportRequest request,
        AdventureRunState state,
        AdventureTransportResponse response)
    {
        AdventureKernelObservation? observation = response.Observation;
        if (observation is null && response.Encounter is not null)
            observation = AdventureKernelObservation.From(response.Encounter);
        if (observation is null && response.Html is not null)
        {
            var encounter = _parser.Parse(
                response.AdventureId ?? state.AdventureId,
                response.ResponseUri,
                response.Html);
            observation = AdventureKernelObservation.From(encounter);
        }
        if (observation is null) return null;

        var adventureId = string.IsNullOrWhiteSpace(observation.AdventureId)
            ? response.AdventureId ?? state.AdventureId
            : observation.AdventureId;
        var responseHash = observation.ResponseHash ?? response.EffectiveResponseHash;
        if (string.IsNullOrWhiteSpace(responseHash) && response.Html is not null)
            responseHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(response.Html)))
                .ToLowerInvariant();

        return observation with
        {
            AdventureId = adventureId,
            ResponseHash = responseHash
        };
    }

    private static (AdventureAbortReason Reason, string Message)? ValidateObservation(
        AdventureRunState state,
        AdventureKernelObservation observation)
    {
        if (observation.Kind == AdventureEncounterKind.Fight
            && (!observation.FightRound.HasValue || observation.FightRound.Value < 0))
            return (AdventureAbortReason.UnknownFight, "The fight response has no valid round.");
        if (state.Phase == AdventureRunPhase.Fight
            && observation.Kind == AdventureEncounterKind.Fight
            && state.FightRound.HasValue
            && observation.FightRound <= state.FightRound)
            return (AdventureAbortReason.UnknownFight,
                "The fight response round did not advance beyond the checkpoint.");
        if (state.Phase == AdventureRunPhase.Fight
            && observation.Kind == AdventureEncounterKind.Fight
            && !string.IsNullOrWhiteSpace(state.MonsterId)
            && !string.IsNullOrWhiteSpace(observation.MonsterId)
            && !string.Equals(state.MonsterId, observation.MonsterId, StringComparison.Ordinal))
            return (AdventureAbortReason.UnknownFight,
                "The fight response monster does not match the checkpoint.");
        if (observation.Kind == AdventureEncounterKind.Choice
            && (string.IsNullOrWhiteSpace(observation.ChoiceId)
                || observation.AvailableChoices is null
                || observation.AvailableChoices.Count == 0))
            return (AdventureAbortReason.UnknownChoice,
                "The choice response has no safe choice id and option set.");
        if (observation.Kind is AdventureEncounterKind.Fight or AdventureEncounterKind.Choice
            && !observation.CanContinue)
            return (AdventureAbortReason.UnknownEncounter,
                "The encounter cannot continue and was not a recognised terminal response.");
        return null;
    }

    private static bool LooksLikeSessionBoundary(AdventureTransportResponse response)
    {
        if (response.ResponseUri?.AbsolutePath.Contains("login", StringComparison.OrdinalIgnoreCase) == true)
            return true;
        var html = response.Html;
        return html is not null
            && (html.Contains("session has expired", StringComparison.OrdinalIgnoreCase)
                || html.Contains("please log in", StringComparison.OrdinalIgnoreCase)
                || html.Contains("name=\"loginname", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<bool> TryCasAsync(
        AdventureCheckpoint? expected,
        AdventureCheckpoint replacement,
        CancellationToken cancellationToken)
    {
        var value = replacement.Normalize();
        if (_checkpoints is IAdventureRunCheckpointCasStore cas)
        {
            var result = await cas.CompareAndSwapAsync(
                value.RunId, expected, value, cancellationToken).ConfigureAwait(false);
            return result.Succeeded;
        }

        // Legacy stores predate CAS.  Keep the safe read/compare/write shape
        // for compatibility, while hosts that need process-level atomicity can
        // provide IAdventureRunCheckpointCasStore.
        var current = await _checkpoints.LoadAsync(value.RunId, cancellationToken).ConfigureAwait(false);
        var matches = AdventureCheckpointComparer.Equals(current, expected);
        if (!matches) return false;
        await _checkpoints.SaveAsync(value, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static AdventureCheckpoint PrepareCheckpoint(
        AdventureCheckpoint checkpoint,
        AdventureCheckpoint? expected = null)
    {
        var revision = expected is null
            ? Math.Max(1, checkpoint.Revision)
            : expected.Revision >= long.MaxValue
                ? long.MaxValue
                : expected.Revision + 1;
        return checkpoint with { Revision = revision, InFlightRequestId = null };
    }

    private static IReadOnlyList<string> AppendResponseHash(
        IReadOnlyList<string>? history,
        string hash)
    {
        var values = (history ?? Array.Empty<string>()).ToList();
        if (!values.Any(existing => string.Equals(existing, hash, StringComparison.OrdinalIgnoreCase)))
            values.Add(hash);
        return values;
    }

    private static AdventureCheckpoint PrepareCheckpoint(
        AdventureCheckpoint checkpoint,
        string inFlightRequestId)
    {
        var revision = checkpoint.Revision >= long.MaxValue
            ? long.MaxValue
            : checkpoint.Revision + 1;
        return checkpoint with { Revision = revision, InFlightRequestId = inFlightRequestId };
    }

    private static AdventureKernelDecision AbortForRequest(
        AdventureRunRequest request,
        AdventureAbortReason reason,
        string message)
    {
        var state = new AdventureAbortedRunState(
            request.RunId,
            request.AdventureId,
            request.Role,
            request.DailyLimit.Normalize(),
            request.Counters.Normalize(),
            reason,
            message);
        return new AdventureKernelDecision
        {
            State = state,
            Checkpoint = AdventureCheckpoint.FromState(state),
            Actions = Array.Empty<AdventureKernelAction>(),
            AbortReason = reason
        };
    }

    private static AdventureKernelDecision AbortExisting(
        AdventureRunState state,
        AdventureAbortReason reason,
        string message,
        string? responseHash = null)
    {
        var next = new AdventureAbortedRunState(
            state.RunId,
            state.AdventureId,
            state.Role,
            state.DailyLimit,
            state.Counters,
            reason,
            message)
        {
            EncounterId = state.EncounterId,
            MonsterId = state.MonsterId,
            FightRound = state.FightRound,
            ChoiceId = state.ChoiceId,
            SelectedOption = state.SelectedOption,
            LastResponseHash = responseHash ?? state.LastResponseHash,
            AbortMessage = message
        };
        return new AdventureKernelDecision
        {
            State = next,
            Checkpoint = AdventureCheckpoint.FromState(next),
            Actions = Array.Empty<AdventureKernelAction>(),
            AbortReason = reason
        };
    }

    private static AdventureTransportOperation OperationFor(AdventureKernelAction action)
        => action.Kind switch
        {
            AdventureKernelActionKind.Encounter => AdventureTransportOperation.Adventure,
            AdventureKernelActionKind.Fight => AdventureTransportOperation.Fight,
            AdventureKernelActionKind.Choice => AdventureTransportOperation.Choice,
            _ => throw new InvalidOperationException($"Action kind '{action.Kind}' cannot be sent.")
        };

    private static AdventureExecutionResult Result(
        AdventureExecutionStatus status,
        AdventureKernelDecision decision,
        string? requestId = null,
        IReadOnlyList<string>? messages = null,
        AdventureTransportResponse? response = null)
        => new()
        {
            Status = status,
            Decision = decision,
            RequestId = requestId,
            Response = response,
            Messages = messages ?? Array.Empty<string>()
        };

    private Task<AdventureExecutionResult> InLaneAsync(
        Func<CancellationToken, Task<AdventureExecutionResult>> operation,
        CancellationToken cancellationToken)
        => RunInLaneAsync(operation, cancellationToken);

    private async Task<AdventureExecutionResult> RunInLaneAsync(
        Func<CancellationToken, Task<AdventureExecutionResult>> operation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lane.Release();
        }
    }

    private static string NewRequestId()
        => Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lane.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
