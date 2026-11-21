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
using KolArbitrage.Core.Data;

namespace KoLibri.Desktop;

public partial class MainWindow : Window
{
    private StackPanel? _contentRoot;
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
    private Button? _navAccounts;
    private ScrollViewer? _pageScroll;
    private readonly DesktopLiveDataProvider _live;
    private readonly KoLmafiaCatalog? _catalog;
    private Task _busyTask = Task.CompletedTask;
    private int _navigationVersion;
    private TextBlock? _shellPageTitle;
    private TextBlock? _shellPageDescription;
    private Border? _accountsPanel;
    private Border? _adventurePanel;
    private TextBlock? _adventureAccountLabel;
    private TextBlock? _adventureTurnsLabel;
    private TextBlock? _adventureKnucklebonesLabel;
    private TextBlock? _adventurePlanSummary;
    private TextBlock? _adventurePlanAdvisories;
    private ComboBox? _accountRoleSelector;
    private Button? _globalRefreshButton;
    private ProgressBar? _busyIndicator;
    private Button? _cancelRefreshButton;
    private CancellationTokenSource? _refreshCancellation;
    private string _activeSection = "Guts";
    private AccountRole _selectedRole = AccountRole.Mallbot;
    private readonly AccountSessionManager _accounts;
    private readonly KoLLoginClient _loginClient = new();
    private EncounterBrowserPane? _encounterBrowser;
    private ListBox? _encounterLocationsGrid;
    private ListBox? _encounterMonstersGrid;
    private ListBox? _encounterActionsGrid;
    private TextBox? _encounterFilterBox;
    private Button? _encounterStartButton;
    private Button? _encounterSubmitButton;
    private Button? _encounterEndButton;
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
    private InventoryViewModel _inventory = null!;
    private DailyBriefingViewModel _daily = null!;
    private LiveGutsViewModel _guts = null!;
    private LiveArbitrageViewModel _arbitrage = null!;
    private LiveMallViewModel _mall = null!;
    private AutoCombatViewModel _autoCombat = null!;
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
    private TextBlock? _combatRunStatusText;
    private StackPanel? _gutsPanel;
    private StackPanel? _arbitragePanel;
    private ItemsControl? _gutsAccountCards;
    private TextBlock? _gutsSummaryText;
    private TextBlock? _storeSummaryText;
    private ListBox? _storeRows;
    private ListBox? _arbitrageRows;
    private TextBlock? _arbitrageSummaryText;
    private ListBox? _arbitrageRules;
    private TextBlock? _arbitragePlanText;
    private StackPanel? _dailyPanel;
    private ListBox? _dailyRows;
    private TextBlock? _dailySnapshotText;
    private TextBlock? _dailySummaryText;
    private TextBlock? _dailyStatusText;

    public MainWindow() : this(null)
    {
    }

