namespace KolArbitrage.Core.Pricing;

public sealed record PriceSummary(
    string ItemId,
    decimal? LowestBuyPrice,
    decimal? LowestSellPrice,
    decimal? WeightedMedianSellPrice,
    decimal? WeightedMedianBuyPrice,
    int QuoteCount,
    DateTimeOffset AsOf,
    bool IsStale);

public sealed record PricingRecommendation(
    string ItemId,
    decimal? AcquisitionCost,
    decimal? RecommendedBuyPrice,
    decimal? RecommendedSellPrice,
    decimal? MarketReferencePrice,
    decimal? ExpectedProfit,
    bool IsUnderpriced,
    bool IsProfitable,
    bool IsStale,
    decimal Confidence,
    IReadOnlyCollection<PriceObservation> SupportingQuotes,
    string Rationale);
