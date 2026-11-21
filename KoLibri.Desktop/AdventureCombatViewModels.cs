using System.Collections.ObjectModel;
using System.Globalization;
using LibKolibri;
using KolArbitrage.Core.Spiel;

namespace KoLibri.Desktop;

/// <summary>
/// State used by the Adventure page.  It is intentionally separate from the
/// Core kernel phase: a page can be loading, stopping or waiting for a stale
/// response while the kernel remains in the same phase.
/// </summary>
public enum AdventureRunUiState
{
    Idle,
    Loading,
    Ready,
    Running,
    Paused,
    Stopping,
    Stopped,
    Complete,
    Blocked,
    Unknown,
    Stale,
    Error,
    Canceled
}

/// <summary>How the page should expose the next combat action.</summary>
public enum AdventureInteractionMode
{
    Manual,
    AutoCombat
}

/// <summary>Read metadata supplied by an account adapter for an adventure plan.</summary>
public sealed record AdventureStateSnapshot
{
    public AdventureState? State { get; init; }
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool IsStale { get; init; }
    public string? Source { get; init; }
    public IReadOnlyList<string> Advisories { get; init; } = Array.Empty<string>();

    public bool IsEmpty => State is null;
    public bool IsFresh => !IsStale;

    public static AdventureStateSnapshot Empty(DateTimeOffset? observedAt = null)
        => new() { ObservedAt = observedAt ?? DateTimeOffset.UtcNow };

    public static AdventureStateSnapshot Fresh(AdventureState state, DateTimeOffset? observedAt = null)
        => new() { State = state, ObservedAt = observedAt ?? DateTimeOffset.UtcNow };
}

/// <summary>Read boundary used by the desktop to build a daily adventure plan.</summary>
public interface IAdventureStateQuery
{
    Task<AdventureStateSnapshot> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>Compatibility name for hosts that call the read boundary a provider.</summary>
public interface IAdventureStateProvider : IAdventureStateQuery
{
}

/// <summary>
/// UI-facing execution boundary.  The concrete Core gateway is wrapped so
/// the ViewModel can be tested without a transport, cookies or a checkpoint
/// store.
/// </summary>
public interface IAdventureExecutionClient
{
    Task<AdventureExecutionResult> StartAsync(
        AdventureRunRequest request,
        AdventureActionAllowlist allowlist,
        CancellationToken cancellationToken = default);

    Task<AdventureExecutionResult> ResumeAsync(
        string runId,
        AdventureActionAllowlist allowlist,
        CancellationToken cancellationToken = default);

    Task<AdventureExecutionResult> ExecutePendingAsync(
        string runId,
        AdventureActionAllowlist allowlist,
        CancellationToken cancellationToken = default);

    Task<AdventureExecutionResult> RefreshAsync(
        string runId,
        AdventureActionAllowlist allowlist,
        CancellationToken cancellationToken = default);
}

/// <summary>Alias retained for hosts that name the boundary a gateway.</summary>
public interface IAdventureExecutionGateway : IAdventureExecutionClient
{
}

/// <summary>Adapter around Core's checkpointed adventure execution gateway.</summary>
public sealed class AdventureExecutionGatewayClient : IAdventureExecutionGateway
{
    private readonly AdventureExecutionGateway _gateway;

    public AdventureExecutionGatewayClient(AdventureExecutionGateway gateway)
        => _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));

    public Task<AdventureExecutionResult> StartAsync(
        AdventureRunRequest request,
        AdventureActionAllowlist allowlist,
        CancellationToken cancellationToken = default)
        => _gateway.StartAsync(request, allowlist, cancellationToken);

    public Task<AdventureExecutionResult> ResumeAsync(
        string runId,
        AdventureActionAllowlist allowlist,
        CancellationToken cancellationToken = default)
        => _gateway.ResumeAsync(runId, allowlist, cancellationToken);

    public Task<AdventureExecutionResult> ExecutePendingAsync(
        string runId,
        AdventureActionAllowlist allowlist,
        CancellationToken cancellationToken = default)
        => _gateway.ExecutePendingAsync(runId, allowlist, cancellationToken);

    public Task<AdventureExecutionResult> RefreshAsync(
        string runId,
        AdventureActionAllowlist allowlist,
        CancellationToken cancellationToken = default)
        => _gateway.RefreshAsync(runId, allowlist, cancellationToken);
}

/// <summary>Snapshot projected for a page card and a shell status badge.</summary>
public sealed record AdventurePlanProjection
{
    public DailyAdventurePlan? Plan { get; init; }
    public AdventureState? State { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
    public bool IsStale { get; init; }
    public bool IsUnknown { get; init; }
    public IReadOnlyList<string> Advisories { get; init; } = Array.Empty<string>();

    public bool IsEmpty => Plan is null;
    public string StatusText => Plan?.Status.ToString() ?? "No snapshot";
    public string FreshnessText => IsStale ? "stale" : IsUnknown ? "unknown" : "fresh";
    public int RemainingTurns => State?.AdventureTurnsRemaining ?? 0;
    public int StartingKnucklebones => Plan?.StartingKnucklebones ?? State?.Knucklebones ?? 0;
    public decimal ProjectedKnucklebones => Plan?.ProjectedKnucklebones ?? StartingKnucklebones;
}

/// <summary>
/// A safe manual action card.  The card is populated from the Core decision;
/// it never invents a fight or choice action when the response is unknown.
/// </summary>
public sealed record AdventureManualActionState
{
    public string? RunId { get; init; }
    public AdventureEncounterKind EncounterKind { get; init; } = AdventureEncounterKind.Unknown;
    public AdventureKernelAction? Action { get; init; }
    public string? ActionId { get; init; }
    public string? ChoiceId { get; init; }
    public string? OptionId { get; init; }
    public int? Round { get; init; }
    public string? MonsterId { get; init; }
    public IReadOnlyList<string> AvailableChoices { get; init; } = Array.Empty<string>();
    public bool RequiresApproval { get; init; }
    public bool IsStale { get; init; }
    public bool IsUnknown { get; init; }
    public bool RequiresRefresh { get; init; }
    public string? BlockReason { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }

