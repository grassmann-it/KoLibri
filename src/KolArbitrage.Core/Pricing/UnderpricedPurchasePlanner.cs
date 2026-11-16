using KolArbitrage.Core.Integration;

namespace KolArbitrage.Core.Pricing;

/// <summary>
/// Produces bounded, explainable purchase previews from exact Mall listings.
/// This class has no transport or mutation capability: a later adapter must
/// refresh the listing and ask for approval before committing a purchase.
/// </summary>
public sealed class UnderpricedPurchasePlanner
{
    private readonly UnderpricedPurchaseLimits _defaults;
    private readonly TimeProvider _clock;

    public UnderpricedPurchasePlanner(UnderpricedPurchaseLimits? defaults = null, TimeProvider? clock = null)
    {
        _defaults = defaults ?? new UnderpricedPurchaseLimits();
        _defaults.Validate();
        _clock = clock ?? TimeProvider.System;
    }

    public UnderpricedPurchasePlan Plan(UnderpricedPurchasePlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Plan(request.Listings, request.References, request.Tradeability, request.Limits);
    }

    public UnderpricedPurchasePlan Plan(
        IEnumerable<UnderpricedPurchaseListing> listings,
        IReadOnlyDictionary<string, RobustPriceReference> references,
        IReadOnlyDictionary<string, UnderpricedItemTradeability>? tradeability = null,
        UnderpricedPurchaseLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(listings);
        ArgumentNullException.ThrowIfNull(references);
        var policy = limits ?? _defaults;
        policy.Validate();

        var drafts = listings.Select(x => EvaluateListing(x, references, tradeability, policy)).ToList();
        var ordered = drafts.Where(x => x.Reasons.Count == 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.NetProfit)
            .ThenBy(x => x.Listing.ItemId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Listing.SellerId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var remaining = policy.Budget - policy.Reserve;
        var itemQuantities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var acceptedItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var acceptedCount = 0;
        foreach (var draft in ordered)
        {
            var itemId = (draft.Listing.ItemId ?? string.Empty).Trim();
            var reasons = new List<(UnderpricedPurchaseRejectionReason Reason, string Text)>();
            var quantity = draft.ProposedQuantity;

            if (acceptedCount >= policy.MaxProposals)
                reasons.Add((UnderpricedPurchaseRejectionReason.ProposalLimitExceeded, "Proposal limit reached."));
            if (!acceptedItems.Contains(itemId) && acceptedItems.Count >= policy.MaxDistinctItems)
                reasons.Add((UnderpricedPurchaseRejectionReason.DistinctItemLimitExceeded, "Distinct item limit reached."));

            var alreadyPlanned = itemQuantities.GetValueOrDefault(itemId);
            var itemRoom = Math.Max(0, policy.MaxQuantityPerItem - alreadyPlanned);
            if (quantity > itemRoom)
            {
                quantity = itemRoom;
                if (quantity == 0)
                    reasons.Add((UnderpricedPurchaseRejectionReason.QuantityLimitExceeded, "Per-item quantity limit reached."));
            }

            var perItemRoom = policy.PerItemBudget == decimal.MaxValue
                ? decimal.MaxValue
                : Math.Max(0m, policy.PerItemBudget - alreadyPlanned * draft.Listing.UnitPrice);
            if (quantity > 0 && perItemRoom != decimal.MaxValue)
            {
                quantity = Math.Min(quantity, (int)Math.Min(quantity, decimal.Floor(perItemRoom / draft.Listing.UnitPrice)));
                if (quantity == 0)
                    reasons.Add((UnderpricedPurchaseRejectionReason.PerItemBudgetExceeded, "Per-item budget exceeded."));
            }

            if (quantity > 0)
            {
                quantity = Math.Min(quantity, (int)Math.Min(quantity, decimal.Floor(remaining / draft.Listing.UnitPrice)));
                // Transfer/packaging costs can make a chunk exceed the
                // budget even when its purchase price fits. Bound the chunk
                // against the complete capital requirement as well.
                var low = 0;
                var high = quantity;
                while (low < high)
                {
                    var middle = low + (high - low + 1) / 2;
                    if (draft.CostFor(middle, policy) <= remaining) low = middle;
                    else high = middle - 1;
                }
                quantity = low;
                if (quantity == 0)
                    reasons.Add((UnderpricedPurchaseRejectionReason.BudgetExceeded, "Available budget after reserve is insufficient."));
            }

            if (reasons.Count > 0)
            {
                draft.Reject(reasons, quantity);
                continue;
            }

            draft.WithQuantity(quantity, policy);
            var economics = ValidateEconomics(draft, policy);
            if (economics.Count > 0)
            {
                draft.Reject(economics, quantity);
                continue;
            }

            draft.Accept(quantity, policy);
            remaining -= draft.TotalCost;
            itemQuantities[itemId] = alreadyPlanned + quantity;
            acceptedItems.Add(itemId);
            acceptedCount++;
        }

        var proposals = drafts.Select(x => x.ToProposal()).ToArray();
        return new UnderpricedPurchasePlan
        {
            Proposals = proposals,
            Budget = policy.Budget,
            ReservedBudget = policy.Reserve,
            PlannedCapital = proposals.Where(x => x.IsAccepted).Sum(x => x.TotalCost),
            PlannedNetProfit = proposals.Where(x => x.IsAccepted).Sum(x => x.NetProfit),
            IsPreviewOnly = true
        };
    }

    public UnderpricedPurchasePlan Plan(
        IEnumerable<UnderpricedPurchaseListing> listings,
        RobustPriceReference reference,
        UnderpricedPurchaseLimits? limits = null,
        UnderpricedItemTradeability? tradeability = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var values = listings?.ToArray() ?? throw new ArgumentNullException(nameof(listings));
        var refs = values.Where(x => x is not null && !string.IsNullOrWhiteSpace(x.ItemId))
            .Select(x => x.ItemId.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x, _ => reference, StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, UnderpricedItemTradeability>? map = tradeability is null
            ? null
            : refs.Keys.ToDictionary(x => x, _ => tradeability, StringComparer.OrdinalIgnoreCase);
        return Plan(values, refs, map, limits);
    }

    /// <summary>Consumes the robust estimate produced by <see cref="MarketPriceEstimator"/>.</summary>
    public UnderpricedPurchasePlan Plan(
        IEnumerable<UnderpricedPurchaseListing> listings,
        MarketEstimate reference,
        UnderpricedPurchaseLimits? limits = null,
        UnderpricedItemTradeability? tradeability = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return Plan(listings, ToReference(reference), limits, tradeability);
    }

    /// <summary>Convenience adapter for the existing read-only Mall model.</summary>
    public UnderpricedPurchasePlan Plan(
        IEnumerable<MallListing> listings,
        MarketEstimate reference,
        UnderpricedPurchaseLimits? limits = null,
        UnderpricedItemTradeability? tradeability = null)
    {
        ArgumentNullException.ThrowIfNull(listings);
        var converted = listings.Select(x => new UnderpricedPurchaseListing(
            x.ItemId, x.StoreId, x.Price, x.Quantity, x.ObservedAt, x.PurchaseLimit, x.StoreName));
        return Plan(converted, reference, limits, tradeability);
    }

    /// <summary>Bridges the guarded purchase budget used by approval adapters.</summary>
    public UnderpricedPurchasePlan Plan(
        IEnumerable<MallListing> listings,
        MarketEstimate reference,
        PurchaseBudget budget,
        UnderpricedItemTradeability? tradeability = null,
        UnderpricedPurchaseLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(budget);
        budget.Validate();
        var mapped = limits ?? new UnderpricedPurchaseLimits
        {
            // Encode the daily cap as a smaller effective budget while still
            // keeping the reserve inside that budget's valid range.
            Budget = Math.Min(budget.AvailableMeat, budget.RemainingDaily + budget.MeatReserve),
            Reserve = budget.MeatReserve,
            PerItemBudget = budget.MaxMeatPerPurchase ?? decimal.MaxValue,
            MaxQuantityPerListing = budget.MaxQuantityPerPurchase ?? int.MaxValue
        };
        return Plan(listings, reference, mapped, tradeability);
    }

    /// <summary>Accepts exact expected purchase requests from the approval layer.</summary>
    public UnderpricedPurchasePlan Plan(
        IEnumerable<PurchaseRequest> requests,
        MarketEstimate reference,
        PurchaseBudget budget,
        UnderpricedItemTradeability? tradeability = null,
        UnderpricedPurchaseLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var converted = requests.Select(x => new UnderpricedPurchaseListing(
            x.ItemId, x.StoreId, x.ExpectedUnitPrice, x.Quantity, null, null, x.ExpectedStoreName));
        budget.Validate();
        var mapped = limits ?? new UnderpricedPurchaseLimits
        {
            Budget = Math.Min(budget.AvailableMeat, budget.RemainingDaily + budget.MeatReserve),
            Reserve = budget.MeatReserve,
            PerItemBudget = budget.MaxMeatPerPurchase ?? decimal.MaxValue,
            MaxQuantityPerListing = budget.MaxQuantityPerPurchase ?? int.MaxValue
        };
        return Plan(converted, reference, mapped, tradeability);
    }

    private static RobustPriceReference ToReference(MarketEstimate value) => new()
    {
        ItemId = value.ItemId,
        ConservativeSellPrice = value.ConservativeSellPrice,
        ReferencePrice = value.MallReferencePrice,
        PricegunMedian = value.PricegunMedian,
        WeightedMedian = value.MallWeightedMedian,
        SecondLowestMallPrice = value.SecondPrice,
        ExpectedSellPrice = value.ExpectedSellPrice,
        AsOf = value.AsOf,
        IsStale = value.IsStale,
        Confidence = value.Confidence,
        RecentSalesQuantity = value.PricegunQuantity > int.MaxValue ? int.MaxValue : (int)Math.Max(0, value.PricegunQuantity),
        ObservedQuantity = value.CumulativeQuantityTopN > int.MaxValue ? int.MaxValue : (int)Math.Max(0, value.CumulativeQuantityTopN)
    };

    public IReadOnlyList<UnderpricedPurchaseProposal> Evaluate(
        IEnumerable<UnderpricedPurchaseListing> listings,
        IReadOnlyDictionary<string, RobustPriceReference> references,
        IReadOnlyDictionary<string, UnderpricedItemTradeability>? tradeability = null,
        UnderpricedPurchaseLimits? limits = null) =>
        Plan(listings, references, tradeability, limits).Proposals;

    public IReadOnlyList<UnderpricedPurchaseProposal> Propose(
        IEnumerable<UnderpricedPurchaseListing> listings,
        IReadOnlyDictionary<string, RobustPriceReference> references,
        IReadOnlyDictionary<string, UnderpricedItemTradeability>? tradeability = null,
        UnderpricedPurchaseLimits? limits = null) =>
        Evaluate(listings, references, tradeability, limits);

    private Draft EvaluateListing(
        UnderpricedPurchaseListing? listing,
        IReadOnlyDictionary<string, RobustPriceReference> references,
        IReadOnlyDictionary<string, UnderpricedItemTradeability>? tradeability,
        UnderpricedPurchaseLimits policy)
    {
        listing ??= new UnderpricedPurchaseListing { ItemId = string.Empty, SellerId = string.Empty };
        var itemId = listing.ItemId?.Trim() ?? string.Empty;
        var sellerId = listing.SellerId?.Trim() ?? string.Empty;
        references.TryGetValue(itemId, out var reference);
        if (reference is null)
        {
            var match = references.FirstOrDefault(x => string.Equals(x.Key.Trim(), itemId, StringComparison.OrdinalIgnoreCase));
            reference = match.Value;
        }

        var reasons = new List<(UnderpricedPurchaseRejectionReason Reason, string Text)>();
        if (string.IsNullOrWhiteSpace(itemId) || string.IsNullOrWhiteSpace(sellerId))
            reasons.Add((UnderpricedPurchaseRejectionReason.InvalidListing, "Item and seller are required."));
        if (listing.UnitPrice <= 0)
            reasons.Add((UnderpricedPurchaseRejectionReason.InvalidPrice, "Unit price must be greater than zero."));
        if (listing.Quantity <= 0)
            reasons.Add((UnderpricedPurchaseRejectionReason.InvalidQuantity, "Listing quantity must be greater than zero."));

        var referencePrice = reference?.GetConservativePrice() ?? 0m;
        if (reference is null || referencePrice <= 0)
            reasons.Add((UnderpricedPurchaseRejectionReason.MissingReference, "No independent conservative resale reference."));
        else
        {
            if (reference.IsStale || (reference.AsOf is { } asOf && _clock.GetUtcNow() - asOf > policy.MaximumReferenceAge))
                reasons.Add((UnderpricedPurchaseRejectionReason.StaleReference, "Resale reference is stale."));
            if (reference.Confidence <= 0)
                reasons.Add((UnderpricedPurchaseRejectionReason.LowConfidenceReference, "Resale reference has no confidence."));
        }

        UnderpricedItemTradeability? itemFacts = null;
        if (tradeability is not null)
        {
            tradeability.TryGetValue(itemId, out itemFacts);
            if (itemFacts is null)
                itemFacts = tradeability.FirstOrDefault(x => string.Equals(x.Key.Trim(), itemId, StringComparison.OrdinalIgnoreCase)).Value;
        }
        if (itemFacts is not null)
        {
            if (!itemFacts.IsTradeable) reasons.Add((UnderpricedPurchaseRejectionReason.NonTradeable, itemFacts.Reason ?? "Item is not tradeable."));
            if (itemFacts.IsAccountBound) reasons.Add((UnderpricedPurchaseRejectionReason.AccountBound, "Item is account-bound."));
            if (!itemFacts.CanPurchase) reasons.Add((UnderpricedPurchaseRejectionReason.CannotPurchase, "Item cannot be purchased."));
            if (!itemFacts.CanSell || !itemFacts.IsGiftable) reasons.Add((UnderpricedPurchaseRejectionReason.CannotResell, "Item cannot be resold or gifted."));
        }

        var liquidity = reference?.GetLiquidityScore() ?? 0m;
        var hasLiquidity = reference is not null &&
            (reference.RecentSalesQuantity > 0 || reference.ObservedQuantity > 0 || reference.SellThroughRate is not null ||
             reference.DailySales is > 0 || reference.EstimatedDaysToSell is > 0);
        if (policy.RequireLiquidityData && !hasLiquidity)
            reasons.Add((UnderpricedPurchaseRejectionReason.MissingLiquidity, "No recent liquidity data."));
        if (hasLiquidity && liquidity < policy.MinimumLiquidityScore)
            reasons.Add((UnderpricedPurchaseRejectionReason.Illiquid, $"Liquidity score {liquidity:0.###} is below {policy.MinimumLiquidityScore:0.###}."));
        if (reference is not null && reference.RecentSalesQuantity < policy.MinimumRecentSalesQuantity)
            reasons.Add((UnderpricedPurchaseRejectionReason.Illiquid, $"Only {reference.RecentSalesQuantity} recent sale(s) observed."));

        var quantity = Math.Max(0, Math.Min(listing.Quantity, Math.Min(policy.MaxQuantityPerListing,
            listing.SellerPurchaseLimit.GetValueOrDefault(int.MaxValue))));
        var draft = new Draft(listing, referencePrice, liquidity, quantity, policy);
        reasons.AddRange(ValidateEconomics(draft, policy));
        draft.Reject(reasons, quantity);
        return draft;
    }

    private static List<(UnderpricedPurchaseRejectionReason Reason, string Text)> ValidateEconomics(Draft draft, UnderpricedPurchaseLimits policy)
    {
        var result = new List<(UnderpricedPurchaseRejectionReason, string)>();
        if (draft.ReferencePrice > 0 && draft.Listing.UnitPrice > 0 &&
            draft.ReferencePrice - draft.Listing.UnitPrice < policy.MinimumAbsoluteSpreadPerUnit)
            result.Add((UnderpricedPurchaseRejectionReason.BelowMinimumSpread, "Spread is below the configured minimum."));
        if (draft.NetProfit < policy.MinimumAbsoluteProfit)
            result.Add((UnderpricedPurchaseRejectionReason.BelowMinimumProfit, "Expected net profit is below the configured minimum."));
        if (draft.MarginPercent < policy.MinimumMarginPercent)
            result.Add((UnderpricedPurchaseRejectionReason.BelowMinimumMargin, "Expected net margin is below the configured minimum."));
        return result;
    }

    private sealed class Draft
    {
        public UnderpricedPurchaseListing Listing { get; }
        public decimal ReferencePrice { get; }
        public decimal LiquidityScore { get; }
        public int ProposedQuantity { get; private set; }
        public decimal GrossSaleValue { get; private set; }
        public decimal SaleFees { get; private set; }
        public decimal AcquisitionCost { get; private set; }
        public decimal TotalCost { get; private set; }
        public decimal NetProfit { get; private set; }
        public decimal MarginPercent { get; private set; }
        public decimal Score { get; private set; }
        public List<(UnderpricedPurchaseRejectionReason Reason, string Text)> Reasons { get; } = [];
        public bool IsAccepted { get; private set; }

        public Draft(UnderpricedPurchaseListing listing, decimal referencePrice, decimal liquidityScore, int quantity, UnderpricedPurchaseLimits policy)
        {
            Listing = listing;
            ReferencePrice = referencePrice;
            LiquidityScore = liquidityScore;
            ProposedQuantity = quantity;
            Calculate(policy);
        }

        public void Reject(IEnumerable<(UnderpricedPurchaseRejectionReason Reason, string Text)> reasons, int quantity)
        {
            ProposedQuantity = quantity;
            foreach (var reason in reasons)
                if (reason.Reason != UnderpricedPurchaseRejectionReason.None && Reasons.All(x => x.Reason != reason.Reason))
                    Reasons.Add(reason);
            IsAccepted = false;
        }

        public void Accept(int quantity, UnderpricedPurchaseLimits policy)
        {
            ProposedQuantity = quantity;
            Calculate(policy);
            Reasons.Clear();
            IsAccepted = true;
        }

        public void WithQuantity(int quantity, UnderpricedPurchaseLimits policy)
        {
            ProposedQuantity = quantity;
            Calculate(policy);
        }

        public decimal CostFor(int quantity, UnderpricedPurchaseLimits policy) =>
            Listing.UnitPrice * quantity + policy.OtherCost + policy.OtherCostPerUnit * quantity;

        private void Calculate(UnderpricedPurchaseLimits policy)
        {
            GrossSaleValue = ReferencePrice * ProposedQuantity;
            SaleFees = GrossSaleValue * policy.SaleFeePercent + (ProposedQuantity > 0 ? policy.ListingFee : 0m);
            AcquisitionCost = Listing.UnitPrice * ProposedQuantity;
            TotalCost = AcquisitionCost + policy.OtherCost + policy.OtherCostPerUnit * ProposedQuantity;
            NetProfit = GrossSaleValue - SaleFees - TotalCost;
            MarginPercent = AcquisitionCost <= 0 ? 0m : NetProfit / AcquisitionCost;
            var marginScore = Math.Clamp(MarginPercent / 2m, 0m, 1m);
            var capitalScore = TotalCost <= 0 ? 0m : Math.Clamp(1m - TotalCost / 100_000m, 0m, 1m);
            var weights = policy.NetProfitWeight + policy.CapitalEfficiencyWeight + policy.LiquidityWeight;
            Score = ((marginScore * policy.NetProfitWeight) + (capitalScore * policy.CapitalEfficiencyWeight) +
                     LiquidityScore * policy.LiquidityWeight) / weights * 100m;
        }

        public UnderpricedPurchaseProposal ToProposal() => new()
        {
            ItemId = Listing.ItemId?.Trim() ?? string.Empty,
            SellerId = Listing.SellerId?.Trim() ?? string.Empty,
            DisplayName = Listing.DisplayName,
            UnitPrice = Listing.UnitPrice,
            AvailableQuantity = Listing.Quantity,
            ProposedQuantity = ProposedQuantity,
            ReferencePrice = ReferencePrice,
            GrossSaleValue = GrossSaleValue,
            SaleFees = SaleFees,
            AcquisitionCost = AcquisitionCost,
            TotalCost = TotalCost,
            NetProfit = NetProfit,
            MarginPercent = MarginPercent,
            LiquidityScore = LiquidityScore,
            Score = Score,
            IsAccepted = IsAccepted,
            IsPreviewOnly = true,
            RejectionReasons = Reasons.Select(x => x.Reason).ToArray(),
            Reasons = Reasons.Select(x => x.Text).ToArray()
        };
    }
}
