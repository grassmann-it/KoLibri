using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using KolArbitrage.Core.Accounts;
using KolArbitrage.Core.Adventure;
using KolArbitrage.Core.Analytics;
using KolArbitrage.Core.Data;
using KolArbitrage.Core.Integration;

namespace KoLibri.Desktop;

public partial class MainWindow : Window
{
    private Border? _sidebar;
    private Border? _contextRail;
    private Border? _compactNavigation;
    private StackPanel? _contentRoot;
    private StackPanel? _overviewPanel;
    private Button? _themeToggleButton;
    private Button? _compactThemeToggleButton;
    private TextBlock? _sidebarThemeLabel;
    private bool _isOpsDark;
    private TextBlock? _statusText;
    private Border? _statusBar;
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
    private TextBlock? _contextRailTitle;
    private TextBlock? _contextRailKind;
    private TextBlock? _contextRailDetails;
    private ComboBox? _accountRoleSelector;
    private Button? _globalRefreshButton;
    private ProgressBar? _busyIndicator;
    private Button? _cancelRefreshButton;
    private CancellationTokenSource? _refreshCancellation;
    private string _activeSection = "Übersicht";
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

    public MainWindow()
    {
        InitializeComponent();
        _sidebar = this.FindControl<Border>("Sidebar");
        _contextRail = this.FindControl<Border>("ContextRail");
        _compactNavigation = this.FindControl<Border>("CompactNavigation");
        _contentRoot = this.FindControl<StackPanel>("ContentRoot");
        _overviewPanel = this.FindControl<StackPanel>("OverviewPanel");
        _themeToggleButton = this.FindControl<Button>("ThemeToggleButton");
        _compactThemeToggleButton = this.FindControl<Button>("CompactThemeToggleButton");
        _sidebarThemeLabel = this.FindControl<TextBlock>("SidebarThemeLabel");
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
        _contextRailTitle = this.FindControl<TextBlock>("ContextRailTitle");
        _contextRailKind = this.FindControl<TextBlock>("ContextRailKind");
        _contextRailDetails = this.FindControl<TextBlock>("ContextRailDetails");
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
            Key.D1 or Key.NumPad1 => "Übersicht",
            Key.D2 or Key.NumPad2 => "Pricing Engine",
            Key.D3 or Key.NumPad3 => "Mall & Shop",
            Key.D4 or Key.NumPad4 => "Adventure",
            Key.D5 or Key.NumPad5 => "Combat",
            Key.D6 or Key.NumPad6 => "Konten & Transfer",
            Key.D7 or Key.NumPad7 => "Einstellungen",
            _ => null
        };
        if (section is null)
            return false;