    public MainWindow(Func<Action<string>, EncounterBrowserPane>? createEncounterPane, string? tokenDirectory = null,
        AccountSessionManager? accounts = null, Func<AccountSession, HttpClient>? createAccountHttp = null)
    {
        _accounts = accounts ?? AccountSessionManager.CreateDefault();
        _live = new DesktopLiveDataProvider(_accounts, createAccountHttp);
        _catalog = TryLoadCatalog();
        InitializeComponent();
        _pageScroll = this.FindControl<ScrollViewer>("PageScroll");
        _navAccounts = this.FindControl<Button>("NavAccounts");
        _contentRoot = this.FindControl<StackPanel>("ContentRoot");
        _inventoryPanel = this.FindControl<StackPanel>("InventoryPanel");
        _inventorySearchBox = this.FindControl<TextBox>("InventorySearchBox");
        _inventorySummaryText = this.FindControl<TextBlock>("InventorySummaryText");
        _inventoryLoadedText = this.FindControl<TextBlock>("InventoryLoadedText");
        _inventoryList = this.FindControl<ListBox>("InventoryList");
        _inventoryCategoryStrip = this.FindControl<StackPanel>("InventoryCategoryStrip");
        _inventoryDetailPanel = this.FindControl<StackPanel>("InventoryDetailPanel");
        if (this.FindControl<ComboBox>("CharacterSlotPicker") is { } slotPicker)
            slotPicker.ItemsSource = Enum.GetValues<CharacterEquipmentSlot>();
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
        _arbitrageRules = this.FindControl<ListBox>("ArbitrageRules");
        _arbitragePlanText = this.FindControl<TextBlock>("ArbitragePlanText");
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
        _combatRunStatusText = this.FindControl<TextBlock>("CombatRunStatusText");
        _dailyRows = this.FindControl<ListBox>("DailyRows");
        _dailySnapshotText = this.FindControl<TextBlock>("DailySnapshotText");
        _dailySummaryText = this.FindControl<TextBlock>("DailySummaryText");
        _dailyStatusText = this.FindControl<TextBlock>("DailyStatusText");
        _themeToggleButton = this.FindControl<Button>("ThemeToggleButton");
        _statusText = this.FindControl<TextBlock>("StatusText");
        _statusBar = this.FindControl<Border>("StatusBar");
        _shellPageTitle = this.FindControl<TextBlock>("ShellPageTitle");
        _shellPageDescription = this.FindControl<TextBlock>("ShellPageDescription");
        _accountsPanel = this.FindControl<Border>("AccountsPanel");
        _adventurePanel = this.FindControl<Border>("AdventurePanel");
        _encounterLocationsGrid = this.FindControl<ListBox>("EncounterLocationsGrid");
        _encounterMonstersGrid = this.FindControl<ListBox>("EncounterMonstersGrid");
        _encounterActionsGrid = this.FindControl<ListBox>("EncounterActionsGrid");
        _encounterFilterBox = this.FindControl<TextBox>("EncounterFilterBox");
        _encounterStartButton = this.FindControl<Button>("EncounterStartButton");
        _encounterSubmitButton = this.FindControl<Button>("EncounterSubmitButton");
        _encounterEndButton = this.FindControl<Button>("EncounterEndButton");
        _encounterStatusText = this.FindControl<TextBlock>("EncounterStatusText");
        _encounterStepLogText = this.FindControl<TextBlock>("EncounterStepLogText");
        _adventureAccountLabel = this.FindControl<TextBlock>("AdventureAccountLabel");
        _adventureTurnsLabel = this.FindControl<TextBlock>("AdventureTurnsLabel");
        _adventureKnucklebonesLabel = this.FindControl<TextBlock>("AdventureKnucklebonesLabel");
        _adventurePlanSummary = this.FindControl<TextBlock>("AdventurePlanSummary");
        _adventurePlanAdvisories = this.FindControl<TextBlock>("AdventurePlanAdvisories");
        _accountRoleSelector = this.FindControl<ComboBox>("AccountRoleSelector");
        _globalRefreshButton = this.FindControl<Button>("GlobalRefreshButton");
        _busyIndicator = this.FindControl<ProgressBar>("BusyIndicator");
        _cancelRefreshButton = this.FindControl<Button>("CancelRefreshButton");
        _selectedRole = ReadSelectedRole();
        _autoCombat = new AutoCombatViewModel(_catalog);
        ResetAccountViews();
        WireEncounterBrowser(createEncounterPane, tokenDirectory);
        SetActiveNavigation(_activeSection);
        UpdatePageState(_activeSection);
        SizeChanged += WindowSizeChanged;
        UpdateResponsiveLayout(Bounds.Width > 0 ? Bounds.Width : Width);
        Opened += async (_, _) => await OpenSectionAsync("Guts");
        Closed += (_, _) =>
        {
            _navigationVersion++;
            _refreshCancellation?.Cancel();
            _encounterBrowser?.Dispose();
            _live.Dispose();
        };
    }

