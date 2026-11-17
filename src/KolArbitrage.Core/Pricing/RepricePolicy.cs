namespace KolArbitrage.Core.Pricing;

/// <summary>
/// Guardrails for the read-only shop repricing planner.
///
/// The policy deliberately contains no transport or commit switch. A caller
/// receives a preview and must pass the returned, expected-price-bound update
/// through an approval/mutation adapter before changing a shop listing.
/// </summary>
public record RepricePolicy
{
    /// <summary>The lowest price a KoL Mall listing may have.</summary>
    public decimal MinimumPrice { get; init; } = 100m;
    public decimal MinPrice { get => MinimumPrice; init => MinimumPrice = value; }
    public decimal MinimumPriceMeat { get => MinimumPrice; init => MinimumPrice = value; }

    /// <summary>
    /// Absolute tick used to get below an independent reference. KoL prices
    /// are integer Meat, therefore the planner rounds down to a whole unit.
    /// </summary>
    public decimal UndercutTick { get; init; } = 1m;
    public decimal UndercutAmount { get => UndercutTick; init => UndercutTick = value; }
    public decimal UndercutBy { get => UndercutTick; init => UndercutTick = value; }

    /// <summary>Relative undercut used when it is smaller than the tick.</summary>
    public decimal UndercutPercent { get; init; } = 0.005m;

    /// <summary>
    /// Historical prices above the independent Mall reference by this amount
    /// are reported as a premium. The premium is a review signal; it does not
    /// silently make a one-listing floor the target price.
    /// </summary>
    public decimal HistoricalPremiumThreshold { get; init; } = 0.10m;
    public decimal HistoricalPremiumPercent { get => HistoricalPremiumThreshold; init => HistoricalPremiumThreshold = value; }
    public decimal HistoricalPremiumTrigger { get => HistoricalPremiumThreshold; init => HistoricalPremiumThreshold = value; }
    public decimal HistoricalPremiumThresholdPercent { get => HistoricalPremiumThreshold; init => HistoricalPremiumThreshold = value; }
    public bool UseHistoricalPremium { get; init; } = true;
    public bool ConsiderHistoricalPremium { get => UseHistoricalPremium; init => UseHistoricalPremium = value; }
    public decimal HistoricalPremiumMarkupPercent { get; init; }

    /// <summary>Fixed KoL Mall listing fee, charged once per listing.</summary>
    public decimal ListingFee { get; init; } = 10m;
    public decimal ListingFeeMeat { get => ListingFee; init => ListingFee = value; }
    public decimal MallListingFee { get => ListingFee; init => ListingFee = value; }

    /// <summary>KoL's share of a successful Mall sale (one percent by default).</summary>
    public decimal SaleFeeRate { get; init; } = 0.01m;
    public decimal SaleCommissionRate { get => SaleFeeRate; init => SaleFeeRate = value; }
    public decimal SaleFeePercent { get => SaleFeeRate; init => SaleFeeRate = value; }
    public decimal SaleFee { get => SaleFeeRate; init => SaleFeeRate = value; }

    /// <summary>Optional fixed handling/packaging cost in the preview.</summary>
    public decimal PackagingCost { get; init; }
    public decimal HandlingCost { get => PackagingCost; init => PackagingCost = value; }

    /// <summary>
    /// Require an independent anchor when selecting a target. A single Mall
    /// floor is still retained in the result, but is not treated as a robust
    /// target while this guard is enabled.
    /// </summary>
    public bool RequireIndependentReference { get; init; } = true;

    /// <summary>
    /// Keep stale evidence visible in the preview. The planner remains
    /// read-only either way; this switch controls whether a stale row can be
    /// marked actionable.
    /// </summary>
    public bool AllowStaleMarket { get; init; } = true;
    public bool RequireFreshMarket { get => !AllowStaleMarket; init => AllowStaleMarket = !value; }

    /// <summary>
    /// Exclude a listing reported by the estimator as our own from the market
    /// target. MarketEstimate already removes matching own rows; this option
    /// controls the additional safety gate when no independent anchor remains.
    /// </summary>
    public bool ExcludeOwnStore { get; init; } = true;
    public bool ExcludeOwnListings { get => ExcludeOwnStore; init => ExcludeOwnStore = value; }
    public bool OwnStoreExclusion { get => ExcludeOwnStore; init => ExcludeOwnStore = value; }

    /// <summary>
    /// When true, a historical premium is a review signal and therefore does
    /// not become an automatically actionable mutation.
    /// </summary>
    public bool RequireReviewForHistoricalPremium { get; init; } = true;

