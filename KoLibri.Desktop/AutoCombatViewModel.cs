using System.Collections.ObjectModel;

namespace KoLibri.Desktop;

/// <summary>One skill or item the auto-combat engine may use.</summary>
public sealed record CombatOption
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required string Detail { get; init; }
}

/// <summary>One fight target from the catalog.</summary>
public sealed record CombatTarget
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Zone { get; init; }
    public required string Detail { get; init; }
}

/// <summary>
/// The auto-combat configurator: pick a target, pick the skills and items,
/// set the abort rules and see what the engine would do.
///
/// KoLmafia needs a dozen clicks to set up the same thing; here it is one
/// panel with a preview. The preview is an estimate — the effect and
/// modifier tracker is not complete yet — so the panel says so rather
/// than pretending to be exact.
/// </summary>
public sealed class AutoCombatViewModel : DesktopViewModel
{
    private readonly KoLmafiaCatalog? _catalog;
    private CombatTarget? _selectedTarget;
    private CombatOption? _selectedSkill;
    private CombatOption? _selectedItem;
    private int _abortHpPercent = 30;
    private int _maxRounds = 20;
    private int _healBelowHpPercent;
    private string _healStepsText = "rest";
    private string _upkeepEffectText = string.Empty;
    private int _upkeepMinTurns = 5;
    private string _upkeepStepText = string.Empty;
    private bool _stopOnMonster;
    private string _stopMonsterName = string.Empty;
    private bool _stopOnTurtle;
    private bool _useChoiceDefaults = true;
    private string _choiceOverridesText = string.Empty;

    public AutoCombatViewModel(KoLmafiaCatalog? catalog = null)
    {
        _catalog = catalog;
        Targets = new ObservableCollection<CombatTarget>();
        Skills = new ObservableCollection<CombatOption>();
        Items = new ObservableCollection<CombatOption>();
        SelectedSkills = new ObservableCollection<CombatOption>();
        SelectedItems = new ObservableCollection<CombatOption>();
    }

    public ObservableCollection<CombatTarget> Targets { get; }
    public ObservableCollection<CombatOption> Skills { get; }
    public ObservableCollection<CombatOption> Items { get; }

    /// <summary>The skills the engine will use, in order.</summary>
    public ObservableCollection<CombatOption> SelectedSkills { get; }

    /// <summary>The items the engine may use.</summary>
    public ObservableCollection<CombatOption> SelectedItems { get; }

