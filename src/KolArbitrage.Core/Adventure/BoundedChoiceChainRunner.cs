namespace KolArbitrage.Core.Adventure;

public sealed record BoundedChoiceChainOptions
{
    public int MaxChoices { get; init; } = 10;
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(2);

    public BoundedChoiceChainOptions Normalize()
        => this with
        {
            MaxChoices = Math.Clamp(MaxChoices, 1, 1_000),
            MaxDuration = MaxDuration <= TimeSpan.Zero ? TimeSpan.FromMinutes(2) : MaxDuration
        };
}

public enum BoundedChoiceChainStopReason
{
    Complete,
    NoChoicePending,
    ChoiceLimitReached,
    DeadlineReached,
    RequiresRefresh,
    Aborted,
    Canceled
}

public sealed record BoundedChoiceChainResult
{
    public required BoundedChoiceChainStopReason StopReason { get; init; }
    public required IReadOnlyList<AdventureExecutionResult> Steps { get; init; }
    public AdventureExecutionResult? LastStep => Steps.Count == 0 ? null : Steps[^1];
    public bool IsSafeStop => StopReason is not BoundedChoiceChainStopReason.Aborted;
}

/// <summary>
/// Continues only pending, allowlisted choice actions from a checkpoint. It
/// deliberately stops before fights or unknown encounters; those belong to a
/// separate runner and cannot be guessed as part of a choice chain.
/// </summary>
public sealed class BoundedChoiceChainRunner
{
    private readonly AdventureExecutionGateway _gateway;
    private readonly TimeProvider _clock;

    public BoundedChoiceChainRunner(AdventureExecutionGateway gateway, TimeProvider? clock = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<BoundedChoiceChainResult> RunAsync(
        string runId,
        AdventureActionAllowlist allowlist,
        BoundedChoiceChainOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(allowlist);
        var settings = (options ?? new BoundedChoiceChainOptions()).Normalize();
        var started = _clock.GetUtcNow();
        var results = new List<AdventureExecutionResult>();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var checkpoint = await _gateway.CheckpointStore.LoadAsync(runId.Trim(), cancellationToken).ConfigureAwait(false);
            if (checkpoint is null)
                return new BoundedChoiceChainResult { StopReason = BoundedChoiceChainStopReason.Aborted, Steps = results };
            if (!string.IsNullOrWhiteSpace(checkpoint.InFlightRequestId))
                return new BoundedChoiceChainResult { StopReason = BoundedChoiceChainStopReason.RequiresRefresh, Steps = results };
            var state = checkpoint.ToState();
            if (state is AdventureCompleteRunState)
                return new BoundedChoiceChainResult { StopReason = BoundedChoiceChainStopReason.Complete, Steps = results };
            if (state is AdventureAbortedRunState)
            {
                return new BoundedChoiceChainResult { StopReason = BoundedChoiceChainStopReason.Aborted, Steps = results };
            }
            if (state.PendingAction is not AdventureChoiceAction)
                return new BoundedChoiceChainResult { StopReason = BoundedChoiceChainStopReason.NoChoicePending, Steps = results };
            if (results.Count >= settings.MaxChoices)
                return new BoundedChoiceChainResult { StopReason = BoundedChoiceChainStopReason.ChoiceLimitReached, Steps = results };
            if (_clock.GetUtcNow() - started >= settings.MaxDuration)
                return new BoundedChoiceChainResult { StopReason = BoundedChoiceChainStopReason.DeadlineReached, Steps = results };

            var current = await _gateway.ExecutePendingAsync(runId.Trim(), allowlist, cancellationToken).ConfigureAwait(false);
            results.Add(current);
            if (current.IsAborted)
                return new BoundedChoiceChainResult { StopReason = BoundedChoiceChainStopReason.Aborted, Steps = results };
            if (current.RequiresRefresh || current.IsIndeterminate)
                return new BoundedChoiceChainResult { StopReason = BoundedChoiceChainStopReason.RequiresRefresh, Steps = results };
        }
    }
}
