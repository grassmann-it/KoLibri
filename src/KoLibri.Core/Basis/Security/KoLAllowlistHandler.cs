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

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestUri is null || !request.RequestUri.IsAbsoluteUri)
            throw new KoLSecurityException("Authenticated KoL request has no absolute URI.");
        _policy.EnsureAllowed(request.RequestUri, "Authenticated KoL request");
        return base.SendAsync(request, cancellationToken);
    }
}
