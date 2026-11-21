using KoLibri.Core.Markt;
using KoLibri.Core.Spiel;
namespace KoLibri.Core.Markt;

/// <summary>Calculates discounted utility value without contacting KoL.</summary>
public static class IotMUtilityValuation
{
    public static IotMUtilityNpv Calculate(
        decimal utilityPerPeriod,
        int periods,
        decimal discountRate,
        decimal replacementCost = 0m,
        decimal terminalValue = 0m)
    {
        if (utilityPerPeriod < 0m) throw new ArgumentOutOfRangeException(nameof(utilityPerPeriod));
        if (periods < 0) throw new ArgumentOutOfRangeException(nameof(periods));
        if (discountRate < 0m) throw new ArgumentOutOfRangeException(nameof(discountRate));
        if (replacementCost < 0m) throw new ArgumentOutOfRangeException(nameof(replacementCost));
        if (terminalValue < 0m) throw new ArgumentOutOfRangeException(nameof(terminalValue));

        var gross = 0m;
        for (var period = 1; period <= periods; period++)
            gross += utilityPerPeriod / Pow(1m + discountRate, period);

        var terminal = periods == 0
            ? terminalValue
            : terminalValue / Pow(1m + discountRate, periods);
        return new IotMUtilityNpv
        {
            GrossUtilityNpv = gross,
            ReplacementCostNpv = replacementCost,
            TerminalValueNpv = terminal,
            NetUtilityNpv = gross + terminal - replacementCost,
            Periods = periods,
            DiscountRate = discountRate
        };
    }

    public static IotMUtilityNpv Calculate(
        IotMCatalogEntry entry,
        IotMScannerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        options ??= new IotMScannerOptions();
        options.Validate();
        var periods = entry.UtilityPeriods > 0 ? entry.UtilityPeriods : options.UtilityHorizonPeriods;
        var rate = entry.UtilityDiscountRate > 0m ? entry.UtilityDiscountRate : options.UtilityDiscountRate;
        return Calculate(entry.UtilityPerPeriod, periods, rate, entry.ReplacementCost, entry.TerminalValue);
    }

    // NPV is often requested as a scalar in report builders.
    public static decimal Npv(decimal utilityPerPeriod, int periods, decimal discountRate,
        decimal replacementCost = 0m, decimal terminalValue = 0m) =>
        Calculate(utilityPerPeriod, periods, discountRate, replacementCost, terminalValue).NetUtilityNpv;

    private static decimal Pow(decimal value, int exponent)
    {
        var result = 1m;
        for (var i = 0; i < exponent; i++) result *= value;
        return result;
    }
}

/// <summary>Builds a conservative Mr. Accessory benchmark from cached sources.</summary>
public sealed class MrAccessoryBenchmarkCalculator
{
    public MrAccessoryBenchmark Build(
        string itemId,
        string itemName,
        MallSnapshot? mall,
        PricegunSnapshot? pricegun,
        bool isStale = false,
        DateTimeOffset? asOf = null,
        IReadOnlySet<string>? excludedStoreIds = null)
    {
        if (string.IsNullOrWhiteSpace(itemId)) throw new ArgumentException("Item id is required.", nameof(itemId));
        var mallPrices = GetMallPrices(mall, excludedStoreIds);
        var sales = pricegun?.Sales.Where(x => x.UnitPrice > 0).Select(x => x.UnitPrice).ToArray() ?? [];
        var mallMedian = Median(mallPrices);
        var historyMedian = Median(sales);

        // A benchmark should not inherit a one-listing floor. Prefer history
        // when it exists, and use the lower of two independent medians when
        // both sources have enough observations.
        var benchmark = mallMedian is not null && historyMedian is not null
            ? Math.Min(mallMedian.Value, historyMedian.Value)
            : historyMedian ?? mallMedian;
        return new MrAccessoryBenchmark(itemId.Trim())
        {
            ItemName = string.IsNullOrWhiteSpace(itemName) ? "Mr. Accessory" : itemName.Trim(),
            MallMedianPrice = mallMedian,
            HistoricalMedianPrice = historyMedian,
            BenchmarkPrice = benchmark,
            MallListingCount = mall?.Listings.Count(x => excludedStoreIds is null || !excludedStoreIds.Contains(x.StoreId)) ?? 0,
            HistoricalSaleCount = sales.Length,
            AsOf = asOf ?? DateTimeOffset.UtcNow,
            IsStale = isStale || mall?.IsStale == true || pricegun?.IsStale == true
        };
    }

