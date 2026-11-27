using KoLibri.Desktop.ContextMenus;

namespace KoLibri.Desktop;

/// <summary>Current organ fill and the limits it is compared with. A limit is null when the status read did not
/// report it: real limits vary per character (path, skills, items), so no constant is assumed.</summary>
public sealed record OrganState(int? Fullness, int? Inebriety, int? Spleen,
    int? FullnessLimit = null, int? InebrietyLimit = null, int? SpleenLimit = null)
{
    public static readonly OrganState Unknown = new(null, null, null);

    /// <summary>From the library status: the current fill is always read; a limit is null when the charsheet did not report it.</summary>
    public static OrganState From(Status s) => new(
        s.Fullness.Current, s.Inebriety.Current, s.Spleen.Current,
        Positive(s.Fullness.Limit), Positive(s.Inebriety.Limit), Positive(s.Spleen.Limit));

    private static int? Positive(int? v) => v is > 0 ? v : null;

    public (int? Current, int? Limit, string Label) For(ItemOrgan organ) => organ switch
    {
        ItemOrgan.Food => (Fullness, FullnessLimit, "fullness"),
        ItemOrgan.Drink => (Inebriety, InebrietyLimit, "inebriety"),
        ItemOrgan.Spleen => (Spleen, SpleenLimit, "spleen"),
        _ => (null, null, "")
    };

    /// <summary>"Fullness 3/15 · Inebriety 0/? · Spleen 5/15" with "?" for unobserved values and limits.</summary>
    public string Summary =>
        $"Fullness {Show(Fullness)}/{Show(FullnessLimit)} · Inebriety {Show(Inebriety)}/{Show(InebrietyLimit)} · Spleen {Show(Spleen)}/{Show(SpleenLimit)}";

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
        { IsUsable: true, IsQuest: false } => ItemUseKind.Use,
        _ => ItemUseKind.None
    };

    public static ConsumeKind? ConsumableKindOf(ItemOrgan organ) => organ switch
    {
        ItemOrgan.Food => ConsumeKind.Eat,
        ItemOrgan.Drink => ConsumeKind.Drink,
        ItemOrgan.Spleen => ConsumeKind.Chew,
        _ => null
    };

    /// <summary>Slot names offered in the menu. Familiar equipment is the pseudo-slot "Familiar".</summary>
    public static IReadOnlyList<string> EquipSlotsOf(ItemClassification c) => c.Slot switch
    {
        ItemEquipSlot.Hat => new[] { "Hat" },
        ItemEquipSlot.Shirt => new[] { "Shirt" },
        ItemEquipSlot.Pants => new[] { "Pants" },
        ItemEquipSlot.Weapon when c.IsOneHandedWeapon
            => new[] { "Weapon", DualWieldSlot },
        ItemEquipSlot.Weapon => new[] { "Weapon" },
        ItemEquipSlot.Offhand => new[] { "Offhand" },
        ItemEquipSlot.Container => new[] { "Container" },
        ItemEquipSlot.Accessory => new[] { "Accessory1", "Accessory2", "Accessory3" },
        ItemEquipSlot.Familiar => new[] { FamiliarSlot },
        _ => Array.Empty<string>()
    };

    /// <summary>The library slot of a menu slot name (the names stay as 0.4 showed them: Offhand, Container).</summary>
    public static EquipSlot? EquipSlotOf(string slotName) => slotName switch
    {
        "Hat" => EquipSlot.Hat,
        "Weapon" => EquipSlot.Weapon,
        "Offhand" or DualWieldSlot => EquipSlot.OffHand,
        "Container" => EquipSlot.Back,
        "Shirt" => EquipSlot.Shirt,
        "Pants" => EquipSlot.Pants,
        "Accessory1" => EquipSlot.Accessory1,
        "Accessory2" => EquipSlot.Accessory2,
        "Accessory3" => EquipSlot.Accessory3,
        FamiliarSlot => EquipSlot.Familiar,
        _ => null
    };

    /// <summary>The slot name the Gear page and the menus show for a library slot.</summary>
    public static string SlotLabel(EquipSlot slot) => slot switch
    {
        EquipSlot.OffHand => "Offhand",
        EquipSlot.Back => "Container",
        _ => slot.ToString()
    };

    public const string FamiliarSlot = "Familiar";

    /// <summary>Pseudo-slot for a one-handed weapon in the offhand (KoL's dual wield action).</summary>
    public const string DualWieldSlot = "Offhand (dual wield)";

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

    /// <summary>Checks whether consuming <paramref name="quantity"/> units overfills any organ the item costs
    /// (all catalog cost rows count: a drunki-bear costs fullness and inebriety, an ice stein is "usable" but
    /// costs inebriety). <see cref="OverfillCheck.Known"/> is false when a cost or a current fill is unknown;
    /// callers must then ask instead of sending silently.</summary>
    public static OverfillCheck CheckOverfill(ItemClassification c, int quantity, OrganState state)
    {
        var qty = Math.Max(1, quantity);
        // An item missing from the catalog has no cost data at all: that is "unknown", never "free".
        if (!c.Known)
            return new(false, false, 0, 0, "This item is not in the item catalog, so its organ cost is unknown and overfilling cannot be checked.");
        var costs = c.OrganCosts.Count > 0 ? c.OrganCosts
            : c.OrganCost is { } single ? new[] { single }
            : Array.Empty<ItemOrganCost>();
        if (costs.Count == 0)
        {
            if (c.Organ == ItemOrgan.None) return new(true, false, 0, 0, string.Empty);
            var (_, _, organLabel) = state.For(c.Organ);
            return new(false, false, 0, 0, $"The {organLabel} cost of this item is unknown, so overfilling cannot be checked.");
        }

        var known = true;
        var overfills = false;
        var messages = new List<string>();
        var added = 0;
        var remaining = 0;
        var first = true;
        foreach (var cost in costs)
        {
            var (current, limit, label) = state.For(cost.Organ);
            if (cost.Size is not { } size)
            {
                known = false;
                messages.Add($"The {label} cost of this item is unknown, so overfilling cannot be checked.");
                continue;
            }
            var add = size * qty;
            if (current is not { } now)
            {
                known = false;
                messages.Add($"Adds {add} {label}; current {label} is unknown, so overfilling cannot be checked.");
                if (first) { added = add; first = false; }
                continue;
            }
            if (limit is not { } lim)
            {
                known = false;
                messages.Add($"Adds {add} {label}; your {label} limit is not known (KoL did not report it), so overfilling is unverified.");
                if (first) { added = add; first = false; }
                continue;
            }
            var left = lim - now;
            var over = add > left;
            overfills |= over;
            messages.Add(over
                ? $"{qty} × {c.Name} adds {add} {label}, but only {Math.Max(0, left)} of {lim} {label} remain. KoL may refuse it or you may overfill."
                : $"Adds {add} {label}; {left - add} of {lim} {label} would remain.");
            if (first || over) { added = add; remaining = left; first = false; }
        }
        return new(known, overfills, added, remaining, string.Join(" ", messages));
    }

    /// <summary>The largest quantity that still fits every organ the item costs, never below 0.</summary>
    public static int MaxFitting(ItemClassification c, OrganState state)
    {
        var costs = c.OrganCosts.Count > 0 ? c.OrganCosts
            : c.OrganCost is { } single ? new[] { single }
            : Array.Empty<ItemOrganCost>();
        var best = int.MaxValue;
        foreach (var cost in costs)
        {
            var (current, limit, _) = state.For(cost.Organ);
            if (cost.Size is not { } size || size <= 0 || current is not { } now || limit is not { } lim) continue;
            best = Math.Min(best, Math.Max(0, (lim - now) / size));
        }
        return best;
    }

    /// <summary>True when the check must stop the action and ask: the item would overfill, or an organ cost or
    /// current fill needed for the decision is unknown.</summary>
    public static bool NeedsConfirmation(OverfillCheck check) => check.Overfills || !check.Known;

    /// <summary>The verdict of one consume/use, computed only from numbers read right before and right after the
    /// action (never from the possibly outdated list row).</summary>
    public sealed record ConsumeVerdict(bool Ok, string Message);

    /// <summary>Judges a finished consume or use. <paramref name="heldBefore"/> is the stock read right before the
    /// action, <paramref name="heldAfter"/> the stock read after it. A reusable item keeps its stock, so there the
    /// accepted answer is the evidence. <paramref name="stopReason"/> explains why a Use series ended early.</summary>
    public static ConsumeVerdict JudgeConsume(
        string verb, string kind, string name, int requested, int heldBefore, int heldAfter,
        bool reusable, string? rejection, bool stale, string? stopReason = null)
    {
        var delta = heldBefore - heldAfter;
        var stopped = stopReason is null ? "" : $" {stopReason}";
        if (rejection is not null)
            return new(false, $"{kind} {name} failed: {rejection}" + (delta > 0 ? $" ({delta} succeeded first)" : "") + stopped);
        if (stale)
            return new(false, $"KoL did not return a fresh inventory after {kind.ToLowerInvariant()}ing {name}; it may have happened. Refresh and check before repeating.");
        if (reusable)
            return stopReason is null
                ? new(true, $"Used {name} (reusable: the stock of {heldAfter} stays; KoL accepted it).")
                : new(false, $"{kind} {name}: {stopReason}");
        if (delta == requested && stopReason is null)
            return new(true, $"{verb} {delta} × {name}; {heldAfter} left.");
        if (delta <= 0)
            return new(false, $"KoL did not confirm {kind.ToLowerInvariant()}ing {name} (you still hold {heldAfter}).{stopped}");
        return new(false, delta < requested
            ? $"{kind} {name}: only {delta} of {requested} were confirmed; {heldAfter} left.{stopped}"
            : $"{kind} {name}: stock dropped by {delta} but {requested} were requested; another client may have changed it too. Refresh and check.");
    }

    /// <summary>One readable line about an item for the "Info" entry.</summary>
    public static string Describe(ItemClassification c, string name, int quantity)
    {
        var parts = new List<string> { $"{name} (#{c.ItemId}) × {quantity}", Label(c) };
        foreach (var cost in c.OrganCosts)
            parts.Add($"{cost.Size?.ToString() ?? "?"} {(cost.Organ switch { ItemOrgan.Food => "fullness", ItemOrgan.Drink => "inebriety", _ => "spleen" })}");
        if (c.IsReusable) parts.Add("reusable");
        if (c.IsQuest) parts.Add("quest item");
        if (!c.Known) parts.Add("not in the item catalog");
        return string.Join(" · ", parts);
    }
}
