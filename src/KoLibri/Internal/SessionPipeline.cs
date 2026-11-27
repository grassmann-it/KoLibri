using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using CoreBasis = KoLibri.Core.Basis;
using CoreSpiel = KoLibri.Core.Spiel;

namespace KoLibri.Internal;

/// <summary>Builds the HTTP chain of a session: allowlist → request events → limiter lane → transport.</summary>
internal static class SessionPipeline
{
    internal static readonly HttpRequestOptionsKey<RequestLane> LaneKey = new("kolibri.lane");
    internal static readonly HttpRequestOptionsKey<TimeSpan> WaitedKey = new("kolibri.waited");

    public static CoreBasis.KoLSecurityPolicy CreatePolicy(Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        var hosts = new HashSet<string>(CoreBasis.KoLHostAllowlist.OfficialHosts, StringComparer.OrdinalIgnoreCase) { baseUri.Host };
        var policy = new CoreBasis.KoLSecurityPolicy { AllowedHosts = hosts };
        policy.Validate();
        policy.EnsureAllowed(baseUri, "KoLibri base");
        return policy;
    }

    public static CoreBasis.KoLRateLimiter CreateLimiter(KolibriOptions options)
    {
        if (options.DisableLimiterForTests) return new CoreBasis.KoLRateLimiter(TimeSpan.Zero, null);
        var limiter = options.Limiter ?? new LimiterOptions();
        return new CoreBasis.KoLRateLimiter(new CoreBasis.KoLLimiterOptions
        {
            // Floors: the action lane never runs faster than one request per second.
            ActionInterval = limiter.ActionInterval < CoreBasis.KoLRateLimiter.MinimumInterval ? CoreBasis.KoLRateLimiter.MinimumInterval : limiter.ActionInterval,
            ReadInterval = limiter.ReadInterval,
            ReadConcurrency = limiter.ReadConcurrency,
            MaxPerSecond = double.IsNaN(limiter.MaxPerSecond) ? 2 : limiter.MaxPerSecond <= 0 ? 0 : Math.Clamp(limiter.MaxPerSecond, 1, CoreBasis.KoLLimiterOptions.MaximumPerSecond),
        });
    }

    /// <summary>Handler chain for login.php: the cookie jar captures the session cookie, the limiter still applies.</summary>
    public static Func<CookieContainer, HttpMessageHandler> LoginHandlerFactory(KolibriClient client) => jar =>
    {
        HttpMessageHandler transport = client.Options.HttpHandler is { } custom
            ? new CookieCaptureHandler(jar, new ForwardingHandler(custom))
            : new HttpClientHandler
            {
                UseCookies = true,
                CookieContainer = jar,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All
            };
        return new LaneHandler(client.Limiter, transport);
    };

