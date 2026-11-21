using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using KolArbitrage.Core.Basis;

namespace KolArbitrage.Core.Spiel;

/// <summary>The small, explicit action vocabulary exposed by manual combat.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ManualCombatActionKind
{
    Attack,
    Skill,
    Item,
    Run
}

/// <summary>Compatibility discriminator for callers that call a kind a type.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ManualCombatActionType
{
    Attack,
    Skill,
    Item,
    Run
}

/// <summary>One action a human may explicitly submit for one combat round.</summary>
public abstract record ManualCombatAction
{
    protected ManualCombatAction(ManualCombatActionKind kind, string? id = null, int quantity = 1)
    {
        Kind = kind;
        Id = NormalizeOptional(id);
        Quantity = quantity;
    }

    public ManualCombatActionKind Kind { get; }
    public ManualCombatActionKind ActionKind => Kind;
    public ManualCombatActionType Type => (ManualCombatActionType)Kind;

    /// <summary>Skill id or item id; attack and run intentionally have no id.</summary>
    public string? Id { get; }
    public string? ActionId => Id;
    public string? SkillId => Kind == ManualCombatActionKind.Skill ? Id : null;
    public string? ItemId => Kind == ManualCombatActionKind.Item ? Id : null;
    public int Quantity { get; }

    public virtual void Validate()
    {
        if (Kind is ManualCombatActionKind.Skill or ManualCombatActionKind.Item
            && string.IsNullOrWhiteSpace(Id))
            throw new ArgumentException($"A {Kind.ToString().ToLowerInvariant()} id is required.", nameof(Id));
        if (Quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(Quantity));
    }