        Navigate(section);
        return true;
    }

    private void ShowShortcutHelp()
    {
        SetStatus("Tastatur: Ctrl+1..7 Bereiche · F5 Aktualisieren · Shift+F10 Kontextvorschau · Ctrl+Esc Abbrechen · F1 Hilfe.");
        if (_contextRailDetails is not null)
            _contextRailDetails.Text = "Ctrl+1..7 wechseln den Bereich. F5 aktualisiert. Shift+F10 öffnet die sichere Kontextvorschau. Ctrl+Esc bricht laufende lokale Reads ab.";
    }

    private void WindowSizeChanged(object? sender, SizeChangedEventArgs e)
        => UpdateResponsiveLayout(e.NewSize.Width);

    private void UpdateResponsiveLayout(double width)
    {
        // Keep the desktop rail when there is room for it. At smaller widths
        // the same commands move into a horizontally scrollable top rail;
        // no navigation state or handler changes between the two shells.
        var compact = width > 0 && width < 1080;
        var showContextRail = width >= 1320;
        if (_sidebar is not null)
            _sidebar.IsVisible = !compact;
        if (_contextRail is not null)
            _contextRail.IsVisible = !compact && showContextRail;
        if (_compactNavigation is not null)
            _compactNavigation.IsVisible = compact;
        if (_contentRoot is not null)
            _contentRoot.Margin = compact ? new Thickness(20, 20) : new Thickness(34, 30);
    }

    private void ThemeToggleClick(object? sender, RoutedEventArgs e)
    {
        _isOpsDark = !_isOpsDark;
        if (Application.Current is not null)
            Application.Current.RequestedThemeVariant = _isOpsDark ? ThemeVariant.Dark : ThemeVariant.Light;

        var themeName = _isOpsDark ? "Ops Dark" : "Stickman Standard";
        var buttonText = _isOpsDark ? "☀  Stickman Standard öffnen" : "☾  Ops Dark öffnen";
        var compactButtonText = _isOpsDark ? "☀ Stickman" : "☾ Ops Dark";
        if (_themeToggleButton is not null)
            _themeToggleButton.Content = buttonText;
        if (_compactThemeToggleButton is not null)
            _compactThemeToggleButton.Content = compactButtonText;
        if (_sidebarThemeLabel is not null)
            _sidebarThemeLabel.Text = themeName;
        RefreshAccountStatusBrushes();
        SetStatus($"Theme „{themeName}“ aktiviert.");
    }

    private void Navigate(string section)
    {
        _activeSection = section;
        SetActiveNavigation(section);
        UpdatePageState(section);
        UpdateContextRail(section);
        SetStatus($"Bereich „{section}“ ausgewählt.");
    }

    private void UpdateContextRail(string section)
    {
        if (_contextRailTitle is not null)
            _contextRailTitle.Text = section;
        if (_contextRailKind is not null)
            _contextRailKind.Text = section switch
            {
                "Mall & Shop" => "Shopzeile / Preisvergleich",
                "Adventure" => "Encounter / Tagesplan",
                "Combat" => "Fight-Observation / Policy",
                "Konten & Transfer" => "Konto / Rollenstatus",
                _ => "Arbeitsbereich"
            };
        if (_contextRailDetails is not null)
            _contextRailDetails.Text = section switch
            {
                "Mall & Shop" => "Wähle ein Listing für Detailpreis, historische Spanne und Reprice-Vorschau.",
                "Adventure" => "Wähle ein Encounter für Status, Allowlist und bounded Preview.",
                "Combat" => "Wähle eine legale Action für manuelle oder AutoCombat-Preview.",
                _ => "Wähle ein Objekt, um sichere Kontextaktionen zu sehen."
            };
    }

    private void OverviewClick(object? sender, RoutedEventArgs e) => Navigate("Übersicht");
    private void PricingClick(object? sender, RoutedEventArgs e) => Navigate("Pricing Engine");
    private async void MallClick(object? sender, RoutedEventArgs e)
    {
        Navigate("Mall & Shop");
        await RunBusyOperationAsync("Mall-Aktualisierung", cancellationToken => RefreshMallAsync(false, cancellationToken));
    }
    private async void AccountsClick(object? sender, RoutedEventArgs e)
    {
        Navigate("Konten & Transfer");
        await RunBusyOperationAsync("Konten laden", LoadSavedAccountsAsync);
    }
    private async void AdventureClick(object? sender, RoutedEventArgs e)
    {
        Navigate("Adventure");
        await RunBusyOperationAsync("Adventure-Plan laden", cancellationToken => RefreshAdventureAsync(false, cancellationToken));
    }
    private void CombatClick(object? sender, RoutedEventArgs e)
    {
        Navigate("Combat");
        if (_combatPreviewSummary is not null)
            _combatPreviewSummary.Text = "Keine Observation geladen. Preview bleibt blockiert, bis Monster, Runde und legale Actions frisch gelesen wurden.";
    }
    private void SettingsClick(object? sender, RoutedEventArgs e) => Navigate("Einstellungen");
    private void ContextPreviewClick(object? sender, RoutedEventArgs e)
    {
        SetStatus($"Kontextvorschau für „{_activeSection}“ geöffnet. Es wurde keine KoL-Aktion gesendet.");
        if (_contextRailDetails is not null)
            _contextRailDetails.Text = "Preview erzeugt: aktuelle Daten und erlaubte nächste Schritte werden angezeigt; Commit bleibt separat freizugeben.";
    }

    private void ContextRefreshClick(object? sender, RoutedEventArgs e)
    {
        SetStatus($"Kontext „{_activeSection}“ markiert. Aktualisierung läuft über den Bereichs-Refresh.");
        if (_contextRailDetails is not null)
            _contextRailDetails.Text = "Der Bereichs-Refresh lädt den gemeinsamen Cache. Das Kontextpanel bleibt read-only.";
    }
    private async void RefreshClick(object? sender, RoutedEventArgs e)
    {
        switch (_activeSection)
        {
            case "Mall & Shop":
                await RunBusyOperationAsync("Mall-Aktualisierung", cancellationToken => RefreshMallAsync(true, cancellationToken));
                break;
            case "Konten & Transfer":
                await RunBusyOperationAsync("Konten laden", LoadSavedAccountsAsync);
                break;
            case "Adventure":
                await RunBusyOperationAsync("Adventure-Plan laden", cancellationToken => RefreshAdventureAsync(true, cancellationToken));
                break;
            default:
                await RunBusyOperationAsync("Aktualisierung", RefreshCurrentPageStateAsync);
                break;
        }
    }

    private async void RefreshMallClick(object? sender, RoutedEventArgs e)
        => await RunBusyOperationAsync("Mall-Aktualisierung", cancellationToken => RefreshMallAsync(true, cancellationToken));

    private async void RefreshAdventureClick(object? sender, RoutedEventArgs e)
        => await RunBusyOperationAsync("Adventure-Plan laden", cancellationToken => RefreshAdventureAsync(true, cancellationToken));

    private void AccountRoleSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _selectedRole = ReadSelectedRole();
        if (_mallRoleLabel is not null)
            _mallRoleLabel.Text = $"Konto: {_selectedRole}";

        if (_activeSection == "Mall & Shop")
        {
            if (_mallSummary is not null)
                _mallSummary.Text = $"Konto „{_selectedRole}“ ausgewählt. Mall-Daten neu laden.";
            if (_mallRows is not null)
                _mallRows.Text = string.Empty;
        }

        SetStatus($"Konto „{_selectedRole}“ ausgewählt. Read-only-Abfragen verwenden dieses Konto.");
    }

    private async Task RefreshCurrentPageStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Keep the busy/cancel affordance visible for at least one dispatcher
        // turn even when a page has no query service yet.
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        UpdatePageStateStatus(_activeSection, "Keine externe Datenquelle für diesen Bereich geladen.");
        SetStatus(_activeSection == "Übersicht"
            ? "Übersicht aktualisiert. Read-only-Kennzahlen warten auf einen verbundenen Query-Service."
            : $"{_activeSection}: Noch keine Daten geladen.");
    }

    private async Task RunBusyOperationAsync(
        string operation,
        Func<CancellationToken, Task> action)
    {
        if (_refreshCancellation is not null)
        {
            SetStatus("Eine Aktualisierung läuft bereits. Abbrechen oder warten, bevor eine neue gestartet wird.");
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _refreshCancellation = cancellation;
        SetBusy(true, $"{operation} läuft …");
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
            _globalRefreshButton.Content = busy ? "⟳  Aktualisierung läuft …" : "⟳  Daten aktualisieren";
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
                _mallSummary.Text = $"Bitte zuerst {role} über Konten anmelden.";
                _mallRows.Text = string.Empty;
                UpdatePageStateStatus("Mall & Shop", $"Kein gültiger {role}-Login vorhanden.");
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
            SetStatus("Mall- und Shopdaten aktualisiert. Preisänderungen bleiben deaktiviert.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _mallSummary.Text = "Mall-Aktualisierung fehlgeschlagen.";
            _mallRows.Text = exception.Message;
            UpdatePageStateStatus("Mall & Shop", "Abruf fehlgeschlagen; vorhandene Daten bleiben unverändert.");
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
            _adventurePlanSummary.Text = $"Bitte zuerst {role} über Konten anmelden.";
            _adventurePlanAdvisories.Text = "Kein Session-Snapshot verfügbar.";
            UpdatePageStateStatus("Adventure", $"Kein gültiger {role}-Login vorhanden.");
            return;
        }

        using var http = KoLLoginClient.CreateAuthenticatedClient(
            session.Credentials,
            new Uri("https://www.kingdomofloathing.com/"));
        var account = new KoLAccountReadClient(http);
        var status = await account.FetchStatusAsync(forceRefresh, cancellationToken);
        var inventory = await account.FetchInventoryAsync(forceRefresh, cancellationToken);
        var knucklebones = inventory.Items
            .Where(item => item.ItemId.Contains("knucklebone", StringComparison.OrdinalIgnoreCase)
                || item.Name.Contains("knucklebone", StringComparison.OrdinalIgnoreCase))
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
        _adventurePlanSummary.Text = $"{plan.Status}: {plan.Steps.Count} Schritt(e), prognostiziert {plan.ProjectedKnucklebones:N1} Knucklebones.";
        _adventurePlanAdvisories.Text = plan.Advisories.Count == 0
            ? "Keine Warnungen. Jede Ausführung bleibt Approval-/Verify-gebunden."
            : string.Join(Environment.NewLine, plan.Advisories);
        UpdatePageStateStatus("Adventure", $"Status und Inventar von {role} geladen; Preview only.");
        SetStatus("Adventure-Plan aktualisiert. Keine Adventure- oder Kampfaktion wurde gesendet.");
    }

    private async Task LoadSavedAccountsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _accounts.InitializeAsync(cancellationToken);
            await LoadSavedAccountAsync(AccountRole.Main, cancellationToken);
            await LoadSavedAccountAsync(AccountRole.Mallbot, cancellationToken);
            UpdatePageStateStatus("Konten & Transfer", "Gespeicherte Rollen und Sessions geprüft.");
            SetStatus("Konten geladen. Passwörter bleiben geschützt und werden nicht angezeigt.");
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
        status.Text = "Gespeichert (Session noch nicht geprüft)";
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

            status.Text = "Login läuft …";
            status.Foreground = ThemeBrush("WarningBrush", Brushes.DarkGoldenrod);
            var login = await _loginClient.LoginAsync(username, password, rememberPassword: rememberBox.IsChecked == true);
            await _accounts.SetCredentialsAsync(role, login.Credentials);
            status.Text = "Verbunden und verschlüsselt gespeichert";
            status.Foreground = ThemeBrush("SuccessBrush", Brushes.LightGreen);
            SetStatus($"{role} ist bei KoL angemeldet. Read-only-Daten können jetzt geladen werden.");
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
        var isOverview = string.Equals(section, "Übersicht", StringComparison.Ordinal);
        var isAccounts = string.Equals(section, "Konten & Transfer", StringComparison.Ordinal);
        var isMall = string.Equals(section, "Mall & Shop", StringComparison.Ordinal);
        var isAdventure = string.Equals(section, "Adventure", StringComparison.Ordinal);
        var isCombat = string.Equals(section, "Combat", StringComparison.Ordinal);
        var (title, description) = DescribeSection(section);

        if (_overviewPanel is not null)
            _overviewPanel.IsVisible = isOverview;
        if (_accountsPanel is not null)
            _accountsPanel.IsVisible = isAccounts;
        if (_mallPanel is not null)
            _mallPanel.IsVisible = isMall;
        if (_adventurePanel is not null)
            _adventurePanel.IsVisible = isAdventure;
        if (_combatPanel is not null)
            _combatPanel.IsVisible = isCombat;
        if (_pageStatePanel is not null)
            _pageStatePanel.IsVisible = !isOverview && !isAccounts && !isMall && !isAdventure && !isCombat;
        if (_shellPageTitle is not null)
            _shellPageTitle.Text = isOverview ? "Guten Morgen" : title;
        if (_shellPageDescription is not null)
            _shellPageDescription.Text = isOverview
                ? "Dein Arbitrage-Überblick für heute."
                : description;
        if (_pageStateTitle is not null)
            _pageStateTitle.Text = title;
        if (_pageStateDescription is not null)
            _pageStateDescription.Text = description;
        if (isMall && _mallRoleLabel is not null)
            _mallRoleLabel.Text = $"Konto: {_selectedRole}";
        if (_pageStateStatus is not null && !isOverview)
            _pageStateStatus.Text = isAccounts
                ? "Noch keine Kontenprüfung ausgeführt."
                : isMall
                    ? $"Noch keine Mall-Daten für {_selectedRole} geladen."
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
            "Pricing Engine" => ("Pricing Engine", "Preisquellen, Referenzen und Kaufkandidaten werden hier als read-only Vorschau geprüft."),
            "Mall & Shop" => ("Mall & Shop", "Eigene Listings und Marktpreise werden für das ausgewählte Konto read-only geladen."),
            "Konten & Transfer" => ("Konten & Transfer", "Main- und Mallbot-Sessions werden getrennt verwaltet; Transfers bleiben eine Vorschau."),
            "Adventure" => ("Adventure", "Tagesziele und Farm-vs-Buy-Pläne erscheinen hier, sobald ein Query-Service verbunden ist."),
            "Combat" => ("Combat", "Manuelle und deklarative AutoCombat-Entscheidungen bleiben an frische Fight-Observations und Approval gebunden."),
            "Einstellungen" => ("Einstellungen", "Shell-, Cache- und Sicherheitsoptionen werden hier verwaltet."),
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

    private void SetActiveNavigation(string section)
    {
        var buttons = new[]
        {
            this.FindControl<Button>("NavOverview"),
            this.FindControl<Button>("NavPricing"),
            this.FindControl<Button>("NavMall"),
            this.FindControl<Button>("NavAccounts"),
            this.FindControl<Button>("NavAdventure"),
            this.FindControl<Button>("NavCombat"),
            this.FindControl<Button>("NavSettings"),
            this.FindControl<Button>("CompactNavOverview"),
            this.FindControl<Button>("CompactNavPricing"),
            this.FindControl<Button>("CompactNavMall"),
            this.FindControl<Button>("CompactNavAccounts"),
            this.FindControl<Button>("CompactNavAdventure"),
            this.FindControl<Button>("CompactNavCombat"),
            this.FindControl<Button>("CompactNavSettings")
        };

        foreach (var button in buttons)
        {
            if (button is null)
                continue;
            if (string.Equals(button.Tag?.ToString(), section, StringComparison.Ordinal))
                button.Classes.Add("active");
            else
                button.Classes.Remove("active");
        }
    }

    private IBrush ThemeBrush(string key, IBrush fallback)
        => this.FindResource(key) as IBrush ?? fallback;

    private void RefreshAccountStatusBrushes()
    {
        var statuses = new[]
        {
            this.FindControl<TextBlock>("MainAccountStatus"),
            this.FindControl<TextBlock>("MallbotAccountStatus")
        };
        foreach (var status in statuses)
        {
            if (status is null)
                continue;
            var text = status.Text ?? string.Empty;
            var key = text.Contains("fehl", StringComparison.OrdinalIgnoreCase)
                || text.Contains("erforderlich", StringComparison.OrdinalIgnoreCase)
                || text.Contains("fehlgeschlagen", StringComparison.OrdinalIgnoreCase)
                ? "ErrorBrush"
                : text.Contains("Verbunden", StringComparison.OrdinalIgnoreCase)
                    ? "SuccessBrush"
                    : "WarningBrush";
            status.Foreground = ThemeBrush(key, Brushes.Gray);
        }
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
