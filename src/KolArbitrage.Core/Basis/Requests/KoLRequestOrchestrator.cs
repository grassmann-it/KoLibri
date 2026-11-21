
namespace KolArbitrage.Core.Basis;

/// <summary>
/// Central request/session coordinator for KoL-facing adapters. It assigns
/// correlation ids, classifies responses, updates session health, retries only
/// safe idempotent requests, trips a circuit on repeated transient failures,
/// and serializes mutations per account.
/// </summary>
public sealed class KoLRequestOrchestrator : IDisposable
{
    private readonly IKoLRequestTransport _transport;
    private readonly KoLRequestOrchestratorOptions _options;
    private readonly TimeProvider _clock;
    private readonly object _healthGate = new();
    private readonly Dictionary<string, KoLSessionObservation> _observations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _ownsAbort;
    private readonly bool _ownsLanes;
    private int _requestSequence;
    private bool _disposed;

    public KoLRequestOrchestrator(
        IKoLRequestTransport transport,
        KoLRequestOrchestratorOptions? options = null,
        TimeProvider? clock = null,
        KoLGlobalAbort? globalAbort = null,
        KoLCircuitBreaker? circuitBreaker = null,
        KoLAccountMutationLanePool? mutationLanes = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? new KoLRequestOrchestratorOptions();
        _options.Validate();
        _clock = clock ?? TimeProvider.System;
        GlobalAbort = globalAbort ?? new KoLGlobalAbort();
        _ownsAbort = globalAbort is null;
        CircuitBreaker = circuitBreaker ?? new KoLCircuitBreaker(_options.CircuitBreaker);
        MutationLanes = mutationLanes ?? new KoLAccountMutationLanePool();
        _ownsLanes = mutationLanes is null;
    }

    public KoLRequestOrchestrator(
        Func<KoLRequest, CancellationToken, Task<KoLResponse>> sender,
        KoLRequestOrchestratorOptions? options = null,
        TimeProvider? clock = null,
        KoLGlobalAbort? globalAbort = null,
        KoLCircuitBreaker? circuitBreaker = null,
        KoLAccountMutationLanePool? mutationLanes = null)
        : this(new DelegateKoLRequestTransport(sender), options, clock, globalAbort,
            circuitBreaker, mutationLanes)
    {
    }

    public KoLGlobalAbort GlobalAbort { get; }
    public CancellationToken GlobalCancellationToken => GlobalAbort.Token;
    public KoLCircuitBreaker CircuitBreaker { get; }
    public KoLAccountMutationLanePool MutationLanes { get; }
    public KoLRequestOrchestratorOptions Options => _options;

    public bool AbortAll(string? reason = null) => GlobalAbort.Abort(reason);
    public bool Abort(string? reason = null) => AbortAll(reason);
    public bool CancelAll(string? reason = null) => AbortAll(reason);

