using KoLibri.Core.Spiel;

namespace KoLibri.Core.Spiel;

/// <summary>
/// Stable route names used by the future Kingdom of Loathing adapter. Keeping
/// route construction outside the pricing and planning code makes it possible
/// to test the workflow with an in-memory provider and to update the adapter
/// when the game changes.
/// </summary>
public static class KoLRoutes
{
    public const string Api = "api.php";
    public const string MallSearch = "mall.php";
    public const string MallStore = "mallstore.php";
    /// <summary>Own-store management page used for reads and explicit price updates.</summary>
    public const string ManageStore = "managestore.php";
    public const string Backoffice = "backoffice.php";
    /// <summary>
    /// Own-store inventory tab (listings, prices, limits, the stock form and pwd).
    /// Plain <c>backoffice.php</c> shows whichever tab was opened last, e.g. the
    /// sales log after a CRM read, so reads must name the tab explicitly.
    /// </summary>
    public const string StoreInventory = "backoffice.php?which=1";
    /// <summary>Own-store sales activity; GET only and never a mutation.</summary>
    public const string StoreSalesLog = "backoffice.php?which=3";
    public const string Adventure = "adventure.php";
    public const string Choice = "choice.php";

    public static string Status(string what = "status") => $"{Api}?what={Uri.EscapeDataString(what)}&for=KoLibri";

    public static string MallListing(string itemId, string storeId, int quantity = 1)
    {
        if (string.IsNullOrWhiteSpace(itemId)) throw new ArgumentException("Item id is required.", nameof(itemId));
        if (string.IsNullOrWhiteSpace(storeId)) throw new ArgumentException("Store id is required.", nameof(storeId));
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        return $"{MallStore}?buying=1&whichitem={Uri.EscapeDataString(itemId)}&whichstore={Uri.EscapeDataString(storeId)}&quantity={quantity}";
    }

    public static string AdventureAt(string adventureId)
    {
        if (string.IsNullOrWhiteSpace(adventureId)) throw new ArgumentException("Adventure id is required.", nameof(adventureId));
        return $"{Adventure}?snarfblat={Uri.EscapeDataString(adventureId)}";
    }

    /// <summary>
    /// Builds an adventure route from a catalog entry. The stable location key
    /// is never sent as snarfblat by accident; the numeric KoL value is used.
    /// </summary>
    public static string AdventureAtLocation(AdventureLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        var value = location.Normalize();
        if (value.Snarfblat <= 0)
            throw new ArgumentOutOfRangeException(nameof(location), "Snarfblat must be positive.");
        return AdventureAt(value.Snarfblat.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public static string AdventureAt(int snarfblat)
    {
        if (snarfblat <= 0) throw new ArgumentOutOfRangeException(nameof(snarfblat));
        return AdventureAt(snarfblat.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}

/// <summary>Mutation guardrails shared by future buy, sell and transfer adapters.</summary>
public sealed record AutomationPolicy
{
    public bool DryRun { get; init; } = true;
    public bool RequireApprovalForPurchases { get; init; } = true;
    public bool RequireApprovalForTransfers { get; init; } = true;
    public int MaxMeatPerPurchase { get; init; } = 250_000;
    public int MaxQuantityPerPurchase { get; init; } = 10;
    public int MaxDailyMeatSpend { get; init; } = 500_000;

    public void Validate()
    {
        if (MaxMeatPerPurchase < 0) throw new ArgumentOutOfRangeException(nameof(MaxMeatPerPurchase));
        if (MaxQuantityPerPurchase <= 0) throw new ArgumentOutOfRangeException(nameof(MaxQuantityPerPurchase));
        if (MaxDailyMeatSpend < 0) throw new ArgumentOutOfRangeException(nameof(MaxDailyMeatSpend));
    }
}
