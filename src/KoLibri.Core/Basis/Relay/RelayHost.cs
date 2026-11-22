using System.Net;
using System.Net.Sockets;

namespace KoLibri.Core.Basis;

/// <summary>
/// Runs <see cref="RelayServer"/> inside the current process for one role: bound to 127.0.0.1 on a free port,
/// upstream through the global KoL rate limiter, one secret token per run, redacted JSONL log.
/// The token is only reachable through <see cref="EnterUrl"/>; it is never logged by this class.
/// </summary>
public sealed class RelayHost : IDisposable
{
    private readonly RelayServer _server;
    private readonly HttpClient _upstream;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _run;

    private RelayHost(RelayServer server, HttpClient upstream)
    {
        _server = server; _upstream = upstream;
        _run = server.RunAsync(_cts.Token);
    }

    /// <summary>Origin only ("http://127.0.0.1:PORT"); contains no secret.</summary>
    public string Origin => _server.Origin;

    /// <summary>The single-use-per-run URL carrying the secret. Hand it to the web view, never show or log it.</summary>
    public string EnterUrl(string nextPath) => _server.EntryUrl(nextPath);

    /// <summary>True when <paramref name="url"/> is on the relay's own origin (scheme, host and port).</summary>
    public bool IsRelayUrl(string? url) => IsSameOrigin(Origin, url);

    public static bool IsSameOrigin(string origin, string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) && Uri.TryCreate(origin, UriKind.Absolute, out var o)
           && u.Scheme == o.Scheme && u.Host == o.Host && u.Port == o.Port;

    public static int FindFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; } finally { l.Stop(); }
    }

    public static string DefaultLogPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KoLibri", "relay-log.jsonl");

    /// <summary>
    /// Starts a relay for the stored session of <paramref name="role"/>. Throws <see cref="InvalidOperationException"/>
    /// with a readable message when the role is not logged in or the port cannot be bound.
    /// </summary>
    public static async Task<RelayHost> StartAsync(AccountSessionManager accounts, AccountRole role,
        int port = 0, string? logPath = null, bool browserUserAgent = false, Action<string>? console = null,
        HttpMessageHandler? upstreamHandler = null, string kolOrigin = "https://www.kingdomofloathing.com",
        KoLRateLimiter? limiter = null)
    {
        var session = await accounts.GetSessionAsync(role).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"{role} is not logged in. Log in first.");
        var cookie = session.Credentials.CookieHeader;
        if (string.IsNullOrWhiteSpace(cookie))
            throw new InvalidOperationException($"{role} has no valid KoL session. Log in again.");
        return Start(session, port, logPath, browserUserAgent, console, upstreamHandler, kolOrigin, limiter,
            SessionBindingCheck(accounts, role, session.BindingKey()));
    }

    /// <summary>
    /// Builds a per-request check that the role still holds the login the relay started with (same player and cookie).
    /// The stored session is re-read at most once a second. Returns null while it matches, else a clear message.
    /// </summary>
    public static Func<Task<string?>> SessionBindingCheck(AccountSessionManager accounts, AccountRole role, string bindingKey)
    {
        var gate = new object(); long checkedAt = 0; string? last = null;
        return async () =>
        {
            var now = Environment.TickCount64;
            lock (gate) { if (checkedAt != 0 && now - checkedAt < 1000) return last; }
            var current = await accounts.GetSessionAsync(role, includeExpired: true).ConfigureAwait(false);
            var msg = current is not null && current.BindingKey() == bindingKey ? null
                : $"The {role} login changed since this relay started. Close this tab and open the browser again.";
            lock (gate) { checkedAt = Math.Max(1, now); last = msg; }
            return msg;
        };
    }

    public static RelayHost Start(AccountSession session, int port = 0, string? logPath = null, bool browserUserAgent = false,
        Action<string>? console = null, HttpMessageHandler? upstreamHandler = null,
        string kolOrigin = "https://www.kingdomofloathing.com", KoLRateLimiter? limiter = null,
        Func<Task<string?>>? sessionCheck = null)
    {
        var cookie = session.Credentials.CookieHeader
            ?? throw new InvalidOperationException("No KoL session cookie.");
        logPath = Path.GetFullPath(logPath ?? DefaultLogPath());
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        // Every upstream request goes through the process-wide limiter. A caller-supplied handler is used as is (tests)
        // unless a limiter is passed too.
        HttpMessageHandler inner = upstreamHandler is null
            ? new KoLRateLimitedHandler(new HttpClientHandler
            {
                AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.All
            }, limiter)
            : limiter is null ? upstreamHandler : new KoLRateLimitedHandler(upstreamHandler, limiter);
        var upstream = new HttpClient(inner) { Timeout = Timeout.InfiniteTimeSpan };
        var logLock = new object();
        RelayServer? server = null;
        try
        {
            server = new RelayServer(upstream, new RelayOptions
            {
                Port = port == 0 ? FindFreePort() : port,
                KolCookie = cookie,
                SessionUserAgent = session.Credentials.UserAgent ?? KoLUserAgent.LegacyValue,
                BrowserUserAgent = browserUserAgent,
                KolOrigin = kolOrigin,
                SessionCheck = sessionCheck,
                Log = line => { lock (logLock) File.AppendAllText(logPath, line + "\n"); },
                Console = console ?? (_ => { }),
            });
            server.Start();
            return new RelayHost(server, upstream);
        }
        catch
        {
            server?.Dispose(); upstream.Dispose();
            throw;
        }
    }

    /// <summary>For tests only: the relay's cookie name and value.</summary>
    internal string CookieNameForTests => _server.CookieName;
    internal string SessionCookieForTests => _server.SessionCookieValue;

    public Task Completion => _run;

    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _cts.Cancel();
        _server.Dispose();
        _upstream.Dispose();
        _cts.Dispose();
    }
}
