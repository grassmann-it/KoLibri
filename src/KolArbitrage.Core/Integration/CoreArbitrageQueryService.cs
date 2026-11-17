using KolArbitrage.Core.Pricing;

namespace KolArbitrage.Core.Integration;

/// <summary>
/// A read-only observation consumed by <see cref="CoreArbitrageQueryService"/>.
/// Keeping the complete <see cref="PriceSnapshot"/> here means a query can be
/// replayed from an offline snapshot and does not need to know which adapter
/// fetched Mall or Pricegun data.
/// </summary>
public sealed record ArbitrageObservation
{
    public PriceSnapshot Snapshot { get; init; } = null!;

    /// <summary>Catalog facts used by the tradeability gate.</summary>
    public UnderpricedItemTradeability? Tradeability { get; init; }

    /// <summary>
    /// Cost of moving one unit to the account that will sell it. A null value
    /// means that the route was not priced; zero is an explicit free route.
    /// </summary>
    public decimal? TransferCostPerUnit { get; init; }

    /// <summary>Fixed transfer cost for this observation, if any.</summary>
    public decimal? TransferCost { get; init; }

    public string ItemId => Snapshot.ItemId;

    public ArbitrageObservation(PriceSnapshot snapshot)
        => Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));

    public ArbitrageObservation(
        MallSnapshot mall,
        PricegunSnapshot? pricegun = null,
        IEnumerable<OwnShopListing>? ownShopListings = null,
        IEnumerable<string>? ownStoreIds = null,
        UnderpricedItemTradeability? tradeability = null,
        decimal? transferCost = null,
        decimal? transferCostPerUnit = null)
        : this(new PriceSnapshot(mall, pricegun, ownShopListings, ownStoreIds))
    {
        Tradeability = tradeability;
        TransferCost = transferCost;
        TransferCostPerUnit = transferCostPerUnit;
    }

    public decimal? TotalTransferCost(int quantity)
    {
        if (quantity < 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        if (TransferCost is null && TransferCostPerUnit is null) return null;
        return (TransferCost ?? 0m) + (TransferCostPerUnit ?? 0m) * quantity;
    }
}

/// <summary>Policy and safety gates for an arbitrage read.</summary>
public record ArbitrageQueryOptions
{
    public MarketPriceEstimatorOptions Market { get; init; } = new();
    public MarketPriceEstimatorOptions MarketOptions { get => Market; init => Market = value; }
    public UnderpricedPurchaseLimits Purchase { get; init; } = new();
    public UnderpricedPurchaseLimits PurchaseLimits { get => Purchase; init => Purchase = value; }

    /// <summary>Maximum acceptable age for either Mall or historical data.</summary>
    public TimeSpan MaximumSnapshotAge { get; init; } = TimeSpan.FromHours(6);
    public TimeSpan MaximumAge { get => MaximumSnapshotAge; init => MaximumSnapshotAge = value; }

    /// <summary>
    /// Stale rows remain visible when true, but are never marked Buy. When
    /// false, stale rows are returned as rejected rows only when requested.
    /// </summary>
    public bool AllowStale { get; init; } = true;
    public bool AllowStaleSnapshots { get => AllowStale; init => AllowStale = value; }
    public bool RequireFreshSnapshots { get => !AllowStale; init => AllowStale = !value; }

    /// <summary>
    /// If enabled, a missing catalog fact is a safety failure. The default is
    /// conservative because a Mall underprice without tradeability evidence
    /// must not silently become a purchase recommendation.
    /// </summary>
    public bool RequireTradeabilityFacts { get; init; } = true;
    public bool RequireTradeability { get => RequireTradeabilityFacts; init => RequireTradeabilityFacts = value; }

    /// <summary>
    /// Require an explicitly priced transfer route. A supplied zero cost is a
    /// valid route and is different from an unknown (null) cost.
    /// </summary>
    public bool RequireTransferCost { get; init; } = true;
    public bool RequireTransferPricing { get => RequireTransferCost; init => RequireTransferCost = value; }

