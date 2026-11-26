using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace KoLibri.Core.Basis;

/// <summary>Options for the official KoL login page.</summary>
public sealed record KoLLoginOptions
{
    public Uri BaseUri { get; init; } = new("https://www.kingdomofloathing.com/", UriKind.Absolute);
    public string UserAgent { get; init; } = KoLUserAgent.Value;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Minimum delay between login attempts made by one client.</summary>
    public TimeSpan MinimumRequestInterval { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>HTTPS host and redirect boundary for login requests.</summary>
    public KoLSecurityPolicy Security { get; init; } = new();
    /// <summary>Compatibility alias for callers using the explicit policy name.</summary>
    public KoLSecurityPolicy SecurityPolicy
    {
        get => Security;
        init => Security = value;
    }
    /// <summary>Convenience forwarding property for explicit test endpoints.</summary>
    public IReadOnlyCollection<string> AllowedHosts
    {
        get => Security.AllowedHosts;
        init => Security = Security with { AllowedHosts = value };
    }

    public Uri LoginUri => new(BaseUri, "login.php");

    public void Validate()
    {
        if (!BaseUri.IsAbsoluteUri || BaseUri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("KoL login requires an HTTPS base URI.", nameof(BaseUri));
        if (Timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(Timeout));
        if (MinimumRequestInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(MinimumRequestInterval));
        (Security ?? throw new ArgumentException("A KoL login security policy is required.", nameof(Security))).Validate();
        Security.EnsureAllowed(BaseUri, "KoL login base");
    }
}

public sealed record KoLLoginResult(
    string UserName,
    AccountCredentials Credentials,
    DateTimeOffset LoggedInAt);

public enum KoLLoginFailureReason
{
    Network,
    InvalidResponse,
    InvalidCredentials,
    MissingSession
}

public sealed class KoLLoginException : Exception
{
    public KoLLoginFailureReason Reason { get; }

    public KoLLoginException(KoLLoginFailureReason reason, string message, Exception? inner = null)
        : base(message, inner) => Reason = reason;
}

/// <summary>
/// Performs the normal KoL challenge-response login. The password is used only
/// in memory to calculate the MD5 response and is never placed in a URL, log,
/// profile or exception message.
/// </summary>
public sealed class KoLLoginClient
{
    private static readonly Regex ChallengeRegex = new(
        "name\\s*=\\s*[\\\"']challenge[\\\"'][^>]*value\\s*=\\s*[\\\"']([^\\\"']+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly KoLLoginOptions _options;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private DateTimeOffset? _lastRequestAt;

    private readonly Func<CookieContainer, HttpMessageHandler>? _handlerFactory;

    public KoLLoginClient(KoLLoginOptions? options = null)
    {
        _options = options ?? new KoLLoginOptions();
        _options.Validate();
    }

    /// <summary>
    /// For the KoLibri facade: <paramref name="handlerFactory"/> builds the whole handler chain (limiter
    /// included) around the cookie container the login reads its session cookie from.
    /// </summary>
    internal KoLLoginClient(KoLLoginOptions? options, Func<CookieContainer, HttpMessageHandler>? handlerFactory)
        : this(options) => _handlerFactory = handlerFactory;

    public async Task<KoLLoginResult> LoginAsync(
        string userName,
        string password,
        bool rememberPassword = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userName))
            throw new ArgumentException("A KoL character name is required.", nameof(userName));
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("A KoL password is required.", nameof(password));

        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lastRequestAt is { } lastRequest)
            {
                var wait = _options.MinimumRequestInterval - (DateTimeOffset.UtcNow - lastRequest);
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
            _lastRequestAt = DateTimeOffset.UtcNow;
            return await LoginCoreAsync(userName, password, rememberPassword, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task<KoLLoginResult> LoginCoreAsync(
        string userName,
        string password,
        bool rememberPassword,
        CancellationToken cancellationToken)
    {

        var cookieJar = new CookieContainer();
        using var handler = _handlerFactory?.Invoke(cookieJar) ?? new KoLRateLimitedHandler(new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = cookieJar,
            // Redirects are followed explicitly below so a Location header
            // cannot send login cookies to an untrusted host.
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All
        });
        using var client = new HttpClient(handler)
        {
            BaseAddress = _options.BaseUri,
            Timeout = _options.Timeout
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(_options.UserAgent);

        string loginPage;
        var loginUri = _options.LoginUri;
        try
        {
            using var pageResponse = await GetFollowingRedirectsAsync(client, loginUri, cancellationToken).ConfigureAwait(false);
            loginPage = await pageResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            pageResponse.EnsureSuccessStatusCode();
            if (KoLHtmlResponseClassifier.Classify(loginPage) == KoLHtmlResponseKind.Empty)
                throw new KoLLoginException(KoLLoginFailureReason.InvalidResponse, "KoL returned an empty login page.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            throw new KoLLoginException(KoLLoginFailureReason.Network, "KoL login page could not be reached.", exception);
        }

        var form = new Dictionary<string, string>
        {
            ["loggingin"] = "Yup.",
            ["loginname"] = userName.Trim(),
            ["submitbutton"] = "Log In"
        };

        var challengeMatch = ChallengeRegex.Match(loginPage);
        if (challengeMatch.Success)
        {
            // KoL expects the plaintext field to be present but empty when the
            // challenge-response path is used (the response carries the hash).
            var challenge = challengeMatch.Groups[1].Value;
            form["password"] = "";
            form["secure"] = "1";
            form["challenge"] = challenge;
            form["response"] = ComputeChallengeResponse(password, challenge);
        }
        else
        {
            // The live login page currently advertises secure=0 and no
            // challenge. HTTPS still protects the request; mirror the normal
            // browser/KoLmafia fallback so login remains functional when the
            // challenge endpoint is unavailable.
            form["password"] = password;
            form["secure"] = "0";
        }

        HttpResponseMessage loginResponse;
        try
        {
            using var content = new FormUrlEncodedContent(form);
            loginResponse = await PostFollowingRedirectsAsync(client, loginUri, content, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            throw new KoLLoginException(KoLLoginFailureReason.Network, "KoL login request failed.", exception);
        }

        var finalUri = loginResponse.RequestMessage?.RequestUri ?? loginUri;
        _options.Security.EnsureResponseUri(loginUri, finalUri);
        var finalPath = finalUri.AbsolutePath;
        var redirectedIntoGame = finalPath.EndsWith("/game.php", StringComparison.OrdinalIgnoreCase)
            || finalPath.EndsWith("/main.php", StringComparison.OrdinalIgnoreCase);

        string loginBody;
        try
        {
            loginBody = await loginResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            loginResponse.Dispose();
            throw new KoLLoginException(KoLLoginFailureReason.Network, "KoL login response could not be read.", exception);
        }

        try
        {
            loginResponse.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException exception)
        {
            throw new KoLLoginException(KoLLoginFailureReason.InvalidResponse, "KoL returned an unexpected login response.", exception);
        }
        finally
        {
            loginResponse.Dispose();
        }

        if (KoLHtmlResponseClassifier.Classify(loginBody) == KoLHtmlResponseKind.Empty)
            throw new KoLLoginException(KoLLoginFailureReason.InvalidResponse, "KoL returned an empty login response.");

        var cookies = cookieJar.GetCookies(_options.BaseUri)
            .Cast<Cookie>()
            .Where(cookie => !string.IsNullOrWhiteSpace(cookie.Value))
            .ToArray();
        var cookieHeader = string.Join("; ", cookies.Select(cookie => $"{cookie.Name}={cookie.Value}"));
        var charPassword = cookies.FirstOrDefault(cookie =>
            string.Equals(cookie.Name, "charpwd", StringComparison.OrdinalIgnoreCase))?.Value;

        // A failed login commonly returns the login form again with an error
        // message and no charpwd cookie. Never expose the response body in the
        // exception because it may contain account-specific data.
        if (!redirectedIntoGame)
            throw new KoLLoginException(KoLLoginFailureReason.InvalidCredentials, "KoL rejected the login.");

        // KoLmafia treats the redirect into game.php as the successful-login
        // signal. Depending on the server shard, the session is exposed as
        // charpwd or PHPSESSID; accept either after the verified redirect.
        var sessionCookie = charPassword
            ?? cookies.FirstOrDefault(cookie =>
                string.Equals(cookie.Name, "PHPSESSID", StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrWhiteSpace(sessionCookie))
            throw new KoLLoginException(KoLLoginFailureReason.MissingSession, "KoL accepted the login but returned no session cookie.");

        var accessToken = sessionCookie;
        var credentials = new AccountCredentials
        {
            AccessToken = accessToken,
            CookieHeader = cookieHeader,
            UserAgent = _options.UserAgent,
            Username = userName.Trim(),
            Password = rememberPassword ? password : null,
            // KoL session cookies are renewed by the server; keep the local
            // record non-expiring and validate it with a status request later.
            ExpiresAt = null
        };

        return new KoLLoginResult(userName.Trim(), credentials, DateTimeOffset.UtcNow);
    }

    public Task<KoLLoginResult> LoginAsync(
        string userName,
        string password,
        CancellationToken cancellationToken)
        => LoginAsync(userName, password, rememberPassword: false, cancellationToken);

    public static string ComputeChallengeResponse(string password, string challenge)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentException.ThrowIfNullOrEmpty(challenge);
        var passwordHash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(password))).ToLowerInvariant();
        var payload = Encoding.UTF8.GetBytes($"{passwordHash}:{challenge}");
        return Convert.ToHexString(MD5.HashData(payload)).ToLowerInvariant();
    }

    public static HttpClient CreateAuthenticatedClient(
        AccountCredentials credentials,
        Uri baseUri,
        TimeSpan? timeout = null,
        string? userAgent = null,
        KoLSecurityPolicy? securityPolicy = null,
        HttpMessageHandler? innerHandler = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(baseUri);
        var policy = securityPolicy ?? new KoLSecurityPolicy();
        policy.Validate();
        policy.EnsureAllowed(baseUri, "Authenticated KoL base");
        if (string.IsNullOrWhiteSpace(credentials.CookieHeader))
            throw new ArgumentException("Credentials do not contain a KoL cookie header.", nameof(credentials));

        KoLAllowlistHandler.EnsureNoAutoRedirect(innerHandler);
        HttpMessageHandler handler = innerHandler ?? new HttpClientHandler
        {
            // Callers must handle redirects themselves (or use a safe
            // request wrapper) so cookies cannot be sent cross-host.
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All
        };
        // Allowlist is outermost: a foreign target is refused before any wait or send.
        var client = new HttpClient(new KoLAllowlistHandler(new KoLRateLimitedHandler(handler), policy))
        {
            BaseAddress = baseUri,
            Timeout = timeout ?? TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent ?? credentials.UserAgent ?? KoLUserAgent.LegacyValue);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", credentials.CookieHeader);
        KoLAllowlistHandler.MarkTrusted(client);
        return client;
    }

    private async Task<HttpResponseMessage> GetFollowingRedirectsAsync(
        HttpClient client,
        Uri requestUri,
        CancellationToken cancellationToken)
    {
        _options.Security.EnsureAllowed(requestUri, "KoL login request");
        var current = requestUri;
        for (var redirect = 0; ; redirect++)
        {
            var response = await client.GetAsync(current, cancellationToken).ConfigureAwait(false);
            _options.Security.EnsureResponseUri(current, response.RequestMessage?.RequestUri);
            if (!IsRedirect(response.StatusCode))
                return response;
            if (redirect >= _options.Security.MaxRedirects)
            {
                response.Dispose();
                throw new KoLSecurityException("KoL login exceeded the redirect limit.");
            }
            var next = _options.Security.ResolveRedirect(current, response.Headers.Location?.ToString());
            response.Dispose();
            current = next;
        }
    }

    private async Task<HttpResponseMessage> PostFollowingRedirectsAsync(
        HttpClient client,
        Uri requestUri,
        HttpContent content,
        CancellationToken cancellationToken)
    {
        _options.Security.EnsureAllowed(requestUri, "KoL login request");
        var current = requestUri;
        var response = await client.PostAsync(current, content, cancellationToken).ConfigureAwait(false);
        for (var redirect = 0; IsRedirect(response.StatusCode); redirect++)
        {
            _options.Security.EnsureResponseUri(current, response.RequestMessage?.RequestUri);
            if (redirect >= _options.Security.MaxRedirects)
            {
                response.Dispose();
                throw new KoLSecurityException("KoL login exceeded the redirect limit.");
            }
            var next = _options.Security.ResolveRedirect(current, response.Headers.Location?.ToString());
            response.Dispose();
            // KoL's login endpoint responds to POST with a normal 302/303
            // browser redirect. Follow it as GET while retaining cookies.
            current = next;
            response = await client.GetAsync(current, cancellationToken).ConfigureAwait(false);
        }
        _options.Security.EnsureResponseUri(current, response.RequestMessage?.RequestUri);
        return response;
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect
            or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;
}
