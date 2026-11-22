using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KoLibri.Core.Basis;

internal static partial class KolibriCli
{
    /// <summary>MCP's HTTP host defaults to 8765, so the relay uses a different default.</summary>
    internal const int RelayDefaultPort = 8766;

    /// <summary>
    /// relay --role R [--port 8766] [--log FILE] [--browser-ua]
    /// Local reverse proxy: opens the stored KoLibri session in your own browser at http://127.0.0.1:PORT/
    /// and logs every request (names and a few safe values only; never cookies, hashes or free values).
    /// The first URL printed contains a per-run secret; only the browser that opened it can use the relay.
    /// </summary>
    private static async Task<int> RelayAsync(string[] args)
    {
        var role = ParseRole(GetOption(args, "--role") ?? "main");
        var port = Math.Clamp(ReadInt(args, "--port", RelayDefaultPort), 1024, 65535);
        var browserUa = HasFlag(args, "--browser-ua");
        var logPath = GetOption(args, "--log") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KoLibri", "relay-log.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        var accounts = AccountSessionManager.CreateDefault();
        var session = await accounts.GetSessionAsync(role)
            ?? throw new InvalidOperationException($"{role} is not logged in. Run 'login --role {role.ToString().ToLowerInvariant()}'.");
        var cookie = session.Credentials.CookieHeader;
        if (string.IsNullOrWhiteSpace(cookie))
            throw new InvalidOperationException($"{role} has no valid KoL session. Log in again.");
        var sessionUa = session.Credentials.UserAgent ?? KoLUserAgent.LegacyValue;

        // Every upstream request goes through the process-wide limiter; no automatic redirects, no cookie jar.
        using var upstream = new HttpClient(new KoLRateLimitedHandler(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All
        })) { Timeout = Timeout.InfiniteTimeSpan };

        var logLock = new object();
        using var server = new RelayServer(upstream, new RelayOptions
        {
            Port = port,
            KolCookie = cookie,
            SessionUserAgent = sessionUa,
            BrowserUserAgent = browserUa,
            Log = line => { lock (logLock) File.AppendAllText(logPath, line + "\n"); },
            Console = Console.WriteLine,
        });
        try { server.Start(); }
        catch (InvalidOperationException ex) { Console.Error.WriteLine(ex.Message); return 1; }

        Console.WriteLine($"KoLibri relay for {role} ({session.Profile.DisplayName}) on {server.Origin}/");
        Console.WriteLine($"Open this URL in your own browser (it carries the secret for this run): {server.EntryUrl("/main.php")}");
        Console.WriteLine($"User-Agent sent to KoL: {(browserUa ? "the browser's own" : sessionUa)}");
        Console.WriteLine($"Log: {logPath}   (Ctrl-C to stop)");
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        await server.RunAsync(stop.Token);
        return 0;
    }
}

internal sealed class RelayOptions
{
    public int Port { get; init; }
    public string KolCookie { get; init; } = "";
    public string SessionUserAgent { get; init; } = "";
    public bool BrowserUserAgent { get; init; }
    public Action<string> Log { get; init; } = _ => { };
    public Action<string> Console { get; init; } = _ => { };
    public TimeSpan UpstreamTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public int MaxConcurrency { get; init; } = 8;
    public TimeSpan ConcurrencyWait { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Text responses larger than this are passed through unmodified (streamed).</summary>
    public int MaxRewriteBytes { get; init; } = 2 * 1024 * 1024;
    /// <summary>Form bodies up to this size are buffered to log field names; larger ones are streamed.</summary>
    public int MaxFormLogBytes { get; init; } = 256 * 1024;
    public long MaxRequestBodyBytes { get; init; } = 16L * 1024 * 1024;
    public string KolOrigin { get; init; } = "https://www.kingdomofloathing.com";
}

/// <summary>
/// Local reverse proxy for the user's own browser. Security model: it binds 127.0.0.1 only, accepts only the exact
/// Host "127.0.0.1:PORT" (DNS rebinding), and requires a per-run session cookie (SameSite=Strict, HttpOnly) that is only
/// handed out for the secret token in the first URL. Cross-site Origin/Referer/Sec-Fetch-Site are rejected.
/// Known limit: browsers share cookies between ports of the same host, so another local server on a different
/// 127.0.0.1 port would receive the (random-named, HttpOnly) cookie if you browsed to it. A per-run
/// "&lt;random&gt;.localhost" origin was evaluated and rejected: HttpListener answers 404 for Host values outside its
/// prefixes and resolves a named prefix through DNS (it came back as ::1 first), so the relay would not reliably
/// listen on IPv4 loopback only. Do not browse other local servers with the relay tab's browser profile open.
/// </summary>
internal sealed class RelayServer : IDisposable
{
    public const string EntryPath = "/__kolibri/enter";

