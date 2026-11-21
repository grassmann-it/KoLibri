
namespace KolArbitrage.Core.Markt;

/// <summary>Options for <see cref="MarketPriceEstimator"/>.</summary>
public record MarketPriceEstimatorOptions
{
    /// <summary>Number of cheapest independent Mall listings considered.</summary>
    public int TopN { get; init; } = 10;
    public int TopListings { get => TopN; init => TopN = value; }
    public int TopK { get => TopN; init => TopN = value; }
    public int TopCount { get => TopN; init => TopN = value; }
    /// <summary>Maximum age of a source before its estimate is marked stale.</summary>
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan FreshnessWindow { get => MaxAge; init => MaxAge = value; }
    public TimeSpan MaxSnapshotAge { get => MaxAge; init => MaxAge = value; }
    /// <summary>Weights used when fresh Mall and Pricegun values are blended.</summary>
    public decimal MallWeight { get; init; } = 0.5m;
    public decimal PricegunWeight { get; init; } = 0.5m;
    /// <summary>KoL charges 10 Meat for a listing and keeps one percent on sale.</summary>
    public decimal ListingFee { get; init; } = 10m;
    public decimal ListingFeeMeat { get => ListingFee; init => ListingFee = value; }
    public decimal MallListingFee { get => ListingFee; init => ListingFee = value; }
    public decimal SaleFeeRate { get; init; } = 0.01m;
    public decimal SaleCommissionRate { get => SaleFeeRate; init => SaleFeeRate = value; }
    public decimal SaleFeePercent { get => SaleFeeRate; init => SaleFeeRate = value; }
    public decimal PackagingCost { get; init; }
    public IReadOnlyCollection<string> OwnStoreIds { get; init; } = Array.Empty<string>();
    public IReadOnlyCollection<string> OwnStoreNames { get; init; } = Array.Empty<string>();
    /// <summary>
    /// Exclude matching item/price rows when a manage-store read has no store id.
    /// This conservative default prevents our listing becoming the market floor.
    /// </summary>
    public bool ExcludeOwnListingsByPrice { get; init; } = true;

    public void Validate()
    {
        if (TopN <= 0) throw new ArgumentOutOfRangeException(nameof(TopN));
        if (MaxAge <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(MaxAge));
        if (MallWeight < 0 || PricegunWeight < 0 || MallWeight + PricegunWeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(MallWeight), "At least one source weight must be positive.");
        if (ListingFee < 0) throw new ArgumentOutOfRangeException(nameof(ListingFee));
        if (SaleFeeRate < 0 || SaleFeeRate >= 1) throw new ArgumentOutOfRangeException(nameof(SaleFeeRate));
        if (PackagingCost < 0) throw new ArgumentOutOfRangeException(nameof(PackagingCost));
    }
}

/// <summary>Compatibility name for callers that omit the "Price" qualifier.</summary>
public sealed record MarketEstimatorOptions : MarketPriceEstimatorOptions;

