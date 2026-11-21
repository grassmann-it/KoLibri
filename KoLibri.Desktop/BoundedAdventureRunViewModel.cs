using System.Collections.ObjectModel;
using KoLibri.Core.Spiel;

namespace KoLibri.Desktop;

/// <summary>
/// Stop reasons surfaced to the shell for a bounded run.
/// Mirrors <see cref="BoundedAdventureRunStopReason"/> and
/// <see cref="BoundedChoiceChainStopReason"/> strings; the VM never has to know
/// the underlying enums.
/// </summary>
public enum BoundedRunUiState
{
    Idle,
    Previewing,
    Preview,
    Running,
    Stopping,
    Stopped,
    Complete,
    Denied,
    Blocked,
    Error
}

/// <summary>
/// View-model that exposes a bounded run / choice-chain to the shell.
/// Drives the Stop button and the approval-token dialog; it never invents
/// live HTTP itself, so a missing bridge just keeps the preview-only path.
/// </summary>
public sealed class BoundedAdventureRunViewModel : DesktopViewModel
{
    private readonly DesktopAdventureRunBridge? _bridge;
    private BoundedRunUiState _state = BoundedRunUiState.Idle;
    private string? _stopReason;
    private string? _requestId;
    private string? _runId;
    private string? _adventureId;
    private bool _requiresApproval;
    private string? _auditPath;

    public BoundedAdventureRunViewModel(DesktopAdventureRunBridge? bridge = null)
    {
        _bridge = bridge;
        Diagnostics = new ObservableCollection<string>();
        AvailableLocations = new ObservableCollection<string>
        {
            "skeleton-of-crimbos-past",
            "knucklebones",
            "lucky-surprise-egg"
        };
        AuditPath = bridge?.AuditPath;
    }

    public ObservableCollection<string> Diagnostics { get; }
    public ObservableCollection<string> AvailableLocations { get; }

    public BoundedRunUiState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value)) return;
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsStopped));
            OnPropertyChanged(nameof(IsComplete));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public string? StopReason
    {
        get => _stopReason;
        private set => SetProperty(ref _stopReason, value);
    }

    public string? RequestId
    {
        get => _requestId;
        private set => SetProperty(ref _requestId, value);
    }

    public string? RunId
    {
        get => _runId;
        private set => SetProperty(ref _runId, value);
    }

    public string? AdventureId
    {
        get => _adventureId;
        private set => SetProperty(ref _adventureId, value);
    }

    public bool RequiresApproval
    {
        get => _requiresApproval;
        private set => SetProperty(ref _requiresApproval, value);
    }

    public string? AuditPath
    {
        get => _auditPath;
        private set => SetProperty(ref _auditPath, value);
    }

    public bool IsRunning => State == BoundedRunUiState.Running;
    public bool IsStopped => State == BoundedRunUiState.Stopped;
    public bool IsComplete => State == BoundedRunUiState.Complete;
    public bool IsBridgeAvailable => _bridge is not null;

    public string StatusText => State switch
    {
        BoundedRunUiState.Previewing => "Previewing…",
        BoundedRunUiState.Preview => "Preview ready",
        BoundedRunUiState.Running => "Running",
        BoundedRunUiState.Stopping => "Stopping",
        BoundedRunUiState.Stopped => "Stopped",
        BoundedRunUiState.Complete => "Complete",
        BoundedRunUiState.Denied => "Denied",
        BoundedRunUiState.Blocked => "Blocked",
        BoundedRunUiState.Error => "Error",
        _ => "Idle"
    };

    /// <summary>Resets the projection so the shell can re-run a different location.</summary>
    public void Reset()
    {
        State = BoundedRunUiState.Idle;
        StopReason = null;
        RequestId = null;
        Diagnostics.Clear();
        RequiresApproval = false;
    }

    public void Stop()
    {
        if (!IsRunning) return;
        CancelOperation();
        State = BoundedRunUiState.Stopping;
        State = BoundedRunUiState.Stopped;
        StopReason = "Stopped by operator.";
        Diagnostics.Add("Local run was stopped; no further POST will be sent.");
    }

    public void Preview(string adventureId, string? runId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adventureId);
        AdventureId = adventureId.Trim();
        RunId = string.IsNullOrWhiteSpace(runId) ? $"desktop-preview-{Guid.NewGuid():N}" : runId!.Trim();
        StopReason = "ReadyToRun";
        RequestId = null;
        RequiresApproval = _bridge is not null;
        State = BoundedRunUiState.Preview;
        Diagnostics.Clear();
        Diagnostics.Add($"Preview prepared for '{adventureId}'.");
        if (_bridge is null)
            Diagnostics.Add("No execution bridge is configured; this is preview-only.");
    }

    public void ApplyResult(BoundedRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        StopReason = result.StopReason;
        RequestId = result.RequestId;
        AuditPath = result.AuditPath;
        RequiresApproval = false;
        Diagnostics.Clear();
        Diagnostics.Add($"Run completed with stop reason '{result.StopReason}'.");
        if (result.StepCount > 0)
            Diagnostics.Add($"Steps executed: {result.StepCount}.");
        State = result.StopReason switch
        {
            "ApprovalDenied" => BoundedRunUiState.Denied,
            "ConditionsBlocked" => BoundedRunUiState.Blocked,
            "Complete" => BoundedRunUiState.Complete,
            "Stopped" => BoundedRunUiState.Stopped,
            _ => result.Executed ? BoundedRunUiState.Stopped : BoundedRunUiState.Preview
        };
    }

    public void ApplyError(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        StopReason = message;
        State = BoundedRunUiState.Error;
        Diagnostics.Add(message);
    }
}

/// <summary>
/// Catalog of shell context actions exposed by the bounded runner. The shell
/// wires them into context menus and toolbar buttons. Each entry is
/// pre-approved at design time; the bridge is the only runtime authority.
/// </summary>
public static class BoundedRunContextActions
{
    public const string RunBoundedAdventure = "adventure.bounded-run";
    public const string RunBoundedChoiceChain = "adventure.bounded-choice-chain";
    public const string CancelBoundedRun = "adventure.bounded-cancel";

    public static IReadOnlyList<ShellContextActionContract> All { get; } = new[]
    {
        new ShellContextActionContract(RunBoundedAdventure,
            "Run bounded adventure", ShellPage.Adventure, RequiresApproval: true),
        new ShellContextActionContract(RunBoundedChoiceChain,
            "Run bounded choice chain", ShellPage.Adventure, RequiresApproval: true),
        new ShellContextActionContract(CancelBoundedRun,
            "Cancel bounded run", ShellPage.Adventure, RequiresApproval: false)
    };
}
