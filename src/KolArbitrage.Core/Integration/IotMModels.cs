namespace KolArbitrage.Core.Integration;

/// <summary>Broad use category used when evaluating an Item of the Month.</summary>
public enum IotMItemKind
{
    Unknown,
    Utility,
    Equipment,
    Consumable,
    Accessory
}

/// <summary>Action produced by the read-only IotM scanner.</summary>
public enum IotMRecommendation
{
    Hold,
    Buy,
    Skip
}

/// <summary>Compatibility name for consumers that call a recommendation an action.</summary>
public enum IotMAction
{
    Hold,
    Buy,
    Skip
}

/// <summary>Alias for consumers that model the output as a decision.</summary>
public enum IotMDecision
{
    Hold,
    Buy,
    Skip
}

/// <summary>
/// A catalog row for an IotM, an accessory, or one of its packaged/opened
/// forms. Catalog rows are deliberately data-only; they do not imply that an
/// item can be fetched from, opened in, or bought through mrstore.php.
/// </summary>
public sealed record IotMCatalogEntry(string ItemId, string ItemName)
{
    public bool IsIotM { get; init; } = true;
    public bool IsMrAccessory { get; init; }
    public bool IsPackaged { get; init; }
    public bool IsOpened { get; init; }
    public bool IsTradeable { get; init; } = true;
    public bool IsGiftable { get; init; } = true;
    public bool IsAccountBound { get; init; }
    public IotMItemKind Kind { get; init; } = IotMItemKind.Unknown;

    /// <summary>Utility value received at the end of each utility period.</summary>
    public decimal UtilityPerPeriod { get; init; }

    /// <summary>Number of periods for which the utility is retained.</summary>
    public int UtilityPeriods { get; init; }

    /// <summary>Discount rate applied to utility cash flows per period.</summary>
    public decimal UtilityDiscountRate { get; init; }

    /// <summary>
    /// Meat value of the closest replacement, paid once at the start of the
    /// utility horizon. This is an opportunity cost, not a mutation.
    /// </summary>
    public decimal ReplacementCost { get; init; }

    /// <summary>Optional terminal value retained after the utility horizon.</summary>
    public decimal TerminalValue { get; init; }

    /// <summary>Per-unit cost of packaging/listing this form for resale.</summary>
    public decimal PackagingCost { get; init; }

    /// <summary>Optional identifier for the Mr. Accessory used as numeraire.</summary>
    public string? MrAccessoryItemId { get; init; }

    public IotMCatalogEntry(
        string itemId,
        string itemName,
        bool isPackaged,
        bool isOpened,
        bool isTradeable,
        bool isGiftable = true,
        bool isAccountBound = false)
        : this(itemId, itemName)
    {
        IsPackaged = isPackaged;
        IsOpened = isOpened;
        IsTradeable = isTradeable;
        IsGiftable = isGiftable;
        IsAccountBound = isAccountBound;
    }

    // Short aliases keep the flags convenient for serializers and callers.
    public bool Packaged => IsPackaged;
    public bool Opened => IsOpened;
    public bool Tradeable => IsTradeable;
    public bool AccountBound => IsAccountBound;

    /// <summary>
    /// Mall acquisition and gift transfer are separate KoL capabilities.  A
    /// trade-only (<c>t</c>) item is therefore Mall-tradeable even when it is
    /// not giftable.
    /// </summary>
    public bool IsMallTradeable => IsTradeable && !IsAccountBound;
    public bool MallTradeable => IsMallTradeable;
    public bool CanBuyFromMall => IsMallTradeable;
    public bool CanTrade => IsMallTradeable;
    public bool IsGiftTransferable => IsGiftable && !IsAccountBound;
    public bool GiftTransferable => IsGiftTransferable;
    public bool Giftable => IsGiftable;
    public bool CanGift => IsGiftTransferable;
    public bool CanTransferAsGift => IsGiftTransferable;
    public bool CanTransferByGift => IsGiftTransferable;

