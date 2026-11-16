using KolArbitrage.Core.Accounts;

namespace KolArbitrage.Core.Adventure;

/// <summary>
/// The actions that the daily adventure worker can put in a plan.
/// The planner only emits actions; an account adapter is responsible for executing them.
/// </summary>
public enum AdventureActionKind
{
    ConsumeLuckySurpriseEggs,
    RunSkeletonOfCrimbosPast,
    RunAdventure
}

/// <summary>Encounter types an eventual adventure adapter must expose before execution.</summary>
public enum AdventureEncounterKind
{
    Unknown,
    Fight,
    Choice,
    Reward,
    Complete
}

/// <summary>
/// Checkpoint phases used by a future adventure runner. The planner and the
/// current CLI never advance this state or send a fight/choice request; they
/// only consume read snapshots. Unknown and blocked are first-class values so
/// an adapter can stop for manual review instead of guessing an option.
/// </summary>
public enum AdventureExecutionPhase
{
    Idle,
    Adventure,
    Fight,
    Choice,
    Reward,
    Complete,
    Blocked,
    Unknown
}

/// <summary>Read-only encounter state; no fight or choice request is sent by this model.</summary>
public sealed record AdventureEncounterState(
    string AdventureId,
    AdventureEncounterKind Kind,
    string? Location,
    string? ChoiceId,
    IReadOnlyList<string> AvailableChoices,
    bool CanContinue,
    IReadOnlyList<string> Advisories,
    int? FightRound = null,
    string? ResponseHash = null,
    int RetryCount = 0);

/// <summary>
/// Durable, read-before-act state for a future KoLmafia-style adventure
/// runner. Response hashes and retry counts make timeout recovery explicit;
/// <see cref="NeedsManual"/> prevents a runner from blindly retrying a
/// non-idempotent choice or fight request.
/// </summary>
public sealed record AdventureExecutionState
{
    public AccountRole Role { get; init; } = AccountRole.Main;
    public AdventureExecutionPhase Phase { get; init; } = AdventureExecutionPhase.Idle;
    public string? AdventureId { get; init; }
    public string? LocationId { get; init; }
    public AdventureEncounterKind EncounterKind { get; init; } = AdventureEncounterKind.Unknown;
    public string? ChoiceId { get; init; }
    public int? SelectedOption { get; init; }
    public int FightRound { get; init; }
    public int AdventuresBefore { get; init; }
    public int AdventuresAfter { get; init; }
    public int RetryCount { get; init; }
    public string? LastResponseHash { get; init; }
    public bool NeedsManual { get; init; }
    public string? BlockReason { get; init; }

    public AdventureExecutionState Normalize()
        => this with
        {
            FightRound = Math.Max(0, FightRound),
            AdventuresBefore = Math.Max(0, AdventuresBefore),
            AdventuresAfter = Math.Max(0, AdventuresAfter),
            RetryCount = Math.Max(0, RetryCount)
        };
}

/// <summary>Read boundary for a future KoL adventure adapter.</summary>
public interface IAdventureStateReader
{
    Task<AdventureEncounterState> ReadEncounterAsync(CancellationToken cancellationToken = default);
}

/// <summary>Persistence boundary for checkpoints; implementations must not issue KoL requests.</summary>
public interface IAdventureCheckpointStore
{
    Task SaveAsync(AdventureExecutionState state, CancellationToken cancellationToken = default);
    Task<AdventureExecutionState?> LoadAsync(AccountRole role, CancellationToken cancellationToken = default);
}

public enum DailyAdventurePlanStatus
{
    TargetReached,
    Ready,
    PartiallyReady,
    BlockedNoAdventureTurns,
    BlockedNoYield
}

/// <summary>
/// The subset of account state needed to build a safe daily plan.
/// Values are snapshots: executing a plan must refresh the account before each mutating action.
/// </summary>
public sealed record AdventureState
{
    public int Knucklebones { get; init; }
    public int AdventureTurnsRemaining { get; init; }
    public int LuckySurpriseEggs { get; init; }
    public int ForeignBribeBags { get; init; }
    public int PirateEncryptionKeys { get; init; }
    public int ElfGuardPresents { get; init; }
    public int CrimbucaneerBooty { get; init; }