    /// <summary>
    /// Sends a request and returns the final classified response. Transport
    /// and global abort cancellation remain observable as exceptions; a KoL
    /// login/rate-limit/transient page is represented in the result.
    /// </summary>
    public async Task<KoLRequestResult> SendAsync(
        KoLRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        ObjectDisposedException.ThrowIf(_disposed, this);

        var requestId = string.IsNullOrWhiteSpace(request.RequestId)
            ? CreateRequestId()
            : request.RequestId!.Trim();
        request = request.WithRequestId(requestId);

        using var timeoutSource = CreateTimeoutSource(_options.RequestTimeout);
        using var linked = CreateLinkedSource(cancellationToken, timeoutSource?.Token ?? default,
            GlobalAbort.Token);
        var token = linked.Token;
        try
        {
            GlobalAbort.ThrowIfAborted();
            if (request.IsMutation)
            {
                var lane = MutationLanes.Get(request.AccountId);
                return await lane.ExecuteAsync(
                    laneToken => SendCoreAsync(request, laneToken), token).ConfigureAwait(false);
            }
            return await SendCoreAsync(request, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (GlobalAbort.IsAborted)
        {
            throw new KoLRequestAbortedException(GlobalAbort.Reason ?? exception.Message);
        }
    }

    public Task<KoLRequestResult> ExecuteAsync(
        KoLRequest request,
        CancellationToken cancellationToken = default) => SendAsync(request, cancellationToken);

    public Task<KoLRequestResult> SendRequestAsync(
        KoLRequest request,
        CancellationToken cancellationToken = default) => SendAsync(request, cancellationToken);

    public Task<KoLRequestResult> RequestAsync(
        KoLRequest request,
        CancellationToken cancellationToken = default) => SendAsync(request, cancellationToken);

    /// <summary>
    /// Non-throwing boundary for hosts that want an abort/circuit decision as
    /// data. Ordinary <see cref="SendAsync"/> keeps cancellation and circuit
    /// errors observable to callers.
    /// </summary>
    public async Task<KoLRequestResult> TrySendAsync(
        KoLRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var requestId = string.IsNullOrWhiteSpace(request.RequestId)
            ? CreateRequestId()
            : request.RequestId!.Trim();
        var normalized = request.WithRequestId(requestId);
        try
        {
            return await SendAsync(normalized, cancellationToken).ConfigureAwait(false);
        }
        catch (KoLRequestAbortedException exception)
        {
            return new KoLRequestResult
            {
                Request = normalized,
                RequestId = requestId,
                Attempts = 0,
                Error = exception,
                Aborted = true
            };
        }
        catch (KoLCircuitOpenException exception)
        {
            return new KoLRequestResult
            {
                Request = normalized,
                RequestId = requestId,
                Attempts = 0,
                Error = exception
            };
        }
    }

    public async Task<KoLResponse> SendResponseAsync(
        KoLRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return result.Response ?? throw new InvalidOperationException(
            $"KoL request '{result.RequestId}' completed without a response.");
    }

    public KoLSessionHealth GetSessionHealth(string accountId, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            throw new ArgumentException("An account id is required.", nameof(accountId));
        lock (_healthGate)
        {
            if (!_observations.TryGetValue(accountId.Trim(), out var observation))
                observation = new KoLSessionObservation();
            return KoLSessionHealthEvaluator.Evaluate(
                observation,
                _options.SessionHealth.RetryPolicy,
                now ?? _clock.GetUtcNow());
        }
    }

    public KoLSessionHealth SessionHealth(string accountId, DateTimeOffset? now = null) =>
        GetSessionHealth(accountId, now);

    public void SetSessionObservation(string accountId, KoLSessionObservation observation)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            throw new ArgumentException("An account id is required.", nameof(accountId));
        ArgumentNullException.ThrowIfNull(observation);
        lock (_healthGate) _observations[accountId.Trim()] = observation;
    }

    private async Task<KoLRequestResult> SendCoreAsync(
        KoLRequest request,
        CancellationToken cancellationToken)
    {
        var attempts = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GlobalAbort.ThrowIfAborted();
            CircuitBreaker.ThrowIfOpen(_clock.GetUtcNow());
            attempts++;

            KoLResponse response;
            try
            {
                response = await _transport.SendAsync(request, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The KoL transport returned no response.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or TimeoutException)
            {
                response = KoLResponse.TransientFailure(exception, request.Uri,
                    _clock.GetUtcNow());
            }

            response = EnsureResponseClassification(request, response);
            var health = UpdateSessionHealth(request, response);
            var retry = _options.RetryPolicy.CanRetry(request, response, attempts);
            if (!retry)
            {
                if (response.IsTransient)
                    CircuitBreaker.RecordFailure(_clock.GetUtcNow());
                else
                    CircuitBreaker.RecordSuccess();
                return new KoLRequestResult
                {
                    Request = request,
                    RequestId = request.RequestId!,
                    Response = response,
                    Attempts = attempts,
                    SessionHealth = health
                };
            }

            CircuitBreaker.RecordFailure(_clock.GetUtcNow());
            var delay = _options.RetryPolicy.GetDelay(attempts, response.RetryAfter);
            Func<TimeSpan, CancellationToken, Task> delayAsync =
                _options.DelayAsync ?? ((value, token) => Task.Delay(value, token));
            await delayAsync(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private KoLResponse EnsureResponseClassification(KoLRequest request, KoLResponse response)
    {
        if (response.ReceivedAt == default)
            response = response with { ReceivedAt = _clock.GetUtcNow() };
        if (response.Body is { } body && body.Length > _options.MaximumResponseBodyBytes)
            response = response with
            {
                Body = _options.MaximumResponseBodyBytes == 0
                    ? string.Empty
                    : body[.._options.MaximumResponseBodyBytes]
            };
        var classified = KoLResponseClassifier.ClassifyResponse(request, response);
        return classified.RequestUri is null
            ? classified with { RequestUri = request.Uri }
            : classified;
    }

    private KoLSessionHealth UpdateSessionHealth(KoLRequest request, KoLResponse response)
    {
        var key = request.AccountId.Trim();
        var at = response.ReceivedAt == default ? _clock.GetUtcNow() : response.ReceivedAt;
        lock (_healthGate)
        {
            _observations.TryGetValue(key, out var previous);
            previous ??= new KoLSessionObservation();
            var kind = response.Classification;
            var successful = response.IsSuccessful;
            var failed = kind is KoLResponseKind.Login or KoLResponseKind.RateLimited
                or KoLResponseKind.Transient or KoLResponseKind.ClientError;
            var retryable = kind is KoLResponseKind.RateLimited or KoLResponseKind.Transient;
            var hasCookie = response.HasSessionCookie ??
                (previous.HasCookie || request.RequiresAuthentication);
            var observation = previous with
            {
                HasCookie = hasCookie,
                LastSuccessAt = successful ? at : previous.LastSuccessAt,
                LastFailureAt = failed ? at : previous.LastFailureAt,
                LastServerResponse = response.ToServerResponse(),
                ConsecutiveFailures = retryable ? previous.ConsecutiveFailures + 1 : 0,
                LastFailureReason = failed ? response.Error : null
            };
            _observations[key] = observation;
            return KoLSessionHealthEvaluator.Evaluate(
                observation,
                _options.SessionHealth.RetryPolicy,
                at);
        }
    }

    private string CreateRequestId()
    {
        var sequence = Interlocked.Increment(ref _requestSequence);
        return $"kol-{_clock.GetUtcNow():yyyyMMddHHmmssfff}-{sequence:x}";
    }

    private static CancellationTokenSource? CreateTimeoutSource(TimeSpan timeout)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
            return null;
        return new CancellationTokenSource(timeout);
    }

    private static CancellationTokenSource CreateLinkedSource(
        CancellationToken caller,
        CancellationToken timeout,
        CancellationToken global)
    {
        if (!caller.CanBeCanceled && !timeout.CanBeCanceled && !global.CanBeCanceled)
            return new CancellationTokenSource();
        return CancellationTokenSource.CreateLinkedTokenSource(caller, timeout, global);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsAbort && !GlobalAbort.IsAborted)
            GlobalAbort.Abort("KoL request orchestrator disposed.");
        if (_ownsLanes) MutationLanes.Dispose();
        if (_ownsAbort) GlobalAbort.Dispose();
    }
}
