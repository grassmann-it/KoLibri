
namespace KolArbitrage.Core.Markt;

/// <summary>A preview row for one own-shop listing.</summary>
public sealed record RepriceRecommendation
{
    public string ItemId { get; init; } = string.Empty;
    public string? DisplayName { get; init; }
    public long CurrentPrice { get; init; }
    public long CurrentAsk { get => CurrentPrice; init => CurrentPrice = value; }
    public long Current { get => CurrentPrice; init => CurrentPrice = value; }
    public int Quantity { get; init; }
    public int AvailableQuantity { get => Quantity; init => Quantity = value; }
    public DateTimeOffset ObservedAt { get; init; }

    /// <summary>Decimal form is useful for JSON and market arithmetic.</summary>
    public decimal? RecommendedPrice { get; init; }
    public decimal? ProposedPrice { get => RecommendedPrice; init => RecommendedPrice = value; }
    public decimal? TargetPrice { get => RecommendedPrice; init => RecommendedPrice = value; }
    /// <summary>Whole-Meat form accepted by the shop mutation adapter.</summary>
    public long? SuggestedPrice => RecommendedPrice is { } value ? checked((long)value) : null;
    public long? NewPrice => SuggestedPrice;
    public long? RecommendedPriceMeat => SuggestedPrice;

    public decimal? PriceDelta => RecommendedPrice is { } target ? target - CurrentPrice : null;
    public decimal? Delta => PriceDelta;
    public bool HasPriceChange => PriceDelta is { } delta && delta != 0m;
    public bool ShouldReprice => HasPriceChange && Status == RepriceRecommendationStatus.Ready;
    public decimal MinimumPrice { get; init; }

    // Market anchors are kept separately so a reviewer can see why a target
    // was selected and can distinguish a one-listing floor from a robust anchor.
    public decimal? FloorPrice { get; init; }
    public decimal? LowestPrice { get => FloorPrice; init => FloorPrice = value; }
    public decimal? Floor { get => FloorPrice; init => FloorPrice = value; }
    public decimal? MarketLowest { get => FloorPrice; init => FloorPrice = value; }
    public decimal? MallFloor { get => FloorPrice; init => FloorPrice = value; }
    public decimal? MarketFloor { get => FloorPrice; init => FloorPrice = value; }
    public decimal? SecondPrice { get; init; }
    public decimal? Second { get => SecondPrice; init => SecondPrice = value; }
    public decimal? SecondMallPrice { get => SecondPrice; init => SecondPrice = value; }
    public decimal? MarketSecond { get => SecondPrice; init => SecondPrice = value; }
    public decimal? FifthPrice { get; init; }
    public decimal? Fifth { get => FifthPrice; init => FifthPrice = value; }
    public decimal? FifthMallPrice { get => FifthPrice; init => FifthPrice = value; }
    public decimal? MarketFifth { get => FifthPrice; init => FifthPrice = value; }
    public decimal? MedianPrice { get; init; }
    public decimal? Median { get => MedianPrice; init => MedianPrice = value; }
    public decimal? MallMedian { get => MedianPrice; init => MedianPrice = value; }
    public decimal? MarketMedian { get => MedianPrice; init => MedianPrice = value; }
    public decimal? WeightedMedianPrice { get; init; }
    public decimal? MallWeightedMedian { get => WeightedMedianPrice; init => WeightedMedianPrice = value; }
    public decimal? HistoricalMedian { get; init; }
    public decimal? PricegunMedian { get => HistoricalMedian; init => HistoricalMedian = value; }
    public decimal? HistoricalPrice { get => HistoricalMedian; init => HistoricalMedian = value; }
    public decimal? HistoricalWeightedAverage { get; init; }
    public decimal? PricegunWeightedAverage { get => HistoricalWeightedAverage; init => HistoricalWeightedAverage = value; }
    public decimal? ReferencePrice { get; init; }
    public decimal? MarketReferencePrice { get => ReferencePrice; init => ReferencePrice = value; }
    public decimal? ConservativeSellPrice { get; init; }
    public decimal? BlendedPrice { get; init; }
    public decimal Confidence { get; init; }
    public DateTimeOffset? MarketAsOf { get; init; }
    public string ReferenceKind { get; init; } = string.Empty;

