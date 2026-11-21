using System.Net;
using System.Security.Cryptography;
using System.Text;
using KoLibri.Core.Basis;

namespace KoLibri.Core.Spiel;

/// <summary>
/// Narrow HTTP adapter for the four adventure server boundaries. The adapter
/// owns no cookies and performs no retries; the injected HttpClient is expected
/// to be bound to the account session by the host.
/// </summary>
/// <remarks>
/// Construction is side-effect free. A request is sent only when one of the
/// explicit transport methods is called by an approval-bound gateway. Routes,
/// host, scheme, redirect behavior and response size are checked before any
/// response is handed back to the parser.
/// </remarks>
public class HttpAdventureExecutionTransport : IAdventureExecutionHttpTransport, IDisposable
{
    private readonly HttpClient _http;
    private readonly AdventureHttpTransportOptions _options;
    private readonly KoLSecurityPolicy _security;
    private readonly AdventureTransportAccountBinding? _binding;
    private bool _disposed;
    private int _httpRequestCount;

    public HttpAdventureExecutionTransport(
        HttpClient httpClient,
        AdventureHttpTransportOptions? options = null)
        : this(httpClient, binding: null, options, initialize: true)
    {
    }

    public HttpAdventureExecutionTransport(
        HttpClient httpClient,
        AdventureTransportAccountBinding binding,
        AdventureHttpTransportOptions? options = null)
        : this(httpClient, (AdventureTransportAccountBinding?)binding, options, initialize: true)
    {
    }

    public HttpAdventureExecutionTransport(
        HttpClient httpClient,
        AccountRole role,
        string accountId,
        AdventureHttpTransportOptions? options = null,
        string? displayName = null)
        : this(httpClient, new AdventureTransportAccountBinding(role, accountId, displayName), options, initialize: true)
    {
    }

    public HttpAdventureExecutionTransport(
        HttpClient httpClient,
        Uri baseUri,
        AdventureTransportAccountBinding? binding = null,
        AdventureHttpTransportOptions? options = null)
        : this(httpClient, binding,
            (options ?? new AdventureHttpTransportOptions()) with { BaseUri = baseUri }, initialize: true)
    {
    }

    public HttpAdventureExecutionTransport(
        HttpClient httpClient,
        Uri baseUri,
        AdventureHttpTransportOptions? options,
        AdventureTransportAccountBinding? binding = null)
        : this(httpClient, binding,
            (options ?? new AdventureHttpTransportOptions()) with { BaseUri = baseUri }, initialize: true)
    {
    }

