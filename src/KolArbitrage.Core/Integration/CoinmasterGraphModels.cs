namespace KolArbitrage.Core.Integration;

/// <summary>
/// A single row in a coinmaster (including an airport shop).  Costs are
/// expressed in whole currency units, as they are in KoL's shop data.
/// </summary>
public record CoinmasterCatalogEntry(
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
    public static implicit operator CoinmasterCatalogEntry(ForeignBribeCatalogEntry entry) =>
        new(entry.Currency, entry.ItemId, entry.ItemName, entry.CurrencyCost,
            entry.IsTradeable, entry.IsOneDayTicket, entry.RequiresTicket,
            entry.TicketCurrency, entry.TicketCost);

    /// <summary>Compatibility overload for catalogs that put ticket units in the seventh column.</summary>
    public CoinmasterCatalogEntry(
        string currency,
        string itemId,
        string itemName,
        int currencyCost,
        bool isTradeable,
        bool isOneDayTicket,
        int? ticketCost)
        : this(currency, itemId, itemName, currencyCost, isTradeable,
            isOneDayTicket, true, null, ticketCost)
    {
    }

    public string NormalizedCurrency => Currency.Trim();
    public string NormalizedItemId => ItemId.Trim();
    public string OutputItemId => ItemId;
    public int Cost => CurrencyCost;
    public bool IsTicket => IsOneDayTicket;
    public bool Tradeable => IsTradeable;

    /// <summary>Currency used to purchase the required ticket, when one exists.</summary>
    public string EffectiveTicketCurrency =>
        string.IsNullOrWhiteSpace(TicketCurrency) ? NormalizedCurrency : TicketCurrency.Trim();

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Currency)) throw new ArgumentException("Currency is required.", nameof(Currency));
        if (string.IsNullOrWhiteSpace(ItemId)) throw new ArgumentException("Item id is required.", nameof(ItemId));
        if (string.IsNullOrWhiteSpace(ItemName)) throw new ArgumentException("Item name is required.", nameof(ItemName));
        if (CurrencyCost < 0) throw new ArgumentOutOfRangeException(nameof(CurrencyCost));
        if (TicketCost is < 0) throw new ArgumentOutOfRangeException(nameof(TicketCost));
        if (RequiresTicket && TicketCost is null && string.IsNullOrWhiteSpace(TicketCurrency))
        {
            // A catalog-level ticket map can fill this in later.  Requiring a
            // currency or an explicit cost here would make importing raw
            // coinmasters.txt rows needlessly difficult.
        }
    }
}

/// <summary>
/// Naming alias useful for importers that call rows "records".
/// </summary>
public sealed record CoinmasterCatalogRecord(
    string Currency,
    string ItemId,
    string ItemName,
    int CurrencyCost,
    bool IsTradeable = true,
    bool IsOneDayTicket = false,
    bool RequiresTicket = false,
    string? TicketCurrency = null,
    int? TicketCost = null)
    : CoinmasterCatalogEntry(Currency, ItemId, ItemName, CurrencyCost, IsTradeable,
        IsOneDayTicket, RequiresTicket, TicketCurrency, TicketCost)
{
    public CoinmasterCatalogRecord(
        string currency,
        string itemId,
        string itemName,
        int currencyCost,
        bool isTradeable,
        bool isOneDayTicket,
        int? ticketCost)
        : this(currency, itemId, itemName, currencyCost, isTradeable,
            isOneDayTicket, true, null, ticketCost)
    {
    }
}

/// <summary>One-day ticket price in a currency.</summary>
public sealed record CoinmasterTicketCost(
    string Currency,
    int CurrencyUnits,
    string? ItemId = null,
    string? ItemName = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Currency)) throw new ArgumentException("Currency is required.", nameof(Currency));
        if (CurrencyUnits <= 0) throw new ArgumentOutOfRangeException(nameof(CurrencyUnits));
    }
}

/// <summary>
/// Immutable catalog of all rows and optional one-day ticket prices.
/// </summary>
public class CoinmasterCatalog
{
    private readonly IReadOnlyList<CoinmasterCatalogEntry> _entries;
    private readonly IReadOnlyDictionary<string, int> _ticketCosts;

    /// <summary>Optional provenance for a dynamically imported catalog.</summary>
    public CoinmasterCatalogManifest? Manifest { get; }

    public CoinmasterCatalog(
        IEnumerable<CoinmasterCatalogEntry> entries,
        IReadOnlyDictionary<string, int>? ticketCosts = null)
        : this(entries, ticketCosts, null)
    {
    }