    /// <summary>Fallback transfer costs for observations that provide none.</summary>
    public decimal? DefaultTransferCost { get; init; }
    public decimal? TransferCost { get => DefaultTransferCost; init => DefaultTransferCost = value; }
    public decimal? DefaultTransferCostPerUnit { get; init; }
    public decimal? TransferCostPerUnit { get => DefaultTransferCostPerUnit; init => DefaultTransferCostPerUnit = value; }

    /// <summary>Tradeability facts keyed by item id, used as a fallback.</summary>
    public IReadOnlyDictionary<string, UnderpricedItemTradeability> Tradeability { get; init; } =
        new Dictionary<string, UnderpricedItemTradeability>(StringComparer.OrdinalIgnoreCase);

    public void Validate()
    {
        Market.Validate();
        Purchase.Validate();
        if (MaximumSnapshotAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(MaximumSnapshotAge));
        if (DefaultTransferCost is < 0 || DefaultTransferCostPerUnit is < 0)
            throw new ArgumentOutOfRangeException(nameof(DefaultTransferCost));
    }
}

/// <summary>Bounded input to a Core arbitrage query.</summary>
public record ArbitrageQuery
{
    public IReadOnlyCollection<ArbitrageObservation> Observations { get; init; } = Array.Empty<ArbitrageObservation>();
    public IReadOnlyCollection<string> ItemIds { get; init; } = Array.Empty<string>();
    public IReadOnlySet<string> ExcludedSellerIds { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public bool IncludeRejected { get; init; }
    public int Limit { get; init; } = 100;
    public string? Cursor { get; init; }
    public DateTimeOffset? AsOf { get; init; }
    public TimeSpan? MaximumAge { get; init; }
    public decimal? MinimumMarginPercent { get; init; }
    public decimal? MinimumNetProfit { get; init; }
    public decimal? TransferCost { get; init; }
    public decimal? TransferCostPerUnit { get; init; }
    public UnderpricedPurchaseLimits? PurchaseLimits { get; init; }
    public IReadOnlyDictionary<string, UnderpricedItemTradeability>? Tradeability { get; init; }

    // Compatibility aliases used by hosts that call this request-shaped.
    public IReadOnlyCollection<string> Items { get => ItemIds; init => ItemIds = value ?? Array.Empty<string>(); }
    public int MaxResults { get => Limit; init => Limit = value; }
    public string? ItemId
    {
        get => ItemIds.Count == 1 ? ItemIds.First() : null;
        init => ItemIds = string.IsNullOrWhiteSpace(value) ? Array.Empty<string>() : new[] { value };
    }

    public void Validate()
    {
        if (Limit <= 0) throw new ArgumentOutOfRangeException(nameof(Limit));
        if (MaximumAge is { } age && age <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(MaximumAge));
        if (MinimumMarginPercent is < 0)
            throw new ArgumentOutOfRangeException(nameof(MinimumMarginPercent));
        if (MinimumNetProfit is < 0)
            throw new ArgumentOutOfRangeException(nameof(MinimumNetProfit));
        if (TransferCost is < 0 || TransferCostPerUnit is < 0)
            throw new ArgumentOutOfRangeException(nameof(TransferCost));
    }
}

/// <summary>Compatibility name for callers that use request terminology.</summary>
public sealed record ArbitrageQueryRequest : ArbitrageQuery;

/// <summary>Local projection of LibKolibri's read-side snapshot marker.</summary>
public sealed record CoreSnapshotState
{
    public SnapshotFreshness Freshness { get; init; } = SnapshotFreshness.Unknown;
    public DateTimeOffset ObservedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string? Source { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public bool IsStale => Freshness is SnapshotFreshness.Stale or SnapshotFreshness.Expired;
    public bool IsFresh => Freshness == SnapshotFreshness.Fresh;
}

/// <summary>Freshness values mirror LibKolibri's read-side contract.</summary>
public enum SnapshotFreshness
{
    Unknown,
    Fresh,
    Stale,
    Expired,
    Missing
}

/// <summary>Decision values mirror LibKolibri.ArbitrageDecision.</summary>
public enum CoreArbitrageDecision
{
    Unknown,
    Buy,
    Hold,
    Reject,
    Investigate
}

/// <summary>A serializable, explainable arbitrage candidate.</summary>
public sealed record CoreArbitrageCandidate
{
    public string ItemId { get; init; } = string.Empty;
    public string? DisplayName { get; init; }
    public string? SellerId { get; init; }
    public int AvailableQuantity { get; init; }
    public int ProposedQuantity { get; init; }
    public decimal? AskPrice { get; init; }
    public decimal? ReferencePrice { get; init; }
    public decimal? ExpectedNetProfit { get; init; }
    public decimal? MarginPercent { get; init; }
    public decimal? LiquidityScore { get; init; }
    public bool IsTradeable { get; init; }
    public bool IsAccountBound { get; init; }
    public bool IsStale { get; init; }
    public bool TransferCostKnown { get; init; }
    public decimal? TransferCost { get; init; }
    public CoreArbitrageDecision Decision { get; init; } = CoreArbitrageDecision.Unknown;
    public bool RequiresApproval { get; init; }
    public IReadOnlyList<string> ReasonCodes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();
    public DateTimeOffset ObservedAt { get; init; }
    public CoreSnapshotState Snapshot { get; init; } = new();