    private HttpAdventureExecutionTransport(
        HttpClient httpClient,
        AdventureTransportAccountBinding? binding,
        AdventureHttpTransportOptions? options,
        bool initialize)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? new AdventureHttpTransportOptions();
        _options.Validate();
        _security = _options.Security;
        _binding = binding;
        _binding?.Validate();
    }

    public AdventureHttpTransportOptions Options => _options;

    public AdventureHttpRouteMap Routes => AdventureHttpRouteMap.Default;

    public AdventureTransportAccountBinding? Binding => _binding;

    public AdventureTransportAccountBinding? AccountBinding => _binding;

    public AccountRole? Role => _binding?.Role;

    public string? AccountId => _binding?.AccountId;

    public bool UsesAccountBinding => _binding is not null;

    public int HttpRequestCount => Volatile.Read(ref _httpRequestCount);

    public bool UsedHttp => HttpRequestCount > 0;

    public AdventureTransportRequest? LastRequest { get; private set; }

    public AdventureTransportResponse? LastResponse { get; private set; }

    public Task<AdventureTransportResponse> StartAdventureAsync(
        AdventureTransportRequest request,
        CancellationToken cancellationToken = default)
        => SendAsync(request, AdventureTransportOperation.Adventure, cancellationToken);

    public Task<AdventureTransportResponse> AdventureAsync(
        AdventureTransportRequest request,
        CancellationToken cancellationToken = default)
        => StartAdventureAsync(request, cancellationToken);

    public Task<AdventureTransportResponse> SubmitFightAsync(
        AdventureTransportRequest request,
        CancellationToken cancellationToken = default)
        => SendAsync(request, AdventureTransportOperation.Fight, cancellationToken);

    public Task<AdventureTransportResponse> FightAsync(
        AdventureTransportRequest request,
        CancellationToken cancellationToken = default)
        => SubmitFightAsync(request, cancellationToken);

    public Task<AdventureTransportResponse> SubmitChoiceAsync(
        AdventureTransportRequest request,
        CancellationToken cancellationToken = default)
        => SendAsync(request, AdventureTransportOperation.Choice, cancellationToken);

    public Task<AdventureTransportResponse> ChoiceAsync(
        AdventureTransportRequest request,
        CancellationToken cancellationToken = default)
        => SubmitChoiceAsync(request, cancellationToken);

    public Task<AdventureTransportResponse> RefreshAsync(
        AdventureTransportRequest request,
        CancellationToken cancellationToken = default)
        => SendAsync(request, AdventureTransportOperation.Refresh, cancellationToken);

    private async Task<AdventureTransportResponse> SendAsync(
        AdventureTransportRequest request,
        AdventureTransportOperation operation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        ValidateRequest(request, operation);
        var route = operation == AdventureTransportOperation.Refresh ? request.Checkpoint?.Phase switch
        {
            AdventureRunPhase.Fight => AdventureTransportOperation.Fight,
            AdventureRunPhase.Choice => AdventureTransportOperation.Choice,
            _ => operation
        } : operation;
        var uri = BuildUri(route);
        _security.EnsureAllowed(uri, $"KoL adventure {operation} request");

        using var message = new HttpRequestMessage(
            operation == AdventureTransportOperation.Refresh ? HttpMethod.Get : HttpMethod.Post,
            uri);

        if (operation != AdventureTransportOperation.Refresh)
        {
            var fields = BuildFields(request, operation);
            message.Content = new FormUrlEncodedContent(fields);
        }

        using var timeout = CreateTimeoutSource(cancellationToken);
        Interlocked.Increment(ref _httpRequestCount);
        LastRequest = request;

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"KoL adventure {operation} request timed out.");
        }

        using (response)
        {
            try
            {
                EnsureNoRedirect(uri, response);
                var responseUri = response.RequestMessage?.RequestUri ?? uri;
                _security.EnsureResponseUri(uri, responseUri);
                var html = await ReadBoundedBodyAsync(response.Content, timeout.Token).ConfigureAwait(false);
                var headers = CopyHeaders(response);
                var responseHash = Convert.ToHexString(
                        SHA256.HashData(Encoding.UTF8.GetBytes(html)))
                    .ToLowerInvariant();

                var result = new AdventureTransportResponse
                {
                    RequestId = request.RequestId,
                    RunId = request.RunId,
                    AdventureId = request.AdventureId,
                    ResponseUri = responseUri,
                    StatusCode = response.StatusCode,
                    Headers = headers,
                    Html = html,
                    BodyBytes = Encoding.UTF8.GetByteCount(html),
                    ResponseHash = responseHash,
                    RequestFingerprint = request.ActionFingerprint,
                    Accepted = response.IsSuccessStatusCode,
                    Message = response.IsSuccessStatusCode
                        ? null
                        : $"KoL adventure request returned HTTP {(int)response.StatusCode}."
                };
                LastResponse = result;
                return result;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"KoL adventure {operation} response timed out.");
            }
        }
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
        if (request.AdventureId.Length > 200)
            throw new ArgumentException("An adventure id is too long.", nameof(request));
        if (request.RunId.Length > 200)
            throw new ArgumentException("A run id is too long.", nameof(request));
        if (operation == AdventureTransportOperation.Refresh && request.Fields.Count > 0)
            throw new AdventureHttpTransportException("Refresh requests cannot carry form fields.");

        var role = request.Checkpoint?.Role ?? request.Role;
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(request.Role));

        if (_binding is null) return;
        if (_binding.Role != role)
            throw new AdventureHttpTransportException(
                $"Adventure request role {role} is not bound to {_binding.Role}.");
        if (!string.IsNullOrWhiteSpace(request.AccountId)
            && !_binding.Matches(request.AccountId))
            throw new AdventureHttpTransportException(
                "Adventure request account does not match the bound account.");
    }

    private Uri BuildUri(AdventureTransportOperation operation)
        => new(_options.BaseUri, AdventureHttpRoutes.For(operation));

    private static IReadOnlyDictionary<string, string> BuildFields(
        AdventureTransportRequest request,
        AdventureTransportOperation operation)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in request.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Key))
                throw new AdventureHttpTransportException("Adventure form field names cannot be blank.");
            if (field.Key.Length > 80 || field.Value is { Length: > 4000 })
                throw new AdventureHttpTransportException("Adventure form field exceeds its bound.");
            fields[field.Key] = field.Value ?? string.Empty;
        }
        if (fields.Keys.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
            throw new AdventureHttpTransportException("Adventure form field names must be unique.");

        switch (operation)
        {
            case AdventureTransportOperation.Adventure:
                var expectedSnarfblat = request.Snarfblat?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    ?? request.AdventureId;
                if (!fields.ContainsKey("snarfblat"))
                    fields["snarfblat"] = expectedSnarfblat;
                EnsureFieldMatches(fields, "snarfblat", expectedSnarfblat);
                if (fields.ContainsKey("adventure")) EnsureFieldMatches(fields, "adventure", request.AdventureId);
                if (fields.ContainsKey("location")) EnsureFieldMatches(fields, "location", request.AdventureId);
                break;
            case AdventureTransportOperation.Fight:
                if (!fields.ContainsKey("action") && !string.IsNullOrWhiteSpace(request.ActionId))
                    fields["action"] = request.ActionId!;
                if (!fields.ContainsKey("round") && request.Round is { } round)
                    fields["round"] = round.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!fields.ContainsKey("action"))
                    throw new AdventureHttpTransportException("A fight action is required.");
                if (request.Action is AdventureFightAction fight)
                {
                    EnsureFieldMatches(fields, "action", fight.ActionId);
                    EnsureFieldMatches(fields, "round",
                        fight.Round.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                var actionId = fields["action"];
                if (actionId.StartsWith("skill:", StringComparison.Ordinal))
                {
                    fields["action"] = "skill";
                    fields["whichskill"] = actionId["skill:".Length..];
                }
                else if (actionId.StartsWith("item:", StringComparison.Ordinal))
                {
                    fields["action"] = "useitem";
                    fields["whichitem"] = actionId["item:".Length..];
                }
                else if (actionId == "run")
                {
                    fields["action"] = "runaway";
                }
                break;
            case AdventureTransportOperation.Choice:
                if (!fields.ContainsKey("whichchoice") && !string.IsNullOrWhiteSpace(request.ChoiceId))
                    fields["whichchoice"] = request.ChoiceId!;
                if (!fields.ContainsKey("option") && !string.IsNullOrWhiteSpace(request.OptionId))
                    fields["option"] = request.OptionId!;
                if (!fields.ContainsKey("whichchoice") || !fields.ContainsKey("option"))
                    throw new AdventureHttpTransportException("A choice id and option are required.");
                if (request.Action is AdventureChoiceAction choice)
                {
                    EnsureFieldMatches(fields, "whichchoice", choice.ChoiceId);
                    EnsureFieldMatches(fields, "option", choice.OptionId);
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }

        return fields;
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

    private CancellationTokenSource CreateTimeoutSource(CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_options.RequestTimeout != System.Threading.Timeout.InfiniteTimeSpan)
            source.CancelAfter(_options.RequestTimeout);
        return source;
    }

    private static void EnsureNoRedirect(Uri requestUri, HttpResponseMessage response)
    {
        var statusCode = (int)response.StatusCode;
        var finalUri = response.RequestMessage?.RequestUri;
        var changedUri = finalUri is not null && !UriEquals(requestUri, finalUri);
        if ((statusCode is >= 300 and <= 399)
            || response.Headers.Location is not null
            || changedUri)
            throw new AdventureHttpTransportException(
                "KoL adventure redirects are disabled; refresh the session manually.");
    }

    private async Task<string> ReadBoundedBodyAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        var maximum = _options.MaximumResponseBodyBytes;
        if (content.Headers.ContentLength is { } declared && declared > maximum)
            throw new AdventureResponseTooLargeException(maximum);

        await using var stream = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var output = new MemoryStream(
            maximum > 0 ? Math.Min(maximum, 81920) : 0);
        var bufferLength = Math.Min(81920, Math.Max(1, maximum == int.MaxValue ? int.MaxValue : maximum + 1));
        var buffer = new byte[bufferLength];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > maximum)
                throw new AdventureResponseTooLargeException(maximum);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }

        return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

    private static IReadOnlyDictionary<string, string> CopyHeaders(HttpResponseMessage response)
        => response.Headers
            .Concat(response.Content.Headers)
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                x => x.Key,
                x => string.Join(",", x.SelectMany(value => value.Value)),
                StringComparer.OrdinalIgnoreCase);

    private static bool UriEquals(Uri left, Uri right)
        => Uri.Compare(left, right, UriComponents.AbsoluteUri,
            UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;

    public void Dispose()
    {
        _disposed = true;
    }
}

