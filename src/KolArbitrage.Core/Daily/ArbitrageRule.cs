namespace KolArbitrage.Core.Daily;

/// <summary>
/// The price rule behind "buy only when it is cheap enough to resell": the most the buyer may pay is
/// the recent sale median reduced by a minimum margin.
/// </summary>
public static class ArbitrageRule
{
    /// <summary>Fewer recent sales than this are too little evidence to judge a price.</summary>
    public const int MinimumSales = 5;

    /// <summary>Median of the sale prices, or null when there are too few sales.</summary>
    public static long? Median(IReadOnlyCollection<long> unitPrices)
    {
        ArgumentNullException.ThrowIfNull(unitPrices);
        if (unitPrices.Count < MinimumSales) return null;
        var sorted = unitPrices.OrderBy(p => p).ToArray();
        return sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    /// <summary>Highest acceptable unit price, or null when the margin is invalid or the median unknown.</summary>
    public static long? MaxBuyPrice(IReadOnlyCollection<long> unitPrices, double minMargin)
    {
        if (minMargin is < 0 or >= 1) return null;
        return Median(unitPrices) is { } median ? (long)Math.Floor(median * (1 - minMargin)) : null;
    }
}
