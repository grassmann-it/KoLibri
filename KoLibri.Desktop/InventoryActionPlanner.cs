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
        { IsUsable: true, IsQuest: false } => ItemUseKind.Use,
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
        ItemEquipSlot.Weapon when c.IsOneHandedWeapon
            => new[] { nameof(CharacterEquipmentSlot.Weapon), DualWieldSlot },
        ItemEquipSlot.Weapon => new[] { nameof(CharacterEquipmentSlot.Weapon) },
        ItemEquipSlot.Offhand => new[] { nameof(CharacterEquipmentSlot.Offhand) },
        ItemEquipSlot.Container => new[] { nameof(CharacterEquipmentSlot.Container) },
        ItemEquipSlot.Accessory => new[] { nameof(CharacterEquipmentSlot.Accessory1),
            nameof(CharacterEquipmentSlot.Accessory2), nameof(CharacterEquipmentSlot.Accessory3) },
        ItemEquipSlot.Familiar => new[] { FamiliarSlot },
        _ => Array.Empty<string>()
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
            var left = limit - now;
            var over = add > left;
            overfills |= over;
            messages.Add(over
                ? $"{qty} × {c.Name} adds {add} {label}, but only {Math.Max(0, left)} of {limit} {label} remain (assumed limit). KoL may refuse it or you may overfill."
                : $"Adds {add} {label}; {left - add} of {limit} {label} would remain.");
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
            if (cost.Size is not { } size || size <= 0 || current is not { } now) continue;
            best = Math.Min(best, Math.Max(0, (limit - now) / size));
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