/// <summary>Short name used by hosts that put the protocol before the type.</summary>
public class AdventureHttpTransport : HttpAdventureExecutionTransport
{
    public AdventureHttpTransport(HttpClient httpClient, AdventureHttpTransportOptions? options = null)
        : base(httpClient, options)
    {
    }

    public AdventureHttpTransport(
        HttpClient httpClient,
        AdventureTransportAccountBinding binding,
        AdventureHttpTransportOptions? options = null)
        : base(httpClient, binding, options)
    {
    }

    public AdventureHttpTransport(
        HttpClient httpClient,
        AccountRole role,
        string accountId,
        AdventureHttpTransportOptions? options = null,
        string? displayName = null)
        : base(httpClient, role, accountId, options, displayName)
    {
    }

    public AdventureHttpTransport(
        HttpClient httpClient,
        Uri baseUri,
        AdventureHttpTransportOptions? options = null,
        AdventureTransportAccountBinding? binding = null)
        : base(httpClient, baseUri, options, binding)
    {
    }
}

/// <summary>Compatibility alias for the KoL-prefixed adapter name.</summary>
public sealed class KoLAdventureHttpTransport : AdventureHttpTransport
{
    public KoLAdventureHttpTransport(HttpClient httpClient, AdventureHttpTransportOptions? options = null)
        : base(httpClient, options)
    {
    }

