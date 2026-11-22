using KoLibri.Core.Markt;

namespace KoLibri.Desktop;

/// <summary>What a BACON top-up in the daily routine may spend, derived from the mall limits in the settings.</summary>
public sealed record BaconTopUpBudget(bool Allowed, int Units, long MaxUnitPrice, long SpendCap, long MeatToOffer, string Note);

/// <summary>
/// The daily BACON top-up goes through the same limits as the Buy button: meat per day (checked against and booked in
/// the shared spend ledger), meat per purchase and units per purchase. A day limit of 0 buys nothing.
/// </summary>
public static class DailyBaconBudget
{
    /// <summary>Checks the limits and reserves the worst-case spend in the ledger. Call <see cref="Finish"/> afterwards.</summary>
    public static BaconTopUpBudget Reserve(
        DesktopSettings settings, MallSpendLedger ledger, string account,
        int needed, long maxUnitPrice, long meat, long meatReserve)
    {
        var perDay = (long)settings.Get("meatPerDay");
        var perPurchase = (long)settings.Get("meatPerPurchase");
        var unitCap = (int)settings.Get("unitsPerPurchase");
        var units = Math.Min(needed, unitCap);
        if (units <= 0 || maxUnitPrice <= 0)
            return new(false, 0, 0, 0, 0, "BACON top-up skipped: nothing to buy or no price limit.");
        if (perDay <= 0)
            return new(false, 0, 0, 0, 0, "BACON top-up skipped: Meat per day is 0 in Settings.");
        var ceiling = Math.Min(maxUnitPrice, perPurchase);
        var cap = Math.Min(units * ceiling, perPurchase);
        if (cap <= 0)
            return new(false, 0, 0, 0, 0, "BACON top-up skipped: Meat per purchase is 0 in Settings.");
        if (!ledger.TryReserve(account, cap, perDay, out var error))
            return new(false, 0, 0, 0, 0, "BACON top-up skipped: " + error);
        return new(true, units, ceiling, cap, Math.Min(meat, meatReserve + cap), "");
    }

    /// <summary>Books the real spend and releases the rest of the reservation. An unclear outcome stays counted.</summary>
    public static void Finish(MallSpendLedger ledger, string account, BaconTopUpBudget budget, long spent, bool unclear)
    {
        if (!budget.Allowed) return;
        if (unclear) { ledger.MarkUnclear(account, budget.SpendCap); return; }
        var settled = Math.Clamp(spent, 0, budget.SpendCap);
        ledger.Settle(account, settled);
        ledger.Release(account, budget.SpendCap - settled);
    }
}
