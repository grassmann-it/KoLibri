using System.Collections.Concurrent;

namespace KolArbitrage.Core.Integration;

/// <summary>
/// Approval-bound adapter for <see cref="KoLShopMutationClient"/>.
///
/// The underlying client remains the only component that knows KoL's form
/// token and mutation route.  This service adds the workflow boundary around
/// it: a fresh preview is registered, approval is bound to its action id,
/// commits run on one serial account lane, an idempotency key is consumed once,
/// and the client's post-commit read is surfaced in an auditable result.
/// </summary>
public class ShopRepriceApprovalService : IDisposable
{
    private const string AuditSource = "shop-reprice";

    private readonly KoLShopMutationClient _client;
    private readonly ShopRepricePolicy _policy;
    private readonly TimeProvider _clock;
    private readonly IShopRepriceIdempotencyStore _idempotency;
    private readonly IAuditEventJournal? _audit;
    private readonly IAccountExecutionLane? _fixedLane;
    private readonly SerialAccountLanePool _lanePool;
    private readonly bool _ownsLanePool;
    private readonly ConcurrentDictionary<string, ShopRepricePreview> _previews = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ShopRepriceApproval> _approvals = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ShopRepriceCommitResult> _results = new(StringComparer.Ordinal);
    private readonly object _registrationGate = new();
    private bool _disposed;

    public ShopRepriceApprovalService(
        KoLShopMutationClient client,
        ShopRepricePolicy? policy = null,
        IAccountExecutionLane? accountLane = null,
        TimeProvider? clock = null,
        IShopRepriceIdempotencyStore? idempotency = null,
        IAuditEventJournal? audit = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _policy = policy ?? new ShopRepricePolicy();
        _policy.Validate();
        _clock = clock ?? TimeProvider.System;
        _idempotency = idempotency ?? new InMemoryShopRepriceIdempotencyStore();
        _audit = audit;
        _fixedLane = accountLane;
        _lanePool = new SerialAccountLanePool();
        _ownsLanePool = true;
    }

    public ShopRepriceApprovalService(
        KoLShopMutationClient client,
        IAccountExecutionLane accountLane,
        ShopRepricePolicy? policy = null,
        TimeProvider? clock = null,
        IShopRepriceIdempotencyStore? idempotency = null,
        IAuditEventJournal? audit = null)
        : this(client, policy, accountLane, clock, idempotency, audit)
    {
    }

    public ShopRepriceApprovalService(
        KoLShopMutationClient client,
        SerialAccountLanePool lanePool,
        ShopRepricePolicy? policy = null,
        TimeProvider? clock = null,
        IShopRepriceIdempotencyStore? idempotency = null,
        IAuditEventJournal? audit = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _policy = policy ?? new ShopRepricePolicy();
        _policy.Validate();
        _clock = clock ?? TimeProvider.System;
        _idempotency = idempotency ?? new InMemoryShopRepriceIdempotencyStore();
        _audit = audit;
        _fixedLane = null;
        _lanePool = lanePool ?? throw new ArgumentNullException(nameof(lanePool));
        _ownsLanePool = false;
    }

    public ShopRepricePolicy Policy => _policy;
    public IAccountExecutionLane? AccountLane => _fixedLane;
    public IShopRepriceIdempotencyStore Idempotency => _idempotency;

    /// <summary>Creates and registers a fresh, read-only reprice preview.</summary>
    public Task<ShopRepricePreview> PreviewAsync(
        ShopRepriceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        return LaneFor(request.AccountId).ExecuteAsync(token => PreviewCoreAsync(request, token), cancellationToken);
    }

    public Task<ShopRepricePreview> CreatePreviewAsync(
        ShopRepriceRequest request,
        CancellationToken cancellationToken = default)
        => PreviewAsync(request, cancellationToken);

    public ShopRepricePreview Preview(
        ShopRepriceRequest request,
        CancellationToken cancellationToken = default)
        => PreviewAsync(request, cancellationToken).GetAwaiter().GetResult();

    /// <summary>
    /// Returns the registered object for an action.  The returned value is
    /// immutable; callers cannot replace the expected prices in the registry.
    /// </summary>
    public bool TryGetPreview(string actionId, out ShopRepricePreview? preview)
    {
        if (string.IsNullOrWhiteSpace(actionId))
        {
            preview = null;
            return false;
        }
        return _previews.TryGetValue(actionId, out preview);
    }

