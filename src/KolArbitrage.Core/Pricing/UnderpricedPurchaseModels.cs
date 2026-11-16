namespace KolArbitrage.Core.Pricing;

/// <summary>
/// A single observed Mall offer. The planner deliberately keeps the seller
/// identity and observation time: a proposal must be tied to the exact offer
/// that was inspected and must not be reconstructed from a price-only scan.
/// </summary>
public record UnderpricedPurchaseListing
{
    public string ItemId { get; init; } = string.Empty;
    public string SellerId { get; init; } = string.Empty;
    private decimal _unitPrice;
    public decimal UnitPrice { get => _unitPrice; init => _unitPrice = value; }
    /// <summary>Price alias for integrations that use the Mall field name.</summary>
    public decimal Price { get => _unitPrice; init => _unitPrice = value; }
    public int Quantity { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public int? SellerPurchaseLimit { get; init; }
    public string? DisplayName { get; init; }

    public UnderpricedPurchaseListing() { }

    public UnderpricedPurchaseListing(
        string itemId,
        string sellerId,
        decimal unitPrice,
        int quantity,
        DateTimeOffset? observedAt = null,
        int? sellerPurchaseLimit = null,
        string? displayName = null)
    {
        ItemId = itemId;
        SellerId = sellerId;
        UnitPrice = unitPrice;
        Quantity = quantity;
        ObservedAt = observedAt;
        SellerPurchaseLimit = sellerPurchaseLimit;
        DisplayName = displayName;
    }

    /// <summary>Aliases used by adapters that call a Mall offer a listing.</summary>
    public string Seller { get => SellerId; init => SellerId = value; }
    public string? SellerName { get; init; }
}

/// <summary>
/// Conservative, independent resale reference for an item. Callers may pass
/// a value already computed by a Pricegun/Mall aggregator in
/// <see cref="ConservativeSellPrice"/>. The other values provide a useful
/// fallback when assembling a reference from separate read-only sources.
/// </summary>
public record RobustPriceReference
{
    public string? ItemId { get; init; }
    public decimal? ConservativeSellPrice { get; init; }
    public decimal? ConservativePrice { get => ConservativeSellPrice; init => ConservativeSellPrice = value; }
    public decimal? ExpectedPrice { get => ConservativeSellPrice; init => ConservativeSellPrice = value; }
    public decimal? ReferencePrice { get; init; }
    public decimal? MarketReferencePrice { get => ReferencePrice; init => ReferencePrice = value; }
    public decimal? PricegunMedian { get; init; }
    public decimal? PricegunReference { get => PricegunMedian; init => PricegunMedian = value; }
    public decimal? WeightedMedian { get; init; }
    public decimal? SecondLowestMallPrice { get; init; }
    public decimal? SecondPrice { get => SecondLowestMallPrice; init => SecondLowestMallPrice = value; }
    public decimal? HistoricalPrice { get; init; }
    public decimal? HistoricalMedian { get => HistoricalPrice; init => HistoricalPrice = value; }
    public decimal? ExpectedSellPrice { get; init; }
    public decimal? MarketPrice { get; init; }
    public DateTimeOffset? AsOf { get; init; }
    public bool IsStale { get; init; }
    public string? Source { get; init; }
    public decimal Confidence { get; init; } = 1m;

    /// <summary>Recent units sold in the period represented by this reference.</summary>
    public int RecentSalesQuantity { get; init; }
    public int RecentSales { get => RecentSalesQuantity; init => RecentSalesQuantity = value; }

    /// <summary>Observed market quantity in the same period.</summary>
    public int ObservedQuantity { get; init; }
    public int MarketQuantity { get => ObservedQuantity; init => ObservedQuantity = value; }

    /// <summary>Optional precomputed sell-through (0..1).</summary>
    public decimal? SellThroughRate { get; init; }
    public decimal? LiquidityScore { get; init; }

    /// <summary>Optional estimated units sold per day.</summary>
    public decimal? DailySales { get; init; }

    /// <summary>Optional expected days to sell; lower is more liquid.</summary>
    public decimal? EstimatedDaysToSell { get; init; }

    public RobustPriceReference() { }

    public RobustPriceReference(decimal conservativeSellPrice, DateTimeOffset? asOf = null)
    {
        ConservativeSellPrice = conservativeSellPrice;
        AsOf = asOf;
    }

