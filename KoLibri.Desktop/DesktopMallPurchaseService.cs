using System.Globalization;

namespace KoLibri.Desktop;

/// <summary>Budget limits for desktop Mall purchases (configurable in Settings).</summary>
public sealed record DesktopMallPurchaseOptions
{
    public long MaxDailySpend { get; init; } = 500_000;
    public long MaxMeatPerPurchase { get; init; } = 250_000;
    public int MaxQuantityPerPurchase { get; init; } = 10;
}

public enum DesktopMallPurchaseStatus
{
    /// <summary>KoL answered and a fresh inventory read shows exactly the ordered quantity.</summary>
    Purchased,
    /// <summary>Fewer units than ordered arrived and the inventory proves it; <see cref="DesktopMallPurchaseResult.ReceivedQuantity"/> units count as spent.</summary>
    Partial,
    /// <summary>Refused before any POST (limits, offer gone, meat, guard, ...).</summary>
    NotSent,
    /// <summary>The price rose above the price the user saw; nothing was sent.</summary>
    PriceChanged,
    /// <summary>KoL answered with a clear refusal.</summary>
    Rejected,
    /// <summary>A POST may have been applied but meat/inventory do not prove it. Never retry: check inventory.</summary>
    Unclear,
    /// <summary>Deferred verification: the POST was sent (see <see cref="DesktopMallPurchaseResult.Confirmed"/>); a later inventory read settles it.
    /// The library engine always verifies itself; only test and QA engines report this.</summary>
    Sent
}

public sealed record DesktopMallPurchaseResult
{
    public required DesktopMallPurchaseStatus Status { get; init; }
    public bool Succeeded => Status is DesktopMallPurchaseStatus.Purchased or DesktopMallPurchaseStatus.Partial;
    /// <summary>Units the inventory read proves arrived.</summary>
    public int ReceivedQuantity { get; init; }
    /// <summary>For <see cref="DesktopMallPurchaseStatus.Sent"/>: KoL's answer was recognised as a purchase receipt.</summary>
    public bool Confirmed { get; init; }
    /// <summary>The unit price the purchase was bound to (the offer read at send time).</summary>
    public long UnitPrice { get; init; }
    /// <summary>Meat the purchase cost at the price actually bound (0 when nothing was bought or the outcome is unclear).</summary>
    public long SpentMeat { get; init; }
    public required string Message { get; init; }
    public IReadOnlyList<MallPurchaseAbortReason> Reasons { get; init; } = Array.Empty<MallPurchaseAbortReason>();
}

/// <summary>
/// UI-facing Mall purchase on the KoLibri library: one buy from one shop, never above the price the user saw.
/// <see cref="Mall.BuyAsync"/> reads the offer fresh, refuses a price above <see cref="PurchaseOrder.MaxUnitPrice"/>,
/// sends at most one POST per shop (bound to the price just read: <c>whichitem=item.price</c>) and checks inventory
/// and Meat afterwards. This class adds the desktop's per-purchase limits and maps the result onto the texts and
/// states the Mall tab shows. Never a retry.
/// </summary>
public sealed class DesktopMallPurchaseService
{
    private readonly Func<PurchaseQuote, CancellationToken, Task<PurchaseResult>> _buy;
    private readonly IMallPurchaseReads _reads;
    private readonly DesktopMallPurchaseOptions _options;

    /// <param name="buy">The library purchase (<see cref="Mall.BuyAsync"/> of the role's session); a seam for tests.</param>
    /// <param name="reads">Fresh status and inventory of the same role.</param>
    public DesktopMallPurchaseService(
        Func<PurchaseQuote, CancellationToken, Task<PurchaseResult>> buy,
        IMallPurchaseReads reads,
        DesktopMallPurchaseOptions? options = null)
    {
        _buy = buy ?? throw new ArgumentNullException(nameof(buy));
        _reads = reads ?? throw new ArgumentNullException(nameof(reads));
        _options = options ?? new DesktopMallPurchaseOptions();
    }

