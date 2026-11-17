using System.Collections.ObjectModel;
using System.Text.Json;

namespace KolArbitrage.Core.Adventure;

/// <summary>
/// The category of a server-side or local turn counter. Counter values are
/// observations only; the Core layer never spends a turn by changing one.
/// </summary>
public enum TurnCounterKind
{
    Unknown = 0,
    Normal = 1,
    Wander = 2,
    Item = 3,
    Location = 4,
    EffectExpiry = 5,
    DailyReset = 6,
    Daily = DailyReset,
    Wanderer = Wander
}

/// <summary>Result of evaluating a counter before a caller emits a request.</summary>
public enum CounterGate
{
    Allow = 0,
    Warn = 1,
    Stop = 2,
    Manual = 3,
    Warning = Warn,
    Blocked = Stop
}

/// <summary>
/// Read-only account turn state. It deliberately carries the server's game
/// day rather than deriving a reset from the local machine clock.
/// </summary>
public record TurnSnapshot
{
    public TurnSnapshot() { }

    public TurnSnapshot(string accountId, int adventuresRemaining,
        DateTimeOffset observedAt, DateOnly? gameDay = null)
    {
        AccountId = accountId;
        AdventuresRemaining = adventuresRemaining;
        ObservedAt = observedAt;
        GameDay = gameDay ?? default;
    }

    public TurnSnapshot(string accountId, DateOnly gameDay, int adventuresRemaining,
        int freeTurnsRemaining = 0, DateTimeOffset? observedAt = null)
        : this(accountId, adventuresRemaining, observedAt ?? default, gameDay)
        => FreeTurnsRemaining = freeTurnsRemaining;

    public string AccountId { get; init; } = string.Empty;
    public DateOnly GameDay { get; init; }
    public DateOnly Day { get => GameDay; init => GameDay = value; }
    public DateTimeOffset? RolloverAt { get; init; }
    public int AscensionTurn { get; init; }
    public int TurnsUsedToday { get; init; }
    public int AdventuresRemaining { get; init; }
    public int FreeTurnsRemaining { get; init; }
    public int TurnsRemaining { get => AdventuresRemaining; init => AdventuresRemaining = value; }
    public int AvailableTurns { get => AdventuresRemaining; init => AdventuresRemaining = value; }
    public int Adventures => AdventuresRemaining;
    public int Turns => AdventuresRemaining;
    public int TotalTurns => AdventuresRemaining + FreeTurnsRemaining;
    public DateTimeOffset ObservedAt { get; init; }
    public DateTimeOffset FetchedAt { get => ObservedAt; init => ObservedAt = value; }
    public string Source { get; init; } = string.Empty;
    public bool IsStale { get; init; }
    public string? ResponseHash { get; init; }
    public long Revision { get; init; }
    public long ETag
    {
        get => Revision;
        init => Revision = value;
    }
    public long Version { get => Revision; init => Revision = value; }
    public bool IsFresh => !IsStale;

    public TurnSnapshot Normalize()
        => this with
        {
            AccountId = (AccountId ?? string.Empty).Trim(),
            AscensionTurn = Math.Max(0, AscensionTurn),
            TurnsUsedToday = Math.Max(0, TurnsUsedToday),
            AdventuresRemaining = Math.Max(0, AdventuresRemaining),
            FreeTurnsRemaining = Math.Max(0, FreeTurnsRemaining),
            Source = (Source ?? string.Empty).Trim(),
            ResponseHash = string.IsNullOrWhiteSpace(ResponseHash) ? null : ResponseHash.Trim(),
            Revision = Math.Max(0, Revision)
        };

    public static TurnSnapshot From(AdventureCounterContract counters,
        string? accountId = null, DateOnly? gameDay = null,
        DateTimeOffset? observedAt = null)
    {
        ArgumentNullException.ThrowIfNull(counters);
        var value = counters.Normalize();
        return new TurnSnapshot
        {
            AccountId = accountId ?? string.Empty,
            GameDay = gameDay ?? default,
            TurnsUsedToday = value.AdventuresStarted,
            ObservedAt = observedAt ?? default,
            Source = "AdventureCounterContract"
        };
    }
}

