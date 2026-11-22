using Avalonia.Controls;
using Avalonia.Interactivity;
using KoLibri.Desktop.ContextMenus;

namespace KoLibri.Desktop;

/// <summary>View for the InventoryView tab area; its handlers forward to <see cref="IInventoryViewHandlers"/> until the tab worker moves them here.</summary>
public partial class InventoryView : UserControl
{
    private IShellHost? _host;
    private IInventoryViewHandlers? _handlers;

    public InventoryView() => InitializeComponent();

    /// <summary>The shell this view reports to (null until the shell attaches).</summary>
    public IShellHost? Host => _host;

    /// <summary>Right-click and keyboard menu of the item list.</summary>
    public RowContextMenuController<DesktopInventoryItem>? ItemMenu { get; private set; }

    public void Attach(IShellHost host, IInventoryViewHandlers handlers)
    {
        _host = host;
        _handlers = handlers;
        ItemMenu = RowContextMenuController<DesktopInventoryItem>.Attach(this.FindControl<ListBox>("InventoryList")!,
            row => new ItemContextMenu(host).Build(ItemManagerViewModel.ToMenuTarget(row)));
    }

    /// <summary>Raised with Items, Gear or Skills when the strip switches the sub-view.</summary>
    public event Action<string>? SubViewChanged;

    private void InventoryViewClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string view }) return;
        ShowSubView(view);
    }

    /// <summary>Shows Items, Gear or Skills and marks the strip button.</summary>
    public void ShowSubView(string view)
    {
        this.FindControl<StackPanel>("InventoryItemsView")!.IsVisible = view == "Items";
        foreach (var name in new[] { "InvViewItems", "InvViewGear", "InvViewSkills" })
        {
            var button = this.FindControl<Button>(name)!;
            button.Classes.Set("active", Equals(button.Tag, view));
        }
        SubViewChanged?.Invoke(view);
    }

    private void InitializeComponent() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);

    private void InventorySearchChanged(object? sender, TextChangedEventArgs e) => _handlers?.InventorySearchChanged(sender, e);

    private void InventorySelectionChanged(object? sender, SelectionChangedEventArgs e) => _handlers?.InventorySelectionChanged(sender, e);

    private void ReadCharacterStorageClick(object? sender, RoutedEventArgs e) => _handlers?.ReadCharacterStorageClick(sender, e);

    private void TakeCharacterStoredItemClick(object? sender, RoutedEventArgs e) => _handlers?.TakeCharacterStoredItemClick(sender, e);

    private void PreviewCharacterAcquisitionClick(object? sender, RoutedEventArgs e) => _handlers?.PreviewCharacterAcquisitionClick(sender, e);

    private void CombineCharacterItemClick(object? sender, RoutedEventArgs e) => _handlers?.CombineCharacterItemClick(sender, e);
}
