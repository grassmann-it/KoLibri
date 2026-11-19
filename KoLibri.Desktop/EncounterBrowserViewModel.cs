using System.Collections.ObjectModel;
using KolArbitrage.Core.Adventure;
using KolArbitrage.Core.Data;

namespace KoLibri.Desktop;

/// <summary>One row in the location list of the manual adventure browser.</summary>
public sealed record EncounterBrowserLocationRow(
    string LocationId,
    string DisplayName,
    string Zone,
    int Snarfblat,
    int TurnCost,
    int EncounterCount,
    int MonsterCount,
    string? Description = null);

/// <summary>One row in the monster list of the selected location.</summary>
public sealed record EncounterBrowserMonsterRow(
    string MonsterId,
    string Name,
    string Phylum,
    int Hp,
    int Attack,
    int Defense,
    IReadOnlyList<string> Elements,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> Items);

/// <summary>One row in the skill/item/action list of the selected monster.</summary>
public sealed record EncounterBrowserActionRow(
    string Kind,
    string Id,
    string DisplayName,
    string Detail);

/// <summary>
/// Read-only browser surface for the KoLmafia catalog. The VM never talks
/// to the gateway itself; the operator selects rows and
/// <see cref="EncounterBrowserSessionViewModel"/> submits the resulting
/// step through the bounded bridge.
/// </summary>
public sealed class EncounterBrowserViewModel : DesktopViewModel
{
    private readonly KoLmafiaCatalog _catalog;
    private EncounterBrowserLocationRow? _selectedLocation;
    private EncounterBrowserMonsterRow? _selectedMonster;
    private string _filterText = string.Empty;

    public EncounterBrowserViewModel(KoLmafiaCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        Locations = new ObservableCollection<EncounterBrowserLocationRow>(BuildLocations(catalog));
        Monsters = new ObservableCollection<EncounterBrowserMonsterRow>();
        Actions = new ObservableCollection<EncounterBrowserActionRow>();
        ZoneGroups = new ObservableCollection<string>(catalog.Zones.Select(z => z.Name));
    }

