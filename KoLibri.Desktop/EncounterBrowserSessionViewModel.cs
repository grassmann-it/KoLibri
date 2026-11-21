using LibKolibri;
using System.Collections.ObjectModel;
using KolArbitrage.Core.Adventure;
using KolArbitrage.Core.Accounts;
using KolArbitrage.Core.Data;
using KolArbitrage.Core.Integration;
using KolArbitrage.Core.Security;

namespace KoLibri.Desktop;

/// <summary>
/// Read/write session state for the manual adventure browser. Each row in
/// <see cref="StepLog"/> corresponds to one gateway POST through
/// <see cref="AdventureManualDriver"/>. The approval token is sourced
/// from an <see cref="ApprovalTokenStore"/> so it survives UI restarts.
/// </summary>
public sealed class EncounterBrowserSessionViewModel : DesktopViewModel
{
    private AdventureManualDriver? _driver;
    private readonly Func<AccountRole, IAdventureApprovalHook, IAdventureVerificationHook,
        CancellationToken, Task<ManualAdventureSession>>? _openSession;
    private readonly ApprovalTokenStore _tokenStore;
    private readonly AccountRole _role;
    private AdventureActionAllowlist _allowlist = AdventureActionAllowlist.Empty;
    private string? _runId;
    private AdventureRunRequest? _batchTemplate;
    private IReadOnlyList<AdventureCondition> _batchConditions = Array.Empty<AdventureCondition>();
    private int _batchRemaining;
    private bool _requiresRecheck;
    private string? _lastMessage;
    private string _approvalToken = string.Empty;
    private string? _lastEncounterSummary;
    private AdventureEncounterState? _currentEncounter;

    public EncounterBrowserSessionViewModel(
        AdventureManualDriver driver,
        ApprovalTokenStore tokenStore,
        AccountRole role)
    {
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _role = role;
        StepLog = new ObservableCollection<ManualStepLogEntry>();
    }

    public EncounterBrowserSessionViewModel(
        AccountSessionManager accounts, ApprovalTokenStore tokenStore, AccountRole role)
        : this((selectedRole, approval, verification, token) =>
            ManualAdventureSession.OpenAsync(selectedRole, approval, verification, accounts, token), tokenStore, role)
    {
    }

    public EncounterBrowserSessionViewModel(
        Func<AccountRole, IAdventureApprovalHook, IAdventureVerificationHook,
            CancellationToken, Task<ManualAdventureSession>> openSession,
        ApprovalTokenStore tokenStore, AccountRole role)
    {
        _openSession = openSession ?? throw new ArgumentNullException(nameof(openSession));
        _tokenStore = tokenStore;
        _role = role;
        StepLog = new ObservableCollection<ManualStepLogEntry>();
    }

    private Task<ManualAdventureSession> OpenLiveAsync(CancellationToken cancellationToken)
    {
        var token = ApprovalToken;
        return _openSession!(_role,
            new DelegateAdventureApprovalHook((_, _) =>
                ValueTask.FromResult(AdventureApprovalDecision.Allow(token))),
            new DelegateAdventureVerificationHook((context, _) =>
                ValueTask.FromResult(context.Response.Accepted == false
                    ? AdventureVerificationResult.Failed("KoL rejected the action.")
                    : AdventureVerificationResult.Success())),
            cancellationToken);
    }

    public ObservableCollection<ManualStepLogEntry> StepLog { get; }

    public string? RunId
    {
        get => _runId;
        private set => SetProperty(ref _runId, value);
    }

    public string? LastMessage
    {
        get => _lastMessage;
        private set => SetProperty(ref _lastMessage, value);
    }

    public string? LastEncounterSummary
    {
        get => _lastEncounterSummary;
        private set => SetProperty(ref _lastEncounterSummary, value);
    }

    public string ApprovalToken
    {
        get => _approvalToken;
        set => SetProperty(ref _approvalToken, value ?? string.Empty);
    }

    public bool IsSessionActive => RunId is not null;

