using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KoLibri.Core.Basis;

/// <summary>
/// Resolves the real KoL request hash ("pwd") of a signed-in session. The
/// <c>charpwd</c>/<c>PHPSESSID</c> cookie is NOT that hash: KoL rejects POSTs
/// that carry the cookie value. KoLmafia reads the hash from
/// <c>api.php?what=status</c> (field <c>pwd</c>) and falls back to page markup.
/// The result is cached in memory per cookie header (a new login has a new
/// cookie and therefore resolves again). The hash is never logged.
/// </summary>
public static class KoLPwdHashResolver
{
    private static readonly ConcurrentDictionary<string, string> Cache = new();
    private static readonly Regex PageHash = new(
        @"(?:pwd=|pwdhash\s*=\s*[""'])(?<h>[0-9a-fA-F]{16,})", RegexOptions.Compiled);

    /// <summary>The hash, or null when KoL did not expose one (caller must refuse to POST).</summary>
    public static async Task<string?> TryResolveAsync(
        HttpClient http, string cookieHeader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (string.IsNullOrWhiteSpace(cookieHeader)) return null;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cookieHeader)));
        if (Cache.TryGetValue(key, out var cached)) return cached;

        var hash = await FromStatusApiAsync(http, cancellationToken).ConfigureAwait(false);
        if (hash is null)
        {
            foreach (var page in new[] { "charpane.php", "inventory.php?which=3" })
            {
                var body = await GetBodyAsync(http, page, cancellationToken).ConfigureAwait(false);
                if (body is not null && PageHash.Match(body) is { Success: true } found)
                {
                    hash = found.Groups["h"].Value;
                    break;
                }
            }
        }
        if (hash is not null) Cache[key] = hash;
        return hash;
    }

    /// <summary>Like <see cref="TryResolveAsync"/> but throws a clear error instead of returning null.</summary>
    public static async Task<string> RequireAsync(
        HttpClient http, string cookieHeader, CancellationToken cancellationToken = default)
        => await TryResolveAsync(http, cookieHeader, cancellationToken).ConfigureAwait(false)
           ?? throw new InvalidOperationException(
               "KoL did not expose a request hash for this session. Sign in again; nothing was sent.");

    public static void Forget(string cookieHeader)
    {
        if (!string.IsNullOrWhiteSpace(cookieHeader))
            Cache.TryRemove(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cookieHeader))), out _);
    }

    /// <summary>The verified player id and name of the session, from api.php status; null if unreadable.</summary>
    public static async Task<KoLPlayerIdentity?> TryReadIdentityAsync(HttpClient http, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        var body = await GetBodyAsync(http, "api.php?what=status&for=KoLibri", cancellationToken).ConfigureAwait(false);
        return body is null ? null : ParseIdentityJson(body);
    }

    internal static KoLPlayerIdentity? ParseIdentityJson(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            static string? Text(JsonElement e, string n) => e.TryGetProperty(n, out var v)
                ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null }
                : null;
            var id = Text(root, "playerid");
            var name = Text(root, "name");
            return string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) ? null : new KoLPlayerIdentity(id.Trim(), name.Trim());
        }
        catch (JsonException) { return null; }
    }

    internal static string? ParseStatusJson(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("pwd", out var pwd) && pwd.ValueKind == JsonValueKind.String &&
                pwd.GetString() is { Length: >= 8 } value && Regex.IsMatch(value, "^[0-9a-fA-F]+$"))
                return value;
        }
        catch (JsonException) { }
        return null;
    }

    private static async Task<string?> FromStatusApiAsync(HttpClient http, CancellationToken ct)
    {
        var body = await GetBodyAsync(http, "api.php?what=status&for=KoLibri", ct).ConfigureAwait(false);
        return body is null ? null : ParseStatusJson(body);
    }

    private static async Task<string?> GetBodyAsync(HttpClient http, string relative, CancellationToken ct)
    {
        try
        {
            var uri = http.BaseAddress is { } b ? new Uri(b, relative) : new Uri("https://www.kingdomofloathing.com/" + relative);
            using var response = await http.GetAsync(uri, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException) { return null; }
    }
}

/// <summary>Player id and character name as KoL reports them for a signed-in session.</summary>
public sealed record KoLPlayerIdentity(string PlayerId, string Name);
