using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using KolArbitrage.Core.Accounts;
using KolArbitrage.Core.Adventure;
using KolArbitrage.Core.Daily;
using KolArbitrage.Core.Integration;
using KolArbitrage.Core.Analytics;
using KolArbitrage.Core.Data;
using KolArbitrage.Core.Integration;

namespace KoLibri.Desktop;

public partial class MainWindow : Window
{
    private StackPanel? _contentRoot;
    private StackPanel? _overviewPanel;
    private Button? _themeToggleButton;
    private bool _isOpsDark;
    private TextBlock? _statusText;
    private Border? _statusBar;
    private Button? _navOverview;
    private Button? _navInventory;
    private Button? _navDaily;
    private Button? _navArbitrage;
    private Button? _navMall;
    private Button? _navAdventure;
    private Button? _navCombat;
    private TextBlock? _shellPageTitle;
    private TextBlock? _shellPageDescription;
    private Border? _pageStatePanel;
    private TextBlock? _pageStateTitle;
    private TextBlock? _pageStateDescription;
    private TextBlock? _pageStateStatus;
    private Border? _accountsPanel;
    private Border? _mallPanel;
    private Border? _adventurePanel;
    private Border? _combatPanel;
    private TextBlock? _mallRoleLabel;
    private TextBlock? _mallSummary;
    private TextBlock? _mallRows;
    private TextBlock? _adventureAccountLabel;
    private TextBlock? _adventureTurnsLabel;
    private TextBlock? _adventureKnucklebonesLabel;
    private TextBlock? _adventurePlanSummary;
    private TextBlock? _adventurePlanAdvisories;
    private TextBlock? _combatPreviewSummary;
    private ComboBox? _accountRoleSelector;
    private Button? _globalRefreshButton;
    private ProgressBar? _busyIndicator;
    private Button? _cancelRefreshButton;
    private CancellationTokenSource? _refreshCancellation;
    private string _activeSection = "Guts";
    private AccountRole _selectedRole = AccountRole.Mallbot;
    private readonly AccountSessionManager _accounts = AccountSessionManager.CreateDefault();
    private readonly KoLLoginClient _loginClient = new();
    private EncounterBrowserPane? _encounterBrowser;
    private ListBox? _encounterLocationsGrid;
    private ListBox? _encounterMonstersGrid;
    private ListBox? _encounterActionsGrid;
    private TextBox? _encounterFilterBox;
    private TextBox? _encounterTokenBox;
    private Button? _encounterStartButton;
    private Button? _encounterSubmitButton;
    private Button? _encounterEndButton;
    private Button? _encounterSaveTokenButton;
    private Button? _encounterLoadTokenButton;
    private TextBlock? _encounterStatusText;
    private TextBlock? _encounterStepLogText;

    // Inventory browser.
    private StackPanel? _inventoryPanel;
    private TextBox? _inventorySearchBox;
    private TextBlock? _inventorySummaryText;
    private TextBlock? _inventoryLoadedText;
    private ListBox? _inventoryList;
    private StackPanel? _inventoryCategoryStrip;
    private StackPanel? _inventoryDetailPanel;
    private InventoryViewModel _inventory = new();
    private DailyBriefingViewModel _daily = new();
    private LiveGutsViewModel _guts = new();
    private LiveArbitrageViewModel _arbitrage = new();
    private LiveMallViewModel _mall = new();
    private AutoCombatViewModel _autoCombat = new();
    private StackPanel? _mallSection;
    private StackPanel? _combatSection;
    private ListBox? _mallListingRows;
    private TextBlock? _mallSummaryText;
    private ListBox? _combatTargetList;
    private ComboBox? _combatSkillPicker;
    private ComboBox? _combatItemPicker;
    private ListBox? _combatSkillList;
    private ListBox? _combatItemList;
    private NumericUpDown? _combatAbortHp;
    private NumericUpDown? _combatMaxRounds;
    private TextBlock? _combatPreviewText;
    private StackPanel? _gutsPanel;
    private StackPanel? _arbitragePanel;
    private ItemsControl? _gutsAccountCards;
    private TextBlock? _gutsSummaryText;
    private TextBlock? _storeSummaryText;
    private ListBox? _storeRows;
    private ListBox? _arbitrageRows;
    private TextBlock? _arbitrageSummaryText;
    private StackPanel? _dailyPanel;
    private ListBox? _dailyRows;
    private TextBlock? _dailySnapshotText;
    private TextBlock? _dailySummaryText;
    private TextBlock? _dailyStatusText;
    private TextBox? _dailyTokenBox;

