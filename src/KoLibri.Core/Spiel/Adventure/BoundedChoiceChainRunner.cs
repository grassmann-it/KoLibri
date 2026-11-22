namespace KoLibri.Core.Spiel;

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
    Canceled,
    /// <summary>A choice is open and no policy answered it; see <see cref="BoundedChoiceChainResult.NeedsDecision"/>.</summary>
    NeedsDecision
}

public sealed record BoundedChoiceChainResult
{
    public required BoundedChoiceChainStopReason StopReason { get; init; }
    public required IReadOnlyList<AdventureExecutionResult> Steps { get; init; }
    public NeedsDecisionInfo? NeedsDecision { get; init; }
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
            AdventureChoiceAction? decided = null;
            if (state.PendingAction is not AdventureChoiceAction && checkpoint.ToNeedsDecision() is { } needs)
            {
                // The user's decision (an explicit rule for exactly this choice, option visible on the
                // page) is applied first; without one the run still needs a decision.
                decided = DecidedChoice(checkpoint, state, allowlist);
                if (decided is null)
                    return new BoundedChoiceChainResult { StopReason = BoundedChoiceChainStopReason.NeedsDecision, Steps = results, NeedsDecision = needs };
            }
            if (state.PendingAction is not AdventureChoiceAction && decided is null)
                return new BoundedChoiceChainResult { StopReason = BoundedChoiceChainStopReason.NoChoicePending, Steps = results };
            if (results.Count >= settings.MaxChoices)
                return new BoundedChoiceChainResult { StopReason = BoundedChoiceChainStopReason.ChoiceLimitReached, Steps = results };
            if (_clock.GetUtcNow() - started >= settings.MaxDuration)
                return new BoundedChoiceChainResult { StopReason = BoundedChoiceChainStopReason.DeadlineReached, Steps = results };

            var current = decided is null
                ? await _gateway.ExecutePendingAsync(runId.Trim(), allowlist, cancellationToken).ConfigureAwait(false)
                : await _gateway.ExecuteManualActionAsync(checkpoint, decided, allowlist, cancellationToken).ConfigureAwait(false);
            results.Add(current);
            if (current.IsAborted)
                return new BoundedChoiceChainResult { StopReason = BoundedChoiceChainStopReason.Aborted, Steps = results };
            if (current.RequiresRefresh || current.IsIndeterminate)
                return new BoundedChoiceChainResult { StopReason = BoundedChoiceChainStopReason.RequiresRefresh, Steps = results };
            if (current.State is AdventureChoiceRunState && current.NextAction is null)
                continue; // the next loop turn reads the checkpoint and reports NeedsDecision
        }
    }

    private static AdventureChoiceAction? DecidedChoice(
        AdventureCheckpoint checkpoint, AdventureRunState state, AdventureActionAllowlist allowlist)
    {
        if (state is not AdventureChoiceRunState waiting || string.IsNullOrWhiteSpace(checkpoint.ChoiceId)) return null;
        var visible = checkpoint.ManualChoiceOptions ?? Array.Empty<string>();
        if (visible.Count == 0
            || !allowlist.TryGetChoiceAction(checkpoint.AdventureId, checkpoint.ChoiceId, visible, waiting.EncounterId, out var rule)
            || !visible.Contains(rule.OptionId, StringComparer.Ordinal))
            return null;
        return new AdventureChoiceAction(checkpoint.AdventureId, rule.ChoiceId, rule.OptionId,
            waiting.EncounterId, checkpoint.PendingChoiceFields);
    }
}