    public bool OwnStoreExcluded { get; init; }
    public bool OwnStoreContaminatesMarket { get => OwnStoreExcluded; init => OwnStoreExcluded = value; }
    public bool IsStale { get; init; }
    public bool HistoricalPremium { get; init; }
    public bool HasHistoricalPremium => HistoricalPremium;
    public decimal? HistoricalPremiumPercent { get; init; }
    public decimal? HistoricalPremiumRate { get => HistoricalPremiumPercent; init => HistoricalPremiumPercent = value; }
    public decimal? HistoricalPremiumAmount { get; init; }
    public decimal? HistoricalPremiumValue => HistoricalPremiumAmount;
    public decimal? HistoricalPremiumBaseline { get; init; }

    // Fee arithmetic is explicit in every row. ListingFee and PackagingCost
    // are fixed per listing; SaleFee is the percentage cut on gross proceeds.
    public decimal ListingFee { get; init; }
    public decimal SaleFeeRate { get; init; }
    public decimal SaleFeePercent => SaleFeeRate;
    public decimal SaleFee { get; init; }
    public decimal SaleCommission => SaleFee;
    public decimal PackagingCost { get; init; }
    public decimal TotalFees { get; init; }
    public decimal Fees => TotalFees;
    public decimal GrossProceeds { get; init; }
    public decimal ExpectedGrossProceeds => GrossProceeds;
    public decimal NetProceeds { get; init; }
    public decimal ExpectedNetProceeds => NetProceeds;
    public decimal NetSale => NetProceeds;

    public RepriceRecommendationStatus Status { get; init; }
    public RepriceAction Action => Status switch
    {
        RepriceRecommendationStatus.Ready => RepriceAction.Reprice,
        RepriceRecommendationStatus.Keep => RepriceAction.Keep,
        RepriceRecommendationStatus.Review => RepriceAction.Review,
        _ => RepriceAction.Hold
    };
    public bool IsReady => Status == RepriceRecommendationStatus.Ready;
    public bool IsReview => Status == RepriceRecommendationStatus.Review;
    public bool IsHeld => Status == RepriceRecommendationStatus.Hold;
    public bool RequiresApproval { get; init; }
    public bool IsPreviewOnly { get; init; } = true;
    public ShopPriceUpdate? PriceUpdate { get; init; }
    public ShopPriceUpdate? Update => PriceUpdate;

    public IReadOnlyList<RepriceReasonCode> ReasonCodes { get; init; } = Array.Empty<RepriceReasonCode>();
    public IReadOnlyList<RepriceReasonCode> Reasons => ReasonCodes;
    public RepriceReasonCode ReasonCode => ReasonCodes.FirstOrDefault();
    public RepriceReasonCode PrimaryReasonCode => Status switch
    {
        RepriceRecommendationStatus.Ready => RepriceReasonCode.Ready,
        RepriceRecommendationStatus.Keep => RepriceReasonCode.AlreadyCompetitive,
        RepriceRecommendationStatus.Review when ReasonCodes.Contains(RepriceReasonCode.ReviewRequired)
            => RepriceReasonCode.ReviewRequired,
        _ => ReasonCodes.FirstOrDefault()
    };
    public IReadOnlyList<string> ReasonCodeNames => ReasonCodes.Select(x => x.ToString()).ToArray();
    public IReadOnlyList<string> ReasonTexts { get; init; } = Array.Empty<string>();
    public string Rationale { get; init; } = string.Empty;
}

/// <summary>All rows produced by one deterministic reprice preview.</summary>
public sealed record RepricePlan(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<RepriceRecommendation> Recommendations)
{
    public DateTimeOffset AsOf => GeneratedAt;
    public IReadOnlyList<RepriceRecommendation> Proposals => Recommendations;
    public IReadOnlyList<RepriceRecommendation> Rows => Recommendations;
    public IReadOnlyList<RepriceRecommendation> Suggestions => Recommendations;
    public IReadOnlyList<RepriceRecommendation> Ready => Recommendations
        .Where(x => x.Status == RepriceRecommendationStatus.Ready)
        .ToArray();
    public IReadOnlyList<RepriceRecommendation> Review => Recommendations
        .Where(x => x.Status == RepriceRecommendationStatus.Review)
        .ToArray();
    public IReadOnlyList<RepriceRecommendation> Held => Recommendations
        .Where(x => x.Status == RepriceRecommendationStatus.Hold)
        .ToArray();
    public bool IsPreviewOnly => true;
    public bool RequiresApproval => Recommendations.Any(x => x.RequiresApproval);
    public int RecommendationCount => Recommendations.Count;
    public int ReadyCount => Ready.Count;
}