    public MainWindow()
    {
        InitializeComponent();
        _contentRoot = this.FindControl<StackPanel>("ContentRoot");
        _overviewPanel = this.FindControl<StackPanel>("OverviewPanel");
        _inventoryPanel = this.FindControl<StackPanel>("InventoryPanel");
        _inventorySearchBox = this.FindControl<TextBox>("InventorySearchBox");
        _inventorySummaryText = this.FindControl<TextBlock>("InventorySummaryText");
        _inventoryLoadedText = this.FindControl<TextBlock>("InventoryLoadedText");
        _inventoryList = this.FindControl<ListBox>("InventoryList");
        _inventoryCategoryStrip = this.FindControl<StackPanel>("InventoryCategoryStrip");
        _inventoryDetailPanel = this.FindControl<StackPanel>("InventoryDetailPanel");
        _navOverview = this.FindControl<Button>("NavOverview");
        _navInventory = this.FindControl<Button>("NavInventory");
        _navDaily = this.FindControl<Button>("NavDaily");
        _navArbitrage = this.FindControl<Button>("NavArbitrage");
        _navMall = this.FindControl<Button>("NavMall");
        _navAdventure = this.FindControl<Button>("NavAdventure");
        _navCombat = this.FindControl<Button>("NavCombat");
        _dailyPanel = this.FindControl<StackPanel>("DailyPanel");
        _gutsPanel = this.FindControl<StackPanel>("GutsPanel");
        _arbitragePanel = this.FindControl<StackPanel>("ArbitragePanel");
        _gutsAccountCards = this.FindControl<ItemsControl>("GutsAccountCards");
        _gutsSummaryText = this.FindControl<TextBlock>("GutsSummaryText");
        _storeSummaryText = this.FindControl<TextBlock>("StoreSummaryText");
        _storeRows = this.FindControl<ListBox>("StoreRows");
        _arbitrageRows = this.FindControl<ListBox>("ArbitrageRows");
        _arbitrageSummaryText = this.FindControl<TextBlock>("ArbitrageSummaryText");
        _mallSection = this.FindControl<StackPanel>("MallSection");
        _combatSection = this.FindControl<StackPanel>("CombatSection");
        _mallListingRows = this.FindControl<ListBox>("MallListingRows");
        _mallSummaryText = this.FindControl<TextBlock>("MallSummaryText");
        _combatTargetList = this.FindControl<ListBox>("CombatTargetList");
        _combatSkillPicker = this.FindControl<ComboBox>("CombatSkillPicker");
        _combatItemPicker = this.FindControl<ComboBox>("CombatItemPicker");
        _combatSkillList = this.FindControl<ListBox>("CombatSkillList");
        _combatItemList = this.FindControl<ListBox>("CombatItemList");
        _combatAbortHp = this.FindControl<NumericUpDown>("CombatAbortHp");
        _combatMaxRounds = this.FindControl<NumericUpDown>("CombatMaxRounds");
        _combatPreviewText = this.FindControl<TextBlock>("CombatPreviewText");
        _dailyRows = this.FindControl<ListBox>("DailyRows");
        _dailySnapshotText = this.FindControl<TextBlock>("DailySnapshotText");
        _dailySummaryText = this.FindControl<TextBlock>("DailySummaryText");
        _dailyStatusText = this.FindControl<TextBlock>("DailyStatusText");
        _dailyTokenBox = this.FindControl<TextBox>("DailyTokenBox");
        WireInventoryBrowser();
        _themeToggleButton = this.FindControl<Button>("ThemeToggleButton");
        _statusText = this.FindControl<TextBlock>("StatusText");
        _statusBar = this.FindControl<Border>("StatusBar");
        _shellPageTitle = this.FindControl<TextBlock>("ShellPageTitle");
        _shellPageDescription = this.FindControl<TextBlock>("ShellPageDescription");
        _pageStatePanel = this.FindControl<Border>("PageStatePanel");
        _pageStateTitle = this.FindControl<TextBlock>("PageStateTitle");
        _pageStateDescription = this.FindControl<TextBlock>("PageStateDescription");
        _pageStateStatus = this.FindControl<TextBlock>("PageStateStatus");
        _accountsPanel = this.FindControl<Border>("AccountsPanel");
        _mallPanel = this.FindControl<Border>("MallPanel");
        _adventurePanel = this.FindControl<Border>("AdventurePanel");
        _combatPanel = this.FindControl<Border>("CombatPanel");
        _encounterLocationsGrid = this.FindControl<ListBox>("EncounterLocationsGrid");
        _encounterMonstersGrid = this.FindControl<ListBox>("EncounterMonstersGrid");
        _encounterActionsGrid = this.FindControl<ListBox>("EncounterActionsGrid");
        _encounterFilterBox = this.FindControl<TextBox>("EncounterFilterBox");
        _encounterTokenBox = this.FindControl<TextBox>("EncounterTokenBox");
        _encounterStartButton = this.FindControl<Button>("EncounterStartButton");
        _encounterSubmitButton = this.FindControl<Button>("EncounterSubmitButton");
        _encounterEndButton = this.FindControl<Button>("EncounterEndButton");
        _encounterSaveTokenButton = this.FindControl<Button>("EncounterSaveTokenButton");
        _encounterLoadTokenButton = this.FindControl<Button>("EncounterLoadTokenButton");
        _encounterStatusText = this.FindControl<TextBlock>("EncounterStatusText");
        _encounterStepLogText = this.FindControl<TextBlock>("EncounterStepLogText");
        WireEncounterBrowser();
        _mallRoleLabel = this.FindControl<TextBlock>("MallRoleLabel");
        _mallSummary = this.FindControl<TextBlock>("MallSummary");
        _mallRows = this.FindControl<TextBlock>("MallRows");
        _adventureAccountLabel = this.FindControl<TextBlock>("AdventureAccountLabel");
        _adventureTurnsLabel = this.FindControl<TextBlock>("AdventureTurnsLabel");
        _adventureKnucklebonesLabel = this.FindControl<TextBlock>("AdventureKnucklebonesLabel");
        _adventurePlanSummary = this.FindControl<TextBlock>("AdventurePlanSummary");
        _adventurePlanAdvisories = this.FindControl<TextBlock>("AdventurePlanAdvisories");
        _combatPreviewSummary = this.FindControl<TextBlock>("CombatPreviewSummary");
        _accountRoleSelector = this.FindControl<ComboBox>("AccountRoleSelector");
        _globalRefreshButton = this.FindControl<Button>("GlobalRefreshButton");
        _busyIndicator = this.FindControl<ProgressBar>("BusyIndicator");
        _cancelRefreshButton = this.FindControl<Button>("CancelRefreshButton");
        _selectedRole = ReadSelectedRole();
        SetActiveNavigation(_activeSection);
        UpdatePageState(_activeSection);
        SizeChanged += WindowSizeChanged;
        UpdateResponsiveLayout(Bounds.Width > 0 ? Bounds.Width : Width);
        Opened += (_, _) => UpdateResponsiveLayout(Bounds.Width > 0 ? Bounds.Width : Width);
        _ = LoadSavedAccountsAsync();
    }