    /// <summary>Returns the explicitly supplied or derived conservative value.</summary>
    public decimal? GetConservativePrice()
    {
        if (ConservativeSellPrice is > 0) return ConservativeSellPrice;
        if (ReferencePrice is > 0) return ReferencePrice;
        if (ExpectedSellPrice is > 0) return ExpectedSellPrice;

        // A minimum of independent observations avoids using a single cheap,
        // stale or self-contaminated listing as the resale expectation.
        var values = new[] { PricegunMedian, WeightedMedian, SecondLowestMallPrice, HistoricalPrice, MarketPrice }
            .Where(x => x is > 0)
            .Select(x => x!.Value)
            .ToArray();
        return values.Length == 0 ? null : values.Min();
    }

    /// <summary>Returns a 0..1 liquidity estimate where 1 is most liquid.</summary>
    public decimal GetLiquidityScore()
    {
        if (LiquidityScore is { } score) return Math.Clamp(score, 0m, 1m);
        if (SellThroughRate is { } rate) return Math.Clamp(rate, 0m, 1m);
        if (EstimatedDaysToSell is > 0)
            return Math.Clamp(1m / EstimatedDaysToSell.Value, 0m, 1m);
        if (ObservedQuantity > 0)
            return Math.Clamp((decimal)Math.Max(0, RecentSalesQuantity) / ObservedQuantity, 0m, 1m);
        if (DailySales is > 0) return Math.Clamp(DailySales.Value / 10m, 0m, 1m);
        return 0m;
    }
}

/// <summary>Facts supplied by an item catalog about whether a purchase can be traded.</summary>
public record UnderpricedItemTradeability
{
    public bool IsTradeable { get; init; } = true;
    public bool Tradeable { get => IsTradeable; init => IsTradeable = value; }
    public bool IsGiftable { get; init; } = true;
    public bool Giftable { get => IsGiftable; init => IsGiftable = value; }
    public bool IsAccountBound { get; init; }
    public bool AccountBound { get => IsAccountBound; init => IsAccountBound = value; }
    public bool CanPurchase { get; init; } = true;
    public bool CanSell { get; init; } = true;
    public string? Reason { get; init; }

    public UnderpricedItemTradeability() { }

    public UnderpricedItemTradeability(bool isTradeable, bool isAccountBound = false)
    {
        IsTradeable = isTradeable;
        IsAccountBound = isAccountBound;
    }

    public static UnderpricedItemTradeability TradeableFacts { get; } = new();
}

/// <summary>Budget, risk and per-item limits for a preview run.</summary>
public record UnderpricedPurchaseLimits
{
    /// <summary>Total Meat available for this run.</summary>
    public decimal Budget { get; init; } = decimal.MaxValue;
    public decimal AvailableBudget { get => Budget; init => Budget = value; }
    public decimal DailyBudget { get => Budget; init => Budget = value; }

    /// <summary>Meat that must remain untouched.</summary>
    public decimal Reserve { get; init; }
    public decimal MeatReserve { get => Reserve; init => Reserve = value; }

    public decimal PerItemBudget { get; init; } = decimal.MaxValue;
    public decimal MaxMeatPerItem { get => PerItemBudget; init => PerItemBudget = value; }
    public int MaxQuantityPerItem { get; init; } = int.MaxValue;
    public int MaxQuantity { get => MaxQuantityPerItem; init => MaxQuantityPerItem = value; }
    public int MaxQuantityPerListing { get; init; } = int.MaxValue;
    public int MaxQuantityPerPurchase { get => MaxQuantityPerListing; init => MaxQuantityPerListing = value; }
    public int MaxDistinctItems { get; init; } = int.MaxValue;
    public int MaxItems { get => MaxDistinctItems; init => MaxDistinctItems = value; }
    public int MaxProposals { get; init; } = int.MaxValue;

    /// <summary>Minimum net Meat required for an accepted proposal.</summary>
    public decimal MinimumAbsoluteProfit { get; init; }
    public decimal MinimumProfit { get => MinimumAbsoluteProfit; init => MinimumAbsoluteProfit = value; }

