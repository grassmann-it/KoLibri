using System.Collections.ObjectModel;
using LibKolibri;
using KoLibri.Core.Markt;

namespace KoLibri.Desktop;

/// <summary>Own-shop row projected for a virtualized table.</summary>
public sealed record ShopListingProjection
{
    public string ItemId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public long Price { get; init; }
    public int Quantity { get; init; }
    public int? PurchaseLimit { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
    public bool IsStale { get; init; }

    public string PriceText => $"{Price:N0} Meat";
    public string QuantityText => Quantity == 1 ? "1 pc" : $"{Quantity:N0} pcs";
    public string FreshnessText => IsStale ? "stale" : "fresh";

    public static ShopListingProjection From(OwnShopListing listing, bool stale)
    {
        ArgumentNullException.ThrowIfNull(listing);
        return new ShopListingProjection
        {
            ItemId = listing.ItemId,
            DisplayName = listing.DisplayName ?? listing.ItemId,
            Price = listing.Price,
            Quantity = listing.Quantity,
            PurchaseLimit = listing.PurchaseLimit,
            ObservedAt = listing.ObservedAt,
            IsStale = stale
        };
    }
}

public sealed record ShopReadSnapshot(
    IReadOnlyList<OwnShopListing> Listings,
    DateTimeOffset ObservedAt,
    bool IsStale)
{
    public bool IsEmpty => Listings.Count == 0;
}

public sealed record ShopMutationProjection
{
    public string PreviewId { get; init; } = string.Empty;
    public ShopMutationState State { get; init; }
    public bool RequiresApproval { get; init; }
    public IReadOnlyList<ShopMutationItemResult> Results { get; init; } = Array.Empty<ShopMutationItemResult>();
    public ExpectedState Expected { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public bool IsStale { get; init; }
    public bool IsExpired => ExpiresAt is { } expiry && DateTimeOffset.UtcNow >= expiry;

    public static ShopMutationProjection From(ShopMutationPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return new ShopMutationProjection
        {
            PreviewId = preview.PreviewId,
            State = preview.State,
            RequiresApproval = preview.RequiresApproval,
            Results = preview.Results.ToArray(),
            Expected = preview.Expected,
            CreatedAt = preview.CreatedAt,
            ExpiresAt = preview.ExpiresAt,
            IsStale = preview.Snapshot.IsStale
        };
    }
}

/// <summary>Own-shop query and approval-bound reprice preview projection.</summary>
public class ShopViewModel : DesktopViewModel
{
    private readonly IMallGateway? _mallGateway;
    private readonly IShopMutationGateway? _mutationGateway;
    private readonly TimeSpan _staleAfter;
    private ShopListingProjection? _selectedListing;
    private ShopMutationProjection? _preview;
    private ShopReadSnapshot? _readSnapshot;
    private string? _accountId;

    public ShopViewModel(
        IMallGateway? mallGateway = null,
        IShopMutationGateway? mutationGateway = null,
        TimeSpan? staleAfter = null)
    {
        _mallGateway = mallGateway;
        _mutationGateway = mutationGateway;
        _staleAfter = staleAfter ?? TimeSpan.FromMinutes(20);
        if (_staleAfter <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(staleAfter));
        Listings = new ObservableCollection<ShopListingProjection>();
        Diagnostics = new ObservableCollection<GatewayDiagnostic>();
        Changes = new ObservableCollection<ShopPriceChange>();
    }

    public ObservableCollection<ShopListingProjection> Listings { get; }
    public ObservableCollection<GatewayDiagnostic> Diagnostics { get; }
    public ObservableCollection<ShopPriceChange> Changes { get; }

    public ShopListingProjection? SelectedListing
    {
        get => _selectedListing;
        set => SetProperty(ref _selectedListing, value);
    }

    public ShopMutationProjection? Preview
    {
        get => _preview;
        private set => SetProperty(ref _preview, value);
    }

