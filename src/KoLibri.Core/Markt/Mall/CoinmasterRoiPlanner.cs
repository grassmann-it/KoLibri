namespace KoLibri.Core.Markt;

/// <summary>A sell-side observation used by the generic coinmaster planner.</summary>
public record CoinmasterResaleObservation(
    string ItemId,
    decimal? ExpectedResale,
    decimal? MallLowest = null,
    decimal? PricegunMedian = null,
    decimal? PricegunWeightedAverage = null,
    int SalesCount = 0,
    bool IsStale = false)
{
    public decimal? ConservativeResale => ExpectedResale
        ?? (MallLowest is not null && PricegunMedian is not null
            ? Math.Min(MallLowest.Value, PricegunMedian.Value)
            : MallLowest ?? PricegunMedian ?? PricegunWeightedAverage);
}

/// <summary>Short alias for integrations that call observations "resales".</summary>
public sealed record CoinmasterResale(
    string ItemId,
    decimal? ExpectedResale,
    bool IsStale = false)
    : CoinmasterResaleObservation(ItemId, ExpectedResale, IsStale: IsStale);

/// <summary>Controls fees, filtering and safety requirements for ROI plans.</summary>
public record CoinmasterRoiOptions
{
    /// <summary>KoL Mall's sale cut; callers can set this to .01 for live Mall proceeds.</summary>
    public decimal SaleFeeRate { get; init; }
    public decimal SaleFee { get => SaleFeeRate; init => SaleFeeRate = value; }
    public decimal SaleFeePercent { get => SaleFeeRate; init => SaleFeeRate = value; }
    public decimal SaleCommissionRate { get => SaleFeeRate; init => SaleFeeRate = value; }
    /// <summary>Fixed listing fee to subtract from each output unit.</summary>
    public decimal ListingFee { get; init; }
    public decimal TransactionFee { get => ListingFee; init => ListingFee = value; }
    public decimal Fee { get => ListingFee; init => ListingFee = value; }
    public decimal ListingFeeMeat { get => ListingFee; init => ListingFee = value; }
    public decimal MallListingFee { get => ListingFee; init => ListingFee = value; }
    /// <summary>Known transfer/handling cost charged for one output route.</summary>
    public decimal TransferCost { get; init; }
    public decimal TransferCostInMeat { get => TransferCost; init => TransferCost = value; }
    public decimal TransferCostPerUnit { get => TransferCost; init => TransferCost = value; }
    /// <summary>Opportunity cost of holding one output before it sells.</summary>
    public decimal HoldingCost { get; init; }
    public decimal HoldingCostPerDay { get => HoldingCost; init => HoldingCost = value; }
    public decimal HandlingCost { get => HoldingCost; init => HoldingCost = value; }
    public decimal HoldingCostInMeat { get => HoldingCost; init => HoldingCost = value; }
    public decimal HoldingCostPerUnit { get => HoldingCost; init => HoldingCost = value; }
    /// <summary>Optional share of the bag or batch acquisition cost allocated to this output.</summary>
    public decimal BagCostAllocation { get; init; }
    public decimal BagCostInMeat { get => BagCostAllocation; init => BagCostAllocation = value; }
    public decimal AcquisitionCostAllocation { get => BagCostAllocation; init => BagCostAllocation = value; }
    /// <summary>Extra conservative buffer in Meat per output unit.</summary>
    public decimal SafetyBuffer { get; init; }
    public decimal SafetyBufferInMeat { get => SafetyBuffer; init => SafetyBuffer = value; }
    public bool IncludeNonTradeable { get; init; }
    public bool IncludeTickets { get; init; } = true;
    /// <summary>Tickets are route inputs by default, rather than resale candidates.</summary>
    public bool AllowTicketCandidates { get; init; }
    /// <summary>Only routes with a positive net resale are candidates by default.</summary>
    public bool RequirePositiveNet { get; init; } = true;
    /// <summary>
    /// Unknown currency values are review-only. A zero-cost route remains
    /// eligible because it does not consume a currency unit.
    /// </summary>
    public bool RequireKnownCurrency { get; init; } = true;

