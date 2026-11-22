namespace KoLibri.Desktop.ContextMenus;

public sealed record GearSlotMenuTarget(string Slot, string? ItemName, string? ItemId);

/// <summary>Equipment slot rows: equip or change, unequip, compare.</summary>
public sealed class GearSlotContextMenu(IShellHost host)
{
    public IReadOnlyList<ContextMenuItem> Build(GearSlotMenuTarget slot)
    {
        var filled = !string.IsNullOrEmpty(slot.ItemName);
        return
        [
            ContextMenuItem.Action(filled ? "Change…" : "Equip…", MenuActions.NotImplemented(host, $"Equip {slot.Slot}")),
            ContextMenuItem.Action("Unequip", MenuActions.NotImplemented(host, $"Unequip {slot.Slot}"))
                .When(filled, "Nothing is equipped in this slot."),
            ContextMenuItem.Action("Compare…", MenuActions.NotImplemented(host, $"Compare {slot.Slot}")),
            ContextMenuItem.Separator,
            ContextMenuItem.Action("Copy name", MenuActions.Copy(host, slot.ItemName ?? slot.Slot, "name"))
        ];
    }
}