    public MrAccessoryBenchmark Calculate(
        string itemId,
        MallSnapshot? mall,
        PricegunSnapshot? pricegun,
        IReadOnlySet<string>? excludedStoreIds = null) =>
        Build(itemId, "Mr. Accessory", mall, pricegun, excludedStoreIds: excludedStoreIds);

    public static decimal? ExpectedPrice(MallSnapshot? mall, PricegunSnapshot? pricegun,
        IReadOnlySet<string>? excludedStoreIds = null) =>
        new MrAccessoryBenchmarkCalculator().Build("mr-accessory", "Mr. Accessory", mall, pricegun,
            excludedStoreIds: excludedStoreIds).BenchmarkPrice;

    internal static IReadOnlyList<decimal> GetMallPrices(MallSnapshot? mall, IReadOnlySet<string>? excludedStoreIds)
    {
        if (mall is null) return [];
        return mall.Listings
            .Where(x => x.Price > 0 && (excludedStoreIds is null || !excludedStoreIds.Contains(x.StoreId)))
            .Select(x => (decimal)x.Price)
            .OrderBy(x => x)
            .ToArray();
    }

    internal static decimal? Median(IEnumerable<long> values)
    {
        var ordered = values.Where(x => x > 0).Select(x => (decimal)x).OrderBy(x => x).ToArray();
        return Median(ordered);
    }

    internal static decimal? Median(IEnumerable<decimal> values)
    {
        var ordered = values.Where(x => x > 0m).OrderBy(x => x).ToArray();
        if (ordered.Length == 0) return null;
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2m
            : ordered[middle];
    }
}

/// <summary>Derives demand and supply indicators from Mall and Pricegun snapshots.</summary>
public sealed class IotMLiquidityCalculator
{
    public IotMLiquidity Calculate(
        MallSnapshot? mall,
        PricegunSnapshot? pricegun,
        DateTimeOffset? now = null,
        int minimumSales = 3,
        decimal minimumSalesPerDay = 0.01m,
        int maximumDaysToSell = 90,
        TimeSpan? maximumAge = null)
    {
        if (minimumSales < 0) throw new ArgumentOutOfRangeException(nameof(minimumSales));
        if (minimumSalesPerDay < 0m) throw new ArgumentOutOfRangeException(nameof(minimumSalesPerDay));
        if (maximumDaysToSell < 0) throw new ArgumentOutOfRangeException(nameof(maximumDaysToSell));

        var observedAt = now ?? DateTimeOffset.UtcNow;
        var sales = pricegun?.Sales.Where(x => x.UnitPrice > 0 && x.Quantity > 0 && x.SoldAt <= observedAt).ToArray() ?? [];
        var saleCount = sales.Length;
        var saleQuantity = sales.Sum(x => (long)x.Quantity);
        var mallListings = mall?.Listings.Where(x => x.Price > 0 && x.Quantity > 0).ToArray() ?? [];
        var mallQuantity = mallListings.Sum(x => (long)x.Quantity);

        var periodDays = sales.Length == 0
            ? 1m
            : Math.Max(1m, (decimal)(observedAt - sales.Min(x => x.SoldAt)).TotalDays);
        var salesPerDay = saleQuantity / periodDays;
        var daysToSell = salesPerDay > 0m && mallQuantity > 0
            ? mallQuantity / salesPerDay
            : (decimal?)null;
        var sellThrough = saleQuantity + mallQuantity == 0
            ? 0m
            : Math.Clamp((decimal)saleQuantity / (saleQuantity + mallQuantity), 0m, 1m);

        var age = maximumAge ?? TimeSpan.FromHours(12);
        var stale = mall?.IsStale == true || pricegun?.IsStale == true
            || (mall is not null && observedAt - mall.FetchedAt > age)
            || (pricegun is not null && observedAt - pricegun.FetchedAt > age);

        // Score rewards completed sales and a short estimated sell window;
        // it remains in [0,1] so it can be compared across item classes.
        var volumeScore = minimumSales == 0 ? (saleCount > 0 ? 1m : 0m)
            : Math.Clamp((decimal)saleCount / minimumSales, 0m, 1m);
        var rateScore = minimumSalesPerDay <= 0m ? (salesPerDay > 0m ? 1m : 0m)
            : Math.Clamp(salesPerDay / minimumSalesPerDay, 0m, 1m);
        var daysScore = daysToSell is null || maximumDaysToSell == 0
            ? (daysToSell is null ? 0m : 1m)
            : Math.Clamp(1m - daysToSell.Value / maximumDaysToSell, 0m, 1m);
        var score = Math.Clamp((volumeScore * 0.4m) + (rateScore * 0.4m) + (daysScore * 0.2m), 0m, 1m);
        var liquid = !stale && saleCount >= minimumSales && salesPerDay >= minimumSalesPerDay
            && (daysToSell is null || daysToSell <= maximumDaysToSell) && score >= 0.1m;
        return new IotMLiquidity
        {
            MallListingCount = mallListings.Length,
            MallQuantity = mallQuantity,
            RecentSaleCount = saleCount,
            RecentSaleQuantity = saleQuantity,
            SalesPerDay = salesPerDay,
            DaysToSell = daysToSell,
            SellThroughRate = sellThrough,
            Score = score,
            IsLiquid = liquid,
            IsStale = stale
        };
    }
}

