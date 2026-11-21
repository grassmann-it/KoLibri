
namespace KoLibri.Core.Markt;

/// <summary>One own-shop row paired with the market snapshot used to evaluate it.</summary>
public sealed record ShopObservation
{
    public OwnShopListing Listing { get; init; } = null!;
    public PriceSnapshot Snapshot { get; init; } = null!;
    public UnderpricedItemTradeability? Tradeability { get; init; }

    public ShopObservation(OwnShopListing listing, PriceSnapshot snapshot)
    {
        Listing = listing ?? throw new ArgumentNullException(nameof(listing));
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    public string ItemId => Listing.ItemId;
}

/// <summary>Options for the read-only own-shop query.</summary>
public record ShopQueryOptions
{
    public MarketPriceEstimatorOptions Market { get; init; } = new();
    public MarketPriceEstimatorOptions MarketOptions { get => Market; init => Market = value; }
    public RepricePolicy Policy { get; init; } = new();
    public RepricePolicy RepricePolicy { get => Policy; init => Policy = value; }
    public TimeSpan MaximumSnapshotAge { get; init; } = TimeSpan.FromHours(6);
    public TimeSpan MaximumAge { get => MaximumSnapshotAge; init => MaximumSnapshotAge = value; }
    public bool AllowStale { get; init; } = true;
    public bool RequireFreshSnapshots { get => !AllowStale; init => AllowStale = !value; }
    public bool RequireTradeabilityFacts { get; init; } = false;
    public bool RequireTradeability { get => RequireTradeabilityFacts; init => RequireTradeabilityFacts = value; }
    public IReadOnlyDictionary<string, UnderpricedItemTradeability> Tradeability { get; init; } =
        new Dictionary<string, UnderpricedItemTradeability>(StringComparer.OrdinalIgnoreCase);

    public void Validate()
    {
        Market.Validate();
        Policy.Validate();
        if (MaximumSnapshotAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(MaximumSnapshotAge));
    }
}

/// <summary>Bounded own-shop query input. It contains observations, not commands.</summary>
public record ShopQuery
{
    public IReadOnlyCollection<ShopObservation> Observations { get; init; } = Array.Empty<ShopObservation>();
    public IReadOnlyCollection<string> ItemIds { get; init; } = Array.Empty<string>();
    public bool IncludeHeld { get; init; } = true;
    public bool IncludeRejected { get => IncludeHeld; init => IncludeHeld = value; }
    public int Limit { get; init; } = 100;
    public string? Cursor { get; init; }
    public DateTimeOffset? AsOf { get; init; }
    public TimeSpan? MaximumAge { get; init; }
    public IReadOnlyDictionary<string, UnderpricedItemTradeability>? Tradeability { get; init; }

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
    }
}

public sealed record ShopQueryRequest : ShopQuery;

public enum CoreShopDecision
{
    Unknown,
    Reprice,
    Keep,
    Review,
    Hold,
    Reject
}

/// <summary>LibKolibri-compatible projection of one own-shop recommendation.</summary>
public sealed record CoreShopCandidate
{
    public string ItemId { get; init; } = string.Empty;
    public string? DisplayName { get; init; }
    public long CurrentPrice { get; init; }
    public long? RecommendedPrice { get; init; }
    public int Quantity { get; init; }
    public decimal? ReferencePrice { get; init; }
    public decimal? FloorPrice { get; init; }
    public decimal? ExpectedNetProceeds { get; init; }
    public decimal? MarginPercent { get; init; }
    public bool IsTradeable { get; init; }
    public bool IsAccountBound { get; init; }
    public bool IsStale { get; init; }
    public CoreShopDecision Decision { get; init; } = CoreShopDecision.Unknown;
    public bool RequiresApproval { get; init; }
    public bool IsPreviewOnly { get; init; } = true;
    public IReadOnlyList<string> ReasonCodes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();
    public DateTimeOffset ObservedAt { get; init; }
    public CoreSnapshotState Snapshot { get; init; } = new();

    public long? ProposedPrice => RecommendedPrice;
    public long? TargetPrice => RecommendedPrice;
    public long CurrentAsk => CurrentPrice;
    public bool ShouldReprice => Decision == CoreShopDecision.Reprice;
}

public sealed record CoreShopQueryResult
{
    public CoreQueryState State { get; init; } = CoreQueryState.Unknown;
    public IReadOnlyList<CoreShopCandidate> Candidates { get; init; } = Array.Empty<CoreShopCandidate>();
    public IReadOnlyList<CoreShopCandidate> Recommendations => Candidates;
    public CoreSnapshotState Snapshot { get; init; } = new();
    public IReadOnlyList<CoreQueryDiagnostic> Diagnostics { get; init; } = Array.Empty<CoreQueryDiagnostic>();
    public string? NextCursor { get; init; }
    public bool IsStale => Snapshot.IsStale || State == CoreQueryState.Stale;
    public bool Succeeded => State is CoreQueryState.Ready or CoreQueryState.Empty or CoreQueryState.Stale;
}

