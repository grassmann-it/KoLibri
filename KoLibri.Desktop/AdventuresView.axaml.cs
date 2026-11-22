using Avalonia.Controls;
using Avalonia.Interactivity;
using KoLibri.Desktop.ContextMenus;

namespace KoLibri.Desktop;

/// <summary>View for the AdventuresView tab area; its handlers forward to <see cref="IAdventuresViewHandlers"/> until the tab worker moves them here.</summary>
public partial class AdventuresView : UserControl
{
    private IShellHost? _host;
    private IAdventuresViewHandlers? _handlers;

    public AdventuresView() => InitializeComponent();

    /// <summary>The shell this view reports to (null until the shell attaches).</summary>
    public IShellHost? Host => _host;

    /// <summary>Right-click and keyboard menu of the location list.</summary>
    public RowContextMenuController<EncounterBrowserLocationRow>? LocationMenu { get; private set; }

    public void Attach(IShellHost host, IAdventuresViewHandlers handlers)
    {
        _host = host;
        _handlers = handlers;
        LocationMenu = RowContextMenuController<EncounterBrowserLocationRow>.Attach(this.FindControl<ListBox>("EncounterLocationsGrid")!,
            row => new LocationContextMenu(host).Build(new LocationMenuTarget(row.LocationId, row.DisplayName, row.Zone)));
    }

    private void InitializeComponent() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);

    private void RefreshClick(object? sender, RoutedEventArgs e) => _handlers?.RefreshClick(sender, e);

    private void OpenFriarsClick(object? sender, RoutedEventArgs e) => _handlers?.OpenFriarsClick(sender, e);

    private void FriarRitualClick(object? sender, RoutedEventArgs e) => _handlers?.FriarRitualClick(sender, e);

    private void FriarBlessingClick(object? sender, RoutedEventArgs e) => _handlers?.FriarBlessingClick(sender, e);

    private void CombatRunClick(object? sender, RoutedEventArgs e) => _handlers?.CombatRunClick(sender, e);
}
