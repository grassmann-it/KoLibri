using System.Net;
using System.Text;
using System.Text.Json;
using KoLibri.Core.Basis;

internal static partial class KolibriCli
{
    private static readonly HashSet<string> RelayLoggedFieldValues = new(StringComparer.OrdinalIgnoreCase)
        { "snarfblat", "action", "whichchoice", "option", "which", "whichitem", "ajax" };

    private static readonly HashSet<string> RelayDroppedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "host", "cookie", "accept-encoding", "content-length", "connection", "transfer-encoding", "expect", "proxy-connection" };

    private static readonly HashSet<string> RelayDroppedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "set-cookie", "content-length", "content-encoding", "transfer-encoding", "connection", "keep-alive" };

    private static string RelayRedact(string? url)
        => System.Text.RegularExpressions.Regex.Replace(url ?? "", @"(?i)([?&](?:pwd|pwdhash|password)=)[^&]*", "$1…");

    private const string RelayKolOrigin = "https://www.kingdomofloathing.com";

    /// <summary>
    /// relay --role R [--port 8765] [--log FILE] [--browser-ua]
    /// Local reverse proxy: opens the stored KoLibri session in your own browser at http://127.0.0.1:PORT/
    /// and logs every request (headers, form field names, status, redirect target; never cookies or passwords).
    /// </summary>
    private static async Task<int> RelayAsync(string[] args)
    {
        var role = ParseRole(GetOption(args, "--role") ?? "main");
        var port = Math.Clamp(ReadInt(args, "--port", 8765), 1024, 65535);
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

        using var upstream = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All
        }) { Timeout = TimeSpan.FromSeconds(60) };

        var relayOrigin = $"http://127.0.0.1:{port}";
        using var listener = new HttpListener();
        listener.Prefixes.Add(relayOrigin + "/");
        listener.Start();
        var logLock = new object();
        Console.WriteLine($"KoLibri relay for {role} ({session.Profile.DisplayName}) on {relayOrigin}/");
        Console.WriteLine($"Open {relayOrigin}/main.php in your browser. User-Agent sent to KoL: {(browserUa ? "the browser's own" : sessionUa)}");
        Console.WriteLine($"Log: {logPath}   (Ctrl-C to stop)");
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); listener.Stop(); };

        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => RelayHandleAsync(ctx, upstream, relayOrigin, cookie, sessionUa, browserUa, logPath, logLock));
        }
        return 0;
    }

    private static async Task RelayHandleAsync(HttpListenerContext ctx, HttpClient upstream, string relayOrigin,
        string cookie, string sessionUa, bool browserUa, string logPath, object logLock)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        var started = DateTimeOffset.Now;
        try
        {
            var host = req.UserHostName ?? "";
            if (!host.StartsWith("127.0.0.1", StringComparison.Ordinal) && !host.StartsWith("localhost", StringComparison.OrdinalIgnoreCase))
            {
                res.StatusCode = 403; res.Close(); return;
            }
            var target = new Uri(RelayKolOrigin + req.RawUrl);
            using var outgoing = new HttpRequestMessage(new HttpMethod(req.HttpMethod), target);
            var sent = new Dictionary<string, string>();
            foreach (string name in req.Headers)
            {
                if (RelayDroppedRequestHeaders.Contains(name)) continue;
                var value = (req.Headers[name] ?? "").Replace(relayOrigin, RelayKolOrigin, StringComparison.OrdinalIgnoreCase)
                    .Replace("http://localhost:" + new Uri(relayOrigin).Port, RelayKolOrigin, StringComparison.OrdinalIgnoreCase);
                if (name.Equals("user-agent", StringComparison.OrdinalIgnoreCase) && !browserUa) value = sessionUa;
                if (name.StartsWith("content-", StringComparison.OrdinalIgnoreCase)) continue;
                if (outgoing.Headers.TryAddWithoutValidation(name, value)) sent[name] = value;
            }
            if (!browserUa) { outgoing.Headers.Remove("User-Agent"); outgoing.Headers.TryAddWithoutValidation("User-Agent", sessionUa); sent["User-Agent"] = sessionUa; }
            outgoing.Headers.TryAddWithoutValidation("Cookie", cookie);

            var fields = new Dictionary<string, string>();
            if (req.HasEntityBody)
            {
                using var ms = new MemoryStream();
                await req.InputStream.CopyToAsync(ms);
                var bytes = ms.ToArray();
                outgoing.Content = new ByteArrayContent(bytes);
                if (req.ContentType is not null) outgoing.Content.Headers.TryAddWithoutValidation("Content-Type", req.ContentType);
                if ((req.ContentType ?? "").StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
                    foreach (var pair in Encoding.UTF8.GetString(bytes).Split('&', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var kv = pair.Split('=', 2);
                        var key = Uri.UnescapeDataString(kv[0].Replace('+', ' '));
                        fields[key] = RelayLoggedFieldValues.Contains(key) && kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "…";
                    }
            }
            using var response = await upstream.SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead);
            res.StatusCode = (int)response.StatusCode;
            string? location = null;
            foreach (var h in response.Headers.Concat(response.Content.Headers))
            {
                if (RelayDroppedResponseHeaders.Contains(h.Key)) continue;
                var value = string.Join(", ", h.Value);
                if (h.Key.Equals("Location", StringComparison.OrdinalIgnoreCase))
                {
                    location = value;
                    value = value.Replace(RelayKolOrigin, relayOrigin, StringComparison.OrdinalIgnoreCase);
                }
                try { res.Headers[h.Key] = value; } catch { }
            }
            var body = await response.Content.ReadAsByteArrayAsync();
            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (type is "text/html" or "text/javascript" or "application/javascript" or "text/css")
            {
                var text = Encoding.UTF8.GetString(body)
                    .Replace(RelayKolOrigin, relayOrigin, StringComparison.OrdinalIgnoreCase)
                    .Replace("http://www.kingdomofloathing.com", relayOrigin, StringComparison.OrdinalIgnoreCase);
                body = Encoding.UTF8.GetBytes(text);
                res.ContentType = response.Content.Headers.ContentType?.ToString().Replace("ISO-8859-1", "utf-8") ?? type;
                res.ContentEncoding = Encoding.UTF8;
            }
            res.ContentLength64 = body.Length;
            await res.OutputStream.WriteAsync(body);

            var entry = new
            {
                time = started, method = req.HttpMethod, path = RelayRedact(req.RawUrl), status = (int)response.StatusCode,
                location = RelayRedact(location), fields = fields.Count == 0 ? null : fields, requestHeaders = sent, bytes = body.Length
            };
            lock (logLock) File.AppendAllText(logPath, JsonSerializer.Serialize(entry) + "\n");
            Console.WriteLine($"{req.HttpMethod,-4} {RelayRedact(req.RawUrl)} -> {(int)response.StatusCode}{(location is null ? "" : " => " + RelayRedact(location))}");
        }
        catch (Exception ex)
        {
            try { res.StatusCode = 502; var b = Encoding.UTF8.GetBytes("Relay error: " + ex.Message); await res.OutputStream.WriteAsync(b); } catch { }
            Console.WriteLine($"{req.HttpMethod} {RelayRedact(req.RawUrl)} -> relay error: {ex.Message}");
        }
        finally { try { res.Close(); } catch { } }
    }
}