/// <summary>Read-only service composing market estimation and repricing.</summary>
public class CoreShopQueryService
{
    private readonly MarketPriceEstimator _estimator;
    private readonly RepricePlanner _planner;
    private readonly ShopQueryOptions _defaults;
    private readonly TimeProvider _clock;

    public CoreShopQueryService(
        MarketPriceEstimator? estimator = null,
        RepricePlanner? planner = null,
        ShopQueryOptions? options = null,
        TimeProvider? clock = null)
    {
        _defaults = options ?? new ShopQueryOptions();
        _defaults.Validate();
        _clock = clock ?? TimeProvider.System;
        _estimator = estimator ?? new MarketPriceEstimator(_defaults.Market);
        _planner = planner ?? new RepricePlanner(_defaults.Policy, _clock);
    }

    public MarketPriceEstimator Estimator => _estimator;
    public RepricePlanner Planner => _planner;
    public ShopQueryOptions Options => _defaults;

    public CoreShopQueryResult Query(
        IEnumerable<ShopObservation> observations,
        ShopQuery? query = null,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(observations);
        query ??= new ShopQuery();
        query.Validate();
        var options = BuildOptions(query);
        var timestamp = now ?? query.AsOf ?? _clock.GetUtcNow();
        var selected = SelectObservations(observations, query).ToArray();
        if (selected.Length == 0)
        {
            return new CoreShopQueryResult
            {
                State = CoreQueryState.Empty,
                Snapshot = new CoreSnapshotState { Freshness = SnapshotFreshness.Missing, ObservedAt = timestamp },
                Diagnostics = new[] { new CoreQueryDiagnostic("empty", "No shop observations matched the query.") }
            };
        }

        var rows = new List<CoreShopCandidate>();
        var staleAny = false;
        foreach (var group in selected.GroupBy(x => x.ItemId.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var representative = group.OrderByDescending(x => x.Snapshot.FetchedAt).First();
            var estimate = _estimator.Estimate(representative.Snapshot, timestamp);
            var stale = IsStale(representative.Snapshot, timestamp, options.MaximumSnapshotAge) || estimate.IsStale;
            staleAny |= stale;
            var facts = ResolveTradeability(group.Key, group.Select(x => x.Tradeability).FirstOrDefault(x => x is not null), query.Tradeability, options.Tradeability);
            var listings = group.Select(x => x.Listing).ToArray();
            var plan = _planner.Plan(listings, estimate, timestamp);
            foreach (var recommendation in plan.Recommendations)
            {
                var codes = recommendation.ReasonCodes.Select(x => x.ToString()).ToList();
                var reasons = recommendation.ReasonTexts.ToList();
                var decision = recommendation.Status switch
                {
                    RepriceRecommendationStatus.Ready => CoreShopDecision.Reprice,
                    RepriceRecommendationStatus.Keep => CoreShopDecision.Keep,
                    RepriceRecommendationStatus.Review => CoreShopDecision.Review,
                    _ => CoreShopDecision.Hold
                };
                if (stale)
                {
                    AddReason(codes, reasons, "stale_snapshot", "Mall or historical price data is stale.");
                    if (!options.AllowStale || decision == CoreShopDecision.Reprice)
                        decision = CoreShopDecision.Hold;
                }
                if (facts is null)
                {
                    if (options.RequireTradeabilityFacts)
                    {
                        AddReason(codes, reasons, "tradeability_unknown", "No tradeability facts were supplied.");
                        decision = CoreShopDecision.Hold;
                    }
                }
                else if (!facts.IsTradeable || facts.IsAccountBound || !facts.CanSell)
                {
                    AddReason(codes, reasons, "non_tradeable", facts.Reason ?? "Item cannot be sold in the Mall.");
                    decision = CoreShopDecision.Hold;
                }
                if (!query.IncludeHeld && decision is CoreShopDecision.Hold or CoreShopDecision.Review)
                    continue;
                rows.Add(new CoreShopCandidate
                {
                    ItemId = recommendation.ItemId,
                    DisplayName = recommendation.DisplayName,
                    CurrentPrice = recommendation.CurrentPrice,
                    RecommendedPrice = recommendation.SuggestedPrice,
                    Quantity = recommendation.Quantity,
                    ReferencePrice = recommendation.ReferencePrice,
                    FloorPrice = recommendation.FloorPrice,
                    ExpectedNetProceeds = recommendation.NetProceeds,
                    MarginPercent = recommendation.ReferencePrice is > 0
                        ? (recommendation.NetProceeds - recommendation.CurrentPrice * recommendation.Quantity) /
                          (recommendation.CurrentPrice * recommendation.Quantity)
                        : null,
                    IsTradeable = facts?.IsTradeable ?? false,
                    IsAccountBound = facts?.IsAccountBound ?? false,
                    IsStale = stale,
                    Decision = decision,
                    RequiresApproval = decision == CoreShopDecision.Reprice,
                    ReasonCodes = codes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    Reasons = reasons.Distinct(StringComparer.Ordinal).ToArray(),
                    ObservedAt = recommendation.MarketAsOf ?? representative.Snapshot.FetchedAt,
                    Snapshot = new CoreSnapshotState
                    {
                        Freshness = stale ? SnapshotFreshness.Stale : SnapshotFreshness.Fresh,
                        ObservedAt = recommendation.MarketAsOf ?? representative.Snapshot.FetchedAt,
                        Source = representative.Snapshot.MallSourceUri?.ToString()
                    }
                });
            }
        }

        var visible = rows
            .OrderByDescending(x => x.Decision == CoreShopDecision.Reprice)
            .ThenBy(x => x.ItemId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.CurrentPrice)
            .Take(query.Limit)
            .ToArray();
        return new CoreShopQueryResult
        {
            State = staleAny ? CoreQueryState.Stale : visible.Length == 0 ? CoreQueryState.Empty : CoreQueryState.Ready,
            Candidates = visible,
            Snapshot = new CoreSnapshotState { Freshness = staleAny ? SnapshotFreshness.Stale : SnapshotFreshness.Fresh, ObservedAt = timestamp },
            Diagnostics = staleAny
                ? new[] { new CoreQueryDiagnostic("stale_snapshot", "One or more shop observations are stale.") }
                : Array.Empty<CoreQueryDiagnostic>(),
            NextCursor = rows.Count > visible.Length ? (query.Cursor ?? string.Empty) : null
        };
    }

    public CoreShopQueryResult Evaluate(IEnumerable<ShopObservation> observations, ShopQuery? query = null, DateTimeOffset? now = null)
        => Query(observations, query, now);

    public CoreShopQueryResult Query(
        OwnShopListing listing,
        PriceSnapshot snapshot,
        ShopQuery? query = null,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentNullException.ThrowIfNull(snapshot);
        return Query(new[] { new ShopObservation(listing, snapshot) }, query, now);
    }

    public CoreShopQueryResult Query(
        IEnumerable<OwnShopListing> listings,
        IReadOnlyDictionary<string, PriceSnapshot> snapshots,
        ShopQuery? query = null,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(listings);
        ArgumentNullException.ThrowIfNull(snapshots);
        var observations = listings
            .Where(x => x is not null && snapshots.ContainsKey(x.ItemId))
            .Select(x => new ShopObservation(x, snapshots[x.ItemId]));
        return Query(observations, query, now);
    }

    public Task<CoreShopQueryResult> QueryAsync(
        IEnumerable<ShopObservation> observations,
        ShopQuery? query = null,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Query(observations, query, now));
    }