    public string Fingerprint(
        string combatId,
        int expectedRound,
        string expectedResponseHash)
    {
        Validate();
        var canonical = string.Join("|",
            combatId.Trim(),
            expectedRound.ToString(CultureInfo.InvariantCulture),
            expectedResponseHash.Trim(),
            Kind,
            Id ?? string.Empty,
            Quantity.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    public static ManualCombatAction Attack()
        => new ManualAttackAction();

    public static ManualCombatAction Skill(string skillId)
        => new ManualSkillAction(skillId);

    public static ManualCombatAction Item(string itemId, int quantity = 1)
        => new ManualItemAction(itemId, quantity);

    public static ManualCombatAction Run()
        => new ManualRunAction();

    /// <summary>Maps the shared read-side combat action into this gateway's typed action.</summary>
    public static ManualCombatAction From(CombatAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action.Kind switch
        {
            CombatActionKind.Attack => Attack(),
            CombatActionKind.Skill => Skill(action.Id ?? action.Value ?? action.Name
                ?? throw new ArgumentException("The shared skill action has no id.", nameof(action))),
            CombatActionKind.Item => Item(action.Id ?? action.Value ?? action.Name
                ?? throw new ArgumentException("The shared item action has no id.", nameof(action))),
            CombatActionKind.Run => Run(),
            _ => throw new ArgumentException("The shared combat action kind is unknown.", nameof(action))
        };
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public record ManualAttackAction : ManualCombatAction
{
    public ManualAttackAction() : base(ManualCombatActionKind.Attack)
    {
    }
}

public record ManualSkillAction : ManualCombatAction
{
    public ManualSkillAction(string skillId) : base(ManualCombatActionKind.Skill, skillId)
    {
    }
}

public record ManualItemAction : ManualCombatAction
{
    public ManualItemAction(string itemId, int quantity = 1)
        : base(ManualCombatActionKind.Item, itemId, quantity)
    {
    }
}

public record ManualRunAction : ManualCombatAction
{
    public ManualRunAction() : base(ManualCombatActionKind.Run)
    {
    }
}

/// <summary>Compatibility names for hosts that put the type before the action.</summary>
public sealed record AttackCombatAction : ManualAttackAction;
public sealed record SkillCombatAction : ManualSkillAction
{
    public SkillCombatAction(string skillId) : base(skillId) { }
}
public sealed record ItemCombatAction : ManualItemAction
{
    public ItemCombatAction(string itemId, int quantity = 1) : base(itemId, quantity) { }
}
public sealed record RunCombatAction : ManualRunAction;

/// <summary>Terminal/active state observed in a combat page.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ManualCombatOutcome
{
    Unknown,
    Active,
    Won,
    Lost,
    Fled,
    Aborted
}

/// <summary>
/// Redacted combat page data consumed by the manual gateway. It deliberately
/// carries no cookies, HTML, or arbitrary form fields.
/// </summary>
public sealed record ManualCombatObservation
{
    public string CombatId { get; init; } = string.Empty;
    public string? AdventureId { get; init; }
    public string? MonsterId { get; init; }
    public string? MonsterName { get; init; }
    public int Round { get; init; }
    public int FightRound { get => Round; init => Round = value; }
    public int PlayerHp { get; init; }
    public int PlayerMaxHp { get; init; }
    public int PlayerMp { get; init; }
    public int PlayerMaxMp { get; init; }
    public int EnemyHp { get; init; }
    public int EnemyMaxHp { get; init; }
    public int EnemyMp { get; init; }
    public int EnemyMaxMp { get; init; }
    public IReadOnlyList<ManualCombatAction> AvailableActions { get; init; }
        = Array.Empty<ManualCombatAction>();
    public IReadOnlyList<ManualCombatAction> LegalActions
    {
        get => AvailableActions;
        init => AvailableActions = value ?? Array.Empty<ManualCombatAction>();
    }
    public IReadOnlyList<string> Effects { get; init; } = Array.Empty<string>();
    public string? Familiar { get; init; }
    public ManualCombatOutcome Outcome { get; init; } = ManualCombatOutcome.Unknown;
    public string? ResponseHash { get; init; }
    public string? Hash { get => ResponseHash; init => ResponseHash = value; }
    public string? Message { get; init; }

    public bool IsTerminal => Outcome is ManualCombatOutcome.Won
        or ManualCombatOutcome.Lost
        or ManualCombatOutcome.Fled
        or ManualCombatOutcome.Aborted;
    public bool IsActive => Outcome is ManualCombatOutcome.Active or ManualCombatOutcome.Unknown && !IsTerminal;

    public ManualCombatObservation Normalize()
        => this with
        {
            CombatId = CombatId.Trim(),
            AdventureId = NormalizeOptional(AdventureId),
            MonsterId = NormalizeOptional(MonsterId),
            MonsterName = NormalizeOptional(MonsterName),
            Round = Math.Max(0, Round),
            PlayerHp = Math.Max(0, PlayerHp),
            PlayerMaxHp = Math.Max(0, PlayerMaxHp),
            PlayerMp = Math.Max(0, PlayerMp),
            PlayerMaxMp = Math.Max(0, PlayerMaxMp),
            EnemyHp = Math.Max(0, EnemyHp),
            EnemyMaxHp = Math.Max(0, EnemyMaxHp),
            EnemyMp = Math.Max(0, EnemyMp),
            EnemyMaxMp = Math.Max(0, EnemyMaxMp),
            AvailableActions = (AvailableActions ?? Array.Empty<ManualCombatAction>()).ToArray(),
            Effects = (Effects ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            ResponseHash = NormalizeOptional(ResponseHash),
            Message = NormalizeOptional(Message)
        };

    public void Validate()
    {
        var value = Normalize();
        if (string.IsNullOrWhiteSpace(value.CombatId))
            throw new ArgumentException("Combat id is required.", nameof(CombatId));
        if (value.Round < 0)
            throw new ArgumentOutOfRangeException(nameof(Round));
        if (string.IsNullOrWhiteSpace(value.ResponseHash))
            throw new ArgumentException("A response hash is required.", nameof(ResponseHash));
        foreach (var action in value.AvailableActions)
        {
            ArgumentNullException.ThrowIfNull(action);
            action.Validate();
        }
    }

    /// <summary>Adapts the shared combat parser result without copying cookies or HTML.</summary>
    public static ManualCombatObservation From(CombatObservation observation, string combatId)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var value = observation.Normalize();
        return new ManualCombatObservation
        {
            CombatId = combatId,
            MonsterId = value.MonsterId,
            MonsterName = value.MonsterName ?? value.Monster,
            Round = value.Round,
            PlayerHp = value.PlayerHp ?? 0,
            PlayerMaxHp = value.PlayerMaxHp ?? 0,
            PlayerMp = value.PlayerMp ?? 0,
            PlayerMaxMp = value.PlayerMaxMp ?? 0,
            EnemyHp = value.EnemyHp ?? 0,
            EnemyMaxHp = value.EnemyMaxHp ?? 0,
            EnemyMp = value.EnemyMp ?? 0,
            EnemyMaxMp = value.EnemyMaxMp ?? 0,
            AvailableActions = (value.AvailableActions ?? Array.Empty<CombatAction>())
                .Where(x => x is not null && x.IsEnabled)
                .Select(x => ManualCombatAction.From(x!))
                .ToArray(),
            Effects = (value.Effects ?? Array.Empty<CombatEffect>())
                .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Name))
                .Select(x => x.Name)
                .ToArray(),
            Familiar = value.Familiar,
            Outcome = value.Outcome switch
            {
                CombatOutcome.Ongoing => ManualCombatOutcome.Active,
                CombatOutcome.Victory => ManualCombatOutcome.Won,
                CombatOutcome.Defeat => ManualCombatOutcome.Lost,
                CombatOutcome.Fled => ManualCombatOutcome.Fled,
                _ => ManualCombatOutcome.Unknown
            },
            ResponseHash = value.ResponseHash,
            Message = value.Advisories is { Count: > 0 }
                ? string.Join(" ", value.Advisories)
                : null
        };
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public enum ManualCombatAbortReason
{
    None = 0,
    InvalidRequest,
    InvalidObservation,
    MissingObservation,
    CombatNotFound,
    TerminalCombat,
    ActionNotLegal,
    ExpectedRoundMismatch,
    ExpectedResponseHashMismatch,
    StalePreview,
    PreviewExpired,
    ApprovalRequired,
    ApprovalRejected,
    LedgerRejected,
    CheckpointConflict,
    InFlightRequest,
    IndeterminatePost,
    TransportFailure,
    ResponseCorrelationMismatch,
    DuplicateResponse,
    InvalidTransportResponse,
    SessionInvalid,
    DryRun,
    CommitRejected,
    VerificationFailed
}

public enum ManualCombatStatus
{
    Inspected,
    Previewed,
    Approved,
    Committed,
    RequiresRefresh,
    Indeterminate,
    Aborted,
    Complete
}

public sealed record ManualCombatPolicy
{
    public bool DryRun { get; init; }
    public bool RequireApproval { get; init; } = true;
    public TimeSpan PreviewLifetime { get; init; } = TimeSpan.FromMinutes(5);

    public void Validate()
    {
        if (PreviewLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(PreviewLifetime));
    }
}

/// <summary>Read request used by inspect and refresh.</summary>
public sealed record ManualCombatInspectRequest
{
    public string CombatId { get; init; } = string.Empty;
    public string? AdventureId { get; init; }
    public string? RequestId { get; init; }
    public int? ExpectedRound { get; init; }
    public string? ExpectedResponseHash { get; init; }

    public ManualCombatInspectRequest() { }

    public ManualCombatInspectRequest(
        string combatId,
        string? adventureId = null,
        int? expectedRound = null,
        string? expectedResponseHash = null)
    {
        CombatId = combatId;
        AdventureId = adventureId;
        ExpectedRound = expectedRound;
        ExpectedResponseHash = expectedResponseHash;
    }

    public ManualCombatInspectRequest Normalize()
        => this with
        {
            CombatId = CombatId.Trim(),
            AdventureId = NormalizeOptional(AdventureId),
            RequestId = NormalizeOptional(RequestId),
            ExpectedResponseHash = NormalizeOptional(ExpectedResponseHash)
        };

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(CombatId))
            throw new ArgumentException("Combat id is required.", nameof(CombatId));
        if (ExpectedRound is < 0)
            throw new ArgumentOutOfRangeException(nameof(ExpectedRound));
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>One typed action bound to one observed round and response hash.</summary>
public sealed record ManualCombatRequest
{
    public string CombatId { get; init; } = string.Empty;
    public string? AdventureId { get; init; }
    public ManualCombatAction Action { get; init; } = ManualCombatAction.Attack();
    public int ExpectedRound { get; init; }
    public string ExpectedResponseHash { get; init; } = string.Empty;
    public string AccountId { get; init; } = "default";
    public string? OperationId { get; init; }
    public string? IdempotencyKey { get; init; }

    public ManualCombatRequest() { }

    public ManualCombatRequest(
        string combatId,
        ManualCombatAction action,
        int expectedRound,
        string expectedResponseHash,
        string? adventureId = null,
        string accountId = "default",
        string? operationId = null,
        string? idempotencyKey = null)
    {
        CombatId = combatId;
        Action = action;
        ExpectedRound = expectedRound;
        ExpectedResponseHash = expectedResponseHash;
        AdventureId = adventureId;
        AccountId = accountId;
        OperationId = operationId;
        IdempotencyKey = idempotencyKey;
    }

    public string ActionFingerprint => Action.Fingerprint(
        CombatId, ExpectedRound, ExpectedResponseHash);

    public ManualCombatRequest Normalize()
        => this with
        {
            CombatId = CombatId.Trim(),
            AdventureId = NormalizeOptional(AdventureId),
            AccountId = string.IsNullOrWhiteSpace(AccountId) ? "default" : AccountId.Trim(),
            ExpectedResponseHash = ExpectedResponseHash.Trim(),
            OperationId = NormalizeOptional(OperationId),
            IdempotencyKey = NormalizeOptional(IdempotencyKey)
        };

    public void Validate()
    {
        var value = Normalize();
        if (string.IsNullOrWhiteSpace(value.CombatId))
            throw new ArgumentException("Combat id is required.", nameof(CombatId));
        if (value.ExpectedRound < 0)
            throw new ArgumentOutOfRangeException(nameof(ExpectedRound));
        if (string.IsNullOrWhiteSpace(value.ExpectedResponseHash))
            throw new ArgumentException("Expected response hash is required.", nameof(ExpectedResponseHash));
        ArgumentNullException.ThrowIfNull(value.Action);
        value.Action.Validate();
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record ManualCombatPreview
{
    public string OperationId { get; init; } = string.Empty;
    public string ActionId { get => OperationId; init => OperationId = value; }
    public required ManualCombatRequest Request { get; init; }
    public required ManualCombatObservation Observation { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public bool RequiresApproval { get; init; } = true;
    public bool IsExisting { get; init; }
    public string Fingerprint => Request.ActionFingerprint;
    public IReadOnlyList<ManualCombatAbortReason> AbortReasons { get; init; }
        = Array.Empty<ManualCombatAbortReason>();
    public IReadOnlyList<string> Messages { get; init; } = Array.Empty<string>();
    public bool IsReady => AbortReasons.Count == 0;
    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;
    public ManualCombatAbortReason? PrimaryAbortReason
        => AbortReasons.Count == 0 ? null : AbortReasons[0];
}

public sealed record ManualCombatInspectionResult
{
    public required ManualCombatStatus Status { get; init; }
    public ManualCombatObservation? Observation { get; init; }
    public ManualCombatCheckpoint? Checkpoint { get; init; }
    public IReadOnlyList<ManualCombatAbortReason> AbortReasons { get; init; }
        = Array.Empty<ManualCombatAbortReason>();
    public IReadOnlyList<string> Messages { get; init; } = Array.Empty<string>();
    public bool Succeeded => Status is ManualCombatStatus.Inspected or ManualCombatStatus.Complete;
    public bool IsAborted => Status == ManualCombatStatus.Aborted;
    public ManualCombatAbortReason? PrimaryAbortReason
        => AbortReasons.Count == 0 ? null : AbortReasons[0];
}

public sealed record ManualCombatExecutionResult
{
    public required ManualCombatStatus Status { get; init; }
    public ManualCombatPreview? Preview { get; init; }
    public ManualCombatObservation? Before { get; init; }
    public ManualCombatObservation? After { get; init; }
    public ManualCombatTransportResponse? Response { get; init; }
    public ManualCombatCheckpoint? Checkpoint { get; init; }
    public string? RequestId { get; init; }
    public IReadOnlyList<ManualCombatAbortReason> AbortReasons { get; init; }
        = Array.Empty<ManualCombatAbortReason>();
    public IReadOnlyList<string> Messages { get; init; } = Array.Empty<string>();
    public bool Committed => Status is ManualCombatStatus.Committed or ManualCombatStatus.Complete;
    public bool Succeeded => Committed;
    public bool RequiresRefresh => Status is ManualCombatStatus.RequiresRefresh or ManualCombatStatus.Indeterminate;
    public bool IsIndeterminate => Status == ManualCombatStatus.Indeterminate;
    public bool IsAborted => Status == ManualCombatStatus.Aborted;
    public ManualCombatAbortReason? PrimaryAbortReason
        => AbortReasons.Count == 0 ? null : AbortReasons[0];
}

/// <summary>Transport request. Fields are generated from the typed action.</summary>
public sealed record ManualCombatTransportRequest
{
    public string RequestId { get; init; } = string.Empty;
    public string CorrelationId { get => RequestId; init => RequestId = value; }
    public string CombatId { get; init; } = string.Empty;
    public string? AdventureId { get; init; }
    public ManualCombatAction? Action { get; init; }
    public int? ExpectedRound { get; init; }
    public string? ExpectedResponseHash { get; init; }
    public string ActionFingerprint { get; init; } = string.Empty;
    public string? ApprovalToken { get; init; }
    public bool IsRefresh { get; init; }
    public IReadOnlyDictionary<string, string> Fields { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    public bool IsMutating => !IsRefresh && Action is not null;
}

/// <summary>Raw response returned by the injected manual-combat transport.</summary>
public sealed record ManualCombatTransportResponse
{
    public string RequestId { get; init; } = string.Empty;
    public string CorrelationId { get => RequestId; init => RequestId = value; }
    public string? CombatId { get; init; }
    public string? AdventureId { get; init; }
    public ManualCombatObservation? Observation { get; init; }
    /// <summary>Optional shared parser observation; Observation remains the gateway-native form.</summary>
    public CombatObservation? Combat { get; init; }
    public string? ResponseHash { get; init; }
    public bool? Accepted { get; init; }
    public string? Message { get; init; }
    public string? RequestFingerprint { get; init; }

    public string? EffectiveResponseHash => ResponseHash ?? Observation?.ResponseHash ?? Combat?.ResponseHash;
}

public interface IManualCombatTransport
{
    Task<ManualCombatTransportResponse> InspectAsync(
        ManualCombatTransportRequest request,
        CancellationToken cancellationToken = default)
        => ReadAsync(request, cancellationToken);

    Task<ManualCombatTransportResponse> ReadAsync(
        ManualCombatTransportRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The manual combat transport does not implement InspectAsync.");

    Task<ManualCombatTransportResponse> SubmitAsync(
        ManualCombatTransportRequest request,
        CancellationToken cancellationToken = default)
        => SubmitActionAsync(request, cancellationToken);

    Task<ManualCombatTransportResponse> SubmitActionAsync(
        ManualCombatTransportRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The manual combat transport does not implement SubmitAsync.");

    Task<ManualCombatTransportResponse> RefreshAsync(
        ManualCombatTransportRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The manual combat transport does not implement RefreshAsync.");
}

public sealed record ManualCombatApprovalContext(
    ManualCombatPreview Preview,
    ManualCombatTransportRequest Request);

public sealed record ManualCombatApprovalDecision
{
    public bool Approved { get; init; }
    public string? ApprovalToken { get; init; }
    public string? Reason { get; init; }

    public static ManualCombatApprovalDecision Allow(string? token = null)
        => new() { Approved = true, ApprovalToken = token };

    public static ManualCombatApprovalDecision Deny(string reason)
        => new() { Approved = false, Reason = reason };
}

public interface IManualCombatApprovalHook
{
    ValueTask<ManualCombatApprovalDecision> ApproveAsync(
        ManualCombatApprovalContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Preview/execute boundary used by CLI and Desktop hosts.</summary>
public interface IManualCombatGateway
{
    Task<ManualCombatInspectionResult> InspectAsync(
        string combatId,
        CancellationToken cancellationToken = default);

    Task<ManualCombatPreview> PreviewAsync(
        ManualCombatRequest request,
        CancellationToken cancellationToken = default);

    Task<ManualCombatExecutionResult> ExecuteAsync(
        ManualCombatPreview preview,
        string? approvalToken = null,
        CancellationToken cancellationToken = default);

    Task<ManualCombatInspectionResult> RefreshAsync(
        string combatId,
        CancellationToken cancellationToken = default);
}

public sealed class DelegateManualCombatApprovalHook : IManualCombatApprovalHook
{
    private readonly Func<ManualCombatApprovalContext, CancellationToken, ValueTask<ManualCombatApprovalDecision>> _callback;

    public DelegateManualCombatApprovalHook(
        Func<ManualCombatApprovalContext, CancellationToken, ValueTask<ManualCombatApprovalDecision>> callback)
        => _callback = callback ?? throw new ArgumentNullException(nameof(callback));

    public ValueTask<ManualCombatApprovalDecision> ApproveAsync(
        ManualCombatApprovalContext context,
        CancellationToken cancellationToken = default)
        => _callback(context, cancellationToken);
}

/// <summary>Durable marker used to prevent blind replay after a timeout.</summary>
public sealed record ManualCombatCheckpoint
{
    public long Revision { get; init; }
    public string CombatId { get; init; } = string.Empty;
    public string? AdventureId { get; init; }
    public int Round { get; init; }
    public string? ResponseHash { get; init; }
    public ManualCombatObservation? Observation { get; init; }
    public string? InFlightRequestId { get; init; }
    public string? InFlightFingerprint { get; init; }
    public string? LastRequestId { get; init; }
    public string? AbortMessage { get; init; }
    public ManualCombatAbortReason? AbortReason { get; init; }
    public IReadOnlyList<string> ResponseHashes { get; init; } = Array.Empty<string>();
    public bool IsTerminal => Observation?.IsTerminal == true || AbortReason.HasValue;
    public bool RequiresRefresh => !string.IsNullOrWhiteSpace(InFlightRequestId);

    public ManualCombatCheckpoint Normalize()
        => this with
        {
            CombatId = CombatId.Trim(),
            AdventureId = NormalizeOptional(AdventureId),
            Round = Math.Max(0, Round),
            ResponseHash = NormalizeOptional(ResponseHash),
            ResponseHashes = (ResponseHashes ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray()
        };

    public static ManualCombatCheckpoint FromObservation(ManualCombatObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var value = observation.Normalize();
        return new ManualCombatCheckpoint
        {
            CombatId = value.CombatId,
            AdventureId = value.AdventureId,
            Round = value.Round,
            ResponseHash = value.ResponseHash,
            Observation = value,
            ResponseHashes = string.IsNullOrWhiteSpace(value.ResponseHash)
                ? Array.Empty<string>()
                : new[] { value.ResponseHash! }
        };
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public interface IManualCombatCheckpointStore
{
    Task SaveAsync(ManualCombatCheckpoint checkpoint, CancellationToken cancellationToken = default);
    Task<ManualCombatCheckpoint?> LoadAsync(string combatId, CancellationToken cancellationToken = default);
}

public interface IManualCombatCheckpointCasStore : IManualCombatCheckpointStore
{
    Task<ManualCombatCheckpointCasResult> CompareAndSwapAsync(
        string combatId,
        ManualCombatCheckpoint? expected,
        ManualCombatCheckpoint replacement,
        CancellationToken cancellationToken = default);
}

public sealed record ManualCombatCheckpointCasResult
{
    public bool Succeeded { get; init; }
    public ManualCombatCheckpoint? Current { get; init; }
    public bool Conflict => !Succeeded;
}

public sealed class InMemoryManualCombatCheckpointStore : IManualCombatCheckpointCasStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ManualCombatCheckpoint> _checkpoints = new(StringComparer.Ordinal);

    public Task SaveAsync(ManualCombatCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(checkpoint);
        var value = checkpoint.Normalize();
        if (string.IsNullOrWhiteSpace(value.CombatId))
            throw new ArgumentException("Checkpoint combat id is required.", nameof(checkpoint));
        lock (_gate)
            _checkpoints[value.CombatId] = value with { Revision = value.Revision <= 0 ? 1 : value.Revision };
        return Task.CompletedTask;
    }

    public Task<ManualCombatCheckpoint?> LoadAsync(string combatId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(combatId)) throw new ArgumentException("Combat id is required.", nameof(combatId));
        lock (_gate)
            return Task.FromResult(_checkpoints.TryGetValue(combatId.Trim(), out var value) ? value : null);
    }

    public Task<ManualCombatCheckpointCasResult> CompareAndSwapAsync(
        string combatId,
        ManualCombatCheckpoint? expected,
        ManualCombatCheckpoint replacement,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(replacement);
        var id = string.IsNullOrWhiteSpace(combatId) ? throw new ArgumentException("Combat id is required.", nameof(combatId)) : combatId.Trim();
        var value = replacement.Normalize();
        if (!string.Equals(id, value.CombatId, StringComparison.Ordinal))
            throw new ArgumentException("The checkpoint combat id does not match the CAS key.", nameof(replacement));
        lock (_gate)
        {
            _checkpoints.TryGetValue(id, out var current);
            if (!Same(current, expected))
                return Task.FromResult(new ManualCombatCheckpointCasResult { Succeeded = false, Current = current });
            var saved = value with { Revision = (current?.Revision ?? 0) + 1 };
            _checkpoints[id] = saved;
            return Task.FromResult(new ManualCombatCheckpointCasResult { Succeeded = true, Current = saved });
        }
    }

    private static bool Same(ManualCombatCheckpoint? left, ManualCombatCheckpoint? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        var leftValue = left.Normalize() with { Revision = 0 };
        var rightValue = right.Normalize() with { Revision = 0 };
        return leftValue.Equals(rightValue);
    }
}

/// <summary>Small optional adapter over the operation approval/ledger service.</summary>
public interface IManualCombatApprovalLedger : IOperationApprovalLedger
{
}
