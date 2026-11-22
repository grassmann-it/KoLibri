namespace KoLibri.Core.Basis;

/// <summary>
/// Rejects any request whose URI is outside the KoL host allowlist before it
/// reaches the network, so a session cookie can never be sent to a foreign host.
/// </summary>
public sealed class KoLAllowlistHandler : DelegatingHandler
{
    private readonly KoLSecurityPolicy _policy;

    public KoLAllowlistHandler(HttpMessageHandler inner, KoLSecurityPolicy policy) : base(inner)
        => _policy = policy ?? throw new ArgumentNullException(nameof(policy));

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<HttpClient, object> Trusted = new();

    /// <summary>Marks a client built by this library (allowlist handler, no auto-redirect) as safe for cookies.</summary>
    public static void MarkTrusted(HttpClient client) => Trusted.AddOrUpdate(client, new object());

    /// <summary>True when the client was produced by <see cref="MarkTrusted"/>.</summary>
    public static bool IsTrusted(HttpClient client) => Trusted.TryGetValue(client, out _);

    private void EnsureDestination(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestUri is null || !request.RequestUri.IsAbsoluteUri)
            throw new KoLSecurityException("Authenticated KoL request has no absolute URI.");
        _policy.EnsureAllowed(request.RequestUri, "Authenticated KoL request");
    }

    private static void EnsureNotRedirected(HttpRequestMessage request, HttpResponseMessage response)
    {
        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is not null && finalUri != request.RequestUri)
        {
            response.Dispose();
            throw new KoLSecurityException("An inner handler followed a redirect automatically; authenticated KoL transports must not auto-redirect.");
        }
    }

    /// <summary>Synchronous sends get the same destination and redirect checks as <see cref="SendAsync"/>.</summary>
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        EnsureDestination(request);
        var response = base.Send(request, cancellationToken);
        EnsureNotRedirected(request, response);
        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestUri is null || !request.RequestUri.IsAbsoluteUri)
            throw new KoLSecurityException("Authenticated KoL request has no absolute URI.");
        _policy.EnsureAllowed(request.RequestUri, "Authenticated KoL request");
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is not null && finalUri != request.RequestUri)
        {
            // An inner handler followed a redirect by itself; the cookie may
            // already have travelled. Refuse to hand the response on.
            response.Dispose();
            throw new KoLSecurityException("An inner handler followed a redirect automatically; authenticated KoL transports must not auto-redirect.");
        }
        return response;
    }

    /// <summary>Rejects handler chains that follow redirects on their own.</summary>
    public static void EnsureNoAutoRedirect(HttpMessageHandler? handler)
    {
        for (var current = handler; current is not null; current = (current as DelegatingHandler)?.InnerHandler)
        {
            var auto = current switch
            {
                HttpClientHandler h => h.AllowAutoRedirect,
                SocketsHttpHandler h => h.AllowAutoRedirect,
                _ => false
            };
            if (auto)
                throw new KoLSecurityException("Authenticated KoL transports must not use a handler with AllowAutoRedirect enabled.");
        }
    }
}
