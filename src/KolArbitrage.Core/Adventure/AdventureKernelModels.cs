using KolArbitrage.Core.Accounts;

namespace KolArbitrage.Core.Adventure;

/// <summary>
/// The small, transport-neutral state machine used by the adventure runner.
/// A kernel state describes what was observed; it never contains an HTTP
/// client, a session cookie, or a request body.
/// </summary>
public enum AdventureRunPhase
{
    Ready,
    Encounter,
    Fight,
    Choice,
    Reward,
    Complete,
    Aborted
}

/// <summary>Stable discriminator for serializers that do not preserve record types.</summary>
public enum AdventureRunStateKind
{
    Ready,
    Encounter,
    Fight,
    Choice,
    Reward,
    Complete,
    Aborted
}

public enum AdventureKernelActionKind
{
    Encounter,
    Fight,
    Choice,
    Refresh,
    Complete
}

/// <summary>Reasons a pure kernel transition can stop the run.</summary>
public enum AdventureAbortReason
{
    UnknownEncounter,
    UnknownFight,
    UnknownChoice,
    ChoiceOptionNotAvailable,
    AdventureNotAllowlisted,
    EncounterNotAllowlisted,
    DailyLimitReached,
    InvalidObservation,
    ResumeMismatch,
    TerminalState,
    InvalidCheckpoint
}

/// <summary>
/// Daily budget supplied by an account/status adapter. The kernel consumes a
/// run reservation before producing a start action, which makes a checkpoint
/// safe to resume after a process crash.
/// </summary>
public sealed record AdventureDailyLimitContract
{
    public DateOnly Day { get; init; }
    public int MaxTurns { get; init; } = int.MaxValue;
    public int TurnsUsed { get; init; }
    public int MaxRuns { get; init; } = int.MaxValue;
    public int RunsUsed { get; init; }

    public int RemainingTurns => Math.Max(0, MaxTurns - TurnsUsed);
    public int RemainingRuns => Math.Max(0, MaxRuns - RunsUsed);
    public bool IsExhausted => RemainingTurns <= 0 || RemainingRuns <= 0;

    public AdventureDailyLimitContract Normalize()
        => this with
        {
            MaxTurns = Math.Max(0, MaxTurns),
            TurnsUsed = Math.Max(0, TurnsUsed),
            MaxRuns = Math.Max(0, MaxRuns),
            RunsUsed = Math.Max(0, RunsUsed)
        };

    public bool CanStart(int turns = 1, int runs = 1)
    {
        var value = Normalize();
        if (turns < 0 || runs < 0) return false;
        return turns <= value.RemainingTurns && runs <= value.RemainingRuns;
    }

    public AdventureDailyLimitContract Reserve(int turns = 1, int runs = 1)
    {
        var value = Normalize();
        if (!value.CanStart(turns, runs))
            throw new InvalidOperationException("The daily adventure limit has been reached.");

        return value with
        {
            TurnsUsed = checked(value.TurnsUsed + turns),
            RunsUsed = checked(value.RunsUsed + runs)
        };
    }
}

/// <summary>
/// Counters are intentionally separate from the daily limit. A caller can
/// persist them alongside a checkpoint and compare them with a fresh account
/// snapshot before resuming.
/// </summary>
public sealed record AdventureCounterContract
{
    public int AdventuresStarted { get; init; }
    public int EncounterActions { get; init; }
    public int FightActions { get; init; }
    public int ChoicesMade { get; init; }
    public int ResponsesObserved { get; init; }

    public AdventureCounterContract Normalize()
        => this with
        {
            AdventuresStarted = Math.Max(0, AdventuresStarted),
            EncounterActions = Math.Max(0, EncounterActions),
            FightActions = Math.Max(0, FightActions),
            ChoicesMade = Math.Max(0, ChoicesMade),
            ResponsesObserved = Math.Max(0, ResponsesObserved)
        };
}