    public bool IsAvailable => Action is not null && !IsUnknown && !IsStale && !RequiresRefresh;
    public bool CanExecute => IsAvailable && !RequiresApproval;
    public bool IsBlocked => !CanExecute;
    public string ActionText
        => Action switch
        {
            AdventureFightAction fight => $"Fight · {fight.ActionId} · round {fight.Round.ToString(CultureInfo.InvariantCulture)}",
            AdventureChoiceAction choice => $"Choice · {choice.ChoiceId} → {choice.OptionId}",
            AdventureEncounterAction => "Adventure encounter",
            _ => IsUnknown ? "Unknown action · manual review required" : "No pending action"
        };

    public static AdventureManualActionState Empty(bool stale = false, bool unknown = false, string? reason = null)
        => new() { IsStale = stale, IsUnknown = unknown, BlockReason = reason };
}

/// <summary>Desktop projection of the same bounded, read-only runner contract exposed by CLI/MCP.</summary>
public sealed record BoundedAdventurePreviewState
{
    public BoundedAdventureRunStopReason StopReason { get; init; }
    public IReadOnlyList<AdventureConditionResult> Conditions { get; init; }
        = Array.Empty<AdventureConditionResult>();
    public int MaxGatewaySteps { get; init; }
    public TimeSpan MaxDuration { get; init; }
    public bool IsBlocked => StopReason == BoundedAdventureRunStopReason.ConditionsBlocked;
    public bool IsReady => StopReason == BoundedAdventureRunStopReason.ReadyToRun;
    public string StatusText => StopReason.ToString();
}

/// <summary>
/// Adventure plan/execution projection used by the Avalonia page.  Loading
/// and execution have independent cancellation generations in the base class.
/// </summary>
public class AdventureViewModel : DesktopViewModel
{
    private readonly IAdventureGateway? _planner;
    private readonly IAdventureStateQuery? _stateQuery;
    private readonly IAdventureExecutionClient? _execution;
    private readonly TimeSpan _staleAfter;
    private AdventureActionAllowlist _allowlist;
    private AdventureState? _state;
    private DailyAdventurePlan? _plan;
    private AdventurePlanProjection? _projection;
    private AdventureExecutionResult? _lastExecution;
    private AdventureCheckpoint? _checkpoint;
    private AdventureEncounterState? _encounter;
    private AdventureManualActionState _manualAction = AdventureManualActionState.Empty();
    private AdventureRunUiState _runState = AdventureRunUiState.Idle;
    private AdventureInteractionMode _mode = AdventureInteractionMode.Manual;
    private string _adventureId = string.Empty;
    private string _runId = string.Empty;
    private bool _stopRequested;
    private bool _allowStaleExecution;
    private bool _hasUnknownState;
    private string? _unknownReason;
    private string? _runMessage;
    private BoundedAdventurePreviewState? _boundedPreview;

    public AdventureViewModel(
        IAdventureGateway? planner = null,
        AdventureState? initialState = null,
        IAdventureExecutionClient? execution = null,
        AdventureActionAllowlist? allowlist = null,
        IAdventureStateQuery? stateQuery = null,
        TimeSpan? staleAfter = null)
    {
        _planner = planner;
        _state = initialState;
        _execution = execution;
        _stateQuery = stateQuery;
        _allowlist = allowlist ?? AdventureActionAllowlist.Empty;
        _staleAfter = staleAfter ?? TimeSpan.FromMinutes(20);
        if (_staleAfter <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(staleAfter));
        Diagnostics = new ObservableCollection<string>();
    }

    /// <summary>Convenience constructor for the concrete Core gateway.</summary>
    public AdventureViewModel(
        IAdventureGateway? planner,
        AdventureExecutionGateway execution,
        AdventureActionAllowlist? allowlist = null,
        AdventureState? initialState = null,
        IAdventureStateQuery? stateQuery = null,
        TimeSpan? staleAfter = null)
        : this(planner, initialState, new AdventureExecutionGatewayClient(execution), allowlist, stateQuery, staleAfter)
    {
    }

    public ObservableCollection<string> Diagnostics { get; }
    public AdventureActionAllowlist Allowlist
    {
        get => _allowlist;
        set => SetProperty(ref _allowlist, value ?? AdventureActionAllowlist.Empty);
    }

    public AdventureState? State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    public AdventureState? CurrentState => State;
    public DailyAdventurePlan? Plan
    {
        get => _plan;
        private set => SetProperty(ref _plan, value);
    }

    public DailyAdventurePlan? DailyPlan => Plan;
    public AdventurePlanProjection? Projection
    {
        get => _projection;
        private set => SetProperty(ref _projection, value);
    }

    public AdventureExecutionResult? LastExecution
    {
        get => _lastExecution;
        private set => SetProperty(ref _lastExecution, value);
    }

    public AdventureExecutionResult? Execution => LastExecution;
    public AdventureCheckpoint? Checkpoint
    {
        get => _checkpoint;
        private set => SetProperty(ref _checkpoint, value);
    }

    public AdventureEncounterState? Encounter
    {
        get => _encounter;
        private set => SetProperty(ref _encounter, value);
    }

    public AdventureManualActionState ManualAction
    {
        get => _manualAction;
        private set => SetProperty(ref _manualAction, value);
    }

    public AdventureManualActionState ManualActionState => ManualAction;

