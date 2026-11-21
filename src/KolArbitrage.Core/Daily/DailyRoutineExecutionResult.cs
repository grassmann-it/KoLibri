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