/// <summary>A request to start one bounded adventure run.</summary>
public sealed record AdventureRunRequest
{
    public string RunId { get; init; } = string.Empty;
    public string AdventureId { get; init; } = string.Empty;
    public AccountRole Role { get; init; } = AccountRole.Main;
    public AdventureDailyLimitContract DailyLimit { get; init; } = new();
    public AdventureCounterContract Counters { get; init; } = new();

    public AdventureRunRequest Normalize()
        => this with
        {
            RunId = RunId.Trim(),
            AdventureId = AdventureId.Trim(),
            DailyLimit = (DailyLimit ?? new AdventureDailyLimitContract()).Normalize(),
            Counters = (Counters ?? new AdventureCounterContract()).Normalize()
        };
}

/// <summary>
/// Parsed response data consumed by the kernel. It can be built directly by a
/// fixture parser or from <see cref="AdventureEncounterState"/>; no transport
/// implementation is required.
/// </summary>
public sealed record AdventureKernelObservation
{
    public string AdventureId { get; init; } = string.Empty;
    public AdventureEncounterKind Kind { get; init; } = AdventureEncounterKind.Unknown;
    public string? EncounterId { get; init; }
    public string? MonsterId { get; init; }
    public int? FightRound { get; init; }
    public string? ChoiceId { get; init; }
    public IReadOnlyList<string> AvailableChoices { get; init; } = Array.Empty<string>();
    public bool CanContinue { get; init; } = true;
    public int? AdventuresBefore { get; init; }
    public int? AdventuresAfter { get; init; }
    public string? ResponseHash { get; init; }
    public string? UnknownReason { get; init; }

    public AdventureEncounterKind EncounterKind => Kind;

    public static AdventureKernelObservation From(AdventureEncounterState encounter)
        => new()
        {
            AdventureId = encounter.AdventureId,
            Kind = encounter.Kind,
            EncounterId = encounter.Location,
            ChoiceId = encounter.ChoiceId,
            AvailableChoices = encounter.AvailableChoices ?? Array.Empty<string>(),
            CanContinue = encounter.CanContinue,
            FightRound = encounter.FightRound,
            ResponseHash = encounter.ResponseHash,
            UnknownReason = encounter.Advisories is { Count: > 0 }
                ? string.Join(" ", encounter.Advisories)
                : null
        };
}

/// <summary>Base class for the typed states emitted by <see cref="AdventureRunKernel"/>.</summary>
public abstract record AdventureRunState
{
    protected AdventureRunState(
        string runId,
        string adventureId,
        AccountRole role,
        AdventureRunPhase phase,
        AdventureDailyLimitContract dailyLimit,
        AdventureCounterContract counters)
    {
        RunId = runId;
        AdventureId = adventureId;
        Role = role;
        Phase = phase;
        DailyLimit = dailyLimit.Normalize();
        Counters = counters.Normalize();
    }

    public string RunId { get; }
    public string AdventureId { get; }
    public AccountRole Role { get; }
    public AdventureRunPhase Phase { get; }
    public AdventureRunStateKind StateKind => (AdventureRunStateKind)Phase;
    public AdventureDailyLimitContract DailyLimit { get; init; }
    public AdventureCounterContract Counters { get; init; }
    public string? EncounterId { get; init; }
    public string? MonsterId { get; init; }
    public int? FightRound { get; init; }
    public string? ChoiceId { get; init; }
    public string? SelectedOption { get; init; }
    public string? LastResponseHash { get; init; }
    public AdventureKernelAction? PendingAction { get; init; }
    public string? AbortMessage { get; init; }

    public bool IsTerminal => Phase is AdventureRunPhase.Complete or AdventureRunPhase.Aborted;
}

public sealed record AdventureReadyRunState : AdventureRunState
{
    public AdventureReadyRunState(
        string runId,
        string adventureId,
        AccountRole role,
        AdventureDailyLimitContract dailyLimit,
        AdventureCounterContract counters)
        : base(runId, adventureId, role, AdventureRunPhase.Ready, dailyLimit, counters) { }
}

public sealed record AdventureEncounterRunState : AdventureRunState
{
    public AdventureEncounterRunState(
        string runId,
        string adventureId,
        AccountRole role,
        AdventureDailyLimitContract dailyLimit,
        AdventureCounterContract counters)
        : base(runId, adventureId, role, AdventureRunPhase.Encounter, dailyLimit, counters) { }
}

