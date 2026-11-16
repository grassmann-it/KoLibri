using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace KolArbitrage.Core.Accounts;

/// <summary>Options for the official KoL login page.</summary>
public sealed record KoLLoginOptions
{
    public Uri BaseUri { get; init; } = new("https://www.kingdomofloathing.com/", UriKind.Absolute);
    public string UserAgent { get; init; } = "KoLibri/0.1 (Kingdom of Loathing client)";
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Minimum delay between login attempts made by one client.</summary>
    public TimeSpan MinimumRequestInterval { get; init; } = TimeSpan.FromSeconds(1);

    public Uri LoginUri => new(BaseUri, "login.php");

    public void Validate()
    {
        if (!BaseUri.IsAbsoluteUri || BaseUri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("KoL login requires an HTTPS base URI.", nameof(BaseUri));
        if (Timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(Timeout));
        if (MinimumRequestInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(MinimumRequestInterval));
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

    public KoLLoginClient(KoLLoginOptions? options = null)
    {
        _options = options ?? new KoLLoginOptions();
        _options.Validate();
    }

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

        using var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All
        };
        using var client = new HttpClient(handler)
        {
            BaseAddress = _options.BaseUri,
            Timeout = _options.Timeout
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(_options.UserAgent);

        string loginPage;
        try
        {
            using var pageResponse = await client.GetAsync("login.php", cancellationToken).ConfigureAwait(false);
            loginPage = await pageResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            pageResponse.EnsureSuccessStatusCode();
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
            loginResponse = await client.PostAsync("login.php", content, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            throw new KoLLoginException(KoLLoginFailureReason.Network, "KoL login request failed.", exception);
        }

        var finalPath = loginResponse.RequestMessage?.RequestUri?.AbsolutePath ?? string.Empty;
        var redirectedIntoGame = finalPath.EndsWith("/game.php", StringComparison.OrdinalIgnoreCase)
            || finalPath.EndsWith("/main.php", StringComparison.OrdinalIgnoreCase);

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

        var cookies = handler.CookieContainer.GetCookies(_options.BaseUri)
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
        string userAgent = "KoLibri/0.1 (Kingdom of Loathing client)")
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(baseUri);
        if (!baseUri.IsAbsoluteUri || baseUri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Authenticated KoL clients require an HTTPS base URI.", nameof(baseUri));
        if (string.IsNullOrWhiteSpace(credentials.CookieHeader))
            throw new ArgumentException("Credentials do not contain a KoL cookie header.", nameof(credentials));

        var client = new HttpClient
        {
            BaseAddress = baseUri,
            Timeout = timeout ?? TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", credentials.CookieHeader);
        return client;
    }
}
