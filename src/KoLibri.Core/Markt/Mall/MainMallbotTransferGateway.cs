using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Markt;

/// <summary>
/// The transport used by the Main/Mallbot workflow.  Implementations own the
/// account sessions and are deliberately separate from the approval service:
/// building a preview and verifying a receipt never sends a mutating request.
/// </summary>
public interface IMainMallbotTransferTransport
{
    /// <summary>Reads both accounts in the request's source/destination order.</summary>
    Task<TransferObservation> RefreshAsync(
        MainMallbotTransferRefresh request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs one explicitly requested transfer and returns fresh reads from
    /// both accounts.  The gateway calls this only after all approval checks.
    /// </summary>
    Task<MainMallbotTransferReceipt> CommitAsync(
        MainMallbotTransferTransportCommit request,
        CancellationToken cancellationToken = default);
}

/// <summary>Stable role/profile binding supplied to a transfer adapter.</summary>
public sealed record MainMallbotTransferAccountBinding
{
    public MainMallbotTransferAccountBinding()
    {
    }

    public MainMallbotTransferAccountBinding(AccountRole role, string accountId,
        string? displayName = null)
    {
        Role = role;
        AccountId = accountId;
        DisplayName = displayName;
    }

    public AccountRole Role { get; init; }
    public string AccountId { get; init; } = string.Empty;
    public string? DisplayName { get; init; }

    public string CanonicalName => Role.ToString();

    public bool Matches(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return false;
        var value = reference.Trim();
        return string.Equals(value, CanonicalName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, AccountId, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(DisplayName)
                && string.Equals(value, DisplayName, StringComparison.OrdinalIgnoreCase));
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(AccountId))
            throw new ArgumentException("A transfer account id is required.", nameof(AccountId));
    }
}

/// <summary>Read request passed from the workflow to a transport.</summary>
public sealed record MainMallbotTransferRefresh
{
    public MainMallbotTransferRefresh()
    {
    }

    public MainMallbotTransferRefresh(TransferRequest request,
        MainMallbotTransferAccountBinding source,
        MainMallbotTransferAccountBinding destination)
    {
        Request = request;
        Source = source;
        Destination = destination;
    }

    public TransferRequest Request { get; init; } = null!;
    public MainMallbotTransferAccountBinding Source { get; init; } = null!;
    public MainMallbotTransferAccountBinding Destination { get; init; } = null!;
}

/// <summary>Low-level mutation request.  It contains no credentials.</summary>
public sealed record MainMallbotTransferTransportCommit
{
    public MainMallbotTransferTransportCommit()
    {
    }

    public MainMallbotTransferTransportCommit(TransferPreview preview,
        TransferObservation before, string approvalToken, DateTimeOffset committedAt,
        MainMallbotTransferAccountBinding? source = null,
        MainMallbotTransferAccountBinding? destination = null)
    {
        Preview = preview;
        Before = before;
        ApprovalToken = approvalToken;
        CommittedAt = committedAt;
        Source = source;
        Destination = destination;
    }

    public TransferPreview Preview { get; init; } = null!;
    public TransferObservation Before { get; init; } = null!;
    public string ApprovalToken { get; init; } = string.Empty;
    public DateTimeOffset CommittedAt { get; init; }
    public MainMallbotTransferAccountBinding? Source { get; init; }
    public MainMallbotTransferAccountBinding? Destination { get; init; }
}

/// <summary>Transport acknowledgement plus fresh post-commit observations.</summary>
public sealed record MainMallbotTransferReceipt
{
    public MainMallbotTransferReceipt()
    {
    }

    public MainMallbotTransferReceipt(bool accepted, TransferObservation before,
        TransferObservation after, string? message = null,
        IReadOnlyList<TransferItemCommit>? itemResults = null,
        bool sessionValid = true, DateTimeOffset? committedAt = null)
    {
        Accepted = accepted;
        Before = before;
        After = after;
        Message = message;
        ItemResults = itemResults ?? Array.Empty<TransferItemCommit>();
        SessionValid = sessionValid;
        CommittedAt = committedAt ?? DateTimeOffset.UtcNow;
    }

    public bool Accepted { get; init; }
    public bool Committed => Accepted;
    public TransferObservation Before { get; init; } = null!;
    public TransferObservation After { get; init; } = null!;
    public string? Message { get; init; }
    public IReadOnlyList<TransferItemCommit> ItemResults { get; init; }
        = Array.Empty<TransferItemCommit>();
    public bool SessionValid { get; init; } = true;
    public DateTimeOffset CommittedAt { get; init; }
}

/// <summary>Whether a transfer is sent through KoL's trade or gift flow.</summary>
public enum MainMallbotTransferMode
{
    Gift,
    Trade
}

/// <summary>
/// Guardrails specific to the Main/Mallbot adapter.  The Core approval policy
/// still controls limits, expiry, reservations and idempotency.
/// </summary>
public sealed record MainMallbotTransferPolicy
{
    public MainMallbotTransferMode Mode { get; init; } = MainMallbotTransferMode.Gift;
    public bool RequireItemCatalog { get; init; } = true;
    public bool DryRun { get; init; } = true;
    public bool RequireApproval { get; init; } = true;

    public void Validate()
    {
        if (!Enum.IsDefined(Mode))
            throw new ArgumentOutOfRangeException(nameof(Mode));
    }

    public TransferApprovalPolicy ApprovalPolicy(TransferApprovalPolicy? basePolicy = null)
    {
        var policy = basePolicy ?? new TransferApprovalPolicy();
        policy.Validate();
        return policy with { DryRun = DryRun, RequireApproval = RequireApproval };
    }
}

