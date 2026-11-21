namespace KolArbitrage.Core.Spiel;

/// <summary>
/// Deterministic transport for gateway tests and offline hosts.  It stores no
/// cookies, creates no HttpClient, and returns responses in the order queued
/// for each operation.  Every response is stamped with the request correlation
/// id when the caller did not set one explicitly.
/// </summary>
public sealed class AdventureExecutionFixtureTransport : IAdventureTransport, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<AdventureTransportOperation, Queue<AdventureTransportResponse>> _responses = new();
    private readonly Dictionary<AdventureTransportOperation, Queue<Exception>> _failures = new();
    private readonly List<AdventureTransportRequest> _requests = new();
    private bool _disposed;

    public AdventureExecutionFixtureTransport()
    {
        foreach (var operation in Enum.GetValues<AdventureTransportOperation>())
        {
            _responses[operation] = new Queue<AdventureTransportResponse>();
            _failures[operation] = new Queue<Exception>();
        }
    }

    public AdventureExecutionFixtureTransport(
        AdventureTransportOperation operation,
        AdventureTransportResponse response)
        : this()
        => Enqueue(operation, response);

    public bool UsedHttp => false;
    public int HttpRequestCount => 0;
    public IReadOnlyList<AdventureTransportRequest> Requests
    {
        get
        {
            lock (_gate) return _requests.ToArray();
        }
    }

    public int RequestCount
    {
        get
        {
            lock (_gate) return _requests.Count;
        }
    }

    public int Count(AdventureTransportOperation operation)
    {
        lock (_gate)
            return _requests.Count(x => x.Operation == operation);
    }

    public void Enqueue(AdventureTransportOperation operation, AdventureTransportResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        lock (_gate) _responses[operation].Enqueue(response);
    }

    /// <summary>Queues a deterministic transport failure (including a timeout).</summary>
    public void EnqueueFailure(AdventureTransportOperation operation, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (_gate) _failures[operation].Enqueue(exception);
    }

    public void EnqueueEncounter(
        AdventureTransportOperation operation,
        string adventureId,
        AdventureEncounterState encounter,
        string? requestId = null)
        => Enqueue(operation, new AdventureTransportResponse
        {
            RequestId = requestId ?? string.Empty,
            AdventureId = adventureId,
            Encounter = encounter,
            ResponseHash = encounter.ResponseHash
        });

    public void EnqueueObservation(
        AdventureTransportOperation operation,
        AdventureKernelObservation observation,
        string? requestId = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        Enqueue(operation, new AdventureTransportResponse
        {
            RequestId = requestId ?? string.Empty,
            AdventureId = observation.AdventureId,
            Observation = observation,
            ResponseHash = observation.ResponseHash
        });
    }

    public void EnqueueHtml(
        AdventureTransportOperation operation,
        string adventureId,
        string html,
        Uri? responseUri = null,
        string? requestId = null)
        => Enqueue(operation, new AdventureTransportResponse
        {
            RequestId = requestId ?? string.Empty,
            AdventureId = adventureId,
            Html = html,
            ResponseUri = responseUri
        });

    public Task<AdventureTransportResponse> StartAdventureAsync(
        AdventureTransportRequest request,
        CancellationToken cancellationToken = default)
        => DequeueAsync(request, cancellationToken);

    public Task<AdventureTransportResponse> SubmitFightAsync(
        AdventureTransportRequest request,
        CancellationToken cancellationToken = default)
        => DequeueAsync(request, cancellationToken);

    public Task<AdventureTransportResponse> SubmitChoiceAsync(
        AdventureTransportRequest request,
        CancellationToken cancellationToken = default)
        => DequeueAsync(request, cancellationToken);

    public Task<AdventureTransportResponse> RefreshAsync(
        AdventureTransportRequest request,
        CancellationToken cancellationToken = default)
        => DequeueAsync(request, cancellationToken);

    private Task<AdventureTransportResponse> DequeueAsync(
        AdventureTransportRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _requests.Add(request);
            if (_failures.TryGetValue(request.Operation, out var failures)
                && failures.Count > 0)
                throw failures.Dequeue();
            if (!_responses.TryGetValue(request.Operation, out var queue)
                || queue.Count == 0)
                throw new InvalidOperationException(
                    $"No fixture response was queued for {request.Operation}.");

            var response = queue.Dequeue();
            if (string.IsNullOrWhiteSpace(response.RequestId))
                response = response with { RequestId = request.RequestId };
            if (string.IsNullOrWhiteSpace(response.RunId))
                response = response with { RunId = request.RunId };
            if (string.IsNullOrWhiteSpace(response.AdventureId))
                response = response with { AdventureId = request.AdventureId };
            if (string.IsNullOrWhiteSpace(response.RequestFingerprint))
                response = response with { RequestFingerprint = request.ActionFingerprint };
            if (string.IsNullOrWhiteSpace(response.ResponseHash))
            {
                var hash = response.EffectiveResponseHash;
                if (!string.IsNullOrWhiteSpace(hash))
                    response = response with { ResponseHash = hash };
            }
            return Task.FromResult(response);
        }
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
    }
}

/// <summary>Short fixture alias for callers that prefer a concise type name.</summary>
public sealed class FixtureAdventureTransport : IAdventureTransport
{
    private readonly AdventureExecutionFixtureTransport _inner = new();

    public bool UsedHttp => _inner.UsedHttp;
    public int HttpRequestCount => _inner.HttpRequestCount;
    public IReadOnlyList<AdventureTransportRequest> Requests => _inner.Requests;
    public int RequestCount => _inner.RequestCount;
    public int Count(AdventureTransportOperation operation) => _inner.Count(operation);
    public void Enqueue(AdventureTransportOperation operation, AdventureTransportResponse response)
        => _inner.Enqueue(operation, response);
    public void EnqueueFailure(AdventureTransportOperation operation, Exception exception)
        => _inner.EnqueueFailure(operation, exception);
    public void EnqueueEncounter(AdventureTransportOperation operation, string adventureId, AdventureEncounterState encounter, string? requestId = null)
        => _inner.EnqueueEncounter(operation, adventureId, encounter, requestId);
    public void EnqueueObservation(AdventureTransportOperation operation, AdventureKernelObservation observation, string? requestId = null)
        => _inner.EnqueueObservation(operation, observation, requestId);
    public void EnqueueHtml(AdventureTransportOperation operation, string adventureId, string html, Uri? responseUri = null, string? requestId = null)
        => _inner.EnqueueHtml(operation, adventureId, html, responseUri, requestId);
    public Task<AdventureTransportResponse> StartAdventureAsync(AdventureTransportRequest request, CancellationToken cancellationToken = default)
        => _inner.StartAdventureAsync(request, cancellationToken);
    public Task<AdventureTransportResponse> SubmitFightAsync(AdventureTransportRequest request, CancellationToken cancellationToken = default)
        => _inner.SubmitFightAsync(request, cancellationToken);
    public Task<AdventureTransportResponse> SubmitChoiceAsync(AdventureTransportRequest request, CancellationToken cancellationToken = default)
        => _inner.SubmitChoiceAsync(request, cancellationToken);
    public Task<AdventureTransportResponse> RefreshAsync(AdventureTransportRequest request, CancellationToken cancellationToken = default)
        => _inner.RefreshAsync(request, cancellationToken);
}
