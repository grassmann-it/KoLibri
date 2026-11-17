using System.Net;
using System.Net.Http.Headers;
using KolArbitrage.Core.Security;

namespace KolArbitrage.Core.Requests;

/// <summary>
/// Minimal HttpClient transport.  It does not own the client or its handler,
/// so hosts can provide a cookie container and an explicit no-redirect
/// handler. Response bodies are bounded before classification.
/// </summary>
public sealed class HttpClientKoLRequestTransport : IKoLRequestTransport
{
    private readonly HttpClient _http;
    private readonly Uri? _baseUri;
    private readonly int _maximumBodyBytes;
    private readonly KoLSecurityPolicy _securityPolicy;

    public HttpClientKoLRequestTransport(
        HttpClient httpClient,
        Uri? baseUri = null,
        int maximumBodyBytes = 1_048_576,
        KoLSecurityPolicy? securityPolicy = null)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _baseUri = baseUri;
        if (maximumBodyBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBodyBytes));
        _maximumBodyBytes = maximumBodyBytes;
        _securityPolicy = securityPolicy ?? new KoLSecurityPolicy();
        _securityPolicy.Validate();
    }

    public async Task<KoLResponse> SendAsync(
        KoLRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var uri = ResolveUri(request.Uri);
        _securityPolicy.EnsureAllowed(uri);
        using var message = new HttpRequestMessage(request.Method, uri);
        foreach (var header in request.Headers)
        {
            if (!message.Headers.TryAddWithoutValidation(header.Key, header.Value))
                message.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Body is not null)
        {
            message.Content = new StringContent(request.Body);
            if (request.Headers.TryGetValue("Content-Type", out var contentType) &&
                MediaTypeHeaderValue.TryParse(contentType, out var parsed))
                message.Content.Headers.ContentType = parsed;
        }
        else if (request.Parameters.Count > 0)
        {
            message.Content = new FormUrlEncodedContent(request.Parameters);
        }

        try
        {
            using var response = await _http.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            _securityPolicy.EnsureResponseUri(uri, response.RequestMessage?.RequestUri);
            var body = await ReadBoundedBodyAsync(response, cancellationToken).ConfigureAwait(false);
            var retryAfter = ParseRetryAfter(response.Headers, response.Content.Headers);
            var headers = response.Headers
                .Concat(response.Content.Headers)
                .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => string.Join(",", x.SelectMany(v => v.Value)),
                    StringComparer.OrdinalIgnoreCase);
            return new KoLResponse
            {
                StatusCode = (int)response.StatusCode,
                RequestUri = uri,
                Body = body,
                Headers = headers,
                ReceivedAt = DateTimeOffset.UtcNow,
                RetryAfter = retryAfter,
                HasSessionCookie = response.Headers.TryGetValues("Set-Cookie", out var cookies) &&
                    cookies.Any() ? true : null
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new KoLResponse
            {
                RequestUri = uri,
                ReceivedAt = DateTimeOffset.UtcNow,
                Classification = KoLResponseKind.Transient,
                Error = "The KoL request timed out."
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return KoLResponse.TransientFailure(exception, uri);
        }
    }

    private Uri ResolveUri(Uri route)
    {
        if (route.IsAbsoluteUri) return route;
        if (_baseUri is null)
            throw new InvalidOperationException("A base URI is required for relative KoL routes.");
        return new Uri(_baseUri, route);
    }

    private async Task<string?> ReadBoundedBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (_maximumBodyBytes == 0) return string.Empty;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await ReadBoundedStreamAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ReadBoundedStreamAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[Math.Min(81920, Math.Max(1, _maximumBodyBytes))];
        var total = 0;
        await using var output = new MemoryStream();
        while (total < _maximumBodyBytes)
        {
            var wanted = Math.Min(buffer.Length, _maximumBodyBytes - total);
            var read = await stream.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;
        }
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }

    private static TimeSpan? ParseRetryAfter(
        HttpResponseHeaders headers,
        HttpContentHeaders contentHeaders)
    {
        if (!headers.TryGetValues("Retry-After", out var values) &&
            !contentHeaders.TryGetValues("Retry-After", out values))
            return null;
        var value = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (int.TryParse(value, out var seconds) && seconds >= 0)
            return TimeSpan.FromSeconds(seconds);
        if (DateTimeOffset.TryParse(value, out var at))
        {
            var delay = at - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }
        return null;
    }
}