    public ShopReadSnapshot? Result
    {
        get => _readSnapshot;
        private set => SetProperty(ref _readSnapshot, value);
    }

    public string? AccountId
    {
        get => _accountId;
        set => SetProperty(ref _accountId, value);
    }

    public bool HasListings => Listings.Count > 0;
    public bool HasPreview => Preview is not null;
    public TimeSpan StaleAfter => _staleAfter;

    public Task<ShopReadSnapshot?> LoadAsync(CancellationToken cancellationToken = default)
        => LoadCoreAsync(
            LoadSnapshotAsync,
            ApplySnapshot,
            snapshot => snapshot.IsEmpty ? DesktopLoadState.Empty : snapshot.IsStale ? DesktopLoadState.Stale : DesktopLoadState.Ready,
            snapshot => snapshot.IsStale,
            snapshot => snapshot.ObservedAt,
            cancellationToken);

    public Task<ShopReadSnapshot?> RefreshAsync(CancellationToken cancellationToken = default)
        => LoadAsync(cancellationToken);

    public Task<ShopMutationPreview?> PreviewAsync(
        ShopMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return OperationCoreAsync(
            token => _mutationGateway is null
                ? Task.FromResult(new ShopMutationPreview
                {
                    State = ShopMutationState.Failed,
                    Diagnostics = new[] { new GatewayDiagnostic("gateway.missing", "No shop mutation gateway is configured.", true) }
                })
                : _mutationGateway.PreviewAsync(request, token),
            ApplyPreview,
            cancellationToken,
            preview => preview.State == ShopMutationState.Failed ? DesktopLoadState.Error : DesktopLoadState.Ready);
    }

    public Task<ShopMutationPreview?> PlanAsync(
        ShopMutationRequest request,
        CancellationToken cancellationToken = default)
        => PreviewAsync(request, cancellationToken);

    public Task<ShopMutationPreview?> PreviewChangesAsync(
        IEnumerable<ShopPriceChange> changes,
        string? accountId = null,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var request = new ShopMutationRequest(changes, accountId ?? AccountId, reason);
        request.Validate();
        return PreviewAsync(request, cancellationToken);
    }

    private async Task<ShopReadSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        var listings = _mallGateway is null
            ? Array.Empty<OwnShopListing>()
            : await _mallGateway.GetOwnShopAsync(cancellationToken).ConfigureAwait(false);
        var values = listings ?? Array.Empty<OwnShopListing>();
        var observedAt = values.Count == 0
            ? DateTimeOffset.UtcNow
            : values.Max(item => item.ObservedAt);
        var stale = values.Count > 0 && observedAt != default && DateTimeOffset.UtcNow - observedAt > _staleAfter;
        return new ShopReadSnapshot(values.ToArray(), observedAt, stale);
    }

    private void ApplySnapshot(ShopReadSnapshot snapshot)
    {
        Result = snapshot;
        Listings.Clear();
        foreach (var listing in snapshot.Listings)
            Listings.Add(ShopListingProjection.From(listing, snapshot.IsStale));
        Diagnostics.Clear();
        OnPropertyChanged(nameof(HasListings));
        if (SelectedListing is not null)
            SelectedListing = Listings.FirstOrDefault(x => x.ItemId == SelectedListing.ItemId);
    }

    private void ApplyPreview(ShopMutationPreview preview)
    {
        Preview = ShopMutationProjection.From(preview);
        Diagnostics.Clear();
        foreach (var diagnostic in preview.Diagnostics)
            Diagnostics.Add(diagnostic);
        OnPropertyChanged(nameof(HasPreview));
    }
}

public sealed class ShopState : ShopViewModel
{
    public ShopState(IMallGateway? mallGateway = null, IShopMutationGateway? mutationGateway = null,
        TimeSpan? staleAfter = null)
        : base(mallGateway, mutationGateway, staleAfter) { }
}
