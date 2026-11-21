namespace KolArbitrage.Core.Markt;

public sealed record ForeignBribeCatalogEntry(
    string Currency,
    string ItemId,
    string ItemName,
    int CurrencyCost,
    bool IsTradeable = true,
    bool IsOneDayTicket = false,
    bool RequiresTicket = false,
    string? TicketCurrency = null,
    int? TicketCost = null)
{
    public static implicit operator ForeignBribeCatalogEntry(CoinmasterCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new ForeignBribeCatalogEntry(entry.Currency, entry.ItemId, entry.ItemName,
            entry.CurrencyCost, entry.IsTradeable, entry.IsOneDayTicket,
            entry.RequiresTicket, entry.TicketCurrency, entry.TicketCost);
    }

    public string NormalizedCurrency => Currency.Trim();
    public string NormalizedItemId => ItemId.Trim();
    public bool IsTicket => IsOneDayTicket;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Currency)) throw new ArgumentException("Currency is required.", nameof(Currency));
        if (string.IsNullOrWhiteSpace(ItemId)) throw new ArgumentException("Item id is required.", nameof(ItemId));
        if (string.IsNullOrWhiteSpace(ItemName)) throw new ArgumentException("Item name is required.", nameof(ItemName));
        if (CurrencyCost < 0) throw new ArgumentOutOfRangeException(nameof(CurrencyCost));
        if (TicketCost is < 0) throw new ArgumentOutOfRangeException(nameof(TicketCost));
    }
}

public sealed record ForeignBribeRanking(
    ForeignBribeCatalogEntry Entry,
    long? MallLowest,
    decimal? PricegunMedian,
    decimal? PricegunWeightedAverage,
    int PricegunSaleCount,
    int MallListingCount,
    decimal? ExpectedResale,
    decimal? ResalePerCurrencyUnit,
    decimal? CurrencyOpportunityCost,
    decimal? NetBeforeTicket,
    decimal? TicketCostInMeat,
    bool HistoricalHigher,
    bool BuyCandidate,
    bool MallIsStale,
    bool PricegunIsStale,
    string Rationale)
{
    /// <summary>Sale commission and fixed handling costs applied by the ranker.</summary>
    public decimal SaleFeeInMeat { get; init; }
    public decimal ListingFeeInMeat { get; init; }
    public decimal TransferCostInMeat { get; init; }
    public decimal HoldingCostInMeat { get; init; }
    public decimal BagCostAllocationInMeat { get; init; }
    public decimal SafetyBufferInMeat { get; init; }
    public decimal? NetAfterTicket => NetBeforeTicket is null
        ? null
        : NetBeforeTicket - (TicketCostInMeat ?? 0m);
    public decimal? NetAfterCosts => NetAfterTicket;
    public decimal? NetResale => NetAfterTicket;
    public decimal? NetResaleAfterTicket => NetAfterTicket;
    public decimal? NetProfit => NetAfterTicket;
    public bool IsCandidate => BuyCandidate;
    public decimal? CurrencyCostInMeat => CurrencyOpportunityCost;
}

/// <summary>Cost policy for foreign-bribe resale ranking.</summary>
public sealed record ForeignBribeCostOptions : CoinmasterRoiOptions;

/// <summary>Compatibility name for callers that model the ranking as ROI.</summary>
public sealed record ForeignBribeRoiOptions : CoinmasterRoiOptions;

/// <summary>Tradeable airport-shop seed catalog used for Foreign Bribe research.</summary>
public static class ForeignBribeCatalog
{
    public const string BagOfForeignBribesItemId = "8691";

