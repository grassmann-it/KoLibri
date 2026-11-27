using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
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
    public RowContextMenuController<LocationPickerEntry>? LocationMenu { get; private set; }

    /// <summary>Set by the shell: starts an adventure (1 turn) or a bounded run (N turns) at a location row.</summary>
    public Action<LocationMenuTarget, int>? AdventureHere { get; set; }

    /// <summary>Set by the shell: the job the batch form describes right now (null when its input is invalid).</summary>
    public Func<AdventureJob?>? CurrentJob { get; set; }

    public void Attach(IShellHost host, IAdventuresViewHandlers handlers)
    {
        _host = host;
        _handlers = handlers;
        LocationMenu = RowContextMenuController<LocationPickerEntry>.Attach(this.FindControl<ListBox>("EncounterLocationsGrid")!,
            entry => entry.Location is not { } row ? Array.Empty<ContextMenuItem>() : new LocationContextMenu(host, (target, turns) => AdventureHere?.Invoke(target, turns)).Build(CurrentJob?.Invoke() is { } job
                ? new LocationMenuTarget(row.LocationId, row.DisplayName, row.Zone, job.Turns, job.ItemId)
                : new LocationMenuTarget(row.LocationId, row.DisplayName, row.Zone)));
    }

    /// <summary>The location picker the list shows (null until the catalog is loaded).</summary>
    public LocationPickerViewModel? Picker { get; private set; }

    /// <summary>Set by the shell: Enter or a double-click chose a location.</summary>
    public Action<EncounterBrowserLocationRow>? LocationChosen { get; set; }

    /// <summary>
    /// Shows <paramref name="picker"/> in the location list: the search box filters as you type, Up/Down move
    /// between locations (from the search box too), Enter chooses, a click on a zone opens or closes it, ☆ stars.
    /// </summary>
    public void AttachLocationPicker(LocationPickerViewModel picker)
    {
        ArgumentNullException.ThrowIfNull(picker);
        var list = this.FindControl<ListBox>("EncounterLocationsGrid");
        var search = this.FindControl<TextBox>("EncounterFilterBox");
        var empty = this.FindControl<TextBlock>("LocationPickerEmpty");
        var chosen = this.FindControl<TextBlock>("LocationPickerSelected");
        if (list is null || search is null) return;
        Picker = picker;
        list.ItemsSource = picker.Entries;
        var syncing = false;

        void ShowSelection()
        {
            syncing = true;
            try
            {
                list.SelectedItem = picker.Selected;
                if (picker.Selected is { } entry) list.ScrollIntoView(entry);
            }
            finally { syncing = false; }
            if (chosen is not null) chosen.Text = picker.Selected?.AccessibleName ?? string.Empty;
            if (empty is not null)
            {
                empty.Text = picker.EmptyMessage ?? string.Empty;
                empty.IsVisible = picker.EmptyMessage is not null;
            }
        }

        picker.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LocationPickerViewModel.Selected) or nameof(LocationPickerViewModel.EmptyMessage))
                ShowSelection();
        };
        picker.LocationChosen += (_, row) => LocationChosen?.Invoke(row);
        search.Text = picker.SearchText;
        search.TextChanged += (_, _) => picker.SearchText = search.Text ?? string.Empty;
        search.AddHandler(KeyDownEvent, (_, e) => HandleKey(e), RoutingStrategies.Tunnel);
        list.AddHandler(KeyDownEvent, (_, e) => HandleKey(e), RoutingStrategies.Tunnel);
        // Typing while the list has focus goes to the search box.
        list.AddHandler(TextInputEvent, (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
            search.Text = (search.Text ?? string.Empty) + e.Text;
            search.CaretIndex = search.Text.Length;
            search.Focus();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        list.SelectionChanged += (_, _) =>
        {
            // A rebuild clears the list for a moment; the picker restores its own highlight afterwards.
            if (syncing || list.SelectedItem is null) return;
            if (list.SelectedItem is LocationPickerEntry { IsHeader: true } header)
            {
                picker.ToggleZone(header.Section);
                ShowSelection();
                return;
            }
            picker.Selected = list.SelectedItem as LocationPickerEntry;
        };
        list.DoubleTapped += (_, e) =>
        {
            if ((e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is LocationPickerEntry { IsLocation: true })
                picker.Choose();
        };
        list.AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is Button { Tag: LocationPickerEntry { Location: { } row } })
            {
                picker.ToggleFavorite(row.LocationId);
                e.Handled = true;
            }
        });
        ShowSelection();

        void HandleKey(KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Down: picker.Move(+1); e.Handled = true; break;
                case Key.Up: picker.Move(-1); e.Handled = true; break;
                case Key.Enter when picker.Selected is not null: picker.Choose(); e.Handled = true; break;
                case Key.Escape when !string.IsNullOrEmpty(search.Text): search.Text = string.Empty; e.Handled = true; break;
            }
        }
    }

    private void InitializeComponent() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);

    private void RefreshClick(object? sender, RoutedEventArgs e) => _handlers?.RefreshClick(sender, e);

    private void OpenFriarsClick(object? sender, RoutedEventArgs e) => _handlers?.OpenFriarsClick(sender, e);

    private void FriarRitualClick(object? sender, RoutedEventArgs e) => _handlers?.FriarRitualClick(sender, e);

    private void FriarBlessingClick(object? sender, RoutedEventArgs e) => _handlers?.FriarBlessingClick(sender, e);

    private void CombatRunClick(object? sender, RoutedEventArgs e) => _handlers?.CombatRunClick(sender, e);
}
