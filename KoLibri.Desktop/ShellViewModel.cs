using System.Collections.ObjectModel;

namespace KoLibri.Desktop;

public enum ShellPage
{
    Cockpit,
    Inventory,
    Arbitrage,
    Shop,
    Approvals,
    Operations,
    Adventure,
    Combat
}

/// <summary>
/// Stable metadata for a shell page.  Keeping title and description in the
/// ViewModel lets compact and full navigation render the same contract while
/// the legacy MainWindow can continue to use its existing handlers.
/// </summary>
public sealed record ShellPageContract(
    ShellPage Page,
    string Title,
    string Description,
    string AutomationName,
    bool IsReadOnly = true);

/// <summary>Command metadata shared by menu, shortcut help and future key routing.</summary>
public sealed record ShellShortcutContract(
    string CommandId,
    string Label,
    string Gesture,
    ShellPage Page,
    bool Configurable = true);

/// <summary>Context-menu action metadata for the selected row or encounter.</summary>
public sealed record ShellContextActionContract(
    string CommandId,
    string Label,
    ShellPage Page,
    bool RequiresApproval = false);

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
        InventoryViewModel? inventory = null,
        ArbitrageViewModel? arbitrage = null,
        ShopViewModel? shop = null,
        ApprovalViewModel? approvals = null,
        OperationsViewModel? operations = null,
        AdventureViewModel? adventure = null,
        CombatViewModel? combat = null)
    {
        Cockpit = cockpit ?? new CockpitViewModel();
        Inventory = inventory ?? new InventoryViewModel();
        Arbitrage = arbitrage ?? new ArbitrageViewModel();
        Shop = shop ?? new ShopViewModel();
        Approvals = approvals ?? new ApprovalViewModel();
        Operations = operations ?? new OperationsViewModel();
        Adventure = adventure ?? new AdventureViewModel();
        Combat = combat ?? new CombatViewModel();
        Pages = new ReadOnlyCollection<ShellPage>(Enum.GetValues<ShellPage>());
        PageContracts = new ReadOnlyCollection<ShellPageContract>(new[]
        {
            new ShellPageContract(ShellPage.Cockpit, "Cockpit", "Account and inventory snapshot for Main and Mallbot.", "Open cockpit"),
            new ShellPageContract(ShellPage.Inventory, "Inventory", "Live inventory with search, categories and item detail.", "Open inventory"),
            new ShellPageContract(ShellPage.Arbitrage, "Arbitrage", "Price sources and buy candidates as a read-only preview.", "Open arbitrage"),
            new ShellPageContract(ShellPage.Shop, "Shop", "Own listings and reprice previews.", "Open shop"),
            new ShellPageContract(ShellPage.Approvals, "Approvals", "Approval inbox and verification status.", "Open approvals"),
            new ShellPageContract(ShellPage.Operations, "Operations", "Audit and operations ledger.", "Open operations"),
            new ShellPageContract(ShellPage.Adventure, "Adventure", "Daily plan, encounter and safe resume actions.", "Open adventure"),
            new ShellPageContract(ShellPage.Combat, "Combat", "Manual single action and auto-combat policy preview.", "Open combat")
        });
        Shortcuts = new ReadOnlyCollection<ShellShortcutContract>(new[]
        {
            new ShellShortcutContract("navigation.overview", "Guts", "Ctrl+1", ShellPage.Cockpit),
            new ShellShortcutContract("navigation.inventory", "Inventory", "Ctrl+2", ShellPage.Inventory),
            new ShellShortcutContract("navigation.arbitrage", "Arbitrage", "Ctrl+3", ShellPage.Arbitrage),
            new ShellShortcutContract("navigation.shop", "Shop", "Ctrl+4", ShellPage.Shop),
            new ShellShortcutContract("navigation.adventure", "Adventure", "Ctrl+5", ShellPage.Adventure),
            new ShellShortcutContract("navigation.combat", "Combat", "Ctrl+6", ShellPage.Combat),
            new ShellShortcutContract("shell.refresh", "Refresh data", "F5", ShellPage.Cockpit),
            new ShellShortcutContract("shell.shortcut-help", "Keyboard help", "F1", ShellPage.Cockpit),
            new ShellShortcutContract("shell.stop", "Stop run", "Escape", ShellPage.Adventure)
        });
        ContextActions = new ReadOnlyCollection<ShellContextActionContract>(new[]
        {
            new ShellContextActionContract("adventure.inspect", "Inspect encounter", ShellPage.Adventure),
            new ShellContextActionContract("adventure.refresh", "Refresh encounter", ShellPage.Adventure),
            new ShellContextActionContract("combat.manual-preview", "Preview manual combat action", ShellPage.Combat),
            new ShellContextActionContract("combat.stop", "Stop combat", ShellPage.Combat),
            new ShellContextActionContract("shop.price-detail", "Load detail price", ShellPage.Shop),
            new ShellContextActionContract("shop.reprice-preview", "Create reprice preview", ShellPage.Shop)
        });
    }

    public CockpitViewModel Cockpit { get; }
    public InventoryViewModel Inventory { get; }
    public ArbitrageViewModel Arbitrage { get; }
    public ShopViewModel Shop { get; }
    public ApprovalViewModel Approvals { get; }
    public OperationsViewModel Operations { get; }
    public AdventureViewModel Adventure { get; }
    public CombatViewModel Combat { get; }
    public ReadOnlyCollection<ShellPage> Pages { get; }
    public ReadOnlyCollection<ShellPageContract> PageContracts { get; }
    public ReadOnlyCollection<ShellShortcutContract> Shortcuts { get; }
    public ReadOnlyCollection<ShellContextActionContract> ContextActions { get; }

    /// <summary>Alias used by hosts that call the Adventure page an executor.</summary>
    public AdventureViewModel AdventurePage => Adventure;

    /// <summary>Alias used by hosts that call the Combat page a fight page.</summary>
    public CombatViewModel CombatPage => Combat;

    public ShellPage CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public string CurrentPageText => CurrentPage switch
    {
        ShellPage.Cockpit => "Cockpit",
        ShellPage.Inventory => "Inventory",
        ShellPage.Arbitrage => "Arbitrage",
        ShellPage.Shop => "Shop",
        ShellPage.Approvals => "Approvals",
        ShellPage.Operations => "Operations",
        ShellPage.Adventure => "Adventure",
        ShellPage.Combat => "Combat",
        _ => CurrentPage.ToString()
    };

    public ShellPageContract CurrentPageContract
        => PageContracts.First(contract => contract.Page == CurrentPage);

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsAnyLoading =>
        Cockpit.IsLoading || Inventory.IsLoading || Arbitrage.IsLoading || Shop.IsLoading || Approvals.IsLoading || Operations.IsLoading
        || Adventure.IsLoading || Combat.IsLoading;

    public bool HasStaleData =>
        Cockpit.IsStale || Inventory.IsStale || Arbitrage.IsStale || Shop.IsStale || Approvals.IsStale || Operations.IsStale
        || Adventure.IsStaleData || Combat.IsStaleData;

    public bool HasErrors =>
        Cockpit.HasError || Inventory.HasError || Arbitrage.HasError || Shop.HasError || Approvals.HasError || Operations.HasError
        || Adventure.HasError || Combat.HasError;

    public ObservableCollection<string> StaleSources
    {
        get
        {
            var stale = new ObservableCollection<string>();
            if (Cockpit.IsStale) stale.Add("Cockpit");
            if (Inventory.IsStale) stale.Add("Inventory");
            if (Arbitrage.IsStale) stale.Add("Arbitrage");
            if (Shop.IsStale) stale.Add("Shop");
            if (Approvals.IsStale) stale.Add("Approvals");
            if (Operations.IsStale) stale.Add("Operations");
            if (Adventure.IsStaleData) stale.Add("Adventure");
            if (Combat.IsStaleData) stale.Add("Combat");
            return stale;
        }
    }

    public void Navigate(ShellPage page)
    {
        CurrentPage = page;
        StatusMessage = $"{CurrentPageText} opened.";
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
            Inventory.LoadAsync(cancellationToken: cancellationToken),
            Arbitrage.LoadAsync(cancellationToken: cancellationToken),
            Shop.LoadAsync(cancellationToken),
            Approvals.LoadAsync(cancellationToken),
            Operations.LoadAsync(cancellationToken),
            Adventure.LoadAsync(cancellationToken),
            Combat.LoadAsync(cancellationToken)).ConfigureAwait(false);

        return new ShellSnapshot(DateTimeOffset.UtcNow)
        {
            IsStale = HasStaleData,
            HasErrors = HasErrors,
            IsEmpty = !IsAnyLoading
                && Cockpit.LoadState == DesktopLoadState.Empty
                && Inventory.LoadState == DesktopLoadState.Empty
                && Arbitrage.LoadState == DesktopLoadState.Empty
                && Shop.LoadState == DesktopLoadState.Empty
                && Approvals.LoadState == DesktopLoadState.Empty
                && Operations.LoadState == DesktopLoadState.Empty
                && Adventure.LoadState == DesktopLoadState.Empty
                && Combat.LoadState == DesktopLoadState.Empty
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
        OnPropertyChanged(nameof(CurrentPageContract));
    }
}

public sealed class ShellState : ShellViewModel
{
    public ShellState(CockpitViewModel? cockpit = null, InventoryViewModel? inventory = null,
        ArbitrageViewModel? arbitrage = null, ShopViewModel? shop = null,
        ApprovalViewModel? approvals = null, OperationsViewModel? operations = null,
        AdventureViewModel? adventure = null, CombatViewModel? combat = null)
        : base(cockpit, inventory, arbitrage, shop, approvals, operations, adventure, combat) { }
}
