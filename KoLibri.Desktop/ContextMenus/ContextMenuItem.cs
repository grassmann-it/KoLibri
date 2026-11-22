using Avalonia.Controls;

namespace KoLibri.Desktop.ContextMenus;

/// <summary>
/// One entry of a context menu: a label, an enabled state, a handler, an optional submenu,
/// a separator or shortcut text. Building a menu is pure: no handler runs until the user
/// picks an entry, so opening a menu can never send a game action.
/// </summary>
public sealed class ContextMenuItem
{
    private ContextMenuItem(string label, bool isSeparator) { Label = label; IsSeparator = isSeparator; }

    public string Label { get; }
    public bool IsSeparator { get; }
    public bool IsEnabled { get; init; } = true;

    /// <summary>Why the entry is disabled; shown as its tooltip.</summary>
    public string? DisabledReason { get; init; }

    /// <summary>Shortcut hint shown at the right edge, for example "Enter".</summary>
    public string? ShortcutText { get; init; }

    /// <summary>The action. The matching button calls the same delegate (see <see cref="BindTo"/>).</summary>
    public Action? Handler { get; init; }

    public IReadOnlyList<ContextMenuItem>? Submenu { get; init; }

    public bool HasSubmenu => Submenu is { Count: > 0 };

    public static ContextMenuItem Separator { get; } = new("-", isSeparator: true) { IsEnabled = false };

    public static ContextMenuItem Action(string label, Action handler, string? shortcut = null)
        => new(label, false) { Handler = handler, ShortcutText = shortcut };

    public static ContextMenuItem Disabled(string label, string reason)
        => new(label, false) { IsEnabled = false, DisabledReason = reason };

    public static ContextMenuItem Sub(string label, IReadOnlyList<ContextMenuItem> items)
        => new(label, false) { Submenu = items };

    /// <summary>Enabled copy of the same entry when <paramref name="enabled"/>, else a disabled one with the reason.</summary>
    public ContextMenuItem When(bool enabled, string? reason = null)
        => enabled == IsEnabled ? this : new ContextMenuItem(Label, IsSeparator)
        {
            IsEnabled = enabled, DisabledReason = enabled ? null : reason,
            ShortcutText = ShortcutText, Handler = Handler, Submenu = Submenu
        };

    /// <summary>Runs the handler when the entry is enabled; returns whether it ran.</summary>
    public bool Invoke()
    {
        if (IsSeparator || !IsEnabled || Handler is null) return false;
        Handler();
        return true;
    }

    /// <summary>Makes a button run this entry's handler, so button and menu share one code path.</summary>
    public void BindTo(Button button)
    {
        button.Click += (_, _) => Invoke();
        button.IsEnabled = IsEnabled;
    }
}