    // LibKolibri-compatible convenience aliases.
    public decimal? NetProfit => ExpectedNetProfit;
    public decimal? MarketPrice => ReferencePrice;
    public int Quantity => ProposedQuantity > 0 ? ProposedQuantity : AvailableQuantity;
    public bool IsAccepted => Decision == CoreArbitrageDecision.Buy;
}

/// <summary>Result of a deterministic, read-only Core arbitrage query.</summary>
public sealed record CoreArbitrageQueryResult
{
    public CoreQueryState State { get; init; } = CoreQueryState.Unknown;
    public IReadOnlyList<CoreArbitrageCandidate> Candidates { get; init; } = Array.Empty<CoreArbitrageCandidate>();
    public IReadOnlyList<CoreArbitrageCandidate> Opportunities => Candidates;
    public CoreSnapshotState Snapshot { get; init; } = new();
    public IReadOnlyList<CoreQueryDiagnostic> Diagnostics { get; init; } = Array.Empty<CoreQueryDiagnostic>();
    public string? NextCursor { get; init; }
    public bool IsStale => Snapshot.IsStale || State == CoreQueryState.Stale;
    public bool Succeeded => State is CoreQueryState.Ready or CoreQueryState.Empty or CoreQueryState.Stale;
}

public enum CoreQueryState
{
    Unknown,
    Ready,
    Empty,
    Stale,
    Error
}

public sealed record CoreQueryDiagnostic(string Code, string Message, bool IsError = false, bool Retryable = false);

/// <summary>
/// Composes the robust estimator and underprice planner into one bounded
/// read-only query. No method in this class can buy, transfer or mutate a
/// listing.
/// </summary>
public class CoreArbitrageQueryService
{
    private readonly MarketPriceEstimator _estimator;
    private readonly UnderpricedPurchasePlanner _planner;
    private readonly ArbitrageQueryOptions _defaults;
    private readonly TimeProvider _clock;

    public CoreArbitrageQueryService(
        MarketPriceEstimator? estimator = null,
        UnderpricedPurchasePlanner? planner = null,
        ArbitrageQueryOptions? options = null,
        TimeProvider? clock = null)
    {
        _defaults = options ?? new ArbitrageQueryOptions();
        _defaults.Validate();
        _clock = clock ?? TimeProvider.System;
        _estimator = estimator ?? new MarketPriceEstimator(_defaults.Market);
        _planner = planner ?? new UnderpricedPurchasePlanner(_defaults.Purchase, _clock);
    }

    public MarketPriceEstimator Estimator => _estimator;
    public UnderpricedPurchasePlanner Planner => _planner;
    public ArbitrageQueryOptions Options => _defaults;

