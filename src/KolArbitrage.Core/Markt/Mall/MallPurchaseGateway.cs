namespace KolArbitrage.Core.Markt;

/// <summary>
/// Core purchase workflow.  It only talks to an <see cref="IMallPurchaseTransport"/>
/// supplied by the host, so this project cannot accidentally send live Mall
/// HTTP requests.  The transport is refreshed before every commit and the
/// resulting receipt is checked against the expected Meat/inventory deltas.
/// </summary>
public sealed class MallPurchaseGateway : IMallPurchaseGateway
{
    private readonly IMallPurchaseTransport _transport;
    private readonly IAccountExecutionLane _lane;
    private readonly MallPurchasePolicy _policy;
    private readonly TimeProvider _clock;

    public MallPurchaseGateway(
        IMallPurchaseTransport transport,
        MallPurchasePolicy? policy = null,
        IAccountExecutionLane? accountLane = null,
        TimeProvider? clock = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _policy = policy ?? new MallPurchasePolicy();
        _policy.Validate();
        _lane = accountLane ?? new SerialAccountLane();
        _clock = clock ?? TimeProvider.System;
    }

    public MallPurchaseGateway(
        IMallPurchaseTransport transport,
        IAccountExecutionLane accountLane,
        MallPurchasePolicy? policy = null,
        TimeProvider? clock = null)
        : this(transport, policy, accountLane, clock)
    {
    }

    public MallPurchasePolicy Policy => _policy;
    public IAccountExecutionLane AccountLane => _lane;

    public Task<MallPurchasePreview> PreviewAsync(
        MallPurchaseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _lane.ExecuteAsync(async token =>
        {
            var now = _clock.GetUtcNow();
            MallPurchaseObservation observation;
            try
            {
                request.Validate();
                observation = await _transport.RefreshAsync(request, token).ConfigureAwait(false);
                observation.Validate();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // A preview is a review object.  Transport failures are
                // represented as an abort, rather than being mistaken for a
                // ready purchase.
                var fallback = new MallPurchaseObservation(
                    new MallPurchaseOffer(
                        request.SellerId, request.StoreId, request.ItemId,
                        request.ExpectedUnitPrice, 0, now),
                    new MallPurchaseAccountState(
                        request.AccountId, 0, 0, request.ExpectedDailyLimit ?? 0,
                        null, now, sessionValid: false, isStale: true), now);
                return BuildPreview(request, fallback, now,
                    [MallPurchaseAbortReason.TransportFailure],
                    [exception.Message]);
            }

            return BuildPreview(request, observation, now);
        }, cancellationToken);
    }

    public Task<MallPurchaseRefreshResult> RefreshAsync(
        MallPurchasePreview preview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return _lane.ExecuteAsync(async token =>
        {
            var now = _clock.GetUtcNow();
            if (!TryGetRequest(preview, out var request, out var invalid))
            {
                return new MallPurchaseRefreshResult(preview, preview.Observation,
                    [invalid]);
            }

            MallPurchaseObservation fresh;
            try
            {
                fresh = await _transport.RefreshAsync(request, token).ConfigureAwait(false);
                fresh.Validate();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return new MallPurchaseRefreshResult(preview, preview.Observation,
                    [MallPurchaseAbortReason.TransportFailure], [exception.Message]);
            }

            var reasons = Evaluate(request, fresh, now).ToList();
            CompareExpected(preview.Observation, fresh, request, reasons);
            if (now > preview.ExpiresAt)
                Add(reasons, MallPurchaseAbortReason.PreviewExpired);

            return new MallPurchaseRefreshResult(preview, fresh, reasons);
        }, cancellationToken);
    }

    public Task<MallPurchaseCommitResult> CommitAsync(
        MallPurchasePreview preview,
        string? approvalToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return _lane.ExecuteAsync(token => CommitCoreAsync(preview, approvalToken, token), cancellationToken);
    }

    public MallPurchaseVerificationResult Verify(
        MallPurchasePreview preview,
        MallPurchaseObservation before,
        MallPurchaseObservation after,
        bool commitAcknowledged = true)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var reasons = new List<MallPurchaseAbortReason>();
        var messages = new List<string>();
        if (!commitAcknowledged)
            Add(reasons, MallPurchaseAbortReason.CommitRejected);

