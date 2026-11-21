using KoLibri.Core.Spiel;

namespace KoLibri.Core.Markt;

/// <summary>
/// Produces explainable aging and excess recommendations from inventory and a
/// local sales ledger. This planner has no mutation capability.
/// </summary>
public class InventoryAgingPlanner
{
    private readonly InventoryAgingOptions _options;
    private readonly InventoryVelocityCalculator _velocity;

    public InventoryAgingPlanner(InventoryAgingOptions? options = null)
    {
        _options = options ?? new InventoryAgingOptions();
        _options.Validate();
        _velocity = new InventoryVelocityCalculator(_options.LookbackWindow);
    }

    public InventoryAgingOptions Options => _options;

    public InventoryAgingResult Analyze(
        InventoryAgingItem item,
        IEnumerable<ShopSale>? sales,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        var timestamp = now ?? DateTimeOffset.UtcNow;
        var quantity = Math.Max(0, item.Quantity);
        var reserve = Math.Clamp(item.ReserveQuantity, 0, quantity);
        var normalized = item with
        {
            ItemId = item.ItemId.Trim(),
            DisplayName = item.DisplayName.Trim(),
            Quantity = quantity,
            ReserveQuantity = reserve,
            CostBasis = NormalizePrice(item.CostBasis),
            CurrentListingPrice = NormalizePrice(item.CurrentListingPrice),
            MarketReferencePrice = NormalizePrice(item.MarketReferencePrice)
        };
        var velocity = _velocity.Calculate(normalized, sales, timestamp);
        var (age, ageDays, ageBucket) = GetAge(normalized, timestamp);

        var targetDemand = velocity.UnitsPerDay > 0m
            ? CeilingToInt(velocity.UnitsPerDay * _options.TargetDaysOfSupply)
            : 0;
        var targetQuantity = Math.Max(reserve, targetDemand);
        var excessQuantity = Math.Max(0, quantity - targetQuantity);

        var (action, recommendedQuantity, recommendedPrice, rationale) = ChooseAction(
            normalized,
            velocity,
            ageBucket,
            ageDays,
            targetQuantity,
            excessQuantity);

        return new InventoryAgingResult
        {
            ItemId = normalized.ItemId,
            DisplayName = normalized.DisplayName,
            Quantity = quantity,
            GeneratedAt = timestamp,
            Age = age,
            AgeDays = ageDays,
            AgeBucket = ageBucket,
            Velocity = velocity,
            TargetQuantity = targetQuantity,
            ExcessQuantity = excessQuantity,
            RecommendedAction = action,
            RecommendedQuantity = recommendedQuantity,
            RecommendedPrice = recommendedPrice,
            CostBasis = normalized.CostBasis,
            CurrentListingPrice = normalized.CurrentListingPrice,
            MarketReferencePrice = normalized.MarketReferencePrice,
            SuggestedActions = new[] { action },
            Rationale = rationale
        };
    }

    public InventoryAgingResult Analyze(
        InventoryItemSnapshot snapshot,
        IEnumerable<ShopSale>? sales,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var input = new InventoryAgingItem(snapshot.ItemId, snapshot.DisplayName, snapshot.OwnedQuantity)
        {
            ObservedAt = snapshot.AsOf,
            CostBasis = snapshot.CostBasis,
            CurrentListingPrice = snapshot.CurrentListingPrice,
            MarketReferencePrice = snapshot.MarketReferencePrice ?? snapshot.LowestMarketPrice
        };
        return Analyze(input, sales, now);
    }

    public InventoryAgingResult Analyze(
        KoLInventoryItem item,
        IEnumerable<ShopSale>? sales,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Analyze(new InventoryAgingItem(item.ItemId, item.Name, item.Quantity), sales, now);
    }

    public IReadOnlyList<InventoryAgingResult> AnalyzeMany(
        IEnumerable<InventoryAgingItem> items,
        IEnumerable<ShopSale>? sales,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        var ledger = sales?.ToArray() ?? Array.Empty<ShopSale>();
        var timestamp = now ?? DateTimeOffset.UtcNow;
        return items.Select(item => Analyze(item, ledger, timestamp)).ToArray();
    }

    public IReadOnlyList<InventoryAgingResult> AnalyzeMany(
        IEnumerable<InventoryItemSnapshot> snapshots,
        IEnumerable<ShopSale>? sales,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var ledger = sales?.ToArray() ?? Array.Empty<ShopSale>();
        var timestamp = now ?? DateTimeOffset.UtcNow;
        return snapshots.Select(item => Analyze(item, ledger, timestamp)).ToArray();
    }

