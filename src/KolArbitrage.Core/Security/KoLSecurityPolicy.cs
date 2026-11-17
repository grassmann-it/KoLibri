using System.Net;

namespace KolArbitrage.Core.Security;

/// <summary>
/// Host names operated by the official Kingdom of Loathing service.  The
/// allowlist is intentionally exact; a host such as
/// <c>kingdomofloathing.com.attacker.example</c> must never be treated as KoL.
/// Test or self-hosted endpoints can be enabled by supplying an explicit
/// allowlist to <see cref="KoLSecurityPolicy"/>.
/// </summary>
public static class KoLHostAllowlist
{
    public static IReadOnlySet<string> OfficialHosts { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "kingdomofloathing.com",
            "www.kingdomofloathing.com"
        };

    public static bool Contains(Uri uri, IEnumerable<string>? allowedHosts = null)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var hosts = allowedHosts ?? OfficialHosts;
        return uri.IsAbsoluteUri && hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// HTTP boundary policy for KoL clients.  It validates the endpoint before a
/// request and validates the final response URI after a request.  The latter
    /// check is required even when a host application has configured an
    /// HttpClient that follows redirects automatically.
/// </summary>
public sealed record KoLSecurityPolicy
{
    public IReadOnlyCollection<string> AllowedHosts { get; init; } = KoLHostAllowlist.OfficialHosts;
    public bool RequireHttps { get; init; } = true;
    public int MaxRedirects { get; init; } = 5;

    public void Validate()
    {
        if (AllowedHosts is null || AllowedHosts.Count == 0)
            throw new ArgumentException("At least one allowed KoL host is required.", nameof(AllowedHosts));
        foreach (var host in AllowedHosts)
        {
            if (string.IsNullOrWhiteSpace(host))
                throw new ArgumentException("Allowed KoL hosts cannot be blank.", nameof(AllowedHosts));
            // Uri host parsing gives us the same canonical syntax used by the
            // request/response checks and rejects path/query injection.
            if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
                throw new ArgumentException($"'{host}' is not a valid host name.", nameof(AllowedHosts));
        }
        if (MaxRedirects < 0 || MaxRedirects > 20)
            throw new ArgumentOutOfRangeException(nameof(MaxRedirects));
    }

    public bool IsAllowed(Uri uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
            return false;
        if (RequireHttps && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!RequireHttps && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            return false;
        return AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    }

    public void EnsureAllowed(Uri uri, string operation = "KoL request")
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!IsAllowed(uri))
            throw new KoLSecurityException($"{operation} URI is outside the KoL host allowlist: {Redact(uri)}");
    }

    /// <summary>
    /// Ensures an automatic redirect did not leave the exact request host.
    /// Same-host, same-port and same-scheme redirects are allowed; changing from
    /// kingdomofloathing.com to www.kingdomofloathing.com is deliberately
    /// rejected so that cookies are never forwarded across hosts.
    /// </summary>
    public void EnsureResponseUri(Uri requestUri, Uri? responseUri)
    {
        EnsureAllowed(requestUri, "KoL request");
        if (responseUri is null)
            return;
        EnsureAllowed(responseUri, "KoL response");
        if (!string.Equals(requestUri.Host, responseUri.Host, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(requestUri.Scheme, responseUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || requestUri.Port != responseUri.Port)
        {
            throw new KoLSecurityException(
                $"KoL request was redirected to a different host, port or scheme ({Redact(requestUri)} -> {Redact(responseUri)}).");
        }
    }

    /// <summary>Checks a Location header before a caller follows it manually.</summary>
    public Uri ResolveRedirect(Uri requestUri, string? location)
    {
        EnsureAllowed(requestUri, "KoL request");
        if (string.IsNullOrWhiteSpace(location))
            throw new KoLSecurityException("KoL returned a redirect without a Location header.");
        if (!Uri.TryCreate(requestUri, location, out var destination) || destination is null)
            throw new KoLSecurityException("KoL returned an invalid redirect location.");
        EnsureResponseUri(requestUri, destination);
        return destination;
    }

    private static string Redact(Uri uri) =>
        $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";
}

/// <summary>Raised when an HTTP endpoint crosses the configured KoL boundary.</summary>
public sealed class KoLSecurityException : InvalidOperationException
{
    public KoLSecurityException(string message) : base(message)
    {
    }
}

/// <summary>Classification used by HTML endpoints before parsing content.</summary>
public enum KoLHtmlResponseKind
{
    Content = 0,
    Empty = 1,
    LoginPage = 2
}

/// <summary>
/// Conservative classifier for HTML returned by KoL pages.  It avoids
/// treating an unauthenticated login form or an empty proxy response as a
/// legitimate zero-listing page.
/// </summary>
public static class KoLHtmlResponseClassifier
{
    private static readonly System.Text.RegularExpressions.Regex LoginMarker = new(
        "(?:<input\\b[^>]*\\bname\\s*=\\s*[\\\"']loginname[\\\"']|<form\\b[^>]*\\b(?:action\\s*=\\s*[\\\"'][^\\\"']*login\\.php|id\\s*=\\s*[\\\"'][^\\\"']*login)|(?:please\\s+log\\s+in|session\\s+has\\s+expired|invalid\\s+session))",
        System.Text.RegularExpressions.RegexOptions.Compiled
            | System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex Tags = new(
        "<[^>]+>",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.Singleline);

    public static KoLHtmlResponseKind Classify(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return KoLHtmlResponseKind.Empty;
        if (LoginMarker.IsMatch(html))
            return KoLHtmlResponseKind.LoginPage;
        var text = System.Net.WebUtility.HtmlDecode(Tags.Replace(html, " "));
        return string.IsNullOrWhiteSpace(text) ? KoLHtmlResponseKind.Empty : KoLHtmlResponseKind.Content;
    }

    public static void EnsureContent(string? html, Uri? sourceUri = null)
    {
        var kind = Classify(html);
        if (kind == KoLHtmlResponseKind.Content)
            return;
        var suffix = sourceUri is null ? string.Empty : $" from {sourceUri.AbsolutePath}";
        throw new KoLHtmlResponseException(kind, kind == KoLHtmlResponseKind.LoginPage
            ? $"KoL returned a login page{suffix}; the session is not authenticated."
            : $"KoL returned an empty HTML response{suffix}.");
    }
}

/// <summary>Raised when an HTML endpoint returns login or empty content.</summary>
public sealed class KoLHtmlResponseException : InvalidOperationException
{
    public KoLHtmlResponseKind Kind { get; }

    public KoLHtmlResponseException(KoLHtmlResponseKind kind, string message) : base(message)
        => Kind = kind;
}