/// <summary>
/// A robust, read-only market estimate. Mall values are calculated only from
/// the cheapest independent listings; Pricegun values use transaction history.
/// </summary>
public sealed class MarketEstimate
{
    internal MarketEstimate(
        string itemId,
        IReadOnlyList<MallListing> topListings,
        int topN,
        decimal? lowest,
        decimal? second,
        decimal? fifth,
        decimal? mallMedian,
        decimal? mallWeightedMedian,
        long cumulativeTopN,
        long cumulativeThroughSecond,
        long cumulativeThroughFifth,
        decimal? pricegunMedian,
        decimal? pricegunWeightedAverage,
        decimal? mallReference,
        decimal? blended,
        decimal? conservative,
        bool ownStoreContaminatesMarket,
        bool mallIsStale,
        bool pricegunIsStale,
        decimal mallConfidence,
        decimal pricegunConfidence,
        decimal confidence,
        DateTimeOffset asOf,
        decimal listingFee,
        decimal saleFeeRate,
        decimal packagingCost)
    {
        ItemId = itemId;
        TopListings = topListings;
        TopN = topN;
        LowestPrice = lowest;
        SecondPrice = second;
        FifthPrice = fifth;
        MallMedian = mallMedian;
        MallWeightedMedian = mallWeightedMedian;
        CumulativeQuantityTopN = cumulativeTopN;
        CumulativeQuantityThroughSecond = cumulativeThroughSecond;
        CumulativeQuantityThroughFifth = cumulativeThroughFifth;
        PricegunMedian = pricegunMedian;
        PricegunWeightedAverage = pricegunWeightedAverage;
        MallReferencePrice = mallReference;
        BlendedPrice = blended;
        ConservativeSellPrice = conservative;
        OwnStoreContaminatesMarket = ownStoreContaminatesMarket;
        MallIsStale = mallIsStale;
        PricegunIsStale = pricegunIsStale;
        IsStale = mallIsStale || pricegunIsStale;
        MallConfidence = mallConfidence;
        PricegunConfidence = pricegunConfidence;
        Confidence = confidence;
        AsOf = asOf;
        ListingFee = listingFee;
        SaleFeeRate = saleFeeRate;
        PackagingCost = packagingCost;
    }

    public string ItemId { get; }
    public IReadOnlyList<MallListing> TopListings { get; }
    public IReadOnlyList<MallListing> TopNListings => TopListings;
    public int TopN { get; }
    public int MallListingCount => TopListings.Count;

    public decimal? LowestPrice { get; }
    public decimal? SecondPrice { get; }
    public decimal? FifthPrice { get; }
    public decimal? MallLowest => LowestPrice;
    public decimal? MallSecond => SecondPrice;
    public decimal? MallFifth => FifthPrice;
    public decimal? MallLowestPrice => LowestPrice;
    public decimal? MallSecondPrice => SecondPrice;
    public decimal? MallFifthPrice => FifthPrice;
    public decimal? SecondMallPrice => SecondPrice;
    public decimal? FifthMallPrice => FifthPrice;
    public decimal? MallMedian { get; }
    public decimal? MallWeightedMedian { get; }
    public long CumulativeQuantityTopN { get; }
    public long CumulativeQuantity => CumulativeQuantityTopN;
    public long AvailableQuantity => CumulativeQuantityTopN;
    public long MallAvailableQuantity => CumulativeQuantityTopN;
    public long CumulativeQuantityThroughSecond { get; }
    public long CumulativeQuantityThroughFifth { get; }
    public long CumulativeQuantityAtSecond => CumulativeQuantityThroughSecond;
    public long CumulativeQuantityAtFifth => CumulativeQuantityThroughFifth;
    public long CumulativeQuantityTop5 => CumulativeQuantityThroughFifth;
    public long CumulativeQuantityTopFive => CumulativeQuantityThroughFifth;
    public long TopFiveQuantity => CumulativeQuantityThroughFifth;
    public long TopNQuantity => CumulativeQuantityTopN;

    public decimal? PricegunMedian { get; }
    public decimal? PricegunWeightedAverage { get; }
    public decimal? PricegunWeightedMean => PricegunWeightedAverage;
    public decimal? PricegunWeighted => PricegunWeightedAverage;
    public decimal? PricegunAverage => PricegunWeightedAverage;
    public int PricegunSaleCount { get; internal set; }
    public long PricegunQuantity { get; internal set; }
    public long PricegunTotalQuantity => PricegunQuantity;
    public decimal? WeightedAveragePrice => PricegunWeightedAverage;

    /// <summary>Independent Mall anchor (second listing, then fifth, then floor).</summary>
    public decimal? MallReferencePrice { get; }
    public decimal? MallReference => MallReferencePrice;
    /// <summary>Confidence-weighted Mall/Pricegun midpoint when both are present.</summary>
    public decimal? BlendedPrice { get; }
    public decimal? MarketPrice => BlendedPrice;
    /// <summary>Conservative resale price used for net-sale calculations.</summary>
    public decimal? ConservativeSellPrice { get; }
    public decimal? ExpectedSellPrice => ConservativeSellPrice;

