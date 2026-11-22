using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;

namespace KoLibri.Desktop;

public partial class MainWindow : Window, IShellHost, IAdventuresViewHandlers, IInventoryViewHandlers,
    IGearViewHandlers, ISkillsEffectsViewHandlers, IDailyViewHandlers, IDailyStepHandlers
{
    // The tab views own their own name scopes; the shell keeps looking controls up by name
    // (and the tests do too), so the lookup falls through to the views.
    private Control?[] ViewScopes => new Control?[]
    {
        _charpaneView, _adventuresView, _inventoryView, _gearView, _skillsEffectsView, _dailyView
    };

    private CharpaneView? _charpaneView;
    private AdventuresView? _adventuresView;
    private InventoryView? _inventoryView;
    private GearView? _gearView;
    private SkillsEffectsView? _skillsEffectsView;
    private DailyView? _dailyView;

    /// <summary>Finds a control by x:Name in the shell or in one of the tab views.</summary>
    public T? FindControl<T>(string name) where T : Control
    {
        if (ControlExtensions.FindControl<T>(this, name) is { } own) return own;
        foreach (var view in ViewScopes)
            if (view is not null && ControlExtensions.FindControl<T>(view, name) is { } found)
                return found;
        return null;
    }

    private StackPanel? _contentRoot;
    private Button? _themeToggleButton;
    private bool _isOpsDark;
    private TextBlock? _statusText;
    private Border? _statusBar;
    private Button? _navInventory;
    private Button? _navDaily;
    private Button? _navMall;
    private Button? _navCommunication;
    private Button? _navSettings;
    private StackPanel? _communicationPanel;
    private StackPanel? _settingsPanel;
    private Expander? _arbitrageExpander;
    private readonly DesktopSettingsStore _settingsStore;
    private DesktopSettings _settings;
    private readonly SessionLogViewModel _log;
    private readonly MallSpendLedger _spendLedger;
    private object _accountToken = new();
    private bool _combatCatalogLoaded;
    private bool _arbitrageLoaded;
    private bool _signInOffered;
    private Button? _navAdventure;
    private ScrollViewer? _pageScroll;
    private readonly DesktopLiveDataProvider _live;
    private readonly KoLmafiaCatalog? _catalog;
    private Task _busyTask = Task.CompletedTask;
    private int _navigationVersion;
    private TextBlock? _shellPageTitle;
    private TextBlock? _shellPageDescription;
    private Control? _adventurePanel;
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
    private string _activeSection = "Adventure";
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
    private LiveArbitrageViewModel _arbitrage = null!;
    private LiveMallViewModel _mall = null!;
    private MallSearchViewModel _mallSearchModel = null!;
    private AutoCombatViewModel _autoCombat = null!;
    private StackPanel? _mallSection;
    private MallSearchView? _mallSearch;
    private ListBox? _combatTargetList;
    private ComboBox? _combatSkillPicker;
    private ComboBox? _combatItemPicker;
    private ListBox? _combatSkillList;
    private ListBox? _combatItemList;
    private NumericUpDown? _combatAbortHp;
    private NumericUpDown? _combatMaxRounds;
    private TextBlock? _combatPreviewText;
    private TextBlock? _combatRunStatusText;
    private StackPanel? _arbitragePanel;
    private ListBox? _arbitrageRows;
    private TextBlock? _arbitrageSummaryText;
    private ListBox? _arbitrageRules;
    private TextBlock? _arbitragePlanText;
    private Control? _dailyPanel;
    private BrowserView? _browserPanel;
    private Button? _navBrowser;
    private ListBox? _dailyRows;
    private TextBlock? _dailySnapshotText;
    private TextBlock? _dailySummaryText;
    private TextBlock? _dailyStatusText;

    public MainWindow() : this(null)
    {
    }

    public MainWindow(Func<Action<string>, EncounterBrowserPane>? createEncounterPane, string? tokenDirectory = null,
        AccountSessionManager? accounts = null, Func<AccountSession, HttpClient>? createAccountHttp = null,
        DesktopSettingsStore? settingsStore = null, bool applySettingsToLimiters = true)
    {
        _accounts = accounts ?? AccountSessionManager.CreateDefault();
        _settingsStore = settingsStore ?? new DesktopSettingsStore();
        _settings = _settingsStore.Load();
        if (applySettingsToLimiters) _settings.ApplyToLimiters();
        _log = new SessionLogViewModel(post: action =>
        {
            if (Dispatcher.UIThread.CheckAccess()) action(); else Dispatcher.UIThread.Post(action);
        });
        _spendLedger = new MallSpendLedger(path: MallSpendLedger.PathNextTo(_settingsStore));
        _live = new DesktopLiveDataProvider(_accounts, createAccountHttp, _settings,
            () => _settingsStore.Load().ReloginAutomatically);
        _catalog = TryLoadCatalog();
        InitializeComponent();
        _charpaneView = ControlExtensions.FindControl<CharpaneView>(this, "CharacterPane");
        _adventuresView = ControlExtensions.FindControl<AdventuresView>(this, "AdventurePanel");
        _inventoryView = ControlExtensions.FindControl<InventoryView>(this, "InventoryTab");
        _gearView = ControlExtensions.FindControl<GearView>(this, "InventoryGearView");
        _skillsEffectsView = ControlExtensions.FindControl<SkillsEffectsView>(this, "InventorySkillsView");
        _dailyView = ControlExtensions.FindControl<DailyView>(this, "DailyPanel");
        _charpaneView?.Attach(this);
        _adventuresView?.Attach(this, this);
        _inventoryView?.Attach(this, this, this);
        _gearView?.Attach(this, this);
        _skillsEffectsView?.Attach(this, this);
        AttachSkillsEffects();
        _dailyView?.Attach(this, this);
        ControlExtensions.FindControl<MallSearchView>(this, "MallSearch")?.AttachMenu(this);
        if (_inventoryView is not null)
            _inventoryView.SubViewChanged += view =>
            {
                if (_gearView is not null) _gearView.IsVisible = view == "Gear";
                if (_skillsEffectsView is not null) _skillsEffectsView.IsVisible = view == "Skills";
            };
        _pageScroll = this.FindControl<ScrollViewer>("PageScroll");
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
        _navInventory = this.FindControl<Button>("NavInventory");
        _navDaily = this.FindControl<Button>("NavDaily");
        _navMall = this.FindControl<Button>("NavMall");
        _navAdventure = this.FindControl<Button>("NavAdventure");
        _navCommunication = this.FindControl<Button>("NavCommunication");
        _navBrowser = this.FindControl<Button>("NavBrowser");
        _browserPanel = this.FindControl<BrowserView>("BrowserPanel");
        _browserPanel?.Attach(_accounts, () => _selectedRole, message => SetStatus(message),
            message => _log.Add(SessionLogKind.Info, message));
        _navSettings = this.FindControl<Button>("NavSettings");
        _communicationPanel = this.FindControl<StackPanel>("CommunicationPanel");
        _settingsPanel = this.FindControl<StackPanel>("SettingsPanel");
        _arbitrageExpander = this.FindControl<Expander>("ArbitrageExpander");
        _dailyPanel = this.FindControl<Control>("DailyPanel");
        _arbitragePanel = this.FindControl<StackPanel>("ArbitragePanel");
        _arbitrageRows = this.FindControl<ListBox>("ArbitrageRows");
        _arbitrageSummaryText = this.FindControl<TextBlock>("ArbitrageSummaryText");
        _arbitrageRules = this.FindControl<ListBox>("ArbitrageRules");
        _arbitragePlanText = this.FindControl<TextBlock>("ArbitragePlanText");
        _mallSection = this.FindControl<StackPanel>("MallSection");
        _mallSearch = this.FindControl<MallSearchView>("MallSearch");
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
        _adventurePanel = this.FindControl<Control>("AdventurePanel");
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
        if (Enum.TryParse<AccountRole>(_settings.DefaultAccount, ignoreCase: true, out var defaultRole)
            && _accountRoleSelector is not null)
            _accountRoleSelector.SelectedIndex = defaultRole == AccountRole.Main ? 0 : 1;
        _selectedRole = ReadSelectedRole();
        _autoCombat = new AutoCombatViewModel(_catalog);
        InitializeShell();
        ResetAccountViews();
        WireEncounterBrowser(createEncounterPane, tokenDirectory);
        SetActiveNavigation(_activeSection);
        UpdatePageState(_activeSection);
        SizeChanged += WindowSizeChanged;
        UpdateResponsiveLayout(Bounds.Width > 0 ? Bounds.Width : Width);
        Opened += async (_, _) =>
        {
            await StartupLoginAsync();
            await OpenSectionAsync(_activeSection);
            await OfferSignInIfNeededAsync();
        };
        Closed += (_, _) =>
        {
            _navigationVersion++;
            _refreshCancellation?.Cancel();
            _encounterBrowser?.Dispose();
            _browserPanel?.Dispose();
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

        var command = ShortcutModifier;
        if (e.Key == Key.OemPeriod && e.KeyModifiers == command)
        {
            CancelRefreshClick(sender, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers == command && TryNavigateShortcut(e.Key))
        {
            e.Handled = true;
        }
    }

    /// <summary>Command on macOS, Control elsewhere: Ctrl+number is taken by the system on a Mac.</summary>
    private static KeyModifiers ShortcutModifier
        => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    private bool TryNavigateShortcut(Key key)
    {
        var section = key switch
        {
            Key.D1 or Key.NumPad1 => "Adventure",
            Key.D2 or Key.NumPad2 => "Inventory",
            Key.D3 or Key.NumPad3 => "Mall & Shop",
            Key.D4 or Key.NumPad4 => "Daily",
            Key.D5 or Key.NumPad5 => "Communication",
            Key.OemComma => "Settings",
            _ => null
        };
        if (section is null)
            return false;

        _ = OpenSectionAsync(section);
        return true;
    }

    private void ShowShortcutHelp()
    {
        var key = OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl";
        SetStatus($"Keyboard: {key}+1–4 Adventures, Inventory, Mall, Daily · {key}+5 Chat & Mail · {key}+, Settings · F5 refresh · {key}+. cancel · F1 help.");
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
            (_navAdventure, "Adventure"),
            (_navInventory, "Inventory"),
            (_navMall, "Mall & Shop"),
            (_navDaily, "Daily"),
            (_navBrowser, "Browser"),
            (_navCommunication, "Communication"),
            (_navSettings, "Settings")
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

    public async void RefreshClick(object? sender, RoutedEventArgs e)
    {
        await RefreshSectionAsync(forceRefresh: true);
        await RefreshSidebarAsync(forceRefresh: true);
    }

    private Task RefreshSectionAsync(bool forceRefresh)
    {
        var section = _activeSection;
        return RunBusyOperationAsync($"Loading {section}", async cancellationToken =>
        {
            switch (section)
            {
                case "Inventory":
                    await _inventory.LoadAsync(forceRefresh, cancellationToken);
                    ShowLoadResult(section, _inventory);
                    break;
                case "Daily":
                    await _daily.LoadAsync(forceRefresh, cancellationToken);
                    ShowLoadResult(section, _daily);
                    if (_arbitrageExpander is { IsExpanded: true } && (forceRefresh || !_arbitrageLoaded))
                        await LoadArbitrageAsync(forceRefresh, cancellationToken);
                    break;
                case "Mall & Shop":
                    // Search first: the own shop loads only when its section is opened.
                    if (_mallSearch is not null)
                    {
                        if (forceRefresh) await _mallSearch.RefreshAsync();
                        else _mallSearch.FocusSearch();
                    }
                    break;
                case "Settings":
                    await LoadSettingsSectionAsync(cancellationToken);
                    break;
                case "Communication":
                    break;
                case "Browser":
                    if (_browserPanel is not null) await _browserPanel.OpenAsync();
                    break;
                case "Adventure":
                    await RefreshAdventureAsync(forceRefresh, cancellationToken);
                    break;
            }
        });
    }

    private void ShowLoadResult(string section, DesktopViewModel viewModel)
    {
        var failed = viewModel.LoadState == DesktopLoadState.Error;
        SetStatus(failed ? $"{section}: {viewModel.ErrorMessage}" : $"{section} updated.");
        if (failed) _log.Error($"{section}: {viewModel.ErrorMessage}");
    }

    private async void AccountRoleSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_accountRoleSelector is null || ReadSelectedRole() == _selectedRole)
            return;

        _selectedRole = ReadSelectedRole();
        if (_skillsEffectsModel is not null) _skillsEffectsModel.Role = _selectedRole.ToString();
        ResetAccountViews();
        _browserPanel?.ResetForRoleChange();
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
            _log.Error($"{operation} failed: {exception.Message}");
        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, cancellation))
                _refreshCancellation = null;
            SetBusy(false);
            _ = RefreshSidebarAsync();
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
            _adventurePlanSummary.Text = $"Sign {role} in under Settings → Accounts first.";
            _adventurePlanAdvisories.Text = "No session snapshot available.";
            SetStatus($"Sign in as {role} under Settings → Accounts to load the farm plan.");
            return;
        }

        // The charpane reads the same cached status through the provider: one fetch, not two.
        var status = await _live.LoadStatusAsync(role, forceRefresh, cancellationToken);
        var inventory = await _live.LoadInventoryAsync(role, forceRefresh, null, cancellationToken);
        if (status is null) return;
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
        var state = new KoLibri.Core.Spiel.AdventureState
        {
            IsStale = status.IsStale || inventory.IsStale,
            Knucklebones = Math.Max(0, knucklebones),
            AdventureTurnsRemaining = Math.Max(0, status.Adventures),
            SkeletonOfCrimbosPastAvailable = true
        };
        var plan = new KoLibri.Core.Spiel.DailyAdventurePlanner().Plan(state);
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
        if (_inventoryPanel is not null) _inventoryPanel.IsVisible = section == "Inventory";
        if (_dailyPanel is not null) _dailyPanel.IsVisible = section == "Daily";
        if (_mallSection is not null) _mallSection.IsVisible = section == "Mall & Shop";
        if (_adventurePanel is not null) _adventurePanel.IsVisible = section == "Adventure";
        if (_communicationPanel is not null) _communicationPanel.IsVisible = section == "Communication";
        if (_browserPanel is not null)
        {
            _browserPanel.IsVisible = section == "Browser";
            _browserPanel.Height = Math.Max(360, (Bounds.Height > 0 ? Bounds.Height : Height) - 230);
        }
        if (_settingsPanel is not null) _settingsPanel.IsVisible = section == "Settings";
        if (_shellPageTitle is not null)
            _shellPageTitle.Text = section switch
            {
                "Adventure" => "Adventures",
                "Mall & Shop" => "Mall",
                "Communication" => "Chat & Mail",
                _ => section
            };
        if (_shellPageDescription is not null)
            _shellPageDescription.Text = section switch
            {
                "Settings" => "Budgets, pacing, accounts, agent rights, combat rules",
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
        _mallSearchModel = new MallSearchViewModel(
            new LiveMallSearchBackend(_live, _selectedRole), LookupCatalogItems);
        _mallSearchModel.PurchaseRequest = CreatePurchaseCoordinator();
        _mallSearchModel.CloverThreshold = () => (long)_settings.Get("cloverThreshold");
        WatchMallSearch(_mallSearchModel);
        _mallSearch?.Attach(_mallSearchModel, _mall);
        _accountToken = new object();
        _arbitrageLoaded = false;
        _daily = new DailyBriefingViewModel(_selectedRole, _live, () => _settings);
        _dailyView?.Bind(_daily);

        if (_arbitrageRows is not null) _arbitrageRows.ItemsSource = _arbitrage.Rows;
        if (_arbitrageRules is not null) _arbitrageRules.ItemsSource = _arbitrage.Rules;
        if (_dailyRows is not null) _dailyRows.ItemsSource = _daily.Rows;

        _arbitrage.PropertyChanged += (_, _) => RenderAccountSummaries();
        _daily.PropertyChanged += (_, _) => RenderAccountSummaries();
        RenderAccountSummaries();
        WireInventoryBrowser();
    }

    private IReadOnlyList<(string Id, string Name)> LookupCatalogItems(string text)
        => _catalog?.Items
            .Where(item => item.Id == text || string.Equals(item.Name, text, StringComparison.OrdinalIgnoreCase))
            .Select(item => (item.Id, item.Name)).ToArray()
            ?? Array.Empty<(string, string)>();

    private void RenderAccountSummaries()
    {
        if (_arbitrageSummaryText is not null) _arbitrageSummaryText.Text = _arbitrage.SummaryText;
        if (_arbitragePlanText is not null) _arbitragePlanText.Text = _arbitrage.PlanText;
        if (_dailySnapshotText is not null) _dailySnapshotText.Text = _daily.SnapshotText;
        if (_dailySummaryText is not null) _dailySummaryText.Text = _daily.SummaryText;
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
                case nameof(InventoryViewModel.ActionResult):
                    if (_inventoryView?.FindControl<TextBlock>("InventoryActionResult") is { } result)
                        result.Text = _inventory.ActionResult;
                    break;
                case nameof(InventoryViewModel.OrganSummary):
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
            Text = $"Uses: {detail.UsesText}",
            FontSize = 12,
            Margin = new Avalonia.Thickness(0, 8, 0, 0)
        });
        _inventoryDetailPanel.Children.Add(new TextBlock
        {
            Text = $"Cheapest mall price: {detail.MallPriceText}",
            FontSize = 12
        });
        RenderInventoryActions(detail);
    }

    public void InventorySearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (_inventorySearchBox is null) return;
        _inventory.SearchText = _inventorySearchBox.Text ?? string.Empty;
    }

    public void InventorySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_inventoryList?.SelectedItem is DesktopInventoryItem item)
            _inventory.SelectedItem = item;
    }

}
