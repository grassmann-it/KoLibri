// Desktop copy of 0.4's UnknownPageArchive (KoLibri.Core, #72/#73), as in the CLI (0.5 step 3): same folder, same redaction.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace KoLibri.Desktop;

internal static class AdventureUnknownPageArchive
{
    /// <summary>~/.local/share/KoLibri/logs/unknown-pages (LocalApplicationData on every OS).</summary>
    public static string DefaultDirectory
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KoLibri", "logs", "unknown-pages");

    /// <summary>Tests only.</summary>
    internal static string? DirectoryOverride { get; set; }

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly Regex PwdPattern = new(
        @"(\bpwd(?:hash)?\b\s*(?:=|:)\s*['""]?|name\s*=\s*['""]?pwd['""]?\s+value\s*=\s*['""]?)[^'""&\s>;]+", Options);
    private static readonly Regex CookiePattern = new(
        @"(\b(?:PHPSESSID|appserver|AWSALB\w*|cookie|set-cookie)\b\s*(?:=|:)\s*['""]?)[^'""\s<>]+", Options);
    private static readonly Regex HexPattern = new(@"\b[0-9a-f]{32,}\b", Options);

    /// <summary>Removes pwd hashes, cookies and every 32+ character hex string.</summary>
    public static string Redact(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        var value = PwdPattern.Replace(html, "$1[redacted]");
        value = CookiePattern.Replace(value, "$1[redacted]");
        return HexPattern.Replace(value, "[hex]");
    }

    /// <summary>
    /// Writes <paramref name="html"/> redacted to
    /// <c>&lt;directory&gt;/&lt;utc time&gt;-&lt;reason&gt;-&lt;hash8&gt;.html</c>. Returns the path, or null when
    /// there is no page or the file could not be written (diagnostics never break a run).
    /// </summary>
    /// <remarks>
    /// <paramref name="trail"/> (how the client got to the page: requests, status codes, redirect targets) is
    /// written as a comment at the top; <paramref name="precedingHtml"/> (the page read just before, e.g.
    /// main.php after a redirect) goes, redacted, to the same name with <c>.prev.html</c>.
    /// </remarks>
    /// <summary>Saves the page redacted as &lt;utc&gt;-&lt;reason&gt;-&lt;hash8&gt;.html (trail in a comment on top, the page before as .prev.html).</summary>
    public static string? TrySave(string reason, string? html, DateTimeOffset now, IReadOnlyList<string>? trail = null, string? precedingHtml = null)
    {
        var directory = DirectoryOverride ?? DefaultDirectory;
        if (string.IsNullOrEmpty(html)) return null;
        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(html)))[..8].ToLowerInvariant();
            var name = $"{now.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{reason}-{hash}";
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, name + ".html");
            var header = trail is { Count: > 0 }
                ? "<!-- KoLibri request trail:\n" + string.Join("\n", trail.Select(step => "  " + step.Replace("--", "- -")))
                  + (string.IsNullOrEmpty(precedingHtml) ? "" : $"\n  preceding page: {name}.prev.html") + "\n-->\n"
                : string.Empty;
            File.WriteAllText(path, Redact(header + html), Encoding.UTF8);
            if (!string.IsNullOrEmpty(precedingHtml))
                File.WriteAllText(Path.Combine(directory, name + ".prev.html"), Redact(precedingHtml), Encoding.UTF8);
            return path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
