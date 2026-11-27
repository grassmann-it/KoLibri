namespace KoLibri.Desktop;

/// <summary>
/// The one switch for extras outside the 0.5.0 scope (manual adventuring, consumption, inventory, mall buy, KoLmail,
/// Daily): Arbitrage, clan VIP lounge and stash, farm figures. Off by default. On when the environment variable
/// <c>KOLIBRI_EXPERIMENTAL=1</c> is set or the Settings box "Show experimental tools" is ticked.
/// While off, these panels are hidden and start no requests. The code stays in place.
/// </summary>
public static class ExperimentalFeatures
{
    public const string EnvironmentVariable = "KOLIBRI_EXPERIMENTAL";

    /// <summary>True for "1", "true", "yes" or "on" (case ignored).</summary>
    public static bool FromEnvironment(Func<string, string?>? read = null)
    {
        var value = (read ?? Environment.GetEnvironmentVariable)(EnvironmentVariable)?.Trim();
        return value is not null
            && (value == "1"
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsEnabled(DesktopSettings settings, Func<string, string?>? read = null)
        => settings.ExperimentalTools || FromEnvironment(read);
}
