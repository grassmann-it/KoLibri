using System.Text.Json;

namespace KolArbitrage.Core.Data;

/// <summary>
/// Reads KoLmafia-compatible JSON Lines snapshots from a directory and
/// returns the typed records the rest of KoLibri consumes. The loader is
/// tolerant: it skips lines that fail to parse and reports them via
/// <see cref="LoadResult.Warnings"/> so a partial catalog never breaks
/// the host.
/// </summary>
public sealed class MafiaDataLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public KoLmafiaCatalog Load(string directory)
        => Load(directory, out _);

    public KoLmafiaCatalog Load(string directory, out LoadDiagnostics diagnostics)
    {        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("A directory path is required.", nameof(directory));
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Mafia data directory '{directory}' is missing.");

        var diag = new LoadDiagnostics();
        var monsters = LoadFlexible(directory, "monsters.jsonl", diag);
        var items = LoadFlexible(directory, "items.jsonl", diag);
        var skills = LoadFlexible(directory, "skills.jsonl", diag);
        var adventures = LoadFlexible(directory, "adventures.jsonl", diag);
        var zones = LoadFlexible(directory, "zones.jsonl", diag);
        var modifiers = LoadFlexible(directory, "modifiers.jsonl", diag);
        var effects = LoadFlexible(directory, "effects.jsonl", diag);
        var concoctions = LoadFlexible(directory, "concoctions.jsonl", diag);
        var monsterParts = LoadFlexible(directory, "monsterparts.jsonl", diag);
        var restores = LoadFlexible(directory, "restores.jsonl", diag);

        var familiars = LoadFlexible(directory, "familiars.jsonl", diag);
        diagnostics = diag;
        return new KoLmafiaCatalog(
            monsters.Select(MapMonster).Where(m => m is not null).Cast<MafiaMonsterRecord>().ToArray(),
            items.Select(MapItem).Where(i => i is not null).Cast<MafiaItemRecord>().ToArray(),
            skills.Select(MapSkill).Where(s => s is not null).Cast<MafiaSkillRecord>().ToArray(),
            adventures.Select(MapAdventure).Where(a => a is not null).Cast<MafiaAdventureRecord>().ToArray(),
            zones.Select(MapZone).Where(z => z is not null).Cast<MafiaZoneRecord>().ToArray(),
            effects.Select(MapEffect).Where(e => e is not null).Cast<MafiaEffectRecord>().ToArray(),
            modifiers.Select(MapModifier).Where(m => m is not null).Cast<MafiaModifierRecord>().ToArray(),
            concoctions.Select(MapConcoction).Where(c => c is not null).Cast<MafiaConcoctionRecord>().ToArray(),
            monsterParts.Select(MapMonsterPart).Where(p => p is not null).Cast<MafiaMonsterPartRecord>().ToArray(),
            restores.Select(MapRestore).Where(r => r is not null).Cast<MafiaRestoreRecord>().ToArray(),
            DateTimeOffset.UtcNow,
            Path.GetFullPath(directory))
        {
            FamiliarSpecies = familiars.Select(row => (Id: GetString(row, "id"), Name: GetString(row, "name")))
                .Where(row => row.Id is not null && row.Name is not null)
                .GroupBy(row => row.Id!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Name!, StringComparer.Ordinal)
        };
    }

    /// <summary>Resolve the default data directory under the user's local app data.</summary>
    public static string DefaultDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrWhiteSpace(local)
            ? Path.Combine(Environment.CurrentDirectory, ".kolibri")
            : Path.Combine(local, "KoLibri");
        return Path.Combine(root, "kolmafia-data");
    }

    private static IReadOnlyList<Dictionary<string, object?>> LoadFlexible(
        string directory,
        string filename,
        LoadDiagnostics diagnostics)
    {
        var path = Path.Combine(directory, filename);
        if (!File.Exists(path)) return Array.Empty<Dictionary<string, object?>>();

        var result = new List<Dictionary<string, object?>>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream);
        string? line;
        var lineNumber = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var value = JsonSerializer.Deserialize<Dictionary<string, object?>>(line, JsonOptions);
                if (value is not null) result.Add(value);
            }
            catch (JsonException exception)
            {
                diagnostics.Warnings.Add($"{filename}:{lineNumber} parse failed: {exception.Message}");
            }
        }
        return result;
    }

    private static string? GetString(Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value is null) return null;
        string? text = value is JsonElement element
            ? element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.GetRawText(),
                _ => element.GetRawText()
            }
            : value.ToString();
        return text is null ? null : DecodeHtmlEntities(text);
    }

    /// <summary>
    /// KoLmafia's data dump stores item names with HTML entities — the name
    /// of a book is <c>&quot;2 Love Me, Vol. 2&quot;</c> and umlauts arrive as
    /// <c>&amp;Uuml;</c>. Decoding them here means every consumer sees the
    /// plain name, the way KoLmafia shows it.
    /// </summary>
    private static string DecodeHtmlEntities(string value)
    {
        if (!value.Contains('&')) return value;
        return System.Net.WebUtility.HtmlDecode(value);
    }

    private static int? GetInt(Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value is null) return null;
        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Number when element.TryGetInt32(out var n) => n,
                JsonValueKind.String when int.TryParse(element.GetString(), out var s) => s,
                _ => null
            };
        }
        return int.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }

    private static IReadOnlyList<string> GetStringList(Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value is null) return Array.Empty<string>();
        if (value is JsonElement element && element.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString() ?? string.Empty);
            }
            return list.ToArray();
        }
        return Array.Empty<string>();
    }

    private static IReadOnlyDictionary<string, string>? GetStringMap(Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value is null) return null;
        if (value is JsonElement element && element.ValueKind == JsonValueKind.Object)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in element.EnumerateObject())
            {
                map[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                    _ => prop.Value.GetRawText()
                };
            }
            return map;
        }
        return null;
    }

    private static IReadOnlyList<MafiaMonsterDrop>? GetDrops(Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value is null) return null;
        if (value is JsonElement element && element.ValueKind == JsonValueKind.Array)
        {
            var drops = new List<MafiaMonsterDrop>();
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(item.GetRawText(), JsonOptions);
                if (dict is null) continue;
                drops.Add(new MafiaMonsterDrop
                {
                    Name = GetString(dict, "name") ?? string.Empty,
                    Rate = GetInt(dict, "rate") ?? 0,
                    Flags = GetString(dict, "flags") ?? string.Empty
                });
            }
            return drops;
        }
        return null;
    }

    private static MafiaMonsterRecord? MapMonster(Dictionary<string, object?> row)
    {
        var id = GetString(row, "id");
        var name = GetString(row, "name");
        if (id is null || name is null) return null;
        return new MafiaMonsterRecord
        {
            Id = id,
            Name = name,
            Phylum = GetString(row, "phylum") ?? ExtractPhylum(GetStringMap(row, "parameters")),
            Hp = GetInt(row, "hp") ?? ExtractInt(GetStringMap(row, "parameters"), "HP") ?? 0,
            Attack = GetInt(row, "attack") ?? ExtractInt(GetStringMap(row, "parameters"), "Atk") ?? 0,
            Defense = GetInt(row, "defense") ?? ExtractInt(GetStringMap(row, "parameters"), "Def") ?? 0,
            Elements = GetStringList(row, "elements"),
            Skills = GetStringList(row, "skills"),
            Items = GetStringList(row, "items"),
            Manuel = GetInt(row, "manuel") is 1,
            Adventures = GetInt(row, "adventures") is not 0,
            Parameters = GetStringMap(row, "parameters"),
            Drops = GetDrops(row, "drops")
        };
    }

    private static string? ExtractPhylum(IReadOnlyDictionary<string, string>? parameters)
        => parameters is null || !parameters.TryGetValue("P", out var p) ? null : p;

    private static int? ExtractInt(IReadOnlyDictionary<string, string>? parameters, string key)
        => parameters is null || !parameters.TryGetValue(key, out var raw) || !int.TryParse(raw, out var n) ? null : n;

    private static MafiaItemRecord? MapItem(Dictionary<string, object?> row)
    {
        var id = GetString(row, "id");
        var name = GetString(row, "name");
        if (id is null || name is null) return null;
        return new MafiaItemRecord
        {
            Id = id,
            Name = name,
            Type = GetString(row, "type") ?? InferType(row),
            Tradeable = GetStringList(row, "accessFlags").Contains("t"),
            Usable = (GetStringList(row, "primaryUse").Count + GetStringList(row, "secondaryUse").Count) > 0,
            HpDelta = GetInt(row, "hpDelta") ?? 0,
            MpDelta = GetInt(row, "mpDelta") ?? 0,
            Effect = GetString(row, "effect"),
            Image = GetString(row, "image"),
            DescId = GetString(row, "descId"),
            PrimaryUse = GetStringList(row, "primaryUse"),
            SecondaryUse = GetStringList(row, "secondaryUse"),
            AccessFlags = GetStringList(row, "accessFlags"),
            Autosell = GetInt(row, "autosell") ?? 0,
            Plural = GetString(row, "plural") ?? name
        };
    }

    private static string InferType(Dictionary<string, object?> row)
    {
        var primary = GetStringList(row, "primaryUse");
        if (primary.Count == 0) return "none";
        return primary[0];
    }

    private static MafiaSkillRecord? MapSkill(Dictionary<string, object?> row)
    {
        var id = GetInt(row, "id");
        var name = GetString(row, "name");
        if (id is null || name is null) return null;
        return new MafiaSkillRecord
        {
            Id = id.Value,
            Name = name,
            Type = GetString(row, "type") ?? "general",
            MpCost = GetInt(row, "mpCost") ?? 0,
            Permable = GetString(row, "attributes")?.Contains("Permable: true", StringComparison.OrdinalIgnoreCase) is true,
            Image = GetString(row, "image"),
            Tags = GetStringList(row, "tags"),
            Duration = GetString(row, "duration")
        };
    }

    private static MafiaAdventureRecord? MapAdventure(Dictionary<string, object?> row)
    {
        var name = GetString(row, "name");
        if (name is null) return null;
        return new MafiaAdventureRecord
        {
            Id = GetString(row, "id") ?? GetString(row, "url") ?? name.ToLowerInvariant().Replace(' ', '-'),
            Name = name,
            Snarfblat = GetInt(row, "sniff") ?? 0,
            Zone = GetString(row, "zone") ?? string.Empty,
            TurnCost = 1,
            Requirements = Array.Empty<string>(),
            MonsterNames = GetStringList(row, "monsters"),
            SniffProperty = GetString(row, "sniffProperty") ?? string.Empty,
            Type = GetString(row, "type") ?? string.Empty,
            Desc = GetString(row, "desc") ?? string.Empty
        };
    }

    private static MafiaZoneRecord? MapZone(Dictionary<string, object?> row)
    {
        var id = GetString(row, "zoneId");
        var name = GetString(row, "displayName");
        if (id is null || name is null) return null;
        return new MafiaZoneRecord
        {
            Id = id,
            Name = name,
            Type = GetString(row, "parent") ?? string.Empty,
            AccessItem = GetString(row, "accessItem") ?? string.Empty
        };
    }

    private static MafiaEffectRecord? MapEffect(Dictionary<string, object?> row)
    {
        var id = GetInt(row, "id");
        var name = GetString(row, "name");
        if (id is null || name is null) return null;
        return new MafiaEffectRecord
        {
            Id = id.Value,
            Name = name,
            Image = GetString(row, "image"),
            Quality = GetString(row, "quality") ?? "neutral",
            Attributes = GetStringList(row, "attributes"),
            DefaultAction = GetString(row, "defaultAction") ?? string.Empty
        };
    }

    private static MafiaConcoctionRecord? MapConcoction(Dictionary<string, object?> row)
    {
        var name = GetString(row, "name");
        if (name is null) return null;
        return new MafiaConcoctionRecord
        {
            Name = name,
            MixMethod = GetString(row, "mixMethod") ?? string.Empty,
            Description = GetString(row, "description") ?? string.Empty,
            Extras = GetStringList(row, "extras")
        };
    }

    private static MafiaMonsterPartRecord? MapMonsterPart(Dictionary<string, object?> row)
    {
        var monster = GetString(row, "monster");
        var part = GetString(row, "part");
        if (monster is null || part is null) return null;
        return new MafiaMonsterPartRecord
        {
            Monster = monster,
            Part = part,
            PartId = GetInt(row, "partId") ?? 0
        };
    }

    private static MafiaRestoreRecord? MapRestore(Dictionary<string, object?> row)
    {
        var name = GetString(row, "name");
        if (name is null) return null;
        return new MafiaRestoreRecord
        {
            Name = name,
            Effect = GetString(row, "effect") ?? string.Empty,
            Stat = GetString(row, "stat") ?? string.Empty,
            Delta = GetString(row, "delta") ?? string.Empty
        };
    }

    private static MafiaModifierRecord? MapModifier(Dictionary<string, object?> row)
    {
        var header = GetString(row, "header");
        if (header is null) return null;
        return new MafiaModifierRecord
        {
            Header = header,
            Description = GetString(row, "description") ?? string.Empty,
            Extras = GetStringList(row, "extras")
        };
    }
}

/// <summary>Per-load diagnostics: skipped lines, parse warnings, counts.</summary>
public sealed record LoadDiagnostics
{
    public List<string> Warnings { get; } = new();
    public int WarningCount => Warnings.Count;
}

/// <summary>One monster drop entry with rate and pickpocket/condition flags.</summary>
public sealed record MafiaMonsterDrop
{
    public string Name { get; init; } = string.Empty;
    public int Rate { get; init; }
    public string Flags { get; init; } = string.Empty;
}