/// <summary>
/// Main/Mallbot transfer workflow over an explicit transport.  The class owns
/// role binding and item eligibility checks, while TransferApprovalService
/// remains the single source of truth for expected-state and verification.
/// </summary>
public class MainMallbotTransferGateway
{
    private readonly IMainMallbotTransferTransport _transport;
    private readonly MainMallbotTransferAccountBinding _main;
    private readonly MainMallbotTransferAccountBinding _mallbot;
    private readonly ItemMaster? _catalog;
    private readonly MainMallbotTransferPolicy _transferPolicy;
    private readonly TransferApprovalPolicy _approvalPolicy;
    private readonly ITransferIdempotencyStore? _idempotency;
    private readonly ITransferReservationStore? _reservations;
    private readonly TimeProvider _clock;

    public MainMallbotTransferGateway(
        IMainMallbotTransferTransport transport,
        MainMallbotTransferAccountBinding main,
        MainMallbotTransferAccountBinding mallbot,
        ItemMaster? catalog = null,
        MainMallbotTransferPolicy? transferPolicy = null,
        TransferApprovalPolicy? approvalPolicy = null,
        ITransferIdempotencyStore? idempotency = null,
        ITransferReservationStore? reservations = null,
        TimeProvider? clock = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _main = main ?? throw new ArgumentNullException(nameof(main));
        _mallbot = mallbot ?? throw new ArgumentNullException(nameof(mallbot));
        if (_main.Role != AccountRole.Main)
            throw new ArgumentException("The first transfer binding must be Main.", nameof(main));
        if (_mallbot.Role != AccountRole.Mallbot)
            throw new ArgumentException("The second transfer binding must be Mallbot.", nameof(mallbot));
        if (string.Equals(_main.AccountId, _mallbot.AccountId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Main and Mallbot must use different account ids.");
        _main.Validate();
        _mallbot.Validate();
        _catalog = catalog;
        var explicitTransferPolicy = transferPolicy is not null;
        _transferPolicy = transferPolicy ?? new MainMallbotTransferPolicy();
        _transferPolicy.Validate();
        _approvalPolicy = explicitTransferPolicy
            ? _transferPolicy.ApprovalPolicy(approvalPolicy)
            : (approvalPolicy ?? new TransferApprovalPolicy());
        _approvalPolicy.Validate();
        _idempotency = idempotency;
        _reservations = reservations;
        _clock = clock ?? TimeProvider.System;
    }

    public MainMallbotTransferAccountBinding Main => _main;
    public MainMallbotTransferAccountBinding Mallbot => _mallbot;
    public MainMallbotTransferPolicy TransferPolicy => _transferPolicy;
    public TransferApprovalPolicy ApprovalPolicy => _approvalPolicy;

    /// <summary>Builds a preview after a fresh transport read.</summary>
    public async Task<TransferPreview> PreviewAsync(
        TransferRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var bound = BindRequest(request, out var source, out var destination);
        if (source is null || destination is null)
        {
            var invalidPreview = TransferApprovalService.Preview(
                bound, EmptyObservation(bound), _approvalPolicy,
                _clock.GetUtcNow(), reservations: _reservations);
            return invalidPreview with
            {
                AbortReasons = AddEligibilityReason(invalidPreview, bound, source, destination)
            };
        }

        TransferObservation observation;
        try
        {
            observation = await _transport.RefreshAsync(
                new MainMallbotTransferRefresh(bound, source, destination), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var fallback = EmptyObservation(bound);
            var failed = TransferApprovalService.Preview(bound, fallback, _approvalPolicy,
                _clock.GetUtcNow(), reservations: _reservations);
            return failed with
            {
                AbortReasons = AddReason(failed.AbortReasons, TransferAbortReason.SessionInvalid),
                Warnings = failed.Warnings.Concat([exception.Message]).ToArray()
            };
        }

        var preview = TransferApprovalService.Preview(bound, observation, _approvalPolicy,
            _clock.GetUtcNow(), reservations: _reservations);
        return preview with
        {
            AbortReasons = AddEligibilityReason(preview, bound, source, destination)
        };
    }

    /// <summary>Builds a preview from an already-fetched observation.</summary>
    public TransferPreview Preview(
        TransferRequest request,
        TransferObservation observation,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(observation);
        var bound = BindRequest(request, out var source, out var destination);
        var preview = TransferApprovalService.Preview(bound, observation, _approvalPolicy,
            now ?? _clock.GetUtcNow(), reservations: _reservations);
        return preview with { AbortReasons = AddEligibilityReason(preview, bound, source, destination) };
    }

    public Task<TransferApproval> ApproveAsync(
        TransferPreview preview,
        string? approvedBy = null,
        string? approvalToken = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(preview);
        return Task.FromResult(TransferApprovalService.Approve(
            preview, approvedBy, approvalToken, _clock.GetUtcNow()));
    }

    /// <summary>
    /// Explicitly executes one approved transfer.  This is the only gateway
    /// method that may call the transport's mutating method.
    /// </summary>
    public async Task<TransferCommitResult> CommitAsync(
        TransferPreview preview,
        TransferApproval? approval = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        cancellationToken.ThrowIfCancellationRequested();
        var now = _clock.GetUtcNow();
        var approvalToken = approval?.ApprovalToken ?? preview.ApprovalToken;
        var preflightReasons = new List<TransferAbortReason>();
        if (now >= preview.ExpiresAt) Add(preflightReasons, TransferAbortReason.PreviewExpired);
        if (preview.RequiresApproval && (approval is null || !approval.IsValid(now)
            || !string.Equals(approval.ActionId, preview.ActionId, StringComparison.Ordinal)))
            Add(preflightReasons, TransferAbortReason.MissingApproval);
        if (preview.DryRun || _approvalPolicy.DryRun)
            Add(preflightReasons, TransferAbortReason.DryRun);
        foreach (var reason in preview.AbortReasons) Add(preflightReasons, reason);
        if (_reservations is not null)
        {
            if (!_reservations.TryGet(preview.Reservation.ReservationId, out var reservation) || reservation is null)
                Add(preflightReasons, TransferAbortReason.ReservationUnavailable);
            else if (!reservation.IsActive(now))
                Add(preflightReasons, reservation.Status == TransferReservationStatus.Consumed
                    ? TransferAbortReason.ReservationConsumed : TransferAbortReason.ReservationExpired);
        }
        else if (!preview.Reservation.IsActive(now))
            Add(preflightReasons, TransferAbortReason.ReservationExpired);

        // Idempotency must be checked before the transport can mutate.  A
        // replay returns the recorded result and an in-progress operation is
        // refused without issuing another POST.
        var idempotencyKey = preview.Request.IdempotencyKey;
        if (_idempotency is not null && !string.IsNullOrWhiteSpace(idempotencyKey)
            && _idempotency.TryGet(idempotencyKey, out var existingRecord) && existingRecord is not null)
        {
            if (!string.Equals(existingRecord.Fingerprint, preview.Request.Fingerprint(), StringComparison.Ordinal))
                Add(preflightReasons, TransferAbortReason.IdempotencyConflict);
            else if (existingRecord.Result is { } replay)
                return replay with { IdempotencyReplayed = true, OperationStatus = TransferOperationStatus.Replayed };
            else
                Add(preflightReasons, TransferAbortReason.DuplicateOperation);
        }

        TryResolve(preview.Request.SourceAccount, out var source);
        TryResolve(preview.Request.DestinationAccount, out var destination);
        if (source is null || destination is null)
            Add(preflightReasons, TransferAbortReason.UnsupportedAccount);

        TransferObservation before;
        try
        {
            if (source is null || destination is null)
                before = EmptyObservation(preview.Request);
            else
                before = await _transport.RefreshAsync(
                    new MainMallbotTransferRefresh(preview.Request, source, destination), cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failure(preview, before: null,
                AddReason(preflightReasons, TransferAbortReason.SessionInvalid), [exception.Message]);
        }

        // Verify expected state before allowing a transport POST.  This is a
        // read-only check; TransferApprovalService performs the complete
        // delta verification after the transport returns.
        AddExpectedStateReasons(preview, before, preflightReasons);
        if (preflightReasons.Count > 0)
            return Failure(preview, before, preflightReasons, []);

        if (preview.RequiresApproval && string.IsNullOrWhiteSpace(approvalToken))
            return Failure(preview, before, [TransferAbortReason.MissingApproval], []);

        var operationStarted = false;
        if (_idempotency is not null && !string.IsNullOrWhiteSpace(idempotencyKey))
        {
            if (!_idempotency.TryBegin(idempotencyKey, preview.Request.Fingerprint(), preview.ActionId,
                now, out var begunExisting))
            {
                if (begunExisting is not null && !string.Equals(begunExisting.Fingerprint,
                    preview.Request.Fingerprint(), StringComparison.Ordinal))
                    return Failure(preview, before, [TransferAbortReason.IdempotencyConflict], []);
                if (begunExisting?.Result is { } replay)
                    return replay with { IdempotencyReplayed = true, OperationStatus = TransferOperationStatus.Replayed };
                return Failure(preview, before, [TransferAbortReason.DuplicateOperation], []);
            }
            operationStarted = true;
        }

        MainMallbotTransferReceipt receipt;
        try
        {
            receipt = await _transport.CommitAsync(
                new MainMallbotTransferTransportCommit(preview, before, approvalToken ?? string.Empty, now,
                    source, destination),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failed = Failure(preview, before,
                [TransferAbortReason.CommitRejected, TransferAbortReason.ReconciliationRequired],
                [exception.Message]);
            if (operationStarted && idempotencyKey is not null)
                _idempotency!.Complete(idempotencyKey, failed, now);
            return failed;
        }

        var commit = new TransferCommitRequest
        {
            Preview = preview,
            ActionId = preview.ActionId,
            IdempotencyKey = preview.Request.IdempotencyKey,
            ApprovalToken = approvalToken,
            Before = receipt.Before,
            After = receipt.After,
            CommitAcknowledged = receipt.Accepted,
            SessionValid = receipt.SessionValid,
            CommittedAt = receipt.CommittedAt,
            ItemResults = receipt.ItemResults
        };
        var result = TransferApprovalService.VerifyCommit(commit, _reservations);
        if (operationStarted && idempotencyKey is not null)
            _idempotency!.Complete(idempotencyKey, result, receipt.CommittedAt);
        if (!receipt.Accepted && receipt.Message is { Length: > 0 })
            result = result with { Messages = result.Messages.Concat([receipt.Message]).ToArray() };
        return result;
    }

    /// <summary>Verification-only path; it cannot call the transport.</summary>
    public Task<TransferVerificationResult> VerifyAsync(
        TransferVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(TransferApprovalService.Verify(request, _reservations));
    }

    /// <summary>Compatibility overload for hosts that already performed a commit.</summary>
    public Task<TransferCommitResult> CommitAsync(
        TransferCommitRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(TransferApprovalService.Commit(request, _idempotency, _reservations));
    }

    private TransferRequest BindRequest(
        TransferRequest request,
        out MainMallbotTransferAccountBinding? source,
        out MainMallbotTransferAccountBinding? destination)
    {
        source = TryResolve(request.SourceAccount, out var sourceBinding) ? sourceBinding : null;
        destination = TryResolve(request.DestinationAccount, out var destinationBinding) ? destinationBinding : null;
        return request with
        {
            SourceAccount = source?.CanonicalName ?? request.SourceAccount,
            DestinationAccount = destination?.CanonicalName ?? request.DestinationAccount
        };
    }

    private bool TryResolve(string? reference, out MainMallbotTransferAccountBinding? binding)
    {
        if (_main.Matches(reference))
        {
            binding = _main;
            return true;
        }
        if (_mallbot.Matches(reference))
        {
            binding = _mallbot;
            return true;
        }
        binding = null;
        return false;
    }

    private IReadOnlyList<TransferAbortReason> AddEligibilityReason(
        TransferPreview preview,
        TransferRequest request,
        MainMallbotTransferAccountBinding? source,
        MainMallbotTransferAccountBinding? destination)
    {
        var reasons = preview.AbortReasons.ToList();
        if (source is null || destination is null || source.Role == destination.Role)
            Add(reasons, TransferAbortReason.UnsupportedAccount);

        if (request.Items is null || request.Items.Count == 0)
            return reasons;
        foreach (var itemId in request.Items.Keys)
        {
            if (string.IsNullOrWhiteSpace(itemId) || !itemId.Trim().All(char.IsDigit))
            {
                Add(reasons, TransferAbortReason.InvalidItem);
                continue;
            }
            if (_catalog is null)
            {
                if (_transferPolicy.RequireItemCatalog)
                    Add(reasons, TransferAbortReason.InvalidItem);
                continue;
            }
            if (!_catalog.TryGet(itemId, out var item))
            {
                Add(reasons, TransferAbortReason.InvalidItem);
                continue;
            }
            var allowed = _transferPolicy.Mode == MainMallbotTransferMode.Gift
                ? item.IsGiftTransferable
                : item.IsMallTradeable;
            if (!allowed)
                Add(reasons, TransferAbortReason.InvalidItem);
        }
        return reasons;
    }

    private static TransferObservation EmptyObservation(TransferRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var source = request.SourceAccount;
        var destination = request.DestinationAccount;
        return new TransferObservation
        {
            SourceInventory = new KoLInventorySnapshot(Array.Empty<KoLInventoryItem>(), now, IsStale: true),
            DestinationInventory = new KoLInventorySnapshot(Array.Empty<KoLInventoryItem>(), now, IsStale: true),
            SourceAccount = source,
            DestinationAccount = destination,
            ObservedAt = now
        };
    }

    private static TransferCommitResult Failure(
        TransferPreview preview,
        TransferObservation? before,
        IReadOnlyList<TransferAbortReason> reasons,
        IReadOnlyList<string> messages)
        => new()
        {
            ActionId = preview.ActionId,
            IdempotencyKey = preview.Request.IdempotencyKey,
            Status = TransferVerificationStatus.Aborted,
            OperationStatus = TransferOperationStatus.Aborted,
            Committed = false,
            AbortReasons = reasons,
            Messages = messages,
            Before = before
        };

    private static IReadOnlyList<TransferAbortReason> AddReason(
        IReadOnlyList<TransferAbortReason> reasons, TransferAbortReason reason)
    {
        var result = reasons.ToList();
        Add(result, reason);
        return result;
    }

    private static void Add(ICollection<TransferAbortReason> reasons, TransferAbortReason reason)
    {
        if (!reasons.Contains(reason))
            reasons.Add(reason);
    }

    private static void AddExpectedStateReasons(
        TransferPreview preview,
        TransferObservation before,
        ICollection<TransferAbortReason> reasons)
    {
        if (preview.Expected.SourceAccount.Length > 0 && before.SourceAccount is not null
            && !string.Equals(preview.Expected.SourceAccount, before.SourceAccount, StringComparison.OrdinalIgnoreCase))
            Add(reasons, TransferAbortReason.SourceStateChanged);
        if (preview.Expected.DestinationAccount.Length > 0 && before.DestinationAccount is not null
            && !string.Equals(preview.Expected.DestinationAccount, before.DestinationAccount, StringComparison.OrdinalIgnoreCase))
            Add(reasons, TransferAbortReason.DestinationStateChanged);
        foreach (var (itemId, expectedQuantity) in preview.Expected.SourceQuantities)
            if (before.SourceQuantity(itemId) != expectedQuantity)
                Add(reasons, TransferAbortReason.ExpectedStateChanged);
        foreach (var (itemId, expectedQuantity) in preview.Expected.DestinationQuantities)
            if (before.DestinationQuantity(itemId) != expectedQuantity)
                Add(reasons, TransferAbortReason.ExpectedStateChanged);
        if (preview.Expected.SourceMeat is { } sourceMeat && before.SourceStatus?.Meat is { } currentSourceMeat
            && sourceMeat != currentSourceMeat)
            Add(reasons, TransferAbortReason.ExpectedStateChanged);
        if (preview.Expected.DestinationMeat is { } destinationMeat && before.DestinationStatus?.Meat is { } currentDestinationMeat
            && destinationMeat != currentDestinationMeat)
            Add(reasons, TransferAbortReason.ExpectedStateChanged);
        if (before.IsStale)
        {
            if (before.SourceInventory.IsStale || before.SourceStatus?.IsStale == true)
                Add(reasons, TransferAbortReason.StaleSource);
            if (before.DestinationInventory.IsStale || before.DestinationStatus?.IsStale == true)
                Add(reasons, TransferAbortReason.StaleDestination);
        }
    }
}

/// <summary>
/// Deterministic fixture transport.  It models the two account inventories and
/// Meat balances and never creates an HTTP request.
/// </summary>
public sealed class FixtureMainMallbotTransferTransport : IMainMallbotTransferTransport
{
    private readonly object _gate = new();
    private TransferObservation _mainToMallbot;
    private readonly Queue<TransferObservation> _refreshes = new();
    private bool _rejectNextCommit;
    private string? _rejectMessage;

    public FixtureMainMallbotTransferTransport(TransferObservation initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _mainToMallbot = NormalizeMainToMallbot(initial);
    }

    public int RefreshCount { get; private set; }
    public int CommitCount { get; private set; }
    public int HttpRequestCount => 0;
    public bool UsedHttp => false;
    public TransferObservation State
    {
        get
        {
            lock (_gate)
                return Clone(_mainToMallbot);
        }
    }

    public void EnqueueRefresh(TransferObservation observation)
    {
        lock (_gate)
            _refreshes.Enqueue(NormalizeMainToMallbot(observation));
    }

    public void SetState(TransferObservation observation)
    {
        lock (_gate)
            _mainToMallbot = NormalizeMainToMallbot(observation);
    }

    public void RejectNextCommit(string? message = null)
    {
        lock (_gate)
        {
            _rejectNextCommit = true;
            _rejectMessage = message;
        }
    }

    public Task<TransferObservation> RefreshAsync(
        MainMallbotTransferRefresh request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            RefreshCount++;
            if (_refreshes.Count > 0)
                _mainToMallbot = _refreshes.Dequeue();
            return Task.FromResult(Orient(Clone(_mainToMallbot), request.Source.Role,
                request.Destination.Role));
        }
    }

    public Task<MainMallbotTransferReceipt> CommitAsync(
        MainMallbotTransferTransportCommit request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            CommitCount++;
            var sourceRole = request.Preview.Request.SourceRole ?? AccountRole.Main;
            var destinationRole = request.Preview.Request.DestinationRole ?? AccountRole.Mallbot;
            var before = Orient(Clone(_mainToMallbot), sourceRole, destinationRole);
            if (request.Preview.DryRun)
                return Task.FromResult(new MainMallbotTransferReceipt(false, before, before,
                    "Fixture transfer is in dry-run mode."));
            if (_rejectNextCommit)
            {
                _rejectNextCommit = false;
                return Task.FromResult(new MainMallbotTransferReceipt(false, before, before, _rejectMessage));
            }
            if (!string.Equals(request.ApprovalToken, request.ApprovalToken.Trim(), StringComparison.Ordinal)
                || (request.Preview.RequiresApproval && string.IsNullOrWhiteSpace(request.ApprovalToken)))
                return Task.FromResult(new MainMallbotTransferReceipt(false, before, before, "Approval token is required."));
            if (!Matches(request.Before, before, request.Preview.Request))
                return Task.FromResult(new MainMallbotTransferReceipt(false, before, before, "Fixture state changed."));

            var canonical = Clone(_mainToMallbot);
            var source = sourceRole == AccountRole.Main ? canonical.SourceInventory : canonical.DestinationInventory;
            var destination = destinationRole == AccountRole.Main ? canonical.SourceInventory : canonical.DestinationInventory;
            var sourceItems = source.Items.ToDictionary(x => x.ItemId, x => x, StringComparer.Ordinal);
            var destinationItems = destination.Items.ToDictionary(x => x.ItemId, x => x, StringComparer.Ordinal);
            var results = new List<TransferItemCommit>();
            foreach (var (itemId, quantity) in request.Preview.Request.Items)
            {
                sourceItems.TryGetValue(itemId, out var sourceItem);
                var available = sourceItem?.Quantity ?? 0;
                var moved = Math.Min(quantity, available);
                sourceItems[itemId] = new KoLInventoryItem(itemId, sourceItem?.Name ?? itemId, available - moved);
                destinationItems.TryGetValue(itemId, out var destinationItem);
                destinationItems[itemId] = new KoLInventoryItem(itemId, destinationItem?.Name ?? sourceItem?.Name ?? itemId,
                    (destinationItem?.Quantity ?? 0) + moved);
                results.Add(new TransferItemCommit
                {
                    ItemId = itemId,
                    RequestedQuantity = quantity,
                    TransferredQuantity = moved,
                    Acknowledged = moved == quantity,
                    Error = moved == quantity ? null : "Fixture source inventory was insufficient."
                });
            }
            var sourceStatus = sourceRole == AccountRole.Main ? canonical.SourceStatus : canonical.DestinationStatus;
            var destinationStatus = destinationRole == AccountRole.Main ? canonical.SourceStatus : canonical.DestinationStatus;
            if (request.Preview.Request.Meat > 0 && sourceStatus is not null && destinationStatus is not null)
            {
                sourceStatus = sourceStatus with { Meat = sourceStatus.Meat - request.Preview.Request.Meat };
                destinationStatus = destinationStatus with { Meat = destinationStatus.Meat + request.Preview.Request.Meat };
            }
            var updatedSourceInventory = new KoLInventorySnapshot(sourceItems.Values.Where(x => x.Quantity > 0).ToArray(), request.CommittedAt);
            var updatedDestinationInventory = new KoLInventorySnapshot(destinationItems.Values.Where(x => x.Quantity > 0).ToArray(), request.CommittedAt);
            canonical = sourceRole == AccountRole.Main
                ? canonical with
                {
                    SourceInventory = updatedSourceInventory,
                    DestinationInventory = updatedDestinationInventory,
                    SourceStatus = sourceStatus,
                    DestinationStatus = destinationStatus,
                    ObservedAt = request.CommittedAt
                }
                : canonical with
                {
                    SourceInventory = updatedDestinationInventory,
                    DestinationInventory = updatedSourceInventory,
                    SourceStatus = destinationStatus,
                    DestinationStatus = sourceStatus,
                    ObservedAt = request.CommittedAt
                };
            // canonical is always retained Main -> Mallbot.
            _mainToMallbot = sourceRole == AccountRole.Main ? canonical : NormalizeMainToMallbot(canonical);
            var after = Orient(Clone(_mainToMallbot), sourceRole, destinationRole);
            return Task.FromResult(new MainMallbotTransferReceipt(true, before, after,
                itemResults: results, committedAt: request.CommittedAt));
        }
    }

    private static bool Matches(TransferObservation expected, TransferObservation actual, TransferRequest request)
    {
        if (!string.Equals(expected.SourceAccount, actual.SourceAccount, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expected.DestinationAccount, actual.DestinationAccount, StringComparison.OrdinalIgnoreCase))
            return false;
        foreach (var itemId in request.Items.Keys)
            if (expected.SourceQuantity(itemId) != actual.SourceQuantity(itemId)
                || expected.DestinationQuantity(itemId) != actual.DestinationQuantity(itemId))
                return false;
        if (expected.SourceStatus?.Meat != actual.SourceStatus?.Meat
            || expected.DestinationStatus?.Meat != actual.DestinationStatus?.Meat)
            return false;
        return true;
    }

    private static TransferObservation NormalizeMainToMallbot(TransferObservation observation)
    {
        var sourceRole = ParseRole(observation.SourceAccount);
        var destinationRole = ParseRole(observation.DestinationAccount);
        var normalized = sourceRole == AccountRole.Mallbot && destinationRole == AccountRole.Main
            ? Swap(observation)
            : observation;
        return normalized with { SourceAccount = AccountRole.Main.ToString(), DestinationAccount = AccountRole.Mallbot.ToString() };
    }

    private static TransferObservation Orient(TransferObservation mainToMallbot,
        AccountRole source, AccountRole destination)
    {
        if (source == AccountRole.Main && destination == AccountRole.Mallbot)
            return mainToMallbot with { SourceAccount = source.ToString(), DestinationAccount = destination.ToString() };
        return Swap(mainToMallbot) with { SourceAccount = source.ToString(), DestinationAccount = destination.ToString() };
    }

    private static TransferObservation Swap(TransferObservation value)
        => value with
        {
            SourceInventory = value.DestinationInventory,
            DestinationInventory = value.SourceInventory,
            SourceStatus = value.DestinationStatus,
            DestinationStatus = value.SourceStatus,
            SourceAccount = value.DestinationAccount,
            DestinationAccount = value.SourceAccount
        };

    private static TransferObservation Clone(TransferObservation value)
        => value with
        {
            SourceInventory = new KoLInventorySnapshot(value.SourceInventory.Items.ToArray(), value.SourceInventory.FetchedAt, value.SourceInventory.IsStale),
            DestinationInventory = new KoLInventorySnapshot(value.DestinationInventory.Items.ToArray(), value.DestinationInventory.FetchedAt, value.DestinationInventory.IsStale)
        };

    private static AccountRole? ParseRole(string? value)
        => Enum.TryParse<AccountRole>(value, true, out var role) ? role : null;
}

/// <summary>Compatibility alias for callers that use a shorter fixture name.</summary>
public sealed class MainMallbotTransferFixtureTransport : IMainMallbotTransferTransport
{
    private readonly FixtureMainMallbotTransferTransport _inner;

    public MainMallbotTransferFixtureTransport(TransferObservation initial)
        => _inner = new FixtureMainMallbotTransferTransport(initial);

    public int RefreshCount => _inner.RefreshCount;
    public int CommitCount => _inner.CommitCount;
    public int HttpRequestCount => _inner.HttpRequestCount;
    public bool UsedHttp => _inner.UsedHttp;
    public TransferObservation State => _inner.State;
    public void EnqueueRefresh(TransferObservation observation) => _inner.EnqueueRefresh(observation);
    public void SetState(TransferObservation observation) => _inner.SetState(observation);
    public void RejectNextCommit(string? message = null) => _inner.RejectNextCommit(message);
    public Task<TransferObservation> RefreshAsync(MainMallbotTransferRefresh request, CancellationToken cancellationToken = default)
        => _inner.RefreshAsync(request, cancellationToken);
    public Task<MainMallbotTransferReceipt> CommitAsync(MainMallbotTransferTransportCommit request, CancellationToken cancellationToken = default)
        => _inner.CommitAsync(request, cancellationToken);
}

/// <summary>
/// HTTP transport for KoL account reads and the explicit gift/trade POST.
/// Mutation is disabled by default and requires both <see
/// cref="KoLMainMallbotTransferHttpOptions.AllowMutation"/> and a non-empty
/// approval token supplied by the workflow.
/// </summary>
public sealed class KoLMainMallbotTransferTransport : IMainMallbotTransferTransport
{
    private readonly IReadOnlyDictionary<AccountRole, IAccountReadClient> _readers;
    private readonly IReadOnlyDictionary<AccountRole, HttpClient> _clients;
    private readonly IReadOnlyDictionary<AccountRole, string> _accountIds;
    private readonly KoLMainMallbotTransferHttpOptions _options;
    private readonly TimeProvider _clock;

    public KoLMainMallbotTransferTransport(
        MainMallbotTransferAccountBinding main,
        MainMallbotTransferAccountBinding mallbot,
        IAccountReadClient mainReader,
        IAccountReadClient mallbotReader,
        HttpClient? mainClient = null,
        HttpClient? mallbotClient = null,
        KoLMainMallbotTransferHttpOptions? options = null,
        TimeProvider? clock = null)
        : this(
            new Dictionary<AccountRole, IAccountReadClient>
            {
                [AccountRole.Main] = mainReader ?? throw new ArgumentNullException(nameof(mainReader)),
                [AccountRole.Mallbot] = mallbotReader ?? throw new ArgumentNullException(nameof(mallbotReader))
            },
            BuildClients(mainClient, mallbotClient),
            new Dictionary<AccountRole, string>
            {
                [AccountRole.Main] = (main ?? throw new ArgumentNullException(nameof(main))).AccountId,
                [AccountRole.Mallbot] = (mallbot ?? throw new ArgumentNullException(nameof(mallbot))).AccountId
            },
            options,
            clock)
    {
        if (main.Role != AccountRole.Main || mallbot.Role != AccountRole.Mallbot)
            throw new ArgumentException("Transfer bindings must be Main and Mallbot.");
    }

    public KoLMainMallbotTransferTransport(
        IReadOnlyDictionary<AccountRole, IAccountReadClient> readers,
        IReadOnlyDictionary<AccountRole, HttpClient>? clients,
        KoLMainMallbotTransferHttpOptions? options,
        TimeProvider? clock = null)
        : this(readers, clients, null, options, clock)
    {
    }

    public KoLMainMallbotTransferTransport(
        IReadOnlyDictionary<AccountRole, IAccountReadClient> readers,
        IReadOnlyDictionary<AccountRole, HttpClient>? clients = null,
        IReadOnlyDictionary<AccountRole, string>? accountIds = null,
        KoLMainMallbotTransferHttpOptions? options = null,
        TimeProvider? clock = null)
    {
        _readers = readers ?? throw new ArgumentNullException(nameof(readers));
        if (!_readers.ContainsKey(AccountRole.Main) || !_readers.ContainsKey(AccountRole.Mallbot))
            throw new ArgumentException("Readers for both Main and Mallbot are required.", nameof(readers));
        _clients = clients ?? new Dictionary<AccountRole, HttpClient>();
        _accountIds = accountIds ?? new Dictionary<AccountRole, string>();
        _options = options ?? new KoLMainMallbotTransferHttpOptions();
        _options.Validate();
        _clock = clock ?? TimeProvider.System;
    }

    public int HttpRequestCount { get; private set; }
    public bool UsedHttp => HttpRequestCount > 0;
    public KoLMainMallbotTransferHttpOptions Options => _options;

    public async Task<TransferObservation> RefreshAsync(
        MainMallbotTransferRefresh request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var sourceRole = request.Source.Role;
        var destinationRole = request.Destination.Role;
        var sourceReader = _readers[sourceRole];
        var destinationReader = _readers[destinationRole];
        var sourceInventory = await sourceReader.FetchInventoryAsync(true, cancellationToken).ConfigureAwait(false);
        var destinationInventory = await destinationReader.FetchInventoryAsync(true, cancellationToken).ConfigureAwait(false);
        var sourceStatus = await sourceReader.FetchStatusAsync(true, cancellationToken).ConfigureAwait(false);
        var destinationStatus = await destinationReader.FetchStatusAsync(true, cancellationToken).ConfigureAwait(false);
        return new TransferObservation
        {
            SourceInventory = sourceInventory,
            DestinationInventory = destinationInventory,
            SourceStatus = sourceStatus,
            DestinationStatus = destinationStatus,
            ObservedAt = _clock.GetUtcNow(),
            SourceAccount = request.Source.CanonicalName,
            DestinationAccount = request.Destination.CanonicalName
        };
    }

    public async Task<MainMallbotTransferReceipt> CommitAsync(
        MainMallbotTransferTransportCommit request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Preview.DryRun)
            return new MainMallbotTransferReceipt(false, request.Before, request.Before,
                "KoL transfer preview is dry-run; no mutation was sent.", committedAt: _clock.GetUtcNow());
        if (!_options.AllowMutation)
            return new MainMallbotTransferReceipt(false, request.Before, request.Before,
                "KoL transfer mutation is disabled; set AllowMutation explicitly after review.",
                committedAt: _clock.GetUtcNow());
        if (string.IsNullOrWhiteSpace(request.ApprovalToken))
            return new MainMallbotTransferReceipt(false, request.Before, request.Before,
                "An approval token is required for a KoL transfer.", committedAt: _clock.GetUtcNow());
        var source = request.Preview.Request.SourceRole;
        var destination = request.Preview.Request.DestinationRole;
        if (source is null || destination is null || source == destination)
            return new MainMallbotTransferReceipt(false, request.Before, request.Before,
                "Main and Mallbot roles must be bound before a KoL transfer.", committedAt: _clock.GetUtcNow());
        if (!_options.AllowBatch && request.Preview.Request.Items.Count != 1)
            return new MainMallbotTransferReceipt(false, request.Before, request.Before,
                "The configured KoL transfer route accepts exactly one item line.", committedAt: _clock.GetUtcNow());
        if (request.Preview.Request.Items.Keys.Any(itemId => string.IsNullOrWhiteSpace(itemId)
            || !itemId.Trim().All(char.IsDigit)))
            return new MainMallbotTransferReceipt(false, request.Before, request.Before,
                "KoL transfer item ids must be numeric.", committedAt: _clock.GetUtcNow());
        if (!_clients.TryGetValue(source.Value, out var client))
            return new MainMallbotTransferReceipt(false, request.Before, request.Before,
                "No source-account HTTP client is configured.", committedAt: _clock.GetUtcNow());
        var destinationId = request.Destination?.AccountId;
        if (string.IsNullOrWhiteSpace(destinationId)
            && _accountIds.TryGetValue(destination.Value, out var configuredDestinationId))
            destinationId = configuredDestinationId;
        if (string.IsNullOrWhiteSpace(destinationId))
            return new MainMallbotTransferReceipt(false, request.Before, request.Before,
                "No destination-account binding is configured.", committedAt: _clock.GetUtcNow());

        using var content = new FormUrlEncodedContent(BuildForm(request, destination.Value, destinationId));
        using var response = await client.PostAsync(_options.CommitPath, content, cancellationToken).ConfigureAwait(false);
        HttpRequestCount++;
        if (!response.IsSuccessStatusCode)
            return new MainMallbotTransferReceipt(false, request.Before, request.Before,
                $"KoL transfer HTTP request failed ({(int)response.StatusCode}).", committedAt: _clock.GetUtcNow());

        // Refresh through the normal read clients.  The verifier, rather than
        // an HTML success marker, decides whether the item actually moved.
        try
        {
            var after = await RefreshAsync(new MainMallbotTransferRefresh(
                request.Preview.Request,
                new MainMallbotTransferAccountBinding { Role = source.Value, AccountId = source.Value.ToString() },
                new MainMallbotTransferAccountBinding { Role = destination.Value, AccountId = destination.Value.ToString() }), cancellationToken)
                .ConfigureAwait(false);
            return new MainMallbotTransferReceipt(true, request.Before, after,
                committedAt: _clock.GetUtcNow());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // The POST may have succeeded while the verification read failed.
            // Preserve an accepted receipt with an invalid after snapshot so
            // the Core verifier reports reconciliation instead of retrying a
            // potentially duplicated transfer.
            return new MainMallbotTransferReceipt(true, request.Before, request.Before,
                "Transfer was submitted but the post-commit read failed; reconciliation is required.",
                sessionValid: false, committedAt: _clock.GetUtcNow());
        }
    }

    private IReadOnlyDictionary<string, string> BuildForm(
        MainMallbotTransferTransportCommit request,
        AccountRole destination,
        string destinationId)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["action"] = _options.Action,
            ["towho"] = destinationId
        };
        foreach (var (itemId, quantity) in request.Preview.Request.Items)
        {
            form["whichitem"] = itemId;
            form["howmany"] = quantity.ToString(System.Globalization.CultureInfo.InvariantCulture);
            break;
        }
        if (request.Preview.Request.Meat > 0)
            form["sendmeat"] = request.Preview.Request.Meat.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return form;
    }

