using System.Collections.ObjectModel;
using LibKolibri;

namespace KoLibri.Desktop;

/// <summary>Row projection used by the dense arbitrage table.</summary>
public sealed record ArbitrageCandidateProjection
{
    public string ItemId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? SellerId { get; init; }
    public int Quantity { get; init; }
    public decimal? AskPrice { get; init; }
    public decimal? ReferencePrice { get; init; }
    public decimal? ExpectedNetProfit { get; init; }
    public decimal? MarginPercent { get; init; }
    public decimal? LiquidityScore { get; init; }
    public ArbitrageDecision Decision { get; init; }
    public bool IsTradeable { get; init; }
    public bool IsAccountBound { get; init; }
    public bool RequiresApproval { get; init; }
    public bool IsStale { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ReasonCodes { get; init; } = Array.Empty<string>();
    public DateTimeOffset ObservedAt { get; init; }
    public DesktopSnapshotInfo Snapshot { get; init; } = new(SnapshotFreshness.Unknown, null, null, null, null, null);

    public string DecisionText => Decision switch
    {
        ArbitrageDecision.Buy => "Buy",
        ArbitrageDecision.Hold => "Hold",
        ArbitrageDecision.Reject => "Reject",
        ArbitrageDecision.Investigate => "Investigate",
        _ => "Unknown"
    };

    public string MarginText => MarginPercent is { } margin ? $"{margin:P1}" : "—";
    public string ProfitText => ExpectedNetProfit is { } profit ? $"{profit:N0} Meat" : "—";
    public string FreshnessText => IsStale ? "stale" : "fresh";

    public static ArbitrageCandidateProjection From(ArbitrageCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var stale = candidate.IsStale || candidate.Snapshot.IsStale;
        return new ArbitrageCandidateProjection
        {
            ItemId = candidate.ItemId,
            DisplayName = candidate.DisplayName ?? candidate.ItemId,
            SellerId = candidate.SellerId,
            Quantity = candidate.Quantity,
            AskPrice = candidate.AskPrice,
            ReferencePrice = candidate.ReferencePrice,
            ExpectedNetProfit = candidate.ExpectedNetProfit,
            MarginPercent = candidate.MarginPercent,
            LiquidityScore = candidate.LiquidityScore,
            Decision = candidate.Decision,
            IsTradeable = candidate.IsTradeable,
            IsAccountBound = candidate.IsAccountBound,
            RequiresApproval = candidate.RequiresApproval,
            IsStale = stale,
            Reasons = candidate.Reasons?.ToArray() ?? Array.Empty<string>(),
            ReasonCodes = candidate.ReasonCodes?.ToArray() ?? Array.Empty<string>(),
            ObservedAt = candidate.ObservedAt,
            Snapshot = DesktopSnapshotInfo.From(candidate.Snapshot)
        };
    }
}

public sealed record ArbitragePreviewProjection(
    string PreviewId,
    GatewayState State,
    bool RequiresApproval,
    IReadOnlyList<ArbitrageAction> Actions,
    IReadOnlyList<ArbitrageCandidateProjection> Candidates,
    IReadOnlyList<ExpectedStateCheck> Checks,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    bool IsStale)
{
    public bool IsExpired => ExpiresAt is { } expiry && DateTimeOffset.UtcNow >= expiry;
    public PreviewState PreviewState => State switch
    {
        GatewayState.Ready => PreviewState.Ready,
        GatewayState.ApprovalRequired => PreviewState.ApprovalRequired,
        GatewayState.Blocked => PreviewState.Blocked,
        GatewayState.Conflict => PreviewState.Conflict,
        GatewayState.Expired => PreviewState.Expired,
        GatewayState.Rejected => PreviewState.Rejected,
        GatewayState.Committed => PreviewState.Committed,
        GatewayState.Verified => PreviewState.Verified,
        GatewayState.Error => PreviewState.Error,
        _ => PreviewState.Unknown
    };

    public static ArbitragePreviewProjection From(ArbitragePreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return new ArbitragePreviewProjection(
            preview.PreviewId,
            preview.State,
            preview.RequiresApproval,
            preview.Actions.ToArray(),
            preview.Candidates.Select(ArbitrageCandidateProjection.From).ToArray(),
            preview.Checks.ToArray(),
            preview.CreatedAt,
            preview.ExpiresAt,
            preview.Snapshot.IsStale || preview.State == GatewayState.Stale);
    }
}

/// <summary>Arbitrage query and preview projection backed by IArbitrageGateway.</summary>
public class ArbitrageViewModel : DesktopViewModel
{
    private readonly IArbitrageGateway? _gateway;
    private ArbitrageQuery _query = new();
    private ArbitrageQueryResult? _queryResult;
    private ArbitragePreviewProjection? _preview;
    private ArbitrageCandidateProjection? _selectedCandidate;
    private string? _nextCursor;