        if (!before.Account.SessionValid || !after.Account.SessionValid)
            Add(reasons, MallPurchaseAbortReason.SessionInvalid);
        if (before.Account.IsStale)
            Add(reasons, MallPurchaseAbortReason.StaleAccount);
        if (after.Account.IsStale)
            Add(reasons, MallPurchaseAbortReason.StaleAccount);

        CompareExpected(preview.Observation, before, preview.Request, reasons);

        var expectedMeat = before.Account.Meat - preview.TotalCost;
        if (after.Account.Meat != expectedMeat)
        {
            Add(reasons, MallPurchaseAbortReason.MeatVerificationFailed);
            Add(reasons, MallPurchaseAbortReason.MeatChanged);
            messages.Add($"Expected {expectedMeat} Meat after purchase but observed {after.Account.Meat}.");
        }

        var beforeQuantity = before.InventoryQuantity(preview.Request.ItemId);
        var afterQuantity = after.InventoryQuantity(preview.Request.ItemId);
        var delta = afterQuantity - beforeQuantity;
        if (delta < preview.Request.Quantity)
        {
            Add(reasons, MallPurchaseAbortReason.ItemNotReceived);
            Add(reasons, MallPurchaseAbortReason.InventoryVerificationFailed);
            messages.Add($"Expected {preview.Request.Quantity} new item(s) but observed {delta}.");
        }
        else if (delta != preview.Request.Quantity)
        {
            Add(reasons, MallPurchaseAbortReason.UnexpectedQuantity);
            Add(reasons, MallPurchaseAbortReason.InventoryVerificationFailed);
            messages.Add($"Expected exactly {preview.Request.Quantity} new item(s) but observed {delta}.");
        }

        var expectedDailySpent = before.Account.DailySpent + preview.TotalCost;
        if (after.Account.DailySpent != expectedDailySpent)
        {
            Add(reasons, MallPurchaseAbortReason.DailySpendChanged);
            messages.Add($"Expected daily spend {expectedDailySpent} but observed {after.Account.DailySpent}.");
        }

        if (reasons.Count > 0)
        {
            if (reasons.Any(x => x is MallPurchaseAbortReason.MeatVerificationFailed
                or MallPurchaseAbortReason.InventoryVerificationFailed
                or MallPurchaseAbortReason.ItemNotReceived
                or MallPurchaseAbortReason.UnexpectedQuantity))
                Add(reasons, MallPurchaseAbortReason.VerificationFailed);

            return new MallPurchaseVerificationResult
            {
                Status = MallPurchaseVerificationStatus.Aborted,
                AbortReasons = reasons,
                Messages = messages,
                Before = before,
                After = after
            };
        }

