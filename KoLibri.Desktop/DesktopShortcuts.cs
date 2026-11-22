using Avalonia.Input;

namespace KoLibri.Desktop;

/// <summary>What a shortcut does. The window handles each by id; the help panel lists them.</summary>
public enum DesktopCommand
{
    OpenAdventures, OpenInventory, OpenMall, OpenDaily, OpenChat, OpenBrowser, OpenSettings,
    Refresh, CancelLoad, Help, ContextMenu
}

/// <summary>One gesture: a key plus whether it needs the platform command key (Cmd on macOS, Ctrl elsewhere).</summary>
public sealed record DesktopShortcut(string Id, DesktopCommand Command, string Label, Key[] Keys, bool NeedsCommandKey, bool NeedsShift = false)
{
    /// <summary>Text shown in menus and the help panel, e.g. "Cmd+1" or "Ctrl+1".</summary>
    public string Display(bool isMac)
    {
        var key = DisplayKey(Keys[0]);
        return (NeedsCommandKey ? (isMac ? "Cmd+" : "Ctrl+") : "") + (NeedsShift ? "Shift+" : "") + key;
    }

    public bool Matches(Key key, KeyModifiers modifiers, bool isMac)
    {
        var command = isMac ? KeyModifiers.Meta : KeyModifiers.Control;
        var want = (NeedsCommandKey ? command : KeyModifiers.None) | (NeedsShift ? KeyModifiers.Shift : KeyModifiers.None);
        return modifiers == want && Keys.Contains(key);
    }

    private static string DisplayKey(Key key) => key switch
    {
        Key.D0 => "0", Key.D1 => "1", Key.D2 => "2", Key.D3 => "3", Key.D4 => "4", Key.D5 => "5", Key.D6 => "6",
        Key.OemComma => ",", Key.OemPeriod => ".", Key.OemQuestion => "/", Key.Escape => "Esc",
        _ => key.ToString()
    };
}

/// <summary>
/// The one shortcut catalog of the desktop app. The window's key handler, the help panel and the other catalogs
/// (<see cref="KoLibriUxCatalog"/>, <see cref="ShellViewModel"/>) all read this list. Navigation uses the command key
/// (Cmd on macOS, Ctrl elsewhere) so macOS never sees Ctrl+digit, which the system keeps for Spaces. No entry uses
/// a key combination macOS reserves (Cmd+Q, Cmd+H, Cmd+M, Cmd+W, Cmd+Space, Cmd+Tab).
/// </summary>
public static class DesktopShortcuts
{
    public static IReadOnlyList<DesktopShortcut> All { get; } =
    [
        new("navigation.adventure", DesktopCommand.OpenAdventures, "Adventures", [Key.D1, Key.NumPad1], true),
        new("navigation.inventory", DesktopCommand.OpenInventory, "Inventory", [Key.D2, Key.NumPad2], true),
        new("navigation.mall", DesktopCommand.OpenMall, "Mall", [Key.D3, Key.NumPad3], true),
        new("navigation.daily", DesktopCommand.OpenDaily, "Daily", [Key.D4, Key.NumPad4], true),
        new("navigation.chat", DesktopCommand.OpenChat, "Chat & Mail", [Key.D5, Key.NumPad5], true),
        new("navigation.browser", DesktopCommand.OpenBrowser, "Browser", [Key.D6, Key.NumPad6], true),
        new("navigation.settings", DesktopCommand.OpenSettings, "Settings", [Key.OemComma], true),
        new("shell.refresh", DesktopCommand.Refresh, "Refresh the current tab", [Key.F5], false),
        new("shell.refresh-command", DesktopCommand.Refresh, "Refresh the current tab (no function key)", [Key.R], true),
        new("shell.cancel", DesktopCommand.CancelLoad, "Cancel the running load (End run is a button on Adventures)", [Key.OemPeriod], true),
        new("shell.shortcut-help", DesktopCommand.Help, "Keyboard shortcuts (this list)", [Key.F1], false),
        new("shell.shortcut-help-command", DesktopCommand.Help, "Keyboard shortcuts (no function key)", [Key.OemQuestion], true),
        new("shell.context-menu", DesktopCommand.ContextMenu, "Open the menu of the selected row", [Key.F10], false, NeedsShift: true),
    ];

    public static DesktopShortcut? Find(Key key, KeyModifiers modifiers, bool isMac)
        => All.FirstOrDefault(s => s.Matches(key, modifiers, isMac));

    /// <summary>Lines for the help panel: "Cmd+1 — Adventures".</summary>
    public static IReadOnlyList<string> HelpLines(bool isMac)
        => All.Select(s => $"{s.Display(isMac)}  —  {s.Label}").ToArray();
}

/// <summary>One row of the help panel.</summary>
public sealed record ShortcutHelpRow(string Gesture, string Label);
