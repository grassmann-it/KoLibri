namespace KoLibri.Core.Spiel;

/// <summary>Boundaries for one foreground or scheduled adventure run.</summary>
public sealed record BoundedAdventureRunnerOptions
{
    public int MaxGatewaySteps { get; init; } = 20;
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Most automatic fight rounds in one run (one adventure); 0 = no extra limit.</summary>
    public int MaxFightRounds { get; init; }

    /// <summary>Stop before each automatic fight round when HP is below this percentage (or unknown); 0 = off.</summary>
    public int MinimumHpPercent { get; init; }

    /// <summary>
    /// When set, this policy picks every automatic fight action (skill, item, attack, run) from the
    /// current fight page. If it has no legal action the run stops with the fight left open.
    /// Null keeps the allowlist's own fight action (the desktop default is attack).
    /// </summary>
    public AutoCombatPolicy? CombatPolicy { get; init; }

    /// <summary>
    /// Fresh (current, maximum) HP for the <see cref="MinimumHpPercent"/> check when the fight page does
    /// not show the player's HP (fight.php usually does not; KoL shows it in the charpane). Null result =
    /// unknown, which still stops the run.
    /// </summary>
    public Func<CancellationToken, ValueTask<(int Current, int Maximum)?>>? HitPointsProvider { get; init; }

    public BoundedAdventureRunnerOptions Normalize()
        => this with
        {
            MaxFightRounds = Math.Clamp(MaxFightRounds, 0, 10_000),
            MinimumHpPercent = Math.Clamp(MinimumHpPercent, 0, 100),
            MaxGatewaySteps = Math.Clamp(MaxGatewaySteps, 1, 10_000),
            MaxDuration = MaxDuration <= TimeSpan.Zero ? TimeSpan.FromMinutes(5) : MaxDuration
        };
}

public interface IAdventureConditionSnapshotProvider
{
    ValueTask<AdventureConditionSnapshot> ReadAsync(CancellationToken cancellationToken = default);
}

public enum BoundedAdventureRunStopReason
{
    ReadyToRun,
    Complete,
    ConditionsSatisfied,
    ConditionsBlocked,
    StepLimitReached,
    DeadlineReached,
    GatewayStopped,
    AwaitingManualChoice,
    /// <summary>A choice is open and no policy answered it; see <see cref="BoundedAdventureRunResult.NeedsDecision"/>.</summary>
    NeedsDecision,
    /// <summary>The configured fight round limit was reached; the fight is left open.</summary>
    FightRoundLimitReached,
    /// <summary>HP was below the configured minimum (or unknown) before a fight round; the fight is left open.</summary>
    HpBelowMinimum,
    /// <summary>The combat policy had no legal action for this round; the fight is left open.</summary>
    CombatPolicyStopped
}

public sealed record BoundedAdventureRunResult
{
    public required BoundedAdventureRunStopReason StopReason { get; init; }
    public required IReadOnlyList<AdventureExecutionResult> Steps { get; init; }
    public IReadOnlyList<AdventureConditionResult> Conditions { get; init; }
        = Array.Empty<AdventureConditionResult>();
    /// <summary>Set when <see cref="StopReason"/> is NeedsDecision: the open choice and its options.</summary>
    public NeedsDecisionInfo? NeedsDecision { get; init; }
    /// <summary>Why the run stopped, when the runner has a reason to show (e.g. the combat policy's).</summary>
    public string? StopMessage { get; init; }

    public AdventureExecutionResult? LastStep => Steps.Count == 0 ? null : Steps[^1];
    public bool IsSafeStop => StopReason is not BoundedAdventureRunStopReason.GatewayStopped;
}

/// <summary>
/// Bounded coordinator for a daily run. It never invents actions: all network
/// work is delegated to <see cref="AdventureExecutionGateway"/> and therefore
/// retains its approval, correlation, timeout and checkpoint guarantees.
/// </summary>
public sealed class BoundedAdventureRunner
{
    private readonly AdventureExecutionGateway _gateway;
    private readonly TimeProvider _clock;

