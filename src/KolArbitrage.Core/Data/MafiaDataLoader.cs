using System.Text.Json;

namespace KolArbitrage.Core.Data;

/// <summary>
/// Loads a KoLmafia-style catalog from a directory of JSON Lines files.
/// Each file maps to one of the typed records; missing files yield an
/// empty collection so a partial catalog still loads.
/// </summary>
public sealed class MafiaDataLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true
    };

    public KoLmafiaCatalog Load(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("A directory path is required.", nameof(directory));
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Mafia data directory '{directory}' is missing.");

        var monsters = LoadJsonl<MafiaMonsterRecord>(Path.Combine(directory, "monsters.jsonl"));
        var items = LoadJsonl<MafiaItemRecord>(Path.Combine(directory, "items.jsonl"));
        var skills = LoadJsonl<MafiaSkillRecord>(Path.Combine(directory, "skills.jsonl"));
        var adventures = LoadJsonl<MafiaAdventureRecord>(Path.Combine(directory, "adventures.jsonl"));
        var zones = LoadJsonl<MafiaZoneRecord>(Path.Combine(directory, "zones.jsonl"));

        return new KoLmafiaCatalog(
            monsters,
            items,
            skills,
            adventures,
            zones,
            DateTimeOffset.UtcNow,
            Path.GetFullPath(directory));
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

    private static IReadOnlyList<T> LoadJsonl<T>(string path)
    {
        if (!File.Exists(path)) return Array.Empty<T>();
        var result = new List<T>();
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
                var value = JsonSerializer.Deserialize<T>(line, JsonOptions);
                if (value is not null) result.Add(value);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    $"Failed to parse '{path}' at line {lineNumber}: {exception.Message}", exception);
            }
        }
        return result.ToArray();
    }
}