public sealed record AdventureFightRunState : AdventureRunState
{
    public AdventureFightRunState(
        string runId,
        string adventureId,
        AccountRole role,
        AdventureDailyLimitContract dailyLimit,
        AdventureCounterContract counters)
        : base(runId, adventureId, role, AdventureRunPhase.Fight, dailyLimit, counters) { }
}

public sealed record AdventureChoiceRunState : AdventureRunState
{
    public AdventureChoiceRunState(
        string runId,
        string adventureId,
        AccountRole role,
        AdventureDailyLimitContract dailyLimit,
        AdventureCounterContract counters)
        : base(runId, adventureId, role, AdventureRunPhase.Choice, dailyLimit, counters) { }
}

public sealed record AdventureRewardRunState : AdventureRunState
{
    public AdventureRewardRunState(
        string runId,
        string adventureId,
        AccountRole role,
        AdventureDailyLimitContract dailyLimit,
        AdventureCounterContract counters)
        : base(runId, adventureId, role, AdventureRunPhase.Reward, dailyLimit, counters) { }
}

public sealed record AdventureCompleteRunState : AdventureRunState
{
    public AdventureCompleteRunState(
        string runId,
        string adventureId,
        AccountRole role,
        AdventureDailyLimitContract dailyLimit,
        AdventureCounterContract counters)
        : base(runId, adventureId, role, AdventureRunPhase.Complete, dailyLimit, counters) { }
}

public sealed record AdventureAbortedRunState : AdventureRunState
{
    public AdventureAbortedRunState(
        string runId,
        string adventureId,
        AccountRole role,
        AdventureDailyLimitContract dailyLimit,
        AdventureCounterContract counters,
        AdventureAbortReason reason,
        string message)
        : base(runId, adventureId, role, AdventureRunPhase.Aborted, dailyLimit, counters)
    {
        Reason = reason;
        AbortMessage = message;
    }

    public AdventureAbortReason Reason { get; }
}

/// <summary>Transport-neutral action produced by the kernel.</summary>
public abstract record AdventureKernelAction
{
    protected AdventureKernelAction(AdventureKernelActionKind kind, string adventureId)
    {
        Kind = kind;
        AdventureId = adventureId;
    }

    public AdventureKernelActionKind Kind { get; }
    public string AdventureId { get; }
}

public sealed record AdventureEncounterAction : AdventureKernelAction
{
    public AdventureEncounterAction(string adventureId, string? encounterId = null)
        : base(AdventureKernelActionKind.Encounter, adventureId)
        => EncounterId = encounterId;

    public string? EncounterId { get; }
}

public sealed record AdventureFightAction : AdventureKernelAction
{
    public AdventureFightAction(
        string adventureId,
        string actionId,
        int round,
        string? encounterId = null,
        string? monsterId = null)
        : base(AdventureKernelActionKind.Fight, adventureId)
    {
        ActionId = actionId;
        Round = round;
        EncounterId = encounterId;
        MonsterId = monsterId;
    }

    public string ActionId { get; }
    public int Round { get; }
    public string? EncounterId { get; }
    public string? MonsterId { get; }
}

public sealed record AdventureChoiceAction : AdventureKernelAction
{
    public AdventureChoiceAction(
        string adventureId,
        string choiceId,
        string optionId,
        string? encounterId = null)
        : base(AdventureKernelActionKind.Choice, adventureId)
    {
        ChoiceId = choiceId;
        OptionId = optionId;
        EncounterId = encounterId;
    }

    public string ChoiceId { get; }
    public string OptionId { get; }
    public string? EncounterId { get; }
}

/// <summary>Result of a deterministic state transition.</summary>
public sealed record AdventureKernelDecision
{
    public required AdventureRunState State { get; init; }
    public required AdventureCheckpoint Checkpoint { get; init; }
    public IReadOnlyList<AdventureKernelAction> Actions { get; init; } = Array.Empty<AdventureKernelAction>();
    public AdventureAbortReason? AbortReason { get; init; }
    public bool RequiresRefresh { get; init; }
    public bool WasResumed { get; init; }