    private static IReadOnlyDictionary<AccountRole, HttpClient> BuildClients(
        HttpClient? mainClient, HttpClient? mallbotClient)
    {
        var clients = new Dictionary<AccountRole, HttpClient>();
        if (mainClient is not null) clients[AccountRole.Main] = mainClient;
        if (mallbotClient is not null) clients[AccountRole.Mallbot] = mallbotClient;
        return clients;
    }
}

/// <summary>Explicit route/mutation settings for the guarded KoL transport.</summary>
public sealed record KoLMainMallbotTransferHttpOptions
{
    public string CommitPath { get; init; } = "sendmessage.php";
    public string Action { get; init; } = "send";
    public bool AllowMutation { get; init; }
    public bool AllowBatch { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(CommitPath) || Uri.TryCreate(CommitPath, UriKind.Absolute, out _))
            throw new ArgumentException("CommitPath must be a relative KoL route.", nameof(CommitPath));
        if (string.IsNullOrWhiteSpace(Action))
            throw new ArgumentException("A transfer action is required.", nameof(Action));
    }
}

/// <summary>Short alias for the guarded KoL HTTP transport.</summary>
public sealed class KoLMainMallbotTransferHttpAdapter : IMainMallbotTransferTransport
{
    private readonly KoLMainMallbotTransferTransport _inner;

