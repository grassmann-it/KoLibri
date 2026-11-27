using System.Collections.ObjectModel;

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
/// One buy rule in the table: the item, the price above which it is not
/// bought, and the price it fetches again. The operator sets both — the
/// tool only applies them.
/// </summary>
public sealed record BuyRuleRow
{
    public required int ItemId { get; init; }
    public required string ItemName { get; init; }
    /// <summary>The highest price the item is bought at.</summary>
    public int MaximumPrice { get; init; }
    /// <summary>The price the item fetches again.</summary>
    public int ResalePrice { get; init; }
    /// <summary>The listings the rule wants to buy.</summary>
    public int CandidateCount { get; init; }
    /// <summary>The meat the rule would spend.</summary>
    public long PlannedCost { get; init; }
    /// <summary>The profit the rule expects.</summary>
    public long PlannedProfit { get; init; }
    /// <summary>The price ceiling and the resale price, for the table.</summary>
    public string CeilingText => $"{MaximumPrice:N0} Meat";
    /// <summary>The profit for the table.</summary>
    public string ProfitText => PlannedProfit > 0 ? $"{PlannedProfit:N0} Meat" : "—";
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
        "lucky surprise egg",
        "bag of foreign bribes"
    };

    private readonly DesktopLiveDataProvider _provider;
    private readonly AccountRole _role;
    private readonly Func<string, Task<long?>> _medianLookup;
    private readonly KoLmafiaCatalog? _catalog;
    private ObservableCollection<string> _watchList = new(DefaultWatchList);

    public LiveArbitrageViewModel(
        AccountRole role,
        DesktopLiveDataProvider provider,
        Func<string, Task<long?>>? medianLookup = null,
        KoLmafiaCatalog? catalog = null)
    {
        _role = role;
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
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
        return await _provider.LoadMedianPriceAsync(itemId);
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

    /// <summary>
    /// The buy rules. Each row is one item the operator trades: the item,
    /// the price above which it is not bought, and the price it fetches
    /// again. The ceilings are editable — the operator decides what the
    /// item is worth, the tool does not.
    /// </summary>
    public ObservableCollection<BuyRuleRow> Rules { get; } = new();

    /// <summary>The items the table watches.</summary>
    public ObservableCollection<string> WatchList => _watchList;

    public int TotalListings => Rows.Sum(r => r.ListingCount);

    public string SummaryText => Rows.Count == 0
        ? "No listings loaded."
        : $"{Rows.Count} items · {TotalListings} listings · {Rows.Count(r => r.IsProfitable)} profitable";

    /// <summary>The meat the rules would spend, and what they expect back.</summary>
    public string PlanText => Rules.Count == 0
        ? "No buy rules."
        : $"{Rules.Count} rules · {Rules.Sum(r => r.PlannedCost):N0} Meat planned · "
            + $"{Rules.Sum(r => r.PlannedProfit):N0} Meat expected";

    /// <summary>
    /// Loads the buy rules. The defaults are the operator's: the clover
    /// below 26.900, the bribes below 20.000.
    /// </summary>
    public void LoadRules()
    {
        Rules.Clear();
        foreach (var rule in BuyPlanner.DefaultRules)
        {
            Rules.Add(new BuyRuleRow
            {
                ItemId = rule.ItemId,
                ItemName = rule.ItemName,
                MaximumPrice = rule.MaximumPrice,
                ResalePrice = rule.ResalePrice
            });
        }
        OnPropertyChanged(nameof(PlanText));
    }

    /// <summary>Loads the live listings for the watch list.</summary>
    public async Task LoadAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        LoadState = DesktopLoadState.Loading;
        try
        {
            var names = _watchList.ToArray();
            var searches = await _provider
                .LoadMallListingsAsync(_role, names, cancellationToken, forceRefresh)
                ;
            Rows.Clear();
            foreach (var search in searches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (search.IsStale) continue;
                var cheapest = search.Cheapest;
                if (cheapest is null) continue;
                var median = await _medianLookup(search.ItemName);
                var spread = median is { } m ? m - cheapest.UnitPrice : 0;
                var margin = median is { } mm && mm > 0 ? spread / (decimal)mm : 0m;
                Rows.Add(new LiveArbitrageRow
                {
                    ItemName = search.ItemName,
                    CheapestPrice = cheapest.UnitPrice,
                    MedianPrice = median ?? 0,
                    ListingCount = search.Listings.Count,
                    SpreadText = median is null ? "—" : $"{spread:N0} Meat",
                    MarginText = median is null ? "—" : $"{margin * 100:N1} %",
                    IsProfitable = spread > 0
                });
            }
            await LoadRulePlansAsync(searches, cancellationToken);
            LoadState = Rows.Count == 0 ? DesktopLoadState.Empty : DesktopLoadState.Ready;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LoadState = DesktopLoadState.Empty;
            throw;
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
            OnPropertyChanged(nameof(PlanText));
        }
    }

    /// <summary>
    /// Applies the buy rules to the loaded listings, so the table shows
    /// what would be bought and what it would cost.
    /// </summary>
    private async Task LoadRulePlansAsync(
        IReadOnlyList<MallSearchResult> searches,
        CancellationToken cancellationToken)
    {
        if (Rules.Count == 0) LoadRules();
        // A status that cannot be read fresh leaves the rules at zero, as a stale one did in 0.4; the table stays.
        Status? status;
        try { status = await _provider.LoadStatusAsync(_role, cancellationToken: cancellationToken); }
        catch (KolibriException) { status = null; }
        var meat = status?.Meat ?? 0;
        foreach (var index in Enumerable.Range(0, Rules.Count).ToArray())
        {
            var row = Rules[index];
            var search = searches.FirstOrDefault(x =>
                string.Equals(x.ItemName, row.ItemName, StringComparison.OrdinalIgnoreCase));
            if (search is null || search.IsStale || status is null)
            {
                Rules[index] = row with { CandidateCount = 0, PlannedCost = 0, PlannedProfit = 0 };
                continue;
            }
            var rule = new BuyRule
            {
                ItemId = row.ItemId,
                ItemName = row.ItemName,
                MaximumPrice = row.MaximumPrice,
                ResalePrice = row.ResalePrice
            };
            var listings = search.Listings
                .Select(x => new MallListingRow
                {
                    StoreId = x.StoreId,
                    ItemId = row.ItemId,
                    StoreName = x.StoreName ?? string.Empty,
                    Stock = x.Quantity,
                    UnitPrice = (int)Math.Min(x.UnitPrice, int.MaxValue),
                    DailyLimit = x.LimitPerDay ?? 0
                })
                .ToArray();
            var plan = new BuyPlanner().Plan(rule, listings, meat);
            Rules[index] = row with
            {
                CandidateCount = plan.Candidates.Count,
                PlannedCost = plan.TotalCost,
                PlannedProfit = plan.TotalProfit
            };
        }
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
        => LoadAsync(forceRefresh: true, cancellationToken);
}