    public bool IsValid(out string error)
    {
        if (string.IsNullOrWhiteSpace(ItemId))
        {
            error = "Item id is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(ItemName))
        {
            error = "Item name is required.";
            return false;
        }

        if (IsPackaged && IsOpened)
        {
            error = "An item cannot be both packaged and opened in the same catalog row.";
            return false;
        }

        if (UtilityPerPeriod < 0m || UtilityPeriods < 0 || UtilityDiscountRate < 0m
            || ReplacementCost < 0m || TerminalValue < 0m || PackagingCost < 0m)
        {
            error = "Utility and replacement values must be non-negative.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static IotMCatalogEntry FromItemMaster(ItemMasterRecord item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new IotMCatalogEntry(item.ItemId, item.Name)
        {
            IsIotM = item.HasSourceTag(ItemSourceTag.Iotm),
            IsPackaged = item.IsPackaged,
            IsOpened = item.IsOpened,
            IsTradeable = item.IsTradeable,
            IsGiftable = item.IsGiftable,
            IsAccountBound = item.IsAccountBound,
            Kind = item.IsPackaged ? IotMItemKind.Consumable : IotMItemKind.Unknown
        };
    }
}

/// <summary>A point-in-time catalog read used by the scanner.</summary>
public sealed record IotMCatalogSnapshot
{
    public IReadOnlyList<IotMCatalogEntry> Entries { get; init; } = Array.Empty<IotMCatalogEntry>();
    public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.UtcNow;
    public Uri? SourceUri { get; init; }
    public string? Version { get; init; }
    public bool IsStale { get; init; }

    public IotMCatalogSnapshot()
    {
    }

    public IotMCatalogSnapshot(
        IReadOnlyList<IotMCatalogEntry> entries,
        DateTimeOffset fetchedAt,
        Uri? sourceUri = null,
        string? version = null,
        bool isStale = false)
    {
        Entries = entries ?? throw new ArgumentNullException(nameof(entries));
        FetchedAt = fetchedAt;
        SourceUri = sourceUri;
        Version = version;
        IsStale = isStale;
    }

    public IotMCatalogEntry? Find(string itemId) =>
        string.IsNullOrWhiteSpace(itemId)
            ? null
            : Entries.FirstOrDefault(x => string.Equals(x.ItemId.Trim(), itemId.Trim(), StringComparison.OrdinalIgnoreCase));

    public bool IsFresh(DateTimeOffset now, TimeSpan maxAge) =>
        !IsStale && maxAge >= TimeSpan.Zero && now - FetchedAt <= maxAge;

    public static IotMCatalogSnapshot FromItemMaster(ItemMaster master,
        DateTimeOffset? fetchedAt = null, Uri? sourceUri = null, string? version = null,
        bool isStale = false)
    {
        ArgumentNullException.ThrowIfNull(master);
        var entries = master.Items
            .Where(x => x.HasSourceTag(ItemSourceTag.Iotm) || x.Name.Contains("Mr. Accessory", StringComparison.OrdinalIgnoreCase))
            .Select(IotMCatalogEntry.FromItemMaster)
            .ToArray();
        return new IotMCatalogSnapshot(entries, fetchedAt ?? DateTimeOffset.UtcNow,
            sourceUri, version, isStale);
    }
}

/// <summary>
/// Source abstraction for dynamic catalog data. Implementations may read a
/// local export, a maintained item registry, or another read-only source. No
/// mrstore endpoint is assumed by this interface.
/// </summary>
public interface IIotMCatalogSource
{
    Task<IotMCatalogSnapshot> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>Mutable in-memory snapshot store for catalog refresh workflows.</summary>
public sealed class IotMCatalogSnapshotStore : IIotMCatalogSource
{
    private readonly object _gate = new();
    private IotMCatalogSnapshot _current = new();

    public IotMCatalogSnapshot Current
    {
        get
        {
            lock (_gate) return _current;
        }
    }

    public void Replace(IotMCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate) _current = snapshot;
    }

    public void Upsert(IEnumerable<IotMCatalogEntry> entries, DateTimeOffset? fetchedAt = null,
        Uri? sourceUri = null, string? version = null, bool isStale = false)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var incoming = entries.ToArray();
        lock (_gate)
        {
            var merged = _current.Entries
                .Concat(incoming)
                .GroupBy(x => x.ItemId.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Last())
                .ToArray();
            _current = new IotMCatalogSnapshot(merged, fetchedAt ?? DateTimeOffset.UtcNow,
                sourceUri ?? _current.SourceUri, version ?? _current.Version, isStale);
        }
    }

    public Task<IotMCatalogSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Current);

    public async Task<IotMCatalogSnapshot> RefreshAsync(
        IIotMCatalogSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var snapshot = await source.ReadAsync(cancellationToken).ConfigureAwait(false);
        Replace(snapshot);
        return snapshot;
    }
}

/// <summary>Read-only market observations supplied to the IotM scanner.</summary>
public sealed record IotMMarketSnapshot
{
    public string ItemId { get; init; }
    public MallSnapshot? Mall { get; init; }
    public PricegunSnapshot? Pricegun { get; init; }
    public MallSnapshot? MallSnapshot => Mall;
    public PricegunSnapshot? PricegunSnapshot => Pricegun;
    public IotMLiquidity? Liquidity { get; init; }

