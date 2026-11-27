using System.Collections.ObjectModel;
using System.Globalization;

namespace KoLibri.Desktop;

/// <summary>
/// Read/write session state for the manual adventure browser, on KoLibri's <see cref="Adventurer"/>
/// (AdventureAsync / FightAsync / FightToEndAsync / ChooseAsync / RunAsync). Each row in
/// <see cref="StepLog"/> is one start, step, recheck or batch. The approval token is sourced from an
/// <see cref="ApprovalTokenStore"/> so it survives UI restarts; nothing is sent without one.
/// </summary>
public sealed class EncounterBrowserSessionViewModel : DesktopViewModel
{
    private readonly Func<AccountRole, CancellationToken, Task<KolibriSession?>> _openSession;
    private readonly ApprovalTokenStore _tokenStore;
    private readonly AccountRole _role;
    private string? _runId;
    private AdventureLocation? _runLocation;
    private AdventureLocation? _batchLocation;
    private IReadOnlyList<StopCondition> _batchConditions = Array.Empty<StopCondition>();
    private string? _batchRules;
    private int _batchRemaining;
    private bool _requiresRecheck;
    private string? _lastMessage;
    private string _approvalToken = string.Empty;
    private string? _lastEncounterSummary;
    private Encounter? _currentEncounter;
    private EncounterBrowserViewModel? _browser;
    private int _generation;

    public EncounterBrowserSessionViewModel(DesktopAccounts accounts, ApprovalTokenStore tokenStore, AccountRole role)
        : this(AccountsOpener(accounts), tokenStore, role)
    {
    }

    /// <param name="openSession">The session of a role, null when it is signed out (see <see cref="DesktopAccounts.TryOpenAsync"/>).</param>
    public EncounterBrowserSessionViewModel(
        Func<AccountRole, CancellationToken, Task<KolibriSession?>> openSession,
        ApprovalTokenStore tokenStore, AccountRole role)
    {
        _openSession = openSession ?? throw new ArgumentNullException(nameof(openSession));
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _role = role;
        StepLog = new ObservableCollection<ManualStepLogEntry>();
    }

    private static Func<AccountRole, CancellationToken, Task<KolibriSession?>> AccountsOpener(DesktopAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        return accounts.TryOpenAsync;
    }

    /// <summary>The cached session of the role; signed out is an error with a sign-in hint (never a request).</summary>
    private async Task<KolibriSession> RequireSessionAsync(CancellationToken cancellationToken)
        => await _openSession(_role, cancellationToken).ConfigureAwait(true)
           ?? throw new InvalidOperationException($"{_role} is not signed in. Sign in under Settings → Accounts.");

    public ObservableCollection<ManualStepLogEntry> StepLog { get; }

    private AdventureDecisionController? _decisions;

    /// <summary>The open choice or fight with its option buttons and the choice policy.</summary>
    public AdventureDecisionController Decisions => _decisions ??= new AdventureDecisionController(
        new LiveOpenChoicePort(RequireSessionAsync), () => ChoiceResolverFactory())
    { ActiveRunSubmit = SubmitDecisionToRunAsync };

    /// <summary>
    /// Answers the open choice of the active run as a run step, so RunId and the encounter stay in step
    /// with KoL. Null when no run owns this choice.
    /// </summary>
    private async Task<string?> SubmitDecisionToRunAsync(string choiceId, int option, CancellationToken cancellationToken)
    {
        if (RunId is null || _browser is null || _currentEncounter is not ChoiceEncounter current
            || current.ChoiceId.ToString(CultureInfo.InvariantCulture) != choiceId)
            return null;
        var outcome = await SubmitStepAsync(new ChoiceStep(current.ChoiceId, option), _browser, cancellationToken);
        var message = LastMessage ?? "No result.";
        return outcome is { Status: AdventureStepStatus.Open or AdventureStepStatus.Complete }
            ? $"Chose option {option}. {message}"
            : $"Not sent: {message}";
    }

    /// <summary>The report of the last bounded batch (turns, drops, stop reason).</summary>
    public BoundedRunReport? LastReport { get; private set; }

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

