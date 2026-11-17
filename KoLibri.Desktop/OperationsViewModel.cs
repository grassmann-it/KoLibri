using System.Collections.ObjectModel;
using LibKolibri;

namespace KoLibri.Desktop;

/// <summary>Audit row projection without exposing journal implementation details.</summary>
public sealed record AuditEventProjection
{
    public string EventId { get; init; } = string.Empty;
    public AuditEventKind Kind { get; init; }
    public string KindText => Kind.ToString();
    public string? OperationId { get; init; }
    public string? AccountId { get; init; }
    public string? Source { get; init; }
    public string? RequestId { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public SnapshotFreshness Freshness { get; init; }
    public bool IsStale { get; init; }
    public IReadOnlyDictionary<string, string> Data { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public string TimestampText => OccurredAt.ToLocalTime().ToString("g");

    public static AuditEventProjection From(AuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        return new AuditEventProjection
        {
            EventId = auditEvent.EventId,
            Kind = auditEvent.Kind,
            OperationId = auditEvent.OperationId,
            AccountId = auditEvent.AccountId,
            Source = auditEvent.Source,
            RequestId = auditEvent.RequestId,
            OccurredAt = auditEvent.OccurredAt,
            Freshness = auditEvent.Freshness,
            IsStale = auditEvent.IsStale,
            Data = auditEvent.Data.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        };
    }
}

/// <summary>Optional status source for worker/cache/blacklist cards.</summary>
public interface IOperationsHealthQuery
{
    Task<OperationsHealthProjection> GetAsync(CancellationToken cancellationToken = default);
}

public sealed record OperationsHealthProjection
{
    public string CacheStatus { get; init; } = "unknown";
    public string BlacklistStatus { get; init; } = "unknown";
    public string WorkerStatus { get; init; } = "unknown";
    public DateTimeOffset? ObservedAt { get; init; }
    public bool IsStale { get; init; }
}

public sealed record OperationsSnapshot(
    IReadOnlyList<AuditEvent> Events,
    OperationsHealthProjection? Health,
    string? NextCursor,
    DateTimeOffset ObservedAt)
{
    public GatewayState State { get; init; } = GatewayState.Unknown;
    public IReadOnlyList<GatewayDiagnostic> Diagnostics { get; init; } = Array.Empty<GatewayDiagnostic>();
    public bool IsStale => State == GatewayState.Stale || Events.Any(x => x.IsStale) || (Health?.IsStale ?? false);
    public bool IsEmpty => Events.Count == 0 && Health is null;
}

/// <summary>Operations projection for approval/audit/cache/worker status.</summary>
public class OperationsViewModel : DesktopViewModel
{
    private readonly IAuditLogGateway? _auditGateway;
    private readonly IOperationsHealthQuery? _healthQuery;
    private AuditLogQuery _query = new();
    private OperationsSnapshot? _result;
    private OperationsHealthProjection? _health;
    private string? _nextCursor;
    private AuditEventProjection? _selectedEvent;

    public OperationsViewModel(
        IAuditLogGateway? auditGateway = null,
        IOperationsHealthQuery? healthQuery = null,
        AuditLogQuery? initialQuery = null)
    {
        _auditGateway = auditGateway;
        _healthQuery = healthQuery;
        if (initialQuery is not null) _query = initialQuery;
        Events = new ObservableCollection<AuditEventProjection>();
        Diagnostics = new ObservableCollection<GatewayDiagnostic>();
    }

    public AuditLogQuery Query
    {
        get => _query;
        private set => SetProperty(ref _query, value);
    }

    public ObservableCollection<AuditEventProjection> Events { get; }
    public ObservableCollection<AuditEventProjection> AuditEvents => Events;
    public ObservableCollection<GatewayDiagnostic> Diagnostics { get; }

    public AuditEventProjection? SelectedEvent
    {
        get => _selectedEvent;
        set => SetProperty(ref _selectedEvent, value);
    }

    public OperationsSnapshot? Result
    {
        get => _result;
        private set => SetProperty(ref _result, value);
    }

    public OperationsHealthProjection? Health
    {
        get => _health;
        private set => SetProperty(ref _health, value);
    }

    public string? NextCursor
    {
        get => _nextCursor;
        private set => SetProperty(ref _nextCursor, value);
    }

    public bool HasEvents => Events.Count > 0;
    public bool HasNextPage => !string.IsNullOrWhiteSpace(NextCursor);

    public void SetQuery(AuditLogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        Query = query;
    }

    public Task<OperationsSnapshot?> LoadAsync(
        AuditLogQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        if (query is not null) SetQuery(query);
        var request = Query;
        return LoadCoreAsync(
            LoadSnapshotAsync,
            ApplySnapshot,
            snapshot => snapshot.State == GatewayState.Error
                ? DesktopLoadState.Error
                : snapshot.IsEmpty ? DesktopLoadState.Empty : snapshot.IsStale ? DesktopLoadState.Stale : DesktopLoadState.Ready,
            snapshot => snapshot.IsStale,
            snapshot => snapshot.ObservedAt,
            cancellationToken);
    }

    public Task<OperationsSnapshot?> RefreshAsync(CancellationToken cancellationToken = default)
        => LoadAsync(null, cancellationToken);

    public Task<OperationsSnapshot?> LoadAsync(CancellationToken cancellationToken)
        => LoadAsync(null, cancellationToken);

    private async Task<OperationsSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        var auditTask = _auditGateway is null
            ? Task.FromResult(new AuditLogResult(GatewayState.Empty))
            : _auditGateway.QueryAsync(Query, cancellationToken);
        var healthTask = _healthQuery is null
            ? Task.FromResult<OperationsHealthProjection?>(null)
            : LoadHealthAsync(cancellationToken);

        await Task.WhenAll(auditTask, healthTask).ConfigureAwait(false);
        var audit = await auditTask.ConfigureAwait(false);
        var health = await healthTask.ConfigureAwait(false);
        return new OperationsSnapshot(audit.Events, health, audit.NextCursor, DateTimeOffset.UtcNow)
        {
            State = audit.State,
            Diagnostics = audit.Diagnostics.ToArray()
        };
    }

    private async Task<OperationsHealthProjection?> LoadHealthAsync(CancellationToken cancellationToken)
        => _healthQuery is null ? null : await _healthQuery.GetAsync(cancellationToken).ConfigureAwait(false);

    private void ApplySnapshot(OperationsSnapshot snapshot)
    {
        Result = snapshot;
        Health = snapshot.Health;
        Events.Clear();
        foreach (var auditEvent in snapshot.Events)
            Events.Add(AuditEventProjection.From(auditEvent));
        NextCursor = snapshot.NextCursor;
        Diagnostics.Clear();
        foreach (var diagnostic in snapshot.Diagnostics)
            Diagnostics.Add(diagnostic);
        OnPropertyChanged(nameof(HasEvents));
        OnPropertyChanged(nameof(HasNextPage));
        if (SelectedEvent is not null)
            SelectedEvent = Events.FirstOrDefault(x => x.EventId == SelectedEvent.EventId);
    }
}

public sealed class OperationsState : OperationsViewModel
{
    public OperationsState(IAuditLogGateway? auditGateway = null,
        IOperationsHealthQuery? healthQuery = null,
        AuditLogQuery? initialQuery = null)
        : base(auditGateway, healthQuery, initialQuery) { }
}

public sealed class AuditLogViewModel : OperationsViewModel
{
    public AuditLogViewModel(IAuditLogGateway? auditGateway = null,
        IOperationsHealthQuery? healthQuery = null,
        AuditLogQuery? initialQuery = null)
        : base(auditGateway, healthQuery, initialQuery) { }
}
