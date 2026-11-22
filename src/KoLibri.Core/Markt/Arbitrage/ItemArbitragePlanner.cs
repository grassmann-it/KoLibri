using System.Globalization;

namespace KoLibri.Core.Markt;

/// <summary>Default, minimum and maximum of every arbitrage setting.</summary>
public static class ArbitrageLimits
{
    public const long DefaultBudget = 2_000_000, MinBudget = 1, MaxBudget = 1_000_000_000;
    public const long DefaultMeatReserve = 0, MinMeatReserve = 0, MaxMeatReserve = 1_000_000_000;
    /// <summary>Minimum margin below the resale price, in percent of that price.</summary>
    public const double DefaultMarginPercent = 10, MinMarginPercent = 0, MaxMarginPercent = 90;
    /// <summary>Mall tax taken from the resale price, in percent (0 when none applies).</summary>
    public const double DefaultMallTaxPercent = 0, MinMallTaxPercent = 0, MaxMallTaxPercent = 50;
    public const int DefaultUnitsPerRun = 200, MinUnitsPerRun = 1, MaxUnitsPerRun = 10_000;
    /// <summary>Units planned from an offer whose daily limit could not be read.</summary>
    public const int UnknownLimitUnits = 1;
}

/// <summary>What the operator may tune. Validated against <see cref="ArbitrageLimits"/>.</summary>
public sealed record ItemArbitrageSettings
{
    /// <summary>Hard cap on the unit price; only ever lowers the computed ceiling.</summary>
    public long? MaxPrice { get; init; }
    /// <summary>Resale price to use instead of min(own shop price, Pricegun median).</summary>
    public long? ReferencePrice { get; init; }
    public double MarginPercent { get; init; } = ArbitrageLimits.DefaultMarginPercent;
    public double MallTaxPercent { get; init; } = ArbitrageLimits.DefaultMallTaxPercent;
    public long Budget { get; init; } = ArbitrageLimits.DefaultBudget;
    public long MeatReserve { get; init; } = ArbitrageLimits.DefaultMeatReserve;
    public int UnitsPerRun { get; init; } = ArbitrageLimits.DefaultUnitsPerRun;

    public void Validate()
    {
        Check(Budget, ArbitrageLimits.MinBudget, ArbitrageLimits.MaxBudget, nameof(Budget));
        Check(MeatReserve, ArbitrageLimits.MinMeatReserve, ArbitrageLimits.MaxMeatReserve, nameof(MeatReserve));
        Check(MarginPercent, ArbitrageLimits.MinMarginPercent, ArbitrageLimits.MaxMarginPercent, nameof(MarginPercent));
        Check(MallTaxPercent, ArbitrageLimits.MinMallTaxPercent, ArbitrageLimits.MaxMallTaxPercent, nameof(MallTaxPercent));
        Check(UnitsPerRun, ArbitrageLimits.MinUnitsPerRun, ArbitrageLimits.MaxUnitsPerRun, nameof(UnitsPerRun));
        if (MaxPrice is < 1) throw new ArgumentOutOfRangeException(nameof(MaxPrice), "The maximum price must be at least 1.");
        if (ReferencePrice is < 1) throw new ArgumentOutOfRangeException(nameof(ReferencePrice), "The reference price must be at least 1.");
    }

    private static void Check(double value, double min, double max, string name)
    {
        if (double.IsNaN(value) || value < min || value > max)
            throw new ArgumentOutOfRangeException(name, value,
                string.Create(CultureInfo.InvariantCulture, $"{name} must be between {min:0.##} and {max:0.##}."));
    }
}

/// <summary>Live facts the plan is computed from.</summary>
public sealed record ItemArbitrageInputs
{
    public required int ItemId { get; init; }
    public string? ItemName { get; init; }
    public required IReadOnlyList<MallListing> Offers { get; init; }
    /// <summary>The user's own shop price for the item, when it is listed.</summary>
    public long? OwnShopPrice { get; init; }
    /// <summary>Median of recent Pricegun sales (needs enough sales), when known.</summary>
    public long? PricegunMedian { get; init; }
    /// <summary>Meat on the buying account; null = unknown, then only the budget limits the plan.</summary>
    public long? MeatAvailable { get; init; }
    /// <summary>Store ids of the user's own shops; their offers are never bought.</summary>
    public IReadOnlySet<string> OwnStoreIds { get; init; } = new HashSet<string>();
    /// <summary>Shop or seller names of the user's own accounts (case-insensitive); matching offers are never bought.</summary>
    public IReadOnlySet<string> OwnStoreNames { get; init; } = new HashSet<string>();
}