    public CombatTarget? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (!SetProperty(ref _selectedTarget, value)) return;
            OnPropertyChanged(nameof(PreviewText));
            OnPropertyChanged(nameof(FarmSummary));
            OnPropertyChanged(nameof(FarmError));
            OnPropertyChanged(nameof(HasConfiguration));
        }
    }

    public CombatOption? SelectedSkill
    {
        get => _selectedSkill;
        set
        {
            if (!SetProperty(ref _selectedSkill, value)) return;
            if (value is null) return;
            if (SelectedSkills.Contains(value)) return;
            SelectedSkills.Add(value);
            OnPropertyChanged(nameof(PreviewText));
        }
    }

    public CombatOption? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetProperty(ref _selectedItem, value)) return;
            if (value is null) return;
            if (SelectedItems.Contains(value)) return;
            SelectedItems.Add(value);
            OnPropertyChanged(nameof(PreviewText));
        }
    }

    /// <summary>Abort the fight below this percentage of hit points.</summary>
    public int AbortHpPercent
    {
        get => _abortHpPercent;
        set
        {
            if (!SetProperty(ref _abortHpPercent, Math.Clamp(value, 0, 100))) return;
            OnPropertyChanged(nameof(PreviewText));
        }
    }

    /// <summary>Stop after this many rounds.</summary>
    public int MaxRounds
    {
        get => _maxRounds;
        set
        {
            if (!SetProperty(ref _maxRounds, Math.Clamp(value, 1, 200))) return;
            OnPropertyChanged(nameof(PreviewText));
        }
    }

    // ------------------------------------------------------------ farming between turns

    /// <summary>Heal before a new turn when HP is below this percentage; 0 = off.</summary>
    public int HealBelowHpPercent
    {
        get => _healBelowHpPercent;
        set => SetFarm(ref _healBelowHpPercent, Math.Clamp(value, 0, 100));
    }

    /// <summary>Heal steps in priority order: <c>rest, item:ID|NAME, skill:ID|NAME</c>.</summary>
    public string HealStepsText
    {
        get => _healStepsText;
        set => SetFarm(ref _healStepsText, value ?? string.Empty);
    }

    /// <summary>The effect to keep up (id or name); blank = no upkeep.</summary>
    public string UpkeepEffectText
    {
        get => _upkeepEffectText;
        set => SetFarm(ref _upkeepEffectText, value ?? string.Empty);
    }

    public int UpkeepMinTurns
    {
        get => _upkeepMinTurns;
        set => SetFarm(ref _upkeepMinTurns, Math.Clamp(value, 1, 1000));
    }

    /// <summary>How to renew the effect: one step (<c>item:…</c>, <c>skill:…</c> or <c>rest</c>).</summary>
    public string UpkeepStepText
    {
        get => _upkeepStepText;
        set => SetFarm(ref _upkeepStepText, value ?? string.Empty);
    }

    /// <summary>Opt-in: stop after the turn that showed <see cref="StopMonsterName"/> (or the target, when blank).</summary>
    public bool StopOnMonster
    {
        get => _stopOnMonster;
        set => SetFarm(ref _stopOnMonster, value);
    }

    public string StopMonsterName
    {
        get => _stopMonsterName;
        set => SetFarm(ref _stopMonsterName, value ?? string.Empty);
    }

    /// <summary>Opt-in: stop on Turtle Tamer encounters. Off by default: turtles are clicked through.</summary>
    public bool StopOnTurtle
    {
        get => _stopOnTurtle;
        set => SetFarm(ref _stopOnTurtle, value);
    }

    /// <summary>Answer choices from KoLibri's curated defaults (on by default).</summary>
    public bool UseChoiceDefaults
    {
        get => _useChoiceDefaults;
        set => SetFarm(ref _useChoiceDefaults, value);
    }

    /// <summary>Overrides for the choice defaults: <c>CHOICE:OPTION</c>, comma separated.</summary>
    public string ChoiceOverridesText
    {
        get => _choiceOverridesText;
        set => SetFarm(ref _choiceOverridesText, value ?? string.Empty);
    }

    /// <summary>Why the farm settings cannot be used (null when they are valid).</summary>
    public string? FarmError => TryBuildFarmOptions(out _, out var error) ? null : error;

    /// <summary>One line: what the batch does between turns.</summary>
    public string FarmSummary => TryBuildFarmOptions(out var options, out var error) ? options!.Summary : error!;

    private void SetFarm<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref field, value, propertyName)) return;
        OnPropertyChanged(nameof(FarmSummary));
        OnPropertyChanged(nameof(FarmError));
        OnPropertyChanged(nameof(PreviewText));
    }

    /// <summary>
    /// The farm settings as <see cref="FarmOptions"/>; false with a readable error for bad input
    /// (unknown name, bad step, bad override). Names resolve through the KoLmafia catalog.
    /// </summary>
    public bool TryBuildFarmOptions(out FarmOptions? options, out string? error)
    {
        options = null;
        if (!FarmStepText.TryParseSteps(HealStepsText, ItemIdByName, SkillIdByName, out var heal, out error))
        {
            error = "Heal steps: " + error;
            return false;
        }
        if (HealBelowHpPercent > 0 && heal.Count == 0)
        {
            error = "Heal steps: name at least one step (rest, item:…, skill:…) or set the HP percentage to 0.";
            return false;
        }
        UpkeepRule? upkeep = null;
        if (!string.IsNullOrWhiteSpace(UpkeepEffectText))
        {
            var effect = EffectByText(UpkeepEffectText);
            if (effect is null)
            {
                error = $"Upkeep: unknown effect '{UpkeepEffectText.Trim()}'.";
                return false;
            }
            if (!FarmStepText.TryParseStep(UpkeepStepText, ItemIdByName, SkillIdByName, out var step, out error))
            {
                error = "Upkeep step: " + error;
                return false;
            }
            upkeep = new UpkeepRule(effect.Value.Id, effect.Value.Name, UpkeepMinTurns, step!);
        }
        if (!FarmStepText.TryParseOverrides(ChoiceOverridesText, out var overrides, out error))
        {
            error = "Choice overrides: " + error;
            return false;
        }
        string? monster = null;
        if (StopOnMonster)
        {
            monster = string.IsNullOrWhiteSpace(StopMonsterName) ? SelectedTarget?.Name : StopMonsterName.Trim();
            if (string.IsNullOrWhiteSpace(monster))
            {
                error = "Stop on monster: enter a monster name or pick a target.";
                return false;
            }
        }
        options = new FarmOptions
        {
            HealBelowHpPercent = HealBelowHpPercent,
            HealSteps = heal,
            Upkeep = upkeep,
            StopOnMonster = monster,
            StopOnTurtle = StopOnTurtle,
            UseChoiceDefaults = UseChoiceDefaults,
            ChoiceOverrides = overrides
        };
        return true;
    }

    private int? ItemIdByName(string name)
        => _catalog?.Items.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)) is { } item
           && int.TryParse(item.Id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)
            ? id : null;

    private int? SkillIdByName(string name)
        => _catalog?.Skills.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))?.Id;

    private (int Id, string Name)? EffectByText(string text)
    {
        var trimmed = text.Trim();
        if (int.TryParse(trimmed, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0)
        {
            var known = _catalog?.Effects.FirstOrDefault(e => e.Id == id);
            return (id, known?.Name ?? $"effect {id}");
        }
        return _catalog?.Effects.FirstOrDefault(e => string.Equals(e.Name, trimmed, StringComparison.OrdinalIgnoreCase)) is { } effect
            ? (effect.Id, effect.Name) : null;
    }

    public bool HasConfiguration => SelectedTarget is not null && SelectedSkills.Count > 0;

    /// <summary>What the engine would do with the current configuration.
    /// The numbers are an estimate: the modifier tracker does not follow
    /// every conditional buff yet.</summary>
    public string PreviewText
    {
        get
        {
            var skills = SelectedSkills.Count == 0
                ? "no skill selected"
                : string.Join(" → ", SelectedSkills.Select(s => s.Name));
            var items = SelectedItems.Count == 0
                ? "no items"
                : string.Join(", ", SelectedItems.Select(i => i.Name));
            if (SelectedTarget is null)
            {
                return SelectedSkills.Count == 0 && SelectedItems.Count == 0
                    ? "Pick a target and at least one skill to see the plan."
                    : $"no target · skills: {skills} · items: {items}";
            }
            return $"{SelectedTarget.Name} ({SelectedTarget.Detail}) · "
                + $"{MaxRounds} rounds max · abort below {AbortHpPercent} % HP · "
                + $"skills: {skills} · items: {items} · between turns: {FarmSummary}";
        }
    }

    /// <summary>Loads the targets, skills and items from the catalog.</summary>
    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        LoadState = DesktopLoadState.Loading;
        try
        {
            Targets.Clear();
            Skills.Clear();
            Items.Clear();
            if (_catalog is null)
            {
                LoadState = DesktopLoadState.Empty;
                return Task.CompletedTask;
            }
            foreach (var monster in _catalog.Monsters
                .Where(m => !string.IsNullOrWhiteSpace(m.Name))
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
            {
                Targets.Add(new CombatTarget
                {
                    Id = monster.Id ?? string.Empty,
                    Name = monster.Name ?? string.Empty,
                    Zone = monster.Phylum ?? string.Empty,
                    Detail = $"{monster.Hp} HP · {monster.Attack} atk · {monster.Defense} def"
                });
            }
            foreach (var skill in _catalog.Skills
                .Where(s => !string.IsNullOrWhiteSpace(s.Name) && s.UsableInCombat)
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                Skills.Add(new CombatOption
                {
                    Id = skill.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Name = skill.Name ?? string.Empty,
                    Kind = "skill",
                    Detail = skill.Name ?? string.Empty
                });
            }
            foreach (var item in new ItemClassifier(_catalog).All
                .Where(i => i.IsCombatItem && !string.IsNullOrWhiteSpace(i.Name))
                .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
            {
                Items.Add(new CombatOption
                {
                    Id = item.ItemId,
                    Name = item.Name ?? string.Empty,
                    Kind = "item",
                    Detail = item.Name ?? string.Empty
                });
            }
            LoadState = Targets.Count == 0 ? DesktopLoadState.Empty : DesktopLoadState.Ready;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LoadState = DesktopLoadState.Empty;
            throw;
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            LoadState = DesktopLoadState.Error;
        }
        finally
        {
            OnPropertyChanged(nameof(PreviewText));
            OnPropertyChanged(nameof(HasConfiguration));
        }
        return Task.CompletedTask;
    }

    /// <summary>Removes a skill from the plan.</summary>
    public void RemoveSkill(CombatOption skill)
    {
        if (SelectedSkills.Remove(skill)) OnPropertyChanged(nameof(PreviewText));
    }

    /// <summary>Removes an item from the plan.</summary>
    public void RemoveItem(CombatOption item)
    {
        if (SelectedItems.Remove(item)) OnPropertyChanged(nameof(PreviewText));
    }

    /// <summary>
    /// Builds the deterministic plan from the current selection as rule text for
    /// <see cref="CombatStrategies.FromRules"/>. The skills come first in the order the operator
    /// picked them (each once), the items follow (each once), and attacking is the fallback.
    /// </summary>
    public string BuildPolicy()
        => BuildRules(
            SelectedSkills.Select(s => s.Id).ToArray(),
            SelectedItems.Select(i => i.Id).ToArray());

    /// <summary>
    /// The plan for fights in an adventure batch: the plan's skills and items, then attack. Null
    /// when no skill or item is picked, so the batch keeps its plain attack default.
    /// </summary>
    public string? BuildBatchPolicy()
        => SelectedSkills.Count == 0 && SelectedItems.Count == 0 ? null : BuildPolicy();

    /// <summary>The accounts the operator wants to fight on.</summary>
    public ObservableCollection<AccountRole> Roles { get; } = new()
    {
        AccountRole.Main,
        AccountRole.Mallbot
    };

    /// <summary>
    /// The 0.4 policy (one rule per skill/item with maxUses 1, default attack) in the JSON form of the
    /// AutoCombatPolicy DSL: the text DSL has no per-rule use limit.
    /// </summary>
    internal static string BuildRules(IReadOnlyList<string> skillIds, IReadOnlyList<string> itemIds)
    {
        static object Rule(string kind, string id) => new Dictionary<string, object>
        {
            ["when"] = new Dictionary<string, object>(),
            ["action"] = new Dictionary<string, object> { ["kind"] = kind, ["id"] = id.Trim(), ["quantity"] = 1 },
            ["maxUses"] = 1
        };
        var rules = skillIds.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => Rule("skill", s))
            .Concat(itemIds.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => Rule("item", i)))
            .ToArray();
        return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["rules"] = rules,
            ["defaultAction"] = new Dictionary<string, object> { ["kind"] = "attack", ["quantity"] = 1 }
        });
    }
}
