using System.Collections.Specialized;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using KoLibri.Safety.Access;

namespace KolLibri.Mcp;

/// <summary>
/// Streamable-HTTP MCP endpoint plus its OAuth endpoints, bound to 127.0.0.1 only.
/// Every /mcp call needs a bearer token that a human approved on the consent page.
/// </summary>
public sealed class McpHttpHost : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly KolibriMcpServer _server;
    private readonly GrantStore _grants;
    private readonly McpOAuthServer _oauth;
    private readonly TimeProvider _clock;
    private readonly bool _requireCode;
    private readonly TextWriter _log;
    private readonly HttpListener _listener = new();
    private readonly int _requestedPort;

    public McpHttpHost(KolibriMcpServer server, GrantStore grants, AccessGate gate, McpOAuthServer oauth,
        int port = 8765, bool requireCode = true, TextWriter? log = null, TimeProvider? clock = null, string defaultRole = "Main")
    {
        _server = server; _grants = grants; _oauth = oauth; _requestedPort = port; _requireCode = requireCode;
        _log = log ?? Console.Error; _clock = clock ?? TimeProvider.System;
        _server.Authorizer = McpAccessPolicy.ForToken(gate, () => McpRequestContext.Token, defaultRole);
    }

    public int Port { get; private set; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public void Start()
    {
        if (_requestedPort <= 0) throw new InvalidOperationException("Use an explicit port.");
        Port = _requestedPort;
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var reg = ct.Register(() => { try { _listener.Stop(); } catch (ObjectDisposedException) { } });
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception) when (ct.IsCancellationRequested || !_listener.IsListening) { break; }
            _ = Task.Run(() => HandleSafeAsync(ctx, ct), ct);
        }
    }

    private async Task HandleSafeAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try { await HandleAsync(ctx, ct).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _log.WriteLine($"request failed: {ex.GetType().Name}");
            try { Send(ctx, 500, "text/plain", "internal error"); } catch { }
        }
    }

    internal async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = ctx.Request;
        // DNS-rebinding guard: only loopback host names and loopback browser origins.
        if (!IsLoopbackHost(req.Headers["Host"])) { Send(ctx, 403, "text/plain", "forbidden host"); return; }
        var origin = req.Headers["Origin"];
        if (!string.IsNullOrEmpty(origin) && !(Uri.TryCreate(origin, UriKind.Absolute, out var o) && IsLoopbackHost(o.Authority)))
        { Send(ctx, 403, "text/plain", "forbidden origin"); return; }

        var path = req.Url!.AbsolutePath.TrimEnd('/');
        switch (path)
        {
            case "/.well-known/oauth-protected-resource":
            case "/.well-known/oauth-protected-resource/mcp":
                SendJson(ctx, 200, new { resource = BaseUrl + "/mcp", authorization_servers = new[] { BaseUrl },
                    scopes_supported = _server.OfferedRights, bearer_methods_supported = new[] { "header" } });
                return;
            case "/.well-known/oauth-authorization-server":
                SendJson(ctx, 200, new
                {
                    issuer = BaseUrl, authorization_endpoint = BaseUrl + "/authorize", token_endpoint = BaseUrl + "/token",
                    registration_endpoint = BaseUrl + "/register", response_types_supported = new[] { "code" },
                    grant_types_supported = new[] { "authorization_code" }, code_challenge_methods_supported = new[] { "S256" },
                    token_endpoint_auth_methods_supported = new[] { "none" }, scopes_supported = _server.OfferedRights,
                    authorization_response_iss_parameter_supported = true, client_id_metadata_document_supported = false,
                });
                return;
            case "/register" when req.HttpMethod == "POST": await RegisterAsync(ctx).ConfigureAwait(false); return;
            case "/authorize": await AuthorizeAsync(ctx).ConfigureAwait(false); return;
            case "/token" when req.HttpMethod == "POST": await TokenAsync(ctx).ConfigureAwait(false); return;
            case "/mcp": await McpAsync(ctx, ct).ConfigureAwait(false); return;
            case "": Send(ctx, 200, "text/plain", "KoLibri MCP (localhost). Endpoint: /mcp"); return;
            default: Send(ctx, 404, "text/plain", "not found"); return;
        }
    }

    // ---- /mcp ----
    private async Task McpAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        if (ctx.Request.HttpMethod != "POST")
        {
            ctx.Response.AddHeader("Allow", "POST");
            Send(ctx, 405, "text/plain", "POST only");
            return;
        }
        var token = BearerToken(ctx.Request.Headers["Authorization"]);
        var grant = token is null ? null : _grants.FindByToken(token);
        if (grant is null || grant.RevokedAt is not null || grant.ExpiresAt <= _clock.GetUtcNow())
        {
            ctx.Response.AddHeader("WWW-Authenticate",
                $"Bearer resource_metadata=\"{BaseUrl}/.well-known/oauth-protected-resource/mcp\", scope=\"{Rights.AccountRead}\"");
            Send(ctx, 401, "text/plain", "authentication required");
            return;
        }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(await ReadBodyAsync(ctx.Request, 1_000_000).ConfigureAwait(false)); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException) { Send(ctx, 400, "text/plain", "invalid JSON"); return; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { Send(ctx, 400, "text/plain", "single JSON-RPC object expected"); return; }
            McpRequestContext.Token = token;
            var response = await _server.HandleAsync(doc.RootElement.Clone(), ct).ConfigureAwait(false);
            if (response is null) { Send(ctx, 202, "text/plain", ""); return; }
            Send(ctx, 200, "application/json", response.Value.GetRawText());
        }
    }

    // ---- OAuth ----
    private async Task RegisterAsync(HttpListenerContext ctx)
    {
        try
        {
            using var doc = JsonDocument.Parse(await ReadBodyAsync(ctx.Request, 20_000).ConfigureAwait(false));
            var root = doc.RootElement;
            var uris = root.TryGetProperty("redirect_uris", out var u) && u.ValueKind == JsonValueKind.Array
                ? u.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : [];
            var name = root.TryGetProperty("client_name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            var client = _oauth.Register(name, uris);
            if (client is null) { SendJson(ctx, 400, new { error = "invalid_redirect_uri" }); return; }
            SendJson(ctx, 201, new
            {
                client_id = client.ClientId, client_name = client.ClientName, redirect_uris = client.RedirectUris,
                token_endpoint_auth_method = "none", grant_types = new[] { "authorization_code" }, response_types = new[] { "code" },
            });
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException) { SendJson(ctx, 400, new { error = "invalid_client_metadata" }); }
    }

    private async Task AuthorizeAsync(HttpListenerContext ctx)
    {
        if (ctx.Request.HttpMethod == "POST") { await DecideAsync(ctx).ConfigureAwait(false); return; }
        var q = ctx.Request.QueryString;
        var client = _oauth.FindClient(q["client_id"]);
        var redirect = q["redirect_uri"] ?? client?.RedirectUris.FirstOrDefault();
        // Without a trusted client and redirect we must not redirect anywhere: show the error here.
        if (client is null || redirect is null || !McpOAuthServer.RedirectMatches(client, redirect))
        { Send(ctx, 400, "text/plain", "unknown client or redirect_uri"); return; }

        string? Err(string e) => RedirectBack(ctx, redirect, q["state"], error: e);
        if (q["response_type"] != "code") { Err("unsupported_response_type"); return; }
        if (q["code_challenge_method"] != "S256" || string.IsNullOrEmpty(q["code_challenge"])) { Err("invalid_request"); return; }
        var resource = q["resource"]?.TrimEnd('/') ?? BaseUrl + "/mcp";
        if (resource != BaseUrl + "/mcp" && resource != BaseUrl) { Err("invalid_target"); return; }

        var rights = (q["scope"] ?? Rights.AccountRead).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var limits = ParseLimits(q["kolibri_limits"]);
        var accounts = (q["kolibri_accounts"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var pending = _oauth.BeginConsent(client, redirect, q["code_challenge"]!, q["state"], resource, rights, limits, accounts, q["kolibri_reason"]);
        if (_requireCode)
            _log.WriteLine($"Consent code for \"{pending.Client.ClientName}\": {pending.ConfirmationCode}");
        SendHtml(ctx, 200, McpConsentPage.Render(pending, _requireCode, offered: _server.OfferedRights));
    }

    private Task DecideAsync(HttpListenerContext ctx)
    {
        var form = ReadForm(ctx.Request);
        var pending = _oauth.FindPending(form["session"]);
        if (pending is null || form["csrf"] != pending.Csrf)
        { Send(ctx, 400, "text/plain", "request expired, start again"); return Task.CompletedTask; }

        if (form["decision"] != "approve")
        {
            _oauth.Deny(pending);
            RedirectBack(ctx, pending.RedirectUri, pending.State, error: "access_denied");
            return Task.CompletedTask;
        }
        if (_requireCode && form["code"]?.Trim() != pending.ConfirmationCode)
        { SendHtml(ctx, 200, McpConsentPage.Render(pending, true, "Confirmation code incorrect.", _server.OfferedRights)); return Task.CompletedTask; }

        var accounts = form.GetValues("account") ?? [];
        var rights = new Dictionary<string, RightLimit>();
        var offered = _server.OfferedRights;
        foreach (var right in form.GetValues("right") ?? [])
        {
            // A right no registered tool uses is never granted, even if the form was edited.
            if (!offered.Contains(right)) continue;
            rights[right] = new RightLimit
            {
                DailyBudget = Num(form["budget_" + right]),
                MaxUnitPrice = Num(form["maxprice_" + right]),
                MeatReserve = Num(form["reserve_" + right]),
                MaxChangePercent = Num(form["pct_" + right]) is { } p ? (int)Math.Min(p, 100) : null,
            };
        }
        if (accounts.Length == 0 || rights.Count == 0)
        { SendHtml(ctx, 200, McpConsentPage.Render(pending, _requireCode, "Select at least one account and one right – or deny.", _server.OfferedRights)); return Task.CompletedTask; }

        TimeSpan lifetime;
        try { lifetime = TimeSpan.FromHours(Math.Clamp(Num(form["hours"]) ?? 24, 1, 720)); }
        catch (OverflowException) { lifetime = TimeSpan.FromDays(1); }
        try { RequireBudgets(rights); }
        catch (ArgumentException ex) { SendHtml(ctx, 200, McpConsentPage.Render(pending, _requireCode, ex.Message, _server.OfferedRights)); return Task.CompletedTask; }

        var code = _oauth.Approve(pending, accounts, rights, lifetime);
        RedirectBack(ctx, pending.RedirectUri, pending.State, code: code);
        return Task.CompletedTask;
    }

    private static void RequireBudgets(Dictionary<string, RightLimit> rights)
    {
        foreach (var (name, limit) in rights)
            if (OperationCatalog.All.Any(o => o.Right == name && o.Kind == OperationKind.Write) && limit.DailyBudget is null)
                throw new ArgumentException($"A daily limit is required for “{name}”.");
    }

    private async Task TokenAsync(HttpListenerContext ctx)
    {
        var form = ReadForm(ctx.Request, await ReadBodyAsync(ctx.Request, 20_000).ConfigureAwait(false));
        if (form["grant_type"] != "authorization_code") { SendJson(ctx, 400, new { error = "unsupported_grant_type" }); return; }
        var (token, grant, error) = _oauth.Exchange(form["code"], form["client_id"], form["redirect_uri"], form["code_verifier"]);
        ctx.Response.AddHeader("Cache-Control", "no-store");
        if (token is null || grant is null) { SendJson(ctx, 400, new { error }); return; }
        SendJson(ctx, 200, new
        {
            access_token = token, token_type = "Bearer",
            expires_in = (int)Math.Max(1, (grant.ExpiresAt - _clock.GetUtcNow()).TotalSeconds),
            scope = string.Join(' ', grant.Rights.Keys),
        });
    }

    // ---- helpers ----
    private static Dictionary<string, RightLimit> ParseLimits(string? json)
    {
        var map = new Dictionary<string, RightLimit>();
        if (string.IsNullOrWhiteSpace(json) || json.Length > 4000) return map;
        try
        {
            using var d = JsonDocument.Parse(json);
            foreach (var p in d.RootElement.EnumerateObject())
                map[p.Name] = new RightLimit
                {
                    DailyBudget = Get(p.Value, "dailyBudget"), MaxUnitPrice = Get(p.Value, "maxUnitPrice"),
                    MeatReserve = Get(p.Value, "meatReserve"),
                    MaxChangePercent = Get(p.Value, "maxChangePercent") is { } c ? (int)Math.Clamp(c, 0, 100) : null,
                };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        return map;

        static long? Get(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.TryGetInt64(out var l) && l >= 0 ? l : null;
    }

    private static long? Num(string? s) => long.TryParse(s, out var n) && n >= 0 ? n : null;

    private static string? BearerToken(string? header)
        => header is not null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;

    private static bool IsLoopbackHost(string? hostHeader)
    {
        if (string.IsNullOrEmpty(hostHeader)) return false;
        var host = hostHeader.StartsWith('[') ? hostHeader[..(hostHeader.IndexOf(']') + 1)] : hostHeader.Split(':')[0];
        return host is "127.0.0.1" or "localhost" or "[::1]";
    }

    private static async Task<string> ReadBodyAsync(HttpListenerRequest req, int max)
    {
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        int n;
        while ((n = await req.InputStream.ReadAsync(buf).ConfigureAwait(false)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > max) throw new InvalidDataException("body too large");
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static NameValueCollection ReadForm(HttpListenerRequest req, string? body = null)
    {
        body ??= ReadBodyAsync(req, 50_000).GetAwaiter().GetResult();
        return HttpUtility.ParseQueryString(body);
    }

    private string? RedirectBack(HttpListenerContext ctx, string redirect, string? state, string? code = null, string? error = null)
    {
        var sep = redirect.Contains('?') ? '&' : '?';
        var url = new StringBuilder(redirect).Append(sep);
        if (code is not null) url.Append("code=").Append(Uri.EscapeDataString(code));
        if (error is not null) url.Append("error=").Append(Uri.EscapeDataString(error));
        if (state is not null) url.Append("&state=").Append(Uri.EscapeDataString(state));
        url.Append("&iss=").Append(Uri.EscapeDataString(BaseUrl));
        ctx.Response.StatusCode = 302;
        ctx.Response.RedirectLocation = url.ToString();
        ctx.Response.Close();
        return null;
    }

    private static void SendJson(HttpListenerContext ctx, int status, object body)
        => Send(ctx, status, "application/json", JsonSerializer.Serialize(body, Json));

    private static void SendHtml(HttpListenerContext ctx, int status, string html)
    {
        ctx.Response.AddHeader("X-Frame-Options", "DENY");
        ctx.Response.AddHeader("Content-Security-Policy", "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'");
        ctx.Response.AddHeader("Cache-Control", "no-store");
        Send(ctx, status, "text/html; charset=utf-8", html);
    }

    private static void Send(HttpListenerContext ctx, int status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = bytes.Length;
        if (bytes.Length > 0) ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    public void Dispose() => _listener.Close();
}
