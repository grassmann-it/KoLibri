using System.Globalization;

namespace KoLibri.Extras;

/// <summary>Median rule of 0.4 (ArbitrageRule): too few sales are too little evidence.</summary>
public static class PriceRule
{
    public const int MinimumSales = 5;

    public static long? Median(IReadOnlyCollection<long> unitPrices)
    {
        ArgumentNullException.ThrowIfNull(unitPrices);
        if (unitPrices.Count < MinimumSales) return null;
        var sorted = unitPrices.OrderBy(p => p).ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }
}

/// <summary>Reads the live mall, the own shop and Pricegun for one item and plans the buy. Read-only.</summary>
public static class ItemArbitrageService
{
    public static async Task<ItemArbitragePlan> PlanAsync(
        KolibriSession session, int itemId, ItemArbitrageSettings? settings = null, long? meatAvailable = null,
        IReadOnlySet<string>? ownStoreNames = null, bool refresh = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        settings ??= new ItemArbitrageSettings();
        settings.Validate();
        var notes = new List<string>();
        var offers = await session.Mall.GetOffersAsync(itemId, refresh, cancellationToken).ConfigureAwait(false);

        long? own = null;
        try
        {
            var shop = await session.Shop.GetListingsAsync(refresh, cancellationToken).ConfigureAwait(false);
            own = shop.FirstOrDefault(x => x.Item.Id == itemId)?.Price;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            notes.Add("The own shop could not be read: " + e.Message);
        }

        long? median = null;
        var history = await session.Mall.GetPriceHistoryAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (history is null) notes.Add("Pricegun could not be read.");
        else
        {
            median = PriceRule.Median(history.Points.Select(p => p.Price).ToArray());
            if (median is null) notes.Add("Pricegun has too few sales for a median.");
        }

        var plan = ItemArbitragePlanner.Plan(new ItemArbitrageInputs
        {
            ItemId = itemId,
            ItemName = offers.Name,
            Offers = offers.Listings.Select(MallOffer.From).ToArray(),
            OwnShopPrice = own,
            PricegunMedian = median,
            MeatAvailable = meatAvailable,
            OwnStoreNames = ownStoreNames ?? new HashSet<string>()
        }, settings);
        return plan with { Notes = notes.Concat(plan.Notes).ToArray() };
    }
}

public enum ArbitrageStepStatus
{
    /// <summary>Preview only: the purchase would be accepted; nothing was sent.</summary>
    Previewed,
    /// <summary>KoL answered and the inventory moved as expected.</summary>
    Purchased,
    /// <summary>Refused before any POST (price moved, stock, meat, budget, ...).</summary>
    NotSent,
    /// <summary>KoL answered with a clear refusal.</summary>
    Rejected,
    /// <summary>A POST may have been applied but the inventory does not prove it. Never retry; check the account.</summary>
    Unclear,
    /// <summary>An earlier step ended unclear, so this one was not tried.</summary>
    Skipped
}

public sealed record ArbitrageStepResult(ArbitrageStep Step, ArbitrageStepStatus Status, int Quantity, long Spent, string Message, IReadOnlyList<string> Reasons);

public sealed record ArbitrageRunResult(bool Executed, IReadOnlyList<ArbitrageStepResult> Steps)
{
    public int UnitsBought => Steps.Where(s => s.Status == ArbitrageStepStatus.Purchased).Sum(s => s.Quantity);
    public long MeatSpent => Steps.Where(s => s.Status == ArbitrageStepStatus.Purchased).Sum(s => s.Spent);
    public bool Unclear => Steps.Any(s => s.Status == ArbitrageStepStatus.Unclear);
}

/// <summary>
/// Runs the steps of an <see cref="ItemArbitragePlan"/> through <see cref="Mall.BuyAsync"/>: each step is checked
/// against a fresh offer (same store, same price), its Meat is reserved in the persisted <see cref="ArbitrageSpendLedger"/>
/// before the purchase, and it is settled only when KoL and the inventory confirm it. An unclear step ends the run.
/// </summary>
public sealed class ItemArbitrageExecutor
{
    private readonly KolibriSession _session;
    private readonly ArbitrageSpendLedger _ledger;
    private readonly string _account;
    private readonly ItemArbitrageSettings _settings;

    public ItemArbitrageExecutor(KolibriSession session, ArbitrageSpendLedger ledger, string account, ItemArbitrageSettings settings)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _account = string.IsNullOrWhiteSpace(account) ? "default" : account;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// Checks every step against fresh reads. Sends nothing. The steps of one run are counted together: a later
    /// step is checked against the budget as if the earlier ready steps were bought.
    /// </summary>
    public async Task<ArbitrageRunResult> PreviewAsync(ItemArbitragePlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var results = new List<ArbitrageStepResult>();
        long plannedInRun = 0;
        foreach (var step in plan.Steps)
        {
            var problem = await CheckAsync(step, plannedInRun, cancellationToken).ConfigureAwait(false);
            if (problem is null) plannedInRun += step.Total;
            results.Add(problem is null
                ? new(step, ArbitrageStepStatus.Previewed, step.Quantity, 0, "Ready; nothing sent.", Array.Empty<string>())
                : new(step, ArbitrageStepStatus.NotSent, 0, 0, problem.Value.Message, new[] { problem.Value.Reason }));
        }
        return new ArbitrageRunResult(false, results);
    }

    /// <summary>Quantity, unit price and total of a step must be positive and add up.</summary>
    private static bool IsConsistent(ArbitrageStep step)
    {
        if (step.Quantity <= 0 || step.UnitPrice < 0) return false;
        try { return checked(step.UnitPrice * step.Quantity) == step.Total; }
        catch (OverflowException) { return false; }
    }

