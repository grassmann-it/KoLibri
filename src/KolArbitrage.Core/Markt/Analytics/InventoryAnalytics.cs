namespace KolArbitrage.Core.Markt;

/// <summary>Signals used to group an inventory or mall listing for review.</summary>
public enum InventoryCategory
{
    Unknown,
    HighPrice,
    SellsWell,
    Overpriced,
    Underpriced,
    Illiquid
}

public enum PricingAction
{
    Investigate,
    Hold,
    RepriceDown,
    RepriceUp,
    BuyCandidate
}

/// <summary>
/// A read-only snapshot assembled from the account inventory, own shop and
/// cached mall observations. All prices are integer Meat in KoL, represented
/// as decimal here so the same model can be used by other sources.
/// </summary>
public sealed record InventoryItemSnapshot
{
    public required string ItemId { get; init; }
    public required string DisplayName { get; init; }
    public int OwnedQuantity { get; init; }
    public decimal? CostBasis { get; init; }
    public decimal? CurrentListingPrice { get; init; }
    public decimal? MarketReferencePrice { get; init; }
    public decimal? LowestMarketPrice { get; init; }
    public int SoldQuantityLast7Days { get; init; }
    public int ObservedQuantity { get; init; }
    public DateTimeOffset AsOf { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record InventoryCategorization
{
    public required string ItemId { get; init; }
    public required string DisplayName { get; init; }
    public required IReadOnlySet<InventoryCategory> Categories { get; init; }
    public required PricingAction SuggestedAction { get; init; }
    public required int Score { get; init; }
    public decimal? ReferencePrice { get; init; }
    public decimal? CurrentPrice { get; init; }
    public decimal? PriceDeltaPercent { get; init; }
    public decimal SellThroughRate7Days { get; init; }
    public required string Rationale { get; init; }

    public bool Has(InventoryCategory category) => Categories.Contains(category);
}

public sealed record InventoryAnalyticsOptions
{
    /// <summary>Absolute threshold for the high-price tag in Meat.</summary>
    public decimal HighPriceThreshold { get; init; } = 10_000m;

    /// <summary>How far above the reference price a listing must be to be overpriced.</summary>
    public decimal OverpricedThreshold { get; init; } = 0.10m;

    /// <summary>How far below the reference price a listing must be to be underpriced.</summary>
    public decimal UnderpricedThreshold { get; init; } = 0.10m;

    /// <summary>Minimum sold units in seven days for a strong sells-well signal.</summary>
    public int MinimumSoldUnitsForFastSales { get; init; } = 3;

    /// <summary>Sell-through rate over seven days for a strong sells-well signal.</summary>
    public decimal MinimumSellThroughRate { get; init; } = 0.25m;

    /// <summary>Below this rate an item is considered illiquid.</summary>
    public decimal IlliquidSellThroughRate { get; init; } = 0.05m;

    public void Validate()
    {
        if (HighPriceThreshold < 0) throw new ArgumentOutOfRangeException(nameof(HighPriceThreshold));
        if (OverpricedThreshold < 0) throw new ArgumentOutOfRangeException(nameof(OverpricedThreshold));
        if (UnderpricedThreshold < 0) throw new ArgumentOutOfRangeException(nameof(UnderpricedThreshold));
        if (MinimumSoldUnitsForFastSales < 0) throw new ArgumentOutOfRangeException(nameof(MinimumSoldUnitsForFastSales));
        if (MinimumSellThroughRate < 0 || MinimumSellThroughRate > 1)
            throw new ArgumentOutOfRangeException(nameof(MinimumSellThroughRate));
        if (IlliquidSellThroughRate < 0 || IlliquidSellThroughRate > 1)
            throw new ArgumentOutOfRangeException(nameof(IlliquidSellThroughRate));
    }
}

/// <summary>
/// Converts read-only account and market snapshots into explainable review
/// categories. It never places an order or changes a listing.
/// </summary>
public sealed class InventoryCategorizer
{
    private readonly InventoryAnalyticsOptions _options;

    public InventoryCategorizer(InventoryAnalyticsOptions? options = null)
    {
        _options = options ?? new InventoryAnalyticsOptions();
        _options.Validate();
    }

    public InventoryCategorization Analyze(InventoryItemSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(snapshot.ItemId)) throw new ArgumentException("Item id is required.", nameof(snapshot));
        if (string.IsNullOrWhiteSpace(snapshot.DisplayName)) throw new ArgumentException("Display name is required.", nameof(snapshot));

        var reference = snapshot.MarketReferencePrice ?? snapshot.LowestMarketPrice;
        var current = snapshot.CurrentListingPrice;
        var categories = new HashSet<InventoryCategory>();
        var reasons = new List<string>();
        var sold = Math.Max(0, snapshot.SoldQuantityLast7Days);
        var observed = Math.Max(0, snapshot.ObservedQuantity);
        var denominator = Math.Max(1, observed == 0 ? snapshot.OwnedQuantity : observed);
        var sellThrough = Math.Clamp((decimal)sold / denominator, 0m, 1m);

        var benchmark = current ?? reference;
        if (benchmark is not null && benchmark >= _options.HighPriceThreshold)
        {
            categories.Add(InventoryCategory.HighPrice);
            reasons.Add($"Preisniveau ab {benchmark:0} Meat.");
        }

        decimal? delta = null;
        if (current is not null && reference is > 0)
        {
            delta = (current.Value - reference.Value) / reference.Value;
            if (delta >= _options.OverpricedThreshold)
            {
                categories.Add(InventoryCategory.Overpriced);
                reasons.Add($"{delta.Value:P0} über dem Referenzpreis.");
            }
            else if (delta <= -_options.UnderpricedThreshold)
            {
                categories.Add(InventoryCategory.Underpriced);
                reasons.Add($"{Math.Abs(delta.Value):P0} unter dem Referenzpreis.");
            }
        }

        if (sold >= _options.MinimumSoldUnitsForFastSales || sellThrough >= _options.MinimumSellThroughRate)
        {
            categories.Add(InventoryCategory.SellsWell);
            reasons.Add($"{sold} Verkäufe in 7 Tagen ({sellThrough:P0} Sell-through).");
        }
        else if (observed > 0 && sellThrough <= _options.IlliquidSellThroughRate)
        {
            categories.Add(InventoryCategory.Illiquid);
            reasons.Add($"Nur {sellThrough:P0} Sell-through in 7 Tagen.");
        }

        if (reference is null)
            reasons.Add("Keine belastbare Markt-Referenz im Cache.");
        if (current is null)
            reasons.Add("Kein eigener Listing-Preis vorhanden.");

        var action = ChooseAction(categories, reference, current, snapshot.CostBasis);
        var score = Score(categories, sellThrough, reference is not null, current is not null);
        if (reasons.Count == 0) reasons.Add("Noch keine ausreichenden Daten für eine Einstufung.");

        return new InventoryCategorization
        {
            ItemId = snapshot.ItemId.Trim(),
            DisplayName = snapshot.DisplayName.Trim(),
            Categories = categories,
            SuggestedAction = action,
            Score = score,
            ReferencePrice = reference,
            CurrentPrice = current,
            PriceDeltaPercent = delta,
            SellThroughRate7Days = sellThrough,
            Rationale = string.Join(" ", reasons)
        };
    }

    public IReadOnlyList<InventoryCategorization> AnalyzeMany(IEnumerable<InventoryItemSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        return snapshots.Select(Analyze)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static PricingAction ChooseAction(
        IReadOnlySet<InventoryCategory> categories,
        decimal? reference,
        decimal? current,
        decimal? costBasis)
    {
        if (categories.Contains(InventoryCategory.Underpriced) && reference is not null)
            return costBasis is null || reference >= costBasis ? PricingAction.BuyCandidate : PricingAction.Hold;
        if (categories.Contains(InventoryCategory.Overpriced)) return PricingAction.RepriceDown;
        if (categories.Contains(InventoryCategory.SellsWell) && current is not null) return PricingAction.Hold;
        if (categories.Contains(InventoryCategory.Illiquid)) return PricingAction.Investigate;
        return PricingAction.Hold;
    }

    private static int Score(
        IReadOnlySet<InventoryCategory> categories,
        decimal sellThrough,
        bool hasReference,
        bool hasCurrent)
    {
        var score = 0;
        if (categories.Contains(InventoryCategory.Underpriced)) score += 40;
        if (categories.Contains(InventoryCategory.SellsWell)) score += 35;
        if (categories.Contains(InventoryCategory.HighPrice)) score += 10;
        if (categories.Contains(InventoryCategory.Overpriced)) score -= 25;
        if (categories.Contains(InventoryCategory.Illiquid)) score -= 20;
        if (!hasReference) score -= 20;
        if (!hasCurrent) score -= 5;
        score += (int)Math.Round(Math.Clamp(sellThrough, 0m, 1m) * 20m);
        return Math.Clamp(score, 0, 100);
    }
}
