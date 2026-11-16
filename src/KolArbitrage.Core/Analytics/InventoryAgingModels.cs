namespace KolArbitrage.Core.Analytics;

/// <summary>Age bands used by the inventory planner.</summary>
public enum InventoryAgeBucket
{
    Unknown = 0,
    Fresh = 1,
    Aging = 2,
    Stale = 3,
    Critical = 4,

    // Friendly aliases for callers that use the terms from their reports.
    New = Fresh,
    Old = Stale,
    Ancient = Critical,
    VeryOld = Critical,
    Mature = Aging
}

/// <summary>Read-only action suggested for one inventory line.</summary>
public enum InventoryAction
{
    Hold = 0,
    Reprice = 1,
    RepriceDown = Reprice,
    Rotate = 2,
    Craft = 3,
    Liquidate = 4,
    RotateOne = Rotate
}

/// <summary>
/// A normalized inventory line used by the aging and excess planners. The
/// optional fields let the same model be built from an account snapshot, a
/// shop export, or a local lot ledger.
/// </summary>
public sealed record InventoryAgingItem(string ItemId, string DisplayName, int Quantity)
{
    /// <summary>When this lot was acquired. Required for a known age.</summary>
    public DateTimeOffset? AcquiredAt { get; init; }

    /// <summary>Fallback for systems that only retain a first-seen timestamp.</summary>
    public DateTimeOffset? FirstSeenAt { get; init; }

    /// <summary>Timestamp of the inventory observation.</summary>
    public DateTimeOffset? ObservedAt { get; init; }
    public DateTimeOffset? AsOf => ObservedAt;

    /// <summary>Optional precomputed age when the source has no acquisition date.</summary>
    public decimal? AgeDays { get; init; }

    public decimal? CostBasis { get; init; }
    public decimal? CurrentListingPrice { get; init; }
    public decimal? MarketReferencePrice { get; init; }

    /// <summary>Reserve that should remain after an excess action.</summary>
    public int ReserveQuantity { get; init; }

    /// <summary>Whether a configured craft path can consume this excess.</summary>
    public bool Craftable { get; init; }

    public bool CanCraft => Craftable;

    /// <summary>Expected net value per unit when the item is crafted.</summary>
    public decimal? CraftValue { get; init; }

    public decimal? CraftingValue => CraftValue;

    /// <summary>Set false when this item cannot be split into a rotation listing.</summary>
    public bool CanRotate { get; init; } = true;

    // Aliases make adapting existing account snapshots explicit and keep
    // reports readable without duplicating the underlying values.
    public decimal? CurrentPrice => CurrentListingPrice;
    public decimal? MarketPrice => MarketReferencePrice;
}

/// <summary>Sales-derived throughput for one inventory line.</summary>
public sealed record InventoryVelocity
{
    public required string ItemId { get; init; }
    public required string DisplayName { get; init; }
    public int InventoryQuantity { get; init; }
    public TimeSpan LookbackWindow { get; init; }
    public DateTimeOffset WindowStart { get; init; }
    public DateTimeOffset WindowEnd { get; init; }
    public int SoldQuantity { get; init; }
    public int SalesCount { get; init; }
    public decimal UnitsPerDay { get; init; }

    /// <summary>
    /// Inventory quantity divided by units per day. Null means no observed
    /// sales, which is an unbounded/unknown supply rather than zero days.
    /// </summary>
    public decimal? DaysOfSupply { get; init; }

    public decimal? DaysSupply => DaysOfSupply;
    public decimal AverageDailySales => UnitsPerDay;
    public decimal DailyVelocity => UnitsPerDay;
    public int SoldUnits => SoldQuantity;
    public bool HasSales => SoldQuantity > 0 && UnitsPerDay > 0m;
    public bool IsZeroVelocity => UnitsPerDay <= 0m;

    /// <summary>Sold units divided by opening inventory plus sold units.</summary>
    public decimal SellThroughRate { get; init; }
}