    public AdventureKernelAction? NextAction => Actions.Count == 0 ? null : Actions[0];
    public bool IsAborted => State is AdventureAbortedRunState;
    public bool IsComplete => State is AdventureCompleteRunState;
}

/// <summary>Persistent, serializable checkpoint data for a run.</summary>
public sealed record AdventureCheckpoint
{
    public int Version { get; init; } = 1;
    public string RunId { get; init; } = string.Empty;
    public string AdventureId { get; init; } = string.Empty;
    public AccountRole Role { get; init; } = AccountRole.Main;
    public AdventureRunPhase Phase { get; init; } = AdventureRunPhase.Ready;
    public AdventureDailyLimitContract DailyLimit { get; init; } = new();
    public AdventureCounterContract Counters { get; init; } = new();
    public string? EncounterId { get; init; }
    public string? MonsterId { get; init; }
    public int? FightRound { get; init; }
    public string? ChoiceId { get; init; }
    public string? SelectedOption { get; init; }
    public string? LastResponseHash { get; init; }
    public AdventureKernelActionKind? PendingActionKind { get; init; }
    public string? PendingActionId { get; init; }
    public string? PendingChoiceId { get; init; }
    public string? PendingChoiceOption { get; init; }
    public AdventureAbortReason? AbortReason { get; init; }
    public string? AbortMessage { get; init; }

    public static AdventureCheckpoint FromState(AdventureRunState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new AdventureCheckpoint
        {
            RunId = state.RunId,
            AdventureId = state.AdventureId,
            Role = state.Role,
            Phase = state.Phase,
            DailyLimit = state.DailyLimit,
            Counters = state.Counters,
            EncounterId = state.EncounterId,
            MonsterId = state.MonsterId,
            FightRound = state.FightRound,
            ChoiceId = state.ChoiceId,
            SelectedOption = state.SelectedOption,
            LastResponseHash = state.LastResponseHash,
            PendingActionKind = state.PendingAction?.Kind,
            PendingActionId = state.PendingAction is AdventureFightAction fight ? fight.ActionId : null,
            PendingChoiceId = state.PendingAction is AdventureChoiceAction choice ? choice.ChoiceId : null,
            PendingChoiceOption = state.PendingAction is AdventureChoiceAction choiceOption ? choiceOption.OptionId : null,
            AbortReason = state is AdventureAbortedRunState aborted ? aborted.Reason : null,
            AbortMessage = state.AbortMessage
        };
    }

    public AdventureCheckpoint Normalize()
        => this with
        {
            RunId = RunId.Trim(),
            AdventureId = AdventureId.Trim(),
            DailyLimit = (DailyLimit ?? new AdventureDailyLimitContract()).Normalize(),
            Counters = (Counters ?? new AdventureCounterContract()).Normalize()
        };
}

public interface IAdventureRunCheckpointStore
{
    Task SaveAsync(AdventureCheckpoint checkpoint, CancellationToken cancellationToken = default);
    Task<AdventureCheckpoint?> LoadAsync(string runId, CancellationToken cancellationToken = default);
}

/// <summary>Simple process-local checkpoint store for tests and offline hosts.</summary>
public sealed class InMemoryAdventureRunCheckpointStore : IAdventureRunCheckpointStore
{
    private readonly Dictionary<string, AdventureCheckpoint> _checkpoints = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public Task SaveAsync(AdventureCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(checkpoint);
        var value = checkpoint.Normalize();
        if (string.IsNullOrWhiteSpace(value.RunId))
            throw new ArgumentException("Checkpoint run id is required.", nameof(checkpoint));
        lock (_gate) _checkpoints[value.RunId] = value;
        return Task.CompletedTask;
    }

    public Task<AdventureCheckpoint?> LoadAsync(string runId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(runId)) throw new ArgumentException("Run id is required.", nameof(runId));
        lock (_gate)
            return Task.FromResult(_checkpoints.TryGetValue(runId, out var value) ? value : null);
    }
}
