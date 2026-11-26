// Copied from KoLibri.Core (0.5 step 3, desktop): the KoLmafia catalog the desktop shows (images, access flags, organ costs, outfits).
// The Core copy goes away once the MCP server has moved too.
namespace KoLibri.Extras;

/// <summary>Which organ an item fills when consumed.</summary>
public enum ItemOrgan { None, Food, Drink, Spleen }

/// <summary>Equipment slot of an item, derived from its primary use tag.</summary>
public enum ItemEquipSlot { None, Hat, Shirt, Pants, Weapon, Offhand, Container, Accessory, Familiar }

/// <summary>Single inventory category an item is shown under. Precedence: Consumable, Quest, Gear, Other.</summary>
public enum ItemCategory { Other, Consumable, Quest, Gear }

/// <summary>Stat requirement of a piece of equipment; <see cref="Known"/> is false when upstream has no row.</summary>
public sealed record ItemEquipRequirement(bool Known, string? Stat, int? Value, int? Power)
{
    public static readonly ItemEquipRequirement Unknown = new(false, null, null, null);
    public bool HasRequirement => Known && Stat is not null && Value is > 0;
}

/// <summary>Cost of consuming one unit; every numeric is null when unknown.</summary>
public sealed record ItemOrganCost(
    ItemOrgan Organ, int? Size, int? LevelRequired, string? Quality,
    int? AdventuresMin, int? AdventuresMax, bool Unspaded);

/// <summary>Everything the views need to know about one item. Built by <see cref="ItemClassifier"/>.</summary>
public sealed record ItemClassification
{
    public required string ItemId { get; init; }
    /// <summary>False when the id is not in the catalog; all other facts are then defaults.</summary>
    public bool Known { get; init; }
    public string? Name { get; init; }
    public ItemOrgan Organ { get; init; }
    /// <summary>Null for non-organ items, and for organ items upstream has no cost row for.</summary>
    public ItemOrganCost? OrganCost { get; init; }
    /// <summary>Every organ cost row the catalog has for this item (an item can cost fullness and inebriety,
    /// and an item that is only "usable" can still carry one). Empty when none is known.</summary>
    public IReadOnlyList<ItemOrganCost> OrganCosts { get; init; } = Array.Empty<ItemOrganCost>();
    /// <summary>"Use" action applies (not food/drink/spleen, not equipment).</summary>
    public bool IsUsable { get; init; }
    public bool IsReusable { get; init; }
    public bool IsMultiple { get; init; }
    public bool IsCombatItem { get; init; }
    public ItemEquipSlot Slot { get; init; }
    public bool IsFamiliarEquipment => Slot == ItemEquipSlot.Familiar;
    public ItemEquipRequirement Requirement { get; init; } = ItemEquipRequirement.Unknown;
    /// <summary>Catalog weapon type such as "1-handed club"; null when unknown or not a weapon.</summary>
    public string? WeaponType { get; init; }
    /// <summary>A one-handed weapon, which can also go into the offhand slot with Dual Wield.</summary>
    public bool IsOneHandedWeapon => Slot == ItemEquipSlot.Weapon
        && WeaponType?.StartsWith("1-handed", StringComparison.OrdinalIgnoreCase) == true;
    public bool IsQuest { get; init; }
    public bool IsGiftable { get; init; }
    public bool IsTradeable { get; init; }
    public bool IsDiscardable { get; init; }

    public bool IsFood => Organ == ItemOrgan.Food;
    public bool IsDrink => Organ == ItemOrgan.Drink;
    public bool IsSpleen => Organ == ItemOrgan.Spleen;
    public bool IsGear => Slot != ItemEquipSlot.None;
    /// <summary>Food/drink/spleen, or a usable, non-quest, non-reusable item.</summary>
    public bool IsConsumable => Organ != ItemOrgan.None || (IsUsable && !IsQuest && !IsReusable);

    public ItemCategory Category
        => IsConsumable ? ItemCategory.Consumable
         : IsQuest ? ItemCategory.Quest
         : IsGear ? ItemCategory.Gear
         : ItemCategory.Other;

    public bool Matches(ItemCategory category) => category switch
    {
        ItemCategory.Consumable => IsConsumable,
        ItemCategory.Quest => IsQuest,
        ItemCategory.Gear => IsGear,
        _ => Category == ItemCategory.Other
    };
}

