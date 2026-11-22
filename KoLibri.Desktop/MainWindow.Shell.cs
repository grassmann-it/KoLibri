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

    private void InitializeShell()
    {
        if (this.FindControl<ListBox>("SessionLogList") is { } list)
        {
            list.ItemsSource = _log.Entries;
            _log.Entries.CollectionChanged += (_, _) =>
            {
                if (_log.Entries.Count > 0) list.ScrollIntoView(_log.Entries[^1]);
            };
        }
        _log.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SessionLogViewModel.Summary)
                && this.FindControl<Expander>("SessionLogExpander") is { } expander)
                expander.Header = _log.Summary;
        };

        var model = new SettingsViewModel(_settingsStore);
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
        if (_arbitrageExpander is not null) _arbitrageExpander.IsVisible = _settings.OptionalTools;
        var variant = _settings.Theme switch
        {
            "Dark" => ThemeVariant.Dark,
            "System" => ThemeVariant.Default,
            _ => ThemeVariant.Light
        };
        if (Application.Current is not null) Application.Current.RequestedThemeVariant = variant;
        _isOpsDark = variant == ThemeVariant.Dark;
        if (_themeToggleButton is not null) _themeToggleButton.Content = _isOpsDark ? "☀" : "☾";
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

    /// <summary>Without a session the sign-in is the first thing the user sees.</summary>
    private async Task OfferSignInIfNeededAsync()
    {
        if (_signInOffered) return;
        _signInOffered = true;
        try
        {
            await _accounts.InitializeAsync(CancellationToken.None);
            var session = await _accounts.GetSessionAsync(_selectedRole);
            if (session is not null && !string.IsNullOrWhiteSpace(session.Credentials.CookieHeader)) return;
            await OpenSectionAsync("Settings");
            if (this.FindControl<Expander>("AccountsExpander") is { } accounts) accounts.IsExpanded = true;
            SetStatus($"Sign in as {_selectedRole} to load the charpane, adventures and mall.");
        }
        catch (Exception exception)
        {
            _log.Error($"Could not check the saved session: {exception.Message}");
        }
    }

    private void InventoryViewClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string view }) return;
        if (this.FindControl<StackPanel>("InventoryItemsView") is { } items) items.IsVisible = view == "Items";
        if (this.FindControl<Border>("InventoryGearView") is { } gear) gear.IsVisible = view == "Gear";
        if (this.FindControl<Border>("InventorySkillsView") is { } skills) skills.IsVisible = view == "Skills";
        foreach (var name in new[] { "InvViewItems", "InvViewGear", "InvViewSkills" })
            if (this.FindControl<Button>(name) is { } button)
                button.Classes.Set("active", Equals(button.Tag, view));
    }

    private async void SessionLogCopyClick(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard) await clipboard.SetTextAsync(_log.ToText());
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
