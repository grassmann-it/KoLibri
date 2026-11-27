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
                + $"skills: {skills} · items: {items}";
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