public enum ArbitrageSkipReason
{
    AboveCeiling,
    OwnShop,
    OutOfStock,
    StockUnknown,
    NoStoreId,
    BudgetExhausted,
    ReserveReached,
    UnitsPerRunReached,
    NoProfit
}

public sealed record ArbitrageStep(
    string ItemId,
    string StoreId,
    string? StoreName,
    long UnitPrice,
    int Quantity,
    long Total,
    long ProfitPerUnit,
    long ExpectedProfit,
    int? DailyLimit,
    MallLimitKind LimitKind,
    string? Note);

public sealed record ArbitrageSkip(
    string StoreId,
    string? StoreName,
    long UnitPrice,
    int Stock,
    ArbitrageSkipReason Reason,
    string Message);

public sealed record ItemArbitragePlan
{
    public required int ItemId { get; init; }
    public string? ItemName { get; init; }
    public long? ReferencePrice { get; init; }
    public string ReferenceSource { get; init; } = "none";
    /// <summary>Highest unit price that is still bought; null when no reference price exists.</summary>
    public long? BuyCeiling { get; init; }
    public long? NetResalePrice { get; init; }
    public long SpendLimit { get; init; }
    public IReadOnlyList<ArbitrageStep> Steps { get; init; } = [];
    public IReadOnlyList<ArbitrageSkip> Skipped { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
    public int TotalUnits => Steps.Sum(s => s.Quantity);
    public long TotalCost => Steps.Sum(s => s.Total);
    public long TotalExpectedProfit => Steps.Sum(s => s.ExpectedProfit);
    public bool HasWork => Steps.Count > 0;
}

/// <summary>
/// Turns live mall offers into a buy plan: cheapest offers first, bought only up to a ceiling
/// below the resale price, never more than a shop's daily limit, and never beyond the budget,
/// the meat reserve or the units per run. Pure: it reads nothing and sends nothing.
/// </summary>
public static class ItemArbitragePlanner
{
    public static ItemArbitragePlan Plan(ItemArbitrageInputs inputs, ItemArbitrageSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        settings ??= new ItemArbitrageSettings();
        settings.Validate();
        var notes = new List<string>();

        var (reference, source) = Reference(inputs, settings);
        if (reference is null)
        {
            notes.Add("No resale reference price: neither the own shop price nor enough Pricegun sales were found. "
                + "Pass --reference-price or a reference to plan.");
            return new ItemArbitragePlan
            {
                ItemId = inputs.ItemId, ItemName = inputs.ItemName, Notes = notes,
                Skipped = inputs.Offers.OrderBy(o => o.Price).Select(o => Skip(o, ArbitrageSkipReason.NoProfit,
                    "No resale reference price, so no profit can be judged.")).ToArray()
            };
        }

        var net = (long)Math.Floor(reference.Value * (1 - settings.MallTaxPercent / 100d));
        var ceiling = (long)Math.Floor(net * (1 - settings.MarginPercent / 100d));
        if (settings.MaxPrice is { } cap && cap < ceiling) ceiling = cap;
        if (ceiling < 1) notes.Add("The ceiling is below 1 meat; nothing can be bought at this margin.");

        var spendLimit = settings.Budget;
        if (inputs.MeatAvailable is { } meat)
        {
            var spendable = Math.Max(0, meat - settings.MeatReserve);
            if (spendable < spendLimit) notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"Meat on the account ({meat:N0}) minus the reserve ({settings.MeatReserve:N0}) limits spending to {spendable:N0}."));
            spendLimit = Math.Min(spendLimit, spendable);
        }
        else notes.Add("The meat on the account is unknown; only the budget limits spending.");