    public async ValueTask PersistTokenAsync(string label, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ApprovalToken))
            throw new InvalidOperationException("An approval token is required before persisting.");
        await _tokenStore.SaveAsync(new ApprovalToken(ApprovalToken, DateTimeOffset.UtcNow, label), cancellationToken)
            ;
        LastMessage = $"Approval token persisted to {_tokenStore.FilePath}.";
    }

    public async ValueTask LoadStoredTokenAsync(CancellationToken cancellationToken = default)
    {
        var token = await _tokenStore.LoadAsync(cancellationToken);
        if (token is null)
        {
            LastMessage = "No stored approval token found.";
            return;
        }
        ApprovalToken = token.Token;
        LastMessage = $"Approval token loaded from {_tokenStore.FilePath} (label='{token.Label}').";
    }

    public async ValueTask<ManualStepOutcome?> StartAdventureAsync(
        AdventureLocation location,
        EncounterBrowserViewModel browser,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(browser);
        if (string.IsNullOrWhiteSpace(ApprovalToken))
        {
            LastMessage = "An approval token is required before starting an adventure.";
            return null;
        }
        if (RunId is not null)
        {
            LastMessage = "Finish or end the current adventure before starting another.";
            return null;
        }
        _allowlist = new AdventureActionAllowlist { WaitForManualChoice = true }
            .AddAdventure(location.LocationId)
            .AddFightAction(location.LocationId, "attack");
        var runId = $"manual-{Guid.NewGuid():N}";
        var request = new AdventureRunRequest
        {
            RunId = runId,
            AdventureId = location.LocationId,
            Snarfblat = location.Snarfblat,
            Role = _role
        };
        await using var live = _driver is null ? await OpenLiveAsync(cancellationToken) : null;
        if (live is not null && KoLQuestAccess.HasRule(request.Snarfblat))
        {
            var access = await live.ReadLocationAccessAsync(request.Snarfblat, cancellationToken);
            if (access.Allowed == false)
            {
                LastMessage = access.Reason;
                return null;
            }
        }
        var outcome = live is null
            ? await _driver!.StartAsync(request, _allowlist, cancellationToken)
            : await live.StartAsync(request, _allowlist, cancellationToken);
        _requiresRecheck = outcome.RequiresRefresh;
        RunId = outcome.IsComplete || (outcome.IsAborted && !outcome.RequiresRefresh)
            ? null : outcome.RunId;
        LastMessage = outcome.IsAborted || outcome.RequiresRefresh
            ? $"Adventure start stopped: {outcome.Message ?? outcome.StopReason}"
            : $"Started '{location.DisplayName}' as {outcome.RunId}.";
        AppendLog("start", outcome);
        RefreshEncounterSummary(outcome);
        browser.ShowEncounter(outcome.Encounter);
        OnPropertyChanged(nameof(IsSessionActive));
        await RefreshCompletedStatusAsync(live, outcome, cancellationToken);
        return outcome;
    }

    public async ValueTask<ManualStepOutcome?> SubmitStepAsync(
        AdventureStepSpec step,
        EncounterBrowserViewModel browser,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (RunId is null)
        {
            LastMessage = "Start an adventure before submitting steps.";
            return null;
        }
        if (string.IsNullOrWhiteSpace(ApprovalToken))
        {
            LastMessage = "Approval token missing; reload or re-enter it.";
            return null;
        }

        if (_requiresRecheck && _openSession is not null)
            return await RecheckAsync(browser, cancellationToken);

        await using var live = _driver is null ? await OpenLiveAsync(cancellationToken) : null;
        var outcome = live is null
            ? await _driver!.ExecuteAsync(RunId, _allowlist, step, cancellationToken)
            : await live.ExecuteAsync(RunId, _allowlist, step, cancellationToken);
        _requiresRecheck = outcome.RequiresRefresh;
        AppendLog($"step:{step.GetType().Name}", outcome);
        if (outcome.RequestId is null && outcome.Message is not null && !outcome.IsAborted)
        {
            LastMessage = outcome.Message;
            return outcome;
        }
        RefreshEncounterSummary(outcome);
        browser.ShowEncounter(outcome.Encounter);
        if (outcome.IsComplete || (outcome.IsAborted && !outcome.RequiresRefresh))
        {
            RunId = null;
            OnPropertyChanged(nameof(IsSessionActive));
        }
        if (outcome.IsComplete)
        {
            LastMessage = $"Adventure complete ({outcome.Status}).";
        }
        else if (outcome.IsAborted)
        {
            LastMessage = $"Step aborted: {outcome.Message ?? outcome.StopReason}";
        }
        else
        {
            LastMessage = $"Step submitted. Next: {outcome.NextAction?.GetType().Name ?? "none"}.";
        }
        await RefreshCompletedStatusAsync(live, outcome, cancellationToken);
        return outcome;
    }

    public async ValueTask<ManualStepOutcome?> FightCurrentAsync(
        EncounterBrowserViewModel browser, CombatTarget target, AutoCombatPolicy policy, int maxRounds,
        int minimumHpPercent, CancellationToken cancellationToken = default)
    {
        if (RunId is null || _requiresRecheck)
        {
            LastMessage = "Start an adventure and resolve any recheck before using the fight plan.";
            return null;
        }

        ManualStepOutcome? outcome = null;
        var usage = new AutoCombatUsage();
        for (var index = 0; index < maxRounds; index++)
        {
            var encounter = _currentEncounter;
            if (encounter is not { Kind: AdventureEncounterKind.Fight, Combat: { } combat })
            {
                if (outcome is null) LastMessage = "The selected account has no open fight.";
                return outcome;
            }
            if (!(combat.MonsterId is { } id
                    ? string.Equals(id, target.Id, StringComparison.Ordinal)
                    : string.Equals(combat.MonsterName, target.Name, StringComparison.OrdinalIgnoreCase)))
            {
                LastMessage = $"Fight paused. Expected {target.Name}, found {combat.MonsterName ?? "an unknown monster"}.";
                return outcome;
            }
            if (minimumHpPercent > 0 && (combat.PlayerHp is not { } hp
                || combat.PlayerMaxHp is not > 0 || 100L * hp < (long)minimumHpPercent * combat.PlayerMaxHp.Value))
            {
                LastMessage = "Fight paused because HP is unknown or below the configured minimum.";
                return outcome;
            }
            var decision = AutoCombatAdventureAdapter.Evaluate(policy, combat, encounter.AdventureId, usage: usage);
            if (!decision.Allowed || AutoCombatStepSpecMapper.TryMap(
                    encounter.AdventureId, decision.PolicyDecision.Action) is not { } step)
            {
                LastMessage = decision.BlockReason ?? "Fight paused for manual action.";
                return outcome;
            }

            outcome = await SubmitStepAsync(step, browser, cancellationToken);
            if (outcome is null || outcome.IsComplete || outcome.IsAborted || outcome.RequiresRefresh
                || outcome.RequestId is null) return outcome;

            var ruleUses = usage.RuleUses.ToDictionary(pair => pair.Key, pair => pair.Value);
            if (decision.PolicyDecision.RuleId is { } rule)
                ruleUses[rule] = ruleUses.GetValueOrDefault(rule) + 1;
            var action = decision.PolicyDecision.Action.Kind;
            usage = usage with
            {
                ActionsTaken = usage.ActionsTaken + 1,
                SkillUses = usage.SkillUses + (action == AutoCombatActionKind.Skill ? 1 : 0),
                ItemUses = usage.ItemUses + (action == AutoCombatActionKind.Item ? 1 : 0),
                RunAttempts = usage.RunAttempts + (action == AutoCombatActionKind.Run ? 1 : 0),
                RuleUses = ruleUses
            };
        }
        LastMessage = $"Fight paused after {maxRounds} actions.";
        return outcome;
    }

    public async ValueTask<ManualStepOutcome?> RunTurnsAsync(
        EncounterBrowserViewModel browser, AdventureLocation? location, int maxTurns, string? itemId,
        CancellationToken cancellationToken = default)
    {
        if (_openSession is null || RunId is not null)
        {
            LastMessage = "Resolve the current adventure before running more turns.";
            return null;
        }

        if (location is not null)
        {
            if (location.Snarfblat <= 0 || maxTurns <= 0)
            {
                LastMessage = "Select an ordinary adventure location and a positive turn budget.";
                return null;
            }
            _batchTemplate = new AdventureRunRequest
            {
                AdventureId = location.LocationId, Snarfblat = location.Snarfblat, Role = _role
            };
            _batchRemaining = maxTurns;
            _batchConditions = string.IsNullOrWhiteSpace(itemId)
                ? Array.Empty<AdventureCondition>()
                : new[] { new AdventureCondition { Kind = AdventureConditionKind.ItemCount, Key = itemId, Target = 1 } };
            _allowlist = new AdventureActionAllowlist { WaitForManualChoice = true }
                .AddAdventure(location.LocationId).AddFightAction(location.LocationId, "attack");
        }
        if (_batchTemplate is null || _batchRemaining <= 0 || string.IsNullOrWhiteSpace(ApprovalToken))
        {
            LastMessage = "There are no approved turns left to run.";
            return null;
        }

        await using var live = await OpenLiveAsync(cancellationToken);
        if (KoLQuestAccess.HasRule(_batchTemplate.Snarfblat))
        {
            var access = await live.ReadLocationAccessAsync(_batchTemplate.Snarfblat, cancellationToken);
            if (access.Allowed == false)
            {
                LastMessage = access.Reason;
                return null;
            }
        }
        var batch = await live.RunTurnsAsync(_batchTemplate, _allowlist, _batchRemaining, _batchConditions,
            cancellationToken: cancellationToken);
        _batchRemaining = batch.TurnsRemaining;
        var stopReason = batch.LastRun.StopReason;
        if (stopReason == BoundedAdventureRunStopReason.ConditionsSatisfied ||
            batch.LastRun.Conditions is { Count: > 0 } conditions && conditions.All(condition => condition.IsSatisfied))
            _batchRemaining = 0;
        LastMessage = $"{stopReason} · {_batchRemaining} turns left in this run.";
        if (batch.LastRun.LastStep is not { } last) return null;

        var outcome = AdventureManualDriver.ToOutcome(last, last.Checkpoint.RunId, last.Checkpoint.AdventureId);
        RunId = outcome.IsComplete || (outcome.IsAborted && !outcome.RequiresRefresh) ? null : outcome.RunId;
        _requiresRecheck = outcome.RequiresRefresh;
        AppendLog("batch", outcome);
        RefreshEncounterSummary(outcome);
        browser.ShowEncounter(outcome.Encounter);
        OnPropertyChanged(nameof(IsSessionActive));
        await RefreshCompletedStatusAsync(live, outcome, cancellationToken);
        return outcome;
    }

    public async ValueTask<ManualStepOutcome?> RecheckAsync(
        EncounterBrowserViewModel browser, CancellationToken cancellationToken = default)
    {
        if (RunId is not { } runId || _openSession is null)
        {
            LastMessage = "There is no live adventure to recheck.";
            return null;
        }

        if (!_requiresRecheck)
        {
            LastMessage = "The current encounter is ready for your action.";
            return null;
        }

        await using var live = await OpenLiveAsync(cancellationToken);
        var result = await live.RefreshAsync(runId, _allowlist, cancellationToken);
        var outcome = AdventureManualDriver.ToOutcome(result, runId, result.Checkpoint.AdventureId);
        _requiresRecheck = outcome.RequiresRefresh;
        AppendLog("recheck", outcome);
        if (outcome.IsComplete || (outcome.IsAborted && !outcome.RequiresRefresh))
        {
            RunId = null;
            OnPropertyChanged(nameof(IsSessionActive));
        }

        LastMessage = outcome.Message ?? $"Adventure recheck: {outcome.Status}.";
        RefreshEncounterSummary(outcome);
        browser.ShowEncounter(outcome.Encounter);
        await RefreshCompletedStatusAsync(live, outcome, cancellationToken);
        return outcome;
    }

    private async Task RefreshCompletedStatusAsync(
        ManualAdventureSession? live, ManualStepOutcome outcome, CancellationToken cancellationToken)
    {
        if (live is null || !outcome.IsComplete) return;
        try
        {
            var status = await live.ReadStatusAsync(cancellationToken);
            var parts = new List<string> { $"{status.Adventures} adventures left" };
            if (status.MaximumHp > 0) parts.Add($"HP {status.CurrentHp}/{status.MaximumHp}");
            if (status.MaximumMp > 0) parts.Add($"MP {status.CurrentMp}/{status.MaximumMp}");
            if (status.IsStale) parts.Add("stale");
            LastEncounterSummary = $"{LastEncounterSummary} · {string.Join(" · ", parts)}";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LastEncounterSummary = $"{LastEncounterSummary} · Account status unavailable";
        }
    }

    public void EndSession()
    {
        _batchTemplate = null;
        _batchRemaining = 0;
        _requiresRecheck = false;
        RunId = null;
        LastMessage = "Session ended. Audit trail retained in the journal.";
        OnPropertyChanged(nameof(IsSessionActive));
    }

    /// <summary>
    /// Swap the underlying driver so the next submit uses a fresh gateway
    /// (typically after the operator entered a new approval token).
    /// </summary>
    public void ReplaceDriver(AdventureManualDriver driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        _driver = driver;
    }

    private void AppendLog(string kind, ManualStepOutcome outcome)
    {
        StepLog.Insert(0, new ManualStepLogEntry(
            DateTimeOffset.UtcNow,
            kind,
            outcome.Status.ToString(),
            outcome.StopReason,
            outcome.RequestId,
            outcome.Message ?? outcome.Encounter?.Kind.ToString()));
    }

    private void RefreshEncounterSummary(ManualStepOutcome outcome)
    {
        var encounter = _currentEncounter = outcome.Encounter;
        if (encounter is null)
        {
            LastEncounterSummary = outcome.IsComplete ? "Complete" : "(no encounter)";
            return;
        }
        var combat = encounter.Combat;
        var parts = new List<string> { $"{encounter.Kind}: {combat?.MonsterName ?? encounter.Location}" };
        if (encounter.FightRound is { } round) parts.Add($"Round {round}");
        if (combat?.PlayerHp is { } hp) parts.Add($"HP {hp}/{combat.PlayerMaxHp?.ToString() ?? "?"}");
        if (combat?.PlayerMp is { } mp) parts.Add($"MP {mp}/{combat.PlayerMaxMp?.ToString() ?? "?"}");
        if (combat?.EnemyHp is { } enemyHp) parts.Add($"Monster HP {enemyHp}");
        if (outcome.Drops.Count > 0)
            parts.Add($"Loot: {string.Join(", ", outcome.Drops.Select(drop => $"{drop.Quantity} × {drop.ItemName}"))}");
        LastEncounterSummary = string.Join(" · ", parts);
    }
}

/// <summary>One log row for the manual session, surfaced in the UI log pane.</summary>
public sealed record ManualStepLogEntry(
    DateTimeOffset At,
    string Kind,
    string Status,
    string StopReason,
    string? RequestId,
    string? Detail);

/// <summary>
/// Lightweight UI projection of an <see cref="MafiaAdventureRecord"/>;
/// the driver only needs the location id, snarfblat and turn cost.
/// </summary>
public sealed record AdventureLocation(
    string LocationId,
    string DisplayName,
    string Zone,
    int Snarfblat,
    int TurnCost,
    IReadOnlyList<AdventureEncounterMetadata> Encounters);
