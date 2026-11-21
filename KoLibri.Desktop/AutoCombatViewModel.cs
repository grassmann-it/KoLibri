using System.Collections.ObjectModel;
using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;

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
    private readonly KoLibri.Core.Spiel.KoLmafiaCatalog? _catalog;
    private CombatTarget? _selectedTarget;
    private CombatOption? _selectedSkill;
    private CombatOption? _selectedItem;
    private int _abortHpPercent = 30;
    private int _maxRounds = 20;

    public AutoCombatViewModel(KoLibri.Core.Spiel.KoLmafiaCatalog? catalog = null)
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
    /// Builds the deterministic policy from the current selection. The
    /// skills come first in the order the operator picked them, the items
    /// follow, and attacking is the fallback.
    /// </summary>
    public KoLibri.Core.Spiel.AutoCombatPolicy BuildPolicy()
        => BuildPolicyCore(
            SelectedSkills.Select(s => s.Id).ToArray(),
            SelectedItems.Select(i => i.Id).ToArray());

    /// <summary>The accounts the operator wants to fight on.</summary>
    public ObservableCollection<AccountRole> Roles { get; } = new()
    {
        AccountRole.Main,
        AccountRole.Mallbot
    };

    /// <summary>
    /// The policy builder lives here rather than in the core so the
    /// desktop does not depend on the core's internal assembly layout for
    /// something this small.
    /// </summary>
    private static KoLibri.Core.Spiel.AutoCombatPolicy BuildPolicyCore(
        IReadOnlyList<string> skillIds,
        IReadOnlyList<string> itemIds)
    {
        var rules = new List<KoLibri.Core.Spiel.AutoCombatRule>();
        foreach (var skill in skillIds.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            rules.Add(new KoLibri.Core.Spiel.AutoCombatRule
            {
                When = new KoLibri.Core.Spiel.AutoCombatPredicate(),
                Then = new KoLibri.Core.Spiel.AutoCombatAction
                {
                    Kind = KoLibri.Core.Spiel.AutoCombatActionKind.Skill,
                    Id = skill.Trim(),
                    Quantity = 1
                },
                MaxUses = 1
            });
        }
        foreach (var item in itemIds.Where(i => !string.IsNullOrWhiteSpace(i)))
        {
            rules.Add(new KoLibri.Core.Spiel.AutoCombatRule
            {
                When = new KoLibri.Core.Spiel.AutoCombatPredicate(),
                Then = new KoLibri.Core.Spiel.AutoCombatAction
                {
                    Kind = KoLibri.Core.Spiel.AutoCombatActionKind.Item,
                    Id = item.Trim(),
                    Quantity = 1
                },
                MaxUses = 1
            });
        }
        return new KoLibri.Core.Spiel.AutoCombatPolicy
        {
            Rules = rules.ToArray(),
            DefaultAction = new KoLibri.Core.Spiel.AutoCombatAction
            {
                Kind = KoLibri.Core.Spiel.AutoCombatActionKind.Attack,
                Quantity = 1
            }
        };
    }

    /// <summary>
    /// Runs the fight on every selected account. Each account gets its own
    /// gateway and its own run; one account failing never stops the
    /// others.
    /// </summary>
    public async Task<IReadOnlyList<CombatRunOutcome>> RunAsync(
        string encounterId,
        string? adventureId,
        string? approvalToken,
        CancellationToken cancellationToken = default)
    {
        var outcomes = new List<CombatRunOutcome>();
        if (!HasConfiguration)
        {
            outcomes.Add(new CombatRunOutcome
            {
                Role = AccountRole.Main,
                Outcome = "not-configured",
                Rationale = "Pick a target and at least one skill first."
            });
            return outcomes;
        }
        if (string.IsNullOrWhiteSpace(approvalToken))
        {
            outcomes.Add(new CombatRunOutcome
            {
                Role = AccountRole.Main,
                Outcome = "no-token",
                Rationale = "Live fights need an approval token."
            });
            return outcomes;
        }
        var policy = BuildPolicy();
        foreach (var role in Roles)
        {
            try
            {
                var gateway = await CreateCombatGatewayAsync(role, approvalToken, cancellationToken)
                    ;
                var runner = new KoLibri.Core.Spiel.AutoCombatRunner(gateway);
                var result = await runner.RunAsync(
                    policy,
                    combatId: encounterId,
                    adventureId: adventureId,
                    accountId: role.ToString(),
                    maxRounds: MaxRounds,
                    abortBelowHpPercent: AbortHpPercent,
                    approvalToken: approvalToken,
                    cancellationToken: cancellationToken);
                outcomes.Add(new CombatRunOutcome
                {
                    Role = role,
                    Outcome = result.Outcome,
                    Rationale = result.Rationale,
                    Rounds = result.Rounds,
                    RoundLog = result.RoundLog
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                outcomes.Add(new CombatRunOutcome
                {
                    Role = role,
                    Outcome = "failed",
                    Rationale = exception.Message
                });
            }
        }
        return outcomes;
    }

    private static async Task<KoLibri.Core.Spiel.ManualCombatGateway>
        CreateCombatGatewayAsync(AccountRole role, string approvalToken, CancellationToken cancellationToken)
    {
        var accounts = AccountSessionManager.CreateDefault();
        await accounts.InitializeAsync(cancellationToken);
        var session = await accounts.GetSessionAsync(role, cancellationToken: cancellationToken)
            ;
        if (session is null || string.IsNullOrWhiteSpace(session.Credentials.CookieHeader))
            throw new InvalidOperationException($"No valid session for {role}.");
        var http = KoLibri.Core.Basis.KoLLoginClient.CreateAuthenticatedClient(
            session.Credentials, new Uri("https://www.kingdomofloathing.com/"));
        var transport = new KoLibri.Core.Spiel.LiveManualCombatTransport(http);
        var approval = new KoLibri.Core.Spiel.DelegateManualCombatApprovalHook(
            (context, _) => ValueTask.FromResult(
                KoLibri.Core.Spiel.ManualCombatApprovalDecision.Allow(approvalToken)));
        return new KoLibri.Core.Spiel.ManualCombatGateway(
            transport,
            policy: null,
            approval: approval);
    }
}

/// <summary>What one account's fight did.</summary>
public sealed record CombatRunOutcome
{
    public required AccountRole Role { get; init; }
    public required string Outcome { get; init; }
    public required string Rationale { get; init; }
    public int Rounds { get; init; }
    public IReadOnlyList<KoLibri.Core.Spiel.AutoCombatRound> RoundLog { get; init; }
        = Array.Empty<KoLibri.Core.Spiel.AutoCombatRound>();
}