    private static readonly HashSet<string> SafeLoggedValues = new(StringComparer.OrdinalIgnoreCase)
        { "snarfblat", "action", "whichchoice", "option", "which", "whichitem", "ajax" };

    // Only these request headers are forwarded to KoL (plus Content-Type for bodies, Cookie and User-Agent set by us).
    private static readonly string[] ForwardedRequestHeaders =
        ["Accept", "Accept-Language", "Referer", "Origin", "X-Requested-With", "If-None-Match", "If-Modified-Since"];

    // Header values that may appear in the log; every other forwarded header is logged by name only.
    private static readonly HashSet<string> SafeLoggedHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "Accept", "Accept-Language", "X-Requested-With" };

    private static readonly HashSet<string> DroppedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "set-cookie", "content-length", "content-encoding", "transfer-encoding", "connection", "keep-alive", "proxy-authenticate" };

    private static readonly HashSet<string> RewritableTypes = new(StringComparer.OrdinalIgnoreCase)
        { "text/html", "text/javascript", "application/javascript", "text/css" };

    private readonly HttpClient _upstream;
    private readonly RelayOptions _o;
    private readonly HttpListener _listener = new();
    private readonly SemaphoreSlim _slots;
    private readonly string _cookieName;
    private readonly byte[] _tokenBytes;
    private readonly byte[] _sessionBytes;
    private readonly Uri _kol;
    private bool _started;

    public RelayServer(HttpClient upstream, RelayOptions options)
    {
        _upstream = upstream; _o = options;
        _slots = new SemaphoreSlim(Math.Max(1, options.MaxConcurrency));
        // Random per run: a fixed or port-derived name would be predictable, and browsers send
        // 127.0.0.1 cookies to every port of that host, so the name alone isolates nothing.
        // Exact Host-port matching, HttpOnly and SameSite=Strict are the real boundary.
        _cookieName = "kolibri_relay_" + RandomSecret()[..12];
        _kol = new Uri(options.KolOrigin);
        Token = RandomSecret();
        var session = RandomSecret();
        _tokenBytes = Encoding.ASCII.GetBytes(Token);
        _sessionBytes = Encoding.ASCII.GetBytes(session);
        SessionCookieValue = session;
    }

    public string Token { get; }
    /// <summary>Value of the cookie the relay issues after a valid token (exposed for tests).</summary>
    public string SessionCookieValue { get; }
    public string CookieName => _cookieName;
    public string Origin => $"http://127.0.0.1:{_o.Port}";
    public string EntryUrl(string next) => $"{Origin}{EntryPath}?t={Token}&next={Uri.EscapeDataString(next)}";

    public void Start()
    {
        _listener.Prefixes.Add(Origin + "/");
        try { _listener.Start(); _started = true; }
        catch (HttpListenerException ex)
        {
            throw new InvalidOperationException(
                $"Cannot listen on {Origin}/ ({(ex.Message.Contains("use", StringComparison.OrdinalIgnoreCase) ? "port in use" : "listen failed")}). " +
                "Pick another port with --port (the default 8766 avoids the MCP server's 8765).", ex);
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var reg = ct.Register(() => { try { _listener.Stop(); } catch { } });
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch { break; }
            _ = Task.Run(() => HandleAsync(ctx, ct));
        }
    }

    public void Dispose()
    {
        if (_started) { try { _listener.Stop(); } catch { } }
        _listener.Close();
        _slots.Dispose();
    }

    // ---------------------------------------------------------------- redaction

    /// <summary>Path plus query where only the safe keys keep their values: "/a.php?pwd=…&amp;action=x".</summary>
    internal static string RedactUrl(string? rawUrl)
    {
        if (string.IsNullOrEmpty(rawUrl)) return "";
        var q = rawUrl.IndexOf('?');
        if (q < 0) return rawUrl;
        var parts = rawUrl[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p =>
        {
            var kv = p.Split('=', 2);
            if (kv.Length == 1) return kv[0];
            return SafeLoggedValues.Contains(Unescape(kv[0])) ? p : kv[0] + "=…";
        });
        return rawUrl[..q] + "?" + string.Join("&", parts);
    }

    private static string Unescape(string s) { try { return Uri.UnescapeDataString(s.Replace('+', ' ')); } catch { return s; } }

    private static string RedactLocation(string? location)
    {
        if (location is null) return "";
        // keep only host/path/safe query; fragments can carry anything
        var hash = location.IndexOf('#');
        return RedactUrl(hash >= 0 ? location[..hash] : location);
    }

    // ---------------------------------------------------------------- authorization

    private enum Denial { None, Host, Method, NoSession, CrossSite }

    private bool IsExactAuthority(HttpListenerRequest req)
        => string.Equals(req.Headers["Host"], $"127.0.0.1:{_o.Port}", StringComparison.OrdinalIgnoreCase);

    private bool HasSession(HttpListenerRequest req)
    {
        var header = req.Headers["Cookie"];
        if (string.IsNullOrEmpty(header)) return false;
        foreach (var part in header.Split(';'))
        {
            var kv = part.Trim().Split('=', 2);
            if (kv.Length == 2 && kv[0] == _cookieName && FixedEquals(Encoding.ASCII.GetBytes(kv[1]), _sessionBytes))
                return true;
        }
        return false;
    }

    private static bool FixedEquals(byte[] a, byte[] b) => a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);

    private bool IsSameOrigin(string? value)
        => value is not null && Uri.TryCreate(value, UriKind.Absolute, out var u)
           && u.Scheme == "http" && u.Host == "127.0.0.1" && u.Port == _o.Port;

    private Denial CheckRequest(HttpListenerRequest req)
    {
        if (!IsExactAuthority(req)) return Denial.Host;
        var method = req.HttpMethod.ToUpperInvariant();
        if (method is not ("GET" or "HEAD" or "POST")) return Denial.Method;
        if (!HasSession(req)) return Denial.NoSession;
        var site = req.Headers["Sec-Fetch-Site"];
        if (site is not null && site is not ("same-origin" or "none")) return Denial.CrossSite;
        var origin = req.Headers["Origin"];
        if (origin is not null && !IsSameOrigin(origin)) return Denial.CrossSite;
        var referer = req.Headers["Referer"];
        if (referer is not null && !IsSameOrigin(referer)) return Denial.CrossSite;
        // Unsafe methods must prove their origin (browsers always send Origin or Sec-Fetch-Site on POST).
        if (method == "POST" && origin is null && site is null) return Denial.CrossSite;
        return Denial.None;
    }

    // ---------------------------------------------------------------- handling

    private sealed class ClientAbortException(Exception inner) : Exception("client aborted", inner);

    private async Task HandleAsync(HttpListenerContext ctx, CancellationToken stop)
    {
        var req = ctx.Request; var res = ctx.Response;
        var started = DateTimeOffset.Now;
        var method = req.HttpMethod;
        var path = RedactUrl(req.RawUrl);
        string outcome = "ok"; int status = 0; long bytes = 0; string? location = null;
        Dictionary<string, string>? fields = null; Dictionary<string, string>? sentHeaders = null;
        var acquired = false;
        try
        {
            var path0 = req.Url?.AbsolutePath ?? "";
            if (path0 == EntryPath && IsExactAuthority(req) && req.HttpMethod is "GET")
            {
                path = EntryPath;
                status = HandleEntry(req, res, out outcome);
                return;
            }
            var denial = CheckRequest(req);
            if (denial != Denial.None)
            {
                outcome = "denied:" + denial;
                status = denial switch { Denial.Method => 405, Denial.NoSession => 403, _ => 403 };
                await WriteTextAsync(res, status, denial == Denial.NoSession
                    ? "Forbidden. Open the URL printed by 'kolibri relay' in this browser first."
                    : "Forbidden.");
                return;
            }
            if (req.RawUrl is null || !req.RawUrl.StartsWith('/') || req.RawUrl.StartsWith("//") || req.RawUrl.StartsWith("/\\"))
            {
                outcome = "denied:path"; status = 400; await WriteTextAsync(res, 400, "Bad request."); return;
            }
            if (req.ContentLength64 > _o.MaxRequestBodyBytes)
            {
                outcome = "denied:too-large"; status = 413; await WriteTextAsync(res, 413, "Request too large."); return;
            }
            acquired = await _slots.WaitAsync(_o.ConcurrencyWait, stop).ConfigureAwait(false);
            if (!acquired)
            {
                outcome = "busy"; status = 503; res.AddHeader("Retry-After", "5");
                await WriteTextAsync(res, 503, "Relay busy."); return;
            }

            var target = new Uri(_kol, req.RawUrl);
            if (!string.Equals(target.Host, _kol.Host, StringComparison.OrdinalIgnoreCase))
            {
                outcome = "denied:path"; status = 400; await WriteTextAsync(res, 400, "Bad request."); return;
            }
            using var outgoing = new HttpRequestMessage(new HttpMethod(req.HttpMethod), target);
            sentHeaders = new Dictionary<string, string>();
            foreach (var name in ForwardedRequestHeaders)
            {
                var v = req.Headers[name];
                if (v is null) continue;
                v = v.Replace(Origin, _o.KolOrigin, StringComparison.OrdinalIgnoreCase);
                if (outgoing.Headers.TryAddWithoutValidation(name, v))
                    sentHeaders[name] = SafeLoggedHeaders.Contains(name) ? v : "…";
            }
            var ua = _o.BrowserUserAgent ? req.Headers["User-Agent"] : _o.SessionUserAgent;
            if (!string.IsNullOrEmpty(ua)) { outgoing.Headers.TryAddWithoutValidation("User-Agent", ua); sentHeaders["User-Agent"] = "…"; }
            outgoing.Headers.TryAddWithoutValidation("Cookie", _o.KolCookie);

            if (req.HasEntityBody)
            {
                var contentType = req.ContentType;
                var isForm = (contentType ?? "").StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
                if (isForm && req.ContentLength64 >= 0 && req.ContentLength64 <= _o.MaxFormLogBytes)
                {
                    using var ms = new MemoryStream();
                    await req.InputStream.CopyToAsync(ms, stop).ConfigureAwait(false);
                    var body = ms.ToArray();
                    outgoing.Content = new ByteArrayContent(body);
                    fields = ParseFormFields(Encoding.UTF8.GetString(body));
                }
                else outgoing.Content = req.ContentLength64 >= 0
                    ? new StreamContent(new BoundedReadStream(req.InputStream, req.ContentLength64))
                    : new StreamContent(new BoundedReadStream(req.InputStream, _o.MaxRequestBodyBytes));
                if (contentType is not null) outgoing.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
                if (req.ContentLength64 >= 0) outgoing.Content.Headers.ContentLength = req.ContentLength64;
                sentHeaders["Content-Type"] = "…";
            }

            using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(stop);
            headerCts.CancelAfter(_o.UpstreamTimeout);
            HttpResponseMessage response;
            try { response = await _upstream.SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead, headerCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested)
            {
                outcome = "timeout"; status = 504;
                await WriteTextAsync(res, 504, "Relay error: upstream timeout."); return;
            }
            // The 60 s guard must also bound the body: a stalled body would hold a slot forever.
            using var bodyCts = CancellationTokenSource.CreateLinkedTokenSource(stop);
            bodyCts.CancelAfter(_o.UpstreamTimeout);
            var bodyStop = bodyCts.Token;
            using (response)
            {
                status = (int)response.StatusCode;
                res.StatusCode = status;
                foreach (var h in response.Headers.Concat(response.Content.Headers))
                {
                    if (DroppedResponseHeaders.Contains(h.Key)) continue;
                    var value = string.Join(", ", h.Value);
                    if (h.Key.Equals("Location", StringComparison.OrdinalIgnoreCase))
                    {
                        location = value;
                        value = RewriteOrigins(value);
                    }
                    try { res.Headers[h.Key] = value; } catch { }
                }
                var ctype = response.Content.Headers.ContentType;
                var type = ctype?.MediaType ?? "";
                var isHead = method == "HEAD";
                await using var src = await response.Content.ReadAsStreamAsync(bodyStop).ConfigureAwait(false);
                if (isHead) { res.ContentLength64 = 0; }
                else if (RewritableTypes.Contains(type))
                    bytes = await RewriteAndWriteAsync(res, src, ctype, bodyStop).ConfigureAwait(false);
                else
                {
                    var len = response.Content.Headers.ContentLength;
                    if (len is { } l) res.ContentLength64 = l; else res.SendChunked = true;
                    bytes = await CopyAsync(src, res.OutputStream, bodyStop).ConfigureAwait(false);
                }
            }
        }
        catch (ClientAbortException) { outcome = "client-abort"; }
        catch (OperationCanceledException) { outcome = stop.IsCancellationRequested ? "cancelled" : "timeout"; }
        catch (Exception ex)
        {
            // Never echo ex.Message: it can contain URLs. Type name only.
            outcome = "error:" + ex.GetType().Name; status = 502;
            try { await WriteTextAsync(res, 502, "Relay error: upstream request failed."); } catch { }
        }
        finally
        {
            if (acquired) _slots.Release();
            try { res.Close(); } catch { }
            Emit(started, method, path, status, location, fields, sentHeaders, bytes, outcome);
        }
    }

    private int HandleEntry(HttpListenerRequest req, HttpListenerResponse res, out string outcome)
    {
        var t = System.Web.HttpUtility.ParseQueryString(req.Url!.Query);
        var given = Encoding.ASCII.GetBytes(t["t"] ?? "");
        if (!FixedEquals(given, _tokenBytes))
        {
            outcome = "denied:token";
            res.StatusCode = 403; res.ContentType = "text/plain"; res.ContentLength64 = 0;
            return 403;
        }
        var next = t["next"];
        if (string.IsNullOrEmpty(next) || !next.StartsWith('/') || next.StartsWith("//") || next.StartsWith("/\\")
            || next.StartsWith(EntryPath, StringComparison.Ordinal)) next = "/main.php";
        res.AddHeader("Set-Cookie", $"{_cookieName}={SessionCookieValue}; Path=/; HttpOnly; SameSite=Strict");
        res.AddHeader("Cache-Control", "no-store");
        res.StatusCode = 302; res.RedirectLocation = next; res.ContentLength64 = 0;
        outcome = "entered";
        return 302;
    }

    private string RewriteOrigins(string text)
    {
        var host = _kol.Host;
        return text
            .Replace("https://" + host, Origin, StringComparison.OrdinalIgnoreCase)
            .Replace("http://" + host, Origin, StringComparison.OrdinalIgnoreCase)
            .Replace("//" + host, "//127.0.0.1:" + _o.Port, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<long> RewriteAndWriteAsync(HttpListenerResponse res, Stream src, System.Net.Http.Headers.MediaTypeHeaderValue? ctype, CancellationToken ct)
    {
        // Bounded: read at most MaxRewriteBytes+1; anything bigger is streamed unmodified.
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        var limit = _o.MaxRewriteBytes;
        var overflow = false;
        int n;
        while ((n = await src.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, n);
            if (buffer.Length > limit) { overflow = true; break; }
        }
        var enc = ResolveEncoding(ctype);
        if (!overflow && enc is not null)
        {
            var text = RewriteOrigins(enc.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
            var data = enc.GetBytes(text);
            res.ContentLength64 = data.Length;
            await WriteAsync(res.OutputStream, data, ct).ConfigureAwait(false);
            return data.Length;
        }
        res.SendChunked = true;
        long total = buffer.Length;
        await WriteAsync(res.OutputStream, buffer.ToArray(), ct).ConfigureAwait(false);
        if (overflow) total += await CopyAsync(src, res.OutputStream, ct).ConfigureAwait(false);
        return total;
    }

    /// <summary>The declared charset (HTTP default for text/html is ISO-8859-1); unknown charsets mean no rewriting.</summary>
    private static Encoding? ResolveEncoding(System.Net.Http.Headers.MediaTypeHeaderValue? ctype)
    {
        var cs = ctype?.CharSet;
        if (string.IsNullOrWhiteSpace(cs))
            return string.Equals(ctype?.MediaType, "text/html", StringComparison.OrdinalIgnoreCase) ? Encoding.Latin1 : new UTF8Encoding(false);
        try { return Encoding.GetEncoding(cs.Trim('"', '\'')); } catch { return null; }
    }

    private static async Task WriteAsync(Stream output, byte[] data, CancellationToken ct)
    {
        try { await output.WriteAsync(data, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or HttpListenerException or ObjectDisposedException or InvalidOperationException)
        { throw new ClientAbortException(ex); }
    }

    private static async Task<long> CopyAsync(Stream src, Stream dst, CancellationToken ct)
    {
        var buf = new byte[81920]; long total = 0; int n;
        while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            try { await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or HttpListenerException or ObjectDisposedException or InvalidOperationException)
            { throw new ClientAbortException(ex); }
            total += n;
        }
        return total;
    }

    private static async Task WriteTextAsync(HttpListenerResponse res, int status, string text)
    {
        var b = Encoding.UTF8.GetBytes(text);
        res.StatusCode = status; res.ContentType = "text/plain; charset=utf-8"; res.ContentLength64 = b.Length;
        try { await res.OutputStream.WriteAsync(b).ConfigureAwait(false); } catch { }
    }

    private static Dictionary<string, string>? ParseFormFields(string body)
    {
        var fields = new Dictionary<string, string>();
        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            var key = Unescape(kv[0]);
            fields[key] = SafeLoggedValues.Contains(key) && kv.Length > 1 ? Unescape(kv[1]) : "…";
        }
        return fields.Count == 0 ? null : fields;
    }

    private void Emit(DateTimeOffset started, string method, string path, int status, string? location,
        Dictionary<string, string>? fields, Dictionary<string, string>? headers, long bytes, string outcome)
    {
        var loc = location is null ? null : RedactLocation(location);
        try
        {
            _o.Log(JsonSerializer.Serialize(new
            {
                time = started, method, path, status, location = loc, fields, requestHeaders = headers, bytes, outcome
            }));
        }
        catch { }
        _o.Console($"{method,-4} {path} -> {status}{(loc is null ? "" : " => " + loc)}{(outcome == "ok" ? "" : " [" + outcome + "]")}");
    }

    private static string RandomSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>Stops reading after a byte limit so a client cannot stream unbounded data upstream.</summary>
    private sealed class BoundedReadStream(Stream inner, long limit) : Stream
    {
        private long _remaining = limit;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0) return 0;
            var n = inner.Read(buffer, offset, (int)Math.Min(count, _remaining)); _remaining -= n; return n;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_remaining <= 0) return 0;
            var n = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], ct).ConfigureAwait(false);
            _remaining -= n; return n;
        }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
