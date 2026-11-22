using System.Globalization;

namespace KoLibri.Core.Markt;

public enum MallPurchaseSettlementKind
{
    /// <summary>The item count rose by exactly the ordered quantity and Meat did not drop by more than the price.</summary>
    Settled,
    /// <summary>The item count did not rise.</summary>
    NotReceived,
    /// <summary>Fewer units than ordered arrived.</summary>
    Partial,
    /// <summary>More units than ordered arrived (something else added items).</summary>
    Excess,
    /// <summary>The item arrived, but Meat fell by more than the purchase can explain.</summary>
    MeatDropped
}

/// <summary>What a fresh read proves about one purchase.</summary>
public sealed record MallPurchaseSettlementResult(MallPurchaseSettlementKind Kind, int Received, string Message)
{
    public bool Settled => Kind == MallPurchaseSettlementKind.Settled;
}

/// <summary>
/// The one rule for "did this purchase happen". The inventory is the proof: the count of the item must have risen
/// by exactly the ordered quantity. Meat is only a plausibility check in one direction: it may not have fallen by
/// more than the price, but more Meat than expected is fine (a shop that sells while we buy raises it).
/// </summary>
public static class MallPurchaseSettlement
{
    public static MallPurchaseSettlementResult Evaluate(
        MallPurchaseRequest request, MallPurchaseObservation before, MallPurchaseObservation after)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var delta = after.InventoryQuantity(request.ItemId) - before.InventoryQuantity(request.ItemId);
        var ordered = request.Quantity;
        if (delta <= 0)
            return new(MallPurchaseSettlementKind.NotReceived, 0,
                "The fresh inventory read does not show the item yet.");
        if (delta < ordered)
            return new(MallPurchaseSettlementKind.Partial, delta, string.Create(CultureInfo.InvariantCulture,
                $"Only {delta} of {ordered} unit(s) arrived."));
        if (delta > ordered)
            return new(MallPurchaseSettlementKind.Excess, delta, string.Create(CultureInfo.InvariantCulture,
                $"The inventory rose by {delta} but {ordered} unit(s) were ordered."));
        var floor = before.Account.Meat - request.TotalCost;
        if (after.Account.Meat < floor)
            return new(MallPurchaseSettlementKind.MeatDropped, delta, string.Create(CultureInfo.InvariantCulture,
                $"The item arrived, but Meat fell to {after.Account.Meat:N0}, below the {floor:N0} the purchase explains."));
        return new(MallPurchaseSettlementKind.Settled, delta, "Inventory shows the purchase.");
    }
}