    public Task<CoreShopQueryResult> QueryAsync(
        OwnShopListing listing,
        PriceSnapshot snapshot,
        ShopQuery? query = null,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Query(listing, snapshot, query, now));
    }

    public Task<CoreShopQueryResult> GetCandidatesAsync(
        IEnumerable<ShopObservation> observations,
        ShopQuery? query = null,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
        => QueryAsync(observations, query, now, cancellationToken);

    private ShopQueryOptions BuildOptions(ShopQuery query)
    {
        var options = _defaults;
        if (query.MaximumAge is { } age) options = options with { MaximumSnapshotAge = age };
        if (query.Tradeability is not null) options = options with { Tradeability = query.Tradeability };
        options.Validate();
        return options;
    }

    private static IEnumerable<ShopObservation> SelectObservations(IEnumerable<ShopObservation> observations, ShopQuery query)
    {
        var ids = query.ItemIds.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return observations.Where(x => x is not null)
            .Where(x => ids.Count == 0 || ids.Contains(x.ItemId))
            .GroupBy(x => x.ItemId.Trim(), StringComparer.OrdinalIgnoreCase)
            .SelectMany(x => x);
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
        => snapshot.IsStale || now - snapshot.FetchedAt > maxAge || now - snapshot.PricegunFetchedAt > maxAge;

    private static void AddReason(List<string> codes, List<string> reasons, string code, string reason)
    {
        if (!codes.Contains(code, StringComparer.OrdinalIgnoreCase)) codes.Add(code);
        if (!reasons.Contains(reason, StringComparer.Ordinal)) reasons.Add(reason);
    }
}

/// <summary>Compatibility spelling for hosts that omit the Core prefix.</summary>
public sealed class ShopQueryService : CoreShopQueryService
{
    public ShopQueryService(
        MarketPriceEstimator? estimator = null,
        RepricePlanner? planner = null,
        ShopQueryOptions? options = null,
        TimeProvider? clock = null)
        : base(estimator, planner, options, clock)
    {
    }
}
