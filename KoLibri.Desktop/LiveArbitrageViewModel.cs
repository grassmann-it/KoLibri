using System.Collections.ObjectModel;
using KolArbitrage.Core.Accounts;
using KolArbitrage.Core.Integration;

namespace KoLibri.Desktop;

/// <summary>One arbitrage candidate: the item, the cheapest listing and the
/// spread against the median resale price.</summary>
public sealed record LiveArbitrageRow
{
    public required string ItemName { get; init; }
    public required long CheapestPrice { get; init; }
    public required long MedianPrice { get; init; }
    public required int ListingCount { get; init; }
    public required string SpreadText { get; init; }
    public required string MarginText { get; init; }
    public required bool IsProfitable { get; init; }
}

/// <summary>
/// The arbitrage table: live mall listings next to the median resale price
/// from pricegun, with the spread the operator can act on.
///
/// The table is read-only. A purchase needs the buy path, which is gated
/// behind the approval token.
/// </summary>
public sealed class LiveArbitrageViewModel : DesktopViewModel
{
    /// <summary>The items the table watches by default — the ones the daily
    /// routine trades in.</summary>
    public static IReadOnlyList<string> DefaultWatchList { get; } = new[]
    {
        "11-leaf clover",
        "infinite BACON machine",
        "BACON",
        "seal tooth",
        "hermit permit",
        "lucky surprise egg"
    };

    private readonly DesktopLiveDataProvider _provider;
    private readonly AccountRole _role;
    private readonly Func<string, Task<long?>> _medianLookup;
    private readonly KolArbitrage.Core.Data.KoLmafiaCatalog? _catalog;
    private ObservableCollection<string> _watchList = new(DefaultWatchList);

    public LiveArbitrageViewModel(
        AccountRole role = AccountRole.Main,
        DesktopLiveDataProvider? provider = null,
        Func<string, Task<long?>>? medianLookup = null,
        KolArbitrage.Core.Data.KoLmafiaCatalog? catalog = null)
    {
        _role = role;
        _provider = provider ?? new DesktopLiveDataProvider();
        _catalog = catalog;
        _medianLookup = medianLookup ?? DefaultMedianLookup;
    }

    /// <summary>
    /// The pricegun median for an item. Pricegun works on the numeric KoL
    /// item id, so the name has to be resolved through the catalog first.
    /// </summary>
    private async Task<long?> DefaultMedianLookup(string itemName)
    {
        var itemId = ResolveItemId(itemName);
        if (itemId is null) return null;
        return await _provider.LoadMedianPriceAsync(itemId).ConfigureAwait(false);
    }

    /// <summary>Resolves an item name to its KoL numeric id through the
    /// catalog. Case-insensitive, because KoLmafia's names are lower case
    /// while the shop displays them capitalised.</summary>
    private string? ResolveItemId(string itemName)
    {
        if (_catalog is null || string.IsNullOrWhiteSpace(itemName)) return null;
        var match = _catalog.Items.FirstOrDefault(i =>
            string.Equals(i.Name, itemName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(i.Name, itemName, StringComparison.Ordinal));
        return match?.Id;
    }

    public AccountRole Role => _role;

    public ObservableCollection<LiveArbitrageRow> Rows { get; } = new();

    /// <summary>The items the table watches.</summary>
    public ObservableCollection<string> WatchList => _watchList;

    public int TotalListings => Rows.Sum(r => r.ListingCount);

    public string SummaryText => Rows.Count == 0
        ? "No listings loaded."
        : $"{Rows.Count} items · {TotalListings} listings · {Rows.Count(r => r.IsProfitable)} profitable";

    /// <summary>Loads the live listings for the watch list.</summary>
    public async Task LoadAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        LoadState = DesktopLoadState.Loading;
        try
        {
            var names = _watchList.ToArray();
            var searches = await _provider
                .LoadMallListingsAsync(_role, names, cancellationToken)
                .ConfigureAwait(false);
            Rows.Clear();
            foreach (var search in searches)
            {
                var cheapest = search.Cheapest;
                if (cheapest is null) continue;
                var median = await _medianLookup(search.ItemName).ConfigureAwait(false);
                var spread = median is { } m ? m - cheapest.Price : 0;
                var margin = median is { } mm && mm > 0 ? spread / (decimal)mm : 0m;
                Rows.Add(new LiveArbitrageRow
                {
                    ItemName = search.ItemName,
                    CheapestPrice = cheapest.Price,
                    MedianPrice = median ?? 0,
                    ListingCount = search.Listings.Count,
                    SpreadText = median is null ? "—" : $"{spread:N0} Meat",
                    MarginText = median is null ? "—" : $"{margin * 100:N1} %",
                    IsProfitable = spread > 0
                });
            }
            LoadState = Rows.Count == 0 ? DesktopLoadState.Empty : DesktopLoadState.Ready;
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            LoadState = DesktopLoadState.Error;
        }
        finally
        {
            OnPropertyChanged(nameof(TotalListings));
            OnPropertyChanged(nameof(SummaryText));
        }
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
        => LoadAsync(forceRefresh: true, cancellationToken);
}
