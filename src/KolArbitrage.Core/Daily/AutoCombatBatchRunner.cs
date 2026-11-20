using KolArbitrage.Core.Accounts;
using KolArbitrage.Core.Adventure;

namespace KolArbitrage.Core.Daily;

/// <summary>How one account's fight was set up and run.</summary>
public sealed record AutoCombatAccountPlan
{
    public required AccountRole Role { get; init; }
    /// <summary>The adventure the fight happens in — the location that
    /// holds the target monster.</summary>
    public required string AdventureId { get; init; }
    /// <summary>The encounter id within that adventure.</summary>
    public required string EncounterId { get; init; }
    /// <summary>The policy the fight runs with.</summary>
    public required AutoCombatPolicy Policy { get; init; }
    /// <summary>Maximum rounds before the runner gives up.</summary>
    public int MaxRounds { get; init; } = 20;
    /// <summary>Abort below this percentage of hit points.</summary>
    public int AbortBelowHpPercent { get; init; } = 30;
}

/// <summary>What one account's fight did.</summary>
public sealed record AutoCombatAccountOutcome
{
    public required AccountRole Role { get; init; }
    public required bool Started { get; init; }
    public required string Outcome { get; init; }
    public required string Rationale { get; init; }
    public int Rounds { get; init; }
    public IReadOnlyList<AutoCombatRound> RoundLog { get; init; } = Array.Empty<AutoCombatRound>();
    public string? Error { get; init; }
}

/// <summary>The result of a fight run across one or more accounts.
/// </summary>
public sealed record AutoCombatBatchResult
{
    public required IReadOnlyList<AutoCombatAccountOutcome> Outcomes { get; init; }
    public int StartedCount => Outcomes.Count(o => o.Started);
    public int FailedCount => Outcomes.Count(o => o.Error is not null);
}

/// <summary>
/// Runs fights on several accounts at once.
///
/// Each account gets its own gateway, its own runner and its own policy,
/// but they share one audit log, so the operator sees the whole run in
/// one place. A failure on one account never stops the others — a mallbot
/// that is out of adventures should not block the main account's fight.
/// </summary>
public sealed class AutoCombatBatchRunner
{
    private readonly Func<AccountRole, ManualCombatGateway> _gatewayFactory;

    public AutoCombatBatchRunner(Func<AccountRole, ManualCombatGateway> gatewayFactory)
    {
        _gatewayFactory = gatewayFactory ?? throw new ArgumentNullException(nameof(gatewayFactory));
    }

    /// <summary>Runs one fight per plan, in parallel.</summary>
    public async Task<AutoCombatBatchResult> RunAsync(
        IReadOnlyList<AutoCombatAccountPlan> plans,
        string? approvalToken = null,
        BridgeAuditLog? audit = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plans);
        var tasks = plans.Select(plan => RunOneAsync(plan, approvalToken, audit, cancellationToken));
        var outcomes = await Task.WhenAll(tasks).ConfigureAwait(false);
        return new AutoCombatBatchResult { Outcomes = outcomes };
    }

    private async Task<AutoCombatAccountOutcome> RunOneAsync(
        AutoCombatAccountPlan plan,
        string? approvalToken,
        BridgeAuditLog? audit,
        CancellationToken cancellationToken)
    {
        try
        {
            var gateway = _gatewayFactory(plan.Role);
            var runner = new AutoCombatRunner(gateway);
            var result = await runner.RunAsync(
                plan.Policy,
                combatId: plan.EncounterId,
                adventureId: plan.AdventureId,
                accountId: plan.Role.ToString(),
                maxRounds: plan.MaxRounds,
                abortBelowHpPercent: plan.AbortBelowHpPercent,
                approvalToken: approvalToken,
                audit: audit,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new AutoCombatAccountOutcome
            {
                Role = plan.Role,
                Started = result.Started,
                Outcome = result.Outcome,
                Rationale = result.Rationale,
                Rounds = result.Rounds,
                RoundLog = result.RoundLog
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new AutoCombatAccountOutcome
            {
                Role = plan.Role,
                Started = false,
                Outcome = "failed",
                Rationale = exception.Message,
                Error = exception.Message
            };
        }
    }
}
