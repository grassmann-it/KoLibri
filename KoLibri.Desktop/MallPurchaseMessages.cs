
namespace KoLibri.Desktop;

/// <summary>Plain-language texts for every reason a mall purchase is refused or fails.</summary>
public static class MallPurchaseMessages
{
    public static string Describe(MallPurchaseAbortReason reason) => reason switch
    {
        MallPurchaseAbortReason.SessionInvalid => "You are not logged in. Sign in to this account and try again.",
        MallPurchaseAbortReason.InsufficientMeat => "You do not have enough Meat for this purchase.",
        MallPurchaseAbortReason.SoldOut => "The offer is sold out.",
        MallPurchaseAbortReason.MissingOffer => "The offer is gone: the shop no longer lists this item at this price.",
        MallPurchaseAbortReason.PurchaseLimitExceeded => "The shop's daily purchase limit for this item is reached or would be exceeded.",
        MallPurchaseAbortReason.PurchaseLimitChanged => "The shop's purchase limit changed. Search again.",
        MallPurchaseAbortReason.DailyLimitExceeded => "This purchase would exceed your daily Meat budget (see Settings).",
        MallPurchaseAbortReason.DailyLimitChanged => "The daily budget changed. Check the preview again.",
        MallPurchaseAbortReason.PerPurchaseLimitExceeded => "This purchase is more expensive than your per-purchase limit (see Settings).",
        MallPurchaseAbortReason.QuantityLimitExceeded => "That quantity is more than your units-per-purchase limit (see Settings).",
        MallPurchaseAbortReason.QuantityChanged => "The stock changed. Check the preview again.",
        MallPurchaseAbortReason.PriceChanged => "The price changed. Nothing was sent; confirm the new price.",
        MallPurchaseAbortReason.StaleOffer => "The offer data is stale. Search again for a fresh offer.",
        MallPurchaseAbortReason.StaleAccount => "Your account data is stale. Reload and try again.",
        MallPurchaseAbortReason.PreviewExpired => "The preview expired. Check the price again.",
        MallPurchaseAbortReason.SellerChanged or MallPurchaseAbortReason.StoreChanged
            or MallPurchaseAbortReason.ItemChanged => "The offer changed since the preview. Search again.",
        MallPurchaseAbortReason.TransportFailure => "KoL could not be reached. Check your connection and your inventory before buying again.",
        MallPurchaseAbortReason.CommitRejected => "KoL or the purchase check refused the purchase.",
        MallPurchaseAbortReason.InvalidRequest => "The purchase request is not valid.",
        MallPurchaseAbortReason.ApprovalRequired => "The purchase needs your confirmation first.",
        MallPurchaseAbortReason.DryRun => "Dry run: nothing was sent.",
        MallPurchaseAbortReason.VerificationFailed or MallPurchaseAbortReason.MeatVerificationFailed
            or MallPurchaseAbortReason.InventoryVerificationFailed or MallPurchaseAbortReason.ItemNotReceived
            or MallPurchaseAbortReason.UnexpectedQuantity or MallPurchaseAbortReason.PartialQuantity or MallPurchaseAbortReason.MeatChanged
            or MallPurchaseAbortReason.InventoryChanged or MallPurchaseAbortReason.DailySpendChanged
            or MallPurchaseAbortReason.AccountChanged =>
            "The result could not be verified. Check your inventory and Meat before buying again.",
        _ => reason.ToString()
    };

    public static string Describe(IEnumerable<MallPurchaseAbortReason> reasons)
        => string.Join(" ", reasons.Where(r => r != MallPurchaseAbortReason.None).Select(Describe).Distinct());

    /// <summary>Turns an exception from reading or sending into text a player can act on.</summary>
    public static string Describe(Exception exception) => exception switch
    {
        InvalidOperationException e when e.Message.Contains("stale", StringComparison.OrdinalIgnoreCase)
            => "The mall offer is stale; search again for a fresh offer.",
        HttpRequestException => "KoL could not be reached. Nothing was sent unless the log says otherwise.",
        _ => exception.Message
    };
}
