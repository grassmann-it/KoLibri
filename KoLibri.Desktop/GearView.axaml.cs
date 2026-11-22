using Avalonia.Controls;
using Avalonia.Interactivity;

namespace KoLibri.Desktop;

/// <summary>View for the GearView tab area; its handlers forward to <see cref="IGearViewHandlers"/> until the tab worker moves them here.</summary>
public partial class GearView : UserControl
{
    private IShellHost? _host;
    private IGearViewHandlers? _handlers;

    public GearView() => InitializeComponent();

    /// <summary>The shell this view reports to (null until the shell attaches).</summary>
    public IShellHost? Host => _host;

    public void Attach(IShellHost host, IGearViewHandlers handlers)
    {
        _host = host;
        _handlers = handlers;
    }

    private void InitializeComponent() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);

    private void ReadCharacterEquipmentClick(object? sender, RoutedEventArgs e) => _handlers?.ReadCharacterEquipmentClick(sender, e);

    private void UnequipCharacterSlotClick(object? sender, RoutedEventArgs e) => _handlers?.UnequipCharacterSlotClick(sender, e);

    private void WearCharacterOutfitClick(object? sender, RoutedEventArgs e) => _handlers?.WearCharacterOutfitClick(sender, e);

    private void SwitchCharacterFamiliarClick(object? sender, RoutedEventArgs e) => _handlers?.SwitchCharacterFamiliarClick(sender, e);

    private void UnequipCharacterFamiliarClick(object? sender, RoutedEventArgs e) => _handlers?.UnequipCharacterFamiliarClick(sender, e);

    private void CompareCharacterEquipmentClick(object? sender, RoutedEventArgs e) => _handlers?.CompareCharacterEquipmentClick(sender, e);
}