/// <summary>Account-specific spelling retained for account adapters.</summary>
public record AccountTurnSnapshot : TurnSnapshot
{
    public AccountTurnSnapshot() { }

    public AccountTurnSnapshot(string accountId, DateOnly? gameDay,
        DateTimeOffset observedAt, int adventuresRemaining,
        int freeTurnsRemaining = 0)
        : base(accountId, adventuresRemaining, observedAt, gameDay)
        => FreeTurnsRemaining = freeTurnsRemaining;

    public AccountTurnSnapshot(string accountId, DateOnly gameDay,
        DateTimeOffset? rolloverAt, int ascensionTurn, int turnsUsedToday,
        int adventuresRemaining, int freeTurnsRemaining, DateTimeOffset observedAt,
        string? source = null, bool isStale = false, string? responseHash = null)
        : this(accountId, gameDay, observedAt, adventuresRemaining, freeTurnsRemaining)
    {
        RolloverAt = rolloverAt;
        AscensionTurn = ascensionTurn;
        TurnsUsedToday = turnsUsedToday;
        Source = source ?? string.Empty;
        IsStale = isStale;
        ResponseHash = responseHash;
    }
}

/// <summary>
/// A named counter that a planner can inspect before deciding whether to use a
/// turn. Lease fields are local coordination metadata and are never sent to KoL.
/// </summary>
public record TurnCounter
{
    public TurnCounter() { }
    public TurnCounter(string id, int remainingTurns) { Id = id; RemainingTurns = remainingTurns; }
    public TurnCounter(string id, string accountId, TurnCounterKind kind, int remainingTurns)
        : this(id, remainingTurns) { AccountId = accountId; Kind = kind; }
    public TurnCounter(string accountId, string id, int remainingTurns)
        : this(id, accountId, TurnCounterKind.Normal, remainingTurns) { }

    public static TurnCounter From(AdventureCounterContract counters,
        string accountId = "", string counterId = "adventures-started")
        => counters.ToTurnCounter(accountId, counterId);

