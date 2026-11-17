namespace KolArbitrage.Core.Adventure;

/// <summary>Operation buckets exposed by the deterministic manual-combat fixture.</summary>
public enum ManualCombatTransportOperation
{
    Inspect,
    Submit,
    Refresh
}

/// <summary>
/// In-memory transport for manual-combat tests and offline hosts. It records
/// every request and dequeues exactly one response per call; it never creates
/// an HttpClient and never retries a failed POST.
/// </summary>
public sealed class ManualCombatFixtureTransport : IManualCombatTransport, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<ManualCombatTransportOperation, Queue<ManualCombatTransportResponse>> _responses = new();
    private readonly Dictionary<ManualCombatTransportOperation, Queue<Exception>> _failures = new();
    private readonly List<ManualCombatTransportRequest> _requests = new();
    private bool _disposed;

    public ManualCombatFixtureTransport()
    {
        foreach (var operation in Enum.GetValues<ManualCombatTransportOperation>())
        {
            _responses[operation] = new Queue<ManualCombatTransportResponse>();
            _failures[operation] = new Queue<Exception>();
        }
    }

    public ManualCombatFixtureTransport(ManualCombatTransportOperation operation, ManualCombatTransportResponse response)
        : this() => Enqueue(operation, response);

    public bool UsedHttp => false;
    public int HttpRequestCount => 0;
    public IReadOnlyList<ManualCombatTransportRequest> Requests { get { lock (_gate) return _requests.ToArray(); } }
    public int RequestCount { get { lock (_gate) return _requests.Count; } }
    public int Count(ManualCombatTransportOperation operation) { lock (_gate) return _requests.Count(x => OperationFor(x) == operation); }

    public void Enqueue(ManualCombatTransportOperation operation, ManualCombatTransportResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        lock (_gate) _responses[operation].Enqueue(response);
    }

    public void EnqueueFailure(ManualCombatTransportOperation operation, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (_gate) _failures[operation].Enqueue(exception);
    }

    public void EnqueueObservation(ManualCombatTransportOperation operation, ManualCombatObservation observation, string? requestId = null, bool? accepted = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        Enqueue(operation, new ManualCombatTransportResponse
        {
            RequestId = requestId ?? string.Empty,
            CombatId = observation.CombatId,
            AdventureId = observation.AdventureId,
            Observation = observation,
            ResponseHash = observation.ResponseHash,
            Accepted = accepted
        });
    }

    public void EnqueueObservation(ManualCombatTransportOperation operation, CombatObservation observation, string combatId, string? requestId = null, bool? accepted = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        Enqueue(operation, new ManualCombatTransportResponse
        {
            RequestId = requestId ?? string.Empty,
            CombatId = combatId,
            Combat = observation,
            ResponseHash = observation.ResponseHash,
            Accepted = accepted
        });
    }

    public void EnqueueResponse(ManualCombatTransportOperation operation, ManualCombatTransportResponse response)
        => Enqueue(operation, response);

    public Task<ManualCombatTransportResponse> InspectAsync(ManualCombatTransportRequest request, CancellationToken cancellationToken = default)
        => DequeueAsync(ManualCombatTransportOperation.Inspect, request, cancellationToken);

    public Task<ManualCombatTransportResponse> SubmitAsync(ManualCombatTransportRequest request, CancellationToken cancellationToken = default)
        => DequeueAsync(ManualCombatTransportOperation.Submit, request, cancellationToken);

    public Task<ManualCombatTransportResponse> RefreshAsync(ManualCombatTransportRequest request, CancellationToken cancellationToken = default)
        => DequeueAsync(ManualCombatTransportOperation.Refresh, request, cancellationToken);

    private Task<ManualCombatTransportResponse> DequeueAsync(ManualCombatTransportOperation operation, ManualCombatTransportRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _requests.Add(request);
            if (_failures[operation].Count > 0) throw _failures[operation].Dequeue();
            if (_responses[operation].Count == 0) throw new InvalidOperationException($"No fixture response was queued for {operation}.");
            var response = _responses[operation].Dequeue();
            if (string.IsNullOrWhiteSpace(response.RequestId)) response = response with { RequestId = request.RequestId };
            if (string.IsNullOrWhiteSpace(response.CombatId)) response = response with { CombatId = request.CombatId };
            if (string.IsNullOrWhiteSpace(response.AdventureId)) response = response with { AdventureId = request.AdventureId };
            if (string.IsNullOrWhiteSpace(response.RequestFingerprint)) response = response with { RequestFingerprint = request.ActionFingerprint };
            if (string.IsNullOrWhiteSpace(response.ResponseHash)) response = response with { ResponseHash = response.EffectiveResponseHash };
            return Task.FromResult(response);
        }
    }

    private static ManualCombatTransportOperation OperationFor(ManualCombatTransportRequest request)
        => request.IsRefresh ? ManualCombatTransportOperation.Refresh : request.Action is null ? ManualCombatTransportOperation.Inspect : ManualCombatTransportOperation.Submit;

    public void Dispose() { lock (_gate) _disposed = true; }
}

/// <summary>Short fixture alias matching the adventure execution fixture naming.</summary>
public sealed class FixtureManualCombatTransport : IManualCombatTransport, IDisposable
{
    private readonly ManualCombatFixtureTransport _inner = new();
    public bool UsedHttp => _inner.UsedHttp;
    public int HttpRequestCount => _inner.HttpRequestCount;
    public IReadOnlyList<ManualCombatTransportRequest> Requests => _inner.Requests;
    public int RequestCount => _inner.RequestCount;
    public int Count(ManualCombatTransportOperation operation) => _inner.Count(operation);
    public void Enqueue(ManualCombatTransportOperation operation, ManualCombatTransportResponse response) => _inner.Enqueue(operation, response);
    public void EnqueueFailure(ManualCombatTransportOperation operation, Exception exception) => _inner.EnqueueFailure(operation, exception);
    public void EnqueueObservation(ManualCombatTransportOperation operation, ManualCombatObservation observation, string? requestId = null, bool? accepted = null) => _inner.EnqueueObservation(operation, observation, requestId, accepted);
    public void EnqueueObservation(ManualCombatTransportOperation operation, CombatObservation observation, string combatId, string? requestId = null, bool? accepted = null) => _inner.EnqueueObservation(operation, observation, combatId, requestId, accepted);
    public Task<ManualCombatTransportResponse> InspectAsync(ManualCombatTransportRequest request, CancellationToken cancellationToken = default) => _inner.InspectAsync(request, cancellationToken);
    public Task<ManualCombatTransportResponse> SubmitAsync(ManualCombatTransportRequest request, CancellationToken cancellationToken = default) => _inner.SubmitAsync(request, cancellationToken);
    public Task<ManualCombatTransportResponse> RefreshAsync(ManualCombatTransportRequest request, CancellationToken cancellationToken = default) => _inner.RefreshAsync(request, cancellationToken);
    public void Dispose() => _inner.Dispose();
}
