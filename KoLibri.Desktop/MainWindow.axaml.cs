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

    private async Task LoadCombatCatalogAsync(CancellationToken cancellationToken)
    {
        await _autoCombat.LoadAsync(cancellationToken);
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

    private async void CombatRunClick(object? sender, RoutedEventArgs e)
    {
        if (_encounterBrowser is not { Session: { } session } pane || _autoCombat.SelectedTarget is not { } target
            || !_autoCombat.HasConfiguration)
        {
            if (_combatRunStatusText is not null) _combatRunStatusText.Text = "Select a target and a skill first.";
            return;
        }

        await pane.FightCurrentAsync(target, _autoCombat.BuildPolicy(), _autoCombat.MaxRounds, _autoCombat.AbortHpPercent);
        if (_combatRunStatusText is not null && ReferenceEquals(pane.Session, session))
            _combatRunStatusText.Text = session.LastMessage;
        RefreshEncounterStepLog();
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

    private async void ReadKoLChatClick(object? sender, RoutedEventArgs e)
    {
        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync("Reading KoL chat", async token =>
        {
            var batch = await _live.ReadChatAsync(role, _chatCursor, token);
            if (role != _selectedRole || !ReferenceEquals(current, _guts)) return;
            var status = this.FindControl<TextBlock>("KoLChatStatus")!;
            if (batch is null)
            {
                status.Text = "Sign in to read KoL chat.";
                return;
            }

            AppendKoLChat(batch);
            status.Text = _chatMessages.Count == 0 ? "No chat messages." : "";
        });
    }

    private async void ReadKoLChatContactsClick(object? sender, RoutedEventArgs e)
    {
        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync("Reading KoL contacts", async token =>
        {
            var contacts = await _live.ReadChatContactsAsync(role, token);
            if (role != _selectedRole || !ReferenceEquals(current, _guts)) return;
            this.FindControl<ComboBox>("KoLChatContacts")!.ItemsSource = contacts;
            this.FindControl<TextBlock>("KoLChatStatus")!.Text = contacts switch
            {
                null => "Sign in to read KoL contacts.",
                { Count: 0 } => "No KoL contacts.",
                _ => ""
            };
        });
    }

    private void KoLChatContactChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: KoLChatContact contact }
            && this.FindControl<TextBox>("KoLChatRecipient") is { } recipient)
            recipient.Text = contact.PlayerId;
    }

    private async void SendKoLChatClick(object? sender, RoutedEventArgs e)
    {
        if (_refreshCancellation is not null) return;
        var recipient = this.FindControl<TextBox>("KoLChatRecipient")!;
        var draft = this.FindControl<TextBox>("KoLChatDraft")!;
        var status = this.FindControl<TextBlock>("KoLChatStatus")!;
        var channel = this.FindControl<ComboBox>("KoLChatChannel")?.SelectedItem as string;
        var target = string.IsNullOrWhiteSpace(recipient.Text) && channel?.StartsWith('/') == true
            ? channel : recipient.Text;
        var message = draft.Text;
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(message))
        {
            status.Text = "Choose a channel or a private recipient and enter a message.";
            return;
        }
        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync("Sending KoL chat", async token =>
        {
            var batch = await _live.SendChatAsync(role, target, message, _chatCursor, token);
            if (role != _selectedRole || !ReferenceEquals(current, _guts)) return;
            AppendKoLChat(batch);
            status.Text = batch.DeliveryConfirmed ? "Chat message sent."
                : batch.Output ?? "Delivery unconfirmed. Check chat before retrying.";
            if (batch.DeliveryConfirmed && draft.Text == message) draft.Text = string.Empty;
        });
    }

    private void AppendKoLChat(KoLChatBatch batch)
    {
        _chatCursor = batch.Cursor;
        var ids = _chatMessages.Select(message => message.Id).OfType<string>().ToHashSet();
        foreach (var message in batch.Messages)
            if (message.Id is null or "" or "0" || ids.Add(message.Id))
                _chatMessages.Add(message);
        if (_chatMessages.Count > 300)
            _chatMessages.RemoveRange(0, _chatMessages.Count - 300);
        RefreshKoLChatChannels();
    }

    private void RefreshKoLChatChannels()
    {
        if (this.FindControl<ComboBox>("KoLChatChannel") is not { } channel) return;
        var previous = channel.SelectedItem as string;
        var channels = new[] { "All channels" }.Concat(_chatMessages.Select(message => message.Channel)
            .Distinct().OrderBy(name => name, StringComparer.OrdinalIgnoreCase)).ToArray();
        channel.ItemsSource = channels;
        channel.SelectedIndex = Math.Max(0, Array.IndexOf(channels, previous));
        RefreshKoLChatRows();
    }

    private void KoLChatChannelChanged(object? sender, SelectionChangedEventArgs e) => RefreshKoLChatRows();

    private void RefreshKoLChatRows()
    {
        if (this.FindControl<ItemsControl>("KoLChatRows") is not { } rows) return;
        var channel = this.FindControl<ComboBox>("KoLChatChannel")?.SelectedItem as string;
        rows.ItemsSource = _chatMessages.Where(message => channel is null or "All channels" || message.Channel == channel)
            .ToArray();
    }

    private async void ReadClanProfileClick(object? sender, RoutedEventArgs e)
    {
        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync("Reading clan membership", async token =>
        {
            var profile = await _live.ReadClanProfileAsync(role, token);
            if (role != _selectedRole || !ReferenceEquals(current, _guts)) return;
            var rows = this.FindControl<ItemsControl>("ClanMembers")!;
            rows.ItemsSource = null;
            if (this.FindControl<TextBlock>("ClanProfileStatus") is { } status)
                status.Text = profile switch
                {
                    null => "Sign in to see clan membership.",
                    { IsMember: false } => "No clan membership.",
                    { Title: { Length: > 0 } title } => $"{profile.Name} · {title}",
                    _ => profile.Name
                };
            if (profile?.ClanId is not { } clanId) return;
            var members = await _live.ReadClanMembersAsync(role, clanId, token);
            if (role == _selectedRole && ReferenceEquals(current, _guts))
                rows.ItemsSource = members;
        });
    }

    private async void ReadClanLoungeClick(object? sender, RoutedEventArgs e)
    {
        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync("Reading clan VIP lounge", async token =>
        {
            var lounge = await _live.ReadClanLoungeAsync(role, token);
            if (role != _selectedRole || !ReferenceEquals(current, _guts)) return;
            this.FindControl<ItemsControl>("ClanLoungeFacilities")!.ItemsSource = lounge?.Facilities;
            this.FindControl<Button>("UseClanLookingGlass")!.IsVisible = lounge?.Facilities.Any(
                facility => facility.Action.Equals("lookingglass", StringComparison.OrdinalIgnoreCase)) == true;
            this.FindControl<Button>("UseClanCrimboTree")!.IsVisible = lounge?.Facilities.Any(
                facility => facility.Action.Equals("crimbotree", StringComparison.OrdinalIgnoreCase)) == true;
            this.FindControl<Button>("PlayClanKlaw")!.IsVisible = lounge?.Facilities.Any(
                facility => facility.Action.Equals("klaw", StringComparison.OrdinalIgnoreCase)) == true;
            this.FindControl<StackPanel>("ClanHotTubControls")!.IsVisible = lounge?.Facilities.Any(
                facility => facility.Action.Equals("hottub", StringComparison.OrdinalIgnoreCase)) == true;
            this.FindControl<StackPanel>("ClanPoolControls")!.IsVisible = lounge?.Facilities.Any(
                facility => facility.Action.Equals("pooltable", StringComparison.OrdinalIgnoreCase)) == true;
            this.FindControl<StackPanel>("ClanShowerControls")!.IsVisible = lounge?.Facilities.Any(
                facility => facility.Action.Equals("shower", StringComparison.OrdinalIgnoreCase)) == true;
            this.FindControl<StackPanel>("ClanSwimControls")!.IsVisible = lounge?.Facilities.Any(
                facility => facility.Action.Equals("swimmingpool", StringComparison.OrdinalIgnoreCase)) == true;
            this.FindControl<StackPanel>("ClanFaxControls")!.IsVisible = lounge?.Facilities.Any(
                facility => facility.Action.Equals("faxmachine", StringComparison.OrdinalIgnoreCase)) == true;
            this.FindControl<TextBlock>("ClanLoungeStatus")!.Text = lounge switch
            {
                null => "Sign in to see the clan VIP lounge.",
                { HasAccess: false } => "Clan VIP lounge unavailable.",
                { Facilities.Count: 0 } => "No lounge facilities listed.",
                _ => $"{lounge.Facilities.Count} lounge facilities."
            };
        });
    }

    private async void UseClanLookingGlassClick(object? sender, RoutedEventArgs e)
    {
        if (_refreshCancellation is not null || sender is not Button { IsVisible: true }) return;
        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync("Using clan looking glass", async token =>
        {
            var acquired = await _live.UseClanLookingGlassAsync(role, token);
            if (role == _selectedRole && ReferenceEquals(current, _guts))
                this.FindControl<TextBlock>("ClanLoungeStatus")!.Text = acquired
                    ? "Received a DRINK ME potion."
                    : "No DRINK ME potion received. The daily use may already be spent.";
        });
    }

    private async void UseClanCrimboTreeClick(object? sender, RoutedEventArgs e)
    {
        if (_refreshCancellation is not null || sender is not Button { IsVisible: true }) return;
        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync("Checking clan Crimbo Tree", async token =>
        {
            var result = await _live.UseClanCrimboTreeAsync(role, token);
            if (role != _selectedRole || !ReferenceEquals(current, _guts)) return;
            this.FindControl<TextBlock>("ClanLoungeStatus")!.Text = result switch
            {
                { ReceivedGift: true } => "Received a Crimbo gift.",
                { DaysUntilGift: 1 } => "Check the Crimbo Tree again tomorrow.",
                { DaysUntilGift: { } days } => $"Check the Crimbo Tree again in {days} days.",
                _ => "KoL did not confirm the Crimbo Tree status."
            };
        });
    }

    private async void PlayClanKlawClick(object? sender, RoutedEventArgs e)
    {
        if (_refreshCancellation is not null || sender is not Button { IsVisible: true }) return;
        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync("Playing Deluxe Mr. Klaw", async token =>
        {
            var result = await _live.PlayClanKlawAsync(role, token);
            if (role != _selectedRole || !ReferenceEquals(current, _guts)) return;
            this.FindControl<TextBlock>("ClanLoungeStatus")!.Text = result switch
            {
                KoLClanKlawResult.Prize => "Won a Deluxe Mr. Klaw prize.",
                KoLClanKlawResult.Played => "Played Deluxe Mr. Klaw. No prize received.",
                KoLClanKlawResult.DailyLimit => "The Deluxe Mr. Klaw daily limit is reached.",
                _ => "KoL did not confirm the Klaw result."
            };
        });
    }

    private async void UseClanHotTubClick(object? sender, RoutedEventArgs e)
    {
        if (_refreshCancellation is not null || this.FindControl<StackPanel>("ClanHotTubControls")?.IsVisible != true) return;
        var role = _selectedRole;
        var current = _guts;
        var consent = this.FindControl<CheckBox>("AllowHotTubCurseRemoval")!;
        var allowRemovingCurse = consent.IsChecked == true;
        consent.IsChecked = false;
        await RunBusyOperationAsync("Using clan Hot Tub", async token =>
        {
            var result = await _live.UseClanHotTubAsync(role, allowRemovingCurse, token);
            if (role == _selectedRole && ReferenceEquals(current, _guts))
                this.FindControl<TextBlock>("ClanLoungeStatus")!.Text = result;
        });
    }

    private async void PlayClanPoolClick(object? sender, RoutedEventArgs e)
    {
        if (_refreshCancellation is not null || this.FindControl<StackPanel>("ClanPoolControls")?.IsVisible != true) return;
        var stance = (KoLClanPoolStance)(this.FindControl<ComboBox>("ClanPoolStance")!.SelectedIndex + 1);
        var status = this.FindControl<TextBlock>("ClanLoungeStatus")!;
        if (!Enum.IsDefined(stance))
        {
            status.Text = "Choose a pool stance.";
            return;
        }

        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync("Playing clan pool", async token =>
        {
            var result = await _live.PlayClanPoolAsync(role, stance, token);
            if (role != _selectedRole || !ReferenceEquals(current, _guts)) return;
            status.Text = result switch
            {
                KoLClanPoolResult.Won => "Won the pool game.",
                KoLClanPoolResult.Lost => "Lost the pool game.",
                KoLClanPoolResult.PlayedAgainstSelf => "Played a game of pool against yourself.",
                KoLClanPoolResult.DailyLimit => "The pool daily limit is reached.",
                _ => "KoL did not confirm the pool game."
            };
        });
    }

    private async void TakeClanShowerClick(object? sender, RoutedEventArgs e)
    {
        if (_refreshCancellation is not null || this.FindControl<StackPanel>("ClanShowerControls")?.IsVisible != true) return;
        var temperature = (KoLClanShowerTemperature)(this.FindControl<ComboBox>("ClanShowerTemperature")!.SelectedIndex + 1);
        var status = this.FindControl<TextBlock>("ClanLoungeStatus")!;
        if (!Enum.IsDefined(temperature))
        {
            status.Text = "Choose a shower temperature.";
            return;
        }

        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync("Taking April Shower", async token =>
        {
            var result = await _live.TakeClanShowerAsync(role, temperature, token);
            if (role != _selectedRole || !ReferenceEquals(current, _guts)) return;
            status.Text = result switch
            {
                KoLClanShowerResult.ShowerTaken => "April Shower taken.",
                KoLClanShowerResult.DailyLimit => "Today's April Shower is already used.",
                _ => "KoL did not confirm the shower."
            };
        });
    }

    private async void SwimClanLapsClick(object? sender, RoutedEventArgs e)
        => await SwimInClanPoolAsync(KoLClanSwimWorkout.Laps);

    private async void SwimClanSprintsClick(object? sender, RoutedEventArgs e)
        => await SwimInClanPoolAsync(KoLClanSwimWorkout.Sprints);

    private async Task SwimInClanPoolAsync(KoLClanSwimWorkout workout)
    {
        if (_refreshCancellation is not null || this.FindControl<StackPanel>("ClanSwimControls")?.IsVisible != true) return;
        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync("Using clan swimming pool", async token =>
        {
            var result = await _live.SwimInClanPoolAsync(role, workout, token);
            if (role != _selectedRole || !ReferenceEquals(current, _guts)) return;
            this.FindControl<TextBlock>("ClanLoungeStatus")!.Text = result switch
            {
                { DailyLimit: true } => "Today's swimming workout is already used.",
                { Repetitions: { } count } when workout == KoLClanSwimWorkout.Laps => $"Swam {count} laps.",
                { Repetitions: { } count } => $"Completed {count} submarine sprints.",
                _ => "KoL did not confirm the swimming workout."
            };
        });
    }

    private async void ClanCannonballClick(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<StackPanel>("ClanSwimControls")?.IsVisible == true
            && this.FindControl<WrapPanel>("ClanPoolChoiceControls")?.IsVisible == false)
            await RunClanPoolChoiceAsync(null);
    }

    private async void ClanPoolHandstandClick(object? sender, RoutedEventArgs e)
        => await RunClanPoolChoiceAsync(KoLClanPoolMove.Handstand);

    private async void ClanPoolDiveClick(object? sender, RoutedEventArgs e)
        => await RunClanPoolChoiceAsync(KoLClanPoolMove.Dive);

    private async void ClanPoolBlinkClick(object? sender, RoutedEventArgs e)
        => await RunClanPoolChoiceAsync(KoLClanPoolMove.Blink);

    private async void LeaveClanPoolClick(object? sender, RoutedEventArgs e)
        => await RunClanPoolChoiceAsync(KoLClanPoolMove.Leave);

    private async void CheckClanPoolChoiceClick(object? sender, RoutedEventArgs e)
    {
        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync("Checking clan pool", async token =>
        {
            var choice = await _live.ReadClanPoolChoiceAsync(role, token);
            if (role == _selectedRole && ReferenceEquals(current, _guts) && choice is not null)
                ShowClanPoolChoice(choice);
        });
    }

    private async Task RunClanPoolChoiceAsync(KoLClanPoolMove? move)
    {
        if (_refreshCancellation is not null
            || (move is not null && this.FindControl<WrapPanel>("ClanPoolChoiceControls")?.IsVisible != true))
            return;
        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync("Using clan pool choice", async token =>
        {
            var choice = await _live.SubmitClanPoolChoiceAsync(role, move, token);
            if (role == _selectedRole && ReferenceEquals(current, _guts)) ShowClanPoolChoice(choice);
        });
    }

    private void ShowClanPoolChoice(KoLClanPoolChoice choice)
    {
        this.FindControl<WrapPanel>("ClanPoolChoiceControls")!.IsVisible = choice.IsActive;
        this.FindControl<TextBlock>("ClanLoungeStatus")!.Text = choice.Treasure is { } item
            ? $"Found {item} in the pool."
            : choice.IsActive ? "In the clan pool." : "No active clan pool choice.";
    }

    private async void ReceiveClanFaxClick(object? sender, RoutedEventArgs e)
        => await TransferClanFaxAsync(send: false);

    private async void SendClanFaxClick(object? sender, RoutedEventArgs e)
        => await TransferClanFaxAsync(send: true);

    private async void InspectClanFaxClick(object? sender, RoutedEventArgs e)
    {
        if (_refreshCancellation is not null || this.FindControl<StackPanel>("ClanFaxControls")?.IsVisible != true) return;
        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync("Inspecting clan photocopy", async token =>
        {
            var monster = await _live.ReadClanPhotocopyMonsterAsync(role, token);
            if (role == _selectedRole && ReferenceEquals(current, _guts))
                this.FindControl<TextBlock>("ClanLoungeStatus")!.Text = monster is null
                    ? "No copied monster identified." : $"Photocopied monster: {monster}.";
        });
    }

    private async Task TransferClanFaxAsync(bool send)
    {
        if (_refreshCancellation is not null || this.FindControl<StackPanel>("ClanFaxControls")?.IsVisible != true) return;
        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync(send ? "Sending clan fax" : "Receiving clan fax", async token =>
        {
            var completed = await _live.TransferClanFaxAsync(role, send, token);
            if (role != _selectedRole || !ReferenceEquals(current, _guts)) return;
            var status = this.FindControl<TextBlock>("ClanLoungeStatus")!;
            status.Text = completed
                ? send ? "Photocopy sent." : "Photocopy received."
                : "KoL did not confirm the fax transfer.";
            if (!completed || send) return;
            var monster = await _live.ReadClanPhotocopyMonsterAsync(role, token);
            if (monster is not null && role == _selectedRole && ReferenceEquals(current, _guts))
                status.Text = $"Photocopy received: {monster}.";
        });
    }

    private async void ReadClanStashClick(object? sender, RoutedEventArgs e)
    {
        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync("Reading clan stash", async token =>
        {
            var stash = await _live.ReadClanStashAsync(role, token);
            if (role == _selectedRole && ReferenceEquals(current, _guts))
                ShowClanStash(stash);
        });
    }

    private async void TakeClanStashClick(object? sender, RoutedEventArgs e)
    {
        var rows = this.FindControl<ListBox>("ClanStashItems")!;
        var status = this.FindControl<TextBlock>("ClanStashStatus")!;
        var quantityBox = this.FindControl<TextBox>("ClanStashQuantity")!;
        if (_refreshCancellation is not null) return;
        if (rows.SelectedItem is not KoLClanStashItem item)
        {
            status.Text = "Select a stash item to take.";
            return;
        }
        if (!int.TryParse(quantityBox.Text, out var quantity) || quantity <= 0 || quantity > item.Quantity)
        {
            status.Text = $"Enter a quantity between 1 and {item.Quantity}.";
            return;
        }

        var role = _selectedRole;
        var current = _guts;
        rows.ItemsSource = null;
        await RunBusyOperationAsync("Taking clan stash item", async token =>
        {
            var acquired = await _live.TransferClanStashItemAsync(role, item.ItemId, quantity, false, token);
            var updated = await _live.ReadClanStashAsync(role, token);
            if (role != _selectedRole || !ReferenceEquals(current, _guts)) return;
            ShowClanStash(updated);
            status.Text = acquired ? $"KoL reported acquiring {item.Name}." : "KoL did not confirm the withdrawal.";
        });
    }

    private async void LoadClanStashDepositItemsClick(object? sender, RoutedEventArgs e)
    {
        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync("Reading inventory for clan stash", async token =>
        {
            var inventory = await _live.LoadInventoryAsync(role, true, _catalog, token);
            if (role != _selectedRole || !ReferenceEquals(current, _guts)) return;
            ShowClanDepositInventory(inventory);
            if (inventory is { HasSession: false } or { IsStale: true })
                this.FindControl<TextBlock>("ClanStashStatus")!.Text = "Live inventory is unavailable.";
        });
    }

    private async void DepositClanStashClick(object? sender, RoutedEventArgs e)
    {
        if (_refreshCancellation is not null) return;
        var picker = this.FindControl<ComboBox>("ClanStashDepositItems")!;
        var status = this.FindControl<TextBlock>("ClanStashStatus")!;
        if (picker.SelectedItem is not DesktopInventoryItem item)
        {
            status.Text = "Select an inventory item to deposit.";
            return;
        }
        var quantityBox = this.FindControl<TextBox>("ClanStashDepositQuantity")!;
        if (!int.TryParse(quantityBox.Text, out var quantity) || quantity <= 0 || quantity > item.Quantity)
        {
            status.Text = $"Enter a quantity between 1 and {item.Quantity}.";
            return;
        }

        var role = _selectedRole;
        var current = _guts;
        picker.ItemsSource = null;
        await RunBusyOperationAsync("Depositing clan stash item", async token =>
        {
            var deposited = await _live.TransferClanStashItemAsync(role, item.ItemId, quantity, true, token);
            var stash = await _live.ReadClanStashAsync(role, token);
            var inventory = await _live.LoadInventoryAsync(role, true, _catalog, token);
            if (role != _selectedRole || !ReferenceEquals(current, _guts)) return;
            ShowClanStash(stash);
            ShowClanDepositInventory(inventory);
            status.Text = deposited ? $"KoL reported depositing {item.Name}." : "KoL did not confirm the deposit.";
        });
    }

    private void ShowClanDepositInventory(DesktopInventorySnapshot inventory)
    {
        var picker = this.FindControl<ComboBox>("ClanStashDepositItems")!;
        picker.ItemsSource = inventory.HasSession && !inventory.IsStale
            ? inventory.Items.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray()
            : null;
        picker.SelectedIndex = -1;
    }

    private void ShowClanStash(KoLClanStashSnapshot? stash)
    {
        this.FindControl<ListBox>("ClanStashItems")!.ItemsSource = stash?.Items;
        this.FindControl<TextBlock>("ClanStashStatus")!.Text = stash switch
        {
            null => "Sign in to see the clan stash.",
            { CanRead: false } => "Clan stash list unavailable.",
            { Items.Count: 0 } => "The clan stash is empty.",
            _ => $"{stash.Items.Count} item types in the clan stash."
        };
    }

    private int _koLmailPage = 1;
    private KoLMailFolder _koLmailFolder;

    private async void ReadKoLmailInboxClick(object? sender, RoutedEventArgs e)
        => await ReadKoLmailPageAsync(KoLMailFolder.Inbox, 1);

    private async void ReadKoLmailSavedClick(object? sender, RoutedEventArgs e)
        => await ReadKoLmailPageAsync(KoLMailFolder.Saved, 1);

    private async void ReadKoLmailOutboxClick(object? sender, RoutedEventArgs e)
        => await ReadKoLmailPageAsync(KoLMailFolder.Outbox, 1);

    private async void PreviousKoLmailPageClick(object? sender, RoutedEventArgs e)
        => await ReadKoLmailPageAsync(_koLmailFolder, _koLmailPage - 1);

    private async void NextKoLmailPageClick(object? sender, RoutedEventArgs e)
        => await ReadKoLmailPageAsync(_koLmailFolder, _koLmailPage + 1);

    private async Task ReadKoLmailPageAsync(KoLMailFolder folder, int page)
    {
        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync("Reading KoLmail", async token =>
        {
            var inbox = await _live.ReadMailboxPageAsync(role, folder, page, token);
            if (role != _selectedRole || !ReferenceEquals(current, _guts)) return;
            if (inbox is null)
            {
                if (this.FindControl<TextBlock>("KoLmailInboxStatus") is { } loginStatus)
                    loginStatus.Text = "Sign in to read KoLmail.";
                return;
            }
            ShowKoLmailPage(folder, inbox);
        });
    }

    private void ShowKoLmailPage(KoLMailFolder folder, KoLMailboxSnapshot inbox)
    {
        _koLmailFolder = folder;
        SetKoLmailDeleteConfirmation(false);
        SetKoLmailNavigation(inbox.Page, inbox.Recognized && inbox.HasNextPage);
        if (this.FindControl<ListBox>("KoLmailInboxRows") is { } rows)
        {
            rows.ItemsSource = inbox.Entries;
            rows.SelectedIndex = -1;
        }
        if (this.FindControl<TextBlock>("KoLmailInboxStatus") is { } status)
            status.Text = !inbox.Recognized ? "KoLmail response could not be read."
                : inbox.Entries.Count == 0 ? $"{folder} is empty."
                : $"{(folder == KoLMailFolder.Inbox ? "" : $"{folder} · ")}Page {inbox.Page} · {inbox.Entries.Count} messages.";
    }

    private async void SaveKoLmailClick(object? sender, RoutedEventArgs e)
    {
        var status = this.FindControl<TextBlock>("KoLmailInboxStatus")!;
        if (_koLmailFolder != KoLMailFolder.Inbox
            || this.FindControl<ListBox>("KoLmailInboxRows")?.SelectedItem is not KoLMailEntry message)
        {
            status.Text = "Select a message from the inbox to save it.";
            return;
        }

        var role = _selectedRole;
        var current = _guts;
        await RunBusyOperationAsync("Saving KoLmail", async token =>
        {
            await _live.SaveInboxMessageAsync(role, message.Id, token);
            var saved = await _live.ReadMailboxPageAsync(role, KoLMailFolder.Saved, 1, token);
            if (role != _selectedRole || !ReferenceEquals(current, _guts)) return;
            if (saved is not null && saved.Recognized && saved.Entries.Any(entry => entry.Id == message.Id))
                ShowKoLmailPage(KoLMailFolder.Saved, saved);
            else
                status.Text = "Save was unconfirmed. Check Saved mail.";
        });
    }

    private void KoLmailSelectionChanged(object? sender, SelectionChangedEventArgs e)
        => SetKoLmailDeleteConfirmation(false);

    private void SetKoLmailDeleteConfirmation(bool visible)
    {
        if (this.FindControl<Button>("KoLmailConfirmDelete") is { } confirm)
            confirm.IsVisible = visible;
    }

    private void DeleteKoLmailClick(object? sender, RoutedEventArgs e)
    {
        if (_refreshCancellation is null && this.FindControl<ListBox>("KoLmailInboxRows")?.SelectedItem is KoLMailEntry)
            SetKoLmailDeleteConfirmation(true);
    }

    private async void ConfirmDeleteKoLmailClick(object? sender, RoutedEventArgs e)
    {
        if (_refreshCancellation is not null || sender is not Button { IsVisible: true }
            || this.FindControl<ListBox>("KoLmailInboxRows")?.SelectedItem is not KoLMailEntry entry)
            return;

        SetKoLmailDeleteConfirmation(false);
        var role = _selectedRole;
        var current = _guts;
        var folder = _koLmailFolder;
        await RunBusyOperationAsync("Deleting KoLmail", async token =>
        {
            await _live.DeleteMailboxMessageAsync(role, folder, entry.Id, token);
            var snapshot = await _live.ReadMailboxPageAsync(role, folder, 1, token);
            if (snapshot is not null && role == _selectedRole && ReferenceEquals(current, _guts))
                ShowKoLmailPage(folder, snapshot);
        });
    }

    private async void SendKoLmailClick(object? sender, RoutedEventArgs e)
    {
        if (_refreshCancellation is not null || sender is not Button send) return;

        var recipientBox = this.FindControl<TextBox>("KoLmailRecipient")!;
        var messageBox = this.FindControl<TextBox>("KoLmailMessage")!;
        var status = this.FindControl<TextBlock>("KoLmailSendStatus")!;
        var recipient = recipientBox.Text;
        var message = messageBox.Text ?? string.Empty;
        var attachmentBox = this.FindControl<TextBox>("KoLmailAttachments")!;
        var meatBox = this.FindControl<TextBox>("KoLmailMeat")!;
        var attachments = new List<KoLMailAttachment>();
        var itemIds = new HashSet<int>();
        foreach (var line in (attachmentBox.Text ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (pair.Length != 2 || !int.TryParse(pair[0], out var id) || id <= 0
                || !int.TryParse(pair[1], out var quantity) || quantity <= 0 || !itemIds.Add(id) || attachments.Count == 11)
            {
                status.Text = "Enter up to 11 distinct item IDs and positive quantities, one pair per line.";
                return;
            }
            attachments.Add(new KoLMailAttachment(id, quantity));
        }

        long meat = 0;
        if (!string.IsNullOrWhiteSpace(meatBox.Text) && (!long.TryParse(meatBox.Text, out meat) || meat < 0))
        {
            status.Text = "Enter a nonnegative whole Meat amount.";
            return;
        }

        if (string.IsNullOrWhiteSpace(recipient) || (string.IsNullOrWhiteSpace(message) && attachments.Count == 0 && meat == 0))
        {
            status.Text = "Enter a recipient and a message or attachment.";
            return;
        }

        var role = _selectedRole;
        send.IsEnabled = false;
        recipientBox.IsEnabled = messageBox.IsEnabled = attachmentBox.IsEnabled = meatBox.IsEnabled = false;
        status.Text = $"Sending as {role}…";
        try
        {
            await RunBusyOperationAsync("Sending KoLmail", async token =>
            {
                if (!await _live.SendKoLmailAsync(role, recipient, message, attachments, meat, token)) return;
                status.Text = $"Message sent as {role}.";
                recipientBox.Text = string.Empty;
                messageBox.Text = attachmentBox.Text = meatBox.Text = string.Empty;
            });
            if (status.Text == $"Sending as {role}…")
                status.Text = "Delivery was unconfirmed. Check sent mail before retrying.";
        }
        finally
        {
            send.IsEnabled = true;
            recipientBox.IsEnabled = messageBox.IsEnabled = attachmentBox.IsEnabled = meatBox.IsEnabled = true;
        }
    }

    private void SetKoLmailNavigation(int page, bool hasNextPage)
    {
        _koLmailPage = page;
        if (this.FindControl<Button>("KoLmailPrevious") is { } previous) previous.IsEnabled = page > 1;
        if (this.FindControl<Button>("KoLmailNext") is { } next) next.IsEnabled = hasNextPage;
    }

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

    private void DailySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_dailyRows?.SelectedItem is not DailyBriefingRow row) return;
        if (_dailyStatusText is not null)
            _dailyStatusText.Text = $"{row.Surface}: {row.Description} — {row.Detail}";
    }

    private async void DailyRunClick(object? sender, RoutedEventArgs e)
    {
        var token = Guid.NewGuid().ToString("N");
        var role = _selectedRole;
        await RunBusyOperationAsync("Daily routine", async cancellationToken =>
        {
            var account = await _accounts
                .GetSessionAsync(role, cancellationToken: cancellationToken)
                ;
            if (account is null)
            {
                if (_dailyStatusText is not null)
                    _dailyStatusText.Text = "No session for the selected account.";
                return;
            }
            var ok = await RunDailyRoutineAsync(account, token, cancellationToken);
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
        using var http = KoLLoginClient.CreateAuthenticatedClient(
            account.Credentials, new Uri("https://www.kingdomofloathing.com/"));
        var readClient = new KoLAccountReadClient(http);
        var doneStore = new KolArbitrage.Core.Daily.DailyDoneStore();
        var live = await new LiveDailySnapshotBuilder(readClient, doneStore)
            .BuildAsync(new KolArbitrage.Core.Daily.DailyRoutineSnapshot(), cancellationToken,
                role: account.Profile.Role);
        var bridge = new KolArbitrage.Core.Daily.AutoCombatDailyBridge();
        var plan = bridge.Plan(live.State, live.Snapshot);
        // The desktop button authorizes this run. The token correlates its requests.
        var approval = new DelegateAdventureApprovalHook((_, _) =>
            ValueTask.FromResult(AdventureApprovalDecision.Allow(approvalToken)));
        var verification = new DelegateAdventureVerificationHook((context, _) =>
            ValueTask.FromResult(context.Response.Accepted != false
                ? AdventureVerificationResult.Success()
                : AdventureVerificationResult.Failed("Die Live-Antwort wurde von KoL abgelehnt.")));
        await using var session = KoLAdventureSessionFactory.Create(account,
            KoLLoginClient.CreateAuthenticatedClient(account.Credentials, new Uri("https://www.kingdomofloathing.com/")),
            approval, verification);
        var dispatcher = new KolArbitrage.Core.Daily.FreePullDispatcher(
            http, KolArbitrage.Core.Daily.FreePullDispatcher.DefaultBaseUri);
        using var audit = new KolArbitrage.Core.Daily.BridgeAuditLog(
            KolArbitrage.Core.Daily.BridgeAuditLog.DefaultPath());
        var result = await bridge.ExecuteAsync(
            plan.Steps, session.Gateway,
            freePullDispatcher: dispatcher,
            freePullCredentials: account.Credentials,
            audit: audit,
            accountId: account.Profile.Id,
            cancellationToken: cancellationToken,
            doneStore: doneStore,
            doneAccountKey: account.Profile.Role.ToString());
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

    private void WireEncounterBrowser(
        Func<Action<string>, EncounterBrowserPane>? createEncounterPane, string? tokenDirectory)
    {
        var catalogDir = Path.Combine(
            AppContext.BaseDirectory,
            "external", "kolmafia-data");
        if (!Directory.Exists(catalogDir))
            return;

        _encounterBrowser = createEncounterPane?.Invoke(UpdateEncounterStatus)
            ?? new EncounterBrowserPane(_accounts, UpdateEncounterStatus);
        try
        {
            _encounterBrowser.Initialise(catalogDir, tokenDirectory);
            _encounterBrowser.SelectAccount(_selectedRole);
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

        if (_encounterStartButton is not null)
            _encounterStartButton.Click += async (_, _) =>
            {
                if (_encounterLocationsGrid?.SelectedItem is EncounterBrowserLocationRow row)
                {
                    _encounterBrowser.ApplyApprovalToken(Guid.NewGuid().ToString("N"));
                    await _encounterBrowser.StartAdventureAsync(row);
                }
                RefreshEncounterStepLog();
            };

        if (this.FindControl<Button>("EncounterBatchStart") is { } runTurns)
            runTurns.Click += async (_, _) =>
            {
                if (_encounterLocationsGrid?.SelectedItem is not EncounterBrowserLocationRow row) return;
                var budget = (int)(this.FindControl<NumericUpDown>("EncounterBatchTurns")?.Value ?? 1);
                var item = this.FindControl<TextBox>("EncounterBatchItem")?.Text?.Trim();
                if (!string.IsNullOrEmpty(item) && (!int.TryParse(item, out var id) || id <= 0))
                {
                    UpdateEncounterStatus("Enter a positive item ID or leave it blank.");
                    return;
                }
                _encounterBrowser.ApplyApprovalToken(Guid.NewGuid().ToString("N"));
                await _encounterBrowser.RunTurnsAsync(row, budget, item);
                RefreshEncounterStepLog();
            };

        if (this.FindControl<Button>("EncounterBatchContinue") is { } continueTurns)
            continueTurns.Click += async (_, _) =>
            {
                await _encounterBrowser.RunTurnsAsync(null);
                RefreshEncounterStepLog();
            };

        if (_encounterSubmitButton is not null)
            _encounterSubmitButton.Click += async (_, _) =>
            {
                if (_encounterActionsGrid?.SelectedItem is EncounterBrowserActionRow row)
                    await _encounterBrowser.SubmitStepAsync(row);
                RefreshEncounterStepLog();
            };

        if (this.FindControl<Button>("EncounterRecheckButton") is { } recheck)
            recheck.Click += async (_, _) =>
            {
                await _encounterBrowser.RecheckAsync();
                RefreshEncounterStepLog();
            };

        if (_encounterEndButton is not null)
            _encounterEndButton.Click += (_, _) =>
            {
                _encounterBrowser.EndSession();
                RefreshEncounterStepLog();
            };

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
        _encounterStepLogText.Text = string.Join("\n",
            new[] { session.LastEncounterSummary }.OfType<string>().Concat(lines));
    }
}
