using KoLibri.Core.Spiel;
using KoLibri.Desktop.ContextMenus;

namespace KoLibri.Desktop;

/// <summary>Current organ fill and the limits it is compared with.</summary>
public sealed record OrganState(int? Fullness, int? Inebriety, int? Spleen)
{
    /// <summary>Standard KoL limits. The status page does not report the real ones (they grow with
    /// stomach/liver/spleen items), so they are the default assumption and are labelled as such.</summary>
    public const int FullnessLimit = 15;
    public const int InebrietyLimit = 14;
    public const int SpleenLimit = 15;

    public static readonly OrganState Unknown = new(null, null, null);

    public (int? Current, int Limit, string Label) For(ItemOrgan organ) => organ switch
    {
        ItemOrgan.Food => (Fullness, FullnessLimit, "fullness"),
        ItemOrgan.Drink => (Inebriety, InebrietyLimit, "inebriety"),
        ItemOrgan.Spleen => (Spleen, SpleenLimit, "spleen"),
        _ => (null, 0, "")
    };

    /// <summary>"Fullness 3/15 · Inebriety 0/14 · Spleen 5/15" with "?" for unobserved values.</summary>
    public string Summary =>
        $"Fullness {Show(Fullness)}/{FullnessLimit} · Inebriety {Show(Inebriety)}/{InebrietyLimit} · Spleen {Show(Spleen)}/{SpleenLimit}";

    private static string Show(int? value) => value?.ToString() ?? "?";
}

/// <summary>Outcome of checking a planned consume against the organ limit.</summary>
public sealed record OverfillCheck(bool Known, bool Overfills, int Added, int Remaining, string Message);

/// <summary>Pure planning helpers for inventory actions: use kinds, equip slots, overfill checks.</summary>
public static class InventoryActionPlanner
{
    public static ItemUseKind UseKindOf(ItemClassification c) => c switch
    {
        { Organ: ItemOrgan.Food } => ItemUseKind.Eat,
        { Organ: ItemOrgan.Drink } => ItemUseKind.Drink,
        { Organ: ItemOrgan.Spleen } => ItemUseKind.Chew,
        { IsConsumable: true, IsUsable: true } => ItemUseKind.Use,
        _ => ItemUseKind.None
    };

    public static CharacterConsumableKind? ConsumableKindOf(ItemOrgan organ) => organ switch
    {
        ItemOrgan.Food => CharacterConsumableKind.Food,
        ItemOrgan.Drink => CharacterConsumableKind.Drink,
        ItemOrgan.Spleen => CharacterConsumableKind.Spleen,
        _ => null
    };

    /// <summary>Slot names offered in the menu. Familiar equipment is the pseudo-slot "Familiar".</summary>
    public static IReadOnlyList<string> EquipSlotsOf(ItemClassification c) => c.Slot switch
    {
        ItemEquipSlot.Hat => new[] { nameof(CharacterEquipmentSlot.Hat) },
        ItemEquipSlot.Shirt => new[] { nameof(CharacterEquipmentSlot.Shirt) },
        ItemEquipSlot.Pants => new[] { nameof(CharacterEquipmentSlot.Pants) },
        ItemEquipSlot.Weapon => new[] { nameof(CharacterEquipmentSlot.Weapon) },
        ItemEquipSlot.Offhand => new[] { nameof(CharacterEquipmentSlot.Offhand) },
        ItemEquipSlot.Container => new[] { nameof(CharacterEquipmentSlot.Container) },
        ItemEquipSlot.Accessory => new[] { nameof(CharacterEquipmentSlot.Accessory1),
            nameof(CharacterEquipmentSlot.Accessory2), nameof(CharacterEquipmentSlot.Accessory3) },
        ItemEquipSlot.Familiar => new[] { FamiliarSlot },
        _ => Array.Empty<string>()
    };

    public const string FamiliarSlot = "Familiar";

    public static string Label(ItemClassification c) => c.Category switch
    {
        ItemCategory.Consumable => c.Organ switch
        {
            ItemOrgan.Food => "Food",
            ItemOrgan.Drink => "Booze",
            ItemOrgan.Spleen => "Spleen",
            _ => "Usable"
        },
        ItemCategory.Quest => "Quest",
        ItemCategory.Gear => "Equipment",
        _ => "Misc"
    };

    /// <summary>Checks whether consuming <paramref name="quantity"/> units overfills the organ.
    /// Unknown organ cost or unobserved fill never blocks; the message says what is unknown.</summary>
    public static OverfillCheck CheckOverfill(ItemClassification c, int quantity, OrganState state)
    {
        if (c.Organ == ItemOrgan.None) return new(true, false, 0, 0, string.Empty);
        var (current, limit, label) = state.For(c.Organ);
        if (c.OrganCost?.Size is not { } size)
            return new(false, false, 0, 0, $"The {label} cost of this item is unknown, so overfilling cannot be checked.");
        var added = size * Math.Max(1, quantity);
        if (current is not { } now)
            return new(false, false, added, 0, $"Adds {added} {label}; current {label} is unknown, so overfilling cannot be checked.");
        var remaining = limit - now;
        var overfills = added > remaining;
        var message = overfills
            ? $"{quantity} × {c.Name} adds {added} {label}, but only {Math.Max(0, remaining)} of {limit} {label} remain (assumed limit). KoL may refuse it or you may overfill."
            : $"Adds {added} {label}; {remaining - added} of {limit} {label} would remain.";
        return new(true, overfills, added, remaining, message);
    }

    /// <summary>The largest quantity that still fits, never below 0.</summary>
    public static int MaxFitting(ItemClassification c, OrganState state)
    {
        var check = CheckOverfill(c, 1, state);
        if (!check.Known || check.Added <= 0) return int.MaxValue;
        return Math.Max(0, check.Remaining / check.Added);
    }
}