    public bool OwnStoreContaminatesMarket { get; }
    public bool OwnStoreContaminated => OwnStoreContaminatesMarket;
    public bool OwnStoreExcluded => OwnStoreContaminatesMarket;
    public bool MallIsStale { get; }
    public bool PricegunIsStale { get; }
    public bool IsStale { get; }
    public decimal MallConfidence { get; }
    public decimal PricegunConfidence { get; }
    public decimal Confidence { get; }
    public decimal ConfidenceScore => Confidence;
    public DateTimeOffset AsOf { get; }
    public DateTimeOffset FetchedAt => AsOf;

    public decimal ListingFee { get; }
    public decimal ListingFees => ListingFee;
    public decimal SaleFeeRate { get; }
    public decimal SaleFeePercent => SaleFeeRate;
    public decimal PackagingCost { get; }

    public decimal? GrossSale(int quantity)
    {
        ValidateQuantity(quantity);
        return ConservativeSellPrice is { } price ? price * quantity : null;
    }

    public decimal? SaleFee(int quantity)
    {
        ValidateQuantity(quantity);
        return GrossSale(quantity) is { } gross ? gross * SaleFeeRate : null;
    }

    /// <summary>Returns sale proceeds after the one-percent Mall cut and fixed costs.</summary>
    public decimal? NetSale(int quantity, decimal? listingFees = null, decimal? packagingCost = null)
    {
        ValidateQuantity(quantity);
        if (ConservativeSellPrice is not { } price) return null;
        var fees = listingFees ?? ListingFee;
        var packaging = packagingCost ?? PackagingCost;
        if (fees < 0) throw new ArgumentOutOfRangeException(nameof(listingFees));
        if (packaging < 0) throw new ArgumentOutOfRangeException(nameof(packagingCost));
        return price * quantity * (1m - SaleFeeRate) - fees - packaging;
    }

    public decimal? NetSaleForQuantity(int quantity, decimal? listingFees = null, decimal? packagingCost = null)
        => NetSale(quantity, listingFees, packagingCost);

    public decimal? NetProceeds(int quantity, decimal? listingFees = null, decimal? packagingCost = null)
        => NetSale(quantity, listingFees, packagingCost);

    private static void ValidateQuantity(int quantity)
    {
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
    }
}

/// <summary>Computes robust market values from one read-only snapshot.</summary>
public class MarketPriceEstimator
{
    private readonly MarketPriceEstimatorOptions _options;

    public MarketPriceEstimator(MarketPriceEstimatorOptions? options = null)
    {
        _options = options ?? new MarketPriceEstimatorOptions();
        _options.Validate();
    }

    public MarketPriceEstimatorOptions Options => _options;

