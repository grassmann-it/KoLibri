namespace KolArbitrage.Core.Daily;

/// <summary>
/// Read-only inputs for the Bacon arbitrage planner. The planner never
/// performs an HTTP request; it decides whether to buy more Infinite Bacon
/// Machines given the current mall price, the historical Pricegun median,
/// and the player's meat budget.
/// </summary>
public sealed record BaconSnapshot
{
    public int OwnedMachines { get; init; }
    public long MallPricePerMachine { get; init; }
    public long PricegunMedianSellPrice { get; init; }
    public long AvailableMeat { get; init; }
    /// <summary>Minimum profit per machine the planner requires before buying.</summary>
    public long MinimumProfitPerUnit { get; init; } = 50_000;
    /// <summary>Hard cap the planner refuses to exceed even when the spread is positive.</summary>
    public int MaximumBuyQuantity { get; init; } = 10;
}

/// <summary>
/// Result of the Bacon arbitrage planner.
/// </summary>
public sealed record BaconPlan
{
    public required bool ShouldBuy { get; init; }
    public required string Verdict { get; init; }
    public required int SuggestedQuantity { get; init; }
    public required long ExpectedProfitPerUnit { get; init; }
    public required string Rationale { get; init; }
    public required BaconSnapshot Snapshot { get; init; }
}

/// <summary>
/// Read-only planner for "buy Infinite Bacon Machine from the mall and flip
/// it on the resale market". Refuses any purchase when the spread is not
/// positive or the meat reserve would be exhausted.
/// </summary>
public sealed class BaconArbitragePlanner
{
    public BaconArbitragePlannerOptions Options { get; }

    public BaconArbitragePlanner(BaconArbitragePlannerOptions? options = null)
        => Options = options ?? new BaconArbitragePlannerOptions();

    public BaconPlan Plan(BaconSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var profitPerUnit = snapshot.PricegunMedianSellPrice - snapshot.MallPricePerMachine;
        var affordable = snapshot.MallPricePerMachine * Math.Max(1, Options.MinimumBatchSize)
                          <= Math.Max(0, snapshot.AvailableMeat - Options.MeatReserveAfterBacons);

        if (snapshot.PricegunMedianSellPrice <= 0 || snapshot.MallPricePerMachine <= 0)
        {
            return new BaconPlan
            {
                ShouldBuy = false,
                Verdict = "UnknownPrice",
                SuggestedQuantity = 0,
                ExpectedProfitPerUnit = profitPerUnit,
                Rationale = "No comparable bacon prices available; skipping purchase.",
                Snapshot = snapshot
            };
        }

        if (profitPerUnit < snapshot.MinimumProfitPerUnit)
        {
            return new BaconPlan
            {
                ShouldBuy = false,
                Verdict = "NoSpread",
                SuggestedQuantity = 0,
                ExpectedProfitPerUnit = profitPerUnit,
                Rationale = $"Profit {profitPerUnit} below minimum {snapshot.MinimumProfitPerUnit}.",
                Snapshot = snapshot
            };
        }

        if (!affordable)
        {
            return new BaconPlan
            {
                ShouldBuy = false,
                Verdict = "OverBudget",
                SuggestedQuantity = 0,
                ExpectedProfitPerUnit = profitPerUnit,
                Rationale = $"Mall price {snapshot.MallPricePerMachine} leaves the meat reserve under {Options.MeatReserveAfterBacons}.",
                Snapshot = snapshot
            };
        }

        var maxAffordable = Math.Max(0,
            (int)Math.Min(snapshot.MaximumBuyQuantity,
                (snapshot.AvailableMeat - Options.MeatReserveAfterBacons) / Math.Max(1, snapshot.MallPricePerMachine)));

        var quantity = Math.Min(snapshot.MaximumBuyQuantity, maxAffordable);
        return new BaconPlan
        {
            ShouldBuy = quantity > 0,
            Verdict = quantity > 0 ? "BuyBacons" : "NoQuantity",
            SuggestedQuantity = quantity,
            ExpectedProfitPerUnit = profitPerUnit,
            Rationale = $"Spread positive ({profitPerUnit}); buying {quantity} machine(s).",
            Snapshot = snapshot
        };
    }
}

/// <summary>
/// Tunable thresholds for <see cref="BaconArbitragePlanner"/>.
/// </summary>
public sealed record BaconArbitragePlannerOptions
{
    public long MeatReserveAfterBacons { get; init; } = 200_000;
    public int MinimumBatchSize { get; init; } = 1;
}