    public string Id { get; init; } = string.Empty;
    public string CounterId { get => Id; init => Id = value; }
    public string AccountId { get; init; } = string.Empty;
    public TurnCounterKind Kind { get; init; } = TurnCounterKind.Normal;
    public TurnCounterKind Type
    {
        get => Kind;
        init => Kind = value;
    }
    public string Label { get; init; } = string.Empty;
    public int? TargetTurn { get; init; }
    public int RemainingTurns { get; init; }
    public int Turns { get => RemainingTurns; init => RemainingTurns = value; }
    public int Count { get => RemainingTurns; init => RemainingTurns = value; }
    public int TurnsRemaining { get => RemainingTurns; init => RemainingTurns = value; }
    public int? WarnAt { get; init; }
    public string? ExpiryAction { get; init; }
    public bool Enabled { get; init; } = true;
    public IReadOnlyList<string> ExemptLocationIds { get; init; } = Array.Empty<string>();
    public string? LeaseOwner { get; init; }
    public DateTimeOffset? LeaseUntil { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public long Revision { get; init; }
    public long ETag
    {
        get => Revision;
        init => Revision = value;
    }
    public long Version { get => Revision; init => Revision = value; }

    public bool HasLease => !string.IsNullOrWhiteSpace(LeaseOwner)
        && LeaseUntil is { } until && until > DateTimeOffset.UtcNow;
    public bool IsExpired => RemainingTurns <= 0;
    public CounterGate Gate => !Enabled ? CounterGate.Manual
        : IsExpired ? CounterGate.Stop
        : WarnAt is { } warning && RemainingTurns <= warning ? CounterGate.Warn
        : CounterGate.Allow;
    public bool CanConsume(int turns = 1) => Enabled && turns > 0 && turns <= RemainingTurns;

    public TurnCounter Normalize()
    {
        var locations = (ExemptLocationIds ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        return this with
        {
            Id = (Id ?? string.Empty).Trim(),
            AccountId = (AccountId ?? string.Empty).Trim(),
            Label = (Label ?? string.Empty).Trim(),
            RemainingTurns = Math.Max(0, RemainingTurns),
            TargetTurn = TargetTurn is { } target ? Math.Max(0, target) : null,
            WarnAt = WarnAt is { } warning ? Math.Max(0, warning) : null,
            ExpiryAction = string.IsNullOrWhiteSpace(ExpiryAction) ? null : ExpiryAction.Trim(),
            ExemptLocationIds = new ReadOnlyCollection<string>(locations),
            LeaseOwner = string.IsNullOrWhiteSpace(LeaseOwner) ? null : LeaseOwner.Trim(),
            Revision = Math.Max(0, Revision)
        };
    }

    public TurnCounter Consume(int turns = 1)
    {
        var value = Normalize();
        if (!value.CanConsume(turns))
            throw new InvalidOperationException("The turn counter does not have enough available turns.");
        return value with { RemainingTurns = checked(value.RemainingTurns - turns) };
    }
}

public sealed record TurnCounterCasResult
{
    public bool Succeeded { get; init; }
    public bool Success => Succeeded;
    public bool IsSuccess => Succeeded;
    public TurnCounter? Current { get; init; }
    public TurnCounter? Saved { get; init; }
    public TurnCounter? Previous { get; init; }
    public long Version => Saved?.Revision ?? Current?.Revision ?? 0;
    public bool Conflict => !Succeeded;
    public static TurnCounterCasResult SuccessResult(TurnCounter saved, TurnCounter? previous = null)
        => new() { Succeeded = true, Current = saved, Saved = saved, Previous = previous };
    public static TurnCounterCasResult Failed(TurnCounter? current)
        => new() { Succeeded = false, Current = current };
}

public sealed record TurnSnapshotCasResult
{
    public bool Succeeded { get; init; }
    public bool Success => Succeeded;
    public bool IsSuccess => Succeeded;
    public TurnSnapshot? Current { get; init; }
    public TurnSnapshot? Saved { get; init; }
    public TurnSnapshot? Previous { get; init; }
    public long Version => Saved?.Revision ?? Current?.Revision ?? 0;
    public bool Conflict => !Succeeded;
    public static TurnSnapshotCasResult SuccessResult(TurnSnapshot saved, TurnSnapshot? previous = null)
        => new() { Succeeded = true, Current = saved, Saved = saved, Previous = previous };
    public static TurnSnapshotCasResult Failed(TurnSnapshot? current)
        => new() { Succeeded = false, Current = current };
}

public interface ITurnSnapshotReader
{
    Task<TurnSnapshot?> ReadSnapshotAsync(string accountId, CancellationToken cancellationToken = default);
    Task<TurnSnapshot?> ReadAsync(string accountId, CancellationToken cancellationToken = default)
        => ReadSnapshotAsync(accountId, cancellationToken);
    Task<TurnSnapshot?> GetAsync(string accountId, CancellationToken cancellationToken = default)
        => ReadSnapshotAsync(accountId, cancellationToken);
    Task<TurnSnapshot?> GetSnapshotAsync(string accountId, CancellationToken cancellationToken = default)
        => ReadSnapshotAsync(accountId, cancellationToken);
}

/// <summary>Read-only alias for hosts that do not use the Turn prefix.</summary>
public interface IReadOnlyCounterStore : ITurnSnapshotReader
{
}

public interface ITurnCounterReader : ITurnSnapshotReader
{
    Task<TurnCounter?> ReadCounterAsync(string accountId, string counterId,
        CancellationToken cancellationToken = default);
    Task<TurnCounter?> ReadCounterAsync(string counterId,
        CancellationToken cancellationToken = default)
        => ReadCounterAsync("default", counterId, cancellationToken);
    Task<TurnCounter?> LoadAsync(string accountId, string counterId,
        CancellationToken cancellationToken = default)
        => ReadCounterAsync(accountId, counterId, cancellationToken);
    Task<TurnCounter?> GetCounterAsync(string accountId, string counterId,
        CancellationToken cancellationToken = default)
        => ReadCounterAsync(accountId, counterId, cancellationToken);
}

public interface ICounterStore : ITurnCounterReader
{
    Task<TurnCounterCasResult> CompareAndSwapAsync(string accountId, string counterId,
        TurnCounter? expected, TurnCounter replacement,
        CancellationToken cancellationToken = default);
    Task<TurnSnapshotCasResult> CompareAndSwapSnapshotAsync(string accountId,
        TurnSnapshot? expected, TurnSnapshot replacement,
        CancellationToken cancellationToken = default);

    Task<TurnSnapshotCasResult> CompareAndSwapSnapshotAsync(string accountId,
        long expectedVersion, TurnSnapshot replacement,
        CancellationToken cancellationToken = default)
    {
        var expected = expectedVersion < 1
            ? null
            : replacement with { AccountId = accountId, Revision = expectedVersion };
        return CompareAndSwapSnapshotAsync(accountId, expected, replacement, cancellationToken);
    }

    Task<TurnCounterCasResult> TryCompareAndSwapAsync(string accountId, string counterId,
        TurnCounter? expected, TurnCounter replacement,
        CancellationToken cancellationToken = default)
        => CompareAndSwapAsync(accountId, counterId, expected, replacement, cancellationToken);

    Task<TurnCounterCasResult> CompareAndSwapAsync(string counterId,
        TurnCounter? expected, TurnCounter replacement,
        CancellationToken cancellationToken = default)
        => CompareAndSwapAsync(
            replacement.AccountId,
            counterId,
            expected,
            replacement,
            cancellationToken);

    Task<TurnCounterCasResult> CompareAndSwapAsync(string accountId, string counterId,
        long expectedVersion, TurnCounter replacement,
        CancellationToken cancellationToken = default)
    {
        var expected = replacement with { AccountId = accountId, Id = counterId, Revision = expectedVersion };
        return CompareAndSwapAsync(accountId, counterId, expectedVersion < 1 ? null : expected,
            replacement, cancellationToken);
    }
}

public interface ITurnCounterStore : ICounterStore { }

public interface ITurnSnapshotStore : ITurnSnapshotReader
{
    Task<TurnSnapshotCasResult> CompareAndSwapSnapshotAsync(string accountId,
        TurnSnapshot? expected, TurnSnapshot replacement,
        CancellationToken cancellationToken = default);
}

/// <summary>Thread-safe process-local implementation for fixtures and previews.</summary>
public class CounterStore : ICounterStore, ITurnCounterStore, ITurnSnapshotStore, IReadOnlyCounterStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TurnSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TurnCounter> _counters = new(StringComparer.Ordinal);

    public Task<TurnSnapshot?> ReadSnapshotAsync(string accountId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = NormalizeKey(accountId, nameof(accountId));
        lock (_gate) return Task.FromResult(_snapshots.TryGetValue(key, out var value) ? value.Normalize() : null);
    }

    public Task<TurnSnapshot?> GetSnapshotAsync(string accountId, CancellationToken cancellationToken = default)
        => ReadSnapshotAsync(accountId, cancellationToken);

    public Task<TurnSnapshot?> ReadAsync(string accountId, CancellationToken cancellationToken = default)
        => ReadSnapshotAsync(accountId, cancellationToken);

    public Task<TurnSnapshot?> GetAsync(string accountId, CancellationToken cancellationToken = default)
        => ReadSnapshotAsync(accountId, cancellationToken);

    public Task<TurnCounter?> ReadCounterAsync(string accountId, string counterId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = CounterKey(accountId, counterId);
        lock (_gate) return Task.FromResult(_counters.TryGetValue(key, out var value) ? value.Normalize() : null);
    }

    public Task<TurnCounter?> GetCounterAsync(string accountId, string counterId, CancellationToken cancellationToken = default)
        => ReadCounterAsync(accountId, counterId, cancellationToken);

    public Task<TurnCounter?> ReadCounterAsync(string counterId, CancellationToken cancellationToken = default)
        => ReadCounterAsync("default", counterId, cancellationToken);

    public Task<TurnCounter?> LoadAsync(string accountId, string counterId, CancellationToken cancellationToken = default)
        => ReadCounterAsync(accountId, counterId, cancellationToken);

    public Task<TurnCounterCasResult> TryCompareAndSwapAsync(string accountId, string counterId,
        TurnCounter? expected, TurnCounter replacement, CancellationToken cancellationToken = default)
        => CompareAndSwapAsync(accountId, counterId, expected, replacement, cancellationToken);

    public Task<TurnCounterCasResult> TryCompareAndSwapAsync(string accountId, string counterId,
        long expectedVersion, TurnCounter replacement, CancellationToken cancellationToken = default)
        => CompareAndSwapAsync(accountId, counterId, expectedVersion, replacement, cancellationToken);

    public Task<TurnCounterCasResult> CompareAndSwapAsync(string counterId,
        TurnCounter? expected, TurnCounter replacement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        var account = string.IsNullOrWhiteSpace(replacement.AccountId)
            ? expected?.AccountId
            : replacement.AccountId;
        return CompareAndSwapAsync(account ?? "default", counterId, expected,
            replacement, cancellationToken);
    }

    public Task<TurnCounterCasResult> CompareAndSwapAsync(string accountId, string counterId,
        long expectedVersion, TurnCounter replacement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        cancellationToken.ThrowIfCancellationRequested();
        var account = NormalizeKey(accountId, nameof(accountId));
        var id = NormalizeKey(counterId, nameof(counterId));
        var value = replacement.Normalize();
        if (value.AccountId.Length > 0 && !string.Equals(value.AccountId, account, StringComparison.Ordinal))
            throw new ArgumentException("The replacement account id does not match the CAS key.", nameof(replacement));
        if (value.Id.Length > 0 && !string.Equals(value.Id, id, StringComparison.Ordinal))
            throw new ArgumentException("The replacement counter id does not match the CAS key.", nameof(replacement));
        value = value with { AccountId = account, Id = id };
        lock (_gate)
        {
            var key = CounterKey(account, id);
            _counters.TryGetValue(key, out var current);
            var matches = expectedVersion < 1 ? current is null : current is not null && current.Revision == expectedVersion;
            if (!matches) return Task.FromResult(TurnCounterCasResult.Failed(current));
            value = value with { Revision = Math.Max(current?.Revision ?? 0, value.Revision) + 1 };
            _counters[key] = value;
            return Task.FromResult(TurnCounterCasResult.SuccessResult(value, current));
        }
    }

    public void ReplaceSnapshot(TurnSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var value = snapshot.Normalize();
        var key = string.IsNullOrWhiteSpace(value.AccountId) ? "default" : NormalizeKey(value.AccountId, nameof(snapshot));
        lock (_gate) _snapshots[key] = value;
    }

    public void ReplaceCounter(TurnCounter counter)
    {
        ArgumentNullException.ThrowIfNull(counter);
        var value = counter.Normalize();
        if (string.IsNullOrWhiteSpace(value.AccountId)) value = value with { AccountId = "default" };
        if (string.IsNullOrWhiteSpace(value.Id)) throw new ArgumentException("A counter id is required.", nameof(counter));
        var key = CounterKey(value.AccountId, value.Id);
        lock (_gate) _counters[key] = value;
    }

    public Task<TurnCounterCasResult> CompareAndSwapAsync(string accountId, string counterId,
        TurnCounter? expected, TurnCounter replacement, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(replacement);
        var account = NormalizeKey(accountId, nameof(accountId));
        var id = NormalizeKey(counterId, nameof(counterId));
        var value = replacement.Normalize();
        if (value.AccountId.Length > 0 && !string.Equals(value.AccountId, account, StringComparison.Ordinal))
            throw new ArgumentException("The replacement account id does not match the CAS key.", nameof(replacement));
        if (value.Id.Length > 0 && !string.Equals(value.Id, id, StringComparison.Ordinal))
            throw new ArgumentException("The replacement counter id does not match the CAS key.", nameof(replacement));
        value = value with { AccountId = account, Id = id };
        lock (_gate)
        {
            var key = CounterKey(account, id);
            _counters.TryGetValue(key, out var current);
            if (!Matches(current, expected)) return Task.FromResult(TurnCounterCasResult.Failed(current));
            value = value with { Revision = Math.Max(current?.Revision ?? 0, value.Revision) + 1 };
            _counters[key] = value;
            return Task.FromResult(TurnCounterCasResult.SuccessResult(value, current));
        }
    }

    public Task<TurnSnapshotCasResult> CompareAndSwapSnapshotAsync(string accountId,
        TurnSnapshot? expected, TurnSnapshot replacement, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(replacement);
        var account = NormalizeKey(accountId, nameof(accountId));
        var value = replacement.Normalize();
        if (value.AccountId.Length > 0 && !string.Equals(value.AccountId, account, StringComparison.Ordinal))
            throw new ArgumentException("The replacement account id does not match the CAS key.", nameof(replacement));
        value = value with { AccountId = account };
        lock (_gate)
        {
            _snapshots.TryGetValue(account, out var current);
            if (!Matches(current, expected)) return Task.FromResult(TurnSnapshotCasResult.Failed(current));
            value = value with { Revision = Math.Max(current?.Revision ?? 0, value.Revision) + 1 };
            _snapshots[account] = value;
            return Task.FromResult(TurnSnapshotCasResult.SuccessResult(value, current));
        }
    }

    public Task<TurnSnapshotCasResult> CompareAndSwapSnapshotAsync(string accountId,
        long expectedVersion, TurnSnapshot replacement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        cancellationToken.ThrowIfCancellationRequested();
        var account = NormalizeKey(accountId, nameof(accountId));
        var value = replacement.Normalize();
        if (value.AccountId.Length > 0 && !string.Equals(value.AccountId, account, StringComparison.Ordinal))
            throw new ArgumentException("The replacement account id does not match the CAS key.", nameof(replacement));
        value = value with { AccountId = account };
        lock (_gate)
        {
            _snapshots.TryGetValue(account, out var current);
            var matches = expectedVersion < 1 ? current is null : current is not null && current.Revision == expectedVersion;
            if (!matches) return Task.FromResult(TurnSnapshotCasResult.Failed(current));
            value = value with { Revision = Math.Max(current?.Revision ?? 0, value.Revision) + 1 };
            _snapshots[account] = value;
            return Task.FromResult(TurnSnapshotCasResult.SuccessResult(value, current));
        }
    }

    private static bool Matches(TurnCounter? current, TurnCounter? expected)
        => current is null && expected is null || current is not null && expected is not null
            && current.Revision == expected.Revision
            && string.Equals(JsonSerializer.Serialize(current.Normalize()), JsonSerializer.Serialize(expected.Normalize()), StringComparison.Ordinal);

    private static bool Matches(TurnSnapshot? current, TurnSnapshot? expected)
        => current is null && expected is null || current is not null && expected is not null
            && current.Revision == expected.Revision
            && string.Equals(JsonSerializer.Serialize(current.Normalize()), JsonSerializer.Serialize(expected.Normalize()), StringComparison.Ordinal);

    private static string CounterKey(string accountId, string counterId)
        => $"{NormalizeKey(accountId, nameof(accountId))}\u001f{NormalizeKey(counterId, nameof(counterId))}";

    private static string NormalizeKey(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A non-empty key is required.", parameterName);
        return value.Trim();
    }
}

public sealed class InMemoryCounterStore : CounterStore { }
public sealed class InMemoryTurnCounterStore : CounterStore { }

/// <summary>Compatibility helpers for the existing adventure action counters.</summary>
public static class TurnCounterContractExtensions
{
    public static TurnCounter ToTurnCounter(this AdventureCounterContract counters,
        string accountId = "", string counterId = "adventures-started")
    {
        ArgumentNullException.ThrowIfNull(counters);
        var value = counters.Normalize();
        return new TurnCounter(counterId, accountId, TurnCounterKind.Normal, value.AdventuresStarted)
        {
            Label = "Adventures started"
        };
    }

    public static AdventureCounterContract ToAdventureCounterContract(this TurnCounter counter)
    {
        ArgumentNullException.ThrowIfNull(counter);
        return new AdventureCounterContract { AdventuresStarted = counter.Normalize().RemainingTurns };
    }

    public static TurnSnapshot ToTurnSnapshot(this AdventureCounterContract counters,
        string accountId = "", DateOnly? gameDay = null, DateTimeOffset? observedAt = null)
        => TurnSnapshot.From(counters, accountId, gameDay, observedAt);
}