    public void Validate()
    {
        if (SaleFeeRate < 0m || SaleFeeRate >= 1m) throw new ArgumentOutOfRangeException(nameof(SaleFeeRate));
        if (ListingFee < 0m) throw new ArgumentOutOfRangeException(nameof(ListingFee));
        if (TransferCost < 0m) throw new ArgumentOutOfRangeException(nameof(TransferCost));
        if (HoldingCost < 0m) throw new ArgumentOutOfRangeException(nameof(HoldingCost));
        if (BagCostAllocation < 0m) throw new ArgumentOutOfRangeException(nameof(BagCostAllocation));
        if (SafetyBuffer < 0m) throw new ArgumentOutOfRangeException(nameof(SafetyBuffer));
    }
}

/// <summary>
/// One ranked conversion route.  Nullable values distinguish an unknown market
/// or currency price from a known zero-profit route.
/// </summary>
public record CoinmasterRoiPlan(
    CoinmasterCatalogEntry Entry,
    decimal? ExpectedResale,
    decimal? CurrencyOpportunityCost,
    decimal? CurrencyCostInMeat,
    decimal? TicketCostInMeat,
    decimal? NetResale,
    bool IsTradeable,
    bool IsCandidate,
    string Rationale,
    bool IsStale = false)
{
    public string Currency => Entry.Currency;
    public string ItemId => Entry.ItemId;
    public string ItemName => Entry.ItemName;
    public int CurrencyCost => Entry.CurrencyCost;
    public bool IsOneDayTicket => Entry.IsOneDayTicket;

    // Descriptive aliases keep consumers from having to know the exact
    // accounting vocabulary used by this model.
    public decimal? NetMeat => NetResale;
    public decimal? NetProfit => NetResale;
    public decimal? ExpectedNetResale => NetResale;
    public decimal? NetResaleAfterTicket => NetResale;
    public decimal? OpportunityCost => CurrencyCostInMeat;
    public decimal? TotalCurrencyOpportunityCost => CurrencyCostInMeat;
    public decimal? CurrencyOpportunityCostPerUnit => CurrencyOpportunityCost;
    public decimal? CurrencyValueInMeat => CurrencyOpportunityCost;
    public decimal? GrossResale => ExpectedResale;
    public bool BuyCandidate => IsCandidate;
    public bool IsTicket => IsOneDayTicket;

    /// <summary>Sale commission in Meat, when a resale observation exists.</summary>
    public decimal? SaleFeeInMeat { get; init; }
    public decimal? SaleFee => SaleFeeInMeat;
    public decimal ListingFeeInMeat { get; init; }
    public decimal TransferCostInMeat { get; init; }
    public decimal HoldingCostInMeat { get; init; }
    public decimal BagCostAllocationInMeat { get; init; }
    public decimal SafetyBufferInMeat { get; init; }
    public decimal? TotalCostsInMeat { get; init; }
    public decimal? TotalCostInMeat => TotalCostsInMeat;
    public decimal? NetResaleAfterCosts => NetResale;
}

/// <summary>Alias used by callers that model each result as a ranking row.</summary>
public sealed record CoinmasterRoiResult(
    CoinmasterCatalogEntry Entry,
    decimal? ExpectedResale,
    decimal? CurrencyOpportunityCost,
    decimal? CurrencyCostInMeat,
    decimal? TicketCostInMeat,
    decimal? NetResale,
    bool IsTradeable,
    bool IsCandidate,
    string Rationale,
    bool IsStale = false)
    : CoinmasterRoiPlan(Entry, ExpectedResale, CurrencyOpportunityCost,
        CurrencyCostInMeat, TicketCostInMeat, NetResale, IsTradeable,
        IsCandidate, Rationale, IsStale);

/// <summary>
/// Calculates the opportunity cost and net resale of every catalog edge.
/// This type is pure and read-only: it consumes snapshots supplied by callers
/// and never buys a ticket, opens a bag, or submits a Mall request.
/// </summary>
public class CoinmasterRoiPlanner
{
    private readonly CoinmasterConversionGraph _graph;
    private readonly IReadOnlyDictionary<string, decimal> _currencyValues;
    private readonly CoinmasterRoiOptions _options;