/// <summary>Convenience request object for query/command adapters.</summary>
public sealed record RepricePlanRequest
{
    public IReadOnlyList<OwnShopListing> Listings { get; init; } = Array.Empty<OwnShopListing>();
    public MarketEstimate? Estimate { get; init; }
    public RepricePolicy? Policy { get; init; }
    public DateTimeOffset? AsOf { get; init; }
}

/// <summary>
/// Builds robust, read-only repricing previews from own-shop rows and a
/// <see cref="MarketEstimate"/>. It never sends a Mall or shop request.
/// </summary>
public class RepricePlanner
{
    private readonly RepricePolicy _policy;
    private readonly TimeProvider _clock;

    public RepricePlanner(RepricePolicy? policy = null, TimeProvider? clock = null)
    {
        _policy = policy ?? new RepricePolicy();
        _policy.Validate();
        _clock = clock ?? TimeProvider.System;
    }

    public RepricePolicy Policy => _policy;
    public RepricePolicy Options => _policy;
    public RepricePolicy DefaultPolicy => _policy;

    public RepricePlan Plan(RepricePlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Estimate);
        if (request.Policy is null || ReferenceEquals(request.Policy, _policy))
            return Plan(request.Listings, request.Estimate, request.AsOf);
        return new RepricePlanner(request.Policy, _clock).Plan(request.Listings, request.Estimate, request.AsOf);
    }

    public RepricePlan Plan(
        IEnumerable<OwnShopListing> listings,
        MarketEstimate estimate,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(listings);
        ArgumentNullException.ThrowIfNull(estimate);
        var timestamp = now ?? _clock.GetUtcNow();
        var rows = listings
            .Select(listing => Recommend(listing, estimate, timestamp))
            .OrderBy(x => x.ItemId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.CurrentPrice)
            .ToArray();
        return new RepricePlan(timestamp, rows);
    }

    public RepricePlan Plan(
        OwnShopListing listing,
        MarketEstimate estimate,
        DateTimeOffset? now = null)
    {
        return Plan(new[] { listing }, estimate, now);
    }

    /// <summary>Plans one own-shop row without creating a one-row collection.</summary>
    public RepriceRecommendation Recommend(
        OwnShopListing listing,
        MarketEstimate estimate,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        return BuildRecommendation(listing, estimate, now ?? _clock.GetUtcNow());
    }

    public RepriceRecommendation Evaluate(
        OwnShopListing listing,
        MarketEstimate estimate,
        DateTimeOffset? now = null)
        => Recommend(listing, estimate, now);

    public RepriceRecommendation Preview(
        OwnShopListing listing,
        MarketEstimate estimate,
        DateTimeOffset? now = null)
        => Recommend(listing, estimate, now);

    public RepriceRecommendation PlanOne(
        OwnShopListing listing,
        MarketEstimate estimate,
        DateTimeOffset? now = null)
        => Recommend(listing, estimate, now);

    public RepricePlan Build(
        IEnumerable<OwnShopListing> listings,
        MarketEstimate estimate,
        DateTimeOffset? now = null)
        => Plan(listings, estimate, now);

    public RepricePlan BuildPlan(
        IEnumerable<OwnShopListing> listings,
        MarketEstimate estimate,
        DateTimeOffset? now = null)
        => Plan(listings, estimate, now);

    public RepricePlan Propose(
        IEnumerable<OwnShopListing> listings,
        MarketEstimate estimate,
        DateTimeOffset? now = null)
        => Plan(listings, estimate, now);

    public RepricePlan Preview(
        IEnumerable<OwnShopListing> listings,
        MarketEstimate estimate,
        DateTimeOffset? now = null)
        => Plan(listings, estimate, now);

    public IReadOnlyList<RepriceRecommendation> EvaluateMany(
        IEnumerable<OwnShopListing> listings,
        MarketEstimate estimate,
        DateTimeOffset? now = null)
        => Plan(listings, estimate, now).Recommendations;

    /// <summary>
    /// Plans rows against item-specific estimates. Rows without a matching
    /// estimate are retained as held recommendations with ItemMismatch or
    /// NoMarketReference evidence rather than being dropped.
    /// </summary>
    public RepricePlan Plan(
        IEnumerable<OwnShopListing> listings,
        IReadOnlyDictionary<string, MarketEstimate> estimates,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(listings);
        ArgumentNullException.ThrowIfNull(estimates);
        var timestamp = now ?? _clock.GetUtcNow();
        var rows = listings.Select(listing =>
        {
            var id = (listing.ItemId ?? string.Empty).Trim();
            return estimates.TryGetValue(id, out var estimate)
                ? Recommend(listing, estimate, timestamp)
                : MissingEstimate(listing, timestamp);
        }).OrderBy(x => x.ItemId, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.CurrentPrice).ToArray();
        return new RepricePlan(timestamp, rows);
    }

    /// <summary>Alias for callers that call a map of estimates a market map.</summary>
    public RepricePlan Plan(
        IEnumerable<OwnShopListing> listings,
        IReadOnlyDictionary<string, MarketEstimate> estimates,
        RepricePolicy? policy,
        DateTimeOffset? now = null)
    {
        if (policy is null || ReferenceEquals(policy, _policy)) return Plan(listings, estimates, now);
        return new RepricePlanner(policy, _clock).Plan(listings, estimates, now);
    }

    private RepriceRecommendation BuildRecommendation(
        OwnShopListing listing,
        MarketEstimate estimate,
        DateTimeOffset now)
    {
        var itemId = (listing.ItemId ?? string.Empty).Trim();
        var codes = new List<RepriceReasonCode>();
        var texts = new List<string>();
        var floor = Positive(estimate.LowestPrice);
        var second = Positive(estimate.SecondPrice);
        var fifth = Positive(estimate.FifthPrice);
        var median = Positive(estimate.MallMedian);
        var weightedMedian = Positive(estimate.MallWeightedMedian);
        var historical = Positive(estimate.PricegunMedian) ?? Positive(estimate.PricegunWeightedAverage);
        var ownExcluded = estimate.OwnStoreContaminatesMarket;

        if (ownExcluded && _policy.ExcludeOwnStore)
        {
            codes.Add(RepriceReasonCode.OwnStoreExcluded);
            texts.Add("Eigene Listings wurden aus der Markt-Referenz ausgeschlossen.");
        }
        if (estimate.IsStale)
        {
            codes.Add(RepriceReasonCode.StaleMarket);
            texts.Add("Mindestens eine Preisquelle ist stale.");
            if (!_policy.AllowStaleMarket) codes.Add(RepriceReasonCode.StaleReview);
        }

        var common = new RecommendationValues
        {
            ItemId = itemId,
            DisplayName = listing.DisplayName,
            CurrentPrice = listing.Price,
            Quantity = listing.Quantity,
            ObservedAt = listing.ObservedAt,
            Floor = floor,
            Second = second,
            Fifth = fifth,
            Median = median,
            WeightedMedian = weightedMedian,
            Historical = historical,
            HistoricalWeighted = Positive(estimate.PricegunWeightedAverage),
            Conservative = Positive(estimate.ConservativeSellPrice),
            Blended = Positive(estimate.BlendedPrice),
            Confidence = estimate.Confidence,
            MarketAsOf = estimate.AsOf,
            OwnExcluded = ownExcluded,
            IsStale = estimate.IsStale,
            ListingFee = _policy.ListingFee,
            SaleFeeRate = _policy.SaleFeeRate,
            PackagingCost = _policy.PackagingCost
        };

        if (string.IsNullOrWhiteSpace(itemId) || listing.Price < 0 || listing.Quantity <= 0)
        {
            codes.Insert(0, RepriceReasonCode.InvalidListing);
            texts.Insert(0, "Listing enthält keine gültige Item-ID, Menge oder Preisangabe.");
            return Create(common, null, null, RepriceRecommendationStatus.Hold, codes, texts,
                "Listing ist ungültig; keine Preisänderung vorgeschlagen.");
        }

        if (!string.Equals(itemId, estimate.ItemId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            codes.Insert(0, RepriceReasonCode.ItemMismatch);
            texts.Insert(0, $"Listing gehört zu {itemId}, die Markt-Referenz aber zu {estimate.ItemId}.");
            return Create(common, null, null, RepriceRecommendationStatus.Hold, codes, texts,
                "Item-ID der Markt-Referenz stimmt nicht mit dem Listing überein.");
        }

        var reference = SelectReference(floor, second, fifth, median, weightedMedian, estimate.MallReferencePrice,
            out var referenceKind, out var referenceCode);
        if (reference is null && historical is > 0m)
        {
            reference = historical;
            referenceKind = "historical";
            referenceCode = RepriceReasonCode.HistoricalReference;
        }
        if (reference is null || reference <= 0m)
        {
            codes.Insert(0, RepriceReasonCode.NoMarketReference);
            texts.Insert(0, "Keine verwertbare Mall-/Historien-Referenz vorhanden.");
            return Create(common, null, null, RepriceRecommendationStatus.Hold, codes, texts,
                "Keine Preisänderung ohne unabhängige Markt-Referenz.");
        }

        if (referenceCode is { } code) codes.Add(code);
        texts.Add($"Referenz {referenceKind}: {reference:0.##} Meat.");
        var mallMedianIndependent = estimate.MallListingCount >= 2
            && (median is not null || weightedMedian is not null);
        var historicalIndependent = estimate.PricegunSaleCount >= 2;
        var hasIndependent = second is not null || fifth is not null || mallMedianIndependent
            || (referenceKind == "historical" && historicalIndependent);
        if (_policy.RequireIndependentReference && !hasIndependent)
        {
            codes.Add(RepriceReasonCode.MissingIndependentReference);
            texts.Add("Nur ein einzelner Mallboden liegt vor; unabhängige Bestätigung fehlt.");
        }

        // Historical premium follows the established compare semantics: use
        // the visible Mall floor as the baseline when it exists. The target
        // itself may use the second/fifth/median anchor, but a gap between
        // floor and history is precisely the signal that a cheap floor could
        // be an outlier.
        var historicalBaseline = floor ?? reference.Value;
        var historicalPremiumAmount = historical is { } history
            ? history - historicalBaseline
            : (decimal?)null;
        var historicalPremiumPercent = historical is > 0m && historicalBaseline > 0m
            ? historical.Value / historicalBaseline - 1m
            : (decimal?)null;
        var historicalPremium = _policy.UseHistoricalPremium
            && historicalPremiumPercent is { } premium
            && premium >= _policy.HistoricalPremiumThreshold;
        common.Reference = reference;
        common.HistoricalPremium = historicalPremium;
        common.HistoricalPremiumPercent = historicalPremiumPercent;
        common.HistoricalPremiumAmount = historicalPremiumAmount;
        common.HistoricalPremiumBaseline = historicalBaseline;
        if (historicalPremium)
        {
            codes.Add(RepriceReasonCode.HistoricalPremium);
            texts.Add($"Historischer Median liegt {historicalPremiumPercent:P0} über der aktuellen Referenz.");
            if (_policy.RequireReviewForHistoricalPremium) codes.Add(RepriceReasonCode.ReviewRequired);
        }
        if (historical is not null) codes.Add(RepriceReasonCode.HistoricalReference);

        var undercut = Math.Min(
            reference.Value - _policy.UndercutTick,
            reference.Value * (1m - _policy.UndercutPercent));
        var target = decimal.Floor(Math.Max(0m, undercut));
        if (historicalPremium && _policy.HistoricalPremiumMarkupPercent > 0m && historical is { } historicalPrice)
        {
            var historicalTarget = decimal.Floor(historicalPrice * (1m - _policy.HistoricalPremiumMarkupPercent));
            if (historicalTarget > target)
            {
                target = historicalTarget;
                texts.Add($"Historische Referenz {historicalPrice:0.##} Meat mit {_policy.HistoricalPremiumMarkupPercent:P0} Abschlag berücksichtigt.");
            }
        }
        var feeFloor = FeeFloor(listing.Quantity);
        if (feeFloor > target)
        {
            target = feeFloor;
            codes.Add(RepriceReasonCode.FeesApplied);
            texts.Add($"Gebühren erfordern mindestens {feeFloor:0.##} Meat pro Einheit.");
        }
        if (target < _policy.MinimumPrice)
        {
            target = _policy.MinimumPrice;
            codes.Add(RepriceReasonCode.MinimumPriceApplied);
            texts.Add($"Konfigurierte Mindestpreisgrenze {_policy.MinimumPrice:0.##} Meat angewandt.");
        }
        target = Math.Max(_policy.MinimumPrice, decimal.Floor(target));

        var status = RepriceRecommendationStatus.Ready;
        if (target == listing.Price && _policy.KeepAlreadyCompetitive)
        {
            codes.Add(RepriceReasonCode.AlreadyCompetitive);
            codes.Add(RepriceReasonCode.PriceUnchanged);
            texts.Add("Aktueller Ask ist bereits mindestens so günstig wie das Reprice-Ziel.");
            status = RepriceRecommendationStatus.Keep;
        }
        else if (_policy.RequireIndependentReference && !hasIndependent)
        {
            status = RepriceRecommendationStatus.Review;
        }
        else if (historicalPremium && _policy.RequireReviewForHistoricalPremium)
        {
            status = RepriceRecommendationStatus.Review;
        }
        else if (estimate.IsStale && !_policy.AllowStaleMarket)
        {
            status = RepriceRecommendationStatus.Hold;
        }
        else
        {
            codes.Add(RepriceReasonCode.Ready);
            texts.Add("Unabhängige Referenz und Mindestpreisgrenzen sind erfüllt.");
        }

        return Create(common, target, referenceKind, status, codes, texts,
            BuildRationale(status, listing.Price, target, reference, historicalPremium));
    }

    private RepriceRecommendation MissingEstimate(OwnShopListing listing, DateTimeOffset now)
    {
        var codes = new[] { RepriceReasonCode.NoMarketReference };
        var texts = new[] { "Keine Markt-Referenz für dieses Item im Snapshot vorhanden." };
        var common = new RecommendationValues
        {
            ItemId = (listing.ItemId ?? string.Empty).Trim(),
            DisplayName = listing.DisplayName,
            CurrentPrice = listing.Price,
            Quantity = listing.Quantity,
            ObservedAt = listing.ObservedAt,
            IsStale = true,
            ListingFee = _policy.ListingFee,
            SaleFeeRate = _policy.SaleFeeRate,
            PackagingCost = _policy.PackagingCost
        };
        return Create(common, null, null, RepriceRecommendationStatus.Hold, codes, texts,
            "Keine Preisänderung ohne passende Markt-Referenz.");
    }

    private RepriceRecommendation Create(
        RecommendationValues values,
        decimal? target,
        string? referenceKind,
        RepriceRecommendationStatus status,
        IReadOnlyList<RepriceReasonCode> codes,
        IReadOnlyList<string> texts,
        string rationale)
    {
        var quantity = values.Quantity;
        var gross = target is { } price && quantity > 0 ? price * quantity : 0m;
        var saleFee = gross * values.SaleFeeRate;
        var totalFees = saleFee + values.ListingFee + values.PackagingCost;
        var net = gross - totalFees;
        ShopPriceUpdate? update = null;
        if (target is { } proposed && proposed >= 100m && proposed <= long.MaxValue && values.ItemId.Length > 0)
        {
            var whole = checked((long)decimal.Floor(proposed));
            if (whole >= 100m && whole != values.CurrentPrice)
            {
                // ShopPriceUpdate validates numeric IDs. Keep the preview
                // available for display even when an adapter cannot commit a
                // non-numeric fixture item id.
                if (values.ItemId.All(char.IsDigit))
                    update = new ShopPriceUpdate(values.ItemId, values.CurrentPrice, whole);
            }
        }

        return new RepriceRecommendation
        {
            ItemId = values.ItemId,
            DisplayName = values.DisplayName,
            CurrentPrice = values.CurrentPrice,
            Quantity = values.Quantity,
            ObservedAt = values.ObservedAt,
            RecommendedPrice = target,
            MinimumPrice = _policy.MinimumPrice,
            FloorPrice = values.Floor,
            SecondPrice = values.Second,
            FifthPrice = values.Fifth,
            MedianPrice = values.Median,
            WeightedMedianPrice = values.WeightedMedian,
            HistoricalMedian = values.Historical,
            HistoricalWeightedAverage = values.HistoricalWeighted,
            ReferencePrice = values.Reference,
            ConservativeSellPrice = values.Conservative,
            BlendedPrice = values.Blended,
            Confidence = values.Confidence,
            MarketAsOf = values.MarketAsOf,
            ReferenceKind = referenceKind ?? string.Empty,
            OwnStoreExcluded = values.OwnExcluded,
            IsStale = values.IsStale,
            HistoricalPremium = values.HistoricalPremium,
            HistoricalPremiumPercent = values.HistoricalPremiumPercent,
            HistoricalPremiumAmount = values.HistoricalPremiumAmount,
            HistoricalPremiumBaseline = values.HistoricalPremiumBaseline,
            ListingFee = values.ListingFee,
            SaleFeeRate = values.SaleFeeRate,
            SaleFee = saleFee,
            PackagingCost = values.PackagingCost,
            TotalFees = totalFees,
            GrossProceeds = gross,
            NetProceeds = net,
            Status = status,
            RequiresApproval = update is not null,
            IsPreviewOnly = true,
            PriceUpdate = update,
            ReasonCodes = codes.Distinct().ToArray(),
            ReasonTexts = texts.ToArray(),
            Rationale = rationale
        };
    }

    private decimal FeeFloor(int quantity)
    {
        if (quantity <= 0 || _policy.SaleFeeRate >= 1m) return decimal.MaxValue;
        var denominator = quantity * (1m - _policy.SaleFeeRate);
        return decimal.Ceiling((_policy.ListingFee + _policy.PackagingCost) / denominator);
    }

    private static decimal? SelectReference(
        decimal? floor,
        decimal? second,
        decimal? fifth,
        decimal? median,
        decimal? weightedMedian,
        decimal? fallback,
        out string kind,
        out RepriceReasonCode? code)
    {
        if (second is > 0m)
        {
            kind = "second";
            code = RepriceReasonCode.SecondReference;
            return second;
        }
        if (fifth is > 0m)
        {
            kind = "fifth";
            code = RepriceReasonCode.FifthReference;
            return fifth;
        }
        if (median is > 0m)
        {
            kind = "median";
            code = RepriceReasonCode.MedianReference;
            return median;
        }
        if (weightedMedian is > 0m)
        {
            kind = "weightedMedian";
            code = RepriceReasonCode.WeightedMedianReference;
            return weightedMedian;
        }
        if (floor is > 0m)
        {
            kind = "floor";
            code = RepriceReasonCode.FloorReference;
            return floor;
        }
        if (fallback is > 0m)
        {
            kind = "marketReference";
            code = RepriceReasonCode.FloorReference;
            return fallback;
        }
        kind = string.Empty;
        code = null;
        return null;
    }

    private static decimal? Positive(decimal? value) => value is > 0m ? value : null;

    private static string BuildRationale(
        RepriceRecommendationStatus status,
        long current,
        decimal target,
        decimal? reference,
        bool historicalPremium)
    {
        var referenceText = reference is { } value ? $"Referenz {value:0.##} Meat" : "ohne Referenz";
        return status switch
        {
            RepriceRecommendationStatus.Ready => historicalPremium
                ? $"Vorschlag {target:0.##} Meat gegen {referenceText}; historische Prämie bleibt als Review-Signal erhalten."
                : $"Vorschlag {target:0.##} Meat gegen {referenceText} (aktuell {current} Meat).",
            RepriceRecommendationStatus.Keep => $"Aktueller Preis {current} Meat bleibt unverändert; {referenceText}.",
            RepriceRecommendationStatus.Review => $"Review erforderlich für Ziel {target:0.##} Meat gegen {referenceText}.",
            _ => "Keine sichere Preisänderung vorgeschlagen."
        };
    }

    private sealed class RecommendationValues
    {
        public string ItemId { get; init; } = string.Empty;
        public string? DisplayName { get; init; }
        public long CurrentPrice { get; init; }
        public int Quantity { get; init; }
        public DateTimeOffset ObservedAt { get; init; }
        public decimal? Floor { get; init; }
        public decimal? Second { get; init; }
        public decimal? Fifth { get; init; }
        public decimal? Median { get; init; }
        public decimal? WeightedMedian { get; init; }
        public decimal? Historical { get; init; }
        public decimal? HistoricalWeighted { get; init; }
        public decimal? Conservative { get; init; }
        public decimal? Blended { get; init; }
        public decimal Confidence { get; init; }
        public DateTimeOffset? MarketAsOf { get; init; }
        public decimal? Reference { get; set; }
        public bool OwnExcluded { get; init; }
        public bool IsStale { get; init; }
        public decimal ListingFee { get; init; }
        public decimal SaleFeeRate { get; init; }
        public decimal PackagingCost { get; init; }
        public bool HistoricalPremium { get; set; }
        public decimal? HistoricalPremiumPercent { get; set; }
        public decimal? HistoricalPremiumAmount { get; set; }
        public decimal? HistoricalPremiumBaseline { get; set; }
    }
}

/// <summary>Compatibility spelling for code that calls this a robust planner.</summary>
public sealed class RobustRepricePlanner : RepricePlanner
{
    public RobustRepricePlanner(RepricePolicy? policy = null, TimeProvider? clock = null)
        : base(policy, clock)
    {
    }
}
