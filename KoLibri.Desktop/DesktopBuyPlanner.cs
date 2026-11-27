using System.Globalization;

// Copied from KoLibri.Core (0.5 step 3): the arbitrage table's buy rules and planner (Spiel/Daily/BuyPlanner.cs and
// MallListingRow from PriceScanner.cs). The desktop owns them now; the library has no buy-rule planner.
namespace KoLibri.Desktop;

/// <summary>
/// The rule for one item: how deep below the market price a listing has
/// to be before it is bought.
/// </summary>
public sealed record BuyRule
{
    /// <summary>The item's id.</summary>
    public required int ItemId { get; init; }
    /// <summary>The item's name.</summary>
    public required string ItemName { get; init; }
    /// <summary>
    /// The highest price the item is bought at. Zero means "no ceiling" —
    /// the rule then only applies the other conditions.
    /// </summary>
    public int MaximumPrice { get; init; }
    /// <summary>
    /// The price the item is expected to fetch again. A listing below it
    /// is an arbitrage candidate.
    /// </summary>
    public int ResalePrice { get; init; }
    /// <summary>
    /// Whether a listing with a daily limit is bought. A seller that sells
    /// one per day is still worth buying — the limit only slows the flip.
    /// </summary>
    public bool BuyDespiteDailyLimit { get; init; } = true;
    /// <summary>How many at most per day across all listings.</summary>
    public int MaximumPerDay { get; init; } = 40;
    /// <summary>Meat the buying account keeps in reserve.</summary>
    public long MeatReserve { get; init; } = 1_000_000;

    /// <summary>
    /// Whether the rule buys a listing at the given price.
    /// </summary>
    public bool Accepts(int price, bool hasDailyLimit)
    {
        if (price <= 0) return false;
        if (MaximumPrice > 0 && price > MaximumPrice) return false;
        if (hasDailyLimit && !BuyDespiteDailyLimit) return false;
        return true;
    }
}

/// <summary>One listing the planner wants to buy.</summary>
public sealed record BuyCandidate
{
    public required int StoreId { get; init; }
    public required string StoreName { get; init; }
    public required int ItemId { get; init; }
    /// <summary>The item's name.</summary>
    public required string ItemName { get; init; }
    public required int UnitPrice { get; init; }
    public required int Quantity { get; init; }
    /// <summary>The seller's daily limit, if there is one.</summary>
    public int DailyLimit { get; init; }
    public bool HasDailyLimit => DailyLimit > 0;
    /// <summary>The profit per unit against the resale price.</summary>
    public int ProfitPerUnit { get; init; }
    /// <summary>The profit over the whole purchase.</summary>
    public long TotalProfit { get; init; }
    /// <summary>Why the planner wants this listing.</summary>
    public string Reason { get; init; } = string.Empty;
}

/// <summary>What the planner decided for one item.</summary>
public sealed record BuyPlan
{
    public required int ItemId { get; init; }
    public required string ItemName { get; init; }
    public required IReadOnlyList<BuyCandidate> Candidates { get; init; }
    /// <summary>The meat the plan spends.</summary>
    public long TotalCost { get; init; }
    /// <summary>The profit the plan expects.</summary>
    public long TotalProfit { get; init; }
    public required string Rationale { get; init; }
    public bool HasCandidates => Candidates.Count > 0;
}

/// <summary>
/// Plans the purchases for the arbitrage items.
///
/// The rules come from the operator: the 11-leaf clover is bought at
/// every cheap listing, because the arbitrage runs towards 26.900 meat —
/// even a listing with a daily limit of one per day is worth taking.
/// The foreign bribes are bought under 20.000, 22.000 and 25.000 meat,
/// always; the ones above are not worth the meat they tie up.
/// </summary>
public sealed class BuyPlanner
{
    /// <summary>
    /// The rules the operator wants: the clover is bought at every price
    /// below the ceiling, the bribes under 20.000 meat.
    /// </summary>
    public static IReadOnlyList<BuyRule> DefaultRules { get; } = new[]
    {
        new BuyRule
        {
            ItemId = 10881,
            ItemName = "11-leaf clover",
            MaximumPrice = 26_900,
            ResalePrice = 32_995,
            BuyDespiteDailyLimit = true,
            MaximumPerDay = 40
        },
        new BuyRule
        {
            ItemId = 8691,
            ItemName = "bag of foreign bribes",
            MaximumPrice = 20_000,
            ResalePrice = 27_000,
            BuyDespiteDailyLimit = true,
            MaximumPerDay = 40
        }
    };

