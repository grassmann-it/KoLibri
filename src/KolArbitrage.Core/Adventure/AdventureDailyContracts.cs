namespace KolArbitrage.Core.Adventure;

/// <summary>
/// Read boundary for a daily budget. Implementations may read a local status
/// cache or an account adapter; the kernel only consumes this value.
/// </summary>
public interface IAdventureDailyLimitProvider
{
    Task<AdventureDailyLimitContract> GetAsync(
        DateOnly day,
        CancellationToken cancellationToken = default);
}

/// <summary>Persistence boundary for counters used by a daily runner.</summary>
public interface IAdventureDailyCounterStore
{
    Task<AdventureCounterContract> LoadAsync(
        DateOnly day,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        DateOnly day,
        AdventureCounterContract counters,
        CancellationToken cancellationToken = default);
}

/// <summary>In-memory implementation useful for fixture and desktop preview runs.</summary>
public sealed class InMemoryAdventureDailyCounterStore : IAdventureDailyCounterStore
{
    private readonly Dictionary<DateOnly, AdventureCounterContract> _values = new();
    private readonly object _gate = new();

    public Task<AdventureCounterContract> LoadAsync(
        DateOnly day,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(
                _values.TryGetValue(day, out var counters)
                    ? counters.Normalize()
                    : new AdventureCounterContract());
        }
    }

    public Task SaveAsync(
        DateOnly day,
        AdventureCounterContract counters,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(counters);
        lock (_gate) _values[day] = counters.Normalize();
        return Task.CompletedTask;
    }
}

/// <summary>
/// A pure result for a caller that wants to check a counter before emitting a
/// run request. This is deliberately separate from action policy: a counter
/// failure must not silently select another adventure.
/// </summary>
public sealed record AdventureDailyCounterCheck
{
    public required AdventureDailyLimitContract Limit { get; init; }
    public required AdventureCounterContract Counters { get; init; }
    public bool IsAllowed { get; init; }
    public string? Reason { get; init; }

    public static AdventureDailyCounterCheck Evaluate(
        AdventureDailyLimitContract limit,
        AdventureCounterContract counters,
        int turns = 1,
        int runs = 1)
    {
        ArgumentNullException.ThrowIfNull(limit);
        ArgumentNullException.ThrowIfNull(counters);
        var normalizedLimit = limit.Normalize();
        var normalizedCounters = counters.Normalize();
        var allowed = normalizedLimit.CanStart(turns, runs);
        return new AdventureDailyCounterCheck
        {
            Limit = normalizedLimit,
            Counters = normalizedCounters,
            IsAllowed = allowed,
            Reason = allowed ? null : "The daily adventure limit has been reached."
        };
    }
}