    public AdventureRunUiState RunState
    {
        get => _runState;
        private set
        {
            if (!SetProperty(ref _runState, value)) return;
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsStopping));
            OnPropertyChanged(nameof(IsStopped));
            OnPropertyChanged(nameof(IsCanceled));
            OnPropertyChanged(nameof(IsComplete));
            OnPropertyChanged(nameof(IsBlocked));
        }
    }

    public AdventureInteractionMode Mode
    {
        get => _mode;
        set => SetProperty(ref _mode, value);
    }

    public string AdventureId
    {
        get => _adventureId;
        set => SetProperty(ref _adventureId, value?.Trim() ?? string.Empty);
    }

    public string RunId
    {
        get => _runId;
        set => SetProperty(ref _runId, value?.Trim() ?? string.Empty);
    }

    public bool AllowStaleExecution
    {
        get => _allowStaleExecution;
        set => SetProperty(ref _allowStaleExecution, value);
    }

    public bool IsRunning => RunState is AdventureRunUiState.Loading or AdventureRunUiState.Running;
    public bool IsStopping => RunState == AdventureRunUiState.Stopping;
    public bool IsStopped => RunState == AdventureRunUiState.Stopped;
    public new bool IsCanceled => RunState == AdventureRunUiState.Canceled;
    public bool IsComplete => RunState == AdventureRunUiState.Complete;
    public bool IsBlocked => RunState == AdventureRunUiState.Blocked;
    public bool IsStaleData => IsStale || ManualAction.IsStale || Projection?.IsStale == true;
    public bool HasUnknownState => _hasUnknownState || ManualAction.IsUnknown || Projection?.IsUnknown == true;
    public bool IsUnknown => HasUnknownState;
    public string? UnknownReason
    {
        get => _unknownReason;
        private set => SetProperty(ref _unknownReason, value);
    }

    public string? RunMessage
    {
        get => _runMessage;
        private set => SetProperty(ref _runMessage, value);
    }

    public BoundedAdventurePreviewState? BoundedPreview
    {
        get => _boundedPreview;
        private set => SetProperty(ref _boundedPreview, value);
    }

    /// <summary>
    /// Evaluates a bounded run against a caller-supplied fresh snapshot. This
    /// is presentation-only and never invokes the execution client.
    /// </summary>
    public BoundedAdventurePreviewState PreviewBoundedRun(
        IEnumerable<AdventureCondition>? conditions,
        AdventureConditionSnapshot? snapshot,
        BoundedAdventureRunnerOptions? options = null)
    {
        var normalized = (conditions ?? Array.Empty<AdventureCondition>()).Select(x => x.Normalize()).ToArray();
        var settings = (options ?? new BoundedAdventureRunnerOptions()).Normalize();
        IReadOnlyList<AdventureConditionResult> results;
        BoundedAdventureRunStopReason reason;
        if (normalized.Length == 0)
        {
            results = Array.Empty<AdventureConditionResult>();
            reason = BoundedAdventureRunStopReason.ReadyToRun;
        }
        else if (snapshot is null)
        {
            results = normalized.Select(condition => new AdventureConditionResult
            {
                Condition = condition,
                IsSatisfied = false,
                Reason = "A fresh condition snapshot is required before an adventure run can start."
            }).ToArray();
            reason = BoundedAdventureRunStopReason.ConditionsBlocked;
        }
        else
        {
            results = AdventureConditionEvaluator.EvaluateAll(normalized, snapshot);
            reason = results.All(x => x.IsSatisfied)
                ? BoundedAdventureRunStopReason.ConditionsSatisfied
                : BoundedAdventureRunStopReason.ReadyToRun;
        }

        BoundedPreview = new BoundedAdventurePreviewState
        {
            StopReason = reason,
            Conditions = results,
            MaxGatewaySteps = settings.MaxGatewaySteps,
            MaxDuration = settings.MaxDuration
        };
        OnStatusChanged();
        return BoundedPreview;
    }

    public string FreshnessText => IsStaleData ? "stale" : HasUnknownState ? "unknown" : "fresh";
    public string StatusText => RunState switch
    {
        AdventureRunUiState.Ready => "Ready",
        AdventureRunUiState.Running => "Running",
        AdventureRunUiState.Stopping => "Stopping",
        AdventureRunUiState.Stopped => "Stopped",
        AdventureRunUiState.Complete => "Complete",
        AdventureRunUiState.Blocked => "Blocked",
        AdventureRunUiState.Unknown => "Unknown · manual review",
        AdventureRunUiState.Stale => "Stale · refresh required",
        AdventureRunUiState.Canceled => "Canceled",
        AdventureRunUiState.Error => "Error",
        AdventureRunUiState.Loading => "Loading",
        _ => "Idle"
    };

    public Task<AdventurePlanProjection?> LoadAsync(CancellationToken cancellationToken = default)
        => LoadCoreAsync(
            LoadSnapshotAsync,
            ApplyPlan,
            snapshot => snapshot.IsEmpty ? DesktopLoadState.Empty : snapshot.IsStale ? DesktopLoadState.Stale : DesktopLoadState.Ready,
            snapshot => snapshot.IsStale,
            snapshot => snapshot.ObservedAt,
            cancellationToken);

    public Task<AdventurePlanProjection?> RefreshAsync(CancellationToken cancellationToken = default)
        => LoadAsync(cancellationToken);

    public Task<AdventurePlanProjection?> LoadAsync(
        AdventureState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        State = state.Normalize();
        return LoadAsync(cancellationToken);
    }

    public Task<AdventurePlanProjection?> RefreshAsync(
        AdventureState state,
        CancellationToken cancellationToken = default)
        => LoadAsync(state, cancellationToken);

    public Task<AdventurePlanProjection?> PlanAsync(CancellationToken cancellationToken = default)
        => LoadAsync(cancellationToken);

    public Task<AdventureExecutionResult?> StartAsync(
        AdventureRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteOperationAsync(
            async token => _execution is null
                ? null
                : await _execution.StartAsync(request, Allowlist, token).ConfigureAwait(false),
            cancellationToken);
    }

    public Task<AdventureExecutionResult?> StartRunAsync(
        AdventureRunRequest request,
        CancellationToken cancellationToken = default)
        => StartAsync(request, cancellationToken);

    public Task<AdventureExecutionResult?> StartAsync(
        string adventureId,
        string? runId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adventureId);
        var id = string.IsNullOrWhiteSpace(runId) ? NewRunId() : runId.Trim();
        return StartAsync(new AdventureRunRequest
        {
            RunId = id,
            AdventureId = adventureId.Trim(),
            DailyLimit = new AdventureDailyLimitContract(),
            Counters = new AdventureCounterContract()
        }, cancellationToken);
    }

    public Task<AdventureExecutionResult?> ResumeAsync(
        string? runId = null,
        CancellationToken cancellationToken = default)
    {
        var id = string.IsNullOrWhiteSpace(runId) ? RunId : runId.Trim();
        if (string.IsNullOrWhiteSpace(id))
            return FailExecutionAsync("A run id is required to resume.", unknown: true);
        RunId = id;
        return ExecuteOperationAsync(
            async token => _execution is null
                ? null
                : await _execution.ResumeAsync(id, Allowlist, token).ConfigureAwait(false),
            cancellationToken);
    }

    public Task<AdventureExecutionResult?> ExecutePendingAsync(
        string? runId = null,
        CancellationToken cancellationToken = default)
    {
        var id = string.IsNullOrWhiteSpace(runId) ? RunId : runId.Trim();
        if (string.IsNullOrWhiteSpace(id))
            return FailExecutionAsync("A run id is required to execute a pending action.", unknown: true);
        RunId = id;
        return ExecuteOperationAsync(
            async token => _execution is null
                ? null
                : await _execution.ExecutePendingAsync(id, Allowlist, token).ConfigureAwait(false),
            cancellationToken);
    }

    public Task<AdventureExecutionResult?> StepAsync(
        string? runId = null,
        CancellationToken cancellationToken = default)
        => ExecutePendingAsync(runId, cancellationToken);

    public Task<AdventureExecutionResult?> RefreshEncounterAsync(
        string? runId = null,
        CancellationToken cancellationToken = default)
    {
        var id = string.IsNullOrWhiteSpace(runId) ? RunId : runId.Trim();
        if (string.IsNullOrWhiteSpace(id))
            return FailExecutionAsync("A run id is required to refresh the encounter.", unknown: true);
        RunId = id;
        return ExecuteOperationAsync(
            async token => _execution is null
                ? null
                : await _execution.RefreshAsync(id, Allowlist, token).ConfigureAwait(false),
            cancellationToken,
            allowStale: true);
    }

    public Task<AdventureExecutionResult?> RefreshExecutionAsync(
        string? runId = null,
        CancellationToken cancellationToken = default)
        => RefreshEncounterAsync(runId, cancellationToken);

    /// <summary>Stop the local operation and keep the checkpoint for resume.</summary>
    public void Stop()
    {
        _stopRequested = true;
        RunState = AdventureRunUiState.Stopping;
        CancelOperation();
        RunState = AdventureRunUiState.Stopped;
        RunMessage = "Adventure stopped; checkpoint retained for resume.";
        OnStatusChanged();
    }

    public void CancelRun() => Stop();
    public void CancelExecution() => Stop();

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stop();
        return Task.CompletedTask;
    }

    public void SetUnknown(string? reason = null, bool stale = false)
    {
        _hasUnknownState = true;
        UnknownReason = string.IsNullOrWhiteSpace(reason) ? "The encounter is unknown; manual review is required." : reason;
        ManualAction = AdventureManualActionState.Empty(stale, true, UnknownReason);
        RunState = stale ? AdventureRunUiState.Stale : AdventureRunUiState.Unknown;
        OnStatusChanged();
    }

    public void ClearUnknown()
    {
        _hasUnknownState = false;
        UnknownReason = null;
        if (RunState is AdventureRunUiState.Unknown or AdventureRunUiState.Stale)
            RunState = Plan is null ? AdventureRunUiState.Idle : AdventureRunUiState.Ready;
        OnStatusChanged();
    }

    private async Task<AdventurePlanProjection> LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        var stateSnapshot = _stateQuery is null
            ? State is null
                ? AdventureStateSnapshot.Empty()
                : AdventureStateSnapshot.Fresh(State)
            : await _stateQuery.GetAsync(cancellationToken).ConfigureAwait(false);

        stateSnapshot ??= AdventureStateSnapshot.Empty();
        var state = stateSnapshot.State?.Normalize();
        DailyAdventurePlan? plan = null;
        if (state is not null && _planner is not null)
            plan = await _planner.PlanAsync(state, cancellationToken).ConfigureAwait(false);

        var observedAt = stateSnapshot.ObservedAt == default ? DateTimeOffset.UtcNow : stateSnapshot.ObservedAt;
        var stale = stateSnapshot.IsStale || DateTimeOffset.UtcNow - observedAt > _staleAfter;
        return new AdventurePlanProjection
        {
            State = state,
            Plan = plan,
            ObservedAt = observedAt,
            IsStale = stale,
            Advisories = stateSnapshot.Advisories.ToArray()
        };
    }

    private void ApplyPlan(AdventurePlanProjection projection)
    {
        State = projection.State;
        Plan = projection.Plan;
        Projection = projection;
        Diagnostics.Clear();
        foreach (var advisory in projection.Advisories)
            Diagnostics.Add(advisory);

        _hasUnknownState = projection.IsUnknown;
        UnknownReason = projection.IsUnknown ? "The adventure plan source returned an unknown snapshot." : null;
        if (!string.IsNullOrWhiteSpace(AdventureId))
            AdventureId = AdventureId;
        OnStatusChanged();
    }

    private Task<AdventureExecutionResult?> ExecuteOperationAsync(
        Func<CancellationToken, Task<AdventureExecutionResult?>> operation,
        CancellationToken cancellationToken,
        bool allowStale = false)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (_execution is null)
            return FailExecutionAsync("No adventure execution gateway is configured.", unknown: false);
        if (IsStaleData && !allowStale && !AllowStaleExecution)
            return FailExecutionAsync("The adventure snapshot is stale; refresh before executing.", stale: true);

        _stopRequested = false;
        RunState = AdventureRunUiState.Running;
        return OperationCoreAsync(
            operation,
            result =>
            {
                if (result is null)
                {
                    RunMessage = "No execution result was returned.";
                    RunState = AdventureRunUiState.Error;
                    return;
                }
                ApplyExecution(result);
            },
            cancellationToken,
            result => result is null
                ? DesktopLoadState.Error
                : result.IsComplete
                    ? DesktopLoadState.Ready
                    : result.IsAborted
                        ? DesktopLoadState.Error
                        : result.RequiresRefresh || result.IsIndeterminate
                            ? DesktopLoadState.Stale
                            : DesktopLoadState.Ready)
            .ContinueWith(static task => task.Status == TaskStatus.RanToCompletion ? task.Result : null,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    private Task<AdventureExecutionResult?> FailExecutionAsync(
        string message,
        bool stale = false,
        bool unknown = false)
    {
        RunMessage = message;
        ErrorMessage = message;
        OperationErrorMessage = message;
        _hasUnknownState = unknown;
        UnknownReason = unknown ? message : UnknownReason;
        ManualAction = AdventureManualActionState.Empty(stale, unknown, message);
        RunState = stale ? AdventureRunUiState.Stale : unknown ? AdventureRunUiState.Unknown : AdventureRunUiState.Blocked;
        OperationState = DesktopLoadState.Error;
        OnStatusChanged();
        return Task.FromResult<AdventureExecutionResult?>(null);
    }

    private void ApplyExecution(AdventureExecutionResult result)
    {
        LastExecution = result;
        Checkpoint = result.Checkpoint;
        State = result.State is null ? State : State;
        if (!string.IsNullOrWhiteSpace(result.Checkpoint.RunId))
            RunId = result.Checkpoint.RunId;
        if (!string.IsNullOrWhiteSpace(result.Checkpoint.AdventureId))
            AdventureId = result.Checkpoint.AdventureId;
        Encounter = result.Response?.Encounter;
        RunMessage = result.Messages.Count == 0 ? result.Status.ToString() : string.Join(" ", result.Messages);
        Diagnostics.Clear();
        foreach (var message in result.Messages)
            Diagnostics.Add(message);

        var unknown = result.Response?.Encounter?.Kind == AdventureEncounterKind.Unknown
            || result.AbortReason is AdventureAbortReason.UnknownEncounter
                or AdventureAbortReason.UnknownFight
                or AdventureAbortReason.UnknownChoice
                or AdventureAbortReason.ChoiceOptionNotAvailable
            || result.IsIndeterminate;
        var stale = result.RequiresRefresh || result.IsIndeterminate;
        _hasUnknownState = unknown;
        UnknownReason = unknown
            ? result.AbortReason?.ToString() ?? result.Messages.FirstOrDefault() ?? "Unknown encounter; manual review is required."
            : null;
        ManualAction = BuildManualAction(result, stale, unknown);
        RunState = _stopRequested
            ? AdventureRunUiState.Stopped
            : result.IsComplete
                ? AdventureRunUiState.Complete
                : result.IsAborted
                    ? unknown ? AdventureRunUiState.Unknown : AdventureRunUiState.Blocked
                    : stale ? AdventureRunUiState.Stale
                    : result.NextAction is null ? AdventureRunUiState.Paused : AdventureRunUiState.Running;
        OnStatusChanged();
    }

    private AdventureManualActionState BuildManualAction(
        AdventureExecutionResult result,
        bool stale,
        bool unknown)
    {
        var action = result.NextAction;
        return new AdventureManualActionState
        {
            RunId = result.Checkpoint.RunId,
            EncounterKind = result.Response?.Encounter?.Kind ?? KindFor(action),
            Action = action,
            ActionId = action is AdventureFightAction fight ? fight.ActionId : null,
            ChoiceId = action is AdventureChoiceAction choice ? choice.ChoiceId : null,
            OptionId = action is AdventureChoiceAction selected ? selected.OptionId : null,
            Round = action is AdventureFightAction round ? round.Round : result.Response?.Encounter?.FightRound,
            MonsterId = action is AdventureFightAction monster ? monster.MonsterId : null,
            AvailableChoices = result.Response?.Encounter?.AvailableChoices?.ToArray() ?? Array.Empty<string>(),
            RequiresApproval = action is not null && !result.IsComplete,
            IsStale = stale,
            IsUnknown = unknown,
            RequiresRefresh = result.RequiresRefresh,
            BlockReason = unknown ? UnknownReason : result.RequiresRefresh ? "Refresh required before another action." : null,
            ObservedAt = DateTimeOffset.UtcNow
        };
    }

    private static AdventureEncounterKind KindFor(AdventureKernelAction? action)
        => action switch
        {
            AdventureFightAction => AdventureEncounterKind.Fight,
            AdventureChoiceAction => AdventureEncounterKind.Choice,
            AdventureEncounterAction => AdventureEncounterKind.Reward,
            _ => AdventureEncounterKind.Unknown
        };

    private static string NewRunId()
        => $"desktop-{Guid.NewGuid():N}";

    private void OnStatusChanged()
    {
        OnPropertyChanged(nameof(IsStaleData));
        OnPropertyChanged(nameof(HasUnknownState));
        OnPropertyChanged(nameof(IsUnknown));
        OnPropertyChanged(nameof(FreshnessText));
        OnPropertyChanged(nameof(StatusText));
    }
}

