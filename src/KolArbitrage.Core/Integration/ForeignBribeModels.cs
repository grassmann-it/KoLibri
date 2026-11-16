namespace KolArbitrage.Core.Integration;

public sealed record ForeignBribeCatalogEntry(
    string Currency,
    string ItemId,
    string ItemName,
    int CurrencyCost,
    bool IsTradeable = true,
    bool IsOneDayTicket = false);

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
    string Rationale);

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
}

/// <summary>Read-only Mall + Pricegun ranking for tradeable Foreign Bribe outputs.</summary>
public sealed class ForeignBribePlanner
{
    private readonly KoLMallReadClient _mall;
    private readonly PricegunReadClient _pricegun;

    public ForeignBribePlanner(KoLMallReadClient mall, PricegunReadClient pricegun)
    {
        _mall = mall ?? throw new ArgumentNullException(nameof(mall));
        _pricegun = pricegun ?? throw new ArgumentNullException(nameof(pricegun));
    }

    public async Task<IReadOnlyList<ForeignBribeRanking>> RankAsync(
        IReadOnlyDictionary<string, decimal>? currencyValues = null,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var work = ForeignBribeCatalog.Entries.Select(entry => RankOneAsync(entry, currencyValues, forceRefresh, cancellationToken));
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
        CancellationToken cancellationToken)
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
        var currencyValue = currencyValues is not null && currencyValues.TryGetValue(entry.Currency, out var value) ? value : (decimal?)null;
        decimal? opportunity = currencyValue is null ? (decimal?)null : entry.CurrencyCost * currencyValue.Value;
        decimal? net = expected is null || opportunity is null ? (decimal?)null : expected.Value - opportunity.Value;
        decimal? ticketCost = ForeignBribeCatalog.OneDayTicketCosts.TryGetValue(entry.Currency, out var ticketUnits)
            && currencyValue is not null ? (decimal?)(ticketUnits * currencyValue.Value) : (decimal?)null;
        var historicalHigher = lowest is > 0 && median is not null && median.Value > lowest.Value * 1.10m;
        var candidate = entry.IsTradeable && !entry.IsOneDayTicket && history.Sales.Count >= 3
            && lowest is not null && median is not null && lowest.Value <= median.Value * 0.80m
            && (currencyValue is null || net > 0);
        var rationale = candidate
            ? "Mallboden liegt mindestens 20% unter dem Pricegun-Median; als Kaufkandidat prüfen."
            : history.Sales.Count < 3
                ? "Zu wenig Pricegun-Verkäufe für eine belastbare Nachfrageaussage."
                : currencyValue is null
                    ? "Währungswert fehlt; Opportunity Cost vor einem Kauf ergänzen."
                    : "Kein ausreichender Abschlag oder keine positive Nettomarge.";
        return new ForeignBribeRanking(entry, lowest, median, weighted, history.Sales.Count, mall.Listings.Count,
            expected, resalePerCurrency, opportunity, net, ticketCost, historicalHigher, candidate, mall.IsStale, history.IsStale, rationale);
    }
}