    /// <summary>
    /// Do not emit a mutation when the target equals the current ask.
    /// The unchanged row remains in the preview with an AlreadyCompetitive
    /// reason code.
    /// </summary>
    public bool KeepAlreadyCompetitive { get; init; } = true;

    public void Validate()
    {
        if (MinimumPrice < 100m)
            throw new ArgumentOutOfRangeException(nameof(MinimumPrice), "KoL Mall prices must be at least 100 Meat.");
        if (UndercutTick < 0m)
            throw new ArgumentOutOfRangeException(nameof(UndercutTick));
        if (UndercutPercent < 0m || UndercutPercent >= 1m)
            throw new ArgumentOutOfRangeException(nameof(UndercutPercent));
        if (HistoricalPremiumThreshold < 0m)
            throw new ArgumentOutOfRangeException(nameof(HistoricalPremiumThreshold));
        if (HistoricalPremiumMarkupPercent < 0m || HistoricalPremiumMarkupPercent >= 1m)
            throw new ArgumentOutOfRangeException(nameof(HistoricalPremiumMarkupPercent));
        if (ListingFee < 0m)
            throw new ArgumentOutOfRangeException(nameof(ListingFee));
        if (SaleFeeRate < 0m || SaleFeeRate >= 1m)
            throw new ArgumentOutOfRangeException(nameof(SaleFeeRate));
        if (PackagingCost < 0m)
            throw new ArgumentOutOfRangeException(nameof(PackagingCost));
    }
}

/// <summary>Compatibility spelling for callers that name the policy robustly.</summary>
public sealed record RobustRepricePolicy : RepricePolicy;

/// <summary>Compatibility spelling used by options-oriented callers.</summary>
public sealed record RepricePolicyOptions : RepricePolicy;

/// <summary>
/// Reason codes are stable, machine-readable explanations attached to every
/// row in a reprice preview. Text rationale is intentionally separate so a UI
/// can localize it without changing decision logic.
/// </summary>
public enum RepriceReasonCode
{
    None = 0,
    Ready = 1,
    AlreadyCompetitive = 2,
    NoMarketReference = 3,
    NoMarketData = NoMarketReference,
    MissingIndependentReference = 4,
    InvalidListing = 5,
    ItemMismatch = 6,
    StaleMarket = 7,
    OwnStoreExcluded = 8,
    OwnListingExcluded = OwnStoreExcluded,
    OwnStoreContamination = 9,
    HistoricalPremium = 10,
    HistoricalPricePremium = HistoricalPremium,
    MinimumPriceApplied = 11,
    BelowMinimumPrice = MinimumPriceApplied,
    FeesApplied = 12,
    BelowFeeFloor = FeesApplied,
    FloorReference = 13,
    SecondReference = 14,
    FifthReference = 15,
    MedianReference = 16,
    WeightedMedianReference = 17,
    HistoricalReference = 18,
    PriceUnchanged = 19,
    NoChange = PriceUnchanged,
    ReviewRequired = 20,
    StaleReview = 21
}

/// <summary>
/// Short compatibility constants for integrations that use RepriceReason as
/// the type name while keeping the strongly typed <see cref="RepriceReasonCode"/>
/// values in the result.
/// </summary>
public static class RepriceReason
{
    public const RepriceReasonCode None = RepriceReasonCode.None;
    public const RepriceReasonCode Ready = RepriceReasonCode.Ready;
    public const RepriceReasonCode AlreadyCompetitive = RepriceReasonCode.AlreadyCompetitive;
    public const RepriceReasonCode NoMarketReference = RepriceReasonCode.NoMarketReference;
    public const RepriceReasonCode MissingIndependentReference = RepriceReasonCode.MissingIndependentReference;
    public const RepriceReasonCode OwnStoreExcluded = RepriceReasonCode.OwnStoreExcluded;
    public const RepriceReasonCode HistoricalPremium = RepriceReasonCode.HistoricalPremium;
    public const RepriceReasonCode MinimumPriceApplied = RepriceReasonCode.MinimumPriceApplied;
    public const RepriceReasonCode FeesApplied = RepriceReasonCode.FeesApplied;
    public const RepriceReasonCode StaleMarket = RepriceReasonCode.StaleMarket;
}

/// <summary>Lifecycle state of one read-only reprice recommendation.</summary>
public enum RepriceRecommendationStatus
{
    Ready,
    Keep,
    NoChange = Keep,
    Review,
    Hold
}

/// <summary>Action-oriented spelling for table/CLI integrations.</summary>
public enum RepriceAction
{
    Reprice,
    Keep,
    Review,
    Hold
}
