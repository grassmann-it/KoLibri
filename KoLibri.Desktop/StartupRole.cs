using KoLibri.Core.Basis;

namespace KoLibri.Desktop;

/// <summary>
/// Which role the window opens with: the configured default, else the role used last time, else the only role that
/// has an account, else Main. The last used role lives in its own small file next to the settings so the Settings page
/// (which rewrites the settings file) can never overwrite it.
/// </summary>
public static class StartupRole
{
    public const string LastUsedFileName = "last-account.txt";

    public static AccountRole Resolve(string? defaultAccount, string? lastUsed, IReadOnlyCollection<AccountRole>? configured)
    {
        if (TryParse(defaultAccount, out var d)) return d;
        if (TryParse(lastUsed, out var l)) return l;
        if (configured is { Count: 1 }) return configured.First();
        return AccountRole.Main;
    }

    /// <summary>True when the choice is already settled without looking at the configured accounts.</summary>
    public static bool IsSettled(string? defaultAccount, string? lastUsed) => TryParse(defaultAccount, out _) || TryParse(lastUsed, out _);

    public static string LastUsedPath(DesktopSettingsStore store)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(store.Path))!, LastUsedFileName);

    public static string? ReadLast(DesktopSettingsStore store)
    {
        try { var p = LastUsedPath(store); return File.Exists(p) ? File.ReadAllText(p).Trim() : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public static void WriteLast(DesktopSettingsStore store, AccountRole role)
    {
        try
        {
            var p = LastUsedPath(store);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, role.ToString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* a convenience only */ }
    }

    private static bool TryParse(string? text, out AccountRole role)
        => Enum.TryParse(text?.Trim(), ignoreCase: true, out role) && Enum.IsDefined(role)
           && !string.IsNullOrWhiteSpace(text) && !int.TryParse(text, out _);
}
