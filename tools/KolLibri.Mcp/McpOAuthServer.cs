using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KoLibri.Safety.Access;

namespace KolLibri.Mcp;

/// <summary>A client that registered itself (RFC 7591). Only redirect targets and a display name.</summary>
internal sealed record OAuthClient(string ClientId, string ClientName, IReadOnlyList<string> RedirectUris);

/// <summary>What an AI client asked for, as shown to the human. Strings here come from the client: escape before showing.</summary>
internal sealed record PendingConsent(
    string Id, string Csrf, string ConfirmationCode, OAuthClient Client, string RedirectUri,
    string CodeChallenge, string? State, string Resource,
    IReadOnlyList<string> RequestedRights, IReadOnlyDictionary<string, RightLimit> RequestedLimits,
    IReadOnlyList<string> RequestedAccounts, string? Reason, DateTimeOffset ExpiresAt);

internal sealed record IssuedCode(
    string ClientId, string RedirectUri, string CodeChallenge, string ClientName,
    IReadOnlyList<string> Accounts, IReadOnlyDictionary<string, RightLimit> Rights, TimeSpan Lifetime,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Authorization server for the localhost MCP endpoint: client registration, consent
/// bookkeeping, authorization codes with PKCE and token issue. The human decision
/// happens on the consent page; this class never grants anything by itself.
/// </summary>
internal sealed class McpOAuthServer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly GrantStore _grants;
    private readonly TimeProvider _clock;
    private readonly string _clientsPath;
    private readonly object _gate = new();
    private readonly Dictionary<string, OAuthClient> _clients = new();
    private readonly ConcurrentDictionary<string, PendingConsent> _pending = new();
    private readonly ConcurrentDictionary<string, IssuedCode> _codes = new();

    public McpOAuthServer(GrantStore grants, string? clientsPath = null, TimeProvider? clock = null)
    {
        _grants = grants;
        _clock = clock ?? TimeProvider.System;
        _clientsPath = clientsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KoLibri", "oauth-clients.json");
        try
        {
            if (File.Exists(_clientsPath))
                foreach (var c in JsonSerializer.Deserialize<List<OAuthClient>>(File.ReadAllText(_clientsPath), Json) ?? [])
                    _clients[c.ClientId] = c;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
    }

    /// <summary>Registers a client; only loopback http, https and custom-scheme redirects are accepted.</summary>
    public OAuthClient? Register(string? name, IReadOnlyList<string> redirectUris)
    {
        if (redirectUris.Count is 0 or > 10 || redirectUris.Any(u => !ValidRedirect(u))) return null;
        var client = new OAuthClient("kc_" + Hex(10), Clean(name, 80) ?? "Unnamed client", redirectUris.ToArray());
        lock (_gate)
        {
            _clients[client.ClientId] = client;
            Directory.CreateDirectory(Path.GetDirectoryName(_clientsPath)!);
            File.WriteAllText(_clientsPath, JsonSerializer.Serialize(_clients.Values.ToList(), Json));
        }
        return client;
    }

    public OAuthClient? FindClient(string? id)
    {
        lock (_gate) return id is not null && _clients.TryGetValue(id, out var c) ? c : null;
    }

    /// <summary>Redirect must match a registered one; loopback addresses may differ in port (RFC 8252).</summary>
    public static bool RedirectMatches(OAuthClient client, string redirect)
    {
        if (client.RedirectUris.Contains(redirect, StringComparer.Ordinal)) return true;
        if (!Uri.TryCreate(redirect, UriKind.Absolute, out var wanted) || !IsLoopbackHttp(wanted)) return false;
        return client.RedirectUris.Any(r => Uri.TryCreate(r, UriKind.Absolute, out var u) && IsLoopbackHttp(u)
            && u.Host == wanted.Host && u.AbsolutePath == wanted.AbsolutePath);
    }

    public PendingConsent BeginConsent(
        OAuthClient client, string redirectUri, string codeChallenge, string? state, string resource,
        IEnumerable<string> rights, IReadOnlyDictionary<string, RightLimit> limits,
        IEnumerable<string> accounts, string? reason)
    {
        Sweep();
        var requested = rights.Where(r => Rights.All.Contains(r)).Distinct().ToArray();
        var pending = new PendingConsent(
            Hex(16), Hex(16), RandomNumberGenerator.GetInt32(100000, 1000000).ToString(), client, redirectUri,
            codeChallenge, state, resource, requested, limits, accounts.Distinct().ToArray(),
            Clean(reason, 400), _clock.GetUtcNow().AddMinutes(10));
        _pending[pending.Id] = pending;
        return pending;
    }

    public PendingConsent? FindPending(string? id)
        => id is not null && _pending.TryGetValue(id, out var p) && p.ExpiresAt > _clock.GetUtcNow() ? p : null;

    /// <summary>The human approved (possibly with different rights and limits). Returns the one-time code.</summary>
    public string Approve(PendingConsent pending, IEnumerable<string> accounts,
        IReadOnlyDictionary<string, RightLimit> rights, TimeSpan lifetime)
    {
        _pending.TryRemove(pending.Id, out _);
        var code = Hex(24);
        _codes[code] = new IssuedCode(pending.Client.ClientId, pending.RedirectUri, pending.CodeChallenge,
            pending.Client.ClientName, accounts.ToArray(), rights, lifetime, _clock.GetUtcNow().AddMinutes(5));
        return code;
    }

    public void Deny(PendingConsent pending) => _pending.TryRemove(pending.Id, out _);

    /// <summary>Exchanges a code for a token. The code works once, with the matching PKCE verifier only.</summary>
    public (string? Token, Grant? Grant, string? Error) Exchange(string? code, string? clientId, string? redirectUri, string? verifier)
    {
        if (code is null || !_codes.TryRemove(code, out var issued)) return (null, null, "invalid_grant");
        if (issued.ExpiresAt <= _clock.GetUtcNow()) return (null, null, "invalid_grant");
        if (issued.ClientId != clientId || issued.RedirectUri != redirectUri) return (null, null, "invalid_grant");
        if (string.IsNullOrEmpty(verifier) || verifier.Length is < 43 or > 128) return (null, null, "invalid_grant");
        var computed = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(issued.CodeChallenge)))
            return (null, null, "invalid_grant");
        var (grant, token) = _grants.Create(issued.ClientName, issued.Accounts, issued.Rights, issued.Lifetime);
        return (token, grant, null);
    }

    private void Sweep()
    {
        var now = _clock.GetUtcNow();
        foreach (var k in _pending.Where(p => p.Value.ExpiresAt <= now).Select(p => p.Key).ToList()) _pending.TryRemove(k, out _);
        foreach (var k in _codes.Where(p => p.Value.ExpiresAt <= now).Select(p => p.Key).ToList()) _codes.TryRemove(k, out _);
    }

    private static bool ValidRedirect(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || u.Fragment.Length > 0) return false;
        if (u.Scheme == Uri.UriSchemeHttp) return IsLoopbackHttp(u);
        if (u.Scheme == Uri.UriSchemeHttps) return true;
        return u.Scheme is not ("javascript" or "data" or "file" or "vbscript");
    }

    private static bool IsLoopbackHttp(Uri u)
        => u.Scheme == Uri.UriSchemeHttp && (u.Host is "127.0.0.1" or "localhost" or "[::1]" or "::1");

    private static string? Clean(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = new string(text.Where(c => !char.IsControl(c) || c == ' ').ToArray()).Trim();
        return t.Length > max ? t[..max] : t;
    }

    private static string Hex(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();
}
