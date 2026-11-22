namespace KoLibri.Desktop.ContextMenus;

public enum ItemUseKind { None, Eat, Drink, Chew, Use }

/// <summary>What the item menu needs to know about a row. Tab workers map their row type onto this.</summary>
public sealed record ItemMenuTarget(
    string ItemId, string Name, int Quantity,
    ItemUseKind Use = ItemUseKind.None,
    IReadOnlyList<string>? EquipSlots = null,
    bool CanCloset = true);

/// <summary>Real item actions a tab supplies. Closet and Info only appear when the tab wires them.</summary>
public interface IItemMenuActions
{
    /// <summary>Eat, drink, chew or use <paramref name="quantity"/> of the item (through the shell's gates and confirmations).</summary>
    void Consume(ItemMenuTarget item, ItemUseKind kind, int quantity);

    /// <summary>Equip the item to the named slot ("Familiar" for familiar equipment).</summary>
    void Equip(ItemMenuTarget item, string slot);

    /// <summary>True when <see cref="MoveToCloset"/> is wired; otherwise the Closet entry is hidden.</summary>
    bool SupportsCloset => false;
    void MoveToCloset(ItemMenuTarget item) { }

    /// <summary>True when <see cref="ShowInfo"/> is wired; otherwise the Info entry is hidden.</summary>
    bool SupportsInfo => false;
    void ShowInfo(ItemMenuTarget item) { }
}

/// <summary>Item rows: use with quantity, equip to slot, closet, mall search, info, wiki and copy.</summary>
public sealed class ItemContextMenu(IShellHost host, IItemMenuActions? actions = null)
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
                actions is null
                    ? MenuActions.NotImplemented(host, $"{verb} {n} {item.Name}")
                    : () => actions.Consume(item, item.Use, n))).ToArray();
            items.Add(entries.Length == 1 ? entries[0] : ContextMenuItem.Sub(verb, entries));
        }
        if (item.EquipSlots is { Count: > 0 } slots)
            items.Add(ContextMenuItem.Sub("Equip to slot", slots.Select(slot => ContextMenuItem.Action(
                slot, actions is null
                    ? MenuActions.NotImplemented(host, $"Equip {item.Name} to {slot}")
                    : () => actions.Equip(item, slot))).ToArray()));
        if (item.CanCloset && actions is { SupportsCloset: true })
            items.Add(ContextMenuItem.Action("Closet", () => actions.MoveToCloset(item)));
        if (items.Count > 0) items.Add(ContextMenuItem.Separator);
        items.Add(ContextMenuItem.Action("Mallsearch", MenuActions.SearchMall(host, item.Name)));
        if (actions is { SupportsInfo: true })
            items.Add(ContextMenuItem.Action("Info", () => actions.ShowInfo(item)));
        items.Add(ContextMenuItem.Action("Wiki", MenuActions.Wiki(host, item.Name)));
        items.Add(ContextMenuItem.Separator);
        items.Add(ContextMenuItem.Action("Copy name", MenuActions.Copy(host, item.Name, "item name")));
        items.Add(ContextMenuItem.Action("Copy ID", MenuActions.Copy(host, item.ItemId, "item ID")));
        return items;
    }
}
