using System.Collections.ObjectModel;
using KolArbitrage.Core.Spiel;

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

    public AdventureStepSpec BuildStepSpec(EncounterBrowserActionRow action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var monster = _currentEncounter?.Combat?.MonsterId ?? (_currentEncounter is null ? _selectedMonster?.MonsterId : null);
        var round = _currentEncounter?.FightRound;
        return action.Kind switch
        {
            "attack" => new AttackStepSpec(),
            "skill" => new SkillStepSpec(action.Id, MonsterId: monster, Round: round),
            "item" => new ItemStepSpec(action.Id, MonsterId: monster, Round: round),
            "run" => new RunStepSpec(),
            "choice" => ParseChoiceRow(action.Id),
            _ => throw new ArgumentException("Unknown adventure action.", nameof(action))
        };

        ChoiceStepSpec ParseChoiceRow(string id)
        {
            var separator = id.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
                throw new ArgumentException("The choice option is missing its choice ID.", nameof(action));
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

    private AdventureEncounterState? _currentEncounter;

    public void ShowEncounter(AdventureEncounterState? encounter)
    {
        _currentEncounter = encounter;
        RebuildActions();
    }

    private void RebuildActions()
    {
        Actions.Clear();
        if (_currentEncounter is { Kind: AdventureEncounterKind.Choice, ChoiceId: { } choice } encounter)
        {
            foreach (var option in encounter.AvailableChoices)
                Actions.Add(new EncounterBrowserActionRow("choice", $"{choice}:{option}",
                    encounter.ChoiceLabels.GetValueOrDefault(option, $"Option {option}"), ""));
            return;
        }
        if (_currentEncounter?.Combat is { } combat)
        {
            if (_currentEncounter.Kind != AdventureEncounterKind.Fight) return;
            var enabledActions = combat.AvailableActions.Where(action => action.IsEnabled)
                .DistinctBy(action => (action.Kind,
                    action.Kind is CombatActionKind.Attack or CombatActionKind.Run ? null : action.Id));
            foreach (var action in enabledActions)
            {
                var kind = action.Kind switch
                {
                    CombatActionKind.Attack => "attack",
                    CombatActionKind.Run => "run",
                    CombatActionKind.Skill => "skill",
                    CombatActionKind.Item => "item",
                    _ => null
                };
                if (kind is not null)
                    Actions.Add(new EncounterBrowserActionRow(kind, action.Id ?? kind, action.Name ?? kind,
                        action.MpCost is { } mp ? $"{mp} MP" : string.Empty));
            }
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