/// <summary>Classifies items by id from a loaded <see cref="KoLmafiaCatalog"/>. Thread-safe, immutable.</summary>
public sealed class ItemClassifier
{
    private static readonly HashSet<string> NonUseTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "none", "food", "drink", "spleen", "hat", "shirt", "pants", "weapon", "offhand",
        "container", "accessory", "familiar", "food helper", "drink helper"
    };

    private readonly Dictionary<string, ItemClassification> _byId;

    public ItemClassifier(KoLmafiaCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var organsById = new Dictionary<string, MafiaOrganRecord>(StringComparer.Ordinal);
        foreach (var organ in catalog.Organs)
            if (organ.ItemId is not null) organsById.TryAdd(organ.ItemId + "|" + organ.Kind, organ);
        var equipById = new Dictionary<string, MafiaEquipmentRecord>(StringComparer.Ordinal);
        foreach (var equip in catalog.Equipment)
            if (equip.ItemId is not null) equipById.TryAdd(equip.ItemId, equip);

        _byId = new Dictionary<string, ItemClassification>(StringComparer.Ordinal);
        foreach (var item in catalog.Items)
            _byId.TryAdd(item.Id, Build(item, organsById, equipById));
    }

    public int Count => _byId.Count;
    public IEnumerable<ItemClassification> All => _byId.Values;

    /// <summary>Classification for an id; an unknown id yields <c>Known == false</c> and category Other.</summary>
    public ItemClassification Classify(string? itemId)
    {
        var id = itemId?.Trim() ?? string.Empty;
        return _byId.TryGetValue(id, out var found) ? found : new ItemClassification { ItemId = id };
    }

    public ItemClassification Classify(int itemId) => Classify(itemId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public bool Matches(string? itemId, ItemCategory category) => Classify(itemId).Matches(category);

    /// <summary>Whether the "Use" action applies for these use tags (see <see cref="ItemClassification.IsUsable"/>).</summary>
    public static bool IsUsableByUseTags(IReadOnlyList<string> primary, IReadOnlyList<string> secondary)
        => primary.Any(t => !NonUseTags.Contains(t))
        || secondary.Any(t => t is "usable" or "multiple" or "reusable");

    private static ItemClassification Build(
        MafiaItemRecord item,
        Dictionary<string, MafiaOrganRecord> organs,
        Dictionary<string, MafiaEquipmentRecord> equipment)
    {
        var primary = item.PrimaryUse;
        var secondary = item.SecondaryUse;
        var flags = item.AccessFlags;

        var organ = primary.Contains("food") ? ItemOrgan.Food
            : primary.Contains("drink") ? ItemOrgan.Drink
            : primary.Contains("spleen") ? ItemOrgan.Spleen
            : ItemOrgan.None;

        ItemOrganCost? cost = null;
        if (organ != ItemOrgan.None && organs.TryGetValue(item.Id + "|" + organ.ToString().ToLowerInvariant(), out var row))
            cost = new ItemOrganCost(organ, row.Size, row.LevelRequired, row.Quality,
                row.AdventuresMin, row.AdventuresMax, row.Unspaded);

        var allCosts = new List<ItemOrganCost>();
        foreach (var kind in new[] { ItemOrgan.Food, ItemOrgan.Drink, ItemOrgan.Spleen })
            if (organs.TryGetValue(item.Id + "|" + kind.ToString().ToLowerInvariant(), out var costRow))
                allCosts.Add(new ItemOrganCost(kind, costRow.Size, costRow.LevelRequired, costRow.Quality,
                    costRow.AdventuresMin, costRow.AdventuresMax, costRow.Unspaded));

        var slot = primary.Contains("hat") ? ItemEquipSlot.Hat
            : primary.Contains("shirt") ? ItemEquipSlot.Shirt
            : primary.Contains("pants") ? ItemEquipSlot.Pants
            : primary.Contains("weapon") ? ItemEquipSlot.Weapon
            : primary.Contains("offhand") ? ItemEquipSlot.Offhand
            : primary.Contains("container") ? ItemEquipSlot.Container
            : primary.Contains("accessory") ? ItemEquipSlot.Accessory
            : primary.Contains("familiar") ? ItemEquipSlot.Familiar
            : ItemEquipSlot.None;

        var requirement = ItemEquipRequirement.Unknown;
        if (slot == ItemEquipSlot.Familiar)
            requirement = new ItemEquipRequirement(true, null, null, null); // no stat requirement
        else if (slot != ItemEquipSlot.None && equipment.TryGetValue(item.Id, out var eq))
            requirement = new ItemEquipRequirement(true, eq.RequirementStat, eq.RequirementValue, eq.Power);

        var isCombat = secondary.Any(t => t is "combat" or "combat reusable");
        return new ItemClassification
        {
            ItemId = item.Id,
            Known = true,
            Name = item.Name,
            Organ = organ,
            OrganCost = cost,
            OrganCosts = allCosts,
            IsUsable = organ == ItemOrgan.None && slot == ItemEquipSlot.None && IsUsableByUseTags(primary, secondary),
            IsReusable = secondary.Any(t => t is "reusable" or "combat reusable"),
            IsMultiple = secondary.Contains("multiple"),
            IsCombatItem = isCombat,
            Slot = slot,
            Requirement = requirement,
            WeaponType = slot == ItemEquipSlot.Weapon && equipment.TryGetValue(item.Id, out var weaponRow) ? weaponRow.WeaponType : null,
            IsQuest = flags.Contains("q"),
            IsGiftable = flags.Contains("g"),
            IsTradeable = flags.Contains("t"),
            IsDiscardable = flags.Contains("d")
        };
    }
}
