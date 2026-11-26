using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace KoLibri.Desktop;

/// <summary>Budget limits for desktop Mall purchases (configurable; defaults mirror
/// <see cref="MallPurchasePolicy"/>).</summary>
public sealed record DesktopMallPurchaseOptions
{
    public long MaxDailySpend { get; init; } = 500_000;
    public long MaxMeatPerPurchase { get; init; } = 250_000;
    public int MaxQuantityPerPurchase { get; init; } = 10;
    public TimeSpan PreviewLifetime { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>Inventory re-reads (and the wait before each) when KoL confirmed but the item is not visible yet.</summary>
    public int ReconcileReads { get; init; } = 2;
    public TimeSpan ReconcileDelay { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>What the confirmation dialog shows. Nothing was sent to KoL to build it.</summary>
public sealed record DesktopMallPurchasePreview
{
    public required string ActionId { get; init; }
    public required string Role { get; init; }
    public required string ItemId { get; init; }
    public required string ShopId { get; init; }
    public required int Quantity { get; init; }
    /// <summary>The price the user asked for (and the price the purchase is bound to).</summary>
    public required long RequestedUnitPrice { get; init; }
    /// <summary>The shop's current price from the fresh read.</summary>
    public required long UnitPrice { get; init; }
    public required long TotalPrice { get; init; }
    public required long Meat { get; init; }
    public required long RemainingDailyBudget { get; init; }
    public required long RemainingPurchaseBudget { get; init; }
    public bool PriceChanged => RequestedUnitPrice != UnitPrice;
    public required IReadOnlyList<MallPurchaseAbortReason> Problems { get; init; }
    public required IReadOnlyList<string> Messages { get; init; }
    public bool CanConfirm => Problems.Count == 0;
    internal MallPurchasePreview Inner { get; init; } = new();
}

public enum DesktopMallPurchaseStatus
{
    /// <summary>KoL answered and a fresh inventory read shows exactly the ordered quantity (Meat plausible).</summary>
    Purchased,
    /// <summary>Fewer units than ordered arrived and the inventory proves it; <see cref="DesktopMallPurchaseResult.ReceivedQuantity"/> units count as spent.</summary>
    Partial,
    /// <summary>Refused before any POST (budget, stock, meat, stale preview, ...).</summary>
    NotSent,
    /// <summary>The price moved; nothing was sent. <see cref="DesktopMallPurchaseResult.NewPreview"/> needs a new confirmation.</summary>
    PriceChanged,
    /// <summary>KoL answered with a clear refusal.</summary>
    Rejected,
    /// <summary>A POST may have been applied but meat/inventory do not prove it. Never retry: check inventory.</summary>
    Unclear,
    /// <summary>Deferred verification: the POST was sent (see <see cref="DesktopMallPurchaseResult.Confirmed"/>); a later inventory read settles it.</summary>
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
    public DesktopMallPurchasePreview? NewPreview { get; init; }
}

/// <summary>
/// UI-facing Mall purchase: preview, then one confirmed purchase. Wraps
/// <see cref="MallPurchaseGateway"/>, which re-reads the offer, daily limit and meat
/// before sending, binds the price in the POST and verifies meat and inventory
/// afterwards. At most one POST per preview, and never a retry.
/// </summary>
public sealed class DesktopMallPurchaseService
{
    private const string ConfirmToken = "desktop-user-confirmed";

    private readonly MallPurchaseGateway _gateway;
    private readonly DesktopMallPurchaseOptions _options;
    private readonly ConcurrentDictionary<string, byte> _consumed = new();

    public DesktopMallPurchaseService(
        IMallPurchaseTransport transport,
        DesktopMallPurchaseOptions? options = null,
        IAccountExecutionLane? lane = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _options = options ?? new DesktopMallPurchaseOptions();
        _gateway = new MallPurchaseGateway(
            transport,
            new MallPurchasePolicy
            {
                // The explicit ConfirmAsync call is the approval; the gateway's own
                // approval token is not a second prompt.
                DryRun = false,
                RequireApproval = false,
                PreviewLifetime = _options.PreviewLifetime,
                MaxDailySpend = _options.MaxDailySpend,
                MaxMeatPerPurchase = _options.MaxMeatPerPurchase,
                MaxQuantityPerPurchase = _options.MaxQuantityPerPurchase,
                ReconcileReads = _options.ReconcileReads,
                ReconcileDelay = _options.ReconcileDelay
            },
            lane,
            clock);
    }

    public DesktopMallPurchaseOptions Options => _options;

    /// <param name="role">Account role / id the purchase is made with.</param>
    /// <param name="shopId">KoL store id (equal to the seller's player id).</param>
    public async Task<DesktopMallPurchasePreview> PreviewAsync(
        string role, string itemId, string shopId, long unitPrice, int quantity,
        CancellationToken cancellationToken = default)
    {
        var request = new MallPurchaseRequest(shopId, shopId, itemId, unitPrice, quantity,
            accountId: string.IsNullOrWhiteSpace(role) ? "default" : role);
        var inner = await _gateway.PreviewAsync(request, cancellationToken).ConfigureAwait(false);
        return ToPreview(inner, request);
    }

    public async Task<DesktopMallPurchaseResult> ConfirmAsync(
        DesktopMallPurchasePreview preview, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (!preview.CanConfirm)
            return NotSent(preview.Problems, (MallPurchaseMessages.Describe(preview.Problems) + " Nothing was sent.").Trim());
        // A preview is spent by its first confirmation, whatever the outcome.
        if (!_consumed.TryAdd(preview.ActionId, 0))
            return NotSent([MallPurchaseAbortReason.CommitRejected],
                "This purchase was already attempted. Check inventory and preview again; nothing was sent.");

        var result = await _gateway.CommitAsync(preview.Inner, ConfirmToken, cancellationToken).ConfigureAwait(false);
        return await MapAsync(result, preview.Quantity, preview.UnitPrice, preview, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The one-step purchase: no separate preview. The offer is read once, the purchase is refused only when the
    /// price rose above <paramref name="shownUnitPrice"/> (or offer, stock, budget or Meat no longer allow it), and
    /// otherwise sent once at the price just read. At most one POST, never a retry.
    /// </summary>
    public async Task<DesktopMallPurchaseResult> BuyAsync(
        string role, string itemId, string shopId, long shownUnitPrice, int quantity,
        CancellationToken cancellationToken = default, bool deferVerification = false)
    {
        var request = new MallPurchaseRequest(shopId, shopId, itemId, shownUnitPrice, quantity,
            accountId: string.IsNullOrWhiteSpace(role) ? "default" : role);
        var result = await _gateway.PurchaseNowAsync(request, cancellationToken, deferVerification).ConfigureAwait(false);
        return await MapAsync(result, quantity, shownUnitPrice, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DesktopMallPurchaseResult> MapAsync(
        MallPurchaseCommitResult result, int quantity, long shownUnitPrice,
        DesktopMallPurchasePreview? preview, CancellationToken cancellationToken)
    {
        var unit = result.Before?.Offer.UnitPrice is > 0 and var u && u <= shownUnitPrice ? u : shownUnitPrice;
        if (result.Status == MallPurchaseVerificationStatus.Pending)
            return new DesktopMallPurchaseResult
            {
                Status = DesktopMallPurchaseStatus.Sent,
                Confirmed = result.Accepted,
                UnitPrice = unit,
                Message = result.Messages.FirstOrDefault() ?? (result.Accepted ? "KoL acknowledged the purchase." : "KoL's answer was unclear.")
            };
        if (result.Verified)
            return new DesktopMallPurchaseResult
            {
                Status = DesktopMallPurchaseStatus.Purchased,
                ReceivedQuantity = quantity,
                SpentMeat = unit * quantity,
                Message = string.Create(CultureInfo.InvariantCulture,
                    $"Bought {quantity} for {unit * quantity:N0} Meat; the inventory shows the item.")
            };

        var reasons = result.AbortReasons;
        if (reasons.Contains(MallPurchaseAbortReason.PartialQuantity) && result.ReceivedQuantity > 0)
            return new DesktopMallPurchaseResult
            {
                Status = DesktopMallPurchaseStatus.Partial,
                Reasons = reasons,
                ReceivedQuantity = result.ReceivedQuantity,
                SpentMeat = unit * result.ReceivedQuantity,
                Message = string.Create(CultureInfo.InvariantCulture,
                    $"Partial purchase: {result.ReceivedQuantity} of {quantity} unit(s) arrived "
                    + $"({unit * result.ReceivedQuantity:N0} Meat counted).")
            };
        if (result.Receipt is { } receipt)
        {
            var message = receipt.Message ?? string.Empty;
            // Accepted but unproven, or an indeterminate POST that the fresh read did not
            // prove: the POST may have landed. Report, never retry the purchase.
            if (receipt.MaybeSent)
                return new DesktopMallPurchaseResult
                {
                    Status = DesktopMallPurchaseStatus.Unclear,
                    Reasons = reasons,
                    Message = DescribeUnclear(receipt.Accepted, reasons, result, message)
                };
            return new DesktopMallPurchaseResult
            {
                Status = DesktopMallPurchaseStatus.Rejected,
                Reasons = reasons,
                Message = string.IsNullOrWhiteSpace(message) ? "KoL refused the purchase." : message
            };
        }

        // No receipt: the gateway stopped before the POST... except a transport failure after
        // the commit-time read, which we treat as unclear.
        if (reasons.Contains(MallPurchaseAbortReason.TransportFailure) && result.Before is not null)
            return new DesktopMallPurchaseResult
            {
                Status = DesktopMallPurchaseStatus.Unclear,
                Reasons = reasons,
                Message = "The purchase call failed; it may or may not have gone through. "
                    + "Check your inventory and meat before buying again."
            };

        if (reasons.Contains(MallPurchaseAbortReason.PriceChanged))
        {
            DesktopMallPurchasePreview? fresh = null;
            if (preview is not null && result.Before is { } current)
                fresh = await PreviewAsync(preview.Role, preview.ItemId, preview.ShopId,
                    current.Offer.UnitPrice, quantity, cancellationToken).ConfigureAwait(false);
            var now = result.Before?.Offer.UnitPrice;
            return new DesktopMallPurchaseResult
            {
                Status = DesktopMallPurchaseStatus.PriceChanged,
                Reasons = reasons,
                NewPreview = fresh,
                Message = preview is null && now is { } nowPrice
                    ? string.Create(CultureInfo.InvariantCulture,
                        $"The price rose from {shownUnitPrice:N0} to {nowPrice:N0} Meat. Nothing was sent; search again.")
                    : "The shop's price changed. Nothing was sent; please confirm the new price."
            };
        }

        return NotSent(reasons, result.Messages.FirstOrDefault()
            ?? (MallPurchaseMessages.Describe(reasons) is { Length: > 0 } text
                ? text + " Nothing was sent."
                : "The offer, limits or meat changed since the preview; nothing was sent."));
    }

    private static string DescribeUnclear(
        bool confirmed, IReadOnlyList<MallPurchaseAbortReason> reasons, MallPurchaseCommitResult result, string transportMessage)
    {
        const string Tail = "Check your inventory before buying again; use 'Reconcile unclear purchases' once you know.";
        var reads = result.RereadCount > 0
            ? string.Create(CultureInfo.InvariantCulture, $" after {result.RereadCount} more inventory read(s)") : "";
        var detail = string.Join(" ", result.Messages.Append(transportMessage).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct());
        if (detail.Length > 0) detail = " Details: " + detail;
        if (reasons.Contains(MallPurchaseAbortReason.MeatVerificationFailed))
            return "The item arrived, but your Meat fell by more than the purchase explains. " + Tail + detail;
        if (reasons.Contains(MallPurchaseAbortReason.UnexpectedQuantity))
            return "The inventory rose by more than the ordered quantity, so the purchase cannot be matched. " + Tail + detail;
        if (reasons.Contains(MallPurchaseAbortReason.StaleAccount) || reasons.Contains(MallPurchaseAbortReason.SessionInvalid))
            return (confirmed ? "KoL confirmed the purchase" : "KoL's answer was unclear")
                + ", but the inventory could not be read fresh" + reads + ". " + Tail + detail;
        if (reasons.Contains(MallPurchaseAbortReason.ItemNotReceived))
            return (confirmed ? "KoL confirmed the purchase" : "KoL's answer was unclear")
                + ", but the inventory still does not show the item" + reads + ". " + Tail + detail;
        return "KoL's answer could not be matched to an inventory change. " + Tail + detail;
    }

    private DesktopMallPurchasePreview ToPreview(MallPurchasePreview inner, MallPurchaseRequest request)
    {
        var account = inner.Observation.Account;
        var offer = inner.Observation.Offer;
        var total = checked(offer.UnitPrice * request.Quantity);
        var dailyCap = Math.Min(_options.MaxDailySpend, account.DailyLimit);
        return new DesktopMallPurchasePreview
        {
            ActionId = inner.ActionId,
            Role = request.AccountId,
            ItemId = request.ItemId,
            ShopId = request.StoreId,
            Quantity = request.Quantity,
            RequestedUnitPrice = request.ExpectedUnitPrice,
            UnitPrice = offer.UnitPrice,
            TotalPrice = total,
            Meat = account.Meat,
            RemainingDailyBudget = Math.Max(0, dailyCap - account.DailySpent),
            RemainingPurchaseBudget = Math.Max(0, _options.MaxMeatPerPurchase - total),
            Problems = inner.AbortReasons,
            Messages = inner.Warnings,
            Inner = inner
        };
    }

    private static DesktopMallPurchaseResult NotSent(IReadOnlyList<MallPurchaseAbortReason> reasons, string message)
        => new() { Status = DesktopMallPurchaseStatus.NotSent, Reasons = reasons, Message = message };
}
