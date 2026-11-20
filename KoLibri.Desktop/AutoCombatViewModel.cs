using System.Collections.ObjectModel;
using KolArbitrage.Core.Accounts;
using KolArbitrage.Core.Data;

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
    private readonly KolArbitrage.Core.Data.KoLmafiaCatalog? _catalog;
    private CombatTarget? _selectedTarget;
    private CombatOption? _selectedSkill;
    private CombatOption? _selectedItem;
    private int _abortHpPercent = 30;
    private int _maxRounds = 20;

    public AutoCombatViewModel(KolArbitrage.Core.Data.KoLmafiaCatalog? catalog = null)
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
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .Take(300))
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
                .Where(s => !string.IsNullOrWhiteSpace(s.Name))
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
            foreach (var item in _catalog.Items
                .Where(i => i.AccessFlags.Contains("q", StringComparer.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(i.Name))
                .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .Take(400))
            {
                Items.Add(new CombatOption
                {
                    Id = item.Id ?? string.Empty,
                    Name = item.Name ?? string.Empty,
                    Kind = "item",
                    Detail = item.Name ?? string.Empty
                });
            }
            LoadState = Targets.Count == 0 ? DesktopLoadState.Empty : DesktopLoadState.Ready;
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
}
