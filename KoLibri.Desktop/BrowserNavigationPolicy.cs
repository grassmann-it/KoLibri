
namespace KoLibri.Desktop;

public enum BrowserNavigationDecision
{
    /// <summary>Same origin as the relay: load it in the view.</summary>
    Allow,
    /// <summary>Another web site: cancel in the view; open the system browser only after the user confirms.</summary>
    AskExternal,
    /// <summary>Not http(s) at all (javascript:, file:, data:...): cancel, open nothing.</summary>
    Block
}

/// <summary>
/// What the in-app browser may load. The view is pinned to the relay origin; the address bar shows paths only
/// (never the secret token) and accepts only relay paths.
/// </summary>
public static class BrowserNavigationPolicy
{
    public static BrowserNavigationDecision Decide(string relayOrigin, string? url)
    {
        if (string.Equals(url, "about:blank", StringComparison.OrdinalIgnoreCase)) return BrowserNavigationDecision.Allow;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return BrowserNavigationDecision.Block;
        if (IsSameOrigin(relayOrigin, url)) return BrowserNavigationDecision.Allow;
        return uri.Scheme is "http" or "https" ? BrowserNavigationDecision.AskExternal : BrowserNavigationDecision.Block;
    }

    /// <summary>Path and redacted query for the address bar. The entry URL (which carries the token) shows as its bare path.</summary>
    public static string DisplayPath(string relayOrigin, string? url)
    {
        if (!IsSameOrigin(relayOrigin, url)) return "";
        var uri = new Uri(url!);
        if (uri.AbsolutePath == EntryPath) return "/";
        return RedactUrl(uri.PathAndQuery);
    }

    /// <summary>
    /// Turns text typed in the address bar into a relay path ("adventure.php", "/main.php", or a URL on the relay origin).
    /// Null for anything else, including the entry path and protocol-relative forms.
    /// </summary>
    public static string? ResolveTyped(string relayOrigin, string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        string path;
        if (text.Contains("://", StringComparison.Ordinal))
        {
            if (!IsSameOrigin(relayOrigin, text)) return null;
            path = new Uri(text).PathAndQuery;
        }
        else path = text.StartsWith('/') ? text : "/" + text;
        if (path.StartsWith("//", StringComparison.Ordinal) || path.StartsWith("/\\", StringComparison.Ordinal)
            || path.Contains("..", StringComparison.Ordinal) || path.Contains(' ', StringComparison.Ordinal)
            || path.StartsWith(EntryPath, StringComparison.Ordinal)) return null;
        return path;
    }

    // Copied from KoLibri.Core (0.5 step 3): the relay's entry path and its address-bar redaction.

    /// <summary>The relay path that carries the one-time token (see <see cref="RelayServer.EntryUri"/>).</summary>
    public const string EntryPath = "/__kolibri/enter";

    private static readonly HashSet<string> SafeLoggedValues = new(StringComparer.OrdinalIgnoreCase)
        { "snarfblat", "action", "whichchoice", "option", "which", "whichitem", "ajax" };

    public static bool IsSameOrigin(string origin, string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) && Uri.TryCreate(origin, UriKind.Absolute, out var o)
           && u.Scheme == o.Scheme && u.Host == o.Host && u.Port == o.Port;

    /// <summary>Path plus query where only the safe keys keep their values: "/a.php?pwd=…&amp;action=x".</summary>
    public static string RedactUrl(string? rawUrl)
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
}