    public static IReadOnlyList<ForeignBribeCatalogEntry> Entries { get; } =
    [
        new("Beach Buck", "7458", "History's Most Offensive Jokes, Vol. IX", 300),
        new("Beach Buck", "7459", "Now You're Cooking With Grease", 300),
        new("Beach Buck", "7460", "Sloppy Seconds Diner Employee Handbook", 300),
        new("Beach Buck", "7461", "industrial strength anti-fungal spray", 15),
        new("Beach Buck", "7467", "one-day ticket to Spring Break Beach", 100, IsOneDayTicket: true),
        new("Coinspiracy", "7801", "mercenary pistol", 50),
        new("Coinspiracy", "7802", "mercenary rifle", 50),
        new("Coinspiracy", "7774", "specialty ammo bandolier", 1),
        new("Coinspiracy", "7803", "Merc Core Field Manual: Sanity Maintenance", 300),
        new("Coinspiracy", "7804", "Merc Core Field Manual: Intimidation Techniques", 300),
        new("Coinspiracy", "7805", "Merc Core challenge coin", 1000),
        new("FunFunds", "8253", "Scratch-and-Sniff Guide to Dinseylandfill", 50),
        new("FunFunds", "8254", "Trash, a Memoir", 50),
        new("FunFunds", "8255", "Dinsey Maintenance Manual", 50),
        new("FunFunds", "8204", "one-day ticket to Dinseylandfill", 20, IsOneDayTicket: true),
        new("Volcoino", "8482", "Lava Miner's Daughter", 10),
        new("Volcoino", "8483", "Psycho From The Heat", 10),
        new("Volcoino", "8484", "The Firegate Tapes", 10),
        new("Volcoino", "8486", "one-day ticket to That 70s Volcano", 3, IsOneDayTicket: true),
        new("Wal-Mart gift certificate", "8676", "shoulder-warming lotion", 5),
        new("Wal-Mart gift certificate", "8677", "iceberg lettuce", 15),
        new("Wal-Mart gift certificate", "8678", "ice wine", 15),
        new("Wal-Mart gift certificate", "8682", "To Build an Igloo", 150),
        new("Wal-Mart gift certificate", "8683", "The Chill of the Wild", 150),
        new("Wal-Mart gift certificate", "8684", "Cold Fang", 150),
        new("Wal-Mart gift certificate", "8675", "one-day ticket to The Glaciest", 50, IsOneDayTicket: true)
    ];

    public static IReadOnlyDictionary<string, int> OneDayTicketCosts { get; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Beach Buck"] = 100,
            ["FunFunds"] = 20,
            ["Volcoino"] = 3,
            ["Wal-Mart gift certificate"] = 50
        };

    public static IReadOnlyList<ForeignBribeCatalogEntry> FromCoinmasterCatalog(
        CoinmasterCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Entries.Select(x => new ForeignBribeCatalogEntry(
            x.Currency, x.ItemId, x.ItemName, x.CurrencyCost, x.IsTradeable,
            x.IsOneDayTicket, x.RequiresTicket, x.TicketCurrency, x.TicketCost)).ToArray();
    }
}

/// <summary>Read-only Mall + Pricegun ranking for tradeable Foreign Bribe outputs.</summary>
public sealed class ForeignBribePlanner
{
    private readonly KoLMallReadClient _mall;
    private readonly PricegunReadClient _pricegun;
    private readonly IReadOnlyList<ForeignBribeCatalogEntry> _entries;
    private readonly CoinmasterRoiOptions _costs;

    public ForeignBribePlanner(KoLMallReadClient mall, PricegunReadClient pricegun)
        : this(mall, pricegun, ForeignBribeCatalog.Entries, null)
    {
    }

    public ForeignBribePlanner(
        KoLMallReadClient mall,
        PricegunReadClient pricegun,
        CoinmasterRoiOptions options)
        : this(mall, pricegun, ForeignBribeCatalog.Entries, options)
    {
    }

    public ForeignBribePlanner(
        KoLMallReadClient mall,
        PricegunReadClient pricegun,
        CoinmasterCatalog catalog,
        CoinmasterRoiOptions? options = null)
        : this(mall, pricegun, ForeignBribeCatalog.FromCoinmasterCatalog(catalog), options)
    {
    }