/// <summary>
/// Read-only scanner for IotM and Mr. Accessory opportunities. It evaluates
/// supplied snapshots only; it has no mrstore client and never opens, buys,
/// transfers, or lists an item.
/// </summary>
public sealed class IotMScanner
{
    private readonly IotMScannerOptions _options;
    private readonly TimeProvider _clock;
    private readonly IotMLiquidityCalculator _liquidity = new();

    public IotMScanner(IotMScannerOptions? options = null, TimeProvider? clock = null)
    {
        _options = options ?? new IotMScannerOptions();
        _options.Validate();
        _clock = clock ?? TimeProvider.System;
    }

    public IReadOnlyList<IotMScanResult> Scan(
        IotMCatalogSnapshot catalog,
        MrAccessoryBenchmark benchmark,
        IReadOnlyDictionary<string, IotMMarketSnapshot> markets)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(benchmark);
        ArgumentNullException.ThrowIfNull(markets);
        return catalog.Entries
            .Select(entry => Evaluate(entry, benchmark, FindMarket(markets, entry.ItemId), catalog))
            .OrderByDescending(x => x.Recommendation == IotMRecommendation.Buy)
            .ThenByDescending(x => x.Spread ?? decimal.MinValue)
            .ThenBy(x => x.Entry.ItemName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<IotMScanResult> ScanCatalog(
        IotMCatalogSnapshot catalog,
        MrAccessoryBenchmark benchmark,
        IReadOnlyDictionary<string, IotMMarketSnapshot> markets) =>
        Scan(catalog, benchmark, markets);

    private static IotMMarketSnapshot? FindMarket(
        IReadOnlyDictionary<string, IotMMarketSnapshot> markets,
        string itemId)
    {
        if (markets.TryGetValue(itemId, out var exact)) return exact;
        return markets.FirstOrDefault(x => string.Equals(x.Key.Trim(), itemId.Trim(), StringComparison.OrdinalIgnoreCase)).Value;
    }

    public IReadOnlyList<IotMScanResult> Scan(
        ItemMaster itemMaster,
        MrAccessoryBenchmark benchmark,
        IReadOnlyDictionary<string, IotMMarketSnapshot> markets,
        DateTimeOffset? fetchedAt = null)
    {
        ArgumentNullException.ThrowIfNull(itemMaster);
        return Scan(IotMCatalogSnapshot.FromItemMaster(itemMaster, fetchedAt), benchmark, markets);
    }

    public IotMScanResult Evaluate(
        IotMCatalogEntry entry,
        MrAccessoryBenchmark benchmark,
        IotMMarketSnapshot? market = null,
        IotMCatalogSnapshot? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(benchmark);
        var now = _clock.GetUtcNow();
        var utility = IotMUtilityValuation.Calculate(entry, _options);
        var liquidity = market?.Liquidity ?? _liquidity.Calculate(market?.Mall, market?.Pricegun, now,
            _options.MinimumHistoricalSales, _options.MinimumSalesPerDay,
            _options.MaximumDaysToSell, _options.MaximumSnapshotAge);
        var stale = catalog?.IsStale == true || benchmark.IsStale || market?.IsStale == true
            || market?.Mall?.IsStale == true || market?.Pricegun?.IsStale == true
            || (catalog is not null && now - catalog.FetchedAt > _options.MaximumSnapshotAge)
            || (market is not null && now - market.ObservedAt > _options.MaximumSnapshotAge);

        var acquisition = market?.AcquisitionPrice ?? GetAcquisitionPrice(market);
        var expected = market?.ExpectedSellPrice ?? GetExpectedSellPrice(market);
        var netResale = expected is > 0m
            ? expected.Value * (1m - _options.MallFeeRate) - entry.PackagingCost
            : (decimal?)null;
        var spread = acquisition is > 0m && netResale is not null
            ? netResale.Value + Math.Max(0m, utility.NetUtilityNpv) - acquisition.Value
                - Math.Max(0m, market?.TransferCost ?? 0m)
            : (decimal?)null;
        var margin = acquisition is > 0m && spread is not null ? spread.Value / acquisition.Value : (decimal?)null;
        var multiple = benchmark.BenchmarkPrice is > 0m && acquisition is > 0m
            ? benchmark.BenchmarkPrice.Value / acquisition.Value
            : (decimal?)null;

        var reasons = new List<string>();
        var recommendation = ChooseRecommendation(entry, market, benchmark, stale, acquisition, expected,
            netResale, spread, margin, liquidity, utility, reasons);
        if (reasons.Count == 0) reasons.Add("No independent spread or utility edge was observed.");
        return new IotMScanResult
        {
            Entry = entry,
            Recommendation = recommendation,
            AcquisitionPrice = acquisition,
            ExpectedSellPrice = expected,
            NetResaleValue = netResale,
            Spread = spread,
            MarginPercent = margin,
            MrAccessoryMultiple = multiple,
            Utility = utility,
            Liquidity = liquidity,
            IsStale = stale,
            Rationale = string.Join(" ", reasons)
        };
    }

    public IotMScanResult EvaluateItem(
        IotMCatalogEntry entry,
        MrAccessoryBenchmark benchmark,
        IotMMarketSnapshot? market = null,
        IotMCatalogSnapshot? catalog = null) =>
        Evaluate(entry, benchmark, market, catalog);

    public async Task<IReadOnlyList<IotMScanResult>> ScanAsync(
        IIotMCatalogSource catalogSource,
        MrAccessoryBenchmark benchmark,
        IReadOnlyDictionary<string, IotMMarketSnapshot> markets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalogSource);
        var catalog = await catalogSource.ReadAsync(cancellationToken).ConfigureAwait(false);
        return Scan(catalog, benchmark, markets);
    }

