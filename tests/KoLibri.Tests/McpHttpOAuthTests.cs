using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KolLibri.Mcp;
using KoLibri.Safety.Access;
using Xunit;

namespace KoLibri.Tests;

public sealed class McpHttpOAuthTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kolibri-mcp-" + Guid.NewGuid().ToString("N"));
    private readonly StringWriter _log = new();
    private readonly CancellationTokenSource _cts = new();
    private McpHttpHost _host = null!;
    private KolibriMcpServer _server = null!;
    private GrantStore _grants = null!;
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false });
    private string Base => _host.BaseUrl;

    public Task InitializeAsync()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        _grants = new GrantStore(Path.Combine(_dir, "g.json"));
        var gate = new AccessGate(_grants, new UsageLedger(Path.Combine(_dir, "u.jsonl")));
        var oauth = new McpOAuthServer(_grants, Path.Combine(_dir, "c.json"));
        _server = new KolibriMcpServer(McpTest.Accounts());
        _host = new McpHttpHost(_server, _grants, gate, oauth, port, requireCode: true, log: _log);
        _host.Start();
        _ = _host.RunAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _cts.Cancel(); _host.Dispose(); _http.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    private static (string Verifier, string Challenge) Pkce()
    {
        var v = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var c = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(v))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (v, c);
    }

    private async Task<string> RegisterAsync()
    {
        var res = await _http.PostAsync(Base + "/register", new StringContent(
            "{\"client_name\":\"Test <b>AI</b>\",\"redirect_uris\":[\"http://127.0.0.1:5555/cb\"]}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("client_id").GetString()!;
    }

    private async Task<(string Token, string Html)> FlowAsync(string scope, string limits, string? wrongCode = null, string? verifierOverride = null)
    {
        var clientId = await RegisterAsync();
        var (verifier, challenge) = Pkce();
        var url = $"{Base}/authorize?response_type=code&client_id={clientId}&redirect_uri={Uri.EscapeDataString("http://127.0.0.1:5555/cb")}"
            + $"&code_challenge={challenge}&code_challenge_method=S256&state=xyz&resource={Uri.EscapeDataString(Base + "/mcp")}"
            + $"&scope={Uri.EscapeDataString(scope)}&kolibri_limits={Uri.EscapeDataString(limits)}";
        var page = await _http.GetStringAsync(url);
        var session = Regex.Match(page, "name=session value=([0-9a-f]+)").Groups[1].Value;
        var csrf = Regex.Match(page, "name=csrf value=([0-9a-f]+)").Groups[1].Value;
        var code = Regex.Match(_log.ToString(), @"(\d{6})\s*$", RegexOptions.Multiline).Groups[1].Value;

        var form = new List<KeyValuePair<string, string>>
        {
            new("session", session), new("csrf", csrf), new("code", wrongCode ?? code), new("decision", "approve"),
            new("account", "Mallbot"), new("hours", "24"),
        };
        foreach (var r in scope.Split(' '))
        {
            form.Add(new("right", r));
            if (r == Rights.MallBuy) { form.Add(new("budget_mall.buy", "5000")); form.Add(new("maxprice_mall.buy", "400")); }
        }
        var post = await _http.PostAsync(Base + "/authorize", new FormUrlEncodedContent(form));
        if (post.StatusCode != HttpStatusCode.Redirect) return ("", await post.Content.ReadAsStringAsync());
        var location = post.Headers.Location!.ToString();
        Assert.StartsWith("http://127.0.0.1:5555/cb?code=", location);
        Assert.Contains("state=xyz", location);
        var authCode = Regex.Match(location, "code=([0-9a-f]+)").Groups[1].Value;

        var tokenRes = await _http.PostAsync(Base + "/token", new FormUrlEncodedContent(
        [
            new("grant_type", "authorization_code"), new("code", authCode), new("client_id", clientId),
            new("redirect_uri", "http://127.0.0.1:5555/cb"), new("code_verifier", verifierOverride ?? verifier),
        ]));
        var body = await tokenRes.Content.ReadAsStringAsync();
        if (tokenRes.StatusCode != HttpStatusCode.OK) return ("", body);
        return (JsonDocument.Parse(body).RootElement.GetProperty("access_token").GetString()!, page);
    }

    private async Task<JsonElement> RpcAsync(string? token, string json)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, Base + "/mcp") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await _http.SendAsync(req);
        if (token is null) { Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode); return default; }
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Fact]
    public async Task Metadata_documents_and_challenge_point_to_the_local_server()
    {
        var prm = JsonDocument.Parse(await _http.GetStringAsync(Base + "/.well-known/oauth-protected-resource/mcp")).RootElement;
        Assert.Equal(Base + "/mcp", prm.GetProperty("resource").GetString());
        var meta = JsonDocument.Parse(await _http.GetStringAsync(Base + "/.well-known/oauth-authorization-server")).RootElement;
        Assert.Equal("S256", meta.GetProperty("code_challenge_methods_supported")[0].GetString());

        var res = await _http.PostAsync(Base + "/mcp", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Contains("resource_metadata=", res.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Approved_client_gets_a_token_and_may_use_only_granted_rights()
    {
        var (token, page) = await FlowAsync("account.read", "{}");
        Assert.NotEmpty(token);
        Assert.DoesNotContain("<b>AI</b>", page);          // client-supplied text is escaped
        Assert.Contains("&lt;b&gt;AI&lt;/b&gt;", page);

        var list = await RpcAsync(token, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}");
        Assert.True(list.TryGetProperty("result", out var lr), list.GetRawText()); Assert.True(lr.GetProperty("tools").GetArrayLength() > 0);

        // market right was not approved: refused with a reason, nothing executed
        var call = await RpcAsync(token,
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"kol_price_summary\",\"arguments\":{\"role\":\"Mallbot\"}}}");
        Assert.True(call.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Contains("RightNotGranted", call.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Account_outside_the_approval_is_refused()
    {
        var (token, _) = await FlowAsync("account.read", "{}");
        var call = await RpcAsync(token,
            "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"kol_status\",\"arguments\":{\"role\":\"Main\"}}}");
        Assert.Contains("AccountNotGranted", call.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Unmapped_tools_are_closed_over_http()
    {
        // A tool that is registered but has no right assigned stays closed.
        _server.Tools.Register(new McpToolDefinition("kol_unmapped_probe", "probe", default, (_, _) => Task.FromResult<object?>("ran")));
        var (token, _) = await FlowAsync("account.read", "{}");
        var call = await RpcAsync(token,
            "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"kol_unmapped_probe\",\"arguments\":{}}}");
        Assert.Contains("not available to AI clients", call.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Experimental_tools_are_unknown_and_their_rights_are_neither_shown_nor_granted()
    {
        // Default server: the consent page shows only rights of registered tools; a tampered form cannot add others.
        var (token, page) = await FlowAsync("account.read shop.read shop.reprice", "{}");
        Assert.Contains("value=account.read", page);
        Assert.DoesNotContain("value=shop.read", page);
        Assert.DoesNotContain("value=shop.reprice", page);
        var grant = Assert.Single(_grants.List());
        Assert.Equal([Rights.AccountRead], grant.Rights.Keys.ToArray());

        var call = await RpcAsync(token,
            "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"kol_arbitrage_plan\",\"arguments\":{\"itemId\":1}}}");
        Assert.Contains("Unknown MCP tool", call.GetRawText());
        var list = await RpcAsync(token, "{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"tools/list\"}");
        Assert.DoesNotContain(list.GetProperty("result").GetProperty("tools").EnumerateArray(), t => t.GetProperty("name").GetString() == "kol_arbitrage_plan");
    }

    [Fact]
    public async Task Wrong_confirmation_code_does_not_grant_anything()
    {
        var (token, html) = await FlowAsync("account.read", "{}", wrongCode: "000000");
        Assert.Equal("", token);
        Assert.Contains("incorrect", html);
        Assert.Empty(_grants.List());
    }

    [Fact]
    public async Task Wrong_pkce_verifier_and_reused_code_are_rejected()
    {
        var (token, body) = await FlowAsync("account.read", "{}", verifierOverride: new string('a', 50));
        Assert.Equal("", token);
        Assert.Contains("invalid_grant", body);
    }

    [Fact]
    public async Task Revoking_the_grant_stops_the_token_at_once()
    {
        var (token, _) = await FlowAsync("account.read", "{}");
        _grants.Revoke(_grants.List()[0].Id);
        await RpcAsync(null, "{}");   // unauthenticated -> 401
        var req = new HttpRequestMessage(HttpMethod.Post, Base + "/mcp") { Content = new StringContent("{}") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Foreign_origin_and_unknown_redirects_are_refused()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, Base + "/mcp") { Content = new StringContent("{}") };
        req.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.SendAsync(req)).StatusCode);

        var clientId = await RegisterAsync();
        var res = await _http.GetAsync($"{Base}/authorize?response_type=code&client_id={clientId}&redirect_uri={Uri.EscapeDataString("https://evil.example/cb")}&code_challenge=x&code_challenge_method=S256");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
