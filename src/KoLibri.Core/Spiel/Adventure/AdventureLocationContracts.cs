using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using KoLibri.Core.Basis;

namespace KoLibri.Core.Spiel;

/// <summary>The account condition type used by an adventure location catalog.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AdventureLocationRequirementKind
{
    Unknown,
    Level,
    Item,
    Effect,
    Quest,
    Path,
    Familiar,
    Skill,
    Property,
    Custom
}

/// <summary>Read-only encounter metadata attached to a catalog location.</summary>
public sealed record AdventureEncounterMetadata
{
    public string EncounterId { get; init; } = string.Empty;
    public AdventureEncounterKind Kind { get; init; } = AdventureEncounterKind.Unknown;
    public string? Name { get; init; }
    public string? MonsterId { get; init; }
    public string? ChoiceId { get; init; }
    public IReadOnlyList<string> Advisories { get; init; } = Array.Empty<string>();

    public AdventureEncounterMetadata()
    {
    }

    public AdventureEncounterMetadata(
        string encounterId,
        AdventureEncounterKind kind = AdventureEncounterKind.Unknown,
        string? name = null,
        string? monsterId = null,
        string? choiceId = null,
        IEnumerable<string>? advisories = null)
    {
        EncounterId = encounterId?.Trim() ?? string.Empty;
        Kind = kind;
        Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        MonsterId = string.IsNullOrWhiteSpace(monsterId) ? null : monsterId.Trim();
        ChoiceId = string.IsNullOrWhiteSpace(choiceId) ? null : choiceId.Trim();
        Advisories = (advisories ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public AdventureEncounterMetadata Normalize()
        => this with
        {
            EncounterId = EncounterId?.Trim() ?? string.Empty,
            Name = string.IsNullOrWhiteSpace(Name) ? null : Name.Trim(),
            MonsterId = string.IsNullOrWhiteSpace(MonsterId) ? null : MonsterId.Trim(),
            ChoiceId = string.IsNullOrWhiteSpace(ChoiceId) ? null : ChoiceId.Trim(),
            Advisories = (Advisories ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
}

/// <summary>Whether a location is currently available from an account snapshot.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AdventureLocationUnlockState
{
    Unknown,
    Locked,
    Unlocked,
    RequirementsMissing
}

/// <summary>
/// Structured representation of one location requirement. The compact string
/// list on <see cref="AdventureLocation"/> remains available for simple hosts.
/// </summary>
public sealed record AdventureLocationRequirement
{
    public string Key { get; init; } = string.Empty;
    public string? Value { get; init; }
    public AdventureLocationRequirementKind Kind { get; init; }
        = AdventureLocationRequirementKind.Unknown;
    public int? Minimum { get; init; }
    public bool Optional { get; init; }

    public AdventureLocationRequirement()
    {
    }

    public AdventureLocationRequirement(
        string key,
        string? value = null,
        AdventureLocationRequirementKind kind = AdventureLocationRequirementKind.Unknown,
        int? minimum = null,
        bool optional = false)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Requirement key is required.", nameof(key));
        if (minimum is < 0)
            throw new ArgumentOutOfRangeException(nameof(minimum));

        Key = key.Trim();
        Value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        Kind = kind;
        Minimum = minimum;
        Optional = optional;
    }

    [JsonIgnore]
    public string Text
        => string.IsNullOrWhiteSpace(Value) ? Key.Trim() : $"{Key.Trim()}={Value.Trim()}";

    public AdventureLocationRequirement Normalize()
        => this with
        {
            Key = Key.Trim(),
            Value = string.IsNullOrWhiteSpace(Value) ? null : Value.Trim(),
            Minimum = Minimum is < 0 ? 0 : Minimum
        };

    public override string ToString() => Text;
}

/// <summary>
/// Read-only catalog entry for one adventure location. LocationId is KoLibri's
/// stable key, Snarfblat is KoL's numeric form value, and Zone is a grouping.
/// </summary>
public sealed record AdventureLocation
{
    public string LocationId { get; init; } = string.Empty;
    public int Snarfblat { get; init; }
    public string Zone { get; init; } = string.Empty;
    public IReadOnlyList<string> Requirements { get; init; } = Array.Empty<string>();
    public int TurnCost { get; init; } = 1;

    /// <summary>Optional typed conditions for hosts that evaluate requirements.</summary>
    public IReadOnlyList<AdventureLocationRequirement> RequirementContracts { get; init; }
        = Array.Empty<AdventureLocationRequirement>();
    public IReadOnlyList<AdventureEncounterMetadata> EncounterMetadata { get; init; }
        = Array.Empty<AdventureEncounterMetadata>();
    public AdventureLocationUnlockState UnlockState { get; init; }
        = AdventureLocationUnlockState.Unknown;

    [JsonIgnore]
    public IReadOnlyList<AdventureLocationRequirement> StructuredRequirements
        => RequirementContracts;
    [JsonIgnore]
    public IReadOnlyList<AdventureEncounterMetadata> Encounters => EncounterMetadata;
    [JsonIgnore]
    public bool IsUnlocked => UnlockState == AdventureLocationUnlockState.Unlocked;

    [JsonIgnore]
    public string Id => LocationId;
    [JsonIgnore]
    public string LocationKey => LocationId;
    [JsonIgnore]
    public string AdventureId => LocationId;
    [JsonIgnore]
    public int SnarfblatId => Snarfblat;
    [JsonIgnore]
    public string SnarfblatValue => Snarfblat.ToString(CultureInfo.InvariantCulture);
    [JsonIgnore]
    public string SnarfblatText => SnarfblatValue;
    [JsonIgnore]
    public string SnarfblatParameter => SnarfblatValue;
    [JsonIgnore]
    public string ZoneId => Zone;
    [JsonIgnore]
    public int Turns => TurnCost;
    [JsonIgnore]
    public bool HasRequirements => (Requirements?.Count ?? 0) > 0
        || (RequirementContracts?.Count ?? 0) > 0;

    public AdventureLocation()
    {
    }

    public AdventureLocation(
        string locationId,
        int snarfblat,
        string zone,
        IEnumerable<string>? requirements = null,
        int turnCost = 1,
        IEnumerable<AdventureLocationRequirement>? requirementContracts = null,
        IEnumerable<AdventureEncounterMetadata>? encounterMetadata = null,
        AdventureLocationUnlockState unlockState = AdventureLocationUnlockState.Unknown)
    {
        LocationId = locationId?.Trim() ?? string.Empty;
        Snarfblat = snarfblat;
        Zone = zone?.Trim() ?? string.Empty;
        Requirements = NormalizeTextRequirements(requirements);
        TurnCost = turnCost;
        RequirementContracts = NormalizeRequirementContracts(requirementContracts);
        EncounterMetadata = NormalizeEncounterMetadata(encounterMetadata);
        UnlockState = unlockState;
    }

    /// <summary>Convenience overload for catalogs that keep form values as text.</summary>
    public AdventureLocation(
        string locationId,
        string snarfblat,
        string zone,
        IEnumerable<string>? requirements = null,
        int turnCost = 1,
        IEnumerable<AdventureLocationRequirement>? requirementContracts = null,
        IEnumerable<AdventureEncounterMetadata>? encounterMetadata = null,
        AdventureLocationUnlockState unlockState = AdventureLocationUnlockState.Unknown)
        : this(locationId,
            ParseSnarfblat(snarfblat),
            zone,
            requirements,
            turnCost,
            requirementContracts,
            encounterMetadata,
            unlockState)
    {
    }

    public AdventureLocation(
        string locationId,
        int snarfblat,
        string zone,
        int turnCost,
        IEnumerable<string>? requirements = null,
        IEnumerable<AdventureLocationRequirement>? requirementContracts = null,
        IEnumerable<AdventureEncounterMetadata>? encounterMetadata = null,
        AdventureLocationUnlockState unlockState = AdventureLocationUnlockState.Unknown)
        : this(locationId, snarfblat, zone, requirements, turnCost, requirementContracts,
            encounterMetadata, unlockState)
    {
    }

    public AdventureLocation Normalize()
    {
        var compact = NormalizeTextRequirements(Requirements);
        var structured = NormalizeRequirementContracts(RequirementContracts);
        var encounters = NormalizeEncounterMetadata(EncounterMetadata);
        if (structured.Count > 0)
        {
            var values = compact.ToList();
            foreach (var requirement in structured)
            {
                if (!values.Contains(requirement.Text, StringComparer.OrdinalIgnoreCase))
                    values.Add(requirement.Text);
            }
            compact = values;
        }

        return this with
        {
            LocationId = LocationId?.Trim() ?? string.Empty,
            Zone = Zone?.Trim() ?? string.Empty,
            Requirements = compact,
            RequirementContracts = structured,
            EncounterMetadata = encounters,
            TurnCost = Math.Max(1, TurnCost)
        };
    }

    private static IReadOnlyList<string> NormalizeTextRequirements(
        IEnumerable<string>? requirements)
        => (requirements ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static int ParseSnarfblat(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

    private static IReadOnlyList<AdventureLocationRequirement> NormalizeRequirementContracts(
        IEnumerable<AdventureLocationRequirement>? requirements)
        => (requirements ?? Array.Empty<AdventureLocationRequirement>())
            .Where(requirement => requirement is not null
                && !string.IsNullOrWhiteSpace(requirement.Key))
            .Select(requirement => requirement.Normalize())
            .GroupBy(requirement => requirement.Text, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

    private static IReadOnlyList<AdventureEncounterMetadata> NormalizeEncounterMetadata(
        IEnumerable<AdventureEncounterMetadata>? encounters)
        => (encounters ?? Array.Empty<AdventureEncounterMetadata>())
            .Where(encounter => encounter is not null
                && !string.IsNullOrWhiteSpace(encounter.EncounterId))
            .Select(encounter => encounter.Normalize())
            .GroupBy(encounter => encounter.EncounterId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
}

/// <summary>Read-only boundary for a local adventure location catalog.</summary>
public interface IAdventureLocationCatalog
{
    IReadOnlyCollection<AdventureLocation> Locations { get; }
    int Count { get; }
    bool TryGet(string locationId, out AdventureLocation location);
    bool TryGetByLocationId(string locationId, out AdventureLocation location);
    bool TryGetBySnarfblat(int snarfblat, out AdventureLocation location);
    AdventureLocation? Find(string? locationId);
    AdventureLocation? FindBySnarfblat(int snarfblat);
}

/// <summary>Short interface alias for hosts that omit the Adventure prefix.</summary>
public interface ILocationCatalog : IAdventureLocationCatalog
{
}

/// <summary>
/// Fixture-friendly location catalog. It never creates an HttpClient or calls a
/// KoL endpoint; JSON helpers only consume caller-provided local text/files.
/// </summary>
public class AdventureLocationCatalog : IAdventureLocationCatalog, ILocationCatalog
{
    private readonly Dictionary<string, AdventureLocation> _byLocationId
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, AdventureLocation> _bySnarfblat = new();

    public AdventureLocationCatalog()
    {
    }

    public AdventureLocationCatalog(IEnumerable<AdventureLocation>? locations)
    {
        foreach (var location in locations ?? Array.Empty<AdventureLocation>())
            Add(location);
    }

    public int Count => _byLocationId.Count;
    public IReadOnlyCollection<AdventureLocation> Locations => _byLocationId.Values.ToArray();
    public IReadOnlyCollection<AdventureLocation> Entries => Locations;
    public IReadOnlyCollection<AdventureLocation> All => Locations;
    public IReadOnlyDictionary<string, AdventureLocation> ByLocationId => _byLocationId;
    public IReadOnlyDictionary<int, AdventureLocation> BySnarfblat => _bySnarfblat;
    public static AdventureLocationCatalog Empty => new();

    public AdventureLocation this[string locationId] => Get(locationId);

    public bool TryGet(string locationId, out AdventureLocation location)
        => TryGetByLocationId(locationId, out location);

    public bool TryGetValue(string locationId, out AdventureLocation location)
        => TryGetByLocationId(locationId, out location);

    public bool TryGetByLocationId(string locationId, out AdventureLocation location)
    {
        if (!string.IsNullOrWhiteSpace(locationId)
            && _byLocationId.TryGetValue(locationId.Trim(), out var value))
        {
            location = value;
            return true;
        }
        location = null!;
        return false;
    }

    public bool TryGetBySnarfblat(int snarfblat, out AdventureLocation location)
    {
        if (_bySnarfblat.TryGetValue(snarfblat, out var value))
        {
            location = value;
            return true;
        }
        location = null!;
        return false;
    }

    public bool TryGetBySnarfblat(string snarfblat, out AdventureLocation location)
    {
        if (int.TryParse(snarfblat, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return TryGetBySnarfblat(value, out location);
        location = null!;
        return false;
    }

    public AdventureLocation? Find(string? locationId)
        => TryGetByLocationId(locationId ?? string.Empty, out var location) ? location : null;
    public AdventureLocation? FindByLocationId(string? locationId) => Find(locationId);
    public AdventureLocation? FindBySnarfblat(int snarfblat)
        => TryGetBySnarfblat(snarfblat, out var location) ? location : null;
    public AdventureLocation? FindBySnarfblat(string snarfblat)
        => TryGetBySnarfblat(snarfblat, out var location) ? location : null;

    public IReadOnlyList<AdventureLocation> FindByZone(string? zone)
        => string.IsNullOrWhiteSpace(zone)
            ? Array.Empty<AdventureLocation>()
            : Locations
                .Where(location => string.Equals(location.Zone, zone.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToArray();

    public IReadOnlyList<AdventureLocation> GetByZone(string zone) => FindByZone(zone);

    public AdventureLocation Get(string locationId)
        => TryGetByLocationId(locationId, out var location)
            ? location
            : throw new KeyNotFoundException($"Adventure location '{locationId}' is not in the catalog.");
    public AdventureLocation GetByLocationId(string locationId) => Get(locationId);
    public AdventureLocation Resolve(string locationId) => Get(locationId);
    public AdventureLocation GetBySnarfblat(int snarfblat)
        => TryGetBySnarfblat(snarfblat, out var location)
            ? location
            : throw new KeyNotFoundException($"Adventure snarfblat '{snarfblat}' is not in the catalog.");

    public AdventureLocation GetBySnarfblat(string snarfblat)
        => TryGetBySnarfblat(snarfblat, out var location)
            ? location
            : throw new KeyNotFoundException($"Adventure snarfblat '{snarfblat}' is not in the catalog.");

    /// <summary>Adds or replaces a location by stable location id.</summary>
    public AdventureLocationCatalog Add(AdventureLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        var value = location.Normalize();
        if (string.IsNullOrWhiteSpace(value.LocationId))
            throw new ArgumentException("Location id is required.", nameof(location));
        if (value.Snarfblat <= 0)
            throw new ArgumentOutOfRangeException(nameof(location), "Snarfblat must be positive.");

        if (_byLocationId.TryGetValue(value.LocationId, out var previous))
            _bySnarfblat.Remove(previous.Snarfblat);
        if (_bySnarfblat.TryGetValue(value.Snarfblat, out var other)
            && !string.Equals(other.LocationId, value.LocationId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Snarfblat '{value.Snarfblat}' is already assigned to '{other.LocationId}'.",
                nameof(location));
        }

        _byLocationId[value.LocationId] = value;
        _bySnarfblat[value.Snarfblat] = value;
        return this;
    }

    public AdventureLocationCatalog AddLocation(AdventureLocation location) => Add(location);
    public AdventureLocationCatalog Register(AdventureLocation location) => Add(location);

    public AdventureLocationCatalog WithLocation(AdventureLocation location)
    {
        var copy = new AdventureLocationCatalog(Locations);
        copy.Add(location);
        return copy;
    }

    public static AdventureLocationCatalog Parse(string json) => FromJson(json);

    public static AdventureLocationCatalog FromJson(
        string json,
        JsonSerializerOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("Catalog JSON is required.", nameof(json));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var entries = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().ToArray()
            : ReadLocationArray(root);
        var catalog = new AdventureLocationCatalog();
        foreach (var entry in entries)
            catalog.Add(ParseLocation(entry));
        return catalog;
    }

    public static AdventureLocationCatalog LoadJson(
        string path,
        JsonSerializerOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Catalog path is required.", nameof(path));
        return FromJson(File.ReadAllText(path), options);
    }

    public static AdventureLocationCatalog Load(
        string path,
        JsonSerializerOptions? options = null)
        => LoadJson(path, options);

    public string ToJson(JsonSerializerOptions? options = null)
    {
        var settings = options is null
            ? new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }
            : new JsonSerializerOptions(options);
        return JsonSerializer.Serialize(new CatalogDocument(Locations.ToArray()), settings);
    }

    public override string ToString() => $"{nameof(AdventureLocationCatalog)} ({Count} locations)";

    private static IReadOnlyList<JsonElement> ReadLocationArray(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Adventure location catalog must be an array or object.");
        foreach (var name in new[] { "locations", "entries", "catalog" })
        {
            if (TryGetProperty(root, name, out var value)
                && value.ValueKind == JsonValueKind.Array)
                return value.EnumerateArray().ToArray();
        }
        throw new JsonException("Adventure location catalog has no locations array.");
    }

    private static AdventureLocation ParseLocation(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new JsonException("An adventure location must be a JSON object.");
        var locationId = ReadString(element, "locationId", "id", "adventureId");
        var snarfblat = ReadInt(element, "snarfblat", "snarfblatId", "locationNumber");
        var zone = ReadString(element, "zone", "zoneId", "area");
        var turnCost = ReadInt(element, "turnCost", "turns", "adventureTurns");
        if (turnCost == 0) turnCost = 1;
        var unlockState = ReadUnlockState(element);

        var compactRequirements = new List<string>();
        var structuredRequirements = new List<AdventureLocationRequirement>();
        var encounterMetadata = new List<AdventureEncounterMetadata>();
        if (TryGetProperty(element, "requirements", out var requirements)
            || TryGetProperty(element, "requirementContracts", out requirements))
        {
            if (requirements.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in requirements.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var text = item.GetString();
                        if (!string.IsNullOrWhiteSpace(text)) compactRequirements.Add(text!);
                    }
                    else if (item.ValueKind == JsonValueKind.Object)
                    {
                        var requirement = ParseRequirement(item);
                        if (requirement is not null)
                        {
                            structuredRequirements.Add(requirement);
                            compactRequirements.Add(requirement.Text);
                        }
                    }
                }
            }
            else if (requirements.ValueKind == JsonValueKind.String)
            {
                var text = requirements.GetString();
                if (!string.IsNullOrWhiteSpace(text)) compactRequirements.Add(text!);
            }
        }

        // `requirements` is the compact string form while
        // `requirementContracts` carries typed conditions. They may both be
        // present in the same serialized catalog, so parse the typed form
        // independently instead of letting the compact property short-circuit it.
        if (TryGetProperty(element, "requirementContracts", out var typedRequirements)
            && typedRequirements.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in typedRequirements.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var requirement = ParseRequirement(item);
                if (requirement is null) continue;
                structuredRequirements.Add(requirement);
                compactRequirements.Add(requirement.Text);
            }
        }
        foreach (var propertyName in new[] { "encounters", "encounterMetadata" })
        {
            if (!TryGetProperty(element, propertyName, out var encounters)
                || encounters.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var item in encounters.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var encounterId = ReadString(item, "encounterId", "id", "name");
                if (string.IsNullOrWhiteSpace(encounterId)) continue;
                var kindText = ReadOptionalString(item, "kind", "type");
                _ = Enum.TryParse<AdventureEncounterKind>(kindText, true, out var kind);
                encounterMetadata.Add(new AdventureEncounterMetadata
                {
                    EncounterId = encounterId,
                    Kind = kind,
                    Name = ReadOptionalString(item, "name", "label"),
                    MonsterId = ReadOptionalString(item, "monsterId", "monster"),
                    ChoiceId = ReadOptionalString(item, "choiceId", "choice")
                });
            }
            break;
        }
        return new AdventureLocation
        {
            LocationId = locationId,
            Snarfblat = snarfblat,
            Zone = zone,
            Requirements = compactRequirements,
            RequirementContracts = structuredRequirements,
            TurnCost = turnCost,
            EncounterMetadata = encounterMetadata,
            UnlockState = unlockState
        };
    }

    private static AdventureLocationUnlockState ReadUnlockState(JsonElement element)
    {
        var text = ReadOptionalString(element, "unlockState", "unlock", "state");
        if (!string.IsNullOrWhiteSpace(text)
            && Enum.TryParse<AdventureLocationUnlockState>(text, true, out var state))
            return state;
        if (TryGetProperty(element, "unlocked", out var unlocked))
        {
            if (unlocked.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return unlocked.GetBoolean()
                    ? AdventureLocationUnlockState.Unlocked
                    : AdventureLocationUnlockState.Locked;
            if (unlocked.ValueKind == JsonValueKind.String
                && bool.TryParse(unlocked.GetString(), out var value))
                return value
                    ? AdventureLocationUnlockState.Unlocked
                    : AdventureLocationUnlockState.Locked;
        }
        return AdventureLocationUnlockState.Unknown;
    }

    private static AdventureLocationRequirement? ParseRequirement(JsonElement element)
    {
        var key = ReadString(element, "key", "id", "name", "requirement");
        if (string.IsNullOrWhiteSpace(key)) return null;
        var value = ReadOptionalString(element, "value", "equals", "target");
        var kindText = ReadOptionalString(element, "kind", "type");
        _ = Enum.TryParse<AdventureLocationRequirementKind>(kindText, true, out var kind);
        var minimum = ReadOptionalInt(element, "minimum", "min", "level");
        var optional = ReadOptionalBool(element, "optional");
        return new AdventureLocationRequirement(key, value, kind, minimum, optional);
    }

    private static string ReadString(JsonElement element, params string[] names)
        => ReadOptionalString(element, names) ?? string.Empty;

    private static string? ReadOptionalString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String) return value.GetString();
            if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                return value.ToString();
        }
        return null;
    }

    private static int ReadInt(JsonElement element, params string[] names)
        => ReadOptionalInt(element, names) ?? 0;

    private static int? ReadOptionalInt(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return number;
            if (value.ValueKind == JsonValueKind.String
                && int.TryParse(value.GetString(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out number))
                return number;
        }
        return null;
    }

    private static bool ReadOptionalBool(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value)) continue;
            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return value.GetBoolean();
            if (value.ValueKind == JsonValueKind.String
                && bool.TryParse(value.GetString(), out var parsed))
                return parsed;
        }
        return false;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value)) return true;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private sealed record CatalogDocument(IReadOnlyList<AdventureLocation> Locations);
}

/// <summary>Short catalog name for hosts that do not include Adventure.</summary>
public sealed class LocationCatalog : AdventureLocationCatalog
{
    public static new LocationCatalog Empty => new();

