namespace KoLibri.Desktop.ContextMenus;

public sealed record OfferMenuTarget(string ItemId, string ItemName, string ShopName, long UnitPrice, bool CanBuy);

/// <summary>Mall offer rows. Buy stays a placeholder here; the purchase flow keeps its own confirmation.</summary>
public sealed class MallOfferContextMenu(IShellHost host)
{
    public IReadOnlyList<ContextMenuItem> Build(OfferMenuTarget offer) =>
    [
        ContextMenuItem.Action("Buy…", MenuActions.NotImplemented(host, $"Buy {offer.ItemName} from {offer.ShopName}"))
            .When(offer.CanBuy, "This offer cannot be bought."),
        ContextMenuItem.Action("Search item", MenuActions.SearchMall(host, offer.ItemName)),
        ContextMenuItem.Separator,
        ContextMenuItem.Action("Copy item name", MenuActions.Copy(host, offer.ItemName, "item name")),
        ContextMenuItem.Action("Copy price", MenuActions.Copy(host, offer.UnitPrice.ToString(System.Globalization.CultureInfo.InvariantCulture), "price")),
        ContextMenuItem.Action("Copy shop name", MenuActions.Copy(host, offer.ShopName, "shop name"))
    ];
}