    public CoreArbitrageQueryResult Query(
        IEnumerable<ArbitrageObservation> observations,
        ArbitrageQuery? query = null,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(observations);
        query ??= new ArbitrageQuery();
        query.Validate();
        var options = BuildOptions(query);
        var timestamp = now ?? query.AsOf ?? _clock.GetUtcNow();
        var selected = SelectObservations(observations, query).ToArray();
        if (selected.Length == 0)
        {
            return new CoreArbitrageQueryResult
            {
                State = CoreQueryState.Empty,
                Snapshot = Snapshot(timestamp, SnapshotFreshness.Missing, null, "No observations matched the query."),
                Diagnostics = new[] { new CoreQueryDiagnostic("empty", "No observations matched the query.") }
            };
        }

        var staleAny = selected.Any(x => IsStale(x.Snapshot, timestamp, options.MaximumSnapshotAge));
        var references = new Dictionary<string, RobustPriceReference>(StringComparer.OrdinalIgnoreCase);
        var tradeability = new Dictionary<string, UnderpricedItemTradeability>(StringComparer.OrdinalIgnoreCase);
        var listings = new List<UnderpricedPurchaseListing>();
        var staleByItem = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var transferKnownByItem = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var transferByItem = new Dictionary<string, (decimal Fixed, decimal PerUnit)>(StringComparer.OrdinalIgnoreCase);
        var maximumFixedTransfer = 0m;
        var maximumPerUnitTransfer = 0m;

        foreach (var observation in selected)
        {
            var itemId = observation.Snapshot.ItemId.Trim();
            var estimate = _estimator.Estimate(observation.Snapshot, timestamp);
            references[itemId] = ToReference(estimate, timestamp, options.MaximumSnapshotAge);
            staleByItem[itemId] = IsStale(observation.Snapshot, timestamp, options.MaximumSnapshotAge) || estimate.IsStale;

            var facts = ResolveTradeability(itemId, observation.Tradeability, query.Tradeability, options.Tradeability);
            if (facts is not null) tradeability[itemId] = facts;

            var transfer = ResolveTransfer(observation, query, options);
            transferKnownByItem[itemId] = transfer.Known;
            transferByItem[itemId] = (transfer.Fixed, transfer.PerUnit);
            maximumFixedTransfer = Math.Max(maximumFixedTransfer, transfer.Fixed);
            maximumPerUnitTransfer = Math.Max(maximumPerUnitTransfer, transfer.PerUnit);

            foreach (var listing in observation.Snapshot.MallListings)
            {
                if (query.ExcludedSellerIds.Contains(listing.StoreId)) continue;
                listings.Add(new UnderpricedPurchaseListing(
                    itemId,
                    listing.StoreId,
                    listing.Price,
                    listing.Quantity,
                    listing.ObservedAt,
                    listing.PurchaseLimit,
                    observation.Snapshot.ItemId));
            }
        }

        // UnderpricedPurchasePlanner already accounts for fixed and per-unit
        // costs. Applying the largest declared transfer surcharge keeps the
        // batch budget conservative when different items use different
        // routes; the row still reports its exact route cost below.
        var plannerLimits = options.Purchase with
        {
            OtherCost = options.Purchase.OtherCost + maximumFixedTransfer,
            OtherCostPerUnit = options.Purchase.OtherCostPerUnit + maximumPerUnitTransfer
        };
        var plan = _planner.Plan(listings, references, tradeability, plannerLimits);
        var resultRows = new List<CoreArbitrageCandidate>(plan.Proposals.Count);
        foreach (var proposal in plan.Proposals)
        {
            if (!proposal.IsAccepted && !query.IncludeRejected)
                continue;
            var itemId = proposal.ItemId.Trim();
            var stale = staleByItem.GetValueOrDefault(itemId, true);
            var transferKnown = transferKnownByItem.GetValueOrDefault(itemId, false);
            var transferParts = transferByItem.GetValueOrDefault(itemId, (Fixed: 0m, PerUnit: 0m));
            var transfer = transferParts.Fixed + transferParts.PerUnit * proposal.ProposedQuantity;
            var facts = tradeability.GetValueOrDefault(itemId);
            var reasons = proposal.Reasons.ToList();
            var codes = proposal.RejectionReasons.Select(x => x.ToString()).ToList();
            var decision = proposal.IsAccepted ? CoreArbitrageDecision.Buy : CoreArbitrageDecision.Reject;

            if (stale)
            {
                AddReason(codes, reasons, "stale_snapshot", "Mall or historical price data is stale.");
                if (!options.AllowStale || proposal.IsAccepted)
                    decision = CoreArbitrageDecision.Hold;
            }
            if (facts is null)
            {
                AddReason(codes, reasons, "tradeability_unknown", "No tradeability facts were supplied.");
                if (options.RequireTradeabilityFacts)
                {
                    decision = CoreArbitrageDecision.Reject;
                    if (!query.IncludeRejected) continue;
                }
            }
            else
            {
                if (!facts.IsTradeable) AddReason(codes, reasons, "non_tradeable", facts.Reason ?? "Item is not tradeable.");
                if (facts.IsAccountBound) AddReason(codes, reasons, "account_bound", "Item is account-bound.");
                if (!facts.CanPurchase) AddReason(codes, reasons, "cannot_purchase", "Item cannot be purchased.");
                if (!facts.CanSell || !facts.IsGiftable) AddReason(codes, reasons, "cannot_resell", "Item cannot be resold or gifted.");
                if (!facts.IsTradeable || facts.IsAccountBound || !facts.CanPurchase || !facts.CanSell || !facts.IsGiftable)
                {
                    decision = CoreArbitrageDecision.Reject;
                    if (!query.IncludeRejected) continue;
                }
            }
            if (!transferKnown)
            {
                AddReason(codes, reasons, "transfer_cost_unknown", "Transfer cost is not priced.");
                if (options.RequireTransferCost)
                {
                    decision = CoreArbitrageDecision.Reject;
                    if (!query.IncludeRejected) continue;
                }
            }

            // Query thresholds are applied after the planner so they are
            // explicit in the response even when a caller supplied a looser
            // planner policy.
            if (query.MinimumMarginPercent is { } minMargin && proposal.MarginPercent < minMargin)
            {
                AddReason(codes, reasons, "below_minimum_margin", $"Margin {proposal.MarginPercent:P2} is below {minMargin:P2}.");
                decision = CoreArbitrageDecision.Reject;
                if (!query.IncludeRejected) continue;
            }
            if (query.MinimumNetProfit is { } minProfit && proposal.NetProfit < minProfit)
            {
                AddReason(codes, reasons, "below_minimum_profit", $"Net profit {proposal.NetProfit:0.##} is below {minProfit:0.##} Meat.");
                decision = CoreArbitrageDecision.Reject;
                if (!query.IncludeRejected) continue;
            }

            var observation = selected.FirstOrDefault(x => string.Equals(x.ItemId, itemId, StringComparison.OrdinalIgnoreCase));
            var observedAt = observation?.Snapshot.FetchedAt ?? timestamp;
            var freshness = stale ? SnapshotFreshness.Stale : SnapshotFreshness.Fresh;
            resultRows.Add(new CoreArbitrageCandidate
            {
                ItemId = itemId,
                DisplayName = proposal.DisplayName,
                SellerId = proposal.SellerId,
                AvailableQuantity = proposal.AvailableQuantity,
                ProposedQuantity = proposal.ProposedQuantity,
                AskPrice = proposal.UnitPrice,
                ReferencePrice = proposal.ReferencePrice,
                ExpectedNetProfit = proposal.NetProfit,
                MarginPercent = proposal.MarginPercent,
                LiquidityScore = proposal.LiquidityScore,
                IsTradeable = facts?.IsTradeable ?? false,
                IsAccountBound = facts?.IsAccountBound ?? false,
                IsStale = stale,
                TransferCostKnown = transferKnown,
                TransferCost = transferKnown ? transfer : null,
                Decision = decision,
                RequiresApproval = decision == CoreArbitrageDecision.Buy,
                ReasonCodes = codes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                Reasons = reasons.Distinct(StringComparer.Ordinal).ToArray(),
                ObservedAt = observedAt,
                Snapshot = Snapshot(observedAt, freshness, observation?.Snapshot.MallSourceUri?.ToString())
            });
        }

        var visible = resultRows
            .OrderByDescending(x => x.Decision == CoreArbitrageDecision.Buy)
            .ThenByDescending(x => x.ExpectedNetProfit ?? decimal.MinValue)
            .ThenBy(x => x.ItemId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.SellerId, StringComparer.OrdinalIgnoreCase)
            .Take(query.Limit)
            .ToArray();
        var state = staleAny
            ? CoreQueryState.Stale
            : visible.Length == 0 ? CoreQueryState.Empty : CoreQueryState.Ready;
        var diagnostics = staleAny
            ? new[] { new CoreQueryDiagnostic("stale_snapshot", "One or more observations are stale.") }
            : Array.Empty<CoreQueryDiagnostic>();
        return new CoreArbitrageQueryResult
        {
            State = state,
            Candidates = visible,
            Snapshot = Snapshot(timestamp, staleAny ? SnapshotFreshness.Stale : SnapshotFreshness.Fresh),
            Diagnostics = diagnostics,
            NextCursor = resultRows.Count > visible.Length ? (query.Cursor ?? string.Empty) : null
        };
    }

