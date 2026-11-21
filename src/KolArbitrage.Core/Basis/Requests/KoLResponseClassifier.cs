using System.Net;
using System.Text.RegularExpressions;

namespace KolArbitrage.Core.Basis;

/// <summary>
/// Classifies KoL responses using status, route and a small set of body
/// markers.  Markers are deliberately conservative and bounded: the full
/// response is retained by the transport only up to its configured limit.
/// </summary>
public static partial class KoLResponseClassifier
{
    public static KoLResponseKind Classify(KoLRequest request, KoLResponse response)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);
        if (response.Classification != KoLResponseKind.Unknown)
            return response.Classification;

        var status = response.StatusCode;
        var body = response.Body ?? string.Empty;
        var route = request.PathAndQuery;

        // A 200 response containing login HTML is the common stale-session
        // case and must be classified before route/status success checks.
        if (response.IsLoginPage || IsLoginBody(body) ||
            status is 401 or 403 || IsLoginRoute(route) || IsLoginRedirect(response))
            return KoLResponseKind.Login;

        if (status == 429 || IsRateLimitBody(body))
            return KoLResponseKind.RateLimited;

        if (status is 408 or 425 || status is >= 500 and <= 599 ||
            IsTransientBody(body) ||
            (!status.HasValue && !string.IsNullOrWhiteSpace(response.Error)))
            return KoLResponseKind.Transient;

        // Hosts should disable automatic redirects so the final destination
        // can be checked. A non-login redirect is therefore a transient
        // transport condition until the caller explicitly reconciles it.
        if (status is >= 300 and <= 399)
            return KoLResponseKind.Transient;

        if (status is >= 400 and <= 499)
            return KoLResponseKind.ClientError;

        // Path markers identify a page even where the HTML has no form (for
        // example an adventure reward or a completed fight page).
        if (IsChoiceRoute(route) || IsChoiceBody(body))
            return KoLResponseKind.Choice;
        if (IsFightRoute(route) || IsFightBody(body))
            return KoLResponseKind.Fight;
        if (IsAdventureRoute(route) || IsAdventureBody(body))
            return KoLResponseKind.Adventure;

        if (status is >= 200 and <= 299 || response.HasSessionCookie == true)
            return KoLResponseKind.Success;

        return KoLResponseKind.Unknown;
    }

    public static KoLResponse ClassifyResponse(KoLRequest request, KoLResponse response)
    {
        var kind = Classify(request, response);
        return response with
        {
            Classification = kind,
            IsLoginPage = response.IsLoginPage || kind == KoLResponseKind.Login
        };
    }

    public static KoLResponse ClassifyResponse(KoLResponse response, KoLRequest request) =>
        ClassifyResponse(request, response);

    public static KoLResponseKind Classify(
        KoLRequest request,
        int? statusCode,
        string? body = null,
        bool isLoginPage = false,
        TimeSpan? retryAfter = null,
        string? error = null)
    {
        var response = new KoLResponse
        {
            StatusCode = statusCode,
            Body = body,
            IsLoginPage = isLoginPage,
            RetryAfter = retryAfter,
            Error = error
        };
        return Classify(request, response);
    }

    public static KoLResponseKind Classify(
        KoLRequest request,
        HttpStatusCode statusCode,
        string? body = null,
        bool isLoginPage = false,
        TimeSpan? retryAfter = null,
        string? error = null) =>
        Classify(request, (int)statusCode, body, isLoginPage, retryAfter, error);

    public static bool IsLoginBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        // Avoid accepting arbitrary text saying "login". These markers are
        // associated with KoL's login form/page.
        return LoginFormRegex().IsMatch(body) ||
            body.Contains("name=\"loginname\"", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("name='loginname'", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("name=\"loggingin\"", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("name='loggingin'", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("action=\"login.php\"", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("action='login.php'", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("Please log in", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("session has expired", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("invalid session", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("You are not currently logged in", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsRateLimitBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        return body.Contains("too many requests", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("try again later", StringComparison.OrdinalIgnoreCase) &&
            body.Contains("request", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsTransientBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        return body.Contains("temporarily unavailable", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("service unavailable", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("gateway timeout", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("internal server error", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("server error", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("request timed out", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsChoiceBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        return body.Contains("whichchoice", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("choice.php", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsFightBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        return body.Contains("fight.php", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("whichskill", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("combatround", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("You win the fight", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAdventureBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        return body.Contains("adventure.php", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("snarfblat", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("adventure title", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsLoginRedirect(KoLResponse response)
    {
        if (response.StatusCode is not (301 or 302 or 303 or 307 or 308))
            return false;
        return response.Headers.TryGetValue("Location", out var location) &&
            IsLoginRoute(location);
    }

    public static bool IsLoginRoute(string? route) => HasRoute(route, "login.php");
    public static bool IsChoiceRoute(string? route) => HasRoute(route, "choice.php");
    public static bool IsFightRoute(string? route) => HasRoute(route, "fight.php");
    public static bool IsAdventureRoute(string? route) => HasRoute(route, "adventure.php");

    private static bool HasRoute(string? route, string page)
    {
        if (string.IsNullOrWhiteSpace(route)) return false;
        var path = route!;
        var queryStart = path.IndexOf('?', StringComparison.Ordinal);
        if (queryStart >= 0) path = path[..queryStart];
        var fragmentStart = path.IndexOf('#', StringComparison.Ordinal);
        if (fragmentStart >= 0) path = path[..fragmentStart];
        path = path.TrimEnd('/');
        var finalSegment = path[(path.LastIndexOf('/') + 1)..];
        return string.Equals(finalSegment, page, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex("<(?:form|input)[^>]*(?:login|loggingin)[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LoginFormRegex();
}
