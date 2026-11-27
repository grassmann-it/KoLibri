using System.Collections.ObjectModel;

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
            SelectedMonster = null;
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
    {
        var adventure = _catalog.Adventures.FirstOrDefault(a => a.Id == _selectedLocation?.LocationId);
        if (adventure is null)
            return Array.Empty<EncounterBrowserMonsterRow>();

        var names = adventure.MonsterNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _catalog.Monsters.Where(m => names.Contains(m.Name)).Select(BuildMonsterRow).ToArray();
    }

    /// <summary>The step a row of the Actions list stands for. Skills and items need the numeric id the fight page shows.</summary>
    public AdventureStep BuildStepSpec(EncounterBrowserActionRow action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action.Kind switch
        {
            "attack" => new FightStep(CombatAction.Attack),
            "skill" => new FightStep(CombatAction.Skill(NumericId(action, "skill"))),
            "item" => new FightStep(CombatAction.Item(NumericId(action, "item"))),
            "run" => new FightStep(CombatAction.RunAway),
            "choice" => ParseChoiceRow(action.Id),
            _ => throw new ArgumentException("Unknown adventure action.", nameof(action))
        };

        ChoiceStep ParseChoiceRow(string id)
        {
            var separator = id.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
                throw new ArgumentException("The choice option is missing its choice ID.", nameof(action));
            if (!int.TryParse(id[..separator], out var choice) || !int.TryParse(id[(separator + 1)..], out var option) || option <= 0)
                throw new ArgumentException("The choice option is not numeric.", nameof(action));
            return new ChoiceStep(choice, option);
        }

        static int NumericId(EncounterBrowserActionRow row, string kind)
            => int.TryParse(row.Id, out var id) && id > 0
                ? id
                : throw new ArgumentException($"The {kind} '{row.DisplayName}' has no numeric id; pick it from the open fight's action list.", nameof(action));
    }

    private void ApplyFilter()
    {
        Locations.Clear();
        foreach (var row in BuildLocations(_catalog).Where(r => MatchesFilter(r, _filterText)))
            Locations.Add(row);
    }

    /// <summary>
    /// Every word of the filter must appear in the name, zone, description, snarfblat or id
    /// (case-insensitive), so "smut orc", "logging camp" and "295" all find The Smut Orc Logging Camp.
    /// </summary>
    public static bool MatchesFilter(EncounterBrowserLocationRow row, string? filter)
    {
        var words = (filter ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.All(word =>
            row.DisplayName.Contains(word, StringComparison.OrdinalIgnoreCase)
            || row.Zone.Contains(word, StringComparison.OrdinalIgnoreCase)
            || row.LocationId.Contains(word, StringComparison.OrdinalIgnoreCase)
            || (row.Snarfblat > 0 && row.Snarfblat.ToString() == word.TrimStart('#'))
            || (row.Description?.Contains(word, StringComparison.OrdinalIgnoreCase) ?? false));
    }

    private void RebuildMonsters()
    {
        Monsters.Clear();
        foreach (var row in MonstersForSelectedLocation())
            Monsters.Add(row);
    }

    private Encounter? _currentEncounter;

    public void ShowEncounter(Encounter? encounter)
    {
        _currentEncounter = encounter;
        RebuildActions();
    }

    private void RebuildActions()
    {
        Actions.Clear();
        if (_currentEncounter is ChoiceEncounter choice)
        {
            foreach (var option in choice.Options)
                Actions.Add(new EncounterBrowserActionRow("choice", $"{choice.ChoiceId}:{option.Option}",
                    string.IsNullOrWhiteSpace(option.Text) ? $"Option {option.Option}" : option.Text, ""));
            return;
        }
        if (_currentEncounter is FightEncounter fight)
        {
            // The actions the open fight page offers: attack, its skill and item dropdowns, run away.
            Actions.Add(new EncounterBrowserActionRow("attack", "attack", "Attack", string.Empty));
            foreach (var action in FightPageActions.Parse(fight.Page.Html))
                Actions.Add(new EncounterBrowserActionRow(action.Kind == CombatActionKind.Skill ? "skill" : "item",
                    action.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), action.Name,
                    action.MpCost is { } mp ? $"{mp} MP" : string.Empty));
            Actions.Add(new EncounterBrowserActionRow("run", "run", "Run away", string.Empty));
            return;
        }
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
            var encounterCount = adventure.MonsterNames.Count;
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
}