        return new MallPurchaseVerificationResult
        {
            Status = MallPurchaseVerificationStatus.Verified,
            Before = before,
            After = after
        };
    }

    // Convenience aliases for hosts that use method names from an approval
    // service rather than the gateway interface.
    public Task<MallPurchasePreview> CreatePreviewAsync(
        MallPurchaseRequest request,
        CancellationToken cancellationToken = default)
        => PreviewAsync(request, cancellationToken);

    public MallPurchasePreview Preview(
        MallPurchaseRequest request,
        CancellationToken cancellationToken = default)
        => PreviewAsync(request, cancellationToken).GetAwaiter().GetResult();

    public MallPurchaseRefreshResult Refresh(
        MallPurchasePreview preview,
        CancellationToken cancellationToken = default)
        => RefreshAsync(preview, cancellationToken).GetAwaiter().GetResult();

    public MallPurchaseCommitResult Commit(
        MallPurchasePreview preview,
        string? approvalToken = null,
        CancellationToken cancellationToken = default)
        => CommitAsync(preview, approvalToken, cancellationToken).GetAwaiter().GetResult();

    public Task<MallPurchaseRefreshResult> RefreshPreviewAsync(
        MallPurchasePreview preview,
        CancellationToken cancellationToken = default)
        => RefreshAsync(preview, cancellationToken);

    public MallPurchaseVerificationResult VerifyCommit(
        MallPurchasePreview preview,
        MallPurchaseObservation before,
        MallPurchaseObservation after,
        bool commitAcknowledged = true)
        => Verify(preview, before, after, commitAcknowledged);

    private async Task<MallPurchaseCommitResult> CommitCoreAsync(
        MallPurchasePreview preview,
        string? approvalToken,
        CancellationToken cancellationToken)
    {
        var actionId = preview.ActionId;
        var now = _clock.GetUtcNow();
        if (!TryGetRequest(preview, out var request, out var invalid))
            return Abort(actionId, [invalid], "Preview request is invalid.");

        var reasons = new List<MallPurchaseAbortReason>();
        var messages = new List<string>();
        if (now > preview.ExpiresAt)
            Add(reasons, MallPurchaseAbortReason.PreviewExpired);
        if (preview.RequiresApproval && string.IsNullOrWhiteSpace(approvalToken))
            Add(reasons, MallPurchaseAbortReason.ApprovalRequired);
        if (preview.DryRun || _policy.DryRun)
            Add(reasons, MallPurchaseAbortReason.DryRun);
        foreach (var reason in preview.AbortReasons) Add(reasons, reason);

        MallPurchaseObservation before;
        try
        {
            before = await _transport.RefreshAsync(request, cancellationToken).ConfigureAwait(false);
            before.Validate();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Add(reasons, MallPurchaseAbortReason.TransportFailure);
            messages.Add(exception.Message);
            return Abort(actionId, reasons, messages, before: null);
        }

        foreach (var reason in Evaluate(request, before, now)) Add(reasons, reason);
        CompareExpected(preview.Observation, before, request, reasons);
        if (reasons.Count > 0)
            return Abort(actionId, reasons, messages, before: before);

        MallPurchaseTransportReceipt receipt;
        try
        {
            receipt = await _transport.CommitAsync(
                new MallPurchaseTransportCommit(preview, before, approvalToken, now),
                cancellationToken).ConfigureAwait(false);
            receipt.Before.Validate();
            receipt.After.Validate();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Abort(actionId,
                [MallPurchaseAbortReason.TransportFailure],
                [exception.Message], before);
        }

        if (!receipt.Accepted)
        {
            return new MallPurchaseCommitResult
            {
                ActionId = actionId,
                Status = MallPurchaseVerificationStatus.Aborted,
                Accepted = false,
                Committed = false,
                AbortReasons = [MallPurchaseAbortReason.CommitRejected],
                Messages = string.IsNullOrWhiteSpace(receipt.Message)
                    ? Array.Empty<string>()
                    : [receipt.Message],
                Before = receipt.Before,
                After = receipt.After,
                Receipt = receipt
            };
        }

        var verified = Verify(preview, receipt.Before, receipt.After, commitAcknowledged: true);
        return new MallPurchaseCommitResult
        {
            ActionId = actionId,
            Status = verified.Status,
            Accepted = true,
            Committed = true,
            AbortReasons = verified.AbortReasons,
            Messages = verified.Messages,
            Before = verified.Before,
            After = verified.After,
            Receipt = receipt
        };
    }

    private MallPurchasePreview BuildPreview(
        MallPurchaseRequest request,
        MallPurchaseObservation observation,
        DateTimeOffset now,
        IReadOnlyList<MallPurchaseAbortReason>? additionalReasons = null,
        IReadOnlyList<string>? warnings = null)
    {
        var reasons = new List<MallPurchaseAbortReason>();
        if (additionalReasons is not null)
            foreach (var reason in additionalReasons) Add(reasons, reason);
        try
        {
            request.Validate();
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            Add(reasons, MallPurchaseAbortReason.InvalidRequest);
        }

        foreach (var reason in Evaluate(request, observation, now)) Add(reasons, reason);
        return new MallPurchasePreview
        {
            ActionId = Guid.NewGuid().ToString("N"),
            Request = request,
            Observation = observation,
            CreatedAt = now,
            ExpiresAt = now + _policy.PreviewLifetime,
            RequiresApproval = _policy.RequireApproval,
            DryRun = _policy.DryRun,
            AbortReasons = reasons,
            Warnings = warnings ?? Array.Empty<string>()
        };
    }

    private IReadOnlyList<MallPurchaseAbortReason> Evaluate(
        MallPurchaseRequest request,
        MallPurchaseObservation observation,
        DateTimeOffset now)
    {
        var reasons = new List<MallPurchaseAbortReason>();
        try
        {
            observation.Validate();
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            Add(reasons, MallPurchaseAbortReason.MissingOffer);
            return reasons;
        }

        // Invalid request values are handled as a refusal and never reach the
        // transport commit boundary.
        try
        {
            request.Validate();
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            Add(reasons, MallPurchaseAbortReason.InvalidRequest);
            return reasons;
        }

        var offer = observation.Offer;
        if (!string.Equals(request.AccountId, observation.Account.AccountId, StringComparison.OrdinalIgnoreCase))
            Add(reasons, MallPurchaseAbortReason.AccountChanged);
        if (!string.Equals(request.SellerId, offer.SellerId, StringComparison.Ordinal))
            Add(reasons, MallPurchaseAbortReason.SellerChanged);
        if (!string.Equals(request.StoreId, offer.StoreId, StringComparison.Ordinal))
            Add(reasons, MallPurchaseAbortReason.StoreChanged);
        if (!string.Equals(request.ItemId, offer.ItemId, StringComparison.Ordinal))
            Add(reasons, MallPurchaseAbortReason.ItemChanged);
        if (request.ExpectedUnitPrice != offer.UnitPrice)
            Add(reasons, MallPurchaseAbortReason.PriceChanged);
        if (offer.Quantity <= 0)
            Add(reasons, MallPurchaseAbortReason.SoldOut);
        else if (offer.Quantity < request.Quantity)
            Add(reasons, MallPurchaseAbortReason.QuantityChanged);
        if (offer.PurchaseLimit is { } purchaseLimit && purchaseLimit < request.Quantity)
            Add(reasons, MallPurchaseAbortReason.PurchaseLimitExceeded);
        if (request.ExpectedDailyLimit is { } expectedLimit && expectedLimit != observation.Account.DailyLimit)
            Add(reasons, MallPurchaseAbortReason.DailyLimitChanged);
        if (!observation.Account.SessionValid)
            Add(reasons, MallPurchaseAbortReason.SessionInvalid);
        if (observation.Account.IsStale || offer.ObservedAt < now - _policy.MaxObservationAge
            || observation.ObservedAt < now - _policy.MaxObservationAge)
            Add(reasons, offer.ObservedAt < now - _policy.MaxObservationAge
                ? MallPurchaseAbortReason.StaleOffer
                : MallPurchaseAbortReason.StaleAccount);

        var total = request.TotalCost;
        if (observation.Account.Meat < total)
            Add(reasons, MallPurchaseAbortReason.InsufficientMeat);
        if (observation.Account.DailySpent + total > observation.Account.DailyLimit)
            Add(reasons, MallPurchaseAbortReason.DailyLimitExceeded);
        if (_policy.MaxDailySpend is { } maxDaily && observation.Account.DailySpent + total > maxDaily)
            Add(reasons, MallPurchaseAbortReason.DailyLimitExceeded);
        if (_policy.MaxMeatPerPurchase is { } maxPurchase && total > maxPurchase)
            Add(reasons, MallPurchaseAbortReason.PerPurchaseLimitExceeded);
        if (_policy.MaxQuantityPerPurchase is { } maxQuantity && request.Quantity > maxQuantity)
            Add(reasons, MallPurchaseAbortReason.QuantityLimitExceeded);
        return reasons;
    }

    private static void CompareExpected(
        MallPurchaseObservation expected,
        MallPurchaseObservation actual,
        MallPurchaseRequest request,
        ICollection<MallPurchaseAbortReason> reasons)
    {
        if (!string.Equals(expected.Account.AccountId, actual.Account.AccountId, StringComparison.OrdinalIgnoreCase))
            Add(reasons, MallPurchaseAbortReason.AccountChanged);
        if (expected.Account.Meat != actual.Account.Meat)
            Add(reasons, MallPurchaseAbortReason.MeatChanged);
        if (expected.Account.DailySpent != actual.Account.DailySpent)
            Add(reasons, MallPurchaseAbortReason.DailySpendChanged);
        if (expected.Account.DailyLimit != actual.Account.DailyLimit)
            Add(reasons, MallPurchaseAbortReason.DailyLimitChanged);
        if (expected.InventoryQuantity(request.ItemId) != actual.InventoryQuantity(request.ItemId))
            Add(reasons, MallPurchaseAbortReason.InventoryChanged);
        if (!string.Equals(expected.Offer.SellerId, actual.Offer.SellerId, StringComparison.Ordinal))
            Add(reasons, MallPurchaseAbortReason.SellerChanged);
        if (!string.Equals(expected.Offer.StoreId, actual.Offer.StoreId, StringComparison.Ordinal))
            Add(reasons, MallPurchaseAbortReason.StoreChanged);
        if (!string.Equals(expected.Offer.ItemId, actual.Offer.ItemId, StringComparison.Ordinal))
            Add(reasons, MallPurchaseAbortReason.ItemChanged);
        if (expected.Offer.UnitPrice != actual.Offer.UnitPrice)
            Add(reasons, MallPurchaseAbortReason.PriceChanged);
        if (expected.Offer.Quantity != actual.Offer.Quantity)
            Add(reasons, actual.Offer.Quantity <= 0
                ? MallPurchaseAbortReason.SoldOut
                : MallPurchaseAbortReason.QuantityChanged);
        if (expected.Offer.PurchaseLimit != actual.Offer.PurchaseLimit)
            Add(reasons, MallPurchaseAbortReason.PurchaseLimitChanged);
    }

    private static bool TryGetRequest(
        MallPurchasePreview preview,
        out MallPurchaseRequest request,
        out MallPurchaseAbortReason reason)
    {
        request = preview.Request;
        try
        {
            request.Validate();
            reason = MallPurchaseAbortReason.None;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            reason = MallPurchaseAbortReason.InvalidRequest;
            return false;
        }
    }

    private static MallPurchaseCommitResult Abort(
        string actionId,
        IReadOnlyList<MallPurchaseAbortReason> reasons,
        string message,
        MallPurchaseObservation? before = null)
        => Abort(actionId, reasons, string.IsNullOrWhiteSpace(message) ? Array.Empty<string>() : [message], before);

    private static MallPurchaseCommitResult Abort(
        string actionId,
        IReadOnlyList<MallPurchaseAbortReason> reasons,
        IReadOnlyList<string> messages,
        MallPurchaseObservation? before = null)
        => new()
        {
            ActionId = actionId,
            Status = MallPurchaseVerificationStatus.Aborted,
            Committed = false,
            Accepted = false,
            AbortReasons = reasons,
            Messages = messages,
            Before = before
        };

    private static void Add(ICollection<MallPurchaseAbortReason> reasons, MallPurchaseAbortReason reason)
    {
        if (reason != MallPurchaseAbortReason.None && !reasons.Contains(reason)) reasons.Add(reason);
    }
}

/// <summary>Static verifier facade for hosts that keep a gateway stateless.</summary>
public static class MallPurchaseVerifier
{
    public static MallPurchaseVerificationResult Verify(
        MallPurchasePreview preview,
        MallPurchaseObservation before,
        MallPurchaseObservation after,
        bool commitAcknowledged = true,
        TimeProvider? clock = null)
    {
        var gateway = new MallPurchaseGateway(
            new RejectingMallPurchaseTransport(),
            new MallPurchasePolicy { DryRun = false, RequireApproval = false },
            new SerialAccountLane(preview.Request.AccountId),
            clock);
        return gateway.Verify(preview, before, after, commitAcknowledged);
    }

    private sealed class RejectingMallPurchaseTransport : IMallPurchaseTransport
    {
        public Task<MallPurchaseObservation> RefreshAsync(MallPurchaseRequest request, CancellationToken cancellationToken = default)
            => Task.FromException<MallPurchaseObservation>(new InvalidOperationException("Verifier has no transport."));

        public Task<MallPurchaseTransportReceipt> CommitAsync(MallPurchaseTransportCommit request, CancellationToken cancellationToken = default)
            => Task.FromException<MallPurchaseTransportReceipt>(new InvalidOperationException("Verifier has no transport."));
    }
}