    public ArbitrageViewModel(IArbitrageGateway? gateway = null, ArbitrageQuery? initialQuery = null)
    {
        _gateway = gateway;
        if (initialQuery is not null) _query = initialQuery;
        Candidates = new ObservableCollection<ArbitrageCandidateProjection>();
        Diagnostics = new ObservableCollection<GatewayDiagnostic>();
    }

    public ArbitrageQuery Query
    {
        get => _query;
        private set => SetProperty(ref _query, value);
    }

    public ObservableCollection<ArbitrageCandidateProjection> Candidates { get; }
    public ObservableCollection<GatewayDiagnostic> Diagnostics { get; }

    public ArbitrageCandidateProjection? SelectedCandidate
    {
        get => _selectedCandidate;
        set => SetProperty(ref _selectedCandidate, value);
    }

    public ArbitrageQueryResult? Result
    {
        get => _queryResult;
        private set => SetProperty(ref _queryResult, value);
    }

    public ArbitragePreviewProjection? Preview
    {
        get => _preview;
        private set => SetProperty(ref _preview, value);
    }

    public string? NextCursor
    {
        get => _nextCursor;
        private set => SetProperty(ref _nextCursor, value);
    }

    public bool HasCandidates => Candidates.Count > 0;
    public bool HasPreview => Preview is not null;
    public bool HasNextPage => !string.IsNullOrWhiteSpace(NextCursor);

    public void SetQuery(ArbitrageQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        Query = query;
    }

    public Task<ArbitrageQueryResult?> LoadAsync(
        ArbitrageQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        if (query is not null) SetQuery(query);
        var request = Query;
        return LoadCoreAsync(
            token => _gateway is null
                ? Task.FromResult(new ArbitrageQueryResult(GatewayState.Empty))
                : _gateway.QueryAsync(request, token),
            ApplyResult,
            result => StateFor(result.State, result.Candidates.Count > 0),
            result => result.IsStale,
            result => result.Snapshot.ObservedAt == default ? null : result.Snapshot.ObservedAt,
            cancellationToken);
    }

    public Task<ArbitrageQueryResult?> RefreshAsync(CancellationToken cancellationToken = default)
        => LoadAsync(null, cancellationToken);

    public Task<ArbitrageQueryResult?> LoadAsync(CancellationToken cancellationToken)
        => LoadAsync(null, cancellationToken);

    public Task<ArbitragePreview?> PreviewAsync(
        ArbitragePreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return OperationCoreAsync(
            token => _gateway is null
                ? Task.FromResult(new ArbitragePreview { State = GatewayState.Error, Diagnostics = new[] { new GatewayDiagnostic("gateway.missing", "No arbitrage gateway is configured.", true) } })
                : _gateway.PreviewAsync(request, token),
            ApplyPreview,
            cancellationToken,
            preview => preview.State == GatewayState.Error ? DesktopLoadState.Error : DesktopLoadState.Ready);
    }

    public Task<ArbitragePreview?> PlanAsync(
        ArbitragePreviewRequest request,
        CancellationToken cancellationToken = default)
        => PreviewAsync(request, cancellationToken);

    private void ApplyResult(ArbitrageQueryResult result)
    {
        Result = result;
        Candidates.Clear();
        foreach (var candidate in result.Candidates)
        {
            var projection = ArbitrageCandidateProjection.From(candidate);
            Candidates.Add(result.IsStale && !projection.IsStale
                ? projection with { IsStale = true }
                : projection);
        }
        Diagnostics.Clear();
        foreach (var diagnostic in result.Diagnostics)
            Diagnostics.Add(diagnostic);
        NextCursor = result.NextCursor;
        OnPropertyChanged(nameof(HasCandidates));
        OnPropertyChanged(nameof(HasNextPage));
        if (SelectedCandidate is not null)
            SelectedCandidate = Candidates.FirstOrDefault(x => x.ItemId == SelectedCandidate.ItemId);
    }

    private void ApplyPreview(ArbitragePreview preview)
    {
        Preview = ArbitragePreviewProjection.From(preview);
        OnPropertyChanged(nameof(HasPreview));
    }
}

public sealed class ArbitrageState : ArbitrageViewModel
{
    public ArbitrageState(IArbitrageGateway? gateway = null, ArbitrageQuery? initialQuery = null)
        : base(gateway, initialQuery) { }
}