    public ForeignBribePlanner(
        KoLMallReadClient mall,
        PricegunReadClient pricegun,
        CoinmasterCatalogSnapshot snapshot,
        CoinmasterRoiOptions? options = null)
        : this(mall, pricegun, ForeignBribeCatalog.FromCoinmasterCatalog(snapshot.ToCatalog()), options)
    {
    }

    public ForeignBribePlanner(
        KoLMallReadClient mall,
        PricegunReadClient pricegun,
        IEnumerable<ForeignBribeCatalogEntry> entries,
        CoinmasterRoiOptions? options = null)
    {
        _mall = mall ?? throw new ArgumentNullException(nameof(mall));
        _pricegun = pricegun ?? throw new ArgumentNullException(nameof(pricegun));
        ArgumentNullException.ThrowIfNull(entries);
        _entries = entries.Select(entry => new ForeignBribeCatalogEntry(
            entry.Currency.Trim(), entry.ItemId.Trim(), entry.ItemName.Trim(), entry.CurrencyCost,
            entry.IsTradeable, entry.IsOneDayTicket, entry.RequiresTicket,
            string.IsNullOrWhiteSpace(entry.TicketCurrency) ? null : entry.TicketCurrency.Trim(),
            entry.TicketCost)).ToArray();
        foreach (var entry in _entries) entry.Validate();
        _costs = options ?? new CoinmasterRoiOptions();
        _costs.Validate();
    }

    public CoinmasterRoiOptions Costs => _costs;
    public IReadOnlyList<ForeignBribeCatalogEntry> Entries => _entries;