/// <summary>One UI-friendly policy preview; no combat request is emitted.</summary>
public sealed record AutoCombatPolicyPreview
{
    public AutoCombatPolicy Policy { get; init; } = new();
    public AutoCombatContext? Context { get; init; }
    public AutoCombatUsage? Usage { get; init; }
    public AutoCombatDecision? Decision { get; init; }
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool IsStale { get; init; }
    public bool IsUnknown { get; init; }
    public bool IsPreviewOnly { get; init; } = true;
    public bool IsBlocked => Decision?.IsAborted == true || Decision?.IsManual == true || IsStale || IsUnknown;
    public bool RequiresManualAction => Decision?.IsManual == true || IsUnknown;
    public string StatusText => IsStale ? "stale" : IsUnknown ? "unknown" : Decision switch
    {
        null => "not evaluated",
        { IsAborted: true } => "blocked",
        { IsManual: true } => "manual review",
        _ => "action ready"
    };
    public IReadOnlyList<AutoCombatRuleEvaluation> EvaluatedRules
        => Decision?.EvaluatedRules ?? Array.Empty<AutoCombatRuleEvaluation>();
    public IReadOnlyList<string> Advisories { get; init; } = Array.Empty<string>();
}

/// <summary>Optional read boundary for a live combat observation.</summary>
public interface ICombatObservationQuery
{
    Task<AutoCombatContext?> InspectAsync(CancellationToken cancellationToken = default);
    Task<AutoCombatContext?> RefreshAsync(CancellationToken cancellationToken = default)
        => InspectAsync(cancellationToken);
}

