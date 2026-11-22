using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using KoLibri.Desktop.ContextMenus;

namespace KoLibri.Desktop;

/// <summary>
/// Mall search first, own shop as a lazily loaded sub-section.
/// The host calls <see cref="Attach"/> whenever the account role (and so the
/// view models) changes, <see cref="FocusSearch"/> when the tab opens and
/// <see cref="RefreshAsync"/> for F5.
/// </summary>
public partial class MallSearchView : UserControl
{
    private MallSearchViewModel? _search;
    private LiveMallViewModel? _own;
    private bool _ownLoaded;

    public MallSearchView() => InitializeComponent();

    private async void ReconcileClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_search is not null) await _search.ReconcileUnclearAsync();
    }

    public void Attach(MallSearchViewModel search, LiveMallViewModel own)
    {
        if (_own is not null) _own.PropertyChanged -= OwnChanged;
        _search = search;
        _own = own;
        _ownLoaded = false;
        DataContext = search;
        search.RefreshUnclear();
        OwnShopRows.ItemsSource = own.Rows;
        OwnShopSummary.Text = "Open this section to load your shop.";
        own.PropertyChanged += OwnChanged;
        OwnShopExpander.IsExpanded = false;
    }

    /// <summary>Right-click and keyboard menu of the offer list.</summary>
    public RowContextMenuController<MallOfferRow>? OfferMenu { get; private set; }

    /// <summary>Adds the row context menu; the shell calls it once.</summary>
    public void AttachMenu(IShellHost host)
        => OfferMenu = RowContextMenuController<MallOfferRow>.Attach(this.FindControl<ListBox>("OfferList")!,
            row => new MallOfferContextMenu(host,
                    () => _search is null ? Task.CompletedTask : _search.RequestBuyAsync(row),
                    () => _search is null ? Task.CompletedTask : _search.SearchItemAsync(row.ItemId, row.ItemName))
                .Build(new OfferMenuTarget(row.ItemId, row.ItemName, row.ShopName, row.UnitPrice, row.CanBuy)
                {
                    ShopId = row.ShopId, SellerName = row.SellerName
                }));

    public void FocusSearch() => Dispatcher.UIThread.Post(() =>
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }, DispatcherPriority.Input);

    /// <summary>F5: refreshes the active search, and the own shop when it is open.</summary>
    public async Task RefreshAsync()
    {
        if (_search is null) return;
        if (_search.CanRefresh) await _search.RefreshAsync();
        if (OwnShopExpander.IsExpanded && _own is not null) await _own.RefreshAsync();
    }

    private void OwnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => Dispatcher.UIThread.Post(() => { if (_own is not null) OwnShopSummary.Text = _own.Summary; });

    private async void SearchClick(object? sender, RoutedEventArgs e)
    {
        if (_search is not null) await _search.SearchAsync();
    }

    private async void SearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _search is null) return;
        e.Handled = true;
        await _search.SearchAsync();
    }

    private async void CloverClick(object? sender, RoutedEventArgs e)
    {
        if (_search is not null) await _search.SearchCloverAsync();
    }

    private void SortClick(object? sender, RoutedEventArgs e)
    {
        if (_search is not null && sender is Button { Tag: string tag } && Enum.TryParse<MallOfferSort>(tag, out var column))
            _search.SortBy(column);
    }

    private void CancelClick(object? sender, RoutedEventArgs e) => _search?.Cancel();

    private async void ChoiceClick(object? sender, RoutedEventArgs e) => await PickSelectedAsync();
    private async void ChoiceActivated(object? sender, TappedEventArgs e) => await PickSelectedAsync();

    private async void ChoiceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await PickSelectedAsync();
    }

    private async Task PickSelectedAsync()
    {
        if (_search is not null && ChoiceList.SelectedItem is MallItemChoice choice)
            await _search.PickItemAsync(choice);
    }

    private async void BuyClick(object? sender, RoutedEventArgs e)
    {
        if (_search is not null && (sender as Control)?.DataContext is MallOfferRow row)
            await _search.RequestBuyAsync(row);
    }

    private async void OwnShopExpanded(object? sender, RoutedEventArgs e)
    {
        if (_ownLoaded || _own is null) return;
        _ownLoaded = true;
        await _own.LoadAsync();
    }

    private async void OwnShopReloadClick(object? sender, RoutedEventArgs e)
    {
        if (_own is null) return;
        _ownLoaded = true;
        await _own.RefreshAsync();
    }

    private async void OwnShopDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_search is null || OwnShopRows.SelectedItem is not MallStoreRow row) return;
        _search.SearchText = row.ItemName;
        await _search.SearchAsync();
    }
}
