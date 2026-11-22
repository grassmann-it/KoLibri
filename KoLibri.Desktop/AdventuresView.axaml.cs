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

    /// <summary>Set by the shell: starts an adventure (1 turn) or a bounded run (N turns) at a location row.</summary>
    public Action<LocationMenuTarget, int>? AdventureHere { get; set; }

    /// <summary>Set by the shell: the job the batch form describes right now (null when its input is invalid).</summary>
    public Func<AdventureJob?>? CurrentJob { get; set; }

    public void Attach(IShellHost host, IAdventuresViewHandlers handlers)
    {
        _host = host;
        _handlers = handlers;
        LocationMenu = RowContextMenuController<EncounterBrowserLocationRow>.Attach(this.FindControl<ListBox>("EncounterLocationsGrid")!,
            row => new LocationContextMenu(host, (target, turns) => AdventureHere?.Invoke(target, turns)).Build(CurrentJob?.Invoke() is { } job
                ? new LocationMenuTarget(row.LocationId, row.DisplayName, row.Zone, job.Turns, job.ItemId)
                : new LocationMenuTarget(row.LocationId, row.DisplayName, row.Zone)));
    }

    /// <summary>Fills the zone picker; choosing a zone filters the location list, "All zones" clears it.</summary>
    public void SetZones(IEnumerable<string> zones)
    {
        var box = this.FindControl<ComboBox>("EncounterZoneBox");
        var filter = this.FindControl<TextBox>("EncounterFilterBox");
        if (box is null || filter is null) return;
        box.ItemsSource = new[] { AllZones }.Concat(zones.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(z => z, StringComparer.OrdinalIgnoreCase)).ToArray();
        box.SelectedIndex = 0;
        box.SelectionChanged += (_, _) =>
            filter.Text = box.SelectedItem is string zone && zone != AllZones ? zone : string.Empty;
    }

    private const string AllZones = "All zones";

    private void InitializeComponent() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);

    private void RefreshClick(object? sender, RoutedEventArgs e) => _handlers?.RefreshClick(sender, e);

    private void OpenFriarsClick(object? sender, RoutedEventArgs e) => _handlers?.OpenFriarsClick(sender, e);

    private void FriarRitualClick(object? sender, RoutedEventArgs e) => _handlers?.FriarRitualClick(sender, e);

    private void FriarBlessingClick(object? sender, RoutedEventArgs e) => _handlers?.FriarBlessingClick(sender, e);

    private void CombatRunClick(object? sender, RoutedEventArgs e) => _handlers?.CombatRunClick(sender, e);
}
