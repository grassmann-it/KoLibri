using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace KoLibri.Core.Spiel;

/// <summary>
/// Keeps the response page of a run that stopped on an unknown or undecidable page
/// (UnknownFight, UnknownChoice, UnknownEncounter, IndeterminatePost), redacted, so the
/// cause can be read later instead of only a response hash.
/// </summary>
public static class UnknownPageArchive
{
    public static string DefaultDirectory
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KoLibri", "logs", "unknown-pages");

    public static bool ShouldArchive(AdventureAbortReason? reason)
        => reason is AdventureAbortReason.UnknownFight
            or AdventureAbortReason.UnknownChoice
            or AdventureAbortReason.UnknownEncounter
            or AdventureAbortReason.IndeterminatePost;

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
    public static string? TrySave(string directory, AdventureAbortReason reason, string? html, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrEmpty(html)) return null;
        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(html)))[..8].ToLowerInvariant();
            var name = $"{now.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{reason}-{hash}.html";
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, name);
            File.WriteAllText(path, Redact(html), Encoding.UTF8);
            return path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
                                              or NotSupportedException)
        {
            return null;
        }
    }
}