    /// <summary>Records an opaque approval; it does not call KoL.</summary>
    public Task<ShopRepriceApproval> ApproveAsync(
        ShopRepricePreview preview,
        string? approvedBy = null,
        string? approvalToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Approve(preview, approvedBy, approvalToken));
    }

    public ShopRepriceApproval Approve(
        ShopRepricePreview preview,
        string? approvedBy = null,
        string? approvalToken = null,
        DateTimeOffset? now = null,
        TimeSpan? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ThrowIfDisposed();
        var registered = RequireRegistered(preview.ActionId);
        EnsureSamePreview(registered, preview);
        var at = now ?? _clock.GetUtcNow();
        if (at >= registered.ExpiresAt)
            throw new InvalidOperationException("The shop reprice preview has expired.");

        if (_approvals.TryGetValue(registered.ActionId, out var existing)
            && existing.IsValid(at)
            && string.IsNullOrWhiteSpace(approvalToken))
            return existing;

        var expires = at + (lifetime ?? (registered.ExpiresAt - registered.CreatedAt));
        if (expires > registered.ExpiresAt) expires = registered.ExpiresAt;
        var result = new ShopRepriceApproval
        {
            ApprovalId = $"approval-{Guid.NewGuid():N}",
            ActionId = registered.ActionId,
            ApprovalToken = string.IsNullOrWhiteSpace(approvalToken)
                ? Guid.NewGuid().ToString("N")
                : approvalToken,
            ApprovedBy = approvedBy,
            ApprovedAt = at,
            ExpiresAt = expires
        };
        _approvals[registered.ActionId] = result;
        return result;
    }

    public Task<ShopRepriceApproval> ApproveAsync(
        ShopRepriceApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetPreview(request.ActionId, out var preview) || preview is null)
            throw new ArgumentException("The approval action does not identify a registered preview.", nameof(request));
        var at = request.ApprovedAt ?? _clock.GetUtcNow();
        var lifetime = request.ExpiresAt is { } expires ? expires - at : (TimeSpan?)null;
        return Task.FromResult(Approve(preview, request.ApprovedBy, request.ApprovalToken, at, lifetime));
    }

    /// <summary>Refreshes the exact expected prices without performing a POST.</summary>
    public Task<ShopRepriceRefreshResult> RefreshAsync(
        ShopRepricePreview preview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ThrowIfDisposed();
        return LaneFor(preview.AccountId).ExecuteAsync(token => RefreshCoreAsync(preview, token), cancellationToken);
    }

    public Task<ShopRepriceRefreshResult> RefreshPreviewAsync(
        ShopRepricePreview preview,
        CancellationToken cancellationToken = default)
        => RefreshAsync(preview, cancellationToken);

    public ShopRepriceRefreshResult Refresh(
        ShopRepricePreview preview,
        CancellationToken cancellationToken = default)
        => RefreshAsync(preview, cancellationToken).GetAwaiter().GetResult();

    /// <summary>
    /// Commits a registered preview with a previously issued, action-bound
    /// approval token.  The underlying client performs a second fresh read
    /// immediately before POST and a fresh read after POST for verification.
    /// </summary>
    public Task<ShopRepriceCommitResult> CommitAsync(
        ShopRepricePreview preview,
        ShopRepriceApproval approval,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(approval);
        return CommitAsync(new ShopRepriceCommitRequest(
            preview.ActionId, approval.ApprovalId, approval.ApprovalToken,
            preview.Request.IdempotencyKey), cancellationToken);
    }

    public Task<ShopRepriceCommitResult> CommitAsync(
        ShopRepricePreview preview,
        string? approvalToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var approvalId = _approvals.TryGetValue(preview.ActionId, out var approval)
            ? approval.ApprovalId
            : string.Empty;
        return CommitAsync(new ShopRepriceCommitRequest(
            preview.ActionId, approvalId, approvalToken ?? string.Empty,
            preview.Request.IdempotencyKey), cancellationToken);
    }

    public Task<ShopRepriceCommitResult> CommitAsync(
        ShopRepriceCommitRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        if (!TryGetPreview(request.PreviewId, out var preview) || preview is null)
            return Task.FromResult(Abort(
                request.PreviewId,
                request.IdempotencyKey,
                [ShopRepriceAbortReason.PreviewNotRegistered],
                "The shop reprice preview is not registered."));
        return LaneFor(preview.AccountId).ExecuteAsync(
            token => CommitCoreAsync(preview, request, token), cancellationToken);
    }

    public ShopRepriceCommitResult Commit(
        ShopRepricePreview preview,
        ShopRepriceApproval approval,
        CancellationToken cancellationToken = default)
        => CommitAsync(preview, approval, cancellationToken).GetAwaiter().GetResult();

    /// <summary>
    /// Verifies that every requested item is now at its target price.  This is
    /// read-only and can be called after a host supplied an external commit.
    /// </summary>
    public Task<ShopRepriceVerificationResult> VerifyAsync(
        ShopRepricePreview preview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ThrowIfDisposed();
        return LaneFor(preview.AccountId).ExecuteAsync(token => VerifyCoreAsync(preview, token), cancellationToken);
    }

    public async Task<ShopRepriceCommitResult> VerifyAsync(
        string previewId,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetPreview(previewId, out var preview) || preview is null)
            return Abort(
                previewId,
                null,
                [ShopRepriceAbortReason.PreviewNotRegistered],
                "The shop reprice preview is not registered.");
        if (_results.TryGetValue(preview.ActionId, out var existing))
            return existing;
        var verification = await VerifyAsync(preview, cancellationToken).ConfigureAwait(false);
        var result = ToCommitVerification(preview, verification);
        _results[preview.ActionId] = result;
        return result;
    }

    public ShopRepriceVerificationResult Verify(
        ShopRepricePreview preview,
        CancellationToken cancellationToken = default)
        => VerifyAsync(preview, cancellationToken).GetAwaiter().GetResult();

    public ShopRepriceCommitResult VerifyCommit(
        ShopRepricePreview preview,
        CancellationToken cancellationToken = default)
        => VerifyAsync(preview.ActionId, cancellationToken).GetAwaiter().GetResult();

    private async Task<ShopRepricePreview> PreviewCoreAsync(
        ShopRepriceRequest request,
        CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            var existing = FindPreviewByIdempotency(request.IdempotencyKey);
            if (existing is not null)
            {
                if (string.Equals(existing.Request.Fingerprint(), request.Fingerprint(), StringComparison.Ordinal))
                    return existing;

                var conflict = new ShopRepricePreview(
                    Guid.NewGuid().ToString("N"),
                    request,
                    EmptyMutation(now, request.Updates),
                    now,
                    now + _policy.PreviewLifetime,
                    _policy.RequireApproval,
                    _policy.DryRun,
                    [ShopRepriceAbortReason.IdempotencyConflict],
                    ["The idempotency key is already bound to a different reprice request."]);
                RegisterPreview(conflict);
                await AuditPreviewAsync(conflict, cancellationToken).ConfigureAwait(false);
                return conflict;
            }
        }
        var actionId = Guid.NewGuid().ToString("N");
        var reasons = new List<ShopRepriceAbortReason>();
        var warnings = new List<string>();

        try
        {
            request.Validate(_policy.RequireIdempotencyKey, _policy.MaxUpdates);
            ValidateLaneAccount(request.AccountId, reasons);
        }
        catch (ArgumentException exception)
        {
            Add(reasons, ShopRepriceAbortReason.InvalidRequest);
            warnings.Add(exception.Message);
        }

        ShopMutationResult mutation;
        if (reasons.Count > 0)
        {
            mutation = EmptyMutation(now, request.Updates);
        }
        else
        {
            try
            {
                mutation = await _client.ApplyPriceUpdatesAsync(
                    request.Updates, commit: false, cancellationToken).ConfigureAwait(false);
                AddMutationReasons(mutation, reasons);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                mutation = EmptyMutation(now, request.Updates);
                Add(reasons, ShopRepriceAbortReason.TransportFailure);
                warnings.Add(exception.Message);
            }
        }

        var preview = new ShopRepricePreview(
            actionId,
            request,
            mutation,
            now,
            now + _policy.PreviewLifetime,
            _policy.RequireApproval,
            _policy.DryRun,
            reasons,
            warnings);

        RegisterPreview(preview);
        await AuditPreviewAsync(preview, cancellationToken).ConfigureAwait(false);
        return preview;
    }

    private async Task<ShopRepriceRefreshResult> RefreshCoreAsync(
        ShopRepricePreview supplied,
        CancellationToken cancellationToken)
    {
        var preview = RequireRegistered(supplied.ActionId);
        EnsureSamePreview(preview, supplied);
        var now = _clock.GetUtcNow();
        var reasons = new List<ShopRepriceAbortReason>();
        var warnings = new List<string>();
        ValidateLaneAccount(preview.AccountId, reasons);
        ShopMutationResult fresh;
        if (reasons.Count > 0)
        {
            fresh = EmptyMutation(now, preview.Request.Updates);
        }
        else try
        {
            fresh = await _client.ApplyPriceUpdatesAsync(
                preview.Request.Updates, commit: false, cancellationToken).ConfigureAwait(false);
            AddMutationReasons(fresh, reasons);
            if (now > preview.ExpiresAt) Add(reasons, ShopRepriceAbortReason.PreviewExpired);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            fresh = EmptyMutation(now, preview.Request.Updates);
            Add(reasons, ShopRepriceAbortReason.TransportFailure);
            warnings.Add(exception.Message);
        }
        return new ShopRepriceRefreshResult
        {
            Preview = preview,
            Fresh = fresh,
            AbortReasons = reasons,
            Warnings = warnings
        };
    }

    private async Task<ShopRepriceCommitResult> CommitCoreAsync(
        ShopRepricePreview preview,
        ShopRepriceCommitRequest request,
        CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var reasons = new List<ShopRepriceAbortReason>();
        var messages = new List<string>();
        ValidateLaneAccount(preview.AccountId, reasons);
        var key = request.IdempotencyKey ?? preview.Request.IdempotencyKey;
        if (!string.Equals(request.PreviewId, preview.ActionId, StringComparison.Ordinal))
            Add(reasons, ShopRepriceAbortReason.ActionChanged);
        if (!string.Equals(key, preview.Request.IdempotencyKey, StringComparison.Ordinal))
            Add(reasons, ShopRepriceAbortReason.IdempotencyKeyChanged);
        if (_policy.RequireIdempotencyKey && string.IsNullOrWhiteSpace(key))
            Add(reasons, ShopRepriceAbortReason.MissingIdempotencyKey);
        if (now >= preview.ExpiresAt)
            Add(reasons, ShopRepriceAbortReason.PreviewExpired);
        if (preview.RequiresApproval || _policy.RequireApproval)
        {
            if (string.IsNullOrWhiteSpace(request.ApprovalToken))
                Add(reasons, ShopRepriceAbortReason.MissingApproval);
            else if (!_approvals.TryGetValue(preview.ActionId, out var approval)
                     || !string.Equals(approval.ApprovalToken, request.ApprovalToken, StringComparison.Ordinal))
                Add(reasons, ShopRepriceAbortReason.ApprovalActionChanged);
            else
            {
                if (!string.IsNullOrWhiteSpace(request.ApprovalId)
                    && !string.Equals(request.ApprovalId, approval.ApprovalId, StringComparison.Ordinal))
                    Add(reasons, ShopRepriceAbortReason.ApprovalActionChanged);
                if (!approval.IsValid(now)) Add(reasons, ShopRepriceAbortReason.ApprovalExpired);
            }
        }
        if (preview.DryRun || _policy.DryRun)
            Add(reasons, ShopRepriceAbortReason.DryRun);
        foreach (var reason in preview.AbortReasons) Add(reasons, reason);
        ValidateLaneAccount(preview.AccountId, reasons);

        if (reasons.Count > 0)
        {
            var aborted = Abort(preview.ActionId, key, reasons, string.Join("; ", reasons));
            await AuditCommitAndMaybeVerifyAsync(preview, aborted, cancellationToken).ConfigureAwait(false);
            return aborted;
        }

        if (!string.IsNullOrWhiteSpace(key)
            && !_idempotency.TryBegin(key, preview.Request.Fingerprint(), preview.ActionId, now, out var existing))
        {
            if (existing is not null
                && !string.Equals(existing.Fingerprint, preview.Request.Fingerprint(), StringComparison.Ordinal))
            {
                var conflict = Abort(preview.ActionId, key,
                    [ShopRepriceAbortReason.IdempotencyConflict],
                    "The idempotency key is already bound to a different reprice request.");
                await AuditCommitAndMaybeVerifyAsync(preview, conflict, cancellationToken).ConfigureAwait(false);
                return conflict;
            }
            if (existing?.Result is { } replay)
            {
                var replayed = replay with
                {
                    IdempotencyReplayed = true,
                    Status = ShopRepriceOperationStatus.Replayed
                };
                _results[preview.ActionId] = replayed;
                return replayed;
            }
            var duplicate = Abort(preview.ActionId, key,
                [ShopRepriceAbortReason.DuplicateOperation],
                "A reprice with this idempotency key is already in progress.");
            await AuditCommitAndMaybeVerifyAsync(preview, duplicate, cancellationToken).ConfigureAwait(false);
            return duplicate;
        }

        ShopMutationResult fresh;
        try
        {
            // This read is a service-level guard.  The wrapped client performs
            // another read while holding its mutation gate immediately before
            // POST, closing the final race window.
            fresh = await _client.ApplyPriceUpdatesAsync(
                preview.Request.Updates, commit: false, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var transportFailure = Abort(preview.ActionId, key,
                [ShopRepriceAbortReason.TransportFailure], exception.Message);
            CompleteIdempotency(key, transportFailure, now);
            await AuditCommitAndMaybeVerifyAsync(preview, transportFailure, cancellationToken).ConfigureAwait(false);
            return transportFailure;
        }
        var freshReasons = new List<ShopRepriceAbortReason>();
        AddMutationReasons(fresh, freshReasons);
        if (now > preview.ExpiresAt) Add(freshReasons, ShopRepriceAbortReason.PreviewExpired);
        if (freshReasons.Count > 0)
        {
            var stale = new ShopRepriceCommitResult
            {
                ActionId = preview.ActionId,
                IdempotencyKey = key,
                Status = ShopRepriceOperationStatus.Conflict,
                VerificationStatus = ShopRepriceVerificationStatus.Aborted,
                Committed = false,
                Results = fresh.Results,
                Mutation = fresh,
                AbortReasons = freshReasons,
                Messages = ["The fresh shop read no longer matches the approved preview."],
                ObservedAt = fresh.ObservedAt
            };
            CompleteIdempotency(key, stale, now);
            await AuditCommitAndMaybeVerifyAsync(preview, stale, cancellationToken).ConfigureAwait(false);
            return stale;
        }

        ShopMutationResult mutation;
        try
        {
            mutation = await _client.ApplyPriceUpdatesAsync(
                preview.Request.Updates, commit: true, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var transportFailure = Abort(preview.ActionId, key,
                [ShopRepriceAbortReason.TransportFailure], exception.Message);
            CompleteIdempotency(key, transportFailure, now);
            await AuditCommitAndMaybeVerifyAsync(preview, transportFailure, cancellationToken).ConfigureAwait(false);
            return transportFailure;
        }

        var result = BuildCommitResult(preview, key, mutation);
        CompleteIdempotency(key, result, now);
        _results[preview.ActionId] = result;
        await AuditCommitAndMaybeVerifyAsync(preview, result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<ShopRepriceVerificationResult> VerifyCoreAsync(
        ShopRepricePreview supplied,
        CancellationToken cancellationToken)
    {
        var preview = RequireRegistered(supplied.ActionId);
        EnsureSamePreview(preview, supplied);
        var now = _clock.GetUtcNow();
        var reasons = new List<ShopRepriceAbortReason>();
        var messages = new List<string>();
        ValidateLaneAccount(preview.AccountId, reasons);
        ShopMutationResult mutation;
        if (reasons.Count > 0)
        {
            mutation = EmptyMutation(now, preview.Request.Updates);
        }
        else try
        {
            // Asking the existing client to compare NewPrice with NewPrice is
            // a safe read-only way to reuse its parser and fresh-read gate.
            var target = preview.Request.Updates
                .Select(x => new ShopPriceUpdate(x.ItemId, x.NewPrice, x.NewPrice, x.ExpectedPurchaseLimit))
                .ToArray();
            mutation = await _client.ApplyPriceUpdatesAsync(target, commit: false, cancellationToken)
                .ConfigureAwait(false);
            if (mutation.Results.Any(x => x.Status == ShopPriceUpdateStatus.Missing))
                Add(reasons, ShopRepriceAbortReason.MissingListing);
            if (mutation.Results.Any(x => x.Status == ShopPriceUpdateStatus.Conflict))
                Add(reasons, ShopRepriceAbortReason.VerificationFailed);
            if (mutation.Results.Any(x => x.Status == ShopPriceUpdateStatus.VerificationFailed))
                Add(reasons, ShopRepriceAbortReason.VerificationFailed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            mutation = EmptyMutation(now, preview.Request.Updates);
            Add(reasons, ShopRepriceAbortReason.TransportFailure);
            messages.Add(exception.Message);
        }

        var verification = new ShopRepriceVerificationResult
        {
            Status = reasons.Count == 0
                ? ShopRepriceVerificationStatus.Verified
                : ShopRepriceVerificationStatus.Failed,
            AbortReasons = reasons,
            Messages = messages,
            Observation = mutation
        };
        await AuditVerifyAsync(preview, verification, cancellationToken).ConfigureAwait(false);
        return verification;
    }

    private void RegisterPreview(ShopRepricePreview preview)
    {
        lock (_registrationGate)
            _previews[preview.ActionId] = preview;
    }

    private ShopRepricePreview? FindPreviewByIdempotency(string idempotencyKey)
    {
        lock (_registrationGate)
        {
            return _previews.Values.FirstOrDefault(x =>
                string.Equals(x.Request.IdempotencyKey, idempotencyKey, StringComparison.Ordinal));
        }
    }

    private ShopRepricePreview RequireRegistered(string actionId)
    {
        if (!_previews.TryGetValue(actionId, out var preview))
            throw new InvalidOperationException("The shop reprice preview is not registered.");
        return preview;
    }

    private static void EnsureSamePreview(ShopRepricePreview registered, ShopRepricePreview supplied)
    {
        if (!string.Equals(registered.ActionId, supplied.ActionId, StringComparison.Ordinal)
            || !string.Equals(registered.Request.Fingerprint(), supplied.Request.Fingerprint(), StringComparison.Ordinal))
            throw new InvalidOperationException("The supplied shop reprice preview is not the registered action.");
    }

    private async Task AuditPreviewAsync(ShopRepricePreview preview, CancellationToken cancellationToken)
    {
        if (_audit is null) return;
        await _audit.AppendPreviewAsync(
            preview.ActionId,
            AuditSource,
            preview.ActionId,
            "shop-reprice",
            preview.IsReady ? "ready" : "aborted",
            preview.Warnings.Count == 0 ? null : string.Join("; ", preview.Warnings),
            AuditData(preview.Request, preview.Results.Count, preview.AbortReasons),
            preview.CreatedAt,
            cancellationToken,
            preview.AccountId,
            preview.AbortReasons.Contains(ShopRepriceAbortReason.FreshReadFailed)).ConfigureAwait(false);
    }

    private async Task AuditCommitAndMaybeVerifyAsync(
        ShopRepricePreview preview,
        ShopRepriceCommitResult result,
        CancellationToken cancellationToken)
    {
        if (_audit is null) return;
        await _audit.AppendCommitAsync(
            preview.ActionId,
            AuditSource,
            preview.ActionId,
            "shop-reprice",
            result.Committed ? "submitted" : "aborted",
            result.Messages.Count == 0 ? null : string.Join("; ", result.Messages),
            AuditData(preview.Request, result.Results.Count, result.AbortReasons),
            result.ObservedAt == default ? _clock.GetUtcNow() : result.ObservedAt,
            cancellationToken,
            preview.AccountId,
            result.AbortReasons.Contains(ShopRepriceAbortReason.FreshReadFailed)).ConfigureAwait(false);
        if (result.VerificationStatus == ShopRepriceVerificationStatus.NotAttempted) return;
        await _audit.AppendVerifyAsync(
            preview.ActionId,
            AuditSource,
            preview.ActionId,
            "shop-reprice",
            result.Verified ? "verified" : "failed",
            result.Messages.Count == 0 ? null : string.Join("; ", result.Messages),
            AuditData(preview.Request, result.Results.Count, result.AbortReasons),
            result.ObservedAt == default ? _clock.GetUtcNow() : result.ObservedAt,
            cancellationToken,
            preview.AccountId,
            result.AbortReasons.Contains(ShopRepriceAbortReason.FreshReadFailed)).ConfigureAwait(false);
    }

    private async Task AuditVerifyAsync(
        ShopRepricePreview preview,
        ShopRepriceVerificationResult result,
        CancellationToken cancellationToken)
    {
        if (_audit is null) return;
        await _audit.AppendVerifyAsync(
            preview.ActionId,
            AuditSource,
            preview.ActionId,
            "shop-reprice",
            result.Verified ? "verified" : "failed",
            result.Messages.Count == 0 ? null : string.Join("; ", result.Messages),
            AuditData(preview.Request, result.Observation?.Results.Count ?? 0, result.AbortReasons),
            result.Observation?.ObservedAt ?? _clock.GetUtcNow(),
            cancellationToken,
            preview.AccountId,
            result.AbortReasons.Contains(ShopRepriceAbortReason.FreshReadFailed)).ConfigureAwait(false);
    }

    private static IReadOnlyDictionary<string, string?> AuditData(
        ShopRepriceRequest request,
        int resultCount,
        IReadOnlyList<ShopRepriceAbortReason> reasons)
        => new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["accountId"] = request.AccountId,
            ["idempotencyKey"] = request.IdempotencyKey,
            ["updateCount"] = request.Updates.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["resultCount"] = resultCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["reasons"] = reasons.Count == 0 ? null : string.Join(",", reasons)
        };

    private static ShopRepriceCommitResult ToCommitVerification(
        ShopRepricePreview preview,
        ShopRepriceVerificationResult verification)
        => new()
        {
            ActionId = preview.ActionId,
            IdempotencyKey = preview.Request.IdempotencyKey,
            Status = verification.Verified ? ShopRepriceOperationStatus.Verified : ShopRepriceOperationStatus.Failed,
            VerificationStatus = verification.Status,
            Committed = false,
            Results = verification.Observation?.Results ?? Array.Empty<ShopPriceUpdateResult>(),
            Mutation = verification.Observation,
            AbortReasons = verification.AbortReasons,
            Messages = verification.Messages,
            ObservedAt = verification.Observation?.ObservedAt ?? DateTimeOffset.UtcNow
        };

    private static ShopRepriceCommitResult BuildCommitResult(
        ShopRepricePreview preview,
        string? key,
        ShopMutationResult mutation)
    {
        var reasons = new List<ShopRepriceAbortReason>();
        AddMutationReasons(mutation, reasons);
        if (mutation.Committed && !mutation.Succeeded)
            Add(reasons, ShopRepriceAbortReason.VerificationFailed);
        var verified = mutation.Succeeded;
        return new ShopRepriceCommitResult
        {
            ActionId = preview.ActionId,
            IdempotencyKey = key,
            Status = verified
                ? ShopRepriceOperationStatus.Verified
                : mutation.Committed
                    ? ShopRepriceOperationStatus.Committed
                    : ShopRepriceOperationStatus.Aborted,
            VerificationStatus = verified
                ? ShopRepriceVerificationStatus.Verified
                : mutation.Committed
                    ? ShopRepriceVerificationStatus.Failed
                    : ShopRepriceVerificationStatus.Aborted,
            Committed = mutation.Committed,
            Results = mutation.Results,
            Mutation = mutation,
            AbortReasons = reasons,
            Messages = mutation.Results
                .Where(x => !string.IsNullOrWhiteSpace(x.Message))
                .Select(x => x.Message!)
                .ToArray(),
            ObservedAt = mutation.ObservedAt
        };
    }

    private void CompleteIdempotency(string? key, ShopRepriceCommitResult result, DateTimeOffset now)
    {
        if (!string.IsNullOrWhiteSpace(key)) _idempotency.Complete(key, result, now);
    }

    private IAccountExecutionLane LaneFor(string? accountId)
    {
        if (_fixedLane is not null) return _fixedLane;
        return _lanePool.Get(string.IsNullOrWhiteSpace(accountId) ? "default" : accountId);
    }

    private void ValidateLaneAccount(string? accountId, ICollection<ShopRepriceAbortReason> reasons)
    {
        if (_fixedLane is not null && !string.IsNullOrWhiteSpace(accountId)
            && !string.Equals(_fixedLane.AccountId, accountId, StringComparison.OrdinalIgnoreCase))
            Add(reasons, ShopRepriceAbortReason.AccountChanged);
    }

    private static ShopMutationResult EmptyMutation(
        DateTimeOffset observedAt,
        IReadOnlyList<ShopPriceUpdate>? updates)
        => new(
            false,
            false,
            observedAt,
            (updates ?? Array.Empty<ShopPriceUpdate>())
                .Select(x => new ShopPriceUpdateResult(
                    x.ItemId, x.ExpectedCurrentPrice, null, x.NewPrice,
                    x.ExpectedPurchaseLimit, null, ShopPriceUpdateStatus.Missing,
                    "No fresh shop snapshot was available."))
                .ToArray());

    private static ShopRepriceCommitResult Abort(
        string actionId,
        string? idempotencyKey,
        IReadOnlyList<ShopRepriceAbortReason> reasons,
        string message)
        => new()
        {
            ActionId = actionId,
            IdempotencyKey = idempotencyKey,
            Status = ShopRepriceOperationStatus.Aborted,
            VerificationStatus = ShopRepriceVerificationStatus.Aborted,
            Committed = false,
            AbortReasons = reasons,
            Messages = string.IsNullOrWhiteSpace(message) ? Array.Empty<string>() : [message],
            ObservedAt = DateTimeOffset.UtcNow
        };

    private static void AddMutationReasons(
        ShopMutationResult mutation,
        ICollection<ShopRepriceAbortReason> reasons)
    {
        foreach (var item in mutation.Results)
        {
            if (item.Status == ShopPriceUpdateStatus.Missing)
                Add(reasons, ShopRepriceAbortReason.MissingListing);
            else if (item.Status == ShopPriceUpdateStatus.Conflict)
                Add(reasons, ShopRepriceAbortReason.Conflict);
            else if (item.Status == ShopPriceUpdateStatus.VerificationFailed)
                Add(reasons, ShopRepriceAbortReason.VerificationFailed);
        }
    }

    private static void Add(ICollection<ShopRepriceAbortReason> reasons, ShopRepriceAbortReason reason)
    {
        if (reason != ShopRepriceAbortReason.None && !reasons.Contains(reason)) reasons.Add(reason);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsLanePool) _lanePool.Dispose();
    }
}

/// <summary>Compatibility name for hosts that call the boundary a service.</summary>
public sealed class ShopRepriceService : ShopRepriceApprovalService
{
    public ShopRepriceService(
        KoLShopMutationClient client,
        ShopRepricePolicy? policy = null,
        IAccountExecutionLane? accountLane = null,
        TimeProvider? clock = null,
        IShopRepriceIdempotencyStore? idempotency = null,
        IAuditEventJournal? audit = null)
        : base(client, policy, accountLane, clock, idempotency, audit)
    {
    }
}

/// <summary>Compatibility name for hosts that expose a KoL-specific adapter.</summary>
public sealed class KoLShopRepriceService : ShopRepriceApprovalService
{
    public KoLShopRepriceService(
        KoLShopMutationClient client,
        ShopRepricePolicy? policy = null,
        IAccountExecutionLane? accountLane = null,
        TimeProvider? clock = null,
        IShopRepriceIdempotencyStore? idempotency = null,
        IAuditEventJournal? audit = null)
        : base(client, policy, accountLane, clock, idempotency, audit)
    {
    }
}

/// <summary>Compatibility name for hosts that expose a gateway boundary.</summary>
public sealed class ShopRepriceGateway : ShopRepriceApprovalService
{
    public ShopRepriceGateway(
        KoLShopMutationClient client,
        ShopRepricePolicy? policy = null,
        IAccountExecutionLane? accountLane = null,
        TimeProvider? clock = null,
        IShopRepriceIdempotencyStore? idempotency = null,
        IAuditEventJournal? audit = null)
        : base(client, policy, accountLane, clock, idempotency, audit)
    {
    }
}

