
namespace KolArbitrage.Core.Spiel;

/// <summary>One candidate purchase: a mall listing that is cheap enough to
/// buy for the restock.</summary>
public sealed record RestockCandidate
{
    /// <summary>KoL's numeric store id.</summary>
    public required int StoreId { get; init; }
    public required int ItemId { get; init; }
    public required string ItemName { get; init; }
    public required long UnitPrice { get; init; }
    public required int AvailableQuantity { get; init; }
    public long TotalPrice => UnitPrice * Math.Max(1, AvailableQuantity);
}

/// <summary>Inputs for the restock decision.</summary>
public sealed record RestockSnapshot
{
    /// <summary>Cheapest listing for the item, or null when nothing is listed.</summary>
    public RestockCandidate? CheapestListing { get; init; }

    /// <summary>Meat the buying account holds.</summary>
    public long AvailableMeat { get; init; }

    /// <summary>Meat the buying account must keep in reserve.</summary>
    public long MeatReserve { get; init; } = 200_000;

    /// <summary>How many of the item the account already holds.</summary>
    public int OwnedQuantity { get; init; }

    /// <summary>How many more the account wants in total.</summary>
    public int TargetQuantity { get; init; }

    /// <summary>Highest price the operator is willing to pay per unit.</summary>
    public long MaximumUnitPrice { get; init; }
}

/// <summary>Why the restock did or didn't happen.</summary>
public sealed record RestockPlan
{
    public required string Verdict { get; init; }
    public required bool ShouldBuy { get; init; }
    public required int Quantity { get; init; }
    public required long EstimatedSpend { get; init; }
    public required string Rationale { get; init; }
    public RestockCandidate? Candidate { get; init; }
}

/// <summary>
/// Decides whether to buy more of an item on the mall so the main account
/// has enough for the daily routine. The planner is read-only: it picks the
/// cheapest listing that fits the price ceiling and the meat reserve, and
/// caps the quantity at what the listing offers and what the target wants.
///
/// Because both accounts may buy, the caller runs the planner per role —
/// each account has its own daily shop limit and its own meat pool.
/// </summary>
public sealed class RestockPlanner
{
    /// <summary>KoL item ids that cannot be bought on the mall. The
    /// inflammable leaf (11341) is the important one: it is only obtained
    /// from the campfire, so a restock plan for it can never be fulfilled.
    /// </summary>
    public static IReadOnlySet<int> UnbuyableItems { get; } =
        new HashSet<int> { 11341 };

    public RestockPlan Plan(RestockSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var candidate = snapshot.CheapestListing;
        if (candidate is not null && UnbuyableItems.Contains(candidate.ItemId))
        {
            return new RestockPlan
            {
                Verdict = "NotBuyable",
                ShouldBuy = false,
                Quantity = 0,
                EstimatedSpend = 0,
                Rationale = $"Item {candidate.ItemId} cannot be bought on the mall.",
                Candidate = candidate
            };
        }
        if (candidate is null)
        {
            return new RestockPlan
            {
                Verdict = "NoListing",
                ShouldBuy = false,
                Quantity = 0,
                EstimatedSpend = 0,
                Rationale = "No listing for the item on the mall."
            };
        }
        if (candidate.UnitPrice <= 0)
        {
            return new RestockPlan
            {
                Verdict = "InvalidPrice",
                ShouldBuy = false,
                Quantity = 0,
                EstimatedSpend = 0,
                Rationale = "The cheapest listing has no usable price.",
                Candidate = candidate
            };
        }
        if (snapshot.MaximumUnitPrice > 0 && candidate.UnitPrice > snapshot.MaximumUnitPrice)
        {
            return new RestockPlan
            {
                Verdict = "TooExpensive",
                ShouldBuy = false,
                Quantity = 0,
                EstimatedSpend = 0,
                Rationale = $"Cheapest listing costs {candidate.UnitPrice} meat, ceiling is {snapshot.MaximumUnitPrice}.",
                Candidate = candidate
            };
        }
        var wanted = snapshot.TargetQuantity - snapshot.OwnedQuantity;
        if (wanted <= 0)
        {
            return new RestockPlan
            {
                Verdict = "TargetMet",
                ShouldBuy = false,
                Quantity = 0,
                EstimatedSpend = 0,
                Rationale = $"Already holding {snapshot.OwnedQuantity} of {snapshot.TargetQuantity} target.",
                Candidate = candidate
            };
        }
        var affordable = candidate.AvailableQuantity <= 0 ? 1 : candidate.AvailableQuantity;
        var quantity = Math.Min(wanted, affordable);
        var spend = candidate.UnitPrice * quantity;
        if (snapshot.AvailableMeat - spend < snapshot.MeatReserve)
        {
            // Reduce the quantity until the reserve holds, or bail out.
            var affordableCount = (int)Math.Max(0,
                (snapshot.AvailableMeat - snapshot.MeatReserve) / candidate.UnitPrice);
            quantity = Math.Min(quantity, affordableCount);
            spend = candidate.UnitPrice * quantity;
            if (quantity <= 0)
            {
                return new RestockPlan
                {
                    Verdict = "BudgetTooLow",
                    ShouldBuy = false,
                    Quantity = 0,
                    EstimatedSpend = 0,
                    Rationale = $"Buying would drop the account below its {snapshot.MeatReserve} meat reserve.",
                    Candidate = candidate
                };
            }
        }
        return new RestockPlan
        {
            Verdict = "Buy",
            ShouldBuy = true,
            Quantity = quantity,
            EstimatedSpend = spend,
            Rationale = $"Buying {quantity} at {candidate.UnitPrice} meat keeps {snapshot.AvailableMeat - spend} meat in reserve.",
            Candidate = candidate
        };
    }
}