    /// <summary>The open fight or choice of the active run (null when none is open or a recheck is needed).</summary>
    public Encounter? CurrentEncounter => _currentEncounter;

    public async ValueTask PersistTokenAsync(string label, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ApprovalToken))
            throw new InvalidOperationException("An approval token is required before persisting.");
        await _tokenStore.SaveAsync(new ApprovalToken(ApprovalToken, DateTimeOffset.UtcNow, label), cancellationToken);
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

    public async ValueTask<AdventureStepOutcome?> StartAdventureAsync(
        AdventureLocation location,
        EncounterBrowserViewModel browser,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(browser);
        _browser = browser;
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
        if (location.Snarfblat <= 0)
        {
            LastMessage = "This location requires a special route.";
            return null;
        }

        var session = await RequireSessionAsync(cancellationToken);
        // Only locations with a known access rule cost a read here; the others are left to KoL.
        var access = await session.Adventure.GetLocationAccessAsync(location.Snarfblat, cancellationToken);
        if (!access.Accessible)
        {
            LastMessage = access.Reason ?? $"{location.DisplayName} is not accessible.";
            return null;
        }
        var runId = $"manual-{Guid.NewGuid():N}";
        var encounter = await session.Adventure.AdventureAsync(location.Snarfblat, cancellationToken);
        var outcome = FromEncounter(encounter, runId) ;
        _runLocation = location;
        _requiresRecheck = outcome.RequiresRefresh;
        RunId = outcome.Status == AdventureStepStatus.Open || outcome.RequiresRefresh ? runId : null;
        LastMessage = outcome.IsAborted
            ? $"Adventure start stopped: {AdventureFailureMapper.Describe(outcome.Message ?? outcome.StopReason).Text}{outcome.SavedPageNote}"
            : $"Started '{location.DisplayName}' as {runId}.";
        AppendLog("start", outcome);
        RefreshEncounterSummary(outcome);
        browser.ShowEncounter(outcome.Encounter);
        OnPropertyChanged(nameof(IsSessionActive));
        await RefreshCompletedStatusAsync(session, outcome, cancellationToken);
        return outcome;
    }

    public async ValueTask<AdventureStepOutcome?> SubmitStepAsync(
        AdventureStep step,
        EncounterBrowserViewModel browser,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(step);
        _browser = browser;
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

        if (_requiresRecheck)
            return await RecheckAsync(browser, cancellationToken);

        var session = await RequireSessionAsync(cancellationToken);
        var outcome = step switch
        {
            FightStep fight => await FightStepAsync(session, fight, cancellationToken),
            ChoiceStep choice => await ChoiceStepAsync(session, choice, cancellationToken),
            _ => throw new ArgumentException("Unknown adventure step.", nameof(step))
        };
        AppendLog($"step:{step.GetType().Name}", outcome);
        if (outcome.Status == AdventureStepStatus.NotSent)
        {
            LastMessage = outcome.Message;
            return outcome;
        }
        ApplyStepOutcome(outcome, browser);
        if (outcome.IsComplete)
            LastMessage = $"Adventure complete ({outcome.StopReason}).";
        else if (outcome.IsAborted)
            LastMessage = $"Step aborted: {AdventureFailureMapper.Describe(outcome.Message ?? outcome.StopReason).Text}{outcome.SavedPageNote}";
        else
            LastMessage = $"Step submitted. Next: {AdventureStepOutcome.KindOf(outcome.Encounter)}.";
        await RefreshCompletedStatusAsync(session, outcome, cancellationToken);
        return outcome;
    }

    /// <summary>
    /// Fights the open fight with the plan (<paramref name="rules"/>: AutoCombatPolicy rule text) through
    /// KoLibri's FightToEndAsync, which reads HP from a fresh status when the fight page shows none.
    /// Pauses below <paramref name="minimumHpPercent"/> (or with unknown HP), after <paramref name="maxRounds"/>
    /// actions, or when the plan has no action.
    /// </summary>
    public async ValueTask<AdventureStepOutcome?> FightCurrentAsync(
        EncounterBrowserViewModel browser, CombatTarget target, string rules, int maxRounds,
        int minimumHpPercent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        _browser = browser;
        if (RunId is null || _requiresRecheck)
        {
            LastMessage = "Start an adventure and resolve any recheck before using the fight plan.";
            return null;
        }
        if (string.IsNullOrWhiteSpace(ApprovalToken))
        {
            LastMessage = "Approval token missing; reload or re-enter it.";
            return null;
        }
        if (_currentEncounter is not FightEncounter fight)
        {
            LastMessage = "The selected account has no open fight.";
            return null;
        }
        if (!(fight.MonsterId is { } id
                ? string.Equals(id.ToString(CultureInfo.InvariantCulture), target.Id, StringComparison.Ordinal)
                : string.Equals(fight.MonsterName, target.Name, StringComparison.OrdinalIgnoreCase)))
        {
            LastMessage = $"Fight paused. Expected {target.Name}, found {fight.MonsterName}.";
            return null;
        }
        DesktopCombatStrategy strategy;
        try { strategy = new DesktopCombatStrategy(DesktopCombatStrategy.FromRules(rules), minimumHpPercent, Math.Max(1, maxRounds)); }
        catch (ArgumentException exception)
        {
            LastMessage = exception.Message;
            return null;
        }

        var session = await RequireSessionAsync(cancellationToken);
        var result = await session.Adventure.FightToEndAsync(strategy, cancellationToken);
        var last = result.Rounds.Count > 0 ? result.Rounds[^1] : null;
        var outcome = await AfterFightAsync(session, fight, result.Outcome, last, result.Items, cancellationToken);
        AppendLog("fight", outcome);
        ApplyStepOutcome(outcome, browser);
        LastMessage = result.Outcome switch
        {
            FightOutcome.Stopped => strategy.StopReason switch
            {
                BoundedAdventureRunStopReason.HpBelowMinimum => "Fight paused because HP is unknown or below the configured minimum.",
                BoundedAdventureRunStopReason.FightRoundLimitReached => $"Fight paused after {maxRounds} actions.",
                BoundedAdventureRunStopReason.CombatPolicyStopped => "Fight paused for manual action.",
                _ => $"Fight paused after {result.Rounds.Count} actions (KoLibri's round limit)."
            },
            _ when outcome.IsAborted => $"Step aborted: {AdventureFailureMapper.Describe(outcome.Message ?? outcome.StopReason).Text}{outcome.SavedPageNote}",
            _ when outcome.IsComplete => $"Adventure complete ({outcome.StopReason}).",
            _ => $"Step submitted. Next: {AdventureStepOutcome.KindOf(outcome.Encounter)}."
        };
        await RefreshCompletedStatusAsync(session, outcome, cancellationToken);
        return outcome;
    }

    /// <summary>
    /// Runs (or, with a null location, continues) a batch of turns through KoLibri's RunAsync. In fights
    /// the plan (<paramref name="combatRules"/>, AutoCombatPolicy rule text) picks each action; without one
    /// the batch attacks. A choice without a saved answer, a defeat, the HP threshold, an error or an
    /// unclear page stops the batch and shows the encounter.
    /// </summary>
    public async ValueTask<AdventureStepOutcome?> RunTurnsAsync(
        EncounterBrowserViewModel browser, AdventureLocation? location, int maxTurns, string? itemId,
        CancellationToken cancellationToken = default, int maxFightRounds = 0, int minimumHpPercent = 0,
        string? combatRules = null)
    {
        _browser = browser;
        if (RunId is not null)
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
            if (!string.IsNullOrWhiteSpace(itemId) && !(int.TryParse(itemId, out var stopItem) && stopItem > 0))
            {
                LastMessage = "Enter a positive item ID or leave it blank.";
                return null;
            }
            try { DesktopCombatStrategy.FromRules(combatRules); }
            catch (ArgumentException exception)
            {
                LastMessage = exception.Message;
                return null;
            }
            _batchLocation = location;
            _batchRemaining = maxTurns;
            _batchConditions = string.IsNullOrWhiteSpace(itemId)
                ? Array.Empty<StopCondition>()
                : new[] { StopCondition.ItemCountAtLeast(int.Parse(itemId, CultureInfo.InvariantCulture), 1) };
            _batchRules = combatRules;
        }
        if (_batchLocation is not { } batchLocation || _batchRemaining <= 0 || string.IsNullOrWhiteSpace(ApprovalToken))
        {
            LastMessage = "There are no approved turns left to run.";
            return null;
        }

        var session = await RequireSessionAsync(cancellationToken);
        var access = await session.Adventure.GetLocationAccessAsync(batchLocation.Snarfblat, cancellationToken);
        if (!access.Accessible)
        {
            LastMessage = access.Reason ?? $"{batchLocation.DisplayName} is not accessible.";
            return null;
        }
        var generation = _generation;
        var strategy = new DesktopCombatStrategy(DesktopCombatStrategy.FromRules(_batchRules), minimumHpPercent);
        var result = await session.Adventure.RunAsync(new RunRequest(batchLocation.Snarfblat, _batchRemaining)
        {
            Combat = strategy,
            Choices = ChoiceResolverFactory(),
            StopWhen = _batchConditions,
            MaxRoundsPerFight = maxFightRounds > 0 ? maxFightRounds : 30
        }, cancellationToken);
        // "End run" while the batch was in flight: do not resurrect the ended session.
        if (generation != _generation) return null;
        if (result.StopReason == RunStopReason.Cancelled) cancellationToken.ThrowIfCancellationRequested();

        var stop = MapStop(result, strategy);
        var used = result.StopReason == RunStopReason.TurnsDone ? _batchRemaining : Math.Clamp(result.TurnsSpent, 0, _batchRemaining);
        _batchRemaining -= used;
        var drops = result.Items.Select(item => new AdventureDrop { ItemName = item.Name, Quantity = item.Quantity });
        LastReport = BoundedRunReport.From(used, _batchRemaining, stop, drops, result.Detail);
        if (stop == BoundedAdventureRunStopReason.ConditionsSatisfied) _batchRemaining = 0;
        LastMessage = LastReport.Summary;
        string? savedPage = null;
        foreach (var unknown in result.Encounters.OfType<BlockedEncounter>().Where(b => b.Reason == BlockReason.UnknownPage))
            savedPage = SavePage(unknown) ?? savedPage;
        if (savedPage is not null) LastMessage += $" The page was saved (redacted) to {savedPage}.";

        var open = result.Open is FightEncounter or ChoiceEncounter ? result.Open : null;
        var runId = open is null ? null : $"manual-{Guid.NewGuid():N}";
        var outcome = new AdventureStepOutcome(
            open is not null ? AdventureStepStatus.Open
                : stop is BoundedAdventureRunStopReason.Complete or BoundedAdventureRunStopReason.ConditionsSatisfied
                    ? AdventureStepStatus.Complete : AdventureStepStatus.Aborted,
            open ?? result.Encounters.LastOrDefault(), result.Detail)
        {
            RunId = runId,
            Drops = result.Items,
            StopReason = stop.ToString(),
            SavedPagePath = savedPage
        };
        RunId = runId;
        _runLocation = batchLocation;
        _requiresRecheck = false;
        AppendLog("batch", outcome);
        RefreshEncounterSummary(outcome);
        browser.ShowEncounter(outcome.Encounter);
        if (result.StopReason == RunStopReason.NeedsChoice && open is ChoiceEncounter needs && runId is not null)
            Decisions.Show(NeedsDecisionInfo.From(needs, runId, batchLocation.LocationId, result.Detail, _role));
        OnPropertyChanged(nameof(IsSessionActive));
        await RefreshCompletedStatusAsync(session, outcome, cancellationToken);
        return outcome;
    }

    public async ValueTask<AdventureStepOutcome?> RecheckAsync(
        EncounterBrowserViewModel browser, CancellationToken cancellationToken = default)
    {
        _browser = browser;
        if (RunId is null)
        {
            LastMessage = "There is no live adventure to recheck.";
            return null;
        }

        if (!_requiresRecheck)
        {
            LastMessage = "The current encounter is ready for your action.";
            return null;
        }

        var session = await RequireSessionAsync(cancellationToken);
        var open = await session.Adventure.GetOpenEncounterAsync(cancellationToken);
        var outcome = open is FightEncounter or ChoiceEncounter
            ? new AdventureStepOutcome(AdventureStepStatus.Open, open, null) { RunId = RunId, StopReason = "Open" }
            : new AdventureStepOutcome(AdventureStepStatus.Complete, null, "No fight or choice is open any more.")
                { RunId = RunId, StopReason = "Complete" };
        _requiresRecheck = false;
        AppendLog("recheck", outcome);
        if (outcome.IsComplete)
        {
            RunId = null;
            OnPropertyChanged(nameof(IsSessionActive));
        }

        LastMessage = outcome.Message ?? $"Adventure recheck: {outcome.Status}.";
        RefreshEncounterSummary(outcome);
        browser.ShowEncounter(outcome.Encounter);
        await RefreshCompletedStatusAsync(session, outcome, cancellationToken);
        return outcome;
    }

    public void EndSession()
    {
        _generation++;
        _batchLocation = null;
        _batchRules = null;
        _batchRemaining = 0;
        _requiresRecheck = false;
        _currentEncounter = null;
        RunId = null;
        LastMessage = "Session ended. Audit trail retained in the journal.";
        OnPropertyChanged(nameof(IsSessionActive));
    }

    /// <summary>Loads the user's choice policy; replaced in tests.</summary>
    public Func<IChoiceResolver> ChoiceResolverFactory { get; set; } = DesktopChoicePolicy.Load;

    // ------------------------------------------------------------------ steps

    private async Task<AdventureStepOutcome> FightStepAsync(KolibriSession session, FightStep step, CancellationToken cancellationToken)
    {
        if (_currentEncounter is not FightEncounter fight)
            return NotSent("No fight is open in this run; nothing was sent.");
        var round = await session.Adventure.FightAsync(step.Action, cancellationToken);
        return await AfterFightAsync(session, fight, round.Outcome, round, round.Items, cancellationToken);
    }

    private async Task<AdventureStepOutcome> ChoiceStepAsync(KolibriSession session, ChoiceStep step, CancellationToken cancellationToken)
    {
        if (_currentEncounter is not ChoiceEncounter choice || choice.ChoiceId != step.ChoiceId)
            return NotSent($"Choice {step.ChoiceId} is not the open choice of this run; nothing was sent.");
        if (choice.Options.All(o => o.Option != step.Option))
            return NotSent($"Option {step.Option} is not offered; nothing was sent.");
        Encounter next;
        try { next = await session.Adventure.ChooseAsync(step.ChoiceId, step.Option, cancellationToken); }
        catch (KolibriException exception) when (exception is not SessionExpiredException)
        {
            // KoLibri says "Nothing was sent" when its check before the POST refused; otherwise KoL's answer is unclear.
            return exception.Message.Contains("Nothing was sent", StringComparison.OrdinalIgnoreCase)
                ? NotSent(exception.Message)
                : new AdventureStepOutcome(AdventureStepStatus.Aborted, choice, exception.Message)
                    { RunId = RunId, RequiresRefresh = true, StopReason = "GatewayStopped" };
        }
        return FromEncounter(next, RunId);
    }

    /// <summary>What a fight left: still open, over (won/lost/fled, maybe followed by a choice), or unreadable.</summary>
    private async Task<AdventureStepOutcome> AfterFightAsync(
        KolibriSession session, FightEncounter fight, FightOutcome result, FightRound? last,
        IReadOnlyList<InventoryItem> items, CancellationToken cancellationToken)
    {
        switch (result)
        {
            case FightOutcome.Ongoing:
            case FightOutcome.Stopped:
                var open = last is null ? fight : fight with { Page = last.Page, Round = last.Round };
                return new AdventureStepOutcome(AdventureStepStatus.Open, open, null)
                    { RunId = RunId, Round = last, Drops = items, StopReason = result.ToString() };
            case FightOutcome.Won:
            case FightOutcome.RanAway:
                if (last?.Page.Kind is PageKind.Choice or PageKind.Fight
                    && await session.Adventure.GetOpenEncounterAsync(cancellationToken) is { } follow
                    && follow is ChoiceEncounter or FightEncounter)
                    return new AdventureStepOutcome(AdventureStepStatus.Open, follow, null)
                        { RunId = RunId, Round = last, Drops = items, StopReason = result.ToString() };
                return new AdventureStepOutcome(AdventureStepStatus.Complete, null, null)
                    { RunId = RunId, Round = last, Drops = items, StopReason = result.ToString() };
            case FightOutcome.Lost:
                return new AdventureStepOutcome(AdventureStepStatus.Complete, null, $"Lost the fight against {fight.MonsterName}.")
                    { RunId = RunId, Round = last, Drops = items, StopReason = "Defeat" };
            default:
                var unreadable = last is null ? null : new BlockedEncounter(last.Page, fight.Location, "Unreadable fight page", BlockReason.UnknownPage);
                return new AdventureStepOutcome(AdventureStepStatus.Aborted, unreadable,
                    "The fight ended in a state KoLibri could not read; press Recheck encounter.")
                {
                    RunId = RunId, Round = last, RequiresRefresh = true, StopReason = "UnknownFight",
                    SavedPagePath = unreadable is null ? null : SavePage(unreadable, "UnknownFight")
                };
        }
    }

    private AdventureStepOutcome FromEncounter(Encounter encounter, string? runId) => encounter switch
    {
        FightEncounter or ChoiceEncounter => new AdventureStepOutcome(AdventureStepStatus.Open, encounter, null)
            { RunId = runId, StopReason = "Open" },
        NonCombatEncounter reward => new AdventureStepOutcome(AdventureStepStatus.Complete, reward, null)
            { RunId = runId, Drops = reward.Items, StopReason = "Complete" },
        BlockedEncounter { Reason: BlockReason.UnknownPage } unknown => new AdventureStepOutcome(AdventureStepStatus.Aborted, unknown,
            unknown.Title is { Length: > 0 } title ? $"KoL showed a page KoLibri could not read ({title})." : "KoL showed a page KoLibri could not read.")
            { RunId = runId, RequiresRefresh = true, StopReason = "UnknownEncounter", SavedPagePath = SavePage(unknown) },
        BlockedEncounter blocked => new AdventureStepOutcome(AdventureStepStatus.Aborted, blocked, BlockedText(blocked))
            { RunId = runId, StopReason = "Blocked:" + blocked.Reason },
        _ => new AdventureStepOutcome(AdventureStepStatus.Aborted, encounter, encounter.Title) { RunId = runId, StopReason = "Unknown" }
    };

    private static string BlockedText(BlockedEncounter blocked) => blocked.Reason switch
    {
        BlockReason.NoAdventures => "You are out of adventures.",
        BlockReason.Beaten => "You are too beaten up to adventure. Rest or heal first.",
        BlockReason.LocationLocked => $"This location is locked{(blocked.Title is { Length: > 0 } t ? $" ({t})" : "")}.",
        _ => blocked.Title ?? blocked.Reason.ToString()
    };

    private AdventureStepOutcome NotSent(string message)
        => new(AdventureStepStatus.NotSent, _currentEncounter, message) { RunId = RunId, StopReason = "NotSent" };

    private void ApplyStepOutcome(AdventureStepOutcome outcome, EncounterBrowserViewModel browser)
    {
        _requiresRecheck = outcome.RequiresRefresh;
        RefreshEncounterSummary(outcome);
        browser.ShowEncounter(outcome.Encounter);
        if (outcome.IsComplete || (outcome.IsAborted && !outcome.RequiresRefresh))
        {
            RunId = null;
            OnPropertyChanged(nameof(IsSessionActive));
        }
    }

    private static BoundedAdventureRunStopReason MapStop(RunResult result, DesktopCombatStrategy strategy) => result.StopReason switch
    {
        RunStopReason.TurnsDone => BoundedAdventureRunStopReason.Complete,
        RunStopReason.Condition => BoundedAdventureRunStopReason.ConditionsSatisfied,
        RunStopReason.NeedsChoice => BoundedAdventureRunStopReason.NeedsDecision,
        RunStopReason.NeedsCombat => strategy.StopReason ?? BoundedAdventureRunStopReason.FightRoundLimitReached,
        RunStopReason.Cancelled => BoundedAdventureRunStopReason.DeadlineReached,
        _ => BoundedAdventureRunStopReason.GatewayStopped
    };

    private static string? SavePage(BlockedEncounter page, string? reason = null)
        => AdventureUnknownPageArchive.TrySave(reason ?? (page.Title == "Unreadable fight page" ? "UnknownFight" : "UnknownEncounter"),
            page.Page.Html, DateTimeOffset.UtcNow);

    private async Task RefreshCompletedStatusAsync(
        KolibriSession session, AdventureStepOutcome outcome, CancellationToken cancellationToken)
    {
        if (!outcome.IsComplete) return;
        try
        {
            var status = await session.Character.GetStatusAsync(refresh: true, cancellationToken);
            var parts = new List<string> { $"{status.Adventures} adventures left" };
            if (status.MaxHp > 0) parts.Add($"HP {status.Hp}/{status.MaxHp}");
            if (status.MaxMp > 0) parts.Add($"MP {status.Mp}/{status.MaxMp}");
            LastEncounterSummary = $"{LastEncounterSummary} · {string.Join(" · ", parts)}";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LastEncounterSummary = $"{LastEncounterSummary} · Account status unavailable";
        }
    }

    private void AppendLog(string kind, AdventureStepOutcome outcome)
    {
        StepLog.Insert(0, new ManualStepLogEntry(
            DateTimeOffset.UtcNow,
            kind,
            outcome.Status.ToString(),
            outcome.StopReason,
            outcome.RunId,
            outcome.Message ?? AdventureStepOutcome.KindOf(outcome.Encounter)));
    }

    private void RefreshEncounterSummary(AdventureStepOutcome outcome)
    {
        _currentEncounter = outcome.Status == AdventureStepStatus.Open ? outcome.Encounter : null;
        var encounter = outcome.Encounter;
        Decisions.Show(_currentEncounter);
        var parts = new List<string>();
        if (encounter is null)
            parts.Add(outcome.IsComplete ? "Complete" : "(no encounter)");
        else
        {
            var fight = encounter as FightEncounter;
            parts.Add($"{AdventureStepOutcome.KindOf(encounter)}: {fight?.MonsterName ?? encounter.Title ?? _runLocation?.DisplayName}");
            if (fight is not null) parts.Add($"Round {fight.Round}");
        }
        if (outcome.Round is { } round)
        {
            if (round.Hp > 0) parts.Add($"HP {round.Hp}");
            if (round.Mp > 0) parts.Add($"MP {round.Mp}");
            if (round.DamageDealt > 0) parts.Add($"Damage dealt {round.DamageDealt}");
        }
        if (outcome.Drops.Count > 0)
            parts.Add($"Loot: {string.Join(", ", outcome.Drops.Select(drop => $"{drop.Quantity} × {drop.Name}"))}");
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
/// Lightweight UI projection of a <see cref="MafiaAdventureRecord"/>: the location id, snarfblat and turn cost.
/// </summary>
public sealed record AdventureLocation(
    string LocationId,
    string DisplayName,
    string Zone,
    int Snarfblat,
    int TurnCost);

/// <summary>Adapts the role's live session to <see cref="IOpenChoicePort"/>.</summary>
internal sealed class LiveOpenChoicePort(Func<CancellationToken, Task<KolibriSession>> open) : IOpenChoicePort
{
    public async Task<Encounter?> DetectAsync(CancellationToken cancellationToken)
    {
        var session = await open(cancellationToken).ConfigureAwait(false);
        return await session.Adventure.GetOpenEncounterAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Encounter> SubmitAsync(int choiceId, int option, CancellationToken cancellationToken)
    {
        var session = await open(cancellationToken).ConfigureAwait(false);
        return await session.Adventure.ChooseAsync(choiceId, option, cancellationToken).ConfigureAwait(false);
    }
}
