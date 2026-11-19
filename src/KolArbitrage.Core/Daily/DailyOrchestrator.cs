using KolArbitrage.Core.Accounts;
using KolArbitrage.Core.Adventure;

namespace KolArbitrage.Core.Daily;

/// <summary>
/// One role's plan in an orchestrated multi-account run. The snapshot is
/// per-role so each account can have its own meat, leaves, permits and
/// purchase-budget.
/// </summary>
public sealed record RoleDailyPlan(
    AccountRole Role,
    DailyRoutineSnapshot Snapshot,
    DailyRoutineReport Preview);

/// <summary>
/// Aggregated output of an orchestrated daily run across one or more
/// accounts. The operator gets a single preview before --execute and a
/// single audit afterwards.
/// </summary>
public sealed record DailyOrchestrationReport
{
    public required DateTimeOffset GeneratedAt { get; init; }
    public required IReadOnlyList<RoleDailyPlan> Plans { get; init; }
    public required int TotalApprovableSteps { get; init; }
}

/// <summary>
/// Live result of an orchestrated run. Each role keeps its own
/// <see cref="DailyRoutineExecutionResult"/>; the caller decides whether
/// to halt on the first failure or collect everything.
/// </summary>
public sealed record DailyOrchestrationResult
{
    public required IReadOnlyList<(RoleDailyPlan Plan, DailyRoutineExecutionResult Execution)> Results { get; init; }
    public required int ExecutedRoleCount { get; init; }
    public required int SucceededRoleCount { get; init; }
    public required int FailedRoleCount { get; init; }
}

/// <summary>
/// Per-role override of the routine snapshot. Anything left null falls
/// back to the orchestrator default snapshot, which is itself driven by
/// CLI flags or the supplied <see cref="DailyRoutineSnapshot"/>.
/// </summary>
public sealed record RoleOverride(
    AccountRole Role,
    DailyRoutineSnapshot Snapshot);

/// <summary>
/// Builds and executes the daily routine across multiple accounts. The
/// orchestrator owns no transport of its own; each role delegates to a
/// <see cref="DailyRoutineExecutor"/> built from the host's
/// gateway-factory and approval-token. This keeps the surface small and
/// lets callers plug in their own mocking layer for tests.
/// </summary>
public sealed class DailyOrchestrator
{
    private readonly Func<AccountRole, AdventureExecutionGateway> _gatewayFactory;
    private readonly string? _approvalToken;
    private readonly TimeProvider _clock;

    public DailyOrchestrator(
        Func<AccountRole, AdventureExecutionGateway> gatewayFactory,
        string? approvalToken = null,
        TimeProvider? clock = null)
    {
        _gatewayFactory = gatewayFactory ?? throw new ArgumentNullException(nameof(gatewayFactory));
        _approvalToken = approvalToken;
        _clock = clock ?? TimeProvider.System;
    }

    public bool CanExecute => !string.IsNullOrWhiteSpace(_approvalToken);

