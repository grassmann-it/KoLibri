using System.Net;
using System.Security.Cryptography;
using System.Text;
using KoLibri.Core.Basis;

namespace KoLibri.Core.Spiel;

/// <summary>One deterministic fixture call, including the fixed route.</summary>
public sealed record AdventureFixtureHttpRequest(
    AdventureTransportRequest Request,
    Uri Uri,
    HttpMethod Method);

/// <summary>
/// In-memory counterpart of <see cref="HttpAdventureExecutionTransport"/>.
/// It never opens a socket, but applies the same fixed route, account binding
/// and response-size checks so gateway tests exercise the live contract.
/// </summary>
public class AdventureHttpFixtureTransport : IAdventureExecutionHttpTransport, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<AdventureTransportOperation, Queue<AdventureTransportResponse>> _responses = new();
    private readonly Dictionary<AdventureTransportOperation, Queue<Exception>> _failures = new();
    private readonly List<AdventureFixtureHttpRequest> _requests = new();
    private readonly AdventureHttpTransportOptions _options;
    private readonly AdventureTransportAccountBinding? _binding;
    private bool _disposed;

    public AdventureHttpFixtureTransport(
        AdventureHttpTransportOptions? options = null,
        AdventureTransportAccountBinding? binding = null)
    {
        _options = options ?? new AdventureHttpTransportOptions();
        _options.Validate();
        _binding = binding;
        _binding?.Validate();
        foreach (var operation in Enum.GetValues<AdventureTransportOperation>())
        {
            _responses[operation] = new Queue<AdventureTransportResponse>();
            _failures[operation] = new Queue<Exception>();
        }
    }

    public AdventureHttpFixtureTransport(
        AdventureTransportAccountBinding binding,
        AdventureHttpTransportOptions? options = null)
        : this(options, binding)
    {
    }

    public AdventureHttpFixtureTransport(
        AdventureTransportOperation operation,
        AdventureTransportResponse response,
        AdventureHttpTransportOptions? options = null,
        AdventureTransportAccountBinding? binding = null)
        : this(options, binding)
        => Enqueue(operation, response);

    public AdventureHttpTransportOptions Options => _options;

    public AdventureHttpRouteMap Routes => AdventureHttpRouteMap.Default;

    public AdventureTransportAccountBinding? Binding => _binding;

    public AdventureTransportAccountBinding? AccountBinding => _binding;

    public AccountRole? Role => _binding?.Role;

    public string? AccountId => _binding?.AccountId;

    public bool UsedHttp => false;

    public int HttpRequestCount => 0;

    public int RequestCount
    {
        get
        {
            lock (_gate) return _requests.Count;
        }
    }

    public IReadOnlyList<AdventureTransportRequest> Requests
    {
        get
        {
            lock (_gate) return _requests.Select(x => x.Request).ToArray();
        }
    }

    public IReadOnlyList<AdventureFixtureHttpRequest> HttpRequests
    {
        get
        {
            lock (_gate) return _requests.ToArray();
        }
    }

    public IReadOnlyList<Uri> RequestUris
    {
        get
        {
            lock (_gate) return _requests.Select(x => x.Uri).ToArray();
        }
    }

    public Uri? LastRequestUri
    {
        get
        {
            lock (_gate) return _requests.Count == 0 ? null : _requests[^1].Uri;
        }
    }

    public int Count(AdventureTransportOperation operation)
    {
        lock (_gate) return _requests.Count(x => x.Request.Operation == operation);
    }

    public void Enqueue(AdventureTransportOperation operation, AdventureTransportResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _responses[operation].Enqueue(response);
        }
    }

    public void EnqueueResponse(AdventureTransportOperation operation, AdventureTransportResponse response)
        => Enqueue(operation, response);

    public void EnqueueFailure(AdventureTransportOperation operation, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _failures[operation].Enqueue(exception);
        }
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
        => DequeueAsync(request, AdventureTransportOperation.Adventure, cancellationToken);

    public Task<AdventureTransportResponse> AdventureAsync(
        AdventureTransportRequest request,
        CancellationToken cancellationToken = default)
        => StartAdventureAsync(request, cancellationToken);

    public Task<AdventureTransportResponse> SubmitFightAsync(
        AdventureTransportRequest request,
        CancellationToken cancellationToken = default)
        => DequeueAsync(request, AdventureTransportOperation.Fight, cancellationToken);

    public Task<AdventureTransportResponse> FightAsync(
        AdventureTransportRequest request,
        CancellationToken cancellationToken = default)
        => SubmitFightAsync(request, cancellationToken);

    public Task<AdventureTransportResponse> SubmitChoiceAsync(
        AdventureTransportRequest request,
        CancellationToken cancellationToken = default)
        => DequeueAsync(request, AdventureTransportOperation.Choice, cancellationToken);

    public Task<AdventureTransportResponse> ChoiceAsync(
        AdventureTransportRequest request,
        CancellationToken cancellationToken = default)
        => SubmitChoiceAsync(request, cancellationToken);

    public Task<AdventureTransportResponse> RefreshAsync(
        AdventureTransportRequest request,
        CancellationToken cancellationToken = default)
        => DequeueAsync(request, AdventureTransportOperation.Refresh, cancellationToken);

    private Task<AdventureTransportResponse> DequeueAsync(
        AdventureTransportRequest request,
        AdventureTransportOperation operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidateRequest(request, operation);
            ValidateFields(request, operation);
            var uri = new Uri(_options.BaseUri, AdventureHttpRoutes.For(operation));
            _requests.Add(new AdventureFixtureHttpRequest(
                request,
                uri,
                operation == AdventureTransportOperation.Refresh ? HttpMethod.Get : HttpMethod.Post));

            if (_failures[operation].Count > 0)
                throw _failures[operation].Dequeue();
            if (_responses[operation].Count == 0)
                throw new InvalidOperationException(
                    $"No fixture response was queued for {operation}.");

            var response = _responses[operation].Dequeue();
            if (response.Html is { } html
                && Encoding.UTF8.GetByteCount(html) > _options.MaximumResponseBodyBytes)
                throw new AdventureResponseTooLargeException(_options.MaximumResponseBodyBytes);
            if (string.IsNullOrWhiteSpace(response.RequestId))
                response = response with { RequestId = request.RequestId };
            if (string.IsNullOrWhiteSpace(response.RunId))
                response = response with { RunId = request.RunId };
            if (string.IsNullOrWhiteSpace(response.AdventureId))
                response = response with { AdventureId = request.AdventureId };
            if (response.ResponseUri is null)
                response = response with { ResponseUri = uri };
            if (response.StatusCode is null)
                response = response with { StatusCode = HttpStatusCode.OK };
            var responseUri = response.ResponseUri!;
            if ((int)response.StatusCode.Value is >= 300 and <= 399
                || !UriEquals(uri, responseUri))
                throw new AdventureHttpTransportException(
                    "KoL adventure redirects are disabled in the fixture transport.");
            _options.Security.EnsureResponseUri(uri, responseUri);
            if (string.IsNullOrWhiteSpace(response.RequestFingerprint))
                response = response with { RequestFingerprint = request.ActionFingerprint };
            if (string.IsNullOrWhiteSpace(response.ResponseHash))
            {
                var hash = response.EffectiveResponseHash;
                if (string.IsNullOrWhiteSpace(hash) && response.Html is not null)
                    hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(response.Html)))
                        .ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(hash))
                    response = response with { ResponseHash = hash };
            }
            if (response.Html is { } body)
                response = response with { BodyBytes = Encoding.UTF8.GetByteCount(body) };
            return Task.FromResult(response);
        }
    }

    private static bool UriEquals(Uri left, Uri right)
        => Uri.Compare(left, right, UriComponents.AbsoluteUri,
            UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;

    private static void ValidateFields(
        AdventureTransportRequest request,
        AdventureTransportOperation operation)
    {
        var fields = request.Fields;
        if (fields.Keys.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
            throw new AdventureHttpTransportException("Adventure form field names must be unique.");
        foreach (var field in fields)
        {
            if (string.IsNullOrWhiteSpace(field.Key)
                || field.Key.Length > 80
                || field.Value is { Length: > 4000 })
                throw new AdventureHttpTransportException("Adventure form field exceeds its bound.");
        }

        switch (operation)
        {
            case AdventureTransportOperation.Adventure:
                var expectedSnarfblat = request.Snarfblat?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    ?? request.AdventureId;
                EnsureFieldMatches(fields, "snarfblat", expectedSnarfblat);
                if (fields.ContainsKey("adventure")) EnsureFieldMatches(fields, "adventure", request.AdventureId);
                if (fields.ContainsKey("location")) EnsureFieldMatches(fields, "location", request.AdventureId);
                break;
            case AdventureTransportOperation.Fight:
                if (!fields.ContainsKey("action") && request.Action is not AdventureFightAction)
                    throw new AdventureHttpTransportException("A fight action is required.");
                if (request.Action is AdventureFightAction fight)
                {
                    EnsureFieldMatches(fields, "action", fight.ActionId);
                    EnsureFieldMatches(fields, "round",
                        fight.Round.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                break;
            case AdventureTransportOperation.Choice:
                if ((!fields.ContainsKey("whichchoice") || !fields.ContainsKey("option"))
                    && request.Action is not AdventureChoiceAction)
                    throw new AdventureHttpTransportException("A choice id and option are required.");
                if (request.Action is AdventureChoiceAction choice)
                {
                    EnsureFieldMatches(fields, "whichchoice", choice.ChoiceId);
                    EnsureFieldMatches(fields, "option", choice.OptionId);
                }
                break;
            case AdventureTransportOperation.Refresh:
                if (fields.Count > 0)
                    throw new AdventureHttpTransportException("Refresh requests cannot carry form fields.");
                break;
        }
    }

    private static void EnsureFieldMatches(
        IReadOnlyDictionary<string, string> fields,
        string name,
        string expected)
    {
        if (fields.TryGetValue(name, out var actual)
            && !string.Equals(actual, expected, StringComparison.Ordinal))
            throw new AdventureHttpTransportException(
                $"Adventure form field '{name}' does not match the allowlisted action.");
    }

    private void ValidateRequest(
        AdventureTransportRequest request,
        AdventureTransportOperation operation)
    {
        if (request.Operation != operation)
            throw new AdventureHttpTransportException(
                $"The request operation {request.Operation} does not match {operation}.");
        if (string.IsNullOrWhiteSpace(request.AdventureId))
            throw new ArgumentException("An adventure id is required.", nameof(request));
        var role = request.Checkpoint?.Role ?? request.Role;
        if (_binding is null) return;
        if (_binding.Role != role)
            throw new AdventureHttpTransportException(
                $"Adventure request role {role} is not bound to {_binding.Role}.");
        if (!string.IsNullOrWhiteSpace(request.AccountId)
            && !_binding.Matches(request.AccountId))
            throw new AdventureHttpTransportException(
                "Adventure request account does not match the bound account.");
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
    }
}

/// <summary>Compatibility names for fixture hosts.</summary>
public class FixtureAdventureHttpTransport : AdventureHttpFixtureTransport
{
    public FixtureAdventureHttpTransport(AdventureHttpTransportOptions? options = null,
        AdventureTransportAccountBinding? binding = null)
        : base(options, binding)
    {
    }

    public FixtureAdventureHttpTransport(AdventureTransportAccountBinding binding,
        AdventureHttpTransportOptions? options = null)
        : base(binding, options)
    {
    }
}

public sealed class KoLAdventureHttpFixtureTransport : FixtureAdventureHttpTransport
{
    public KoLAdventureHttpFixtureTransport(AdventureHttpTransportOptions? options = null,
        AdventureTransportAccountBinding? binding = null)
        : base(options, binding)
    {
    }
}

public sealed class AdventureExecutionHttpFixtureTransport : FixtureAdventureHttpTransport
{
    public AdventureExecutionHttpFixtureTransport(AdventureHttpTransportOptions? options = null,
        AdventureTransportAccountBinding? binding = null)
        : base(options, binding)
    {
    }
}
