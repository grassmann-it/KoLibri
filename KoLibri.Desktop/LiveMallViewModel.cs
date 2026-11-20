using System.Collections.ObjectModel;
using KolArbitrage.Core.Accounts;
using KolArbitrage.Core.Integration;

namespace KoLibri.Desktop;

/// <summary>One own-store listing with the numbers the operator acts on.</summary>
public sealed record MallStoreRow
{
    public required string ItemId { get; init; }
    public required string ItemName { get; init; }
    public required long Price { get; init; }
    public required int Quantity { get; init; }
    public required int? PurchaseLimit { get; init; }
    public required string PriceText { get; init; }
    public required string QuantityText { get; init; }
    public required string LimitText { get; init; }
    /// <summary>The cheapest mall listing for the same item, when there is
    /// one — the number that says whether the asking price is competitive.</summary>
    public long? CheapestMarketPrice { get; init; }
    public string MarketText => CheapestMarketPrice is { } price
        ? $"market {price:N0}"
        : "not listed";
}

/// <summary>
/// The mall screen: the account's own listings with the market price next
/// to them. KoLmafia shows the same pairing — your price and the cheapest
/// offer for the same item — because the difference is what you can still
/// win.
///
/// The panel is read-only. Repricing a listing goes through the approval
/// flow, not through this view.
/// </summary>
public sealed class LiveMallViewModel : DesktopViewModel
{
    private readonly DesktopLiveDataProvider _provider;
    private readonly AccountRole _role;
    private readonly KolArbitrage.Core.Data.KoLmafiaCatalog? _catalog;

    public LiveMallViewModel(
        AccountRole role = AccountRole.Mallbot,
        DesktopLiveDataProvider? provider = null,
        KolArbitrage.Core.Data.KoLmafiaCatalog? catalog = null)
    {
        _role = role;
        _provider = provider ?? new DesktopLiveDataProvider();
        _catalog = catalog;
    }

    public AccountRole Role => _role;

    /// <summary>How many distinct items get a market price lookup. Each
    /// lookup is one request to KoL, so the list stays small.</summary>
    public const int MarketPriceLookups = 25;

    public ObservableCollection<MallStoreRow> Rows { get; } = new();

    public string Summary { get; private set; } = "—";

    public int ListingCount => Rows.Count;

    public long TotalOffered => Rows.Sum(r => r.Price * r.Quantity);

    /// <summary>Loads the own store and the market prices for the listed
    /// items.</summary>
    public async Task LoadAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        LoadState = DesktopLoadState.Loading;
        try
        {
            var listings = await _provider
                .LoadOwnShopAsync(_role, cancellationToken).ConfigureAwait(false);
            // One mall search per distinct item — the own store can hold
            // hundreds of listings, and a search per listing would hammer
            // KoL. Only the first few items get a market price; the rest
            // show without one.
            var names = listings
                .Select(l => string.IsNullOrWhiteSpace(l.DisplayName) ? l.ItemId : l.DisplayName!)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MarketPriceLookups)
                .ToArray();
            var searches = await _provider
                .LoadMallListingsAsync(_role, names, cancellationToken).ConfigureAwait(false);
            var market = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var search in searches)
            {
                var cheapest = search.Cheapest;
                if (cheapest is not null) market[search.ItemName] = cheapest.Price;
            }

            Rows.Clear();
            foreach (var listing in listings)
            {
                var name = string.IsNullOrWhiteSpace(listing.DisplayName)
                    ? listing.ItemId
                    : listing.DisplayName!;
                Rows.Add(new MallStoreRow
                {
                    ItemId = listing.ItemId,
                    ItemName = name,
                    Price = listing.Price,
                    Quantity = listing.Quantity,
                    PurchaseLimit = listing.PurchaseLimit,
                    PriceText = $"{listing.Price:N0} Meat",
                    QuantityText = listing.Quantity == 1 ? "1 pc" : $"{listing.Quantity:N0} pcs",
                    LimitText = listing.PurchaseLimit is { } limit
                        ? $"{limit} / day"
                        : "—",
                    CheapestMarketPrice = market.TryGetValue(name, out var marketPrice)
                        ? marketPrice
                        : null
                });
            }

            Summary = Rows.Count == 0
                ? "No own listings."
                : $"{Rows.Count} listings · {TotalOffered:N0} Meat offered";
            LoadState = Rows.Count == 0 ? DesktopLoadState.Empty : DesktopLoadState.Ready;
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            LoadState = DesktopLoadState.Error;
        }
        finally
        {
            OnPropertyChanged(nameof(ListingCount));
            OnPropertyChanged(nameof(TotalOffered));
        }
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
        => LoadAsync(forceRefresh: true, cancellationToken);
}
