namespace KoLibri.Daily;

/// <summary>State of one daily task after a run.</summary>
public enum DailyTaskState
{
    /// <summary>The marker says it already ran this KoL day; nothing was sent.</summary>
    AlreadyDone,
    /// <summary>Preview only: the task would run.</summary>
    Planned,
    /// <summary>The task ran and KoL confirmed it; the marker is set.</summary>
    Done,
    /// <summary>The task decided not to run today (nothing to do, too expensive, …).</summary>
    Skipped,
    /// <summary>KoL refused it; no marker is set, a later run may try again.</summary>
    Failed,
    /// <summary>A request went out but its effect is unknown; the marker is set so it is never repeated blindly.</summary>
    Unclear
}

public sealed record DailyTaskResult(string Key, DailyTaskState State, string Message);

/// <summary>One once-per-day routine (campfire, hermit, meme shop, …). Runs on the public KoLibri API only.</summary>
public interface IDailyTask
{
    /// <summary>Marker key in <see cref="DailyMarkers"/>, stable across versions (for example "campfire-leaf").</summary>
    string Key { get; }

    string Description { get; }

    /// <summary>Decides whether the task has something to do. Reads only.</summary>
    Task<DailyTaskResult?> PlanAsync(KolibriSession session, CancellationToken cancellationToken);

    /// <summary>Does it. Returns Done, Failed, Skipped or Unclear.</summary>
    Task<DailyTaskResult> ExecuteAsync(KolibriSession session, CancellationToken cancellationToken);
}

/// <summary>A task from two delegates, for small routines and tests.</summary>
public sealed class DelegateDailyTask : IDailyTask
{
    private readonly Func<KolibriSession, CancellationToken, Task<DailyTaskResult?>> _plan;
    private readonly Func<KolibriSession, CancellationToken, Task<DailyTaskResult>> _execute;

    public DelegateDailyTask(string key, string description,
        Func<KolibriSession, CancellationToken, Task<DailyTaskResult>> execute,
        Func<KolibriSession, CancellationToken, Task<DailyTaskResult?>>? plan = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Key = key;
        Description = description ?? key;
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _plan = plan ?? ((_, _) => Task.FromResult<DailyTaskResult?>(null));
    }

    public string Key { get; }
    public string Description { get; }
    public Task<DailyTaskResult?> PlanAsync(KolibriSession session, CancellationToken cancellationToken) => _plan(session, cancellationToken);
    public Task<DailyTaskResult> ExecuteAsync(KolibriSession session, CancellationToken cancellationToken) => _execute(session, cancellationToken);
}

/// <summary>
/// Runs daily tasks in order. Preview by default; with <c>execute</c> each task runs at most once per KoL day
/// (marker in <see cref="KolibriSession.Daily"/>). An unclear task stops the run: nothing after it is sent.
/// </summary>
public static class DailyRunner
{
    public static async Task<IReadOnlyList<DailyTaskResult>> RunAsync(
        KolibriSession session, IEnumerable<IDailyTask> tasks, bool execute, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(tasks);
        var results = new List<DailyTaskResult>();
        foreach (var task in tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (session.Daily.IsDone(task.Key))
            {
                results.Add(new DailyTaskResult(task.Key, DailyTaskState.AlreadyDone, $"{task.Description}: already done today."));
                continue;
            }
            var planned = await task.PlanAsync(session, cancellationToken).ConfigureAwait(false);
            if (planned is { State: DailyTaskState.Skipped or DailyTaskState.AlreadyDone })
            {
                if (planned.State == DailyTaskState.AlreadyDone) session.Daily.MarkDone(task.Key);
                results.Add(planned);
                continue;
            }
            if (!execute)
            {
                results.Add(planned ?? new DailyTaskResult(task.Key, DailyTaskState.Planned, $"{task.Description}: would run."));
                continue;
            }
            DailyTaskResult result;
            try
            {
                result = await task.ExecuteAsync(session, cancellationToken).ConfigureAwait(false);
            }
            catch (ActionDeniedException denied)
            {
                result = new DailyTaskResult(task.Key, DailyTaskState.Failed, $"{task.Description}: refused by the guard ({denied.Reason}).");
            }
            if (result.State is DailyTaskState.Done or DailyTaskState.Unclear) session.Daily.MarkDone(task.Key);
            results.Add(result);
            if (result.State == DailyTaskState.Unclear) break;
        }
        return results;
    }
}