    private void InitializeComponent() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);

    private void WindowKeyDown(object? sender, KeyEventArgs e)
    {
        // Let text editors keep ordinary typing and only consume the explicit
        // application gestures defined by the shared UX catalog.
        if (e.Key == Key.F1)
        {
            ShowShortcutHelp();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F5 && e.KeyModifiers == KeyModifiers.None)
        {
            RefreshClick(sender, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift)
        {
            ContextPreviewClick(sender, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.Control)
        {
            CancelRefreshClick(sender, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers == KeyModifiers.Control && TryNavigateShortcut(e.Key))
        {
            e.Handled = true;
        }
    }

    private bool TryNavigateShortcut(Key key)
    {
        var section = key switch
        {
            Key.D1 or Key.NumPad1 => "Guts",
            Key.D2 or Key.NumPad2 => "Pricing Engine",
            Key.D3 or Key.NumPad3 => "Mall & Shop",
            Key.D4 or Key.NumPad4 => "Adventure",
            Key.D5 or Key.NumPad5 => "Combat",
            Key.D6 or Key.NumPad6 => "Accounts & Transfer",
            Key.D7 or Key.NumPad7 => "Settings",
            _ => null
        };
        if (section is null)
            return false;

        Navigate(section);
        return true;
    }

    private void ShowShortcutHelp()
    {
        SetStatus("Keyboard: Ctrl+1..7 sections · F5 refresh · Shift+F10 context preview · Ctrl+Esc cancel · F1 help.");
    }

    private void WindowSizeChanged(object? sender, SizeChangedEventArgs e)
        => UpdateResponsiveLayout(e.NewSize.Width);

    private void UpdateResponsiveLayout(double width)
    {
        // The menu bar stays in one row; only the content margin shrinks on
        // narrow windows so the item list keeps as much width as possible.
        var compact = width > 0 && width < 1080;
        if (_contentRoot is not null)
            _contentRoot.Margin = compact ? new Thickness(20, 20) : new Thickness(34, 30);
    }

    private void ThemeToggleClick(object? sender, RoutedEventArgs e)
    {
        _isOpsDark = !_isOpsDark;
        if (Application.Current is not null)
            Application.Current.RequestedThemeVariant = _isOpsDark ? ThemeVariant.Dark : ThemeVariant.Light;

        var themeName = _isOpsDark ? "Ops Dark" : "Stickman Standard";
        var buttonText = _isOpsDark ? "☀" : "☾";
        if (_themeToggleButton is not null)
            _themeToggleButton.Content = buttonText;
        SetStatus($"Theme “{themeName}” activated.");
    }

    private void Navigate(string section)
    {
        _activeSection = section;
        SetActiveNavigation(section);
        UpdatePageState(section);
        SetStatus($"Section “{section}” selected.");
    }

    /// <summary>
    /// Marks the menu entry of the current section. KoLmafia highlights the
    /// open tab, and the menu bar is the tab strip here.
    /// </summary>
    private void SetActiveNavigation(string section)
    {
        var entries = new (Button? Button, string Section)[]
        {
            (_navOverview, "Guts"),
            (_navInventory, "Inventory"),
            (_navDaily, "Daily"),
            (_navArbitrage, "Arbitrage"),
            (_navMall, "Mall & Shop"),
            (_navAdventure, "Adventure"),
            (_navCombat, "Combat")
        };
        foreach (var (button, tag) in entries)
        {
            if (button is null) continue;
            var active = string.Equals(section, tag, StringComparison.Ordinal);
            if (active) button.Classes.Add("active");
            else button.Classes.Remove("active");
        }
    }
    private void OverviewClick(object? sender, RoutedEventArgs e) => Navigate("Guts");

    /// <summary>Opens the guts: the live account cards and the own store.</summary>
    private async void GutsClick(object? sender, RoutedEventArgs e)
    {
        Navigate("Guts");
        await RunBusyOperationAsync("Guts laden", cancellationToken =>
            _guts.LoadAsync(cancellationToken: cancellationToken));
        if (_gutsAccountCards is not null) _gutsAccountCards.ItemsSource = _guts.Accounts;
        if (_storeRows is not null) _storeRows.ItemsSource = _guts.StoreRows;
        if (_gutsSummaryText is not null) _gutsSummaryText.Text = _guts.GutsSummary;
        if (_storeSummaryText is not null) _storeSummaryText.Text = _guts.StoreSummary;
    }

    /// <summary>Opens the arbitrage table: live mall listings against the
    /// pricegun median.</summary>
    private async void ArbitrageClick(object? sender, RoutedEventArgs e)
    {
        Navigate("Arbitrage");
        await RunBusyOperationAsync("Arbitrage laden", cancellationToken =>
            _arbitrage.LoadAsync(cancellationToken: cancellationToken));
        if (_arbitrageRows is not null) _arbitrageRows.ItemsSource = _arbitrage.Rows;
        if (_arbitrageSummaryText is not null) _arbitrageSummaryText.Text = _arbitrage.SummaryText;
    }

    private void PricingClick(object? sender, RoutedEventArgs e) => Navigate("Pricing Engine");
    private async void MallClick(object? sender, RoutedEventArgs e)
    {
        Navigate("Mall & Shop");
        await RunBusyOperationAsync("Loading mall", cancellationToken =>
            _mall.LoadAsync(cancellationToken: cancellationToken));
        if (_mallListingRows is not null) _mallListingRows.ItemsSource = _mall.Rows;
        if (_mallSummaryText is not null) _mallSummaryText.Text = _mall.Summary;
    }
    private async void AccountsClick(object? sender, RoutedEventArgs e)
    {
        Navigate("Accounts & Transfer");
        await RunBusyOperationAsync("Loading accounts", LoadSavedAccountsAsync);
    }
    private async void AdventureClick(object? sender, RoutedEventArgs e)
    {
        Navigate("Adventure");
        await RunBusyOperationAsync("Loading adventure plan", cancellationToken => RefreshAdventureAsync(false, cancellationToken));
    }
    private void CombatClick(object? sender, RoutedEventArgs e)
    {
        Navigate("Combat");
        if (_combatPreviewSummary is not null)
            _combatPreviewSummary.Text = "Keine Observation geladen. Preview bleibt blockiert, bis Monster, Runde und legale Actions frisch gelesen wurden.";
    }

    /// <summary>Opens the auto-combat configurator with the catalog
    /// targets, skills and items.</summary>
    private async void AutoCombatClick(object? sender, RoutedEventArgs e)
    {
        Navigate("Combat");
        await RunBusyOperationAsync("Loading combat catalog", _autoCombat.LoadAsync);
        if (_combatTargetList is not null) _combatTargetList.ItemsSource = _autoCombat.Targets;
        if (_combatSkillPicker is not null)
        {
            _combatSkillPicker.ItemsSource = _autoCombat.Skills;
            _combatSkillPicker.ItemTemplate = BuildOptionTemplate();
        }
        if (_combatItemPicker is not null)
        {
            _combatItemPicker.ItemsSource = _autoCombat.Items;
            _combatItemPicker.ItemTemplate = BuildOptionTemplate();
        }
        if (_combatSkillList is not null) _combatSkillList.ItemsSource = _autoCombat.SelectedSkills;
        if (_combatItemList is not null) _combatItemList.ItemsSource = _autoCombat.SelectedItems;
        if (_combatAbortHp is not null) _combatAbortHp.Value = _autoCombat.AbortHpPercent;
        if (_combatMaxRounds is not null) _combatMaxRounds.Value = _autoCombat.MaxRounds;
        if (_combatPreviewText is not null) _combatPreviewText.Text = _autoCombat.PreviewText;
    }

    private static Avalonia.Controls.Templates.IDataTemplate BuildOptionTemplate()
        => new Avalonia.Controls.Templates.FuncDataTemplate<CombatOption>((option, _) =>
            new TextBlock { Text = option?.Name ?? string.Empty, Margin = new Avalonia.Thickness(4, 2) });

    private void CombatTargetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_combatTargetList?.SelectedItem is CombatTarget target)
            _autoCombat.SelectedTarget = target;
        if (_combatPreviewText is not null) _combatPreviewText.Text = _autoCombat.PreviewText;
    }

    private void CombatSkillPicked(object? sender, SelectionChangedEventArgs e)
    {
        if (_combatSkillPicker?.SelectedItem is CombatOption skill)
            _autoCombat.SelectedSkill = skill;
        if (_combatSkillList is not null) _combatSkillList.ItemsSource = _autoCombat.SelectedSkills;
        if (_combatPreviewText is not null) _combatPreviewText.Text = _autoCombat.PreviewText;
    }

    private void CombatItemPicked(object? sender, SelectionChangedEventArgs e)
    {
        if (_combatItemPicker?.SelectedItem is CombatOption item)
            _autoCombat.SelectedItem = item;
        if (_combatItemList is not null) _combatItemList.ItemsSource = _autoCombat.SelectedItems;
        if (_combatPreviewText is not null) _combatPreviewText.Text = _autoCombat.PreviewText;
    }

    private void CombatAbortChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (e.NewValue is { } value) _autoCombat.AbortHpPercent = (int)value;
        if (_combatPreviewText is not null) _combatPreviewText.Text = _autoCombat.PreviewText;
    }

    private void CombatRoundsChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (e.NewValue is { } value) _autoCombat.MaxRounds = (int)value;
        if (_combatPreviewText is not null) _combatPreviewText.Text = _autoCombat.PreviewText;
    }
    private void SettingsClick(object? sender, RoutedEventArgs e) => Navigate("Settings");
    private void ContextPreviewClick(object? sender, RoutedEventArgs e)
    {
        SetStatus($"Context preview for “{_activeSection}” opened. No KoL action was sent.");
    }

    private void ContextRefreshClick(object? sender, RoutedEventArgs e)
    {
        SetStatus($"Context “{_activeSection}” marked. The refresh runs through the section refresh.");
    }
    private async void RefreshClick(object? sender, RoutedEventArgs e)
    {
        switch (_activeSection)
        {
            case "Guts":
                await RunBusyOperationAsync("Loading guts", cancellationToken =>
                    _guts.LoadAsync(forceRefresh: true, cancellationToken));
                if (_gutsAccountCards is not null) _gutsAccountCards.ItemsSource = _guts.Accounts;
                if (_storeRows is not null) _storeRows.ItemsSource = _guts.StoreRows;
                if (_gutsSummaryText is not null) _gutsSummaryText.Text = _guts.GutsSummary;
                if (_storeSummaryText is not null) _storeSummaryText.Text = _guts.StoreSummary;
                break;
            case "Arbitrage":
                await RunBusyOperationAsync("Loading arbitrage", cancellationToken =>
                    _arbitrage.LoadAsync(forceRefresh: true, cancellationToken));
                if (_arbitrageRows is not null) _arbitrageRows.ItemsSource = _arbitrage.Rows;
                if (_arbitrageSummaryText is not null) _arbitrageSummaryText.Text = _arbitrage.SummaryText;
                break;
            case "Inventory":
                await RunBusyOperationAsync("Loading inventory", cancellationToken =>
                    _inventory.LoadAsync(forceRefresh: true, cancellationToken));
                if (_inventoryList is not null) _inventoryList.ItemsSource = _inventory.Items;
                if (_inventorySummaryText is not null) _inventorySummaryText.Text = _inventory.SummaryText;
                if (_inventoryLoadedText is not null) _inventoryLoadedText.Text = _inventory.LoadedAtText;
                RenderInventoryDetail();
                break;
            case "Daily":
                await RunBusyOperationAsync("Loading daily plan", cancellationToken =>
                    _daily.LoadAsync(forceRefresh: true, cancellationToken));
                if (_dailyRows is not null) _dailyRows.ItemsSource = _daily.Rows;
                if (_dailySnapshotText is not null) _dailySnapshotText.Text = _daily.SnapshotText;
                if (_dailySummaryText is not null) _dailySummaryText.Text = _daily.SummaryText;
                break;
            case "Mall & Shop":
                await RunBusyOperationAsync("Loading mall", cancellationToken =>
                    _mall.LoadAsync(forceRefresh: true, cancellationToken));
                if (_mallListingRows is not null) _mallListingRows.ItemsSource = _mall.Rows;
                if (_mallSummaryText is not null) _mallSummaryText.Text = _mall.Summary;
                break;
            case "Combat":
                await RunBusyOperationAsync("Loading combat catalog", _autoCombat.LoadAsync);
                if (_combatTargetList is not null) _combatTargetList.ItemsSource = _autoCombat.Targets;
                if (_combatSkillList is not null) _combatSkillList.ItemsSource = _autoCombat.SelectedSkills;
                if (_combatItemList is not null) _combatItemList.ItemsSource = _autoCombat.SelectedItems;
                if (_combatPreviewText is not null) _combatPreviewText.Text = _autoCombat.PreviewText;
                break;
            case "Accounts & Transfer":
                await RunBusyOperationAsync("Loading accounts", LoadSavedAccountsAsync);
                break;
            case "Adventure":
                await RunBusyOperationAsync("Loading adventure plan", cancellationToken => RefreshAdventureAsync(true, cancellationToken));
                break;
            default:
                await RunBusyOperationAsync("Refreshing", RefreshCurrentPageStateAsync);
                break;
        }
    }

    private async void RefreshMallClick(object? sender, RoutedEventArgs e)
        => await RunBusyOperationAsync("Refreshing mall", cancellationToken => RefreshMallAsync(true, cancellationToken));

    private async void RefreshAdventureClick(object? sender, RoutedEventArgs e)
        => await RunBusyOperationAsync("Loading adventure plan", cancellationToken => RefreshAdventureAsync(true, cancellationToken));

    private void AccountRoleSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _selectedRole = ReadSelectedRole();
        if (_mallRoleLabel is not null)
            _mallRoleLabel.Text = $"Konto: {_selectedRole}";

        if (_activeSection == "Mall & Shop")
        {
            if (_mallSummary is not null)
                _mallSummary.Text = $"Account “{_selectedRole}” selected. Reload the mall data.";
            if (_mallRows is not null)
                _mallRows.Text = string.Empty;
        }

        SetStatus($"Account “{_selectedRole}” selected. Read-only queries use this account.");
    }

    private async Task RefreshCurrentPageStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Keep the busy/cancel affordance visible for at least one dispatcher
        // turn even when a page has no query service yet.
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        UpdatePageStateStatus(_activeSection, "No external data source loaded for this section.");
        SetStatus(_activeSection == "Guts"
            ? "Overview refreshed. Read-only metrics wait for a connected query service."
            : $"{_activeSection}: Noch keine Daten geladen.");
    }

    private async Task RunBusyOperationAsync(
        string operation,
        Func<CancellationToken, Task> action)
    {
        if (_refreshCancellation is not null)
        {
            SetStatus("A refresh is already running. Cancel it or wait before starting another.");
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _refreshCancellation = cancellation;
        SetBusy(true, $"{operation} running …");
        try
        {
            // Yield once so the disabled refresh button, progress indicator,
            // and cancel action are rendered before a fast local read returns.
            await Task.Yield();
            cancellation.Token.ThrowIfCancellationRequested();
            await action(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            SetStatus($"{operation} abgebrochen.");
        }
        catch (Exception exception)
        {
            SetStatus($"{operation} fehlgeschlagen: {exception.Message}");
        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, cancellation))
                _refreshCancellation = null;
            SetBusy(false);
        }
    }

    private void CancelRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (_refreshCancellation is null)
            return;
        _refreshCancellation.Cancel();
        SetStatus("Abbruch angefordert …");
    }

    private void SetBusy(bool busy, string? message = null)
    {
        if (_globalRefreshButton is not null)
        {
            _globalRefreshButton.IsEnabled = !busy;
            _globalRefreshButton.Content = busy ? "⟳  Refreshing …" : "⟳  Daten aktualisieren";
        }
        if (_busyIndicator is not null)
            _busyIndicator.IsVisible = busy;
        if (_cancelRefreshButton is not null)
            _cancelRefreshButton.IsVisible = busy;
        if (_statusBar is not null)
            _statusBar.IsVisible = true;
        if (message is not null && _statusText is not null)
            _statusText.Text = message;
    }

    private async Task RefreshMallAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (_mallSummary is null || _mallRows is null) return;
        try
        {
            await _accounts.InitializeAsync(cancellationToken);
            var role = _selectedRole;
            var session = await _accounts.GetSessionAsync(role, cancellationToken: cancellationToken);
            if (session is null || string.IsNullOrWhiteSpace(session.Credentials.CookieHeader))
            {
                _mallSummary.Text = $"Log {role} in through Accounts first.";
                _mallRows.Text = string.Empty;
                UpdatePageStateStatus("Mall & Shop", $"No valid {role} login on file.");
                SetStatus($"Mall konnte nicht geladen werden: {role} ist nicht angemeldet.");
                return;
            }

            using var http = KoLLoginClient.CreateAuthenticatedClient(
                session.Credentials,
                new Uri("https://www.kingdomofloathing.com/"));
            var mall = new KoLMallReadClient(http);
            var listings = await mall.FetchOwnShopAsync(forceRefresh, cancellationToken);
            var categorizer = new InventoryCategorizer();
            var analyses = new List<InventoryCategorization>();
            foreach (var listing in listings.Take(20))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var market = await mall.SearchAsync(
                    listing.DisplayName ?? listing.ItemId,
                    forceRefresh,
                    cancellationToken);
                var lowest = market.Listings.Count == 0 ? (decimal?)null : market.Listings.Min(x => (decimal)x.Price);
                analyses.Add(categorizer.Analyze(new InventoryItemSnapshot
                {
                    ItemId = listing.ItemId,
                    DisplayName = listing.DisplayName ?? $"Item {listing.ItemId}",
                    OwnedQuantity = listing.Quantity,
                    CurrentListingPrice = listing.Price,
                    LowestMarketPrice = lowest,
                    MarketReferencePrice = lowest,
                    ObservedQuantity = market.Listings.Sum(x => x.Quantity)
                }));
            }
            _mallSummary.Text = $"{listings.Count} eigene Listings · {analyses.Count(x => x.Has(InventoryCategory.Overpriced))} overpriced · {analyses.Count(x => x.Has(InventoryCategory.Underpriced))} unter Markt · Quelle: {session.Profile.Role}";
            _mallRows.Text = analyses.Count == 0
                ? "Keine Listings gefunden oder Shopseite konnte noch nicht geparst werden."
                : string.Join(Environment.NewLine, analyses.Select(x =>
                    $"{x.DisplayName}   {x.CurrentPrice:N0} Meat   {string.Join(", ", x.Categories)}   → {x.SuggestedAction}"));
            UpdatePageStateStatus("Mall & Shop", $"{listings.Count} Listings von {role} geladen.");
            SetStatus("Mall and shop data refreshed. Price changes stay disabled.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _mallSummary.Text = "Mall-Aktualisierung fehlgeschlagen.";
            _mallRows.Text = exception.Message;
            UpdatePageStateStatus("Mall & Shop", "The query failed; the existing data stays unchanged.");
            SetStatus("Mall konnte nicht aktualisiert werden: " + exception.Message);
        }
    }

    private async Task RefreshAdventureAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (_adventureAccountLabel is null || _adventureTurnsLabel is null
            || _adventureKnucklebonesLabel is null || _adventurePlanSummary is null
            || _adventurePlanAdvisories is null) return;
        await _accounts.InitializeAsync(cancellationToken);
        var role = _selectedRole;
        _adventureAccountLabel.Text = role.ToString();
        var session = await _accounts.GetSessionAsync(role, cancellationToken: cancellationToken);
        if (session is null || string.IsNullOrWhiteSpace(session.Credentials.CookieHeader))
        {
            _adventureTurnsLabel.Text = "—";
            _adventureKnucklebonesLabel.Text = "— / 100";
            _adventurePlanSummary.Text = $"Log {role} in through Accounts first.";
            _adventurePlanAdvisories.Text = "No session snapshot available.";
            UpdatePageStateStatus("Adventure", $"No valid {role} login on file.");
            return;
        }

        using var http = KoLLoginClient.CreateAuthenticatedClient(
            session.Credentials,
            new Uri("https://www.kingdomofloathing.com/"));
        var account = new KoLAccountReadClient(http);
        var status = await account.FetchStatusAsync(forceRefresh, cancellationToken);
        var inventory = await account.FetchInventoryAsync(forceRefresh, cancellationToken);
        var knucklebones = inventory.Items
            .Where(item => item.Name.Contains("knucklebone", StringComparison.OrdinalIgnoreCase)
                || item.ItemId.Contains("knucklebone", StringComparison.OrdinalIgnoreCase))
            .Sum(item => item.Quantity);
        _adventureTurnsLabel.Text = status.IsStale ? $"{status.Adventures:N0} (stale)" : status.Adventures.ToString("N0");
        _adventureKnucklebonesLabel.Text = $"{knucklebones:N0} / 100";
        var state = new KolArbitrage.Core.Adventure.AdventureState
        {
            IsStale = status.IsStale || inventory.IsStale,
            Knucklebones = Math.Max(0, knucklebones),
            AdventureTurnsRemaining = Math.Max(0, status.Adventures),
            SkeletonOfCrimbosPastAvailable = true
        };
        var plan = new KolArbitrage.Core.Adventure.DailyAdventurePlanner().Plan(state);
        _adventurePlanSummary.Text = $"{plan.Status}: {plan.Steps.Count} step(s), projected {plan.ProjectedKnucklebones:N1} knucklebones.";
        _adventurePlanAdvisories.Text = plan.Advisories.Count == 0
            ? "No warnings. Every execution stays bound to approval and verification."
            : string.Join(Environment.NewLine, plan.Advisories);
        UpdatePageStateStatus("Adventure", $"Status and inventory for {role} loaded; preview only.");
        SetStatus("Adventure plan refreshed. No adventure or combat action was sent.");
    }

    private async Task LoadSavedAccountsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _accounts.InitializeAsync(cancellationToken);
            await LoadSavedAccountAsync(AccountRole.Main, cancellationToken);
            await LoadSavedAccountAsync(AccountRole.Mallbot, cancellationToken);
            UpdatePageStateStatus("Accounts & Transfer", "Stored roles and sessions checked.");
            SetStatus("Accounts loaded. Passwords stay protected and are not shown.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            SetStatus($"Konten konnten nicht geladen werden: {exception.Message}");
        }
    }

    private async Task LoadSavedAccountAsync(AccountRole role, CancellationToken cancellationToken = default)
    {
        var session = await _accounts.GetSessionAsync(
            role,
            includeExpired: true,
            cancellationToken: cancellationToken);
        if (session is null)
            return;

        var username = session.Credentials.Username ?? session.Profile.DisplayName;
        var usernameBox = this.FindControl<TextBox>(role == AccountRole.Main ? "MainUsername" : "MallbotUsername");
        var passwordBox = this.FindControl<TextBox>(role == AccountRole.Main ? "MainPassword" : "MallbotPassword");
        var rememberBox = this.FindControl<CheckBox>(role == AccountRole.Main ? "MainRememberPassword" : "MallbotRememberPassword");
        var status = this.FindControl<TextBlock>(role == AccountRole.Main ? "MainAccountStatus" : "MallbotAccountStatus");
        if (usernameBox is null || passwordBox is null || rememberBox is null || status is null)
            return;
        usernameBox.Text = username;
        // Never repopulate a password from disk. Existing installations may
        // have one from an older version, but it must not be exposed in the UI
        // or used implicitly for a new login.
        passwordBox.Text = string.Empty;
        status.Text = "Stored (session not checked yet)";
        status.Foreground = ThemeBrush("WarningBrush", Brushes.DarkGoldenrod);
    }

    private async void SaveAccountClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !Enum.TryParse<AccountRole>(button.Tag?.ToString(), out var role))
            return;

        button.IsEnabled = false;

        var usernameBox = this.FindControl<TextBox>(role == AccountRole.Main ? "MainUsername" : "MallbotUsername");
        var passwordBox = this.FindControl<TextBox>(role == AccountRole.Main ? "MainPassword" : "MallbotPassword");
        var rememberBox = this.FindControl<CheckBox>(role == AccountRole.Main ? "MainRememberPassword" : "MallbotRememberPassword");
        var status = this.FindControl<TextBlock>(role == AccountRole.Main ? "MainAccountStatus" : "MallbotAccountStatus");
        if (usernameBox is null || passwordBox is null || rememberBox is null || status is null)
            return;
        var username = usernameBox.Text?.Trim();
        var password = passwordBox.Text;

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            status.Text = "Benutzername und Passwort erforderlich";
            status.Foreground = ThemeBrush("ErrorBrush", Brushes.IndianRed);
            return;
        }

        try
        {
            var id = role == AccountRole.Main ? "main" : "mallbot";
            await _accounts.SetProfileAsync(new AccountProfile
            {
                Id = id,
                DisplayName = username,
                Role = role,
                Enabled = true
            });
            // Remove any previous session before associating this role with a
            // new login. This prevents a failed attempt from silently using a
            // stale session for a different character.
            await _accounts.ClearCredentialsAsync(role);

            status.Text = "Logging in …";
            status.Foreground = ThemeBrush("WarningBrush", Brushes.DarkGoldenrod);
            var login = await _loginClient.LoginAsync(username, password, rememberPassword: rememberBox.IsChecked == true);
            await _accounts.SetCredentialsAsync(role, login.Credentials);
            status.Text = "Connected and stored encrypted";
            status.Foreground = ThemeBrush("SuccessBrush", Brushes.LightGreen);
            SetStatus($"{role} is logged in at KoL. Read-only data can be loaded now.");
        }
        catch (KoLLoginException exception)
        {
            passwordBox.Text = string.Empty;
            status.Text = "Login fehlgeschlagen (Passwort nicht gespeichert)";
            status.Foreground = ThemeBrush("ErrorBrush", Brushes.IndianRed);
            SetStatus($"{role}: KoL-Login fehlgeschlagen [{exception.Reason}] ({exception.Message}). Bitte erneut versuchen.");
        }
        catch (Exception exception)
        {
            passwordBox.Text = string.Empty;
            status.Text = "Speichern fehlgeschlagen";
            status.Foreground = ThemeBrush("ErrorBrush", Brushes.IndianRed);
            SetStatus($"{role} konnte nicht gespeichert werden: {exception.Message}");
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void UpdatePageState(string section)
    {
        var isOverview = string.Equals(section, "Guts", StringComparison.Ordinal);
        var isInventory = string.Equals(section, "Inventory", StringComparison.Ordinal);
        var isDaily = string.Equals(section, "Daily", StringComparison.Ordinal);
        var isArbitrage = string.Equals(section, "Arbitrage", StringComparison.Ordinal);
        var isAccounts = string.Equals(section, "Accounts & Transfer", StringComparison.Ordinal);
        var isMall = string.Equals(section, "Mall & Shop", StringComparison.Ordinal);
        var isAdventure = string.Equals(section, "Adventure", StringComparison.Ordinal);
        var isCombat = string.Equals(section, "Combat", StringComparison.Ordinal);
        var (title, description) = DescribeSection(section);

        if (_overviewPanel is not null)
            _overviewPanel.IsVisible = false;
        if (_gutsPanel is not null)
            _gutsPanel.IsVisible = isOverview;
        if (_inventoryPanel is not null)
            _inventoryPanel.IsVisible = isInventory;
        if (_dailyPanel is not null)
            _dailyPanel.IsVisible = isDaily;
        if (_arbitragePanel is not null)
            _arbitragePanel.IsVisible = isArbitrage;
        if (_accountsPanel is not null)
            _accountsPanel.IsVisible = isAccounts;
        if (_mallPanel is not null)
            _mallPanel.IsVisible = false;
        if (_mallSection is not null)
            _mallSection.IsVisible = isMall;
        if (_combatPanel is not null)
            _combatPanel.IsVisible = false;
        if (_combatSection is not null)
            _combatSection.IsVisible = isCombat;
        if (_adventurePanel is not null)
            _adventurePanel.IsVisible = isAdventure;
        if (_pageStatePanel is not null)
            _pageStatePanel.IsVisible = !isOverview && !isInventory && !isDaily && !isArbitrage
                && !isAccounts && !isMall && !isAdventure && !isCombat;
        if (_shellPageTitle is not null)
            _shellPageTitle.Text = isOverview ? "Good morning" : title;
        if (_shellPageDescription is not null)
            _shellPageDescription.Text = isOverview
                ? "Your arbitrage overview for today."
                : description;
        if (_pageStateTitle is not null)
            _pageStateTitle.Text = title;
        if (_pageStateDescription is not null)
            _pageStateDescription.Text = description;
        if (isMall && _mallRoleLabel is not null)
            _mallRoleLabel.Text = $"Konto: {_selectedRole}";
        if (_pageStateStatus is not null && !isOverview)
            _pageStateStatus.Text = isAccounts
                ? "No account check has run yet."
                : isMall
                    ? $"No mall data loaded for {_selectedRole} yet."
                    : "Noch keine Daten geladen.";
    }

    private void UpdatePageStateStatus(string section, string status)
    {
        if (_activeSection == section && _pageStateStatus is not null)
            _pageStateStatus.Text = status;
    }

    private static (string Title, string Description) DescribeSection(string section)
        => section switch
        {
            "Inventory" => ("Inventory", "Live inventory with search, categories and item detail."),
            "Daily" => ("Daily", "What the bot does today — campfire, hermit, free pulls and arbitrage."),
            "Arbitrage" => ("Arbitrage", "Live mall listings against the pricegun median."),
            "Pricing Engine" => ("Pricing Engine", "Price sources, references and buy candidates are checked here as a read-only preview."),
            "Mall & Shop" => ("Mall & Shop", "Own listings and market prices are loaded read-only for the selected account."),
            "Accounts & Transfer" => ("Accounts & Transfer", "Main- und Mallbot-Sessions werden getrennt verwaltet; Transfers bleiben eine Vorschau."),
            "Adventure" => ("Adventure", "Daily goals and farm-vs-buy plans appear here once a query service is connected."),
            "Combat" => ("Combat", "Manuelle und deklarative AutoCombat-Entscheidungen bleiben an frische Fight-Observations und Approval gebunden."),
            "Settings" => ("Settings", "Shell-, Cache- und Sicherheitsoptionen werden hier verwaltet."),
            _ => (section, "Dieser Bereich hat noch keine geladenen Daten.")
        };

    private AccountRole ReadSelectedRole()
    {
        var value = _accountRoleSelector?.SelectedItem switch
        {
            ComboBoxItem item => item.Content?.ToString(),
            _ => _accountRoleSelector?.SelectedItem?.ToString()
        };
        return Enum.TryParse<AccountRole>(value, ignoreCase: true, out var role)
            ? role
            : AccountRole.Mallbot;
    }

    private void SetStatus(string message)
    {
        if (_statusText is not null)
            _statusText.Text = message;
        if (_statusBar is not null)
            _statusBar.IsVisible = true;
    }
    private IBrush ThemeBrush(string key, IBrush fallback)
        => this.FindResource(key) as IBrush ?? fallback;
    /// <summary>
    /// Wires the inventory browser: the list, the search box, the category
    /// strip and the detail pane all read from one view model so the
    /// filtering logic stays testable and out of the view.
    /// </summary>
    private void WireInventoryBrowser()
    {
        // The catalog gives the item rows their uses, access flags and image.
        // It is optional: without it the inventory still lists, it just has
        // no detail data.
        var catalog = TryLoadCatalog();
        if (catalog is not null)
        {
            _inventory = new InventoryViewModel(catalog: catalog);
            _arbitrage = new LiveArbitrageViewModel(catalog: catalog);
            _mall = new LiveMallViewModel(catalog: catalog);
            _autoCombat = new AutoCombatViewModel(catalog);
        }
        if (_inventoryList is not null)
        {
            _inventoryList.ItemsSource = _inventory.Items;
        }
        if (_inventoryCategoryStrip is not null)
        {
            // KoLmafia shows the categories as a row of tabs above the item
            // list. Each one is a button that switches the filter.
            _inventoryCategoryStrip.Children.Clear();
            foreach (var category in _inventory.Categories)
            {
                var captured = category;
                var button = new Button
                {
                    Content = category.Label,
                    Classes = { "menu-item" },
                    Padding = new Avalonia.Thickness(10, 5),
                    FontSize = 12
                };
                button.Click += (_, _) =>
                {
                    _inventory.SelectedCategory = captured;
                    foreach (var child in _inventoryCategoryStrip.Children.OfType<Button>())
                        child.Classes.Remove("active");
                    button.Classes.Add("active");
                };
                _inventoryCategoryStrip.Children.Add(button);
            }
        }
        _inventory.PropertyChanged += (_, args) =>
        {
            switch (args.PropertyName)
            {
                case nameof(InventoryViewModel.SummaryText):
                    if (_inventorySummaryText is not null) _inventorySummaryText.Text = _inventory.SummaryText;
                    break;
                case nameof(InventoryViewModel.LoadedAtText):
                    if (_inventoryLoadedText is not null) _inventoryLoadedText.Text = _inventory.LoadedAtText;
                    break;
                case nameof(InventoryViewModel.Detail):
                    RenderInventoryDetail();
                    break;
            }
        };
        if (_inventorySummaryText is not null) _inventorySummaryText.Text = _inventory.SummaryText;
    }

    private void RenderInventoryDetail()
    {
        if (_inventoryDetailPanel is null) return;
        _inventoryDetailPanel.Children.Clear();
        var detail = _inventory.Detail;
        if (detail is null)
        {
            _inventoryDetailPanel.Children.Add(new TextBlock
            {
                Text = "No item selected",
                Classes = { "muted" }
            });
            return;
        }
        _inventoryDetailPanel.Children.Add(new TextBlock
        {
            Text = detail.Name,
            Classes = { "h3" }
        });
        _inventoryDetailPanel.Children.Add(new TextBlock
        {
            Text = $"ID {detail.ItemId} · {detail.Quantity} pcs",
            Classes = { "muted" },
            FontSize = 12
        });
        _inventoryDetailPanel.Children.Add(new TextBlock
        {
            Text = $"Verwendung: {detail.UsesText}",
            FontSize = 12,
            Margin = new Avalonia.Thickness(0, 8, 0, 0)
        });
        _inventoryDetailPanel.Children.Add(new TextBlock
        {
            Text = $"Billigster Mall-Preis: {detail.MallPriceText}",
            FontSize = 12
        });
    }

    private void InventorySearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (_inventorySearchBox is null) return;
        _inventory.SearchText = _inventorySearchBox.Text ?? string.Empty;
    }

    private void InventorySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_inventoryList?.SelectedItem is DesktopInventoryItem item)
            _inventory.SelectedItem = item;
    }

    private async void InventoryClick(object? sender, RoutedEventArgs e)
    {
        Navigate("Inventory");
        await RunBusyOperationAsync("Loading inventory", cancellationToken =>
            _inventory.LoadAsync(cancellationToken: cancellationToken));
        if (_inventoryList is not null)
            _inventoryList.ItemsSource = _inventory.Items;
        if (_inventorySummaryText is not null) _inventorySummaryText.Text = _inventory.SummaryText;
        if (_inventoryLoadedText is not null) _inventoryLoadedText.Text = _inventory.LoadedAtText;
        RenderInventoryDetail();
    }

    /// <summary>Opens the morning briefing: the live numbers and the plan
    /// the bot would run today.</summary>
    private async void DailyClick(object? sender, RoutedEventArgs e)
    {
        Navigate("Daily");
        await RunBusyOperationAsync("Loading daily plan", cancellationToken =>
            _daily.LoadAsync(cancellationToken: cancellationToken));
        if (_dailyRows is not null) _dailyRows.ItemsSource = _daily.Rows;
        if (_dailySnapshotText is not null) _dailySnapshotText.Text = _daily.SnapshotText;
        if (_dailySummaryText is not null) _dailySummaryText.Text = _daily.SummaryText;
    }

    private void DailySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_dailyRows?.SelectedItem is not DailyBriefingRow row) return;
        if (_dailyStatusText is not null)
            _dailyStatusText.Text = $"{row.Surface}: {row.Description} — {row.Detail}";
    }

    /// <summary>Runs the daily routine with the approval token from the
    /// window. Without a token nothing is submitted — the bridge refuses
    /// to write without one.</summary>
    private async void DailyRunClick(object? sender, RoutedEventArgs e)
    {
        var token = _dailyTokenBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            if (_dailyStatusText is not null)
                _dailyStatusText.Text = "No approval token — nothing was sent.";
            return;
        }
        await RunBusyOperationAsync("Daily routine", async cancellationToken =>
        {
            var account = await _accounts
                .GetSessionAsync(_selectedRole, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (account is null)
            {
                if (_dailyStatusText is not null)
                    _dailyStatusText.Text = "No session for the selected account.";
                return;
            }
            var ok = await RunDailyRoutineAsync(account, token, cancellationToken).ConfigureAwait(false);
            if (_dailyStatusText is not null)
                _dailyStatusText.Text = ok
                    ? "Daily routine executed. Details are in the audit log."
                    : "The daily routine could not be completed.";
        });
    }

    /// <summary>Runs the daily routine for the selected account through the
    /// same bridge the CLI uses.</summary>
    private async Task<bool> RunDailyRoutineAsync(
        AccountSession account, string approvalToken, CancellationToken cancellationToken)
    {
        var http = KoLLoginClient.CreateAuthenticatedClient(
            account.Credentials, new Uri("https://www.kingdomofloathing.com/"));
        var readClient = new KoLAccountReadClient(http);
        var live = await new LiveDailySnapshotBuilder(readClient)
            .BuildAsync(new KolArbitrage.Core.Daily.DailyRoutineSnapshot(), cancellationToken)
            .ConfigureAwait(false);
        var bridge = new KolArbitrage.Core.Daily.AutoCombatDailyBridge();
        var plan = bridge.Plan(live.State, live.Snapshot);
        // The approval hook is where the token gate lives: the gateway only
        // posts when the operator's token matches.
        var approval = new DelegateAdventureApprovalHook((_, _) =>
            ValueTask.FromResult(AdventureApprovalDecision.Allow(approvalToken)));
        var verification = new DelegateAdventureVerificationHook((context, _) =>
            ValueTask.FromResult(context.Response.Accepted != false
                ? AdventureVerificationResult.Success()
                : AdventureVerificationResult.Failed("Die Live-Antwort wurde von KoL abgelehnt.")));
        var session = await KoLAdventureSessionFactory.CreateAsync(
            _selectedRole, approval, verification, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var dispatcher = new KolArbitrage.Core.Daily.FreePullDispatcher(
            http, KolArbitrage.Core.Daily.FreePullDispatcher.DefaultBaseUri);
        using var audit = new KolArbitrage.Core.Daily.BridgeAuditLog(
            KolArbitrage.Core.Daily.BridgeAuditLog.DefaultPath());
        var result = await bridge.ExecuteAsync(
            plan.Steps, session.Gateway,
            freePullDispatcher: dispatcher,
            freePullCredentials: account.Credentials,
            audit: audit,
            accountId: _selectedRole.ToString(),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.Executed;
    }

    private static KolArbitrage.Core.Data.KoLmafiaCatalog? TryLoadCatalog()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "external", "kolmafia-data"),
            Path.Combine(Directory.GetCurrentDirectory(), "external", "kolmafia-data")
        };
        foreach (var candidate in candidates)
        {
            if (!Directory.Exists(candidate)) continue;
            try
            {
                return new KolArbitrage.Core.Data.MafiaDataLoader().Load(candidate);
            }
            catch
            {
                // A broken catalog must not take the window down.
            }
        }
        return null;
    }

    private void WireEncounterBrowser()
    {
        var catalogDir = Path.Combine(
            AppContext.BaseDirectory,
            "external", "kolmafia-data");
        if (!Directory.Exists(catalogDir))
            return;

        _encounterBrowser = new EncounterBrowserPane(
            _selectedRole,
            gatewayFactory: _ => CreateManualGateway(),
            setStatus: msg => UpdateEncounterStatus(msg),
            resolveChoices: locationId => ResolveEncounterChoices(locationId),
            monstersFor: locationId => ResolveMonstersForLocation(locationId));
        try
        {
            _encounterBrowser.Initialise(catalogDir);
        }
        catch (Exception exception)
        {
            UpdateEncounterStatus($"Catalog konnte nicht geladen werden: {exception.Message}");
            return;
        }

        if (_encounterLocationsGrid is not null && _encounterBrowser.Browser is not null)
        {
            _encounterLocationsGrid.ItemsSource = _encounterBrowser.Browser.Locations;
            _encounterMonstersGrid!.ItemsSource = _encounterBrowser.Browser.Monsters;
            _encounterActionsGrid!.ItemsSource = _encounterBrowser.Browser.Actions;
        }

        if (_encounterFilterBox is not null)
        {
            _encounterFilterBox.TextChanged += (_, _) =>
            {
                if (_encounterBrowser?.Browser is { } browser)
                    browser.FilterText = _encounterFilterBox.Text ?? string.Empty;
            };
        }

        if (_encounterLocationsGrid is not null)
        {
            _encounterLocationsGrid.SelectionChanged += (_, _) =>
            {
                if (_encounterBrowser?.Browser is { } browser
                    && _encounterLocationsGrid.SelectedItem is EncounterBrowserLocationRow row)
                {
                    browser.SelectedLocation = row;
                    RefreshEncounterStepLog();
                }
            };
            _encounterLocationsGrid.DoubleTapped += async (_, _) =>
            {
                if (_encounterLocationsGrid.SelectedItem is EncounterBrowserLocationRow row)
                    await _encounterBrowser.StartAdventureAsync(row).ConfigureAwait(false);
                RefreshEncounterStepLog();
            };
        }

        if (_encounterMonstersGrid is not null)
        {
            _encounterMonstersGrid.SelectionChanged += (_, _) =>
            {
                if (_encounterBrowser?.Browser is { } browser
                    && _encounterMonstersGrid.SelectedItem is EncounterBrowserMonsterRow monster)
                {
                    browser.SelectedMonster = monster;
                }
            };
        }

        if (_encounterActionsGrid is not null)
        {
            _encounterActionsGrid.DoubleTapped += async (_, _) =>
            {
                if (_encounterActionsGrid.SelectedItem is EncounterBrowserActionRow row)
                    await _encounterBrowser.SubmitStepAsync(row).ConfigureAwait(false);
                RefreshEncounterStepLog();
            };
        }

        if (_encounterStartButton is not null)
            _encounterStartButton.Click += async (_, _) =>
            {
                if (_encounterLocationsGrid?.SelectedItem is EncounterBrowserLocationRow row)
                    await _encounterBrowser.StartAdventureAsync(row).ConfigureAwait(false);
                RefreshEncounterStepLog();
            };

        if (_encounterSubmitButton is not null)
            _encounterSubmitButton.Click += async (_, _) =>
            {
                if (_encounterActionsGrid?.SelectedItem is EncounterBrowserActionRow row)
                    await _encounterBrowser.SubmitStepAsync(row).ConfigureAwait(false);
                RefreshEncounterStepLog();
            };

        if (_encounterEndButton is not null)
            _encounterEndButton.Click += (_, _) =>
            {
                _encounterBrowser.EndSession();
                RefreshEncounterStepLog();
            };

        if (_encounterSaveTokenButton is not null)
            _encounterSaveTokenButton.Click += async (_, _) =>
            {
                var token = _encounterTokenBox?.Text ?? string.Empty;
                if (_encounterBrowser is null || string.IsNullOrWhiteSpace(token))
                {
                    UpdateEncounterStatus("Approval-Token ist erforderlich.");
                    return;
                }
                _encounterBrowser.ApplyApprovalToken(token);
                try
                {
                    await _encounterBrowser.PersistTokenAsync("manual-browser");
                }
                catch (Exception exception)
                {
                    UpdateEncounterStatus($"Token konnte nicht gespeichert werden: {exception.Message}");
                }
            };

        if (_encounterLoadTokenButton is not null)
            _encounterLoadTokenButton.Click += async (_, _) =>
            {
                try { await _encounterBrowser.LoadStoredTokenAsync(); }
                catch (Exception exception)
                {
                    UpdateEncounterStatus($"Token konnte nicht geladen werden: {exception.Message}");
                }
                if (_encounterTokenBox is not null && _encounterBrowser?.Session is { } session)
                    _encounterTokenBox.Text = session.ApprovalToken;
            };

        // Auto-load on first display if a token is already stored.
        _ = _encounterBrowser.LoadStoredTokenAsync();
    }

    private KolArbitrage.Core.Adventure.AdventureExecutionGateway CreateManualGateway()
    {
        // The gateway is consumed by the manual driver; the in-memory store is
        // enough for the catalog browsing surface. Live POSTs only happen when
        // a real approval token is applied via ApplyApprovalToken.
        return new KolArbitrage.Core.Adventure.AdventureExecutionGateway(
            new AdventureExecutionFixtureTransport(),
            new InMemoryAdventureRunCheckpointStore());
    }

    private void UpdateEncounterStatus(string message)
    {
        if (_encounterStatusText is null) return;
        _encounterStatusText.Text = $"{DateTime.Now:HH:mm:ss} · {message}";
    }

    private void RefreshEncounterStepLog()
    {
        if (_encounterStepLogText is null || _encounterBrowser?.Session is not { } session) return;
        var lines = session.StepLog.Take(10).Select(entry =>
            $"{entry.At:HH:mm:ss} {entry.Kind} {entry.Status} {entry.StopReason}");
        _encounterStepLogText.Text = string.Join("\n", lines);
    }

    private static IEnumerable<string> ResolveEncounterChoices(string locationId)
        => locationId == "skeleton-of-crimbos-past"
            ? new[] { "1003:1", "1003:2" }
            : Array.Empty<string>();

    private static IReadOnlyList<MafiaMonsterRecord> ResolveMonstersForLocation(string locationId)
        => locationId switch
        {
            "skeleton-of-crimbos-past" => new MafiaDataLoader()
                .Load(Path.Combine(AppContext.BaseDirectory, "external", "kolmafia-data"))
                .Monsters.Where(m => m.Id.Contains("skeleton", StringComparison.OrdinalIgnoreCase))
                .ToArray(),
            _ => Array.Empty<MafiaMonsterRecord>()
        };
}