    public KoLAdventureHttpTransport(
        HttpClient httpClient,
        AdventureTransportAccountBinding binding,
        AdventureHttpTransportOptions? options = null)
        : base(httpClient, binding, options)
    {
    }

    public KoLAdventureHttpTransport(
        HttpClient httpClient,
        Uri baseUri,
        AdventureHttpTransportOptions? options = null,
        AdventureTransportAccountBinding? binding = null)
        : base(httpClient, baseUri, options, binding)
    {
    }
}

public class AdventureExecutionHttpTransport : HttpAdventureExecutionTransport
{
    public AdventureExecutionHttpTransport(HttpClient httpClient, AdventureHttpTransportOptions? options = null)
        : base(httpClient, options)
    {
    }

    public AdventureExecutionHttpTransport(
        HttpClient httpClient,
        AdventureTransportAccountBinding binding,
        AdventureHttpTransportOptions? options = null)
        : base(httpClient, binding, options)
    {
    }

    public AdventureExecutionHttpTransport(
        HttpClient httpClient,
        Uri baseUri,
        AdventureHttpTransportOptions? options = null,
        AdventureTransportAccountBinding? binding = null)
        : base(httpClient, baseUri, options, binding)
    {
    }
}

public sealed class KoLAdventureExecutionHttpTransport : AdventureExecutionHttpTransport
{
    public KoLAdventureExecutionHttpTransport(HttpClient httpClient, AdventureHttpTransportOptions? options = null)
        : base(httpClient, options)
    {
    }

    public KoLAdventureExecutionHttpTransport(
        HttpClient httpClient,
        AdventureTransportAccountBinding binding,
        AdventureHttpTransportOptions? options = null)
        : base(httpClient, binding, options)
    {
    }

    public KoLAdventureExecutionHttpTransport(
        HttpClient httpClient,
        Uri baseUri,
        AdventureHttpTransportOptions? options = null,
        AdventureTransportAccountBinding? binding = null)
        : base(httpClient, baseUri, options, binding)
    {
    }
}

public sealed class HttpKoLAdventureTransport : AdventureExecutionHttpTransport
{
    public HttpKoLAdventureTransport(HttpClient httpClient, AdventureHttpTransportOptions? options = null)
        : base(httpClient, options)
    {
    }

    public HttpKoLAdventureTransport(
        HttpClient httpClient,
        AdventureTransportAccountBinding binding,
        AdventureHttpTransportOptions? options = null)
        : base(httpClient, binding, options)
    {
    }

    public HttpKoLAdventureTransport(
        HttpClient httpClient,
        Uri baseUri,
        AdventureHttpTransportOptions? options = null,
        AdventureTransportAccountBinding? binding = null)
        : base(httpClient, baseUri, options, binding)
    {
    }
}
