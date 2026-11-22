namespace KoLibri.Desktop.ContextMenus;

public sealed record OfferMenuTarget(string ItemId, string ItemName, string ShopName, long UnitPrice, bool CanBuy)
{
    public string ShopId { get; init; } = "";
    public string? SellerName { get; init; }
    public string ShopLink => $"https://www.kingdomofloathing.com/mallstore.php?whichstore={ShopId}";
}

/// <summary>Mall offer rows. Buy goes through <paramref name="buy"/>, which opens the usual confirmation dialog.</summary>
public sealed class MallOfferContextMenu(IShellHost host, Func<Task>? buy = null, Func<Task>? searchAgain = null)
{
    public IReadOnlyList<ContextMenuItem> Build(OfferMenuTarget offer) =>
    [
        ContextMenuItem.Action("Buy…", () => Run(buy, "Buy"))
            .When(offer.CanBuy && buy is not null, "This offer cannot be bought."),
        ContextMenuItem.Action("Search this item again",
            searchAgain is null ? MenuActions.SearchMall(host, offer.ItemName) : () => Run(searchAgain, "Search")),
        ContextMenuItem.Separator,
        ContextMenuItem.Action("Copy shop link", MenuActions.Copy(host, offer.ShopLink, "shop link"))
            .When(offer.ShopId.Length > 0, "This offer has no shop id."),
        ContextMenuItem.Action("Copy seller", MenuActions.Copy(host, offer.SellerName ?? offer.ShopName, "seller")),
        ContextMenuItem.Action("Copy item name", MenuActions.Copy(host, offer.ItemName, "item name")),
        ContextMenuItem.Action("Copy price", MenuActions.Copy(host, offer.UnitPrice.ToString(System.Globalization.CultureInfo.InvariantCulture), "price")),
        ContextMenuItem.Action("Copy shop name", MenuActions.Copy(host, offer.ShopName, "shop name"))
    ];

    private async void Run(Func<Task>? work, string what)
    {
        if (work is null) return;
        try { await work(); }
        catch (Exception exception) { host.SessionLog.Error($"{what} failed: {exception.Message}"); }
    }
}
