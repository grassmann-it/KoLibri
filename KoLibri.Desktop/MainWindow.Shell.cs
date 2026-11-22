using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using KoLibri.Core.Basis;
using KoLibri.Core.Markt;

namespace KoLibri.Desktop;

/// <summary>
/// Shell wiring: session log, settings page, optional tools, inventory sub-views and the
/// mall purchase hook. The four main tabs are Adventures, Inventory, Mall and Daily; chat,
/// clan and KoLmail live behind "Chat &amp; Mail", everything configurable behind the gear.
/// </summary>
public partial class MainWindow
{
    private readonly HashSet<string> _loggedSteps = new();
    private SettingsViewModel? _settingsModel;

    private void InitializeShell()
    {
        if (this.FindControl<ListBox>("SessionLogList") is { } list)
        {
            list.ItemsSource = _log.Visible;
            _log.Visible.CollectionChanged += (_, _) =>
            {
                if (_log.Visible.Count > 0) list.ScrollIntoView(_log.Visible[^1]);
            };
        }
        if (this.FindControl<ComboBox>("SessionLogLevelBox") is { } levelBox)
        {
            levelBox.ItemsSource = _log.LevelChoices;
            levelBox.SelectedIndex = 0;
            levelBox.SelectionChanged += (_, _) => { if (levelBox.SelectedItem is string c) _log.LevelChoice = c; };
        }
        if (this.FindControl<ComboBox>("SessionLogRoleBox") is { } roleBox)
        {
            roleBox.ItemsSource = _log.RoleChoices;
            roleBox.SelectedIndex = 0;
            roleBox.SelectionChanged += (_, _) => { if (roleBox.SelectedItem is string c) _log.RoleFilter = c; };
        }
        _log.LoadToday();
        _log.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SessionLogViewModel.Summary)
                && this.FindControl<Expander>("SessionLogExpander") is { } expander)
                expander.Header = _log.Summary;
            if (args.PropertyName == nameof(SessionLogViewModel.FilterSummary)
                && this.FindControl<TextBlock>("SessionLogFilterText") is { } filterText)
                filterText.Text = _log.FilterSummary;
        };

        var model = new SettingsViewModel(_settingsStore, itemLookup: LookupCatalogItems,
            classifier: _catalog is null ? null : new KoLibri.Core.Spiel.ItemClassifier(_catalog));
        _settingsModel = model;
        model.PropertyChanged += (_, args) =>
        {
            // The settings model reports a saved state through Status.
            if (args.PropertyName == nameof(SettingsViewModel.Status)
                && model.Status.StartsWith("Saved", StringComparison.Ordinal))
            {
                ApplySettings(reload: true);
                _log.Add(SessionLogKind.Info, "Settings saved.");
            }
        };
        if (this.FindControl<ContentControl>("SettingsHost") is { } host)
            host.Content = new SettingsView(model);

        if (_arbitrageExpander is not null)
            _arbitrageExpander.Expanded += async (_, _) =>
            {
                if (_arbitrageLoaded) return;
                await RunBusyOperationAsync("Loading Arbitrage", token => LoadArbitrageAsync(false, token));
            };
        ApplySettings(reload: false);
    }

    /// <summary>Applies what can change while the app runs. Request intervals and the pricegun policy apply at startup.</summary>
    private void ApplySettings(bool reload)
    {
        if (reload) _settings = _settingsStore.Load();
        if (reload) _daily?.RebuildSteps();
        if (_arbitrageExpander is not null) _arbitrageExpander.IsVisible = _settings.OptionalTools;
        var variant = _settings.Theme switch
        {
            "Dark" => ThemeVariant.Dark,
            "System" => ThemeVariant.Default,
            _ => ThemeVariant.Light
        };
        if (Application.Current is not null) Application.Current.RequestedThemeVariant = variant;
        _isOpsDark = variant == ThemeVariant.Dark;
        UpdateThemeButton();
        if (this.FindControl<NumericUpDown>("EncounterBatchTurns") is { } turns)
        {
            turns.Maximum = Math.Max(turns.Maximum, (decimal)_settings.Get("turnBudget"));
            turns.Value = (decimal)_settings.Get("turnBudget");
        }
        if (_combatCatalogLoaded) ApplyCombatDefaults();
    }

    private void ApplyCombatDefaults()
    {
        _autoCombat.AbortHpPercent = (int)_settings.Get("hpStopPercent");
        _autoCombat.MaxRounds = (int)_settings.Get("maxCombatRounds");
        if (_combatAbortHp is not null) _combatAbortHp.Value = _autoCombat.AbortHpPercent;
        if (_combatMaxRounds is not null) _combatMaxRounds.Value = _autoCombat.MaxRounds;
    }

    private async Task LoadSettingsSectionAsync(CancellationToken cancellationToken)
    {
        await LoadSavedAccountsAsync(cancellationToken);
        LoadAgents();
        if (!_combatCatalogLoaded)
        {
            await LoadCombatCatalogAsync(cancellationToken);
            _combatCatalogLoaded = true;
            ApplyCombatDefaults();
            if (_combatPreviewText is not null) _combatPreviewText.Text = _autoCombat.PreviewText;
        }
    }

    private async Task LoadArbitrageAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        await _arbitrage.LoadAsync(forceRefresh, cancellationToken);
        _arbitrageLoaded = true;
        ShowLoadResult("Arbitrage", _arbitrage);
    }

    /// <summary>
    /// Auto-login at start: the selected role is renewed first (the window waits, with a status line), the other role
    /// follows in the background. One attempt per role, one request at a time, no retry; the Sign in button stays.
    /// </summary>
    private async Task StartupLoginAsync()
    {
        try
        {
            var auto = _settings.ReloginAutomatically;
            var other = _selectedRole == AccountRole.Main ? AccountRole.Mallbot : AccountRole.Main;
            SetStatus($"Checking the saved {_selectedRole} session…");
            var first = await StartupLogin.RunAsync(_accounts, _selectedRole, auto);
            ReportStartupLogin(first);
            _ = Task.Run(async () =>
            {
                var second = await StartupLogin.RunAsync(_accounts, other, auto);
                Dispatcher.UIThread.Post(() =>
                {
                    if (second.Outcome is StartupLoginOutcome.LoggedIn or StartupLoginOutcome.Failed) _log.Add(SessionLogKind.Info, second.Message);
                });
            });
        }
        catch (Exception exception)
        {
            _log.Error($"Could not check the saved session: {exception.GetType().Name}");
        }
    }

    private void ReportStartupLogin(StartupLoginResult result)
    {
        switch (result.Outcome)
        {
            case StartupLoginOutcome.LoggedIn:
                _log.Add(SessionLogKind.Info, result.Message);
                SetStatus(result.Message);
                break;
            case StartupLoginOutcome.Failed or StartupLoginOutcome.AutoLoginOff:
                _log.Add(SessionLogKind.Info, result.Message);
                SetStatus(result.Message);
                break;
        }
    }

    /// <summary>Without a session the sign-in is the first thing the user sees.</summary>
    /// <summary>Neither a default nor a last-used role: if exactly one role has an account, open that one.</summary>
    private async Task ChooseSingleConfiguredRoleAsync()
    {
        if (StartupRole.IsSettled(_settings.DefaultAccount, StartupRole.ReadLast(_settingsStore))) return;
        try
        {
            await _accounts.InitializeAsync(CancellationToken.None);
            var configured = new List<AccountRole>();
            foreach (var role in new[] { AccountRole.Main, AccountRole.Mallbot })
                if (await _accounts.GetSessionAsync(role, includeExpired: true) is not null) configured.Add(role);
            var chosen = StartupRole.Resolve(null, null, configured);
            if (chosen == _selectedRole || _accountRoleSelector is null) return;
            _selectedRole = chosen;                       // set first: the selection handler then sees no change
            _accountRoleSelector.SelectedIndex = chosen == AccountRole.Main ? 0 : 1;
            if (_skillsEffectsModel is not null) _skillsEffectsModel.Role = chosen.ToString();
            ResetAccountViews();
            UpdatePageState(_activeSection);
        }
        catch (Exception exception)
        {
            _log.Error($"Could not choose the start account: {exception.GetType().Name}");
        }
    }

    /// <summary>The page the user came from when Sign in opened Settings; null when none.</summary>
    private string? _signInReturnSection;

    /// <summary>After a successful sign-in of the active role, goes back to the page the sign-in started from.</summary>
    private async Task ReturnAfterSignInAsync(AccountRole role)
    {
        if (_signInReturnSection is not { } back) return;
        if (role != _selectedRole) return;
        _signInReturnSection = null;
        await OpenSectionAsync(back);
    }

    private async Task OfferSignInIfNeededAsync()
    {
        if (_signInOffered) return;
        _signInOffered = true;
        try
        {
            await _accounts.InitializeAsync(CancellationToken.None);
            var session = await _accounts.GetSessionAsync(_selectedRole);
            if (session is not null && !string.IsNullOrWhiteSpace(session.Credentials.CookieHeader)) return;
            _signInReturnSection = _activeSection == "Settings" ? "Adventure" : _activeSection;
            await OpenSectionAsync("Settings");
            if (this.FindControl<Expander>("AccountsExpander") is { } accounts) accounts.IsExpanded = true;
            FocusSignInField(_selectedRole);
            SetStatus($"Sign in as {_selectedRole} to load the charpane, adventures and mall.");
        }
        catch (Exception exception)
        {
            _log.Error($"Could not check the saved session: {exception.Message}");
        }
    }

    /// <summary>Always-visible way to sign in: opens Settings with the Accounts group expanded.</summary>
    private async void SignInClick(object? sender, RoutedEventArgs e)
    {
        if (_activeSection != "Settings") _signInReturnSection = _activeSection;
        await OpenSectionAsync("Settings");
        if (this.FindControl<Expander>("AccountsExpander") is { } accounts) accounts.IsExpanded = true;
        FocusSignInField(_selectedRole);
        SetStatus($"Sign in as {_selectedRole} (or the other account) below.");
    }

    /// <summary>Puts the cursor in the field the user has to fill: the password if the name is known, else the name.</summary>
    private void FocusSignInField(AccountRole role)
    {
        var prefix = role == AccountRole.Main ? "Main" : "Mallbot";
        Dispatcher.UIThread.Post(() =>
        {
            var user = this.FindControl<TextBox>(prefix + "Username");
            var pass = this.FindControl<TextBox>(prefix + "Password");
            var target = string.IsNullOrWhiteSpace(user?.Text) ? user : pass;
            target?.BringIntoView();
            target?.Focus();
        }, DispatcherPriority.Background);
    }

    private async void SessionLogCopyClick(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard) await clipboard.SetTextAsync(_log.ToText());
    }

    private void SessionLogSaveClick(object? sender, RoutedEventArgs e)
    {
        var path = _log.SaveSelection();
        SetStatus(path is null ? "The log could not be saved." : $"Log saved to {path}");
    }

    private void SessionLogClearClick(object? sender, RoutedEventArgs e) => _log.Clear();

    /// <summary>Search state and errors go to the session log.</summary>
    private void WatchMallSearch(MallSearchViewModel model)
    {
        model.PropertyChanged += (_, args) =>
        {
            switch (args.PropertyName)
            {
                case nameof(MallSearchViewModel.StatusText):
                    _log.Search(model.StatusText);
                    break;
                case nameof(MallSearchViewModel.SearchError) when model.HasSearchError:
                    _log.Error(model.SearchError);
                    break;
            }
        };
    }

    /// <summary>Buy on a row: preview, confirmation window, one confirmed purchase. Limits come from the settings.</summary>
    private MallPurchaseCoordinator CreatePurchaseCoordinator()
    {
        var role = _selectedRole;
        return new MallPurchaseCoordinator(
            role.ToString(),
            async cancellationToken =>
            {
                if (await _live.OpenPurchaseSessionAsync(role, cancellationToken) is not { } session) return null;
                var observer = new MallPurchaseObserver(
                    new LiveMallPurchaseReads(_live, role), role.ToString(),
                    () => (long)_settings.Get("meatPerDay"), _spendLedger);
                var transport = new HttpMallPurchaseTransport(
                    session.Http,
                    new HttpMallPurchaseTransportOptions
                    {
                        PasswordHashProvider = (_, _) => Task.FromResult(session.PasswordHash)
                    },
                    observer.BeforeAsync, observer.AfterAsync);
                var service = new DesktopMallPurchaseService(transport, DesktopMallPurchaseSettings.ToOptions(_settings));
                return (service, observer);
            },
            new WindowMallPurchasePrompt(this),
            _log,
            _spendLedger,
            message => Dispatcher.UIThread.Post(() => SetStatus(message)));
    }

    /// <summary>A skill is chosen by name or by ID.</summary>
    private bool TryReadSkillId(out int id)
    {
        id = 0;
        var text = this.FindControl<TextBox>("CharacterSkillId")?.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return false;
        if (int.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out id)) return true;
        var skill = _catalog?.Skills.FirstOrDefault(s => string.Equals(s.Name, text, StringComparison.OrdinalIgnoreCase));
        if (skill is null) return false;
        id = skill.Id;
        return true;
    }
}