    /// <summary>
    /// Reads an explicitly manifested Mr. Store catalog.  The source remains
    /// dynamic; this overload only adapts its payload to the scanner and does
    /// not embed a list of current IotMs.
    /// </summary>
    public async Task<IReadOnlyList<IotMScanResult>> ScanAsync(
        IMrStoreCatalogSource catalogSource,
        MrAccessoryBenchmark benchmark,
        IReadOnlyDictionary<string, IotMMarketSnapshot> markets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalogSource);
        var catalog = await catalogSource.ReadIotMCatalogAsync(cancellationToken).ConfigureAwait(false);
        return Scan(catalog, benchmark, markets);
    }

    private IotMRecommendation ChooseRecommendation(
        IotMCatalogEntry entry,
        IotMMarketSnapshot? market,
        MrAccessoryBenchmark benchmark,
        bool stale,
        decimal? acquisition,
        decimal? expected,
        decimal? netResale,
        decimal? spread,
        decimal? margin,
        IotMLiquidity liquidity,
        IotMUtilityNpv utility,
        List<string> reasons)
    {
        if (!entry.IsValid(out var validationError))
        {
            reasons.Add(validationError);
            return IotMRecommendation.Skip;
        }

        // Account-bound or non-Mall-tradeable rows may still be useful to an
        // owner, but they can never become a buy candidate from the Mall.
        // Gift transfer is a separate capability: trade-only `t` rows remain
        // valid Mall candidates even when IsGiftable is false.
        if (!entry.IsMallTradeable)
        {
            reasons.Add(entry.IsAccountBound ? "Account-bound item cannot be acquired for resale."
                : "Item is not tradeable through the Mall.");
            return utility.NetUtilityNpv > 0m ? IotMRecommendation.Hold : IotMRecommendation.Skip;
        }

        if (stale)
        {
            reasons.Add("Catalog or market snapshot is stale; refresh before a buy decision.");
            return utility.NetUtilityNpv > 0m ? IotMRecommendation.Hold : IotMRecommendation.Skip;
        }

        if (market is null || acquisition is not > 0m || expected is not > 0m || netResale is null)
        {
            reasons.Add("No complete acquisition and resale quote is available.");
            return utility.NetUtilityNpv > 0m ? IotMRecommendation.Hold : IotMRecommendation.Skip;
        }

        if (!liquidity.IsLiquid || liquidity.Score < _options.MinimumLiquidityScore)
        {
            reasons.Add("Observed sales are too sparse for a liquid resale.");
            return utility.NetUtilityNpv > 0m ? IotMRecommendation.Hold : IotMRecommendation.Skip;
        }

        if (spread is >= 0m && margin is not null
            && spread.Value >= _options.MinimumAbsoluteSpread
            && margin.Value >= _options.MinimumMarginPercent)
        {
            if (benchmark.BenchmarkPrice is not > 0m)
            {
                reasons.Add("Spread clears policy thresholds; Mr. Accessory benchmark is unavailable.");
                return IotMRecommendation.Buy;
            }

            // Mr. Accessory is a numeraire/opportunity cost. A low item price
            // against Mr. A alone is not enough; the spread and liquidity tests
            // above must pass first.
            reasons.Add($"Net spread {spread.Value:0} Meat ({margin.Value:P0}) clears policy thresholds.");
            return IotMRecommendation.Buy;
        }

        if (utility.NetUtilityNpv > 0m)
        {
            reasons.Add($"Retained utility NPV is {utility.NetUtilityNpv:0} Meat; hold for use.");
            return IotMRecommendation.Hold;
        }

        reasons.Add("Price is not sufficiently below the conservative resale value.");
        return IotMRecommendation.Skip;
    }

    private static decimal? GetAcquisitionPrice(IotMMarketSnapshot? market)
    {
        if (market?.Mall is null) return null;
        var excluded = market.ExcludedStoreIds;
        var price = market.Mall.Listings
            .Where(x => x.Price > 0 && (excluded.Count == 0 || !excluded.Contains(x.StoreId)))
            .Select(x => (decimal)x.Price)
            .OrderBy(x => x)
            .FirstOrDefault();
        return price > 0m ? price : null;
    }

    private static decimal? GetExpectedSellPrice(IotMMarketSnapshot? market)
    {
        if (market is null) return null;
        var mall = MrAccessoryBenchmarkCalculator.GetMallPrices(market.Mall, market.ExcludedStoreIds);
        var mallMedian = MrAccessoryBenchmarkCalculator.Median(mall);
        var historyMedian = market.Pricegun is null
            ? null
            : MrAccessoryBenchmarkCalculator.Median(market.Pricegun.Sales.Select(x => x.UnitPrice));
        return mallMedian is not null && historyMedian is not null
            ? Math.Min(mallMedian.Value, historyMedian.Value)
            : historyMedian ?? mallMedian;
    }
}