    /// <summary>Buys the planned steps, one verified purchase per step.</summary>
    public async Task<ArbitrageRunResult> ExecuteAsync(ItemArbitragePlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var results = new List<ArbitrageStepResult>();
        var stopped = false;
        foreach (var step in plan.Steps)
        {
            if (stopped)
            {
                results.Add(new(step, ArbitrageStepStatus.Skipped, 0, 0, "Not tried: an earlier step is unclear.", Array.Empty<string>()));
                continue;
            }
            var result = await ExecuteStepAsync(step, cancellationToken).ConfigureAwait(false);
            results.Add(result);
            if (result.Status == ArbitrageStepStatus.Unclear) stopped = true;
        }
        return new ArbitrageRunResult(true, results);
    }

    private async Task<(string Reason, string Message)?> CheckAsync(ArbitrageStep step, long plannedInRun, CancellationToken ct)
    {
        if (!IsConsistent(step))
            return ("InvalidRequest", "The step is not consistent (quantity, unit price and total do not match); nothing was sent.");
        var itemId = int.Parse(step.ItemId, CultureInfo.InvariantCulture);
        var storeId = int.Parse(step.StoreId, CultureInfo.InvariantCulture);
        var offers = await _session.Mall.GetOffersAsync(itemId, refresh: true, ct).ConfigureAwait(false);
        var listing = offers.Listings.FirstOrDefault(l => l.StoreId == storeId);
        if (listing is null) return ("OfferMissing", "The offer is gone.");
        if (listing.UnitPrice != step.UnitPrice)
            return ("PriceChanged", string.Create(CultureInfo.InvariantCulture, $"The price moved from {step.UnitPrice:N0} to {listing.UnitPrice:N0}; nothing was sent."));
        if (listing.QuantityKnown && listing.Quantity < step.Quantity) return ("InsufficientStock", "Not enough stock left.");
        var status = await _session.Character.GetStatusAsync(refresh: true, ct).ConfigureAwait(false);
        if (status.Meat - step.Total < _settings.MeatReserve)
            return ("MeatReserve", string.Create(CultureInfo.InvariantCulture, $"Buying would leave less than the reserve of {_settings.MeatReserve:N0} meat."));
        bool fits;   // own open reservations and the earlier steps of this preview count too
        try { fits = checked(_ledger.CountedToday(_account) + plannedInRun + step.Total) <= _settings.Budget; }
        catch (OverflowException) { fits = false; }
        if (!fits)
            return ("DailyLimitExceeded", string.Create(CultureInfo.InvariantCulture, $"The daily budget of {_settings.Budget:N0} would be exceeded."));
        return null;
    }

    private async Task<ArbitrageStepResult> ExecuteStepAsync(ArbitrageStep step, CancellationToken ct)
    {
        var problem = await CheckAsync(step, 0, ct).ConfigureAwait(false);
        if (problem is { } p) return new(step, ArbitrageStepStatus.NotSent, 0, 0, p.Message, new[] { p.Reason });

        // Reserved (and written to disk) before the purchase; settled only once KoL and the inventory prove it.
        if (!_ledger.TryReserve(_account, step.Total, _settings.Budget, out var reservation, out var ledgerError))
            return new(step, ArbitrageStepStatus.NotSent, 0, 0, ledgerError ?? "The spend ledger refused the reservation.", new[] { "DailyLimitExceeded" });

        var itemId = int.Parse(step.ItemId, CultureInfo.InvariantCulture);
        var storeId = int.Parse(step.StoreId, CultureInfo.InvariantCulture);
        var order = new PurchaseOrder(itemId, step.Quantity, step.UnitPrice) { MeatReserve = _settings.MeatReserve };
        var quote = new PurchaseQuote(order,
            new[] { new MallListing(storeId, step.StoreName ?? step.StoreId, itemId, step.UnitPrice, step.Quantity, step.DailyLimit) },
            step.Total, DateTimeOffset.UtcNow) { PlannedQuantity = step.Quantity };
        PurchaseResult result;
        try
        {
            result = await _session.Mall.BuyAsync(quote, ct).ConfigureAwait(false);
        }
        catch
        {
            _ledger.MarkUnclear(reservation);   // unknown whether the POST went out
            throw;
        }

        var message = string.Join(" ", result.Lines.Select(l => l.Message).Where(m => !string.IsNullOrWhiteSpace(m)));
        switch (result.Status)
        {
            case PurchaseStatus.Bought:
            case PurchaseStatus.Partial:
                _ledger.SettlePartial(reservation, result.MeatSpent);
                return new(step, ArbitrageStepStatus.Purchased, result.QuantityBought, result.MeatSpent,
                    string.Create(CultureInfo.InvariantCulture,
                        $"Bought {result.QuantityBought} for {result.MeatSpent:N0} meat; KoL and the inventory confirmed it."), Array.Empty<string>());
            case PurchaseStatus.Unclear:
                _ledger.MarkUnclear(reservation);
                return new(step, ArbitrageStepStatus.Unclear, 0, 0,
                    "KoL's answer could not be matched to an inventory change. Check the inventory and meat before buying again. " + message,
                    new[] { "Unclear" });
            default:
                _ledger.Release(reservation);
                return new(step, result.Status == PurchaseStatus.NothingBought ? ArbitrageStepStatus.Rejected : ArbitrageStepStatus.NotSent, 0, 0,
                    string.IsNullOrWhiteSpace(message) ? "KoL refused the purchase." : message, new[] { result.Status.ToString() });
        }
    }
}