    public IReadOnlyList<InventoryAgingResult> AnalyzeMany(
        IEnumerable<KoLInventoryItem> items,
        IEnumerable<ShopSale>? sales,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        return AnalyzeMany(items.Select(item => new InventoryAgingItem(item.ItemId, item.Name, item.Quantity)), sales, now);
    }

    public InventoryExcessPlan BuildPlan(
        IEnumerable<InventoryAgingItem> items,
        IEnumerable<ShopSale>? sales,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        var timestamp = now ?? DateTimeOffset.UtcNow;
        var results = AnalyzeMany(items, sales, timestamp)
            .OrderByDescending(x => ActionPriority(x.RecommendedAction))
            .ThenByDescending(x => x.ExcessQuantity)
            .ThenByDescending(x => x.AgeDays ?? decimal.MinValue)
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.ItemId, StringComparer.Ordinal)
            .ToArray();
        return new InventoryExcessPlan(timestamp, results);
    }

    public InventoryExcessPlan BuildPlan(
        IEnumerable<InventoryItemSnapshot> snapshots,
        IEnumerable<ShopSale>? sales,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var timestamp = now ?? DateTimeOffset.UtcNow;
        var inputs = snapshots.Select(snapshot => new InventoryAgingItem(
            snapshot.ItemId,
            snapshot.DisplayName,
            snapshot.OwnedQuantity)
        {
            ObservedAt = snapshot.AsOf,
            CostBasis = snapshot.CostBasis,
            CurrentListingPrice = snapshot.CurrentListingPrice,
            MarketReferencePrice = snapshot.MarketReferencePrice ?? snapshot.LowestMarketPrice
        });
        return BuildPlan(inputs, sales, timestamp);
    }

    public InventoryExcessPlan BuildPlan(
        IEnumerable<KoLInventoryItem> items,
        IEnumerable<ShopSale>? sales,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        return BuildPlan(items.Select(item => new InventoryAgingItem(item.ItemId, item.Name, item.Quantity)), sales, now);
    }

    // Plan is a convenient spelling used by workers that treat this as a
    // general planner rather than a builder.
    public InventoryExcessPlan Plan(
        IEnumerable<InventoryAgingItem> items,
        IEnumerable<ShopSale>? sales,
        DateTimeOffset? now = null)
        => BuildPlan(items, sales, now);

    public InventoryExcessPlan Build(
        IEnumerable<InventoryAgingItem> items,
        IEnumerable<ShopSale>? sales,
        DateTimeOffset? now = null)
        => BuildPlan(items, sales, now);

    private (InventoryAction Action, int Quantity, decimal? Price, string Rationale) ChooseAction(
        InventoryAgingItem item,
        InventoryVelocity velocity,
        InventoryAgeBucket ageBucket,
        decimal? ageDays,
        int targetQuantity,
        int excessQuantity)
    {
        if (item.Quantity <= 0)
            return (InventoryAction.Hold, 0, null, "No stock available; no action planned.");

        var stale = ageBucket is InventoryAgeBucket.Stale or InventoryAgeBucket.Critical;
        var noVelocity = velocity.IsZeroVelocity;
        var verySlow = velocity.DaysOfSupply is { } dos && dos >= _options.LiquidateDaysOfSupply;

        // Old zero-velocity stock has the highest capital and space cost. A
        // reserve is retained through ExcessQuantity, so liquidation is still
        // bounded and reviewable.
        if (excessQuantity > 0 && stale && (noVelocity || verySlow))
            return (InventoryAction.Liquidate, excessQuantity, null,
                $"{ageBucket} stock without sufficient throughput; {excessQuantity} units to liquidate above the reserve.");

        if (excessQuantity > 0 && item.Craftable && item.CraftValue is > 0m)
            return (InventoryAction.Craft, excessQuantity, null,
                $"{excessQuantity} units exceed the target stock and have a positive craft value of {item.CraftValue:0.##} meat per unit.");

        var market = item.MarketReferencePrice;
        var current = item.CurrentListingPrice;
        if (market is > 0m && current is > 0m
            && current.Value >= market.Value * (1m + _options.RepriceMarkupThreshold))
        {
            return (InventoryAction.Reprice, Math.Max(1, excessQuantity), market,
                $"Listing at {current:0.##} meat is at least {_options.RepriceMarkupThreshold:P0} above the market reference of {market:0.##} meat.");
        }

        var canRotate = item.CanRotate && excessQuantity > 0;
        var enoughSupply = velocity.DaysOfSupply is { } supply
            && supply >= _options.RotateDaysOfSupply;
        if (canRotate && (enoughSupply || stale))
        {
            var rotateQuantity = Math.Min(1, excessQuantity);
            return (InventoryAction.Rotate, rotateQuantity, market,
                $"Propose {rotateQuantity} units as a time-limited rotation from a surplus of {excessQuantity} units.");
        }

        var ageText = ageDays is null ? "unbekanntem Alter" : $"{ageDays:0.#} Tagen Alter";
        var velocityText = noVelocity ? "no observed sales" : $"{velocity.UnitsPerDay:0.##} units/day";
        return (InventoryAction.Hold, 0, null,
            $"Stock with {ageText} and {velocityText} within the configured hold values (target stock {targetQuantity}).");
    }

    private (TimeSpan? Age, decimal? AgeDays, InventoryAgeBucket Bucket) GetAge(
        InventoryAgingItem item,
        DateTimeOffset now)
    {
        if (item.AgeDays is { } suppliedAge)
        {
            var suppliedDays = Math.Max(0m, suppliedAge);
            var suppliedAgeSpan = TimeSpan.FromDays(Math.Min((double)suppliedDays, TimeSpan.MaxValue.TotalDays));
            var suppliedBucket = suppliedDays <= (decimal)_options.FreshAge.TotalDays
                ? InventoryAgeBucket.Fresh
                : suppliedDays <= (decimal)_options.AgingAge.TotalDays
                    ? InventoryAgeBucket.Aging
                    : suppliedDays <= (decimal)_options.StaleAge.TotalDays
                        ? InventoryAgeBucket.Stale
                        : InventoryAgeBucket.Critical;
            return (suppliedAgeSpan, suppliedDays, suppliedBucket);
        }

        var acquired = item.AcquiredAt ?? item.FirstSeenAt;
        if (acquired is null)
            return (null, null, InventoryAgeBucket.Unknown);

        var age = now - acquired.Value;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        var days = (decimal)age.TotalDays;
        var bucket = days <= (decimal)_options.FreshAge.TotalDays
            ? InventoryAgeBucket.Fresh
            : days <= (decimal)_options.AgingAge.TotalDays
                ? InventoryAgeBucket.Aging
                : days <= (decimal)_options.StaleAge.TotalDays
                    ? InventoryAgeBucket.Stale
                    : InventoryAgeBucket.Critical;
        return (age, days, bucket);
    }

    private static int ActionPriority(InventoryAction action) => action switch
    {
        InventoryAction.Liquidate => 5,
        InventoryAction.Craft => 4,
        InventoryAction.Rotate => 3,
        InventoryAction.Reprice => 2,
        _ => 1
    };

    private static decimal? NormalizePrice(decimal? value)
        => value is > 0m ? value : null;

    private static int CeilingToInt(decimal value)
    {
        if (value <= 0m) return 0;
        return value >= int.MaxValue ? int.MaxValue : decimal.ToInt32(decimal.Ceiling(value));
    }
}

/// <summary>Compatibility name for workers that focus on excess stock.</summary>
public sealed class InventoryExcessPlanner : InventoryAgingPlanner
{
    public InventoryExcessPlanner(InventoryAgingOptions? options = null)
        : base(options)
    {
    }
}

/// <summary>Compatibility name for callers that only need an aging analysis.</summary>
public sealed class InventoryAgingAnalyzer : InventoryAgingPlanner
{
    public InventoryAgingAnalyzer(InventoryAgingOptions? options = null)
        : base(options)
    {
    }
}

/// <summary>Short compatibility name for application workers.</summary>
public sealed class InventoryPlanner : InventoryAgingPlanner
{
    public InventoryPlanner(InventoryAgingOptions? options = null)
        : base(options)
    {
    }
}

/// <summary>Short compatibility name for excess-only application workers.</summary>
public sealed class ExcessInventoryPlanner : InventoryAgingPlanner
{
    public ExcessInventoryPlanner(InventoryAgingOptions? options = null)
        : base(options)
    {
    }
}