    public static HttpClient CreateHttpClient(KolibriClient client, AccountId account, string cookieHeader, string userAgent, KolibriSession? events)
    {
        HttpMessageHandler transport;
        if (client.Options.HttpHandler is { } custom)
        {
            CoreBasis.KoLAllowlistHandler.EnsureNoAutoRedirect(custom);
            transport = new ForwardingHandler(custom);
        }
        else
        {
            transport = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All };
        }
        HttpMessageHandler chain = new LaneHandler(client.Limiter, transport);
        if (events is not null) chain = new RequestEventsHandler(events, chain);
        var http = new HttpClient(new CoreBasis.KoLAllowlistHandler(chain, client.Policy))
        {
            BaseAddress = client.Options.BaseUri,
            Timeout = client.Options.RequestTimeout
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        http.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookieHeader);
        CoreBasis.KoLAllowlistHandler.MarkTrusted(http);
        return http;
    }

    /// <summary>Path and query without secrets (pwd, password, hashes, cookies).</summary>
    public static string Redact(Uri? uri)
    {
        if (uri is null) return string.Empty;
        var path = uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString.Split('?')[0];
        var query = uri.IsAbsoluteUri ? uri.Query : (uri.OriginalString.Contains('?') ? uri.OriginalString[uri.OriginalString.IndexOf('?')..] : "");
        return path + SecretParameter.Replace(query, m => m.Groups[1].Value + "=***");
    }

    private static readonly Regex SecretParameter = new(
        @"(?<=[?&])(pwd|password|pass|hash|pwdhash|response|challenge|t|token|cookie)=[^&]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Delegates to a handler the caller owns without disposing it.</summary>
    private sealed class ForwardingHandler(HttpMessageHandler target) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _invoker = new(target, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _invoker.SendAsync(request, cancellationToken);
    }

    /// <summary>Reads Set-Cookie headers into a jar (a custom transport does not do this itself).</summary>
    private sealed class CookieCaptureHandler(CookieContainer jar, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is { } uri && jar.GetCookieHeader(uri) is { Length: > 0 } header)
                request.Headers.TryAddWithoutValidation("Cookie", header);
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (request.RequestUri is { } target && response.Headers.TryGetValues("Set-Cookie", out var values))
                foreach (var value in values)
                    jar.SetCookies(target, value);
            return response;
        }
    }

    /// <summary>Waits for a start slot in the request's lane and holds it until the response headers arrive.</summary>
    private sealed class LaneHandler(CoreBasis.KoLRateLimiter limiter, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var lane = EffectiveLane(request);
            var started = Stopwatch.GetTimestamp();
            using var lease = await limiter.AcquireAsync(
                lane == RequestLane.Read ? CoreBasis.KoLRequestLane.Read : CoreBasis.KoLRequestLane.Action, cancellationToken).ConfigureAwait(false);
            request.Options.Set(WaitedKey, Stopwatch.GetElapsedTime(started));
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    public static RequestLane EffectiveLane(HttpRequestMessage request)
    {
        if (request.Options.TryGetValue(LaneKey, out var forced) && forced != RequestLane.Auto) return forced;
        return CoreBasis.KoLRequestLanes.Classify(request) == CoreBasis.KoLRequestLane.Read ? RequestLane.Read : RequestLane.Action;
    }

    /// <summary>Raises <see cref="KolibriEvents.Request"/> for every request of a session.</summary>
    private sealed class RequestEventsHandler(KolibriSession session, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var started = Stopwatch.GetTimestamp();
            var at = DateTimeOffset.UtcNow;
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var total = Stopwatch.GetElapsedTime(started);
            request.Options.TryGetValue(WaitedKey, out var waited);
            var kind = PageKind.Normal;
            try
            {
                await response.Content.LoadIntoBufferAsync().ConfigureAwait(false);
                var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                kind = PageClassifier.Classify(request.RequestUri, (int)response.StatusCode, html, response.Headers.Location);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                kind = PageKind.Error;
            }
            var args = new RequestEventArgs(session.Account.Id, at,
                request.Method == HttpMethod.Post ? RequestMethod.Post : RequestMethod.Get,
                Redact(request.RequestUri), EffectiveLane(request), (int)response.StatusCode,
                total - waited < TimeSpan.Zero ? TimeSpan.Zero : total - waited, waited, kind);
            session.RaiseRequest(args);
            return response;
        }
    }
}

/// <summary>Sorts a KoL answer into <see cref="PageKind"/>.</summary>
internal static class PageClassifier
{
    private static readonly Regex Maintenance = new(
        @"(down for (nightly )?maintenance|nightly maintenance|currently down for|rollover is in progress)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TooFast = new(
        @"(whoa,? slow down|too many requests|you're (clicking|doing that) too fast)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static PageKind Classify(Uri? url, int statusCode, string? html, Uri? location = null)
    {
        if (statusCode is >= 300 and < 400)
            return IsLogin(location) ? PageKind.LoginRequired : PageKind.Normal;
        if (statusCode == 429) return PageKind.TooFast;
        html ??= string.Empty;
        if (CoreBasis.KoLHtmlResponseClassifier.Classify(html) == CoreBasis.KoLHtmlResponseKind.LoginPage) return PageKind.LoginRequired;
        if (statusCode is 401 or 403) return PageKind.LoginRequired;
        if (Maintenance.IsMatch(html) && html.Length < 20_000) return PageKind.Maintenance;
        if (statusCode >= 400) return PageKind.Error;
        if (TooFast.IsMatch(html) && html.Length < 20_000) return PageKind.TooFast;
        if (CoreSpiel.KoLAdventureEncounterParser.IsNotInAFightPage(html)) return PageKind.NotInFight;
        var page = url is null ? string.Empty : (url.IsAbsoluteUri ? url.AbsolutePath : url.OriginalString.Split('?')[0]);
        if (page.EndsWith("fight.php", StringComparison.OrdinalIgnoreCase)) return PageKind.Fight;
        if (page.EndsWith("choice.php", StringComparison.OrdinalIgnoreCase) || CoreSpiel.OpenChoiceClient.LooksLikeChoicePage(html))
            return PageKind.Choice;
        return PageKind.Normal;
    }

    public static bool IsLogin(Uri? location)
        => location is not null
           && (location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString.Split('?', '#')[0])
               .TrimEnd('/').EndsWith("login.php", StringComparison.OrdinalIgnoreCase);
}