    public LocationCatalog()
    {
    }

    public LocationCatalog(IEnumerable<AdventureLocation>? locations)
        : base(locations)
    {
    }

    public static new LocationCatalog FromJson(
        string json,
        JsonSerializerOptions? options = null)
        => new(AdventureLocationCatalog.FromJson(json, options).Locations);

    public static new LocationCatalog Parse(string json) => FromJson(json);

    public static new LocationCatalog LoadJson(
        string path,
        JsonSerializerOptions? options = null)
        => FromJson(File.ReadAllText(path), options);

    public static new LocationCatalog Load(
        string path,
        JsonSerializerOptions? options = null)
        => LoadJson(path, options);
}

/// <summary>
/// Typed start request that carries a catalog entry alongside the existing
/// <see cref="AdventureRunRequest"/>. It is additive and has no HTTP behavior.
/// </summary>
public sealed record AdventureLocationRunRequest
{
    public string RunId { get; init; } = string.Empty;
    public AccountRole Role { get; init; } = AccountRole.Main;
    public AdventureLocation Location { get; init; } = new();
    public AdventureDailyLimitContract DailyLimit { get; init; } = new();
    public AdventureCounterContract Counters { get; init; } = new();

    [JsonIgnore]
    public string LocationId => Location?.LocationId ?? string.Empty;
    [JsonIgnore]
    public int Snarfblat => Location?.Snarfblat ?? 0;
    [JsonIgnore]
    public string Zone => Location?.Zone ?? string.Empty;
    [JsonIgnore]
    public int TurnCost => Location?.TurnCost ?? 0;
    [JsonIgnore]
    public bool FitsDailyLimit
        => DailyLimit.Normalize().CanStart(Math.Max(1, TurnCost), 1);

    public AdventureRunRequest ToAdventureRunRequest()
    {
        var location = (Location ?? new AdventureLocation()).Normalize();
        return new AdventureRunRequest
        {
            RunId = RunId,
            AdventureId = location.LocationId,
            Location = location,
            Role = Role,
            DailyLimit = DailyLimit,
            Counters = Counters
        };
    }

    public AdventureRunRequest ToRequest() => ToAdventureRunRequest();

    public AdventureLocationRunRequest Normalize()
        => this with
        {
            RunId = RunId.Trim(),
            Location = (Location ?? new AdventureLocation()).Normalize(),
            DailyLimit = (DailyLimit ?? new AdventureDailyLimitContract()).Normalize(),
            Counters = (Counters ?? new AdventureCounterContract()).Normalize()
        };
}