    public BoundedAdventureRunner(AdventureExecutionGateway gateway, TimeProvider? clock = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<BoundedAdventureRunResult> RunAsync(
        AdventureRunRequest request,
        AdventureActionAllowlist allowlist,
        IEnumerable<AdventureCondition>? stopWhenSatisfied = null,
        IAdventureConditionSnapshotProvider? snapshotProvider = null,
        BoundedAdventureRunnerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(allowlist);
        var settings = (options ?? new BoundedAdventureRunnerOptions()).Normalize();
        var conditions = (stopWhenSatisfied ?? Array.Empty<AdventureCondition>()).Select(x => x.Normalize()).ToArray();
        var steps = new List<AdventureExecutionResult>();
        var started = _clock.GetUtcNow();
        var fightRounds = 0;
        var combatUsage = new AutoCombatUsage();

        var initial = await EvaluateAsync(conditions, snapshotProvider, cancellationToken).ConfigureAwait(false);
        if (initial.Any(x => x.IsBlocked))
            return Result(BoundedAdventureRunStopReason.ConditionsBlocked, steps, initial);
        if (initial.Count > 0 && initial.All(x => x.IsSatisfied))
            return Result(BoundedAdventureRunStopReason.ConditionsSatisfied, steps, initial);
        if (initial.Count > 0 && snapshotProvider is null)
            return Result(BoundedAdventureRunStopReason.ConditionsBlocked, steps, initial);

        var current = await _gateway.StartAsync(request, allowlist, cancellationToken).ConfigureAwait(false);
        steps.Add(current);
        while (true)
        {
            if (current.IsComplete)
                return Result(BoundedAdventureRunStopReason.Complete, steps, await EvaluateAsync(conditions, snapshotProvider, cancellationToken).ConfigureAwait(false));
            if (current.IsAborted || current.RequiresRefresh || current.IsIndeterminate)
                return Result(BoundedAdventureRunStopReason.GatewayStopped, steps, await EvaluateAsync(conditions, snapshotProvider, cancellationToken).ConfigureAwait(false));
            if (current.State is AdventureChoiceRunState && current.NextAction is null)
            {
                if (allowlist.ChoiceResolver is null)
                    return Result(BoundedAdventureRunStopReason.AwaitingManualChoice, steps, Array.Empty<AdventureConditionResult>());
                var checkpoint = await _gateway.CheckpointStore.LoadAsync(request.RunId, cancellationToken).ConfigureAwait(false);
                return Result(BoundedAdventureRunStopReason.NeedsDecision, steps, Array.Empty<AdventureConditionResult>())
                    with { NeedsDecision = checkpoint.ToNeedsDecision() };
            }
            if (steps.Count >= settings.MaxGatewaySteps)
                return Result(BoundedAdventureRunStopReason.StepLimitReached, steps, await EvaluateAsync(conditions, snapshotProvider, cancellationToken).ConfigureAwait(false));
            if (_clock.GetUtcNow() - started >= settings.MaxDuration)
                return Result(BoundedAdventureRunStopReason.DeadlineReached, steps, await EvaluateAsync(conditions, snapshotProvider, cancellationToken).ConfigureAwait(false));

            if (current.NextAction is AdventureFightAction)
            {
                if (settings.MaxFightRounds > 0 && fightRounds >= settings.MaxFightRounds)
                    return Result(BoundedAdventureRunStopReason.FightRoundLimitReached, steps, Array.Empty<AdventureConditionResult>());
                if (settings.MinimumHpPercent > 0
                    && !await HpAtLeastAsync(current, settings, cancellationToken).ConfigureAwait(false))
                    return Result(BoundedAdventureRunStopReason.HpBelowMinimum, steps, Array.Empty<AdventureConditionResult>());
                fightRounds++;
            }

            // Without conditions there is nothing to evaluate between rounds; the start already checked the snapshot.
            var observed = conditions.Length == 0
                ? Array.Empty<AdventureConditionResult>()
                : await EvaluateAsync(conditions, snapshotProvider, cancellationToken).ConfigureAwait(false);
            if (observed.Any(condition => condition.IsBlocked))
                return Result(BoundedAdventureRunStopReason.ConditionsBlocked, steps, observed);
            if (observed.Count > 0 && observed.All(x => x.IsSatisfied))
                return Result(BoundedAdventureRunStopReason.ConditionsSatisfied, steps, observed);

            if (current.NextAction is AdventureFightAction pending && settings.CombatPolicy is { } policy)
            {
                var (decision, blocked) = await DecideFightAsync(policy, request.RunId, current, pending, combatUsage, cancellationToken)
                    .ConfigureAwait(false);
                if (blocked is not null)
                    return Result(BoundedAdventureRunStopReason.CombatPolicyStopped, steps, Array.Empty<AdventureConditionResult>())
                        with { StopMessage = blocked };
                var (checkpoint, action, policyDecision) = decision!.Value;
                current = action.ActionId == pending.ActionId
                    ? await _gateway.ExecutePendingAsync(request.RunId, allowlist, cancellationToken).ConfigureAwait(false)
                    : await _gateway.ExecuteManualActionAsync(checkpoint, action, allowlist, cancellationToken).ConfigureAwait(false);
                combatUsage = combatUsage.After(policyDecision);
            }
            else
                current = await _gateway.ExecutePendingAsync(request.RunId, allowlist, cancellationToken).ConfigureAwait(false);
            steps.Add(current);
        }
    }

    /// <summary>
    /// Lets the policy pick this round's action from the fight page. Returns the action to send, or a
    /// reason to stop (no observation, no legal action, or the page offers a different action set).
    /// </summary>
    private async ValueTask<((AdventureCheckpoint Checkpoint, AdventureFightAction Action, AutoCombatDecision Decision)? Decision, string? Blocked)>
        DecideFightAsync(AutoCombatPolicy policy, string runId, AdventureExecutionResult current, AdventureFightAction pending,
            AutoCombatUsage usage, CancellationToken cancellationToken)
    {
        var combat = current.Response?.Encounter?.Combat ?? current.Response?.Observation?.Combat;
        if (combat is null)
            return (null, "The fight page could not be read, so the combat policy cannot choose an action.");
        var evaluated = AutoCombatAdventureAdapter.Evaluate(policy, combat, pending.AdventureId, pending.EncounterId, usage);
        if (!evaluated.Allowed)
            return (null, evaluated.BlockReason ?? "The combat policy chose no action for this round.");
        var actionId = AutoCombatAdventureAdapter.LegalAdventureActionId(
            evaluated.PolicyDecision.Action, combat.Normalize().AvailableActions);
        var checkpoint = await _gateway.CheckpointStore.LoadAsync(runId, cancellationToken).ConfigureAwait(false);
        if (actionId is null || checkpoint is null || checkpoint.InFlightRequestId is not null
            || checkpoint.ObservedFightActions is { } offered
                && !offered.Any(candidate => candidate.IsEnabled && candidate.AdventureActionId == actionId))
            return (null, $"The combat policy action '{evaluated.PolicyDecision.Action.Kind} {evaluated.PolicyDecision.Action.Id}' is not offered on this fight page.");
        return ((checkpoint, new AdventureFightAction(pending.AdventureId, actionId, pending.Round,
            pending.EncounterId, pending.MonsterId), evaluated.PolicyDecision), null);
    }

    private static async ValueTask<bool> HpAtLeastAsync(
        AdventureExecutionResult result, BoundedAdventureRunnerOptions settings, CancellationToken cancellationToken)
    {
        var percent = settings.MinimumHpPercent;
        var combat = result.Response?.Encounter?.Combat ?? result.Response?.Observation?.Combat;
        if (combat is { PlayerHp: { } hp, PlayerMaxHp: > 0 and var max })
            return 100L * hp >= (long)percent * max;
        if (settings.HitPointsProvider is not { } provider) return false;
        var fresh = await provider(cancellationToken).ConfigureAwait(false);
        return fresh is { Maximum: > 0 } value && 100L * value.Current >= (long)percent * value.Maximum;
    }

    private async ValueTask<IReadOnlyList<AdventureConditionResult>> EvaluateAsync(
        IReadOnlyList<AdventureCondition> conditions,
        IAdventureConditionSnapshotProvider? provider,
        CancellationToken cancellationToken)
    {
        if (provider is null)
        {
            if (conditions.Count == 0)
                return Array.Empty<AdventureConditionResult>();
            return conditions.Select(condition => new AdventureConditionResult
            {
                Condition = condition,
                Reason = "A fresh condition snapshot is required before an adventure run can start.",
                IsSatisfied = false,
                IsBlocked = true
            }).ToArray();
        }
        var snapshot = await provider.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot.IsStale || !string.IsNullOrWhiteSpace(snapshot.UnavailableReason))
        {
            return new[] { new AdventureConditionResult
            {
                Condition = conditions.FirstOrDefault() ?? new AdventureCondition(),
                IsSatisfied = false,
                IsBlocked = true,
                Reason = snapshot.UnavailableReason ?? "The condition snapshot is stale; refresh before starting."
            } };
        }
        if (conditions.Count == 0)
            return Array.Empty<AdventureConditionResult>();
        return AdventureConditionEvaluator.EvaluateAll(conditions, snapshot);
    }

    private static BoundedAdventureRunResult Result(
        BoundedAdventureRunStopReason reason,
        IReadOnlyList<AdventureExecutionResult> steps,
        IReadOnlyList<AdventureConditionResult> conditions)
        => new() { StopReason = reason, Steps = steps.ToArray(), Conditions = conditions.ToArray() };
}