    public async Task<IReadOnlyList<ForeignBribeRanking>> RankAsync(
        IReadOnlyDictionary<string, decimal>? currencyValues = null,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default,
        CoinmasterRoiOptions? options = null)
    {
        var policy = options ?? _costs;
        policy.Validate();
        var normalizedCurrencyValues = NormalizeCurrencyValues(currencyValues);
        var work = _entries.Select(entry => RankOneAsync(entry, normalizedCurrencyValues, forceRefresh, cancellationToken, policy));
        var results = await Task.WhenAll(work).ConfigureAwait(false);
        return results
            .OrderByDescending(x => x.BuyCandidate)
            .ThenByDescending(x => x.NetBeforeTicket ?? x.ExpectedResale ?? 0m)
            .ThenBy(x => x.Entry.Currency, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<ForeignBribeRanking> RankOneAsync(
        ForeignBribeCatalogEntry entry,
        IReadOnlyDictionary<string, decimal>? currencyValues,
        bool forceRefresh,
        CancellationToken cancellationToken,
        CoinmasterRoiOptions costs)
    {
        var mall = await _mall.SearchAsync(entry.ItemName, forceRefresh, cancellationToken).ConfigureAwait(false);
        var history = await _pricegun.FetchAsync(entry.ItemId, forceRefresh, cancellationToken).ConfigureAwait(false);
        var prices = history.Sales.Select(x => (decimal)x.UnitPrice).OrderBy(x => x).ToArray();
        var median = prices.Length == 0 ? (decimal?)null : prices.Length % 2 == 1
            ? prices[prices.Length / 2]
            : (prices[prices.Length / 2 - 1] + prices[prices.Length / 2]) / 2m;
        var totalQuantity = history.Sales.Sum(x => (long)x.Quantity);
        var weighted = totalQuantity == 0
            ? (decimal?)null
            : history.Sales.Sum(x => x.UnitPrice * (decimal)x.Quantity) / totalQuantity;
        var lowest = mall.Listings.Count == 0 ? (long?)null : mall.Listings.Min(x => x.Price);
        // A single Mall floor can be an outlier. When enough Pricegun sales
        // exist, use the lower of the live floor and historical median as the
        // conservative resale estimate; keep the Mall floor when history is
        // too sparse to be useful.
        var expected = lowest is not null && history.Sales.Count >= 3 && median is not null
            ? Math.Min((decimal)lowest.Value, median.Value)
            : lowest ?? median;
        var resalePerCurrency = expected is null || entry.CurrencyCost <= 0
            ? (decimal?)null
            : expected.Value / entry.CurrencyCost;
        var currencyValue = currencyValues is not null && currencyValues.TryGetValue(entry.Currency, out var value)
            ? value : (decimal?)null;
        decimal? opportunity = currencyValue is null ? (decimal?)null : entry.CurrencyCost * currencyValue.Value;
        var ticketUnits = entry.TicketCost;
        var mappedTicketUnits = 0;
        if (ticketUnits is null && !entry.IsOneDayTicket)
            ForeignBribeCatalog.OneDayTicketCosts.TryGetValue(entry.TicketCurrency ?? entry.Currency, out mappedTicketUnits);
        else
            mappedTicketUnits = ticketUnits ?? 0;
        if (ticketUnits is null && mappedTicketUnits > 0) ticketUnits = mappedTicketUnits;
        var ticketCurrency = string.IsNullOrWhiteSpace(entry.TicketCurrency) ? entry.Currency : entry.TicketCurrency!;
        decimal? ticketCost = ticketUnits is not null && currencyValues is not null
            && currencyValues.TryGetValue(ticketCurrency, out var ticketValue)
            ? ticketUnits.Value * ticketValue
            : (decimal?)null;
        var saleFee = expected is null ? 0m : expected.Value * costs.SaleFeeRate;
        var knownCosts = costs.ListingFee + costs.TransferCost + costs.HoldingCost
            + costs.BagCostAllocation + costs.SafetyBuffer + saleFee;
        decimal? net = expected is null || opportunity is null
            ? (decimal?)null
            : expected.Value - opportunity.Value - knownCosts;
        var afterTicket = net is null || entry.IsOneDayTicket || ticketCost is null
            ? net
            : net.Value - ticketCost.Value;
        var ticketPriced = ticketUnits is null || ticketCost is not null;
        var historicalHigher = lowest is > 0 && median is not null && median.Value > lowest.Value * 1.10m;
        var candidate = entry.IsTradeable && !entry.IsOneDayTicket && history.Sales.Count >= 3
            && lowest is not null && median is not null && lowest.Value <= median.Value * 0.80m
            && currencyValue is not null && ticketPriced && afterTicket > 0;
        var rationale = candidate
            ? "Mallboden liegt mindestens 20% unter dem Pricegun-Median; als Kaufkandidat prüfen."
            : history.Sales.Count < 3
                ? "Zu wenig Pricegun-Verkäufe für eine belastbare Nachfrageaussage."
                : currencyValue is null
                    ? "Währungswert fehlt; Opportunity Cost vor einem Kauf ergänzen."
                    : !ticketPriced
                        ? "Ticketkosten fehlen; Opportunity Cost vor einem Kauf ergänzen."
                    : "Kein ausreichender Abschlag oder keine positive Nettomarge.";
        return new ForeignBribeRanking(entry, lowest, median, weighted, history.Sales.Count, mall.Listings.Count,
            expected, resalePerCurrency, opportunity, net, ticketCost, historicalHigher, candidate, mall.IsStale, history.IsStale, rationale)
        {
            SaleFeeInMeat = saleFee,
            ListingFeeInMeat = costs.ListingFee,
            TransferCostInMeat = costs.TransferCost,
            HoldingCostInMeat = costs.HoldingCost,
            BagCostAllocationInMeat = costs.BagCostAllocation,
            SafetyBufferInMeat = costs.SafetyBuffer
        };
    }

    private static IReadOnlyDictionary<string, decimal>? NormalizeCurrencyValues(
        IReadOnlyDictionary<string, decimal>? values)
    {
        if (values is null || values.Count == 0) return null;
        var normalized = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values)
        {
            if (string.IsNullOrWhiteSpace(pair.Key)) continue;
            if (pair.Value < 0m) throw new ArgumentOutOfRangeException(nameof(values));
            normalized[pair.Key.Trim()] = pair.Value;
        }
        return normalized;
    }
}