/// <summary>Combined age, velocity and excess assessment.</summary>
public sealed record InventoryAgingResult
{
    public required string ItemId { get; init; }
    public required string DisplayName { get; init; }
    public int Quantity { get; init; }
    public DateTimeOffset GeneratedAt { get; init; }
    public TimeSpan? Age { get; init; }
    public decimal? AgeDays { get; init; }
    public InventoryAgeBucket AgeBucket { get; init; }
    public required InventoryVelocity Velocity { get; init; }
    public int TargetQuantity { get; init; }
    public int ExcessQuantity { get; init; }
    public int ExcessUnits => ExcessQuantity;
    public int TargetUnits => TargetQuantity;
    public bool IsExcess => ExcessQuantity > 0;
    public decimal? DaysOfSupply => Velocity.DaysOfSupply;
    public InventoryAction RecommendedAction { get; init; }
    public InventoryAction Action => RecommendedAction;
    public InventoryAction Recommendation => RecommendedAction;
    public int RecommendedQuantity { get; init; }
    public decimal? RecommendedPrice { get; init; }
    public decimal? CostBasis { get; init; }
    public decimal? CurrentListingPrice { get; init; }
    public decimal? MarketReferencePrice { get; init; }
    public required IReadOnlyList<InventoryAction> SuggestedActions { get; init; }
    public required string Rationale { get; init; }
}

/// <summary>Thresholds used by <see cref="InventoryAgingPlanner"/>.</summary>
public sealed record InventoryAgingOptions
{
    public TimeSpan LookbackWindow { get; init; } = TimeSpan.FromDays(30);
    public TimeSpan SalesWindow
    {
        get => LookbackWindow;
        init => LookbackWindow = value;
    }
    public TimeSpan FreshAge { get; init; } = TimeSpan.FromDays(7);
    public TimeSpan AgingAge { get; init; } = TimeSpan.FromDays(30);
    public TimeSpan StaleAge { get; init; } = TimeSpan.FromDays(90);

    /// <summary>Stock beyond this many days of demand is excess.</summary>
    public decimal TargetDaysOfSupply { get; init; } = 30m;

    /// <summary>Positive-throughput stock at or above this supply may rotate.</summary>
    public decimal RotateDaysOfSupply { get; init; } = 60m;

    /// <summary>Very old/slow stock is liquidated when it reaches this supply.</summary>
    public decimal LiquidateDaysOfSupply { get; init; } = 180m;

    /// <summary>Current listings above this fraction of market are repriced.</summary>
    public decimal RepriceMarkupThreshold { get; init; } = 0.10m;

    public void Validate()
    {
        if (LookbackWindow <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(LookbackWindow));
        if (FreshAge < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(FreshAge));
        if (AgingAge < FreshAge) throw new ArgumentException("AgingAge must be at least FreshAge.", nameof(AgingAge));
        if (StaleAge < AgingAge) throw new ArgumentException("StaleAge must be at least AgingAge.", nameof(StaleAge));
        if (TargetDaysOfSupply < 0) throw new ArgumentOutOfRangeException(nameof(TargetDaysOfSupply));
        if (RotateDaysOfSupply < TargetDaysOfSupply)
            throw new ArgumentException("RotateDaysOfSupply must be at least TargetDaysOfSupply.", nameof(RotateDaysOfSupply));
        if (LiquidateDaysOfSupply < RotateDaysOfSupply)
            throw new ArgumentException("LiquidateDaysOfSupply must be at least RotateDaysOfSupply.", nameof(LiquidateDaysOfSupply));
        if (RepriceMarkupThreshold < 0) throw new ArgumentOutOfRangeException(nameof(RepriceMarkupThreshold));
    }
}

/// <summary>A complete, read-only inventory review generated at one timestamp.</summary>
public sealed record InventoryExcessPlan(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<InventoryAgingResult> Recommendations)
{
    public IReadOnlyList<InventoryAgingResult> Items => Recommendations;
    public IReadOnlyList<InventoryAgingResult> Results => Recommendations;
    public IReadOnlyList<InventoryAgingResult> PlanItems => Recommendations;
    public IReadOnlyList<InventoryAgingResult> Excess => Recommendations.Where(x => x.IsExcess).ToArray();
    public IReadOnlyDictionary<InventoryAgeBucket, int> AgeBucketCounts =>
        Recommendations.GroupBy(x => x.AgeBucket).ToDictionary(group => group.Key, group => group.Count());
    public bool HasRecommendations => Recommendations.Count > 0;
}