    /// <summary>Minimum net margin over acquisition cost (0.10 = 10%).</summary>
    public decimal MinimumMarginPercent { get; init; } = 0.10m;
    public decimal MinimumMargin { get => MinimumMarginPercent; init => MinimumMarginPercent = value; }
    public decimal MinMarginPercent { get => MinimumMarginPercent; init => MinimumMarginPercent = value; }

    /// <summary>Minimum absolute spread per unit before fees.</summary>
    public decimal MinimumAbsoluteSpreadPerUnit { get; init; }
    public decimal MinimumSpread { get => MinimumAbsoluteSpreadPerUnit; init => MinimumAbsoluteSpreadPerUnit = value; }

    public decimal SaleFeePercent { get; init; } = 0.01m;
    public decimal SaleFeeRate { get => SaleFeePercent; init => SaleFeePercent = value; }
    public decimal ListingFee { get; init; } = 10m;
    public decimal ListingFeeMeat { get => ListingFee; init => ListingFee = value; }
    public decimal OtherCostPerUnit { get; init; }
    public decimal OtherCost { get; init; }
    public decimal PackagingCost { get => OtherCost; init => OtherCost = value; }

    public TimeSpan MaximumReferenceAge { get; init; } = TimeSpan.FromHours(6);
    public decimal MinimumLiquidityScore { get; init; }
    public int MinimumRecentSalesQuantity { get; init; }
    public bool RequireLiquidityData { get; init; }

    /// <summary>
    /// Score weights. They are normalized automatically, allowing callers to
    /// emphasize margin, capital efficiency or liquidity.
    /// </summary>
    public decimal NetProfitWeight { get; init; } = 0.45m;
    public decimal CapitalEfficiencyWeight { get; init; } = 0.25m;
    public decimal LiquidityWeight { get; init; } = 0.30m;

    public void Validate()
    {
        if (Budget < 0) throw new ArgumentOutOfRangeException(nameof(Budget));
        if (Reserve < 0 || Reserve > Budget) throw new ArgumentOutOfRangeException(nameof(Reserve));
        if (PerItemBudget < 0) throw new ArgumentOutOfRangeException(nameof(PerItemBudget));
        if (MaxQuantityPerItem <= 0) throw new ArgumentOutOfRangeException(nameof(MaxQuantityPerItem));
        if (MaxQuantityPerListing <= 0) throw new ArgumentOutOfRangeException(nameof(MaxQuantityPerListing));
        if (MaxDistinctItems <= 0) throw new ArgumentOutOfRangeException(nameof(MaxDistinctItems));
        if (MaxProposals <= 0) throw new ArgumentOutOfRangeException(nameof(MaxProposals));
        if (MinimumAbsoluteProfit < 0) throw new ArgumentOutOfRangeException(nameof(MinimumAbsoluteProfit));
        if (MinimumMarginPercent < 0) throw new ArgumentOutOfRangeException(nameof(MinimumMarginPercent));
        if (MinimumAbsoluteSpreadPerUnit < 0) throw new ArgumentOutOfRangeException(nameof(MinimumAbsoluteSpreadPerUnit));
        if (SaleFeePercent < 0 || SaleFeePercent >= 1) throw new ArgumentOutOfRangeException(nameof(SaleFeePercent));
        if (ListingFee < 0 || OtherCostPerUnit < 0 || OtherCost < 0)
            throw new ArgumentOutOfRangeException(nameof(ListingFee));
        if (MaximumReferenceAge <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(MaximumReferenceAge));
        if (MinimumLiquidityScore is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(MinimumLiquidityScore));
        if (MinimumRecentSalesQuantity < 0) throw new ArgumentOutOfRangeException(nameof(MinimumRecentSalesQuantity));
        if (NetProfitWeight < 0 || CapitalEfficiencyWeight < 0 || LiquidityWeight < 0 ||
            NetProfitWeight + CapitalEfficiencyWeight + LiquidityWeight <= 0)
            throw new ArgumentException("At least one score weight must be positive.", nameof(NetProfitWeight));
    }
}

public enum UnderpricedPurchaseRejectionReason
{
    None,
    InvalidListing,
    InvalidPrice,
    InvalidQuantity,
    MissingReference,
    StaleReference,
    LowConfidenceReference,
    NonTradeable,
    AccountBound,
    CannotPurchase,
    CannotResell,
    MissingLiquidity,
    Illiquid,
    BelowMinimumSpread,
    BelowMinimumProfit,
    BelowMinimumMargin,
    BudgetExceeded,
    PerItemBudgetExceeded,
    QuantityLimitExceeded,
    DistinctItemLimitExceeded,
    ProposalLimitExceeded
}

public sealed record UnderpricedPurchaseProposal
{
    public required string ItemId { get; init; }
    public required string SellerId { get; init; }
    public string? DisplayName { get; init; }
    public decimal UnitPrice { get; init; }
    public int AvailableQuantity { get; init; }
    public int ProposedQuantity { get; init; }
    public decimal ReferencePrice { get; init; }
    public decimal GrossSaleValue { get; init; }
    public decimal SaleFees { get; init; }
    public decimal AcquisitionCost { get; init; }
    public decimal TotalCost { get; init; }
    public decimal NetProfit { get; init; }
    public decimal MarginPercent { get; init; }
    public decimal LiquidityScore { get; init; }
    public decimal Score { get; init; }
    public bool IsAccepted { get; init; }
    public bool IsPreviewOnly { get; init; } = true;
    public IReadOnlyList<UnderpricedPurchaseRejectionReason> RejectionReasons { get; init; } = [];
    public IReadOnlyList<string> Reasons { get; init; } = [];

