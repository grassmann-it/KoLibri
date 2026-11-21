namespace KoLibri.Core.Basis;

/// <summary>Configuration for the request circuit breaker.</summary>
public sealed record KoLCircuitBreakerOptions
{
    public int FailureThreshold { get; init; } = 3;
    public TimeSpan OpenDuration { get; init; } = TimeSpan.FromSeconds(30);

    public void Validate()
    {
        if (FailureThreshold < 1)
            throw new ArgumentOutOfRangeException(nameof(FailureThreshold));
        if (OpenDuration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(OpenDuration));
    }
}

public enum KoLCircuitState
{
    Closed = 0,
    Open = 1,
    HalfOpen = 2
}

/// <summary>Thrown when a request is rejected by an open circuit.</summary>
public sealed class KoLCircuitOpenException : Exception
{
    public KoLCircuitOpenException(string message, DateTimeOffset retryAt)
        : base(message)
    {
        RetryAt = retryAt;
    }

    public DateTimeOffset RetryAt { get; }
    public DateTimeOffset NextRetryAt => RetryAt;
}

/// <summary>
/// Thread-safe circuit breaker.  The orchestrator records only rate-limit and
/// transient responses as failures; login/client errors do not trip it.
/// </summary>
public sealed class KoLCircuitBreaker
{
    private readonly object _gate = new();
    private readonly KoLCircuitBreakerOptions _options;
    private KoLCircuitState _state;
    private int _failureCount;
    private DateTimeOffset _openedAt;
    private bool _probeInFlight;

    public KoLCircuitBreaker(KoLCircuitBreakerOptions? options = null)
    {
        _options = options ?? new KoLCircuitBreakerOptions();
        _options.Validate();
    }

    public KoLCircuitState State
    {
        get { lock (_gate) return _state; }
    }

    public int ConsecutiveFailures
    {
        get { lock (_gate) return _failureCount; }
    }

    public DateTimeOffset? OpenedAt
    {
        get { lock (_gate) return _state == KoLCircuitState.Closed ? null : _openedAt; }
    }

    public DateTimeOffset? RetryAt
    {
        get
        {
            lock (_gate)
                return _state == KoLCircuitState.Closed ? null : _openedAt + _options.OpenDuration;
        }
    }

    /// <summary>Reserves permission for a request or throws when open.</summary>
    public void ThrowIfOpen(DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (_state == KoLCircuitState.Closed)
                return;

            if (_state == KoLCircuitState.Open)
            {
                var retryAt = _openedAt + _options.OpenDuration;
                if (at < retryAt)
                    throw new KoLCircuitOpenException(
                        "KoL request circuit is open after repeated transient failures.", retryAt);
                _state = KoLCircuitState.HalfOpen;
                _probeInFlight = false;
            }

            // There is exactly one half-open probe. Other callers wait for a
            // future normal request instead of stampeding the service.
            if (_state == KoLCircuitState.HalfOpen)
            {
                if (_probeInFlight)
                {
                    var retryAt = _openedAt + _options.OpenDuration;
                    throw new KoLCircuitOpenException(
                        "KoL request circuit is waiting for its half-open probe.", retryAt);
                }
                _probeInFlight = true;
            }
        }
    }

    public bool TryEnter(DateTimeOffset? now = null)
    {
        try
        {
            ThrowIfOpen(now);
            return true;
        }
        catch (KoLCircuitOpenException)
        {
            return false;
        }
    }

    public bool AllowRequest(DateTimeOffset? now = null) => TryEnter(now);
    public bool IsOpen => State == KoLCircuitState.Open;

    public void RecordSuccess()
    {
        lock (_gate)
        {
            _state = KoLCircuitState.Closed;
            _failureCount = 0;
            _openedAt = default;
            _probeInFlight = false;
        }
    }

    public void RecordFailure(DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            _failureCount = Math.Max(0, _failureCount) + 1;
            _probeInFlight = false;
            if (_state == KoLCircuitState.HalfOpen || _failureCount >= _options.FailureThreshold)
            {
                _state = KoLCircuitState.Open;
                _openedAt = at;
            }
        }
    }

    public void Reset() => RecordSuccess();

    public KoLCircuitBreakerOptions Options => _options;
}

/// <summary>
/// Process-wide abort source shared by request orchestrators. Abort is
/// intentionally one-way; create a new source for a new run.
/// </summary>
public sealed class KoLGlobalAbort : IDisposable
{
    private readonly CancellationTokenSource _source = new();
    private readonly object _gate = new();
    private string? _reason;
    private DateTimeOffset? _abortedAt;
    private bool _abortRequested;
    private bool _disposed;

    public CancellationToken Token => _source.Token;
    public CancellationToken CancellationToken => Token;
    public bool IsAborted
    {
        get { lock (_gate) return _abortRequested || _source.IsCancellationRequested; }
    }
    public string? Reason { get { lock (_gate) return _reason; } }
    public DateTimeOffset? AbortedAt { get { lock (_gate) return _abortedAt; } }

    public bool Abort(string? reason = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_abortRequested || _source.IsCancellationRequested)
                return false;
            _abortRequested = true;
            _reason = string.IsNullOrWhiteSpace(reason) ? "Global abort requested." : reason;
            _abortedAt = DateTimeOffset.UtcNow;
        }
        // Cancellation callbacks run synchronously. Do this outside the
        // metadata lock so a callback can safely inspect Reason/AbortedAt.
        _source.Cancel();
        return true;
    }

    public bool Cancel(string? reason = null) => Abort(reason);

    public void ThrowIfAborted()
    {
        if (IsAborted)
            throw new KoLRequestAbortedException(Reason ?? "Global abort requested.");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _source.Dispose();
        }
    }
}

public sealed class KoLRequestAbortedException : OperationCanceledException
{
    public KoLRequestAbortedException(string reason)
        : base(reason)
    {
        Reason = reason;
    }

    public string Reason { get; }
}

/// <summary>One serial lane for mutating requests belonging to an account.</summary>
public interface IKoLAccountMutationLane
{
    string AccountId { get; }

    Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default);

    Task ExecuteAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default);
}

public sealed class KoLAccountMutationLane : IKoLAccountMutationLane, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;

    public KoLAccountMutationLane(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            throw new ArgumentException("An account id is required.", nameof(accountId));
        AccountId = accountId.Trim();
    }

    public string AccountId { get; }

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task ExecuteAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return ExecuteAsync<object?>(async token =>
        {
            await operation(token).ConfigureAwait(false);
            return null;
        }, cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _gate.Dispose();
    }
}

/// <summary>Case-insensitive pool of per-account mutation lanes.</summary>
public sealed class KoLAccountMutationLanePool : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, KoLAccountMutationLane> _lanes =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public IKoLAccountMutationLane Get(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            throw new ArgumentException("An account id is required.", nameof(accountId));
        var key = accountId.Trim();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_lanes.TryGetValue(key, out var lane)) return lane;
            lane = new KoLAccountMutationLane(key);
            _lanes.Add(key, lane);
            return lane;
        }
    }

    public IKoLAccountMutationLane For(string accountId) => Get(accountId);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var lane in _lanes.Values) lane.Dispose();
            _lanes.Clear();
        }
    }
}
