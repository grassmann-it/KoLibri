namespace KoLibri.Desktop.ContextMenus;

public enum ItemUseKind { None, Eat, Drink, Chew, Use }

/// <summary>What the item menu needs to know about a row. Tab workers map their row type onto this.</summary>
public sealed record ItemMenuTarget(
    string ItemId, string Name, int Quantity,
    ItemUseKind Use = ItemUseKind.None,
    IReadOnlyList<string>? EquipSlots = null,
    bool CanCloset = true);

/// <summary>Item rows: use with quantity, equip to slot, closet, mall search, info, wiki and copy.</summary>
public sealed class ItemContextMenu(IShellHost host)
{
    public IReadOnlyList<ContextMenuItem> Build(ItemMenuTarget item)
    {
        var items = new List<ContextMenuItem>();
        if (item.Use != ItemUseKind.None)
        {
            var verb = item.Use.ToString();
            var amounts = new List<int> { 1 };
            if (item.Quantity >= 5) amounts.Add(5);
            if (item.Quantity > 1) amounts.Add(item.Quantity);
            var entries = amounts.Distinct().Select(n => ContextMenuItem.Action(
                n == item.Quantity && n > 1 ? $"{verb} all ({n})" : $"{verb} {n}",
                MenuActions.NotImplemented(host, $"{verb} {n} {item.Name}"))).ToArray();
            items.Add(entries.Length == 1 ? entries[0] : ContextMenuItem.Sub(verb, entries));
        }
        if (item.EquipSlots is { Count: > 0 } slots)
            items.Add(ContextMenuItem.Sub("Equip to slot", slots.Select(slot => ContextMenuItem.Action(
                slot, MenuActions.NotImplemented(host, $"Equip {item.Name} to {slot}"))).ToArray()));
        if (item.CanCloset)
            items.Add(ContextMenuItem.Action("Closet", MenuActions.NotImplemented(host, $"Closet {item.Name}")));
        if (items.Count > 0) items.Add(ContextMenuItem.Separator);
        items.Add(ContextMenuItem.Action("Mallsearch", MenuActions.SearchMall(host, item.Name)));
        items.Add(ContextMenuItem.Action("Info", MenuActions.NotImplemented(host, $"Info {item.Name}")));
        items.Add(ContextMenuItem.Action("Wiki", MenuActions.Wiki(host, item.Name)));
        items.Add(ContextMenuItem.Separator);
        items.Add(ContextMenuItem.Action("Copy name", MenuActions.Copy(host, item.Name, "item name")));
        items.Add(ContextMenuItem.Action("Copy ID", MenuActions.Copy(host, item.ItemId, "item ID")));
        return items;
    }
}
