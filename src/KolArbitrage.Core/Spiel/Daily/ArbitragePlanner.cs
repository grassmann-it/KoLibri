namespace KolArbitrage.Core.Spiel;

/// <summary>
/// The two marketplaces KoL offers for the same item. The mall lists
/// permanent listings with a seller who sets the price; the flea market
/// takes a price and a 48-hour timer, and the item comes back if it does
/// not sell. Their price curves are independent, so an item can be cheap in
/// one and expensive in the other.
/// </summary>
public enum MarketKind
{
    /// <summary>The permanent mall, reached through mall.php.</summary>
    Mall,
    /// <summary>The flea market, reached through town_fleamarket.php.</summary>
    FleaMarket
}

/// <summary>One listing on one of the two marketplaces.</summary>
public sealed record MarketListing
{
    public required MarketKind Market { get; init; }
    public required int ItemId { get; init; }
    public required string ItemName { get; init; }
    public required long UnitPrice { get; init; }
    public required int Quantity { get; init; }
    /// <summary>The seller's store id — the flea market has no stores,
    /// so it stays empty there.</summary>
    public string? StoreId { get; init; }
    /// <summary>How many of the item the seller offers in total.</summary>
    public int? StoreQuantity { get; init; }
    public bool IsBuyable => Quantity > 0 && UnitPrice > 0;
}

/// <summary>What the price history says about an item.</summary>
public sealed record PriceHistory
{
    public required int ItemId { get; init; }
    public long MedianPrice { get; init; }
    public long MinimumPrice { get; init; }
    public long MaximumPrice { get; init; }
    public int SaleCount { get; init; }
    public int TotalQuantity { get; init; }
    public bool HasHistory => SaleCount > 0 && MedianPrice > 0;
}

/// <summary>One arbitrage opportunity the planner found.</summary>
public sealed record ArbitrageOpportunity
{
    public required int ItemId { get; init; }
    public required string ItemName { get; init; }
    /// <summary>Where the item is cheap.</summary>
    public required MarketKind BuyMarket { get; init; }
    public required long BuyPrice { get; init; }
    /// <summary>Where the item sells. The same market as the buy market
    /// means a direct flip.</summary>
    public required MarketKind SellMarket { get; init; }
    public required long SellPrice { get; init; }
    public required int AvailableQuantity { get; init; }
    public required long Spread { get; init; }
    public required decimal Margin { get; init; }
    public required long EstimatedProfit { get; init; }
    public bool IsProfitable => Spread > 0;

    public string Summary =>
        $"{ItemName}: buy {BuyPrice:N0} on {BuyMarket}, sell {SellPrice:N0} on {SellMarket} "
        + $"(+{Spread:N0}, {Margin * 100:N1} %)";
}

/// <summary>The decision for one item.</summary>
public sealed record ArbitrageVerdict
{
    public required int ItemId { get; init; }
    public required string ItemName { get; init; }
    public ArbitrageOpportunity? Opportunity { get; init; }
    public required string Rationale { get; init; }
    public bool ShouldBuy => Opportunity is { IsProfitable: true };
}

/// <summary>
/// Finds the arbitrage opportunities across KoL's two marketplaces.
///
/// The comparison is deliberately simple and explicit: the cheapest
/// listing on one market against the best resale price on the other. The
/// resale price comes from the price history (pricegun), because the flea
/// market and the mall hold the same pool of buyers but not the same
/// prices.
///
/// The planner never spends. It produces the plan; the caller decides.
/// </summary>
public sealed class ArbitragePlanner
{
    /// <summary>The profit the operator wants before a purchase is
    /// considered worth it.</summary>
    public long MinimumProfit { get; init; } = 5_000;

    /// <summary>The margin the operator wants, as a fraction.</summary>
    public decimal MinimumMargin { get; init; } = 0.10m;

    /// <summary>Meat the buying account must keep in reserve.</summary>
    public long MeatReserve { get; init; } = 1_000_000;

    /// <summary>How many of one item the operator wants at most. Keeps a
    /// single cheap listing from eating the whole budget.</summary>
    public int MaximumQuantityPerItem { get; init; } = 40;

    /// <summary>Plans one item.</summary>
    public ArbitrageVerdict Plan(
        int itemId,
        string itemName,
        IReadOnlyList<MarketListing> listings,
        PriceHistory? history)
    {
        ArgumentNullException.ThrowIfNull(listings);
        var buyable = listings.Where(l => l.IsBuyable).ToArray();
        if (buyable.Length == 0)
        {
            return new ArbitrageVerdict
            {
                ItemId = itemId,
                ItemName = itemName,
                Rationale = "No listing on either marketplace."
            };
        }

        // The resale price is the best price the item actually fetched.
        // Without a history the median of the market prices is the best
        // guess, and the opportunity is reported but marked as such.
        var resale = history?.HasHistory == true
            ? history!.MedianPrice
            : buyable.Max(l => l.UnitPrice);
        var hasHistory = history?.HasHistory == true;

        var best = buyable
            .OrderBy(l => l.UnitPrice)
            .ThenBy(l => l.Market)
            .First();
        var spread = resale - best.UnitPrice;
        var margin = best.UnitPrice > 0 ? spread / (decimal)best.UnitPrice : 0m;

        // The sell side: the other market, or the same market when the
        // item is listed cheaper there than its own history.
        var sellMarket = best.Market == MarketKind.Mall ? MarketKind.FleaMarket : MarketKind.Mall;
        var opposite = buyable
            .Where(l => l.Market == sellMarket)
            .OrderByDescending(l => l.UnitPrice)
            .FirstOrDefault();
        var sellPrice = opposite?.UnitPrice ?? resale;

        if (spread < MinimumProfit || margin < MinimumMargin)
        {
            return new ArbitrageVerdict
            {
                ItemId = itemId,
                ItemName = itemName,
                Rationale = hasHistory
                    ? $"Cheapest is {best.UnitPrice:N0} on {best.Market}; the median is {resale:N0}. "
                        + $"The spread of {spread:N0} ({margin * 100:N1} %) is below the minimum."
                    : $"Cheapest is {best.UnitPrice:N0} on {best.Market}; without a price history "
                        + "the spread cannot be judged."
            };
        }

        var affordable = best.UnitPrice > 0
            ? (int)Math.Max(0, (long.MaxValue - MeatReserve) / best.UnitPrice)
            : 0;
        var quantity = Math.Min(MaximumQuantityPerItem, Math.Min(best.Quantity, affordable));
        if (quantity <= 0)
        {
            return new ArbitrageVerdict
            {
                ItemId = itemId,
                ItemName = itemName,
                Rationale = "The meat reserve does not allow a purchase of this item."
            };
        }

        return new ArbitrageVerdict
        {
            ItemId = itemId,
            ItemName = itemName,
            Opportunity = new ArbitrageOpportunity
            {
                ItemId = itemId,
                ItemName = itemName,
                BuyMarket = best.Market,
                BuyPrice = best.UnitPrice,
                SellMarket = sellMarket,
                SellPrice = sellPrice,
                AvailableQuantity = quantity,
                Spread = spread,
                Margin = margin,
                EstimatedProfit = spread * quantity
            },
            Rationale = $"Buy {quantity} at {best.UnitPrice:N0} on {best.Market}, "
                + $"resell around {sellPrice:N0}. Est. profit {spread * quantity:N0} meat."
        };
    }
}