    public DailyOrchestrationReport BuildPlan(
        IEnumerable<RoleOverride> overrides,
        DailyRoutineSnapshot defaultSnapshot)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        ArgumentNullException.ThrowIfNull(defaultSnapshot);
        var plans = overrides.Select(o =>
        {
            var merged = MergeSnapshots(defaultSnapshot, o.Snapshot);
            var preview = new DailyRoutineRunner().Run(merged);
            return new RoleDailyPlan(o.Role, merged, preview);
        }).ToArray();
        var totalSteps = plans.Sum(p => p.Preview.ApprovableSteps.Count);
        return new DailyOrchestrationReport
        {
            GeneratedAt = _clock.GetUtcNow(),
            Plans = plans,
            TotalApprovableSteps = totalSteps
        };
    }

    public async Task<DailyOrchestrationResult> ExecuteAsync(
        DailyOrchestrationReport report,
        BoundedChoiceChainOptions? options = null,
        bool failFast = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!CanExecute)
        {
            return new DailyOrchestrationResult
            {
                Results = report.Plans.Select(p => (p, new DailyRoutineExecutionResult
                {
                    Executed = false,
                    Role = p.Role,
                    Steps = Array.Empty<DailyStepOutcome>(),
                    AuditPath = null
                })).ToArray(),
                ExecutedRoleCount = 0,
                SucceededRoleCount = 0,
                FailedRoleCount = 0
            };
        }

        var tasks = report.Plans.Select(async plan =>
        {
            var executor = new DailyRoutineExecutor(_gatewayFactory, _approvalToken, _clock);
            var execResult = await executor.ExecuteAsync(plan.Role, plan.Preview, options, cancellationToken)
                .ConfigureAwait(false);
            if (failFast && !execResult.Executed)
                throw new InvalidOperationException(
                    $"Role {plan.Role} did not execute (token missing or no steps).");
            return (Plan: plan, Execution: execResult);
        });
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        return new DailyOrchestrationResult
        {
            Results = results,
            ExecutedRoleCount = results.Count(r => r.Execution.Executed),
            SucceededRoleCount = results.Count(r => r.Execution.Steps.Count > 0
                && r.Execution.Steps.All(s => s.Status != AdventureExecutionStatus.Aborted)),
            FailedRoleCount = results.Count(r => r.Execution.Steps.Any(s => s.Status == AdventureExecutionStatus.Aborted))
        };
    }

    private static DailyRoutineSnapshot MergeSnapshots(
        DailyRoutineSnapshot defaults,
        DailyRoutineSnapshot overrideSnapshot)
    {
        // The override replaces fields it set explicitly; defaults fill the
        // rest. A field is considered "set" when it differs from the type's
        // zero/default value, which matches the CLI parsing contract.
        return new DailyRoutineSnapshot
        {
            CampfireLeaves = overrideSnapshot.CampfireLeaves != 0 ? overrideSnapshot.CampfireLeaves : defaults.CampfireLeaves,
            CampfireReserve = overrideSnapshot.CampfireReserve != 3 ? overrideSnapshot.CampfireReserve : defaults.CampfireReserve,
            DayShortnerPrice = overrideSnapshot.DayShortnerPrice != long.MaxValue ? overrideSnapshot.DayShortnerPrice : defaults.DayShortnerPrice,
            MainMeat = overrideSnapshot.MainMeat != 0 ? overrideSnapshot.MainMeat : defaults.MainMeat,
            HermitPermits = overrideSnapshot.HermitPermits != 0 ? overrideSnapshot.HermitPermits : defaults.HermitPermits,
            Clovers = overrideSnapshot.Clovers != 0 ? overrideSnapshot.Clovers : defaults.Clovers,
            AdventureReserveValue = overrideSnapshot.AdventureReserveValue != 0 ? overrideSnapshot.AdventureReserveValue : defaults.AdventureReserveValue,
            CloverFloorValue = overrideSnapshot.CloverFloorValue != 0 ? overrideSnapshot.CloverFloorValue : defaults.CloverFloorValue,
            ExpectedCloverSaleValue = overrideSnapshot.ExpectedCloverSaleValue != 0 ? overrideSnapshot.ExpectedCloverSaleValue : defaults.ExpectedCloverSaleValue,
            WishBuyQuantity = overrideSnapshot.WishBuyQuantity != 0 ? overrideSnapshot.WishBuyQuantity : defaults.WishBuyQuantity,
            WishUnitPrice = overrideSnapshot.WishUnitPrice != 0 ? overrideSnapshot.WishUnitPrice : defaults.WishUnitPrice,
            AlreadyPulledItemIds = overrideSnapshot.AlreadyPulledItemIds.Count > 0 ? overrideSnapshot.AlreadyPulledItemIds : defaults.AlreadyPulledItemIds,
            OwnedMachines = overrideSnapshot.OwnedMachines != 0 ? overrideSnapshot.OwnedMachines : defaults.OwnedMachines,
            MallPricePerMachine = overrideSnapshot.MallPricePerMachine != 0 ? overrideSnapshot.MallPricePerMachine : defaults.MallPricePerMachine,
            PricegunMedianBaconPrice = overrideSnapshot.PricegunMedianBaconPrice != 0 ? overrideSnapshot.PricegunMedianBaconPrice : defaults.PricegunMedianBaconPrice,
            MallbotMeat = overrideSnapshot.MallbotMeat != 0 ? overrideSnapshot.MallbotMeat : defaults.MallbotMeat,
            ForeignBribeBagPrice = overrideSnapshot.ForeignBribeBagPrice != long.MaxValue ? overrideSnapshot.ForeignBribeBagPrice : defaults.ForeignBribeBagPrice,
            ForeignBribeBagStock = overrideSnapshot.ForeignBribeBagStock != 0 ? overrideSnapshot.ForeignBribeBagStock : defaults.ForeignBribeBagStock,
            PilotBagQuantity = overrideSnapshot.PilotBagQuantity != 40 ? overrideSnapshot.PilotBagQuantity : defaults.PilotBagQuantity,
            MallbotMeatReserve = overrideSnapshot.MallbotMeatReserve != 1_000_000 ? overrideSnapshot.MallbotMeatReserve : defaults.MallbotMeatReserve
        };
    }
}
