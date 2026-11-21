namespace KolArbitrage.Core.Data;

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
}