    public bool Accepted => IsAccepted;
    public bool Rejected => !IsAccepted;
    public decimal CapitalRequired => TotalCost;
    public decimal ExpectedNetProfit => NetProfit;
    public decimal ReferenceSellPrice => ReferencePrice;
    public int Quantity => ProposedQuantity;
}

public sealed record UnderpricedPurchasePlan
{
    public required IReadOnlyList<UnderpricedPurchaseProposal> Proposals { get; init; }
    public IReadOnlyList<UnderpricedPurchaseProposal> AcceptedProposals =>
        Proposals.Where(x => x.IsAccepted).ToArray();
    public IReadOnlyList<UnderpricedPurchaseProposal> RejectedProposals =>
        Proposals.Where(x => !x.IsAccepted).ToArray();
    public decimal Budget { get; init; }
    public decimal ReservedBudget { get; init; }
    public decimal PlannedCapital { get; init; }
    public decimal PlannedNetProfit { get; init; }
    public bool IsPreviewOnly { get; init; } = true;
}

/// <summary>Convenient request shape for callers that keep all inputs together.</summary>
public sealed record UnderpricedPurchasePlanRequest
{
    public required IReadOnlyCollection<UnderpricedPurchaseListing> Listings { get; init; }
    public required IReadOnlyDictionary<string, RobustPriceReference> References { get; init; }
    public IReadOnlyDictionary<string, UnderpricedItemTradeability>? Tradeability { get; init; }
    public UnderpricedPurchaseLimits? Limits { get; init; }
}

// Names used by some adapters and by older prototypes. They retain one source
// of behaviour while making the public API straightforward to discover.
public sealed record UnderpricedReference : RobustPriceReference
{
    public UnderpricedReference() { }
    public UnderpricedReference(decimal price, DateTimeOffset? asOf = null) : base(price, asOf) { }
}
public sealed record ItemTradeability : UnderpricedItemTradeability
{
    public ItemTradeability() { }
    public ItemTradeability(bool tradeable, bool accountBound = false) : base(tradeable, accountBound) { }
}
public sealed record UnderpricedPurchasePolicy : UnderpricedPurchaseLimits { }
public sealed record UnderpricedPurchasePlannerOptions : UnderpricedPurchaseLimits { }
public sealed record RobustMarketReference : RobustPriceReference
{
    public RobustMarketReference() { }
    public RobustMarketReference(decimal price, DateTimeOffset? asOf = null) : base(price, asOf) { }
}
public record PurchaseListing : UnderpricedPurchaseListing
{
    public PurchaseListing() { }
    public PurchaseListing(string itemId, string sellerId, decimal price, int quantity,
        DateTimeOffset? observedAt = null) : base(itemId, sellerId, price, quantity, observedAt) { }
}
public record PurchaseCandidate : UnderpricedPurchaseListing
{
    public PurchaseCandidate() { }
    public PurchaseCandidate(string itemId, string sellerId, decimal price, int quantity,
        DateTimeOffset? observedAt = null) : base(itemId, sellerId, price, quantity, observedAt) { }
}