    public ObservableCollection<EncounterBrowserLocationRow> Locations { get; }
    public ObservableCollection<EncounterBrowserMonsterRow> Monsters { get; }
    public ObservableCollection<EncounterBrowserActionRow> Actions { get; }
    public ObservableCollection<string> ZoneGroups { get; }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (!SetProperty(ref _filterText, value ?? string.Empty)) return;
            ApplyFilter();
        }
    }

    public EncounterBrowserLocationRow? SelectedLocation
    {
        get => _selectedLocation;
        set
        {
            if (!SetProperty(ref _selectedLocation, value)) return;
            RebuildMonsters();
            RebuildActions();
            OnPropertyChanged(nameof(SelectedLocationDisplay));
        }
    }

    public EncounterBrowserMonsterRow? SelectedMonster
    {
        get => _selectedMonster;
        set
        {
            if (!SetProperty(ref _selectedMonster, value)) return;
            RebuildActions();
            OnPropertyChanged(nameof(SelectedMonsterDisplay));
        }
    }

    public string? SelectedLocationDisplay => _selectedLocation?.DisplayName;
    public string? SelectedMonsterDisplay => _selectedMonster?.Name;

    public IReadOnlyList<EncounterBrowserMonsterRow> MonstersForSelectedLocation()
        => _selectedLocation is null
            ? Array.Empty<EncounterBrowserMonsterRow>()
            : _catalog.Monsters
                .Where(m => MonsterBelongsToLocation(m, _selectedLocation.LocationId))
                .Select(BuildMonsterRow)
                .ToArray();

    public AdventureStepSpec BuildStepSpec(EncounterBrowserActionRow action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var location = _selectedLocation?.LocationId ?? "?";
        var monster = _selectedMonster?.MonsterId;
        return action.Kind switch
        {
            "attack" => new AttackStepSpec(),
            "skill" => new SkillStepSpec(action.Id, MonsterId: monster),
            "item" => new ItemStepSpec(action.Id, MonsterId: monster),
            "run" => new RunStepSpec(),
            "choice" => ParseChoiceRow(action.Id),
            _ => new AttackStepSpec()
        };

        ChoiceStepSpec ParseChoiceRow(string id)
        {
            var separator = id.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
                return new ChoiceStepSpec(action.Id, "1");
            return new ChoiceStepSpec(id[..separator], id[(separator + 1)..]);
        }
    }

    private void ApplyFilter()
    {
        var filter = _filterText.Trim();
        Locations.Clear();
        foreach (var row in BuildLocations(_catalog)
                     .Where(r => string.IsNullOrEmpty(filter)
                                 || r.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                 || r.Zone.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                 || (r.Description?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)))
        {
            Locations.Add(row);
        }
    }

    private void RebuildMonsters()
    {
        Monsters.Clear();
        foreach (var row in MonstersForSelectedLocation())
            Monsters.Add(row);
    }

    private void RebuildActions()
    {
        Actions.Clear();
        if (_selectedMonster is null)
        {
            Actions.Add(new EncounterBrowserActionRow("attack", "attack", "Attack", "Default melee attack."));
            return;
        }
        Actions.Add(new EncounterBrowserActionRow("attack", "attack", "Attack", "Default melee attack."));
        foreach (var skillId in _selectedMonster.Skills)
            Actions.Add(new EncounterBrowserActionRow("skill", skillId, skillId, $"Use skill '{skillId}'."));
        foreach (var itemId in _selectedMonster.Items)
            Actions.Add(new EncounterBrowserActionRow("item", itemId, itemId, $"Use item '{itemId}'."));
        Actions.Add(new EncounterBrowserActionRow("run", "run", "Run away", "Flee this encounter."));
    }

    private static IEnumerable<EncounterBrowserLocationRow> BuildLocations(KoLmafiaCatalog catalog)
    {
        var byZone = catalog.Zones.ToDictionary(z => z.Id, z => z.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var adventure in catalog.Adventures)
        {
            var zoneName = byZone.TryGetValue(adventure.Zone, out var name) ? name : adventure.Zone;
            var encounterCount = catalog.Monsters.Count(m => MonsterBelongsToZone(m, adventure.Zone));
            yield return new EncounterBrowserLocationRow(
                adventure.Id,
                adventure.Name,
                zoneName,
                adventure.Snarfblat,
                adventure.TurnCost,
                encounterCount,
                encounterCount,
                Description: adventure.Desc);
        }
    }

    private static EncounterBrowserMonsterRow BuildMonsterRow(MafiaMonsterRecord monster)
        => new(
            monster.Id,
            monster.Name,
            monster.Phylum,
            monster.Hp,
            monster.Attack,
            monster.Defense,
            monster.Elements,
            monster.Skills,
            monster.Items);

    private static bool MonsterBelongsToZone(MafiaMonsterRecord monster, string zoneId)
    {
        // Heuristic: a monster belongs to a zone when its id starts with the
        // zone id (or vice versa). This keeps the catalog stable until the
        // Mafia data files expose the explicit monster → zone map.
        if (string.IsNullOrEmpty(zoneId)) return false;
        var a = monster.Id.ToLowerInvariant();
        var b = zoneId.ToLowerInvariant();
        return a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal);
    }

    private static bool MonsterBelongsToLocation(MafiaMonsterRecord monster, string locationId)
    {
        if (string.IsNullOrEmpty(locationId)) return false;
        var a = monster.Id.ToLowerInvariant();
        var b = locationId.ToLowerInvariant();
        if (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal)
            || a.Contains(b, StringComparison.Ordinal))
            return true;
        // Fallback: the real Mafia catalog does not embed a monster→location
        // table. As a soft heuristic, if the monster name shares a token with
        // the location id we treat it as a candidate. This is intentionally
        // permissive so the operator can still see a populated monster list.
        foreach (var segment in b.Split('-', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Length >= 4 && a.Contains(segment, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
