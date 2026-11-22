using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace KoLibri.Desktop.ContextMenus;

/// <summary>
/// Attaches a right-click and keyboard (Menu key, Shift+F10) context menu to a list. The menu
/// addresses the row under the pointer, not merely the selected one: a right-click selects that
/// row first. From the keyboard it addresses the focused row, else the selected one.
/// Building the menu runs no handler, so opening it never sends a game action.
/// </summary>
public sealed class RowContextMenuController<T> where T : class
{
    private readonly ListBox _list;
    private readonly Func<T, IReadOnlyList<ContextMenuItem>> _build;
    private ContextMenu? _open;

    private RowContextMenuController(ListBox list, Func<T, IReadOnlyList<ContextMenuItem>> build)
    {
        _list = list;
        _build = build;
    }

    /// <summary>The row the last menu was built for.</summary>
    public T? AddressedRow { get; private set; }

    /// <summary>The entries of the last menu that was opened.</summary>
    public IReadOnlyList<ContextMenuItem>? LastItems { get; private set; }

    /// <summary>How many menus were opened (a test hook for "opening sends nothing").</summary>
    public int OpenCount { get; private set; }

    public static RowContextMenuController<T> Attach(ListBox list, Func<T, IReadOnlyList<ContextMenuItem>> build)
    {
        var controller = new RowContextMenuController<T>(list, build);
        list.AddHandler(InputElement.PointerPressedEvent, controller.OnPointerPressed,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        list.AddHandler(Control.ContextRequestedEvent, controller.OnContextRequested,
            RoutingStrategies.Bubble, handledEventsToo: true);
        list.AddHandler(InputElement.KeyDownEvent, controller.OnKeyDown,
            RoutingStrategies.Tunnel, handledEventsToo: false);
        return controller;
    }

    /// <summary>Selects and addresses the row that contains <paramref name="source"/>.</summary>
    public IReadOnlyList<ContextMenuItem>? OpenForPointer(Visual? source)
    {
        var item = (source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (item?.DataContext is not T row) return null;
        _list.SelectedItem = row;
        return Show(row, item);
    }

    /// <summary>Addresses the focused row, else the selected row.</summary>
    public IReadOnlyList<ContextMenuItem>? OpenForKeyboard()
    {
        var focused = TopLevel.GetTopLevel(_list)?.FocusManager?.GetFocusedElement() as Control;
        var item = focused?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (item?.DataContext is T focusedRow && item.FindAncestorOfType<ListBox>() == _list)
        {
            _list.SelectedItem = focusedRow;
            return Show(focusedRow, item);
        }
        if (_list.SelectedItem is not T selected) return null;
        return Show(selected, _list.ContainerFromItem(selected) ?? _list);
    }

    private IReadOnlyList<ContextMenuItem> Show(T row, Control anchor)
    {
        AddressedRow = row;
        var items = _build(row);
        LastItems = items;
        OpenCount++;
        _open?.Close();
        if (items.Count > 0 && anchor.IsAttachedToVisualTree())
        {
            _open = ContextMenuFactory.Create(items);
            _open.Open(anchor);
        }
        return items;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Select the row under the pointer before the menu opens (on release).
        if (!e.GetCurrentPoint(_list).Properties.IsRightButtonPressed) return;
        var item = (e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (item?.DataContext is T row) _list.SelectedItem = row;
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Handled) return;
        var result = e.TryGetPosition(_list, out _)
            ? OpenForPointer(e.Source as Visual)
            : OpenForKeyboard();
        if (result is not null) e.Handled = true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var isMenuKey = e.Key == Key.Apps
            || (e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift);
        if (!isMenuKey) return;
        if (OpenForKeyboard() is not null) e.Handled = true;
    }
}