    /// <summary>True when the Skeleton of Crimbos Past adventure is currently available.</summary>
    public bool SkeletonOfCrimbosPastAvailable { get; init; }

    public AdventureState Normalize()
        => this with
        {
            Knucklebones = Math.Max(0, Knucklebones),
            AdventureTurnsRemaining = Math.Max(0, AdventureTurnsRemaining),
            LuckySurpriseEggs = Math.Max(0, LuckySurpriseEggs),
            ForeignBribeBags = Math.Max(0, ForeignBribeBags),
            PirateEncryptionKeys = Math.Max(0, PirateEncryptionKeys),
            ElfGuardPresents = Math.Max(0, ElfGuardPresents),
            CrimbucaneerBooty = Math.Max(0, CrimbucaneerBooty)
        };
}

public sealed record AdventurePlannerOptions
{
    public int DailyKnucklebonesTarget { get; init; } = 100;
    /// <summary>
    /// Expected drop rate, not a guaranteed reward. A decimal is intentional: local
    /// KolMafia logs commonly show about one knucklebone per two combats.
    /// </summary>
    public decimal ExpectedKnucklebonesPerAdventure { get; init; } = 0.5m;
    public decimal ExpectedKnucklebonesPerLuckySurpriseEgg { get; init; }
    /// <summary>Lucky Surprise Eggs grant one adventure in the current game logs.</summary>
    public int ExpectedAdventureTurnsPerLuckySurpriseEgg { get; init; } = 1;
    public bool ConsumeLuckySurpriseEggs { get; init; } = true;
    public bool ConsumeEggsWhenTargetAlreadyReached { get; init; } = true;
    /// <summary>Daily fullness makes 15 a safe default; adapters can lower this for a snapshot.</summary>
    public int MaxLuckySurpriseEggsPerPlan { get; init; } = 15;
    public int MaxAdventureRunsPerPlan { get; init; } = 500;

    /// <summary>
    /// Keeps keys in inventory for Crimbo crafting. This is an advisory setting for the
    /// resulting plan; no key spending action is emitted by the daily adventure planner.
    /// </summary>
    public bool PreservePirateEncryptionKeys { get; init; } = true;

    /// <summary>
    /// Foreign bribe bags are surfaced as an advisory for the pricing/research worker.
    /// They are not opened implicitly by this planner because opening them is a separate
    /// DLC purchase decision.
    /// </summary>
    public bool DeferForeignBribeBagsToResearch { get; init; } = true;

    public AdventurePlannerOptions Normalize()
        => this with
        {
            DailyKnucklebonesTarget = Math.Max(0, DailyKnucklebonesTarget),
            ExpectedKnucklebonesPerAdventure = Math.Max(0m, ExpectedKnucklebonesPerAdventure),
            ExpectedKnucklebonesPerLuckySurpriseEgg = Math.Max(0m, ExpectedKnucklebonesPerLuckySurpriseEgg),
            ExpectedAdventureTurnsPerLuckySurpriseEgg = Math.Max(0, ExpectedAdventureTurnsPerLuckySurpriseEgg),
            MaxLuckySurpriseEggsPerPlan = Math.Max(0, MaxLuckySurpriseEggsPerPlan),
            MaxAdventureRunsPerPlan = Math.Max(0, MaxAdventureRunsPerPlan)
        };
}

public sealed record AdventureStep(
    int Sequence,
    AdventureActionKind Action,
    int Quantity,
    decimal ExpectedKnucklebonesDelta,
    string Reason,
    bool RequiresFreshState = true,
    int ExpectedAdventureTurnsDelta = 0);

public sealed record DailyAdventurePlan
{
    public required DateOnly PlanDate { get; init; }
    public required DailyAdventurePlanStatus Status { get; init; }
    public required int TargetKnucklebones { get; init; }
    public required int StartingKnucklebones { get; init; }
    public required decimal ProjectedKnucklebones { get; init; }
    public required IReadOnlyList<AdventureStep> Steps { get; init; }
    public required IReadOnlyList<string> Advisories { get; init; }

    public decimal RemainingToTarget => Math.Max(0m, TargetKnucklebones - ProjectedKnucklebones);
    public bool IsActionable => Steps.Count > 0;
}
