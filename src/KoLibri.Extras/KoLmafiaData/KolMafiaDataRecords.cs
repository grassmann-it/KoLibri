// Copied from KoLibri.Core (0.5 step 3, desktop): the KoLmafia catalog the desktop shows (images, access flags, organ costs, outfits).
// The Core copy goes away once the MCP server has moved too.
namespace KoLibri.Extras;

/// <summary>Raw monster record as published by the KoLmafia data files.</summary>
public sealed record MafiaMonsterRecord
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Phylum { get; init; } = string.Empty;
    public int Hp { get; init; }
    public int Attack { get; init; }
    public int Defense { get; init; }
    public IReadOnlyList<string> Elements { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Skills { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Items { get; init; } = Array.Empty<string>();
    public bool Adventures { get; init; }
    public bool Manuel { get; init; }
    public IReadOnlyDictionary<string, string>? Parameters { get; init; }
    public IReadOnlyList<MafiaMonsterDrop>? Drops { get; init; }
}

/// <summary>Raw item record as published by the KoLmafia data files.</summary>
public sealed record MafiaItemRecord
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public bool Tradeable { get; init; }
    public bool Usable { get; init; }
    public int HpDelta { get; init; }
    public int MpDelta { get; init; }
    public string? Effect { get; init; }
    public string? Image { get; init; }
    public string? DescId { get; init; }
    public IReadOnlyList<string> PrimaryUse { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SecondaryUse { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AccessFlags { get; init; } = Array.Empty<string>();
    public int Autosell { get; init; }
    public string? Plural { get; init; }
}

/// <summary>Raw skill record as published by the KoLmafia data files.</summary>
public sealed record MafiaSkillRecord
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public int MpCost { get; init; }
    public bool Permable { get; init; }
    public string? Image { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public string? Duration { get; init; }

    private bool HasTag(string tag) => Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));

    /// <summary>The skill is passive (the catalog tag, or the legacy type); it can never be cast or used in a fight.</summary>
    public bool IsPassive => HasTag("passive") || string.Equals(Type, "passive", StringComparison.OrdinalIgnoreCase);

    /// <summary>Castable outside a fight: tagged "nc" (non-combat). Without any tags the legacy type decides.</summary>
    public bool UsableOutsideCombat => !IsPassive
        && (HasTag("nc") || (Tags.Count == 0 && !string.Equals(Type, "combat", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(Type)));

    /// <summary>Usable in a fight: tagged "combat" (or the legacy type "combat").</summary>
    public bool UsableInCombat => !IsPassive
        && (HasTag("combat") || string.Equals(Type, "combat", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Raw adventure (location) record as published by the KoLmafia data files.</summary>
public sealed record MafiaAdventureRecord
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int Snarfblat { get; init; }
    public string Zone { get; init; } = string.Empty;
    public int TurnCost { get; init; } = 1;
    public IReadOnlyList<string> Requirements { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MonsterNames { get; init; } = Array.Empty<string>();
    public string SniffProperty { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Desc { get; init; } = string.Empty;
}

/// <summary>Raw zone record as published by the KoLmafia data files.</summary>
public sealed record MafiaZoneRecord
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string AccessItem { get; init; } = string.Empty;
}

/// <summary>Raw status-effect record as published by the KoLmafia data files.</summary>
public sealed record MafiaEffectRecord
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Image { get; init; }
    public string Quality { get; init; } = "neutral";
    public IReadOnlyList<string> Attributes { get; init; } = Array.Empty<string>();
    public string DefaultAction { get; init; } = string.Empty;
}

/// <summary>Modifier text entry — header + human-readable description.</summary>
public sealed record MafiaModifierRecord
{
    public string Header { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<string> Extras { get; init; } = Array.Empty<string>();
}

/// <summary>Concoction recipe entry — name + mix method + description.</summary>
public sealed record MafiaConcoctionRecord
{
    public string Name { get; init; } = string.Empty;
    public string MixMethod { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<string> Extras { get; init; } = Array.Empty<string>();
}

/// <summary>Monster → part name mapping from the KoLmafia monsterparts file.</summary>
public sealed record MafiaMonsterPartRecord
{
    public string Monster { get; init; } = string.Empty;
    public string Part { get; init; } = string.Empty;
    public int PartId { get; init; }
}

/// <summary>HP/MP restore entry from the KoLmafia restores file.</summary>
public sealed record MafiaRestoreRecord
{
    public string Name { get; init; } = string.Empty;
    public string Effect { get; init; } = string.Empty;
    public string Stat { get; init; } = string.Empty;
    public string Delta { get; init; } = string.Empty;
}

/// <summary>Container of every catalog loaded from the KoLmafia data files.</summary>
public sealed record KoLmafiaCatalog(
    IReadOnlyList<MafiaMonsterRecord> Monsters,
    IReadOnlyList<MafiaItemRecord> Items,
    IReadOnlyList<MafiaSkillRecord> Skills,
    IReadOnlyList<MafiaAdventureRecord> Adventures,
    IReadOnlyList<MafiaZoneRecord> Zones,
    IReadOnlyList<MafiaEffectRecord> Effects,
    IReadOnlyList<MafiaModifierRecord> Modifiers,
    IReadOnlyList<MafiaConcoctionRecord> Concoctions,
    IReadOnlyList<MafiaMonsterPartRecord> MonsterParts,
    IReadOnlyList<MafiaRestoreRecord> Restores,
    DateTimeOffset LoadedAt,
    string SourcePath)
{
    public IReadOnlyDictionary<string, string> FamiliarSpecies { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Food, drink and spleen cost rows (fullness/inebriety/spleenhit), one per item name.</summary>
    public IReadOnlyList<MafiaOrganRecord> Organs { get; init; } = Array.Empty<MafiaOrganRecord>();

    /// <summary>Equipment slot requirement rows (equipment.txt).</summary>
    public IReadOnlyList<MafiaEquipmentRecord> Equipment { get; init; } = Array.Empty<MafiaEquipmentRecord>();

    /// <summary>Outfit definitions (outfits.txt).</summary>
    public IReadOnlyList<MafiaOutfitRecord> Outfits { get; init; } = Array.Empty<MafiaOutfitRecord>();
}

/// <summary>
/// Organ cost of a food, drink or spleen item. Values KoLmafia has no data for are
/// <c>null</c> (unknown), never silently 0; <see cref="Unspaded"/> marks rows upstream
/// flags as not yet measured.
/// </summary>
public sealed record MafiaOrganRecord
{
    public string Kind { get; init; } = string.Empty; // food | drink | spleen
    public string Name { get; init; } = string.Empty;
    public string? ItemId { get; init; }
    public int Size { get; init; }
    public int? LevelRequired { get; init; }
    public string? Quality { get; init; }
    public string? Adventures { get; init; }
    public int? AdventuresMin { get; init; }
    public int? AdventuresMax { get; init; }
    public string? Muscle { get; init; }
    public string? Mysticality { get; init; }
    public string? Moxie { get; init; }
    public bool Unspaded { get; init; }
    public string Notes { get; init; } = string.Empty;
}

/// <summary>Power and stat requirement of an equippable item.</summary>
public sealed record MafiaEquipmentRecord
{
    public string Name { get; init; } = string.Empty;
    public string? ItemId { get; init; }
    public int? Power { get; init; }
    public string? Requirement { get; init; }
    public string? RequirementStat { get; init; }
    public int? RequirementValue { get; init; }
    public string? WeaponType { get; init; }
}

/// <summary>An outfit and the item ids of its parts (null where a part name did not resolve).</summary>
public sealed record MafiaOutfitRecord
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Image { get; init; }
    public IReadOnlyList<string> Parts { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string?> PartIds { get; init; } = Array.Empty<string?>();
    public IReadOnlyList<string> Treats { get; init; } = Array.Empty<string>();
}