    public MarketEstimate Estimate(PriceSnapshot snapshot, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var timestamp = now ?? DateTimeOffset.UtcNow;
        var ownStoreIds = new HashSet<string>(_options.OwnStoreIds.Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
        ownStoreIds.UnionWith(snapshot.OwnStoreIds);
        var ownStoreNames = new HashSet<string>(_options.OwnStoreNames.Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
        var ownPrices = snapshot.OwnShopListings
            .Where(x => string.Equals(x.ItemId, snapshot.ItemId, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Price)
            .ToHashSet();

        var candidates = new List<MallListing>();
        var ownExcluded = false;
        foreach (var listing in snapshot.MallListings)
        {
            if (!string.Equals(listing.ItemId, snapshot.ItemId, StringComparison.OrdinalIgnoreCase)) continue;
            if (listing.Price < 0 || listing.Quantity <= 0) continue;
            var own = ownStoreIds.Contains(listing.StoreId)
                || (listing.StoreName is not null && ownStoreNames.Contains(listing.StoreName));
            if (!own && _options.ExcludeOwnListingsByPrice && ownPrices.Contains(listing.Price)) own = true;
            if (own)
            {
                ownExcluded = true;
                continue;
            }
            candidates.Add(listing);
        }

        var top = candidates
            .OrderBy(x => x.Price)
            .ThenBy(x => x.ObservedAt)
            .ThenBy(x => x.StoreId, StringComparer.Ordinal)
            .Take(_options.TopN)
            .ToArray();

        decimal? lowest = top.Length > 0 ? top[0].Price : null;
        decimal? second = top.Length > 1 ? top[1].Price : null;
        decimal? fifth = top.Length > 4 ? top[4].Price : null;
        var mallMedian = Median(top.Select(x => (decimal)x.Price).ToArray());
        var mallWeightedMedian = WeightedMedian(top.Select(x => ((decimal)x.Price, (long)x.Quantity)).ToArray());
        var cumulativeTop = top.Sum(x => (long)x.Quantity);
        var cumulativeSecond = top.Take(2).Sum(x => (long)x.Quantity);
        var cumulativeFifth = top.Take(5).Sum(x => (long)x.Quantity);

        var sales = snapshot.PricegunSales
            .Where(x => x.UnitPrice >= 0 && x.Quantity > 0)
            .OrderBy(x => x.UnitPrice)
            .ToArray();
        var pricegunMedian = Median(sales.Select(x => (decimal)x.UnitPrice).ToArray());
        var totalSaleQuantity = sales.Sum(x => (long)x.Quantity);
        var pricegunWeighted = totalSaleQuantity == 0
            ? (decimal?)null
            : sales.Sum(x => (decimal)x.UnitPrice * x.Quantity) / totalSaleQuantity;

        var mallAgeStale = timestamp - snapshot.MallFetchedAt > _options.MaxAge;
        var pricegunAgeStale = timestamp - snapshot.PricegunFetchedAt > _options.MaxAge;
        var mallStale = snapshot.MallIsStale || snapshot.ExplicitlyStale || mallAgeStale;
        var pricegunStale = snapshot.PricegunIsStale || snapshot.ExplicitlyStale || pricegunAgeStale;
        var mallConfidence = SourceConfidence(top.Length, mallStale, hasIndependentAnchor: second is not null);
        var pricegunConfidence = SourceConfidence(sales.Length, pricegunStale, hasIndependentAnchor: sales.Length >= 2);

        var mallReference = second ?? fifth ?? lowest;
        var blended = Blend(
            mallMedian,
            mallConfidence * _options.MallWeight,
            pricegunMedian ?? pricegunWeighted,
            pricegunConfidence * _options.PricegunWeight);
        var conservative = Conservative(mallReference, pricegunMedian, pricegunWeighted, mallMedian, blended);
        var confidence = CombineConfidence(mallMedian is not null, pricegunMedian is not null || pricegunWeighted is not null,
            mallConfidence, pricegunConfidence, ownExcluded);

        var asOf = snapshot.FetchedAt >= snapshot.PricegunFetchedAt ? snapshot.FetchedAt : snapshot.PricegunFetchedAt;
        var estimate = new MarketEstimate(
            snapshot.ItemId,
            top,
            _options.TopN,
            lowest,
            second,
            fifth,
            mallMedian,
            mallWeightedMedian,
            cumulativeTop,
            cumulativeSecond,
            cumulativeFifth,
            pricegunMedian,
            pricegunWeighted,
            mallReference,
            blended,
            conservative,
            ownExcluded,
            mallStale,
            pricegunStale,
            mallConfidence,
            pricegunConfidence,
            confidence,
            asOf,
            _options.ListingFee,
            _options.SaleFeeRate,
            _options.PackagingCost);
        estimate.PricegunSaleCount = sales.Length;
        estimate.PricegunQuantity = totalSaleQuantity;
        return estimate;
    }

    public MarketEstimate Estimate(PriceSnapshot snapshot, IEnumerable<string>? ownStoreIds,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (ownStoreIds is null) return Estimate(snapshot, now);
        var merged = snapshot.OwnStoreIds.Concat(ownStoreIds)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var copy = new PriceSnapshot(snapshot.ItemId, snapshot.MallListings, snapshot.PricegunSales,
            snapshot.OwnShopListings, snapshot.FetchedAt, snapshot.PricegunFetchedAt,
            snapshot.MallIsStale, snapshot.PricegunIsStale, snapshot.ExplicitlyStale,
            snapshot.MallSourceUri, snapshot.PricegunSourceUri, merged);
        return Estimate(copy, now);
    }

    public MarketEstimate Estimate(MallSnapshot mall, PricegunSnapshot? pricegun = null,
        IEnumerable<OwnShopListing>? ownShopListings = null, DateTimeOffset? now = null)
        => Estimate(new PriceSnapshot(mall, pricegun, ownShopListings), now);

    public MarketEstimate Estimate(MallSnapshot mall, PricegunSnapshot? pricegun,
        IEnumerable<OwnShopListing>? ownShopListings, IEnumerable<string>? ownStoreIds,
        DateTimeOffset? now = null)
        => Estimate(new PriceSnapshot(mall, pricegun, ownShopListings, ownStoreIds), now);

    public MarketEstimate Estimate(string itemId, IEnumerable<MallListing>? mallListings = null,
        IEnumerable<PricegunSale>? pricegunSales = null, DateTimeOffset? fetchedAt = null,
        IEnumerable<OwnShopListing>? ownShopListings = null, DateTimeOffset? now = null)
        => Estimate(new PriceSnapshot(itemId, mallListings, pricegunSales, ownShopListings, fetchedAt), now);

    public MarketEstimate Build(PriceSnapshot snapshot, DateTimeOffset? now = null)
        => Estimate(snapshot, now);

    public MarketEstimate Calculate(PriceSnapshot snapshot, DateTimeOffset? now = null)
        => Estimate(snapshot, now);

    private static decimal? Blend(decimal? mall, decimal mallConfidence, decimal? pricegun, decimal pricegunConfidence)
    {
        if (mall is null) return pricegun;
        if (pricegun is null) return mall;
        var denominator = mallConfidence + pricegunConfidence;
        return denominator <= 0 ? (mall.Value + pricegun.Value) / 2m
            : (mall.Value * mallConfidence + pricegun.Value * pricegunConfidence) / denominator;
    }

    private static decimal? Conservative(decimal? mallReference, decimal? pricegunMedian,
        decimal? pricegunWeighted, decimal? mallMedian, decimal? blended)
    {
        var independent = new[] { mallReference, pricegunMedian, pricegunWeighted }
            .Where(x => x is >= 0)
            .Select(x => x!.Value)
            .ToArray();
        if (independent.Length > 0) return independent.Min();
        return blended ?? mallMedian;
    }

    private static decimal SourceConfidence(int count, bool stale, bool hasIndependentAnchor)
    {
        if (count <= 0) return 0m;
        var confidence = count switch
        {
            1 => 0.35m,
            2 => 0.55m,
            3 => 0.70m,
            4 => 0.80m,
            _ => 0.90m
        };
        if (hasIndependentAnchor) confidence = Math.Min(1m, confidence + 0.05m);
        return stale ? confidence * 0.5m : confidence;
    }

    private static decimal CombineConfidence(bool hasMall, bool hasPricegun,
        decimal mall, decimal pricegun, bool ownExcluded)
    {
        decimal result;
        if (hasMall && hasPricegun) result = (mall + pricegun) / 2m;
        else if (hasMall) result = mall;
        else if (hasPricegun) result = pricegun;
        else result = 0m;
        return ownExcluded ? result * 0.95m : result;
    }

    private static decimal? Median(IReadOnlyList<decimal> values)
    {
        if (values.Count == 0) return null;
        var ordered = values.OrderBy(x => x).ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 1 ? ordered[middle] : (ordered[middle - 1] + ordered[middle]) / 2m;
    }

    private static decimal? WeightedMedian(IReadOnlyList<(decimal Price, long Quantity)> values)
    {
        if (values.Count == 0) return null;
        var ordered = values.Where(x => x.Quantity > 0).OrderBy(x => x.Price).ToArray();
        var total = ordered.Sum(x => x.Quantity);
        if (total <= 0) return null;
        var target = (total + 1) / 2m;
        long cumulative = 0;
        foreach (var value in ordered)
        {
            cumulative += value.Quantity;
            if (cumulative >= target) return value.Price;
        }
        return ordered[^1].Price;
    }
}

/// <summary>Short alias for callers that refer to the component as a market estimator.</summary>
public sealed class MarketEstimator
{
    private readonly MarketPriceEstimator _inner;
    public MarketEstimator(MarketPriceEstimatorOptions? options = null) => _inner = new MarketPriceEstimator(options);
    public MarketPriceEstimatorOptions Options => _inner.Options;
    public MarketEstimate Estimate(PriceSnapshot snapshot, DateTimeOffset? now = null) => _inner.Estimate(snapshot, now);
    public MarketEstimate Build(PriceSnapshot snapshot, DateTimeOffset? now = null) => _inner.Estimate(snapshot, now);
    public MarketEstimate Estimate(MallSnapshot mall, PricegunSnapshot? pricegun = null,
        IEnumerable<OwnShopListing>? ownShopListings = null, DateTimeOffset? now = null)
        => _inner.Estimate(mall, pricegun, ownShopListings, now);
}

/// <summary>Alias for code that treats the snapshot as the estimator's primary API.</summary>
public sealed class PriceSnapshotEstimator
{
    private readonly MarketPriceEstimator _inner;
    public PriceSnapshotEstimator(MarketPriceEstimatorOptions? options = null) => _inner = new MarketPriceEstimator(options);
    public MarketEstimate Estimate(PriceSnapshot snapshot, DateTimeOffset? now = null) => _inner.Estimate(snapshot, now);
    public MarketEstimate Build(PriceSnapshot snapshot, DateTimeOffset? now = null) => _inner.Estimate(snapshot, now);
}

/// <summary>Fee arithmetic shared by previews that do not need a full snapshot.</summary>
public static class SaleProceeds
{
    public const decimal DefaultListingFee = 10m;
    public const decimal DefaultSaleFeeRate = 0.01m;

    public static decimal Net(decimal unitPrice, int quantity, decimal listingFee = DefaultListingFee,
        decimal saleFeeRate = DefaultSaleFeeRate, decimal packagingCost = 0m)
    {
        if (unitPrice < 0) throw new ArgumentOutOfRangeException(nameof(unitPrice));
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        if (listingFee < 0) throw new ArgumentOutOfRangeException(nameof(listingFee));
        if (saleFeeRate < 0 || saleFeeRate >= 1) throw new ArgumentOutOfRangeException(nameof(saleFeeRate));
        if (packagingCost < 0) throw new ArgumentOutOfRangeException(nameof(packagingCost));
        return unitPrice * quantity * (1m - saleFeeRate) - listingFee - packagingCost;
    }

    public static decimal CalculateNetSale(decimal unitPrice, int quantity, decimal listingFee = DefaultListingFee,
        decimal saleFeeRate = DefaultSaleFeeRate, decimal packagingCost = 0m)
        => Net(unitPrice, quantity, listingFee, saleFeeRate, packagingCost);

    public static decimal NetSale(decimal unitPrice, int quantity, decimal listingFee = DefaultListingFee,
        decimal saleFeeRate = DefaultSaleFeeRate, decimal packagingCost = 0m)
        => Net(unitPrice, quantity, listingFee, saleFeeRate, packagingCost);

    public static decimal NetSell(decimal unitPrice, int quantity, decimal listingFee = DefaultListingFee,
        decimal saleFeeRate = DefaultSaleFeeRate, decimal packagingCost = 0m)
        => Net(unitPrice, quantity, listingFee, saleFeeRate, packagingCost);
}
