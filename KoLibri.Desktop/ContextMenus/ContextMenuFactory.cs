using Avalonia.Controls;
using Avalonia.Layout;

namespace KoLibri.Desktop.ContextMenus;

/// <summary>Turns <see cref="ContextMenuItem"/> lists into Avalonia menus.</summary>
public static class ContextMenuFactory
{
    public static ContextMenu Create(IReadOnlyList<ContextMenuItem> items)
    {
        var menu = new ContextMenu();
        foreach (var control in Convert(items)) menu.Items.Add(control);
        return menu;
    }

    private static IEnumerable<Control> Convert(IEnumerable<ContextMenuItem> items)
    {
        foreach (var item in items)
        {
            if (item.IsSeparator) { yield return new Separator(); continue; }
            var menuItem = new MenuItem { Header = BuildHeader(item), IsEnabled = item.IsEnabled };
            if (item.DisabledReason is { Length: > 0 } reason) ToolTip.SetTip(menuItem, reason);
            if (item.HasSubmenu)
                foreach (var child in Convert(item.Submenu!)) menuItem.Items.Add(child);
            else
                menuItem.Click += (_, _) => item.Invoke();
            yield return menuItem;
        }
    }

    private static object BuildHeader(ContextMenuItem item)
    {
        if (string.IsNullOrEmpty(item.ShortcutText)) return item.Label;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinWidth = 180 };
        grid.Children.Add(new TextBlock { Text = item.Label });
        var shortcut = new TextBlock
        {
            Text = item.ShortcutText, Opacity = 0.6, Margin = new Avalonia.Thickness(24, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Grid.SetColumn(shortcut, 1);
        grid.Children.Add(shortcut);
        return grid;
    }
}