    /// <summary>Explicit acquisition quote, when the caller has selected a listing.</summary>
    public decimal? AcquisitionPrice { get; init; }
    public decimal? AcquisitionCost => AcquisitionPrice;

    /// <summary>Explicit conservative resale quote, when already calculated.</summary>
    public decimal? ExpectedSellPrice { get; init; }

    public decimal? ExpectedResale => ExpectedSellPrice;

    /// <summary>Known per-unit transfer/opportunity cost for the acquisition.</summary>
    public decimal TransferCost { get; init; }

    /// <summary>Store ids excluded from market statistics (for example, our own shop).</summary>
    public IReadOnlySet<string> ExcludedStoreIds { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool IsStale { get; init; }

    public IotMMarketSnapshot()
    {
        ItemId = string.Empty;
    }

    public IotMMarketSnapshot(string itemId)
    {
        ItemId = itemId ?? throw new ArgumentNullException(nameof(itemId));
    }

    public IotMMarketSnapshot(string itemId, MallSnapshot? mall, PricegunSnapshot? pricegun)
        : this(itemId)
    {
        Mall = mall;
        Pricegun = pricegun;
    }

    public bool IsFresh(DateTimeOffset now, TimeSpan maxAge) =>
        !IsStale && Mall?.IsStale != true && Pricegun?.IsStale != true
            && maxAge >= TimeSpan.Zero && now - ObservedAt <= maxAge;
}

/// <summary>Mr. Accessory market benchmark and its source quality.</summary>
public sealed record MrAccessoryBenchmark
{
    public string ItemId { get; init; }
    public string ItemName { get; init; } = "Mr. Accessory";
    public decimal? MallMedianPrice { get; init; }
    public decimal? HistoricalMedianPrice { get; init; }
    public decimal? BenchmarkPrice { get; init; }
    public int MallListingCount { get; init; }
    public int HistoricalSaleCount { get; init; }
    public DateTimeOffset AsOf { get; init; } = DateTimeOffset.UtcNow;
    public bool IsStale { get; init; }

    public DateTimeOffset FetchedAt => AsOf;

    public decimal? MeatValue => BenchmarkPrice;
    public decimal? MrAValue => BenchmarkPrice;
    public decimal? Value => BenchmarkPrice;
    public decimal? ReferencePrice => BenchmarkPrice;
    public bool IsUsable => BenchmarkPrice is > 0m && !IsStale;

    public MrAccessoryBenchmark()
    {
        ItemId = string.Empty;
    }

    public MrAccessoryBenchmark(string itemId)
    {
        ItemId = itemId ?? throw new ArgumentNullException(nameof(itemId));
    }

    public MrAccessoryBenchmark(string itemId, decimal? benchmarkPrice, DateTimeOffset? asOf = null, bool isStale = false)
        : this(itemId)
    {
        BenchmarkPrice = benchmarkPrice;
        AsOf = asOf ?? DateTimeOffset.UtcNow;
        IsStale = isStale;
    }
}

/// <summary>Observed demand and supply indicators for one item.</summary>
public sealed record IotMLiquidity
{
    public int MallListingCount { get; init; }
    public long MallQuantity { get; init; }
    public int RecentSaleCount { get; init; }
    public long RecentSaleQuantity { get; init; }
    public decimal SalesPerDay { get; init; }
    public decimal? DaysToSell { get; init; }
    public decimal SellThroughRate { get; init; }
    public decimal Score { get; init; }
    public bool IsLiquid { get; init; }
    public bool IsStale { get; init; }

    public int PricegunSaleCount => RecentSaleCount;
    public decimal SaleRatePerDay => SalesPerDay;

    public IotMLiquidity()
    {
    }

    public IotMLiquidity(
        int mallListingCount,
        long mallQuantity,
        int recentSaleCount,
        long recentSaleQuantity,
        decimal salesPerDay,
        decimal? daysToSell,
        bool isLiquid,
        bool isStale = false)
    {
        MallListingCount = mallListingCount;
        MallQuantity = mallQuantity;
        RecentSaleCount = recentSaleCount;
        RecentSaleQuantity = recentSaleQuantity;
        SalesPerDay = salesPerDay;
        DaysToSell = daysToSell;
        IsLiquid = isLiquid;
        IsStale = isStale;
        SellThroughRate = recentSaleQuantity + mallQuantity == 0
            ? 0m
            : Math.Clamp((decimal)recentSaleQuantity / (recentSaleQuantity + mallQuantity), 0m, 1m);
        Score = isLiquid ? 1m : 0m;
    }
}

/// <summary>Discounted utility value retained over a finite horizon.</summary>
public sealed record IotMUtilityNpv
{
    public decimal GrossUtilityNpv { get; init; }
    public decimal ReplacementCostNpv { get; init; }
    public decimal TerminalValueNpv { get; init; }
    public decimal NetUtilityNpv { get; init; }
    public int Periods { get; init; }
    public decimal DiscountRate { get; init; }

