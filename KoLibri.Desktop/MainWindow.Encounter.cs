using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;


namespace KoLibri.Desktop;

public partial class MainWindow
{
    private void WireEncounterBrowser(
        Func<Action<string>, EncounterBrowserPane>? createEncounterPane, string? tokenDirectory)
    {
        var catalogDir = Path.Combine(
            AppContext.BaseDirectory,
            "external", "kolmafia-data");
        if (!Directory.Exists(catalogDir))
            return;

        _encounterBrowser = createEncounterPane?.Invoke(UpdateEncounterStatus)
            ?? new EncounterBrowserPane(_live.Accounts, UpdateEncounterStatus);
        try
        {
            _encounterBrowser.Initialise(catalogDir, tokenDirectory);
            _encounterBrowser.SelectAccount(_selectedRole);
        }
        catch (Exception exception)
        {
            UpdateEncounterStatus($"Catalog could not be loaded: {exception.Message}");
            return;
        }

        if (_encounterLocationsGrid is not null && _encounterBrowser.Browser is { } catalogBrowser)
        {
            _encounterMonstersGrid!.ItemsSource = catalogBrowser.Monsters;
            _encounterActionsGrid!.ItemsSource = catalogBrowser.Actions;
            // The picker holds every catalog location (the browser's own filter stays empty).
            _locationPicker = new LocationPickerViewModel(catalogBrowser.Locations.ToList(),
                _settings.FavoriteLocations, _settings.RecentLocations);
            _locationPicker.ListsChanged += (_, _) => SaveLocationPickerLists();
            _locationPicker.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(LocationPickerViewModel.SelectedLocation)) return;
                if (_locationPicker.SelectedLocation is { } row && !ReferenceEquals(catalogBrowser.SelectedLocation, row))
                {
                    catalogBrowser.SelectedLocation = row;
                    RefreshEncounterStepLog();
                }
            };
            _adventuresView?.AttachLocationPicker(_locationPicker);
            if (_adventuresView is not null)
                // Enter in the picker: the location is set; go on to the run buttons.
                _adventuresView.LocationChosen = _ => this.FindControl<Button>("EncounterBatchStart")?.Focus();
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
                if (_locationPicker?.SelectedLocation is { } row)
                    await StartLocationAsync(row, 1);
                else
                    UpdateEncounterStatus("Select a location in the list first.");
                RefreshEncounterStepLog();
            };

        if (this.FindControl<Button>("EncounterBatchStart") is { } runTurns)
            runTurns.Click += async (_, _) =>
            {
                if (_locationPicker?.SelectedLocation is not { } row)
                {
                    UpdateEncounterStatus("Select a location in the list first.");
                    return;
                }
                await StartLocationAsync(row, int.MaxValue);
                RefreshEncounterStepLog();
            };

        if (this.FindControl<Button>("EncounterBatchContinue") is { } continueTurns)
            continueTurns.Click += async (_, _) =>
            {
                await _encounterBrowser.RunJobAsync(null, new AdventureJob(
                    0, null, _autoCombat.MaxRounds, _autoCombat.AbortHpPercent));
                RefreshEncounterStepLog();
            };

        if (_encounterSubmitButton is not null)
            _encounterSubmitButton.Click += async (_, _) =>
            {
                if (_encounterActionsGrid?.SelectedItem is EncounterBrowserActionRow row)
                    await _encounterBrowser.SubmitStepAsync(row);
                else
                    UpdateEncounterStatus("Pick an action in the Actions list first.");
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

        WireDecisionPanel();
        if (_adventuresView is not null)
        {
            _adventuresView.AdventureHere = (target, turns) => _ = AdventureAtAsync(target, turns);
            _adventuresView.CurrentJob = () => AdventureJob.TryCreate(
                this.FindControl<NumericUpDown>("EncounterBatchTurns")?.Value,
                this.FindControl<TextBox>("EncounterBatchItem")?.Text, 0, 0, out _);
        }
    }

    /// <summary>The job the form currently describes (turns, item stop, combat limits); null with a status message when invalid.</summary>
    private AdventureJob? ReadAdventureJob()
    {
        var job = AdventureJob.TryCreate(
            this.FindControl<NumericUpDown>("EncounterBatchTurns")?.Value,
            this.FindControl<TextBox>("EncounterBatchItem")?.Text,
            _autoCombat.MaxRounds, _autoCombat.AbortHpPercent, out var error);
        if (job is null) UpdateEncounterStatus(error ?? "The adventure settings are not valid.");
        if (job is null) return null;
        if (!_autoCombat.TryBuildFarmOptions(out var farm, out var farmError))
        {
            UpdateEncounterStatus($"Auto-combat farm settings (Settings → Combat): {farmError}");
            return null;
        }
        return job with { CombatRules = _autoCombat.BuildBatchPolicy(), Farm = farm };
    }

    /// <summary>
    /// Starts one adventure (turns = 1, "Adventure here") or the form's job at a location. The
    /// location menu and the Run turns button both end up here, so they run the same job.
    /// </summary>
    private async Task StartLocationAsync(EncounterBrowserLocationRow row, int turns)
    {
        if (_encounterBrowser is null) { UpdateEncounterStatus("The adventure catalog is not loaded."); return; }
        if (_encounterBrowser.Session is { } session) session.AccessObserved = ObserveLocationAccess;
        if (turns <= 1)
        {
            _locationPicker?.MarkUsed(row.LocationId);
            _encounterBrowser.ApplyApprovalToken(Guid.NewGuid().ToString("N"));
            await _encounterBrowser.StartAdventureAsync(row);
            return;
        }
        if (ReadAdventureJob() is not { } job) return;
        _locationPicker?.MarkUsed(row.LocationId);
        _encounterBrowser.ApplyApprovalToken(Guid.NewGuid().ToString("N"));
        await _encounterBrowser.RunJobAsync(row, job);
    }

    private async Task AdventureAtAsync(ContextMenus.LocationMenuTarget target, int turns)
    {
        var row = _locationPicker?.Find(target.LocationId);
        if (row is null)
        {
            UpdateEncounterStatus($"{target.Name} is not in the location catalog.");
            return;
        }
        _locationPicker!.Select(row.LocationId);
        await StartLocationAsync(row, turns);
        RefreshEncounterStepLog();
    }

    /// <summary>An access result a start already read: the picker grays the location out when it is locked.</summary>
    private void ObserveLocationAccess(int snarfblat, bool accessible, string? reason)
        => Dispatcher.UIThread.Post(() => _locationPicker?.SetAccess(snarfblat, accessible, reason));

    /// <summary>Saves the picker's favorites and recent list into the desktop settings (fresh load, so other edits survive).</summary>
    private void SaveLocationPickerLists()
    {
        if (_locationPicker is null) return;
        try
        {
            var fresh = _settingsStore.Load();
            fresh.FavoriteLocations = _locationPicker.Favorites.ToList();
            fresh.RecentLocations = _locationPicker.Recent.ToList();
            _settingsStore.Save(fresh);
            _settings.FavoriteLocations = fresh.FavoriteLocations;
            _settings.RecentLocations = fresh.RecentLocations;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            UpdateEncounterStatus($"Favorites could not be saved: {e.Message}");
        }
    }

    /// <summary>Screenshot QA only: shows a choice in the decision panel without any request.</summary>
    public void ShowDecisionForQa(NeedsDecisionInfo info)
    {
        _encounterBrowser?.Session?.Decisions.Show(info);
        SyncDecisionPanel();
    }

    private AdventureDecisionController? _boundDecisions;

    private void WireDecisionPanel()
    {
        if (this.FindControl<ItemsControl>("DecisionOptions") is { } options)
            options.AddHandler(Button.ClickEvent, async (_, e) =>
            {
                if (e.Source is Button { Tag: int number } && _encounterBrowser is not null)
                {
                    await _encounterBrowser.SubmitDecisionAsync(number);
                    RefreshEncounterStepLog();
                    SyncDecisionPanel();
                }
            });
        if (this.FindControl<Button>("DecisionApplyPolicy") is { } apply)
            apply.Click += async (_, _) =>
            {
                if (_encounterBrowser is null) return;
                await _encounterBrowser.ApplyPolicyAsync();
                SyncDecisionPanel();
            };
        if (this.FindControl<Button>("DecisionRefresh") is { } refresh)
            refresh.Click += async (_, _) =>
            {
                if (_encounterBrowser is null) return;
                await _encounterBrowser.RefreshDecisionAsync();
                SyncDecisionPanel();
            };
    }

    /// <summary>Mirrors the session's open choice into the panel; re-binds when the account changed.</summary>
    private void SyncDecisionPanel()
    {
        if (this.FindControl<Border>("DecisionPanel") is not { } panel) return;
        var decisions = _encounterBrowser?.Session?.Decisions;
        if (!ReferenceEquals(decisions, _boundDecisions))
        {
            if (_boundDecisions is not null) _boundDecisions.PropertyChanged -= OnDecisionChanged;
            _boundDecisions = decisions;
            if (decisions is not null) decisions.PropertyChanged += OnDecisionChanged;
            if (this.FindControl<ItemsControl>("DecisionOptions") is { } list) list.ItemsSource = decisions?.Options;
        }
        panel.IsVisible = decisions?.IsVisible == true;
        if (decisions is null) return;
        if (this.FindControl<TextBlock>("DecisionTitle") is { } title)
            title.Text = decisions.IsFightOpen ? "Open fight"
                : decisions.ChoiceId is null ? "Open choice"
                : $"Choice {decisions.ChoiceId}{(string.IsNullOrWhiteSpace(decisions.Title) ? "" : " · " + decisions.Title)}";
        if (this.FindControl<TextBlock>("DecisionMessage") is { } message) message.Text = decisions.Message;
        if (this.FindControl<Button>("DecisionApplyPolicy") is { } apply) apply.IsEnabled = decisions.ChoiceId is not null;
    }

    private void OnDecisionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(SyncDecisionPanel);

    private void UpdateEncounterStatus(string message)
    {
        SyncDecisionPanel();
        if (_encounterStatusText is null) return;
        _encounterStatusText.Text = $"{DateTime.Now:HH:mm:ss} · {message}";
        _log.Adventure(message);
    }

    private void RefreshEncounterStepLog()
    {
        if (_encounterStepLogText is null || _encounterBrowser?.Session is not { } session) return;
        foreach (var entry in session.StepLog.OrderBy(entry => entry.At))
        {
            var line = $"{entry.Kind} {entry.Status} {entry.StopReason}";
            if (_loggedSteps.Add($"{entry.At:O}|{line}")) _log.Adventure(line);
        }
        var lines = session.StepLog.Take(10).Select(entry =>
            $"{entry.At:HH:mm:ss} {entry.Kind} {entry.Status} {entry.StopReason}");
        _encounterStepLogText.Text = string.Join("\n",
            new[] { session.LastEncounterSummary }.OfType<string>().Concat(lines));
    }
}