    public KoLMainMallbotTransferHttpAdapter(
        IReadOnlyDictionary<AccountRole, IAccountReadClient> readers,
        IReadOnlyDictionary<AccountRole, HttpClient>? clients,
        KoLMainMallbotTransferHttpOptions? options,
        TimeProvider? clock = null)
        => _inner = new KoLMainMallbotTransferTransport(readers, clients, options, clock);

    public KoLMainMallbotTransferHttpAdapter(
        IReadOnlyDictionary<AccountRole, IAccountReadClient> readers,
        IReadOnlyDictionary<AccountRole, HttpClient>? clients = null,
        IReadOnlyDictionary<AccountRole, string>? accountIds = null,
        KoLMainMallbotTransferHttpOptions? options = null,
        TimeProvider? clock = null)
        => _inner = new KoLMainMallbotTransferTransport(readers, clients, accountIds, options, clock);

    public int HttpRequestCount => _inner.HttpRequestCount;
    public bool UsedHttp => _inner.UsedHttp;
    public Task<TransferObservation> RefreshAsync(MainMallbotTransferRefresh request, CancellationToken cancellationToken = default)
        => _inner.RefreshAsync(request, cancellationToken);
    public Task<MainMallbotTransferReceipt> CommitAsync(MainMallbotTransferTransportCommit request, CancellationToken cancellationToken = default)
        => _inner.CommitAsync(request, cancellationToken);
}