    /// <summary>
    /// Plans the purchases for one item.
    /// </summary>
    public BuyPlan Plan(
        BuyRule rule,
        IReadOnlyList<MallListingRow> listings,
        long availableMeat)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(listings);
        var budget = Math.Max(0, availableMeat - rule.MeatReserve);
        var candidates = new List<BuyCandidate>();
        var spent = 0L;
        var profit = 0L;
        var bought = 0;
        foreach (var listing in listings.OrderBy(x => x.UnitPrice))
        {
            if (bought >= rule.MaximumPerDay) break;
            if (!rule.Accepts(listing.UnitPrice, listing.HasDailyLimit)) continue;
            if (listing.UnitPrice <= 0) continue;
            var quantity = listing.AvailableToday;
            if (quantity <= 0) continue;
            // The daily cap counts units, not listings.
            quantity = Math.Min(quantity, rule.MaximumPerDay - bought);
            // Only what the budget allows — the budget shrinks with every
            // listing, so two cheap listings cannot spend the same money.
            quantity = (int)Math.Min(quantity, budget / listing.UnitPrice);
            if (quantity <= 0) break;
            var unitProfit = rule.ResalePrice - listing.UnitPrice;
            if (unitProfit <= 0) continue;
            candidates.Add(new BuyCandidate
            {
                StoreId = listing.StoreId,
                StoreName = listing.StoreName,
                ItemId = listing.ItemId,
                ItemName = rule.ItemName,
                UnitPrice = listing.UnitPrice,
                Quantity = quantity,
                DailyLimit = listing.DailyLimit,
                ProfitPerUnit = unitProfit,
                TotalProfit = (long)unitProfit * quantity,
                Reason = listing.HasDailyLimit
                    ? $"{listing.UnitPrice:N0} meat with a limit of {listing.DailyLimit}/day — "
                        + $"the flip runs towards {rule.ResalePrice:N0}"
                    : $"{listing.UnitPrice:N0} meat — the flip runs towards {rule.ResalePrice:N0}"
            });
            spent += (long)listing.UnitPrice * quantity;
            profit += (long)unitProfit * quantity;
            budget -= (long)listing.UnitPrice * quantity;
            bought += quantity;
        }
        var rationale = candidates.Count == 0
            ? $"No listing of the {rule.ItemName} is at or below {rule.MaximumPrice:N0} meat."
            : $"{candidates.Count} listings, {spent:N0} meat spent, {profit:N0} meat expected.";
        return new BuyPlan
        {
            ItemId = rule.ItemId,
            ItemName = rule.ItemName,
            Candidates = candidates,
            TotalCost = spent,
            TotalProfit = profit,
            Rationale = rationale
        };
    }

    /// <summary>
    /// The cheapest price the item is on offer for. Zero when the item is
    /// not on the mall at all.
    /// </summary>
    public static int CheapestPrice(IEnumerable<MallListingRow> listings)
    {
        var cheapest = 0;
        foreach (var listing in listings)
        {
            if (listing.UnitPrice <= 0) continue;
            if (cheapest == 0 || listing.UnitPrice < cheapest) cheapest = listing.UnitPrice;
        }
        return cheapest;
    }
}

/// <summary>
/// One listing on the mall, with the seller's daily limit.
/// </summary>
public sealed record MallListingRow
{
    /// <summary>The seller's store id.</summary>
    public required int StoreId { get; init; }
    /// <summary>The item's id.</summary>
    public required int ItemId { get; init; }
    /// <summary>The store name.</summary>
    public required string StoreName { get; init; }
    /// <summary>How many the seller offers in total.</summary>
    public required int Stock { get; init; }
    /// <summary>The price per unit, in meat.</summary>
    public required int UnitPrice { get; init; }
    /// <summary>
    /// How many the seller sells per day. KoL writes "1 / day" in its own
    /// column; a seller without a limit sells as much as the stock holds.
    /// </summary>
    public int DailyLimit { get; init; }
    /// <summary>Whether the seller limits the amount per day.</summary>
    public bool HasDailyLimit => DailyLimit > 0;
    /// <summary>
    /// How many can actually be bought today: the smaller of the stock and
    /// the daily limit. A listing without a limit is bounded by its stock.
    /// </summary>
    public int AvailableToday => HasDailyLimit ? Math.Min(Stock, DailyLimit) : Stock;
}
