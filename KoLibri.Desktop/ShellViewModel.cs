using System.Collections.ObjectModel;

namespace KoLibri.Desktop;

public enum ShellPage
{
    Cockpit,
    Arbitrage,
    Shop,
    Approvals,
    Operations
}

public sealed record ShellSnapshot(DateTimeOffset ObservedAt)
{
    public bool IsStale { get; init; }
    public bool HasErrors { get; init; }
    public bool IsEmpty { get; init; }
}

/// <summary>
/// Shared desktop shell state. Navigation is deliberately independent from
/// gateway state so a theme/layout change cannot discard an in-flight query.
/// </summary>
public class ShellViewModel : DesktopViewModel
{
    private ShellPage _currentPage = ShellPage.Cockpit;
    private string? _statusMessage;

    public ShellViewModel(
        CockpitViewModel? cockpit = null,
        ArbitrageViewModel? arbitrage = null,
        ShopViewModel? shop = null,
        ApprovalViewModel? approvals = null,
        OperationsViewModel? operations = null)
    {
        Cockpit = cockpit ?? new CockpitViewModel();
        Arbitrage = arbitrage ?? new ArbitrageViewModel();
        Shop = shop ?? new ShopViewModel();
        Approvals = approvals ?? new ApprovalViewModel();
        Operations = operations ?? new OperationsViewModel();
        Pages = new ReadOnlyCollection<ShellPage>(Enum.GetValues<ShellPage>());
    }

    public CockpitViewModel Cockpit { get; }
    public ArbitrageViewModel Arbitrage { get; }
    public ShopViewModel Shop { get; }
    public ApprovalViewModel Approvals { get; }
    public OperationsViewModel Operations { get; }
    public ReadOnlyCollection<ShellPage> Pages { get; }

    public ShellPage CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public string CurrentPageText => CurrentPage switch
    {
        ShellPage.Cockpit => "Cockpit",
        ShellPage.Arbitrage => "Arbitrage",
        ShellPage.Shop => "Shop",
        ShellPage.Approvals => "Approvals",
        ShellPage.Operations => "Operations",
        _ => CurrentPage.ToString()
    };

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsAnyLoading =>
        Cockpit.IsLoading || Arbitrage.IsLoading || Shop.IsLoading || Approvals.IsLoading || Operations.IsLoading;

    public bool HasStaleData =>
        Cockpit.IsStale || Arbitrage.IsStale || Shop.IsStale || Approvals.IsStale || Operations.IsStale;

    public bool HasErrors =>
        Cockpit.HasError || Arbitrage.HasError || Shop.HasError || Approvals.HasError || Operations.HasError;

    public ObservableCollection<string> StaleSources
    {
        get
        {
            var stale = new ObservableCollection<string>();
            if (Cockpit.IsStale) stale.Add("Cockpit");
            if (Arbitrage.IsStale) stale.Add("Arbitrage");
            if (Shop.IsStale) stale.Add("Shop");
            if (Approvals.IsStale) stale.Add("Approvals");
            if (Operations.IsStale) stale.Add("Operations");
            return stale;
        }
    }

    public void Navigate(ShellPage page)
    {
        CurrentPage = page;
        StatusMessage = $"{CurrentPageText} geöffnet.";
        OnPropertyChanged(nameof(CurrentPageText));
    }

    public void Navigate(string page)
    {
        if (Enum.TryParse<ShellPage>(page, true, out var parsed))
            Navigate(parsed);
    }

    public Task<ShellSnapshot?> LoadAsync(CancellationToken cancellationToken = default)
        => LoadCoreAsync(
            LoadChildrenAsync,
            ApplySnapshot,
            snapshot => snapshot.HasErrors ? DesktopLoadState.Error
                : snapshot.IsEmpty ? DesktopLoadState.Empty
                : snapshot.IsStale ? DesktopLoadState.Stale
                : DesktopLoadState.Ready,
            snapshot => snapshot.IsStale,
            snapshot => snapshot.ObservedAt,
            cancellationToken);

    public Task<ShellSnapshot?> RefreshAsync(CancellationToken cancellationToken = default)
        => LoadAsync(cancellationToken);

    private async Task<ShellSnapshot> LoadChildrenAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(
            Cockpit.LoadAsync(cancellationToken),
            Arbitrage.LoadAsync(cancellationToken: cancellationToken),
            Shop.LoadAsync(cancellationToken),
            Approvals.LoadAsync(cancellationToken),
            Operations.LoadAsync(cancellationToken: cancellationToken)).ConfigureAwait(false);

        return new ShellSnapshot(DateTimeOffset.UtcNow)
        {
            IsStale = HasStaleData,
            HasErrors = HasErrors,
            IsEmpty = !IsAnyLoading
                && Cockpit.LoadState == DesktopLoadState.Empty
                && Arbitrage.LoadState == DesktopLoadState.Empty
                && Shop.LoadState == DesktopLoadState.Empty
                && Approvals.LoadState == DesktopLoadState.Empty
                && Operations.LoadState == DesktopLoadState.Empty
        };
    }

    private void ApplySnapshot(ShellSnapshot snapshot)
    {
        StatusMessage = snapshot.HasErrors
            ? "Snapshot geladen; mindestens eine Quelle meldet einen Fehler."
            : snapshot.IsStale ? "Snapshot geladen; mindestens eine Quelle ist stale." : "Snapshot geladen.";
        OnPropertyChanged(nameof(IsAnyLoading));
        OnPropertyChanged(nameof(HasStaleData));
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(StaleSources));
    }
}

public sealed class ShellState : ShellViewModel
{
    public ShellState(CockpitViewModel? cockpit = null, ArbitrageViewModel? arbitrage = null,
        ShopViewModel? shop = null, ApprovalViewModel? approvals = null,
        OperationsViewModel? operations = null)
        : base(cockpit, arbitrage, shop, approvals, operations) { }
}
