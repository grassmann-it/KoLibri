using System.Reflection;

namespace KoLibri.Core.Basis;

/// <summary>
/// The User-Agent every KoLibri HTTP client sends, so the game and Pricegun can see which library and version
/// made a request: <c>LibKolibri/0.4.0 (+https://github.com/grassmann-it/KoLibri)</c>.
/// </summary>
public static class KoLUserAgent
{
    public const string ProjectUrl = "https://github.com/grassmann-it/KoLibri";

    /// <summary>Library version without build metadata, for example 0.4.0.</summary>
    public static string Version { get; } = ReadVersion();

    public static string Value { get; } = $"LibKolibri/{Version} (+{ProjectUrl})";

    /// <summary>
    /// What 0.4.0 and earlier sent. KoL ties a session to the User-Agent it was created with, so sessions that
    /// were stored without one keep using this until the next login.
    /// </summary>
    public const string LegacyValue = "KoLibri/0.1 (Kingdom of Loathing client)";

    /// <summary>Sets the User-Agent on a client that has none yet; a caller's own value is kept.</summary>
    public static void ApplyTo(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (client.DefaultRequestHeaders.UserAgent.Count == 0)
            client.DefaultRequestHeaders.UserAgent.ParseAdd(Value);
    }

    private static string ReadVersion()
    {
        var info = typeof(KoLUserAgent).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info))
            return typeof(KoLUserAgent).Assembly.GetName().Version?.ToString(3) ?? "0";
        var plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }
}
