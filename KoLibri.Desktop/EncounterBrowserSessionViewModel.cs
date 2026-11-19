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
    private readonly AdventureManualDriver _driver;
    private readonly ApprovalTokenStore _tokenStore;
    private readonly AccountRole _role;
    private AdventureActionAllowlist _allowlist = AdventureActionAllowlist.Empty;
    private string? _runId;
    private string? _lastMessage;
    private string _approvalToken = string.Empty;
    private string? _lastEncounterSummary;

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
            .ConfigureAwait(false);
        LastMessage = $"Approval token persisted to {_tokenStore.FilePath}.";
    }

    public async ValueTask LoadStoredTokenAsync(CancellationToken cancellationToken = default)
    {
        var token = await _tokenStore.LoadAsync(cancellationToken).ConfigureAwait(false);
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
        _allowlist = new AdventureActionAllowlist()
            .AddAdventure(location.LocationId)
            .AddEncounter(location.LocationId, location.LocationId);
        var runId = $"manual-{Guid.NewGuid():N}";
        var request = new AdventureRunRequest
        {
            RunId = runId,
            AdventureId = location.LocationId,
            Snarfblat = location.Snarfblat,
            Role = _role
        };
        var outcome = await _driver.StartAsync(request, _allowlist, cancellationToken).ConfigureAwait(false);
        RunId = outcome.RunId;
        LastMessage = $"Started '{location.DisplayName}' as {outcome.RunId}.";
        AppendLog("start", outcome);
        RefreshEncounterSummary(outcome);
        OnPropertyChanged(nameof(IsSessionActive));
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

        var outcome = await _driver.ExecuteAsync(RunId, _allowlist, step, cancellationToken).ConfigureAwait(false);
        AppendLog($"step:{step.GetType().Name}", outcome);
        RefreshEncounterSummary(outcome);
        if (outcome.IsComplete)
        {
            LastMessage = $"Adventure complete ({outcome.Status}).";
        }
        else if (outcome.IsAborted)
        {
            LastMessage = $"Step aborted: {outcome.StopReason}";
        }
        else
        {
            LastMessage = $"Step submitted. Next: {outcome.NextAction?.GetType().Name ?? "none"}.";
        }
        return outcome;
    }

    public void EndSession()
    {
        RunId = null;
        LastMessage = "Session ended. Audit trail retained in the journal.";
        OnPropertyChanged(nameof(IsSessionActive));
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
        var encounter = outcome.Encounter;
        if (encounter is null)
        {
            LastEncounterSummary = outcome.IsComplete ? "Complete" : "(no encounter)";
            return;
        }
        LastEncounterSummary = $"{encounter.Kind}: {encounter.Location} "
            + (encounter.FightRound.HasValue ? $"round {encounter.FightRound.Value}" : string.Empty);
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