    /// <summary>The live service of one role: the session is looked up per purchase, so a re-login is picked up.</summary>
    public static DesktopMallPurchaseService ForRole(
        DesktopLiveDataProvider live, AccountRole role, DesktopMallPurchaseOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(live);
        return new DesktopMallPurchaseService(
            async (quote, ct) =>
            {
                var session = await live.RequireSessionAsync(role, "buy in the mall", ct).ConfigureAwait(false);
                return await session.Mall.BuyAsync(quote, ct).ConfigureAwait(false);
            },
            new LiveMallPurchaseReads(live, role),
            options);
    }

    public DesktopMallPurchaseOptions Options => _options;

    /// <summary>Fresh Meat and inventory counts (item id → quantity). Fresh is false when signed out.</summary>
    public async Task<MallAccountReading> ReadAccountAsync(CancellationToken cancellationToken)
    {
        var status = await _reads.StatusAsync(cancellationToken).ConfigureAwait(false);
        var inventory = await _reads.InventoryAsync(cancellationToken).ConfigureAwait(false);
        var items = inventory.Items.GroupBy(i => i.ItemId)
            .ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity), StringComparer.Ordinal);
        return new MallAccountReading(status?.Meat ?? 0, items, status is not null && inventory.HasSession && !inventory.IsStale);
    }

    /// <summary>
    /// The one-step purchase: refused before sending when it breaks a per-purchase limit; otherwise the library reads
    /// the offer fresh and buys only when the price is at most <paramref name="shownUnitPrice"/>. At most one POST.
    /// </summary>
    /// <param name="role">Account role the purchase is made with (for the texts only; the service is bound to its role).</param>
    /// <param name="shopId">KoL store id (equal to the seller's player id).</param>
    public async Task<DesktopMallPurchaseResult> BuyAsync(
        string role, string itemId, string shopId, long shownUnitPrice, int quantity,
        CancellationToken cancellationToken = default, string? shopName = null)
    {
        if (quantity <= 0 || shownUnitPrice <= 0
            || !int.TryParse(itemId, NumberStyles.None, CultureInfo.InvariantCulture, out var item) || item <= 0
            || !int.TryParse(shopId, NumberStyles.None, CultureInfo.InvariantCulture, out var store) || store <= 0)
            return NotSent(MallPurchaseAbortReason.InvalidRequest);
        if (quantity > _options.MaxQuantityPerPurchase)
            return NotSent(MallPurchaseAbortReason.QuantityLimitExceeded);
        var total = checked(shownUnitPrice * quantity);
        if (total > _options.MaxMeatPerPurchase)
            return NotSent(MallPurchaseAbortReason.PerPurchaseLimitExceeded);

        var order = new PurchaseOrder(item, quantity, shownUnitPrice);
        var quote = new PurchaseQuote(order,
            new[] { new MallListing(store, string.IsNullOrWhiteSpace(shopName) ? shopId : shopName, item, shownUnitPrice, quantity, null) },
            total, DateTimeOffset.UtcNow)
        {
            PlannedQuantity = quantity
        };

        PurchaseResult result;
        try
        {
            result = await _buy(quote, cancellationToken).ConfigureAwait(false);
        }
        catch (ActionDeniedException denied)
        {
            // The guard decides before any request goes out.
            return new DesktopMallPurchaseResult
            {
                Status = DesktopMallPurchaseStatus.NotSent,
                Reasons = new[] { MallPurchaseAbortReason.CommitRejected },
                Message = $"The purchase check refused it: {denied.Reason} Nothing was sent."
            };
        }
        return Map(result, quantity, shownUnitPrice);
    }

    internal static DesktopMallPurchaseResult Map(PurchaseResult result, int quantity, long shownUnitPrice)
    {
        var c = CultureInfo.InvariantCulture;
        var detail = string.Join(" ", result.Lines.Select(l => l.Message).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct());
        var unit = result.QuantityBought > 0 ? result.MeatSpent / result.QuantityBought
            : result.Lines.FirstOrDefault()?.UnitPrice is > 0 and var u ? u : shownUnitPrice;
        switch (result.Status)
        {
            case PurchaseStatus.Bought:
                return new DesktopMallPurchaseResult
                {
                    Status = DesktopMallPurchaseStatus.Purchased,
                    ReceivedQuantity = result.QuantityBought,
                    UnitPrice = unit,
                    SpentMeat = result.MeatSpent,
                    Message = string.Create(c, $"Bought {result.QuantityBought} for {result.MeatSpent:N0} Meat; the inventory shows the item.")
                };
            case PurchaseStatus.Partial:
                return new DesktopMallPurchaseResult
                {
                    Status = DesktopMallPurchaseStatus.Partial,
                    Reasons = new[] { MallPurchaseAbortReason.PartialQuantity },
                    ReceivedQuantity = result.QuantityBought,
                    UnitPrice = unit,
                    SpentMeat = result.MeatSpent,
                    Message = string.Create(c,
                        $"Partial purchase: {result.QuantityBought} of {quantity} unit(s) arrived ({result.MeatSpent:N0} Meat counted).")
                        + (detail.Length > 0 ? " " + detail : "")
                };
            case PurchaseStatus.Unclear:
                return new DesktopMallPurchaseResult
                {
                    Status = DesktopMallPurchaseStatus.Unclear,
                    Reasons = new[] { MallPurchaseAbortReason.ItemNotReceived },
                    UnitPrice = unit,
                    Message = "KoL's answer could not be matched to an inventory change. "
                        + "Check your inventory before buying again; use 'Reconcile unclear purchases' once you know."
                        + (detail.Length > 0 ? " Details: " + detail : "")
                };
            case PurchaseStatus.PriceChanged:
                var now = result.Lines.Select(l => l.UnitPrice).Where(p => p > shownUnitPrice).DefaultIfEmpty(0).Max();
                return new DesktopMallPurchaseResult
                {
                    Status = DesktopMallPurchaseStatus.PriceChanged,
                    Reasons = new[] { MallPurchaseAbortReason.PriceChanged },
                    Message = now > 0
                        ? string.Create(c, $"The price rose from {shownUnitPrice:N0} to {now:N0} Meat. Nothing was sent; search again.")
                        : "The shop's price changed. Nothing was sent; search again."
                };
            case PurchaseStatus.Refused:
                return new DesktopMallPurchaseResult
                {
                    Status = DesktopMallPurchaseStatus.NotSent,
                    Reasons = new[] { MallPurchaseAbortReason.InsufficientMeat },
                    Message = (detail.Length > 0 ? detail : MallPurchaseMessages.Describe(MallPurchaseAbortReason.InsufficientMeat)) + " Nothing was sent."
                };
            default:   // NothingBought
                // The library sends nothing when the listing is gone; a KoL refusal carries KoL's own text.
                var gone = result.Lines.Count == 0
                    || result.Lines.All(l => l.Message?.Contains("listing is gone", StringComparison.OrdinalIgnoreCase) == true);
                return gone
                    ? new DesktopMallPurchaseResult
                    {
                        Status = DesktopMallPurchaseStatus.NotSent,
                        Reasons = new[] { MallPurchaseAbortReason.MissingOffer },
                        Message = MallPurchaseMessages.Describe(MallPurchaseAbortReason.MissingOffer) + " Nothing was sent."
                    }
                    : new DesktopMallPurchaseResult
                    {
                        Status = DesktopMallPurchaseStatus.Rejected,
                        Reasons = new[] { MallPurchaseAbortReason.CommitRejected },
                        Message = detail.Length > 0 ? detail : "KoL refused the purchase."
                    };
        }
    }

    private static DesktopMallPurchaseResult NotSent(MallPurchaseAbortReason reason)
        => new()
        {
            Status = DesktopMallPurchaseStatus.NotSent,
            Reasons = new[] { reason },
            Message = MallPurchaseMessages.Describe(reason) + " Nothing was sent."
        };
}