    public CoinmasterCatalog(
        IEnumerable<CoinmasterCatalogEntry> entries,
        IReadOnlyDictionary<string, int>? ticketCosts,
        CoinmasterCatalogManifest? manifest)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var rows = entries.Select(row => new CoinmasterCatalogEntry(
            row.Currency.Trim(), row.ItemId.Trim(), row.ItemName.Trim(), row.CurrencyCost,
            row.IsTradeable, row.IsOneDayTicket, row.RequiresTicket,
            string.IsNullOrWhiteSpace(row.TicketCurrency) ? null : row.TicketCurrency.Trim(),
            row.TicketCost)).ToArray();
        foreach (var row in rows) row.Validate();
        _entries = Array.AsReadOnly(rows);
        _ticketCosts = ToTicketDictionary(ticketCosts);
        Manifest = manifest;
    }

    public CoinmasterCatalog(
        IEnumerable<CoinmasterCatalogEntry> entries,
        IEnumerable<CoinmasterTicketCost> ticketCosts)
        : this(entries, ticketCosts?.ToDictionary(x => x.Currency, x => x.CurrencyUnits,
            StringComparer.OrdinalIgnoreCase))
    {
    }

    public CoinmasterCatalog(
        IEnumerable<CoinmasterCatalogEntry> entries,
        IEnumerable<CoinmasterTicketCost> ticketCosts,
        CoinmasterCatalogManifest? manifest)
        : this(entries, ticketCosts?.ToDictionary(x => x.Currency, x => x.CurrencyUnits,
            StringComparer.OrdinalIgnoreCase), manifest)
    {
    }

    public IReadOnlyList<CoinmasterCatalogEntry> Entries => _entries;
    public IReadOnlyDictionary<string, int> TicketCosts => _ticketCosts;

    public static CoinmasterCatalog FromSnapshot(CoinmasterCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.ToCatalog();
    }

    public IEnumerable<CoinmasterCatalogEntry> ForCurrency(string currency)
    {
        if (string.IsNullOrWhiteSpace(currency)) return [];
        return _entries.Where(x => string.Equals(x.Currency, currency.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public bool TryGetTicketCost(string? currency, out int units)
    {
        if (string.IsNullOrWhiteSpace(currency))
        {
            units = 0;
            return false;
        }
        return _ticketCosts.TryGetValue(currency.Trim(), out units);
    }

    /// <summary>Build a generic catalog from the original Foreign Bribe seed.</summary>
    public static CoinmasterCatalog FromForeignBribes()
    {
        var rows = ForeignBribeCatalog.Entries.Select(x => new CoinmasterCatalogEntry(
            x.Currency, x.ItemId, x.ItemName, x.CurrencyCost, x.IsTradeable, x.IsOneDayTicket));
        return new CoinmasterCatalog(rows, ForeignBribeCatalog.OneDayTicketCosts);
    }

    private static IReadOnlyDictionary<string, int> ToTicketDictionary(IReadOnlyDictionary<string, int>? costs)
    {
        if (costs is null || costs.Count == 0)
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in costs)
        {
            if (string.IsNullOrWhiteSpace(pair.Key)) throw new ArgumentException("Ticket currency is required.", nameof(costs));
            if (pair.Value <= 0) throw new ArgumentOutOfRangeException(nameof(costs), "Ticket costs must be positive.");
            result[pair.Key.Trim()] = pair.Value;
        }
        return result;
    }
}

/// <summary>Airport catalog naming alias.</summary>
public sealed class AirportCatalog : CoinmasterCatalog
{
    public AirportCatalog(IEnumerable<CoinmasterCatalogEntry> entries,
        IReadOnlyDictionary<string, int>? ticketCosts = null)
        : base(entries, ticketCosts)
    {
    }

    public AirportCatalog(
        IEnumerable<CoinmasterCatalogEntry> entries,
        IReadOnlyDictionary<string, int>? ticketCosts,
        CoinmasterCatalogManifest? manifest)
        : base(entries, ticketCosts, manifest)
    {
    }

    public new static AirportCatalog FromForeignBribes() =>
        new(ForeignBribeCatalog.Entries.Select(x => new CoinmasterCatalogEntry(
            x.Currency, x.ItemId, x.ItemName, x.CurrencyCost, x.IsTradeable, x.IsOneDayTicket)),
            ForeignBribeCatalog.OneDayTicketCosts);
}

/// <summary>
/// Directed currency-to-item edge used by conversion graph consumers.
/// </summary>
public sealed record CoinmasterConversionEdge(
    string Currency,
    string ItemId,
    string ItemName,
    int CurrencyCost,
    bool IsTradeable,
    bool IsOneDayTicket,
    bool RequiresTicket,
    string? TicketCurrency,
    int? TicketCost)
{
    public CoinmasterConversionEdge(CoinmasterCatalogEntry entry)
        : this(entry.Currency, entry.ItemId, entry.ItemName, entry.CurrencyCost,
            entry.IsTradeable, entry.IsOneDayTicket, entry.RequiresTicket,
            entry.TicketCurrency, entry.TicketCost)
    {
    }

    public CoinmasterCatalogEntry ToCatalogEntry() => new(
        Currency, ItemId, ItemName, CurrencyCost, IsTradeable, IsOneDayTicket,
        RequiresTicket, TicketCurrency, TicketCost);
}

/// <summary>
/// Conversion graph for coinmaster and airport rows.  It deliberately remains
/// read-only; graph construction never submits a game request.
/// </summary>
public class CoinmasterConversionGraph
{
    private readonly IReadOnlyList<CoinmasterConversionEdge> _edges;

    public CoinmasterConversionGraph(CoinmasterCatalog catalog)
    {
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _edges = Array.AsReadOnly(catalog.Entries.Select(x => new CoinmasterConversionEdge(x)).ToArray());
    }

    public CoinmasterConversionGraph(IEnumerable<CoinmasterCatalogEntry> entries,
        IReadOnlyDictionary<string, int>? ticketCosts = null)
        : this(new CoinmasterCatalog(entries, ticketCosts))
    {
    }

    public CoinmasterCatalog Catalog { get; }
    public IReadOnlyList<CoinmasterConversionEdge> Edges => _edges;
    public IReadOnlyCollection<string> Currencies =>
        _edges.Select(x => x.Currency).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static CoinmasterConversionGraph Build(
        IEnumerable<CoinmasterCatalogEntry> entries,
        IReadOnlyDictionary<string, int>? ticketCosts = null) =>
        new(entries, ticketCosts);

    public IEnumerable<CoinmasterConversionEdge> FromCurrency(string currency) =>
        _edges.Where(x => string.Equals(x.Currency, currency?.Trim(), StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<CoinmasterConversionEdge> GetEdges(string currency) =>
        FromCurrency(currency).ToArray();

    public IReadOnlyList<CoinmasterConversionEdge> OutputsFor(string currency) =>
        GetEdges(currency);

    public bool TryGetEdge(string itemId, out CoinmasterConversionEdge edge)
    {
        edge = _edges.FirstOrDefault(x => string.Equals(x.ItemId, itemId?.Trim(), StringComparison.OrdinalIgnoreCase))!;
        return edge is not null;
    }

    /// <summary>
    /// Returns the best observed resale per currency unit for each currency.
    /// Non-tradeable rows and tickets are excluded from the opportunity-cost
    /// baseline because they cannot be sold as ordinary outputs.
    /// </summary>
    public IReadOnlyDictionary<string, decimal> DeriveCurrencyOpportunityCosts(
        IReadOnlyDictionary<string, decimal> resaleByItemId)
    {
        ArgumentNullException.ThrowIfNull(resaleByItemId);
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in _edges.Where(x => x.IsTradeable && !x.IsOneDayTicket)
                     .GroupBy(x => x.Currency, StringComparer.OrdinalIgnoreCase))
        {
            var alternatives = group
                .Where(x => x.CurrencyCost > 0)
                .Select(x => resaleByItemId.TryGetValue(x.ItemId, out var value) && value >= 0m
                    ? (decimal?)(value / x.CurrencyCost)
                    : null)
                .Where(x => x is not null)
                .Select(x => x!.Value)
                .ToArray();
            if (alternatives.Length > 0) result[group.Key] = alternatives.Max();
        }
        return result;
    }

    public IReadOnlyDictionary<string, decimal> CalculateCurrencyOpportunityCosts(
        IReadOnlyDictionary<string, decimal> resaleByItemId) =>
        DeriveCurrencyOpportunityCosts(resaleByItemId);
}

/// <summary>Airport graph naming alias.</summary>
public sealed class AirportConversionGraph : CoinmasterConversionGraph
{
    public AirportConversionGraph(CoinmasterCatalog catalog) : base(catalog) { }
    public AirportConversionGraph(IEnumerable<CoinmasterCatalogEntry> entries,
        IReadOnlyDictionary<string, int>? ticketCosts = null)
        : base(entries, ticketCosts) { }

    public static AirportConversionGraph FromForeignBribes() =>
        new(AirportCatalog.FromForeignBribes());

    public static new AirportConversionGraph Build(
        IEnumerable<CoinmasterCatalogEntry> entries,
        IReadOnlyDictionary<string, int>? ticketCosts = null) =>
        new(entries, ticketCosts);
}

/// <summary>Short naming alias used by callers that do not distinguish edge type.</summary>
public sealed class CoinmasterGraph : CoinmasterConversionGraph
{
    public CoinmasterGraph(CoinmasterCatalog catalog) : base(catalog) { }
    public CoinmasterGraph(IEnumerable<CoinmasterCatalogEntry> entries,
        IReadOnlyDictionary<string, int>? ticketCosts = null)
        : base(entries, ticketCosts) { }
}
