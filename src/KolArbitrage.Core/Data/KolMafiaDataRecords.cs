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
}

/// <summary>Raw skill record as published by the KoLmafia data files.</summary>
public sealed record MafiaSkillRecord
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public int MpCost { get; init; }
    public bool Permable { get; init; }
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
}

/// <summary>Raw zone record as published by the KoLmafia data files.</summary>
public sealed record MafiaZoneRecord
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
}

/// <summary>Container of every catalog loaded from the KoLmafia data files.</summary>
public sealed record KoLmafiaCatalog(
    IReadOnlyList<MafiaMonsterRecord> Monsters,
    IReadOnlyList<MafiaItemRecord> Items,
    IReadOnlyList<MafiaSkillRecord> Skills,
    IReadOnlyList<MafiaAdventureRecord> Adventures,
    IReadOnlyList<MafiaZoneRecord> Zones,
    DateTimeOffset LoadedAt,
    string SourcePath);