    public decimal Npv => NetUtilityNpv;
    public decimal RetainedUtility => NetUtilityNpv;

    public IotMUtilityNpv()
    {
    }

    public IotMUtilityNpv(decimal grossUtilityNpv, decimal replacementCostNpv,
        decimal terminalValueNpv, decimal netUtilityNpv, int periods, decimal discountRate)
    {
        GrossUtilityNpv = grossUtilityNpv;
        ReplacementCostNpv = replacementCostNpv;
        TerminalValueNpv = terminalValueNpv;
        NetUtilityNpv = netUtilityNpv;
        Periods = periods;
        DiscountRate = discountRate;
    }
}

/// <summary>Scanner thresholds; all values are local policy, never remote state.</summary>
public sealed record IotMScannerOptions
{
    public decimal MallFeeRate { get; init; } = 0.01m;
    public decimal MinimumAbsoluteSpread { get; init; } = 1_000m;
    public decimal MinimumMarginPercent { get; init; } = 0.10m;
    public int MinimumHistoricalSales { get; init; } = 3;
    public decimal MinimumSalesPerDay { get; init; } = 0.01m;
    public int MaximumDaysToSell { get; init; } = 90;
    public decimal MinimumLiquidityScore { get; init; } = 0.10m;
    public TimeSpan MaximumSnapshotAge { get; init; } = TimeSpan.FromHours(12);
    public int UtilityHorizonPeriods { get; init; } = 12;
    public decimal UtilityDiscountRate { get; init; } = 0.01m;

    // Friendly aliases used by callers that use “min” terminology.
    public decimal MinAbsoluteSpread => MinimumAbsoluteSpread;
    public decimal MinMarginPercent => MinimumMarginPercent;
    public decimal MallFeePercent => MallFeeRate;
    public TimeSpan MaxAge => MaximumSnapshotAge;

    public void Validate()
    {
        if (MallFeeRate < 0m || MallFeeRate >= 1m) throw new ArgumentOutOfRangeException(nameof(MallFeeRate));
        if (MinimumAbsoluteSpread < 0m) throw new ArgumentOutOfRangeException(nameof(MinimumAbsoluteSpread));
        if (MinimumMarginPercent < 0m) throw new ArgumentOutOfRangeException(nameof(MinimumMarginPercent));
        if (MinimumHistoricalSales < 0) throw new ArgumentOutOfRangeException(nameof(MinimumHistoricalSales));
        if (MinimumSalesPerDay < 0m) throw new ArgumentOutOfRangeException(nameof(MinimumSalesPerDay));
        if (MaximumDaysToSell < 0) throw new ArgumentOutOfRangeException(nameof(MaximumDaysToSell));
        if (MinimumLiquidityScore < 0m || MinimumLiquidityScore > 1m)
            throw new ArgumentOutOfRangeException(nameof(MinimumLiquidityScore));
        if (MaximumSnapshotAge < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(MaximumSnapshotAge));
        if (UtilityHorizonPeriods < 0) throw new ArgumentOutOfRangeException(nameof(UtilityHorizonPeriods));
        if (UtilityDiscountRate < 0m) throw new ArgumentOutOfRangeException(nameof(UtilityDiscountRate));
    }
}

/// <summary>One explainable recommendation produced by <see cref="IotMScanner"/>.</summary>
public sealed record IotMScanResult
{
    public required IotMCatalogEntry Entry { get; init; }
    public required IotMRecommendation Recommendation { get; init; }
    public decimal? AcquisitionPrice { get; init; }
    public decimal? AcquisitionCost => AcquisitionPrice;
    public decimal? ExpectedSellPrice { get; init; }
    public decimal? NetResaleValue { get; init; }
    public decimal? Spread { get; init; }
    public decimal? MarginPercent { get; init; }
    public decimal? MrAccessoryMultiple { get; init; }
    public IotMUtilityNpv Utility { get; init; } = new();
    public IotMLiquidity Liquidity { get; init; } = new();
    public bool IsStale { get; init; }
    public string Rationale { get; init; } = string.Empty;

    public IotMAction Action => (IotMAction)(int)Recommendation;
    public IotMDecision Decision => (IotMDecision)(int)Recommendation;
    public IotMRecommendation SuggestedAction => Recommendation;
    public decimal? ExpectedResale => ExpectedSellPrice;
    public decimal? UtilityNpv => Utility.NetUtilityNpv;
    public bool IsBuyCandidate => Recommendation == IotMRecommendation.Buy;
}