/// <summary>Optional typed read boundary for the Core fight-page parser.</summary>
public interface ICombatPageQuery
{
    Task<CombatObservation?> InspectAsync(CancellationToken cancellationToken = default);
    Task<CombatObservation?> RefreshAsync(CancellationToken cancellationToken = default)
        => InspectAsync(cancellationToken);
}

/// <summary>Optional action boundary used by the desktop manual-action card.</summary>
public interface ICombatActionExecutor
{
    Task<CombatActionResult> ExecuteAsync(
        AutoCombatAction action,
        AutoCombatContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Transport-neutral result for a manually selected combat action.</summary>
public sealed record CombatActionResult
{
    public AutoCombatAction Action { get; init; } = AutoCombatAction.Manual();
    public AutoCombatContext? Observation { get; init; }
    public bool Accepted { get; init; }
    public bool IsComplete { get; init; }
    public bool IsStale { get; init; }
    public bool IsUnknown { get; init; }
    public string? Message { get; init; }
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Current manual combat card.</summary>
public sealed record CombatManualActionState
{
    public AutoCombatAction? SelectedAction { get; init; }
    public AutoCombatContext? Context { get; init; }
    public bool RequiresApproval { get; init; }
    public bool IsStale { get; init; }
    public bool IsUnknown { get; init; }
    public bool IsComplete { get; init; }
    public string? BlockReason { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }

    public bool IsAvailable => SelectedAction is not null && !IsStale && !IsUnknown && !IsComplete;
    public bool CanExecute => IsAvailable && !RequiresApproval;
    public bool IsBlocked => !CanExecute;
    public string ActionText => SelectedAction is null
        ? IsUnknown ? "Unknown combat action · manual review required" : "No action selected"
        : SelectedAction.Kind switch
        {
            AutoCombatActionKind.Skill => $"Skill · {SelectedAction.Id}",
            AutoCombatActionKind.Item => $"Item · {SelectedAction.Id}",
            _ => SelectedAction.Kind.ToString()
        };
}

/// <summary>
/// Manual combat and policy-preview ViewModel.  It is deliberately usable
/// without an action executor, which keeps disconnected desktop sessions
/// read-only while still showing a deterministic policy explanation.
/// </summary>
public class CombatViewModel : DesktopViewModel
{
    private readonly ICombatObservationQuery? _observationQuery;
    private readonly ICombatPageQuery? _combatPageQuery;
    private readonly ICombatActionExecutor? _executor;
    private AutoCombatContext? _observation;
    private AutoCombatPolicyPreview? _autoCombatPreview;
    private AutoCombatDecision? _lastDecision;
    private CombatActionResult? _lastActionResult;
    private CombatManualActionState _manualAction = new();
    private AdventureRunUiState _runState = AdventureRunUiState.Idle;
    private bool _stopRequested;
    private bool _hasUnknown;
    private string? _unknownReason;
    private string? _statusMessage;
    private CombatObservation? _combatObservation;

    public CombatViewModel(
        ICombatObservationQuery? observationQuery = null,
        ICombatActionExecutor? executor = null)
    {
        _observationQuery = observationQuery;
        _executor = executor;
        Diagnostics = new ObservableCollection<string>();
        AvailableActions = new ObservableCollection<AutoCombatAction>();
    }

    /// <summary>Convenience constructor for a Core fight-page observation source.</summary>
    public CombatViewModel(
        ICombatPageQuery observationQuery,
        ICombatActionExecutor? executor = null)
        : this((ICombatObservationQuery?)null, executor)
    {
        _combatPageQuery = observationQuery ?? throw new ArgumentNullException(nameof(observationQuery));
    }

    public ObservableCollection<string> Diagnostics { get; }
    public ObservableCollection<AutoCombatAction> AvailableActions { get; }

    public AutoCombatContext? Observation
    {
        get => _observation;
        private set => SetProperty(ref _observation, value);
    }

    public AutoCombatContext? CurrentObservation => Observation;
    public CombatObservation? CombatObservation
    {
        get => _combatObservation;
        private set => SetProperty(ref _combatObservation, value);
    }

    public CombatObservation? RawObservation => CombatObservation;
    public AutoCombatPolicyPreview? AutoCombatPreview
    {
        get => _autoCombatPreview;
        private set => SetProperty(ref _autoCombatPreview, value);
    }

    public AutoCombatPolicyPreview? PolicyPreview => AutoCombatPreview;
    public AutoCombatDecision? LastDecision
    {
        get => _lastDecision;
        private set => SetProperty(ref _lastDecision, value);
    }

    public CombatActionResult? LastActionResult
    {
        get => _lastActionResult;
        private set => SetProperty(ref _lastActionResult, value);
    }

    public CombatManualActionState ManualAction
    {
        get => _manualAction;
        private set => SetProperty(ref _manualAction, value);
    }

    public CombatManualActionState ManualActionState => ManualAction;
    public AdventureRunUiState RunState
    {
        get => _runState;
        private set
        {
            if (!SetProperty(ref _runState, value)) return;
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsStopping));
            OnPropertyChanged(nameof(IsStopped));
            OnPropertyChanged(nameof(IsCanceled));
        }
    }

    public bool IsRunning => RunState is AdventureRunUiState.Loading or AdventureRunUiState.Running;
    public bool IsStopping => RunState == AdventureRunUiState.Stopping;
    public bool IsStopped => RunState == AdventureRunUiState.Stopped;
    public new bool IsCanceled => RunState == AdventureRunUiState.Canceled;
    public bool IsStaleData => ManualAction.IsStale || AutoCombatPreview?.IsStale == true;
    public bool HasUnknownState => _hasUnknown || ManualAction.IsUnknown || AutoCombatPreview?.IsUnknown == true;
    public bool IsUnknown => HasUnknownState;
    public bool AllowStaleActions { get; set; }
    public string? UnknownReason
    {
        get => _unknownReason;
        private set => SetProperty(ref _unknownReason, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string FreshnessText => IsStaleData ? "stale" : HasUnknownState ? "unknown" : "fresh";
    public string StatusText => RunState switch
    {
        AdventureRunUiState.Running => "Running",
        AdventureRunUiState.Stopping => "Stopping",
        AdventureRunUiState.Stopped => "Stopped",
        AdventureRunUiState.Canceled => "Canceled",
        AdventureRunUiState.Error => "Error",
        AdventureRunUiState.Unknown => "Unknown · manual review",
        AdventureRunUiState.Stale => "Stale · refresh required",
        _ => "Ready"
    };

    public Task<AutoCombatContext?> LoadAsync(CancellationToken cancellationToken = default)
        => LoadCoreAsync(
            LoadContextAsync,
            ApplyObservation,
            value => value is null ? DesktopLoadState.Empty : HasUnknownState ? DesktopLoadState.Stale : DesktopLoadState.Ready,
            _ => IsStaleData,
            _ => DateTimeOffset.UtcNow,
            cancellationToken);

    public Task<AutoCombatContext?> InspectAsync(CancellationToken cancellationToken = default)
        => OperationCoreAsync(
            LoadContextAsync,
            ApplyObservation,
            cancellationToken,
            value => value is null ? DesktopLoadState.Empty : DesktopLoadState.Ready)
            .ContinueWith(static task => task.Status == TaskStatus.RanToCompletion ? task.Result : null,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

    public Task<AutoCombatContext?> RefreshAsync(CancellationToken cancellationToken = default)
        => OperationCoreAsync(
            RefreshContextAsync,
            ApplyObservation,
            cancellationToken,
            value => value is null ? DesktopLoadState.Empty : DesktopLoadState.Ready)
            .ContinueWith(static task => task.Status == TaskStatus.RanToCompletion ? task.Result : null,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

    public void SetObservation(AutoCombatContext? observation, bool isStale = false, bool isUnknown = false, string? reason = null)
    {
        ApplyObservation(observation);
        if (isStale || isUnknown)
            SetIndicators(isStale, isUnknown, reason);
    }

    public void SetObservation(CombatObservation? observation, bool isStale = false, bool isUnknown = false, string? reason = null)
    {
        CombatObservation = observation?.Normalize();
        SetObservation(observation.ToAutoCombatContext(), isStale, isUnknown || observation.IsUnknown(), reason);
        if (CombatObservation is { } page)
        {
            AvailableActions.Clear();
            foreach (var action in page.AvailableActions.Where(x => x.IsEnabled))
                AvailableActions.Add(action.ToAutoCombatAction());
        }
    }

    public void SelectManualAction(AutoCombatAction? action, bool requiresApproval = true)
    {
        ManualAction = new CombatManualActionState
        {
            SelectedAction = action,
            Context = Observation,
            RequiresApproval = requiresApproval,
            IsStale = IsStaleData,
            IsUnknown = HasUnknownState,
            IsComplete = false,
            BlockReason = HasUnknownState ? UnknownReason : IsStaleData ? "Refresh required before executing." : null,
            ObservedAt = DateTimeOffset.UtcNow
        };
        OnStatusChanged();
    }

    public void ClearManualAction() => SelectManualAction(null, false);

    public AutoCombatPolicyPreview PreviewAutoCombat(
        AutoCombatPolicy policy,
        AutoCombatContext? context = null,
        AutoCombatUsage? usage = null,
        bool isStale = false)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var snapshot = context ?? Observation;
        var unknown = snapshot is null;
        AutoCombatDecision? decision = null;
        var advisories = new List<string>();
        if (snapshot is null)
            advisories.Add("No combat observation is available; manual review is required.");
        else
            decision = policy.Evaluate(snapshot, usage);

        var preview = new AutoCombatPolicyPreview
        {
            Policy = policy,
            Context = snapshot,
            Usage = usage,
            Decision = decision,
            GeneratedAt = DateTimeOffset.UtcNow,
            IsStale = isStale || IsStaleData,
            IsUnknown = unknown || HasUnknownState,
            Advisories = advisories
        };
        AutoCombatPreview = preview;
        LastDecision = decision;
        if (decision is not null)
            SelectManualAction(decision.Action, requiresApproval: true);
        else
            SetIndicators(preview.IsStale, true, advisories[0]);
        OnStatusChanged();
        return preview;
    }

    public Task<AutoCombatPolicyPreview?> PreviewAutoCombatAsync(
        AutoCombatPolicy policy,
        AutoCombatContext? context = null,
        AutoCombatUsage? usage = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return OperationCoreAsync(
            token => Task.FromResult<AutoCombatPolicyPreview?>(PreviewAutoCombat(policy, context, usage)),
            _ => { },
            cancellationToken,
            _ => DesktopLoadState.Ready)
            .ContinueWith(static task => task.Status == TaskStatus.RanToCompletion ? task.Result : null,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    public Task<CombatActionResult?> ExecuteManualActionAsync(
        AutoCombatAction? action = null,
        CancellationToken cancellationToken = default)
    {
        var selected = action ?? ManualAction.SelectedAction;
        if (selected is null)
            return FailActionAsync("Select a combat action before executing.", unknown: true);
        if (Observation is null)
            return FailActionAsync("No combat observation is available; refresh before executing.", unknown: true);
        if ((IsStaleData || ManualAction.IsStale) && !AllowStaleActions)
            return FailActionAsync("The combat observation is stale; refresh before executing.", stale: true);
        if (_executor is null)
            return FailActionAsync("No combat action executor is configured.");

        _stopRequested = false;
        RunState = AdventureRunUiState.Running;
        SelectManualAction(selected, requiresApproval: false);
        return OperationCoreAsync(
            token => _executor.ExecuteAsync(selected, Observation, token),
            ApplyActionResult,
            cancellationToken,
            result => result is null || !result.Accepted ? DesktopLoadState.Error : result.IsStale ? DesktopLoadState.Stale : DesktopLoadState.Ready)
            .ContinueWith(static task => task.Status == TaskStatus.RanToCompletion ? task.Result : null,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    public Task<CombatActionResult?> ExecuteAsync(
        AutoCombatAction? action = null,
        CancellationToken cancellationToken = default)
        => ExecuteManualActionAsync(action, cancellationToken);

    public void Stop()
    {
        _stopRequested = true;
        RunState = AdventureRunUiState.Stopping;
        CancelOperation();
        RunState = AdventureRunUiState.Stopped;
        StatusMessage = "Combat stopped; no further action will be submitted.";
        OnStatusChanged();
    }

    public void CancelRun() => Stop();
    public void CancelExecution() => Stop();
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stop();
        return Task.CompletedTask;
    }

    public void SetUnknown(string? reason = null, bool stale = false)
        => SetIndicators(stale, true, reason);

    private void ApplyObservation(AutoCombatContext? observation)
    {
        Observation = observation?.Normalize();
        AvailableActions.Clear();
        if (Observation is not null)
        {
            AvailableActions.Add(AutoCombatAction.Attack("Manual attack"));
            AvailableActions.Add(AutoCombatAction.Run("Manual runaway"));
        }
        if (observation is null)
            SetIndicators(false, true, "No combat observation is available.");
        else
        {
            _hasUnknown = false;
            UnknownReason = null;
            if (RunState is AdventureRunUiState.Unknown or AdventureRunUiState.Stale)
                RunState = AdventureRunUiState.Ready;
        }
        ManualAction = ManualAction with
        {
            Context = Observation,
            IsUnknown = Observation is null || _hasUnknown,
            IsStale = false,
            BlockReason = Observation is null ? UnknownReason : null,
            ObservedAt = DateTimeOffset.UtcNow
        };
        OnStatusChanged();
    }

    private async Task<AutoCombatContext?> LoadContextAsync(CancellationToken cancellationToken)
    {
        if (_combatPageQuery is not null)
        {
            var page = await _combatPageQuery.InspectAsync(cancellationToken).ConfigureAwait(false);
            CombatObservation = page?.Normalize();
            return page.ToAutoCombatContext();
        }
        return _observationQuery is null
            ? null
            : await _observationQuery.InspectAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<AutoCombatContext?> RefreshContextAsync(CancellationToken cancellationToken)
    {
        if (_combatPageQuery is not null)
        {
            var page = await _combatPageQuery.RefreshAsync(cancellationToken).ConfigureAwait(false);
            CombatObservation = page?.Normalize();
            return page.ToAutoCombatContext();
        }
        return _observationQuery is null
            ? null
            : await _observationQuery.RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ApplyActionResult(CombatActionResult result)
    {
        LastActionResult = result;
        StatusMessage = result.Message;
        if (result.Observation is not null)
            Observation = result.Observation.Normalize();
        SetIndicators(result.IsStale, result.IsUnknown, result.Message);
        ManualAction = ManualAction with
        {
            Context = Observation,
            IsStale = result.IsStale,
            IsUnknown = result.IsUnknown,
            IsComplete = result.IsComplete,
            RequiresApproval = false,
            BlockReason = result.IsUnknown ? result.Message : result.IsStale ? "Refresh required before another action." : null,
            ObservedAt = result.ObservedAt
        };
        RunState = _stopRequested
            ? AdventureRunUiState.Stopped
            : result.IsComplete ? AdventureRunUiState.Complete
            : result.IsUnknown ? AdventureRunUiState.Unknown
            : result.IsStale ? AdventureRunUiState.Stale
            : result.Accepted ? AdventureRunUiState.Ready
            : AdventureRunUiState.Blocked;
        OnStatusChanged();
    }

    private Task<CombatActionResult?> FailActionAsync(string message, bool stale = false, bool unknown = false)
    {
        StatusMessage = message;
        OperationErrorMessage = message;
        ErrorMessage = message;
        SetIndicators(stale, unknown, message);
        ManualAction = ManualAction with
        {
            IsStale = stale,
            IsUnknown = unknown,
            RequiresApproval = false,
            BlockReason = message,
            ObservedAt = DateTimeOffset.UtcNow
        };
        RunState = stale ? AdventureRunUiState.Stale : unknown ? AdventureRunUiState.Unknown : AdventureRunUiState.Blocked;
        OperationState = DesktopLoadState.Error;
        return Task.FromResult<CombatActionResult?>(null);
    }

    private void SetIndicators(bool stale, bool unknown, string? reason)
    {
        _hasUnknown = unknown;
        UnknownReason = unknown ? string.IsNullOrWhiteSpace(reason) ? "Combat state is unknown; manual review is required." : reason : null;
        if (unknown)
            RunState = AdventureRunUiState.Unknown;
        else if (stale)
            RunState = AdventureRunUiState.Stale;
        OnStatusChanged();
    }

    private void OnStatusChanged()
    {
        OnPropertyChanged(nameof(IsStaleData));
        OnPropertyChanged(nameof(HasUnknownState));
        OnPropertyChanged(nameof(IsUnknown));
        OnPropertyChanged(nameof(FreshnessText));
        OnPropertyChanged(nameof(StatusText));
    }
}

/// <summary>Alias used by hosts that call combat state a page state.</summary>
public sealed class CombatState : CombatViewModel
{
    public CombatState(ICombatObservationQuery? observationQuery = null, ICombatActionExecutor? executor = null)
        : base(observationQuery, executor)
    {
    }
}

/// <summary>Alias used by hosts that call the Adventure VM a page state.</summary>
public sealed class AdventureStateViewModel : AdventureViewModel
{
    public AdventureStateViewModel(
        IAdventureGateway? planner = null,
        AdventureState? initialState = null,
        IAdventureExecutionClient? execution = null,
        AdventureActionAllowlist? allowlist = null,
        IAdventureStateQuery? stateQuery = null,
        TimeSpan? staleAfter = null)
        : base(planner, initialState, execution, allowlist, stateQuery, staleAfter)
    {
    }
}

/// <summary>Small adapters from the Core combat observation to the policy DSL.</summary>
public static class CombatObservationDesktopExtensions
{
    public static AutoCombatContext? ToAutoCombatContext(this CombatObservation? observation)
    {
        if (observation is null) return null;
        var value = observation.Normalize();
        return new AutoCombatContext
        {
            Round = Math.Max(1, value.Round),
            Hp = Math.Max(0, value.PlayerHp ?? 0),
            MaxHp = Math.Max(0, value.PlayerMaxHp ?? value.PlayerHp ?? 0),
            Mp = Math.Max(0, value.PlayerMp ?? 0),
            MaxMp = Math.Max(0, value.PlayerMaxMp ?? value.PlayerMp ?? 0),
            Monster = value.MonsterName ?? value.MonsterId,
            Effects = value.Effects.Select(x => x.Name).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray()
        };
    }

    public static bool IsUnknown(this CombatObservation? observation)
        => observation is null
            || observation.Outcome == CombatOutcome.Unknown
            || string.IsNullOrWhiteSpace(observation.MonsterName)
            || observation.Round <= 0;

    public static AutoCombatAction ToAutoCombatAction(this CombatAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action.Kind switch
        {
            CombatActionKind.Attack => AutoCombatAction.Attack(action.Name ?? "Manual attack"),
            CombatActionKind.Skill when !string.IsNullOrWhiteSpace(action.Id)
                => AutoCombatAction.Skill(action.Id!, action.Name),
            CombatActionKind.Item when !string.IsNullOrWhiteSpace(action.Id)
                => AutoCombatAction.Item(action.Id!, action.Name),
            CombatActionKind.Run => AutoCombatAction.Run(action.Name ?? "Manual runaway"),
            _ => AutoCombatAction.Manual("Unknown combat action; manual review is required.")
        };
    }
}
