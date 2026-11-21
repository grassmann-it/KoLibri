using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;


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
            ?? new EncounterBrowserPane(_accounts, UpdateEncounterStatus);
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