    public CoinmasterRoiPlanner(
        CoinmasterCatalog catalog,
        IReadOnlyDictionary<string, decimal>? currencyOpportunityCosts = null,
        CoinmasterRoiOptions? options = null)
        : this(new CoinmasterConversionGraph(catalog), currencyOpportunityCosts, options)
    {
    }

    public CoinmasterRoiPlanner(
        CoinmasterConversionGraph graph,
        IReadOnlyDictionary<string, decimal>? currencyOpportunityCosts = null,
        CoinmasterRoiOptions? options = null)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _currencyValues = NormalizeCurrencyValues(currencyOpportunityCosts);
        _options = options ?? new CoinmasterRoiOptions();
        _options.Validate();
    }

    public CoinmasterConversionGraph Graph => _graph;
    public CoinmasterRoiOptions Options => _options;
    public IReadOnlyDictionary<string, decimal> CurrencyOpportunityCosts => _currencyValues;

    public static IReadOnlyList<CoinmasterRoiPlan> Plan(
        CoinmasterCatalog catalog,
        IReadOnlyDictionary<string, decimal> resaleByItemId,
        IReadOnlyDictionary<string, decimal>? currencyOpportunityCosts = null,
        CoinmasterRoiOptions? options = null) =>
        new CoinmasterRoiPlanner(catalog, currencyOpportunityCosts, options)
            .Plan(resaleByItemId);

    public static IReadOnlyList<CoinmasterRoiPlan> Rank(
        CoinmasterCatalog catalog,
        IReadOnlyDictionary<string, decimal> resaleByItemId,
        IReadOnlyDictionary<string, decimal>? currencyOpportunityCosts = null,
        CoinmasterRoiOptions? options = null) =>
        Plan(catalog, resaleByItemId, currencyOpportunityCosts, options);

    public static IReadOnlyList<CoinmasterRoiPlan> Plan(
        IEnumerable<CoinmasterCatalogEntry> entries,
        IReadOnlyDictionary<string, decimal> resaleByItemId,
        IReadOnlyDictionary<string, decimal>? currencyOpportunityCosts = null,
        CoinmasterRoiOptions? options = null) =>
        Plan(new CoinmasterCatalog(entries), resaleByItemId, currencyOpportunityCosts, options);

    public static decimal? CalculateNetResale(
        decimal? expectedResale,
        int currencyCost,
        decimal? currencyOpportunityCost,
        decimal? ticketCostInMeat = null,
        CoinmasterRoiOptions? options = null)
    {
        if (expectedResale is null || currencyOpportunityCost is null) return null;
        var settings = options ?? new CoinmasterRoiOptions();
        settings.Validate();
        var sale = expectedResale.Value * (1m - settings.SaleFeeRate)
            - settings.ListingFee - settings.TransferCost - settings.HoldingCost
            - settings.BagCostAllocation - settings.SafetyBuffer;
        return sale - currencyCost * currencyOpportunityCost.Value - (ticketCostInMeat ?? 0m);
    }

    /// <summary>Derives per-unit currency values from the supplied alternative outputs.</summary>
    public IReadOnlyDictionary<string, decimal> ComputeCurrencyOpportunityCosts(
        IReadOnlyDictionary<string, decimal> resaleByItemId) =>
        _graph.DeriveCurrencyOpportunityCosts(resaleByItemId);

    /// <summary>
    /// Rank rows from the supplied expected resale values.  Missing resale or
    /// currency values remain review-only rows with a null net value.
    /// </summary>
    public IReadOnlyList<CoinmasterRoiPlan> Plan(
        IReadOnlyDictionary<string, decimal> resaleByItemId,
        IReadOnlyDictionary<string, decimal>? currencyOpportunityCosts = null)
    {
        ArgumentNullException.ThrowIfNull(resaleByItemId);
        var resale = resaleByItemId.ToDictionary(x => x.Key.Trim(), x => (decimal?)x.Value,
            StringComparer.OrdinalIgnoreCase);
        return PlanCore(resale, currencyOpportunityCosts);
    }

    /// <summary>Rank rows from full Mall/Pricegun observations.</summary>
    public IReadOnlyList<CoinmasterRoiPlan> Plan(
        IEnumerable<CoinmasterResaleObservation> observations,
        IReadOnlyDictionary<string, decimal>? currencyOpportunityCosts = null)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var resale = observations
            .Where(x => !string.IsNullOrWhiteSpace(x.ItemId))
            .GroupBy(x => x.ItemId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Last().ConservativeResale,
                StringComparer.OrdinalIgnoreCase);
        var stale = observations
            .Where(x => !string.IsNullOrWhiteSpace(x.ItemId))
            .GroupBy(x => x.ItemId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Last().IsStale, StringComparer.OrdinalIgnoreCase);
        return PlanCore(resale, currencyOpportunityCosts, stale);
    }

    /// <summary>Convenience overload accepting nullable resale values.</summary>
    public IReadOnlyList<CoinmasterRoiPlan> Plan(
        IReadOnlyDictionary<string, decimal?> resaleByItemId,
        IReadOnlyDictionary<string, decimal>? currencyOpportunityCosts = null)
    {
        ArgumentNullException.ThrowIfNull(resaleByItemId);
        var resale = resaleByItemId.ToDictionary(x => x.Key.Trim(), x => x.Value,
            StringComparer.OrdinalIgnoreCase);
        return PlanCore(resale, currencyOpportunityCosts);
    }

    public IReadOnlyList<CoinmasterRoiPlan> Rank(
        IReadOnlyDictionary<string, decimal> resaleByItemId,
        IReadOnlyDictionary<string, decimal>? currencyOpportunityCosts = null) =>
        Plan(resaleByItemId, currencyOpportunityCosts);

    public IReadOnlyList<CoinmasterRoiPlan> Rank(
        IEnumerable<CoinmasterResaleObservation> observations,
        IReadOnlyDictionary<string, decimal>? currencyOpportunityCosts = null) =>
        Plan(observations, currencyOpportunityCosts);

    /// <summary>
    /// Evaluates one row without constructing a temporary result list.
    /// </summary>
    public CoinmasterRoiPlan Evaluate(
        CoinmasterCatalogEntry entry,
        decimal? expectedResale,
        IReadOnlyDictionary<string, decimal>? currencyOpportunityCosts = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        // Evaluate remains useful for an imported row that is not yet part of
        // the planner's catalog.  Keep the same options and currency values
        // while evaluating that single edge in a temporary graph.
        if (!_graph.Edges.Any(x => string.Equals(x.ItemId, entry.ItemId, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(x.Currency, entry.Currency, StringComparison.OrdinalIgnoreCase)))
        {
            var temporary = new CoinmasterRoiPlanner(
                new CoinmasterConversionGraph(new[] { entry }, _graph.Catalog.TicketCosts),
                _currencyValues,
                _options);
            return temporary.Evaluate(entry, expectedResale, currencyOpportunityCosts);
        }
        var resale = new Dictionary<string, decimal?>(StringComparer.OrdinalIgnoreCase)
        {
            [entry.ItemId] = expectedResale
        };
        return PlanCore(resale, currencyOpportunityCosts)
            .Single(x => string.Equals(x.ItemId, entry.ItemId, StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<CoinmasterRoiPlan> PlanCore(
        IReadOnlyDictionary<string, decimal?> resale,
        IReadOnlyDictionary<string, decimal>? explicitCurrencyValues,
        IReadOnlyDictionary<string, bool>? stale = null)
    {
        var values = MergeCurrencyValues(explicitCurrencyValues);
        var explicitCurrencyKeys = values.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // If no explicit value exists for a currency, use the best tradeable
        // alternative in this same graph. This makes a catalog self-contained
        // while allowing callers to override it with a live observed value.
        foreach (var pair in _graph.DeriveCurrencyOpportunityCosts(
                     resale.Where(x => x.Value is not null)
                         .ToDictionary(x => x.Key, x => x.Value!.Value,
                             StringComparer.OrdinalIgnoreCase)))
            values.TryAdd(pair.Key, pair.Value);

        var plans = new List<CoinmasterRoiPlan>(_graph.Edges.Count);
        foreach (var edge in _graph.Edges)
        {
            if (!_options.IncludeTickets && edge.IsOneDayTicket) continue;
            resale.TryGetValue(edge.ItemId, out var expected);
            var hasCurrencyValue = values.TryGetValue(edge.Currency, out var currencyValue);
            // A zero-valued currency is a known (free) input, whereas a
            // missing dictionary entry is represented by null.  Keep the
            // distinction so free event currencies still produce a route.
            var currencyCost = edge.CurrencyCost == 0
                ? 0m
                : hasCurrencyValue && currencyValue >= 0m
                    ? edge.CurrencyCost * currencyValue
                : (decimal?)null;

            decimal? ticketCost = null;
            if (!edge.IsOneDayTicket && edge.RequiresTicket)
            {
                var ticketUnits = edge.TicketCost;
                var catalogUnits = 0;
                if (ticketUnits is null)
                    _graph.Catalog.TryGetTicketCost(edge.TicketCurrency ?? edge.Currency, out catalogUnits);
                else
                    catalogUnits = ticketUnits.Value;

                if (ticketUnits is null && catalogUnits > 0) ticketUnits = catalogUnits;
                var ticketCurrency = string.IsNullOrWhiteSpace(edge.TicketCurrency)
                    ? edge.Currency : edge.TicketCurrency!;
                if (ticketUnits is not null && values.TryGetValue(ticketCurrency, out var ticketValue))
                    ticketCost = ticketUnits.Value * ticketValue;
            }

            // A value inferred only from the same output is not an observed
            // currency quote. It makes the row algebraically break even and
            // must remain review-only until a separate output or an explicit
            // quote anchors the currency value.
            var derivedFromAlternative = _graph.Edges.Any(other =>
                !ReferenceEquals(other, edge)
                && string.Equals(other.Currency, edge.Currency, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(other.ItemId, edge.ItemId, StringComparison.OrdinalIgnoreCase)
                && other.IsTradeable && !other.IsOneDayTicket && other.CurrencyCost > 0
                && resale.TryGetValue(other.ItemId, out var alternativeResale)
                && alternativeResale is not null);
            var hasExplicitCurrencyValue = explicitCurrencyKeys.Contains(edge.Currency);
            var knownCurrency = edge.CurrencyCost == 0 || hasExplicitCurrencyValue || derivedFromAlternative;
            if (_options.RequireKnownCurrency && !knownCurrency)
            {
                currencyCost = null;
                if (edge.RequiresTicket) ticketCost = null;
            }

            decimal? net = null;
            if (expected is not null && currencyCost is not null)
            {
                var sale = expected.Value * (1m - _options.SaleFeeRate)
                    - _options.ListingFee - _options.TransferCost - _options.HoldingCost
                    - _options.BagCostAllocation - _options.SafetyBuffer;
                net = sale - currencyCost.Value - (ticketCost ?? 0m);
            }

            var ticketPriced = !edge.RequiresTicket || ticketCost is not null;
            var eligible = edge.IsTradeable && expected is not null && currencyCost is not null
                && ticketPriced && (!_options.RequireKnownCurrency || knownCurrency);
            var candidate = eligible && (_options.AllowTicketCandidates || !edge.IsOneDayTicket) &&
                (!_options.RequirePositiveNet || net > 0m) &&
                (!_options.IncludeNonTradeable || edge.IsTradeable);
            var reason = BuildRationale(edge, expected, currencyCost, ticketCost, net, candidate,
                knownCurrency || !_options.RequireKnownCurrency);
            decimal? saleFee = expected is null ? null : expected.Value * _options.SaleFeeRate;
            var knownCosts = expected is null ? (decimal?)null
                : (saleFee ?? 0m) + _options.ListingFee + _options.TransferCost
                    + _options.HoldingCost + _options.BagCostAllocation + _options.SafetyBuffer;
            var totalCosts = knownCosts is null || currencyCost is null || ticketCost is null && edge.RequiresTicket
                ? (decimal?)null
                : knownCosts.Value + currencyCost.Value + (ticketCost ?? 0m);
            plans.Add(new CoinmasterRoiPlan(edge.ToCatalogEntry(), expected,
                knownCurrency && values.TryGetValue(edge.Currency, out var opportunity) ? opportunity : null,
                currencyCost, ticketCost, net, edge.IsTradeable, candidate, reason,
                stale is not null && stale.TryGetValue(edge.ItemId, out var rowStale) && rowStale)
            {
                SaleFeeInMeat = saleFee,
                ListingFeeInMeat = _options.ListingFee,
                TransferCostInMeat = _options.TransferCost,
                HoldingCostInMeat = _options.HoldingCost,
                BagCostAllocationInMeat = _options.BagCostAllocation,
                SafetyBufferInMeat = _options.SafetyBuffer,
                TotalCostsInMeat = totalCosts
            });
        }

        return plans
            .OrderByDescending(x => x.IsCandidate)
            .ThenByDescending(x => x.NetResale ?? decimal.MinValue)
            .ThenBy(x => x.Currency, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private Dictionary<string, decimal> MergeCurrencyValues(
        IReadOnlyDictionary<string, decimal>? explicitCurrencyValues)
    {
        var result = new Dictionary<string, decimal>(_currencyValues, StringComparer.OrdinalIgnoreCase);
        if (explicitCurrencyValues is null) return result;
        foreach (var pair in explicitCurrencyValues)
        {
            if (string.IsNullOrWhiteSpace(pair.Key)) continue;
            if (pair.Value < 0m) throw new ArgumentOutOfRangeException(nameof(explicitCurrencyValues));
            result[pair.Key.Trim()] = pair.Value;
        }
        return result;
    }

    private static IReadOnlyDictionary<string, decimal> NormalizeCurrencyValues(
        IReadOnlyDictionary<string, decimal>? values)
    {
        if (values is null || values.Count == 0)
            return new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values)
        {
            if (string.IsNullOrWhiteSpace(pair.Key)) continue;
            if (pair.Value < 0m) throw new ArgumentOutOfRangeException(nameof(values));
            result[pair.Key.Trim()] = pair.Value;
        }
        return result;
    }

    private static string BuildRationale(
        CoinmasterConversionEdge edge,
        decimal? expected,
        decimal? currencyCost,
        decimal? ticketCost,
        decimal? net,
        bool candidate,
        bool knownCurrency = true)
    {
        if (!edge.IsTradeable) return "Output is not tradeable.";
        if (expected is null) return "No resale observation is available.";
        if (!knownCurrency) return "Currency opportunity cost is unknown.";
        if (currencyCost is null) return "Currency opportunity cost is unknown.";
        if (edge.RequiresTicket && ticketCost is null) return "Required ticket cost is unknown.";
        if (candidate) return "Tradeable output has a fully priced route.";
        return net is <= 0m ? "Net resale is not positive after conversion costs." : "Review route and market assumptions.";
    }
}

/// <summary>Airport-specific naming alias for the generic planner.</summary>
public sealed class AirportRoiPlanner : CoinmasterRoiPlanner
{
    public AirportRoiPlanner(CoinmasterCatalog catalog,
        IReadOnlyDictionary<string, decimal>? currencyOpportunityCosts = null,
        CoinmasterRoiOptions? options = null)
        : base(catalog, currencyOpportunityCosts, options) { }

    public AirportRoiPlanner(CoinmasterConversionGraph graph,
        IReadOnlyDictionary<string, decimal>? currencyOpportunityCosts = null,
        CoinmasterRoiOptions? options = null)
        : base(graph, currencyOpportunityCosts, options) { }
}