    public CoreArbitrageQueryResult Evaluate(
        IEnumerable<ArbitrageObservation> observations,
        ArbitrageQuery? query = null,
        DateTimeOffset? now = null)
        => Query(observations, query, now);

    /// <summary>Convenience overload for one offline price snapshot.</summary>
    public CoreArbitrageQueryResult Query(
        PriceSnapshot snapshot,
        ArbitrageQuery? query = null,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Query(new[] { new ArbitrageObservation(snapshot) }, query, now);
    }

    /// <summary>Convenience overload for replaying several snapshots.</summary>
    public CoreArbitrageQueryResult Query(
        IEnumerable<PriceSnapshot> snapshots,
        ArbitrageQuery? query = null,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        return Query(snapshots.Select(x => new ArbitrageObservation(x)), query, now);
    }

    public Task<CoreArbitrageQueryResult> QueryAsync(
        IEnumerable<ArbitrageObservation> observations,
        ArbitrageQuery? query = null,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Query(observations, query, now));
    }

    public Task<CoreArbitrageQueryResult> QueryAsync(
        PriceSnapshot snapshot,
        ArbitrageQuery? query = null,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Query(snapshot, query, now));
    }

    public Task<CoreArbitrageQueryResult> GetCandidatesAsync(
        IEnumerable<ArbitrageObservation> observations,
        ArbitrageQuery? query = null,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
        => QueryAsync(observations, query, now, cancellationToken);

    private ArbitrageQueryOptions BuildOptions(ArbitrageQuery query)
    {
        var options = _defaults;
        if (query.MaximumAge is { } age) options = options with { MaximumSnapshotAge = age };
        if (query.TransferCost is not null) options = options with { DefaultTransferCost = query.TransferCost };
        if (query.TransferCostPerUnit is not null) options = options with { DefaultTransferCostPerUnit = query.TransferCostPerUnit };
        if (query.PurchaseLimits is not null) options = options with { Purchase = query.PurchaseLimits };
        if (query.Tradeability is not null) options = options with { Tradeability = query.Tradeability };
        options.Validate();
        var purchase = options.Purchase;
        if (query.MinimumMarginPercent is { } margin)
            purchase = purchase with { MinimumMarginPercent = margin };
        if (query.MinimumNetProfit is { } profit)
            purchase = purchase with { MinimumAbsoluteProfit = profit };
        return options with { Purchase = purchase };
    }

    private static IEnumerable<ArbitrageObservation> SelectObservations(
        IEnumerable<ArbitrageObservation> observations,
        ArbitrageQuery query)
    {
        var ids = query.ItemIds.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return observations.Where(x => x is not null)
            .Where(x => ids.Count == 0 || ids.Contains(x.ItemId))
            .GroupBy(x => x.ItemId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(x => x.OrderByDescending(o => o.Snapshot.FetchedAt).First());
    }

    private static RobustPriceReference ToReference(
        MarketEstimate estimate,
        DateTimeOffset now,
        TimeSpan maximumAge)
    {
        var stale = estimate.IsStale || now - estimate.AsOf > maximumAge;
        return new RobustPriceReference
        {
            ItemId = estimate.ItemId,
            ConservativeSellPrice = estimate.ConservativeSellPrice,
            ReferencePrice = estimate.MallReferencePrice,
            PricegunMedian = estimate.PricegunMedian,
            WeightedMedian = estimate.MallWeightedMedian,
            SecondLowestMallPrice = estimate.SecondPrice,
            ExpectedSellPrice = estimate.ExpectedSellPrice,
            AsOf = estimate.AsOf,
            IsStale = stale,
            Confidence = estimate.Confidence,
            RecentSalesQuantity = estimate.PricegunQuantity > int.MaxValue ? int.MaxValue : (int)Math.Max(0, estimate.PricegunQuantity),
            ObservedQuantity = estimate.CumulativeQuantityTopN > int.MaxValue ? int.MaxValue : (int)Math.Max(0, estimate.CumulativeQuantityTopN)
        };
    }

    private static (bool Known, decimal Fixed, decimal PerUnit) ResolveTransfer(
        ArbitrageObservation observation,
        ArbitrageQuery query,
        ArbitrageQueryOptions options)
    {
        var fixedCost = observation.TransferCost ?? options.DefaultTransferCost ?? query.TransferCost;
        var perUnit = observation.TransferCostPerUnit ?? options.DefaultTransferCostPerUnit ?? query.TransferCostPerUnit;
        if (fixedCost is null && perUnit is null) return (false, 0m, 0m);
        return (true, Math.Max(0m, fixedCost ?? 0m), Math.Max(0m, perUnit ?? 0m));
    }

    private static UnderpricedItemTradeability? ResolveTradeability(
        string itemId,
        UnderpricedItemTradeability? direct,
        IReadOnlyDictionary<string, UnderpricedItemTradeability>? query,
        IReadOnlyDictionary<string, UnderpricedItemTradeability> defaults)
    {
        if (direct is not null) return direct;
        if (query is not null && query.TryGetValue(itemId, out var fromQuery)) return fromQuery;
        return defaults.TryGetValue(itemId, out var fromDefaults) ? fromDefaults : null;
    }

    private static bool IsStale(PriceSnapshot snapshot, DateTimeOffset now, TimeSpan maxAge)
        => snapshot.IsStale || now - snapshot.FetchedAt > maxAge ||
           now - snapshot.PricegunFetchedAt > maxAge;

    private static CoreSnapshotState Snapshot(
        DateTimeOffset observedAt,
        SnapshotFreshness freshness,
        string? source = null,
        string? error = null)
        => new()
        {
            ObservedAt = observedAt,
            Freshness = freshness,
            Source = source,
            ErrorMessage = error
        };

    private static void AddReason(List<string> codes, List<string> reasons, string code, string reason)
    {
        if (!codes.Contains(code, StringComparer.OrdinalIgnoreCase)) codes.Add(code);
        if (!reasons.Contains(reason, StringComparer.Ordinal)) reasons.Add(reason);
    }
}

/// <summary>Compatibility spelling for clients that omit the Core prefix.</summary>
public sealed class ArbitrageQueryService : CoreArbitrageQueryService
{
    public ArbitrageQueryService(
        MarketPriceEstimator? estimator = null,
        UnderpricedPurchasePlanner? planner = null,
        ArbitrageQueryOptions? options = null,
        TimeProvider? clock = null)
        : base(estimator, planner, options, clock)
    {
    }
}