        var steps = new List<ArbitrageStep>();
        var skipped = new List<ArbitrageSkip>();
        long spent = 0;
        var units = 0;
        foreach (var offer in inputs.Offers.OrderBy(o => o.Price).ThenBy(o => o.StoreId, StringComparer.Ordinal))
        {
            if (inputs.OwnStoreIds.Contains(offer.StoreId) || IsOwnName(inputs, offer))
            { skipped.Add(Skip(offer, ArbitrageSkipReason.OwnShop, "Own shop; never bought.")); continue; }
            if (offer.Price > ceiling)
            {
                skipped.Add(Skip(offer, ArbitrageSkipReason.AboveCeiling, string.Create(CultureInfo.InvariantCulture,
                    $"{offer.Price:N0} is above the ceiling {ceiling:N0}.")));
                continue;
            }
            if (offer.Price >= net)
            { skipped.Add(Skip(offer, ArbitrageSkipReason.NoProfit, "Not below the net resale price.")); continue; }
            if (!MallBuyClient.TryParseStoreId(offer.StoreId, out _))
            { skipped.Add(Skip(offer, ArbitrageSkipReason.NoStoreId, "The offer has no usable store id.")); continue; }
            if (!offer.QuantityKnown)
            { skipped.Add(Skip(offer, ArbitrageSkipReason.StockUnknown, "The stock could not be read.")); continue; }
            if (offer.Quantity <= 0)
            { skipped.Add(Skip(offer, ArbitrageSkipReason.OutOfStock, "No stock.")); continue; }

            var quantity = offer.Quantity;
            string? note = null;
            int? limit = null;
            switch (offer.LimitKind)
            {
                case MallLimitKind.Limited when offer.PurchaseLimit is > 0 and var l:
                    limit = l; quantity = Math.Min(quantity, l);
                    note = $"Daily limit {l} per buyer.";
                    break;
                case MallLimitKind.Unlimited:
                    break;
                default:
                    limit = ArbitrageLimits.UnknownLimitUnits;
                    quantity = Math.Min(quantity, ArbitrageLimits.UnknownLimitUnits);
                    note = "Daily limit unknown; planned conservatively.";
                    break;
            }

            var byUnits = settings.UnitsPerRun - units;
            if (byUnits <= 0)
            { skipped.Add(Skip(offer, ArbitrageSkipReason.UnitsPerRunReached, "Units per run reached.")); continue; }
            var byBudget = (spendLimit - spent) / offer.Price;
            if (byBudget <= 0)
            {
                var reserveHit = inputs.MeatAvailable is not null && spendLimit < settings.Budget;
                skipped.Add(Skip(offer, reserveHit ? ArbitrageSkipReason.ReserveReached : ArbitrageSkipReason.BudgetExhausted,
                    reserveHit ? "Buying would go below the meat reserve." : "Budget used up."));
                continue;
            }
            var take = (int)Math.Min(quantity, Math.Min(byUnits, byBudget));
            var total = checked(offer.Price * take);
            var profit = net - offer.Price;
            steps.Add(new ArbitrageStep(offer.ItemId, offer.StoreId, offer.StoreName, offer.Price, take, total,
                profit, checked(profit * take), limit, offer.LimitKind,
                take < quantity ? (note is null ? "" : note + " ") + "Reduced by budget, reserve or units per run." : note));
            spent += total;
            units += take;
        }

        return new ItemArbitragePlan
        {
            ItemId = inputs.ItemId, ItemName = inputs.ItemName, ReferencePrice = reference, ReferenceSource = source,
            NetResalePrice = net, BuyCeiling = ceiling, SpendLimit = spendLimit,
            Steps = steps, Skipped = skipped, Notes = notes
        };
    }

    private static (long? Price, string Source) Reference(ItemArbitrageInputs inputs, ItemArbitrageSettings settings)
    {
        if (settings.ReferencePrice is { } fixedPrice) return (fixedPrice, "configured");
        return (inputs.OwnShopPrice, inputs.PricegunMedian) switch
        {
            ({ } own, { } median) => own <= median ? (own, "own shop price") : (median, "Pricegun median"),
            ({ } own, null) => (own, "own shop price"),
            (null, { } median) => (median, "Pricegun median"),
            _ => (null, "none")
        };
    }

    private static bool IsOwnName(ItemArbitrageInputs inputs, MallListing o)
        => inputs.OwnStoreNames.Count > 0
           && ((o.StoreName is { } s && inputs.OwnStoreNames.Contains(s, StringComparer.OrdinalIgnoreCase))
               || (o.SellerName is { } n && inputs.OwnStoreNames.Contains(n, StringComparer.OrdinalIgnoreCase)));

    private static ArbitrageSkip Skip(MallListing o, ArbitrageSkipReason reason, string message)
        => new(o.StoreId, o.StoreName, o.Price, o.QuantityKnown ? o.Quantity : 0, reason, message);
}
