using KolArbitrage.Core.Adventure;
using KolArbitrage.Core.Accounts;

namespace KolArbitrage.Core.Daily;

/// <summary>
/// Result of one live daily-step execution.
/// </summary>
public sealed record DailyStepOutcome
{
    public required string StepKind { get; init; }
    public required string Location { get; init; }
    public required string RunId { get; init; }
    public required AdventureExecutionStatus Status { get; init; }
    public required string? RequestId { get; init; }
    public required string StopReason { get; init; }
    public required string? Message { get; init; }
}

/// <summary>
/// Aggregated result for the live run of a daily routine.
/// </summary>
public sealed record DailyRoutineExecutionResult
{
    public required bool Executed { get; init; }
    public required AccountRole Role { get; init; }
    public required IReadOnlyList<DailyStepOutcome> Steps { get; init; }
    public required string? AuditPath { get; init; }
}

/// <summary>
/// Drives a <see cref="DailyRoutineReport"/> through the bounded choice-chain
/// runner. Each step is a typed choice POST against an allowlisted id; the
/// runner stops after MaxChoices or MaxDuration, and the audit ledger
/// records every request. Read-only by default — the bridge requires an
/// explicit approval token to actually hit KoL.
/// </summary>
public sealed class DailyRoutineExecutor
{
    private readonly Func<AccountRole, AdventureExecutionGateway> _gatewayFactory;
    private readonly string? _approvalToken;
    private readonly TimeProvider _clock;

    public DailyRoutineExecutor(
        Func<AccountRole, AdventureExecutionGateway> gatewayFactory,
        string? approvalToken = null,
        TimeProvider? clock = null)
    {
        _gatewayFactory = gatewayFactory ?? throw new ArgumentNullException(nameof(gatewayFactory));
        _approvalToken = approvalToken;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>True when the executor has a non-empty token and is allowed to POST.</summary>
    public bool CanExecute => !string.IsNullOrWhiteSpace(_approvalToken);

    public async ValueTask<DailyRoutineExecutionResult> ExecuteAsync(
        AccountRole role,
        DailyRoutineReport report,
        BoundedChoiceChainOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!CanExecute)
        {
            return new DailyRoutineExecutionResult
            {
                Executed = false,
                Role = role,
                Steps = Array.Empty<DailyStepOutcome>(),
                AuditPath = null
            };
        }

        var settings = (options ?? new BoundedChoiceChainOptions()).Normalize();
        var gateway = _gatewayFactory(role);
        var runner = new BoundedChoiceChainRunner(gateway, _clock);
        var outcomes = new List<DailyStepOutcome>();

        foreach (var step in report.ApprovableSteps.Select(TryParseStep))
        {
            if (step is null) continue;
            cancellationToken.ThrowIfCancellationRequested();
            // The bounded runner needs an existing checkpoint; the daily steps
            // are independent, so each step starts a fresh chain that maps to
            // an already-seeded checkpoint via the report's RunId hint. The
            // runner itself walks any pre-existing choice chain; when the
            // report does not include a RunId we generate one per step.
            var runId = $"daily-{Guid.NewGuid():N}";
            try
            {
                var outcome = await runner.RunAsync(runId, step.Allowlist, settings, cancellationToken)
                    .ConfigureAwait(false);
                outcomes.Add(new DailyStepOutcome
                {
                    StepKind = step.Kind,
                    Location = step.LocationId,
                    RunId = runId,
                    Status = LastStatus(outcome),
                    RequestId = outcome.Steps.Count == 0 ? null : outcome.Steps[^1].RequestId,
                    StopReason = outcome.StopReason.ToString(),
                    Message = outcome.StopReason == BoundedChoiceChainStopReason.NoChoicePending
                        ? "no choice pending; nothing to send"
                        : outcome.StopReason == BoundedChoiceChainStopReason.Aborted
                            ? "step aborted by gateway"
                            : null
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                outcomes.Add(new DailyStepOutcome
                {
                    StepKind = step.Kind,
                    Location = step.LocationId,
                    RunId = runId,
                    Status = AdventureExecutionStatus.Aborted,
                    RequestId = null,
                    StopReason = "Exception",
                    Message = exception.Message
                });
            }
        }

        return new DailyRoutineExecutionResult
        {
            Executed = outcomes.Count > 0,
            Role = role,
            Steps = outcomes,
            AuditPath = gateway.CheckpointStore is null ? "<memory>" : "<persisted>"
        };
    }

    private static AdventureExecutionStatus LastStatus(BoundedChoiceChainResult result)
    {
        if (result.Steps.Count == 0) return AdventureExecutionStatus.Aborted;
        var last = result.Steps[^1];
        if (last.IsComplete) return AdventureExecutionStatus.Complete;
        if (last.IsAborted) return AdventureExecutionStatus.Aborted;
        if (last.RequiresRefresh || last.IsIndeterminate) return AdventureExecutionStatus.RequiresRefresh;
        return AdventureExecutionStatus.Advanced;
    }

    private static DailyStepDescriptor? TryParseStep(string rawStep)
    {
        // Format produced by DailyRoutineRunner: "<kind>: <description>".
        // The bridge only knows the three choice ids that ship in the planner.
        var colon = rawStep.IndexOf(':');
        if (colon <= 0) return null;
        var kindSegment = rawStep[..colon].Trim();
        var description = rawStep[(colon + 1)..].Trim();
        var (choiceId, optionId) = ResolveChoice(kindSegment, description);
        if (choiceId is null || optionId is null) return null;
        var allowlist = new AdventureActionAllowlist().AddChoiceOption(choiceId, choiceId, optionId);
        return new DailyStepDescriptor(kindSegment, choiceId, optionId, allowlist);
    }

    private static (string? choiceId, string? optionId) ResolveChoice(string kind, string description)
    {
        // The Campfire / Hermit / Internet-Shop planners all emit known choice
        // ids ("choice-campfire", "choice-clover", "choice-lucky-surprise-egg",
        // "choice-internet-bacon", "choice-bacon-machine"). Anything else falls
        // through and the executor skips the step — operators see the
        // description in the audit log to decide manually.
        if (kind.Contains("burn", StringComparison.OrdinalIgnoreCase)
            || kind.Contains("campfire", StringComparison.OrdinalIgnoreCase))
            return ("choice-campfire", "1");
        if (kind.Contains("clover", StringComparison.OrdinalIgnoreCase))
            return ("choice-clover", "1");
        if (kind.Contains("Lucky Surprise Egg", StringComparison.Ordinal)
            || description.Contains("Lucky Surprise Egg", StringComparison.Ordinal))
            return ("choice-lucky-surprise-egg", "1");
        if (kind.Contains("Internet Bacon", StringComparison.Ordinal)
            || description.Contains("Internet Bacon", StringComparison.Ordinal))
            return ("choice-internet-bacon", "1");
        if (kind.Contains("Infinite Bacon Machine", StringComparison.Ordinal)
            || description.Contains("Infinite Bacon Machine", StringComparison.Ordinal))
            return ("choice-bacon-machine", "1");
        return (null, null);
    }

    private sealed record DailyStepDescriptor(
        string Kind,
        string ChoiceId,
        string OptionId,
        AdventureActionAllowlist Allowlist)
    {
        public string LocationId => ChoiceId;
    }
}