    private void InitializeComponent() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);

    private void WindowKeyDown(object? sender, KeyEventArgs e)
    {
        // Let text editors keep ordinary typing and only consume the explicit
        // application gestures defined by the shared UX catalog.
        if (e.Key == Key.F1 && e.KeyModifiers == KeyModifiers.None)
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
            Key.D2 or Key.NumPad2 => "Inventory",
            Key.D3 or Key.NumPad3 => "Daily",
            Key.D4 or Key.NumPad4 => "Arbitrage",
            Key.D5 or Key.NumPad5 => "Mall & Shop",
            Key.D6 or Key.NumPad6 => "Adventure",
            Key.D7 or Key.NumPad7 => "Combat",
            Key.D8 or Key.NumPad8 => "Accounts",
            _ => null
        };
        if (section is null)
            return false;

        _ = OpenSectionAsync(section);
        return true;
    }

    private void ShowShortcutHelp()
    {
        SetStatus("Keyboard: Ctrl+1–8 switch tabs · F5 refresh · Ctrl+Esc cancel · F1 help.");
    }

    private void WindowSizeChanged(object? sender, SizeChangedEventArgs e)
        => UpdateResponsiveLayout(e.NewSize.Width);

    private void UpdateResponsiveLayout(double width)
    {
        // The menu bar stays in one row; only the content margin shrinks on
        // narrow windows so the item list keeps as much width as possible.
        var compact = width > 0 && width < 1080;
        if (_contentRoot is not null)
            _contentRoot.Margin = compact ? new Thickness(14, 12) : new Thickness(24, 16);
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
        _pageScroll?.ScrollToHome();
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
            (_navCombat, "Combat"),
            (_navAccounts, "Accounts")
        };
        foreach (var (button, tag) in entries)
        {
            if (button is null) continue;
            var active = string.Equals(section, tag, StringComparison.Ordinal);
            if (active) button.Classes.Add("active");
            else button.Classes.Remove("active");
        }
    }
    private async void SectionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string section })
            await OpenSectionAsync(section);
    }

    private async Task OpenSectionAsync(string section)
    {
        var version = ++_navigationVersion;
        Navigate(section);
        await _busyTask;
        if (version == _navigationVersion && IsVisible)
            await RefreshSectionAsync(forceRefresh: false);
    }

    private async void RefreshClick(object? sender, RoutedEventArgs e)
        => await RefreshSectionAsync(forceRefresh: true);

    private Task RefreshSectionAsync(bool forceRefresh)
    {
        var section = _activeSection;
        return RunBusyOperationAsync($"Loading {section}", async cancellationToken =>
        {
            switch (section)
            {
                case "Guts":
                    await _guts.LoadAsync(forceRefresh, cancellationToken);
                    ShowLoadResult(section, _guts);
                    break;
                case "Inventory":
                    await _inventory.LoadAsync(forceRefresh, cancellationToken);
                    ShowLoadResult(section, _inventory);
                    break;
                case "Daily":
                    await _daily.LoadAsync(forceRefresh, cancellationToken);
                    ShowLoadResult(section, _daily);
                    break;
                case "Arbitrage":
                    await _arbitrage.LoadAsync(forceRefresh, cancellationToken);
                    ShowLoadResult(section, _arbitrage);
                    break;
                case "Mall & Shop":
                    await _mall.LoadAsync(forceRefresh, cancellationToken);
                    ShowLoadResult(section, _mall);
                    break;
                case "Combat":
                    await LoadCombatCatalogAsync(cancellationToken);
                    ShowLoadResult(section, _autoCombat);
                    break;
                case "Accounts":
                    await LoadSavedAccountsAsync(cancellationToken);
                    break;
                case "Adventure":
                    await RefreshAdventureAsync(forceRefresh, cancellationToken);
                    break;
            }
        });
    }

    private void ShowLoadResult(string section, DesktopViewModel viewModel)
    {
        SetStatus(viewModel.LoadState == DesktopLoadState.Error
            ? $"{section}: {viewModel.ErrorMessage}"
            : $"{section} updated.");
    }

    private async void AccountRoleSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_accountRoleSelector is null || ReadSelectedRole() == _selectedRole)
            return;

        _selectedRole = ReadSelectedRole();
        ResetAccountViews();
        await OpenSectionAsync(_activeSection);
    }

    private Task RunBusyOperationAsync(string operation, Func<CancellationToken, Task> action)
    {
        if (_refreshCancellation is not null)
            return _busyTask;

        _busyTask = ExecuteBusyOperationAsync(operation, action);
        return _busyTask;
    }

    private async Task ExecuteBusyOperationAsync(
        string operation,
        Func<CancellationToken, Task> action)
    {
        using var cancellation = new CancellationTokenSource();
        _refreshCancellation = cancellation;
        SetBusy(true, $"{operation}…");
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
            SetStatus($"{operation} cancelled.");
        }
        catch (Exception exception)
        {
            SetStatus($"{operation} failed: {exception.Message}");
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
        SetStatus("Cancelling…");
    }

    private void SetBusy(bool busy, string? message = null)
    {
        if (_globalRefreshButton is not null)
        {
            _globalRefreshButton.IsEnabled = !busy;

        }
        if (_accountRoleSelector is not null)
            _accountRoleSelector.IsEnabled = !busy;
        if (_busyIndicator is not null)
            _busyIndicator.IsVisible = busy;
        if (_cancelRefreshButton is not null)
            _cancelRefreshButton.IsVisible = busy;
        if (_statusBar is not null)
            _statusBar.IsVisible = true;
        if (message is not null && _statusText is not null)
            _statusText.Text = message;
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
            SetStatus($"Sign in as {role} under Accounts to load the farm plan.");
            return;
        }

        using var http = KoLLoginClient.CreateAuthenticatedClient(
            session.Credentials,
            new Uri("https://www.kingdomofloathing.com/"));
        var account = new KoLAccountReadClient(http);
        var status = await account.FetchStatusAsync(forceRefresh, cancellationToken);
        var inventory = await account.FetchInventoryAsync(forceRefresh, cancellationToken);
        if (role != _selectedRole) return;
        if (!status.IsStale && _encounterBrowser?.ObserveAscension(role, session.Profile.Id, status.Ascensions) == true)
        {
            UpdateEncounterStatus("New ascension detected. The previous adventure was closed.");
            RefreshEncounterStepLog();
        }
        var runDetails = new List<string> { role.ToString() };
        if (status.PathId is { } path)
            runDetails.Add(path == 0 ? "No path" : string.IsNullOrWhiteSpace(status.PathName) ? $"Path {path}" : status.PathName);
        if (status.Ascensions is { } ascensions) runDetails.Add($"{ascensions} ascensions");
        if (status.Hardcore == true) runDetails.Add("Hardcore");
        if (status.RoninTurnsRemaining is > 0) runDetails.Add($"Ronin {status.RoninTurnsRemaining}");
        _adventureAccountLabel.Text = string.Join(" · ", runDetails);
        var knucklebones = inventory.Items
            .Where(item => item.ItemId == "12051")
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
        SetStatus("Adventure plan refreshed. No adventure or combat action was sent.");
    }

    private readonly List<KoLChatMessage> _chatMessages = new();
    private long _chatCursor;

    private async Task LoadSavedAccountsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _accounts.InitializeAsync(cancellationToken);
            await LoadSavedAccountAsync(AccountRole.Main, cancellationToken);
            await LoadSavedAccountAsync(AccountRole.Mallbot, cancellationToken);
            SetStatus("Account profiles loaded.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            SetStatus($"Could not load accounts: {exception.Message}");
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
        passwordBox.Text = string.Empty;
        rememberBox.IsChecked = !string.IsNullOrEmpty(session.Credentials.Password);
        status.Text = rememberBox.IsChecked == true ? "Password saved · Sign in to reconnect" : "Session saved";
        status.Foreground = ThemeBrush("WarningBrush", Brushes.DarkGoldenrod);
    }

    private async void SaveAccountClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !Enum.TryParse<AccountRole>(button.Tag?.ToString(), out var role))
            return;

        var usernameBox = this.FindControl<TextBox>(role == AccountRole.Main ? "MainUsername" : "MallbotUsername");
        var passwordBox = this.FindControl<TextBox>(role == AccountRole.Main ? "MainPassword" : "MallbotPassword");
        var rememberBox = this.FindControl<CheckBox>(role == AccountRole.Main ? "MainRememberPassword" : "MallbotRememberPassword");
        var status = this.FindControl<TextBlock>(role == AccountRole.Main ? "MainAccountStatus" : "MallbotAccountStatus");
        if (usernameBox is null || passwordBox is null || rememberBox is null || status is null)
            return;
        var username = usernameBox.Text?.Trim();
        var password = passwordBox.Text;

        button.IsEnabled = false;
        try
        {
            if (string.IsNullOrEmpty(password) && rememberBox.IsChecked == true)
            {
                var saved = await _accounts.GetSessionAsync(role, includeExpired: true);
                var savedUsername = saved?.Credentials.Username ?? saved?.Profile.DisplayName;
                if (string.Equals(username, savedUsername, StringComparison.OrdinalIgnoreCase))
                    password = saved?.Credentials.Password;
            }

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            {
                status.Text = "Enter a username and password.";
                status.Foreground = ThemeBrush("ErrorBrush", Brushes.IndianRed);
                return;
            }

            status.Text = "Logging in…";
            status.Foreground = ThemeBrush("WarningBrush", Brushes.DarkGoldenrod);
            var login = await _loginClient.LoginAsync(username, password, rememberPassword: rememberBox.IsChecked == true);
            var id = role == AccountRole.Main ? "main" : "mallbot";
            await _accounts.SetProfileAsync(new AccountProfile
            {
                Id = id,
                DisplayName = username,
                Role = role,
                Enabled = true
            });
            await _accounts.SetCredentialsAsync(role, login.Credentials);
            passwordBox.Text = string.Empty;
            ResetAccountViews();
            status.Text = "Connected";
            status.Foreground = ThemeBrush("SuccessBrush", Brushes.LightGreen);
            SetStatus($"{role} is logged in at KoL. Read-only data can be loaded now.");
        }
        catch (KoLLoginException exception)
        {
            passwordBox.Text = string.Empty;
            status.Text = "Login failed";
            status.Foreground = ThemeBrush("ErrorBrush", Brushes.IndianRed);
            SetStatus($"{role}: KoL login failed [{exception.Reason}] ({exception.Message}).");
        }
        catch (Exception exception)
        {
            passwordBox.Text = string.Empty;
            status.Text = "Could not save login";
            status.Foreground = ThemeBrush("ErrorBrush", Brushes.IndianRed);
            SetStatus($"{role} could not be saved: {exception.Message}");
        }
        finally
        {
            passwordBox.Text = string.Empty;
            button.IsEnabled = true;
        }
    }

    private void UpdatePageState(string section)
    {
        if (_gutsPanel is not null) _gutsPanel.IsVisible = section == "Guts";
        if (_inventoryPanel is not null) _inventoryPanel.IsVisible = section == "Inventory";
        if (_dailyPanel is not null) _dailyPanel.IsVisible = section == "Daily";
        if (_arbitragePanel is not null) _arbitragePanel.IsVisible = section == "Arbitrage";
        if (_mallSection is not null) _mallSection.IsVisible = section == "Mall & Shop";
        if (_adventurePanel is not null) _adventurePanel.IsVisible = section == "Adventure";
        if (_combatSection is not null) _combatSection.IsVisible = section == "Combat";
        if (_accountsPanel is not null) _accountsPanel.IsVisible = section == "Accounts";
        if (_shellPageTitle is not null)
            _shellPageTitle.Text = section == "Adventure" ? "Adventures" : section;
        if (_shellPageDescription is not null)
            _shellPageDescription.Text = section switch
            {
                "Guts" => $"All accounts · {_selectedRole} store",
                "Accounts" => "Main and Mallbot",
                "Combat" => "Auto-combat · Main and Mallbot",
                _ => _selectedRole.ToString()
            };
    }

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
    private void ResetAccountViews()
    {
        _encounterBrowser?.SelectAccount(_selectedRole);
        _chatCursor = 0;
        _chatMessages.Clear();
        if (this.FindControl<ComboBox>("KoLChatContacts") is { } chatContacts) chatContacts.ItemsSource = null;
        if (this.FindControl<TextBox>("KoLChatRecipient") is { } recipientBox) recipientBox.Text = string.Empty;
        if (this.FindControl<TextBox>("KoLChatDraft") is { } chatDraft) chatDraft.Text = string.Empty;
        RefreshKoLChatChannels();
        if (this.FindControl<TextBlock>("KoLChatStatus") is { } chatStatus) chatStatus.Text = string.Empty;
        ClearCharacterEquipment();
        if (this.FindControl<TextBlock>("ClanProfileStatus") is { } clan) clan.Text = string.Empty;
        if (this.FindControl<ItemsControl>("ClanMembers") is { } roster) roster.ItemsSource = null;
        if (this.FindControl<ItemsControl>("ClanLoungeFacilities") is { } lounge) lounge.ItemsSource = null;
        if (this.FindControl<TextBlock>("ClanLoungeStatus") is { } loungeStatus) loungeStatus.Text = string.Empty;
        if (this.FindControl<Button>("UseClanLookingGlass") is { } lookingGlass) lookingGlass.IsVisible = false;
        if (this.FindControl<Button>("UseClanCrimboTree") is { } crimboTree) crimboTree.IsVisible = false;
        if (this.FindControl<Button>("PlayClanKlaw") is { } klaw) klaw.IsVisible = false;
        if (this.FindControl<StackPanel>("ClanHotTubControls") is { } hotTub) hotTub.IsVisible = false;
        if (this.FindControl<StackPanel>("ClanPoolControls") is { } pool) pool.IsVisible = false;
        if (this.FindControl<StackPanel>("ClanShowerControls") is { } shower) shower.IsVisible = false;
        if (this.FindControl<StackPanel>("ClanSwimControls") is { } swim) swim.IsVisible = false;
        if (this.FindControl<StackPanel>("ClanFaxControls") is { } fax) fax.IsVisible = false;
        if (this.FindControl<WrapPanel>("ClanPoolChoiceControls") is { } poolChoice) poolChoice.IsVisible = false;
        if (this.FindControl<ComboBox>("ClanShowerTemperature") is { } temperature) temperature.SelectedIndex = -1;
        if (this.FindControl<ComboBox>("ClanPoolStance") is { } stance) stance.SelectedIndex = -1;
        if (this.FindControl<CheckBox>("AllowHotTubCurseRemoval") is { } hotTubConsent) hotTubConsent.IsChecked = false;
        if (this.FindControl<ListBox>("ClanStashItems") is { } stash) stash.ItemsSource = null;
        if (this.FindControl<TextBlock>("ClanStashStatus") is { } stashStatus) stashStatus.Text = string.Empty;
        if (this.FindControl<TextBox>("ClanStashQuantity") is { } stashQuantity) stashQuantity.Text = string.Empty;
        if (this.FindControl<ComboBox>("ClanStashDepositItems") is { } depositItems) depositItems.ItemsSource = null;
        if (this.FindControl<TextBox>("ClanStashDepositQuantity") is { } depositQuantity) depositQuantity.Text = string.Empty;
        _koLmailFolder = KoLMailFolder.Inbox;
        SetKoLmailDeleteConfirmation(false);
        SetKoLmailNavigation(1, false);
        if (this.FindControl<TextBox>("KoLmailRecipient") is { } recipient) recipient.Text = string.Empty;
        if (this.FindControl<TextBox>("KoLmailMessage") is { } message) message.Text = string.Empty;
        if (this.FindControl<TextBox>("KoLmailAttachments") is { } attachments) attachments.Text = string.Empty;
        if (this.FindControl<TextBox>("KoLmailMeat") is { } meat) meat.Text = string.Empty;
        if (this.FindControl<TextBlock>("KoLmailSendStatus") is { } sendStatus) sendStatus.Text = string.Empty;
        if (this.FindControl<ListBox>("KoLmailInboxRows") is { } inbox) inbox.ItemsSource = null;
        if (this.FindControl<TextBlock>("KoLmailInboxStatus") is { } mail) mail.Text = string.Empty;
        RefreshEncounterStepLog();
        _inventory = new InventoryViewModel(_selectedRole, _live, _catalog);
        _inventory.SearchText = _inventorySearchBox?.Text ?? string.Empty;
        _arbitrage = new LiveArbitrageViewModel(_selectedRole, _live, catalog: _catalog);
        _arbitrage.LoadRules();
        _mall = new LiveMallViewModel(_selectedRole, _live);
        if (this.FindControl<ListBox>("MallSearchRows") is { } results)
        {
            results.ItemsSource = null;
            results.IsVisible = false;
        }
        if (this.FindControl<TextBlock>("MallSearchStatus") is { } mallSearchStatus)
            mallSearchStatus.Text = string.Empty;
        _guts = new LiveGutsViewModel(_selectedRole, _live);
        _daily = new DailyBriefingViewModel(_selectedRole, _live);

        if (_gutsAccountCards is not null) _gutsAccountCards.ItemsSource = _guts.Accounts;
        if (_storeRows is not null) _storeRows.ItemsSource = _guts.StoreRows;
        if (_arbitrageRows is not null) _arbitrageRows.ItemsSource = _arbitrage.Rows;
        if (_arbitrageRules is not null) _arbitrageRules.ItemsSource = _arbitrage.Rules;
        if (_dailyRows is not null) _dailyRows.ItemsSource = _daily.Rows;
        if (_mallListingRows is not null) _mallListingRows.ItemsSource = _mall.Rows;

        _guts.PropertyChanged += (_, _) => RenderAccountSummaries();
        _arbitrage.PropertyChanged += (_, _) => RenderAccountSummaries();
        _daily.PropertyChanged += (_, _) => RenderAccountSummaries();
        _mall.PropertyChanged += (_, _) => RenderAccountSummaries();
        RenderAccountSummaries();
        WireInventoryBrowser();
    }

    private async void MallSearchClick(object? sender, RoutedEventArgs e) => await SearchMallAsync();

    private async void MallSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchMallAsync();
    }

    private Task SearchMallAsync()
    {
        var input = this.FindControl<TextBox>("MallSearchBox");
        var status = this.FindControl<TextBlock>("MallSearchStatus");
        var rows = this.FindControl<ListBox>("MallSearchRows");
        var query = input?.Text?.Trim();
        if (status is null || rows is null || _refreshCancellation is not null)
            return Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(query))
        {
            status.Text = "Enter an item name or ID.";
            return Task.CompletedTask;
        }

        var catalogItem = _catalog?.Items.FirstOrDefault(item =>
            string.Equals(item.Id, query, StringComparison.Ordinal)
            || string.Equals(item.Name, query, StringComparison.OrdinalIgnoreCase));
        if (int.TryParse(query, out _) && catalogItem is null)
        {
            status.Text = $"Item ID {query} is missing from the catalog. Search by name.";
            return Task.CompletedTask;
        }

        var role = _selectedRole;
        rows.IsVisible = false;
        status.Text = string.Empty;
        return RunBusyOperationAsync("Searching the mall", async cancellationToken =>
        {
            var name = catalogItem?.Name ?? query;
            var result = await _live.SearchMallAsync(role, name, cancellationToken);
            var offers = result.Listings
                .Where(listing => catalogItem is null || listing.ItemId == catalogItem.Id)
                .OrderBy(listing => listing.Price)
                .Select(listing => new MallMarketRow(
                    listing.StoreName ?? $"Store #{listing.StoreId}",
                    $"{listing.Price:N0} Meat", $"{listing.Quantity:N0} in stock",
                    listing.PurchaseLimit is > 0 ? $"{listing.PurchaseLimit} / day" : "—"))
                .ToArray();
            rows.ItemsSource = offers;
            rows.IsVisible = offers.Length > 0;
            status.Text = offers.Length == 0
                ? $"No offers for {name}."
                : $"{name} · {offers.Length:N0} offers · {result.FetchedAt.ToLocalTime():HH:mm}";
            if (result.IsStale) status.Text += " · stale";
            SetStatus($"Mall search finished for {role}.");
        });
    }

    private async void MallListingDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_mallListingRows?.SelectedItem is MallStoreRow row
            && this.FindControl<TextBox>("MallSearchBox") is { } input)
        {
            input.Text = row.ItemName;
            input.BringIntoView();
            await SearchMallAsync();
        }
    }

    private void RenderAccountSummaries()
    {
        if (_gutsSummaryText is not null) _gutsSummaryText.Text = _guts.GutsSummary;
        if (_storeSummaryText is not null) _storeSummaryText.Text = _guts.StoreSummary;
        if (_arbitrageSummaryText is not null) _arbitrageSummaryText.Text = _arbitrage.SummaryText;
        if (_arbitragePlanText is not null) _arbitragePlanText.Text = _arbitrage.PlanText;
        if (_dailySnapshotText is not null) _dailySnapshotText.Text = _daily.SnapshotText;
        if (_dailySummaryText is not null) _dailySummaryText.Text = _daily.SummaryText;
        if (_mallSummaryText is not null) _mallSummaryText.Text = _mall.Summary;
    }

    private void WireInventoryBrowser()
    {
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
                button.Classes.Set("active", category == _inventory.SelectedCategory);
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
        if (_inventoryLoadedText is not null) _inventoryLoadedText.Text = _inventory.LoadedAtText;
        RenderInventoryDetail();
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
        RenderInventoryActions(detail);
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

}
