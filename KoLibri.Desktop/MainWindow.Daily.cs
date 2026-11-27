using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using KoLibri.Daily;

namespace KoLibri.Desktop;

public partial class MainWindow
{
    public void DailySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_dailyRows?.SelectedItem is not DailyBriefingRow row) return;
        if (_dailyStatusText is not null)
            _dailyStatusText.Text = $"{row.Surface}: {row.Description} — {row.Detail}";
    }

    public void DailyStepSkip(DailyStepRow row, bool skip)
    {
        var role = _selectedRole.ToString();
        // The skip lasts until the KoL rollover (like the other daily markers), not local midnight.
        var today = DailyStepBoard.KolToday();
        try
        {
            // Merge into what is on disk: the settings page may have saved since this window loaded its copy,
            // and only the skip list is changed here.
            var fresh = _settingsStore.Load();
            if (skip) fresh.SkipToday(role, row.Id, today);
            else fresh.UnskipToday(role, row.Id);
            _settingsStore.Save(fresh);
            _settings.SkippedSteps = new Dictionary<string, string>(fresh.SkippedSteps);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ReportDailyStep($"Could not save the skip: {exception.Message}", error: true);
            return;
        }
        _daily.RebuildSteps();
        ReportDailyStep(skip ? $"{row.Title}: skipped until the KoL rollover." : $"{row.Title}: back on today's list.");
    }

    public async void DailyStepRun(DailyStepRow row)
    {
        if (!row.CanRun)
        {
            ReportDailyStep($"{row.Title}: {row.DisabledReason ?? "not available."}", error: true);
            return;
        }
        switch (row.Kind)
        {
            case DailyStepKind.Adventures:
                ReportDailyStep($"{row.Title}: {row.Detail}. Opening Adventures; nothing was sent.");
                await ((IShellHost)this).NavigateToAsync("Adventure");
                return;
            case DailyStepKind.CloverCheck:
                await ((IShellHost)this).RefreshStatusAsync(true);
                ReportDailyStep($"{row.Title}: {row.Detail}");
                return;
        }

        var settings = _settings;
        var eggs = _daily.EggsOwned;
        var actions = DailyStepBoard.ActionsFor(row, settings, eggs);
        if (actions.Count == 0)
        {
            ReportDailyStep($"{row.Title}: nothing to do.", error: true);
            return;
        }
        var confirmText = DailyStepBoard.ConfirmText(row, settings, eggs);
        if (!await ConfirmDailyAsync(row.Title, confirmText)) { ReportDailyStep($"{row.Title}: cancelled, nothing was sent."); return; }

        var role = _selectedRole;
        await RunBusyOperationAsync(row.Title, async token =>
        {
            try
            {
                var outcome = await RunDailyActionsAsync(role, row, actions, token);
                ReportDailyStep($"{row.Title}: {outcome}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                ReportDailyStep(DailyStepBoard.ReadableError(row.Title, exception), error: true);
                return;
            }
            await _daily.LoadAsync(forceRefresh: true, token);
        });
    }

    private async Task<string> RunDailyActionsAsync(
        AccountRole role, DailyStepRow row, IReadOnlyList<CharacterCommand> actions, CancellationToken token)
    {
        var session = await OpenCharacterSessionAsync(role, token);
        var classifier = _catalog is null ? null : new ItemClassifier(_catalog);

        async Task<DailyStepState> ReadAsync(CancellationToken ct)
            => new(await session.Character.GetStatusAsync(refresh: true, ct), await session.Inventory.GetAsync(refresh: true, ct));

        async Task<DailyStepState> ExecuteAsync(CharacterCommand action, CancellationToken ct)
        {
            var result = action switch
            {
                ConsumeCharacterItem consume => await session.Character.ConsumeAsync(consume.ItemId, consume.Kind, consume.Quantity, ct),
                RestAtCampground => await session.Character.RestAsync(1, ct),
                _ => throw new ArgumentException($"A daily step cannot send {action.GetType().Name}.", nameof(action))
            };
            var inventory = await session.Inventory.GetAsync(refresh: true, ct);
            var status = result.After ?? await session.Character.GetStatusAsync(refresh: true, ct);
            return new DailyStepState(status, inventory)
            {
                // A partial consume is judged by the runner from the stock change; only a refusal stops it here.
                RejectionReason = result.Status == ActionStatus.Rejected ? result.Rejection ?? "KoL did not confirm it." : null,
                Unclear = result.Status == ActionStatus.Unclear,
                Confirmed = result.Status == ActionStatus.Done
            };
        }

        var result = await DailyStepRunner.RunAsync(row.Kind, actions,
            ReadAsync, ExecuteAsync, classifier,
            // The runner continues on a pool thread after the async status reads; dialogs must open on the UI thread.
            (title, body) => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                () => OverfillConfirmDialog.AskAsync(this, title, body, "Send anyway")), token);
        return result.Summary;
    }

    private void ReportDailyStep(string message, bool error = false)
    {
        if (_dailyStatusText is not null) _dailyStatusText.Text = message;
        SetStatus(message);
        if (error) _log.Error(message); else _log.Add(SessionLogKind.Info, message);
    }

    private async Task<bool> ConfirmDailyAsync(string title, string body)
    {
        var yes = new Button { Content = "Run", Classes = { "primary" } };
        var no = new Button { Content = "Cancel" };
        var dialog = new Window
        {
            Title = title, Width = 460, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20), Spacing = 14,
                Children =
                {
                    new TextBlock { Text = "This sends the following to Kingdom of Loathing:", FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
                    new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { yes, no } }
                }
            }
        };
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        return await dialog.ShowDialog<bool>(this);
    }

    public async void DailyRunClick(object? sender, RoutedEventArgs e)
    {
        var role = _selectedRole;
        await RunBusyOperationAsync("Daily routine", async cancellationToken =>
        {
            var session = await _live.SessionAsync(role, cancellationToken);
            if (session is null)
            {
                if (_dailyStatusText is not null)
                    _dailyStatusText.Text = "No session for the selected account.";
                return;
            }
            var hermitClovers = (int)(this.FindControl<NumericUpDown>("DailyHermitClovers")?.Value ?? 0);
            var topUp = this.FindControl<CheckBox>("DailyBaconTopUp")?.IsChecked == true;
            var maxBaconPrice = (long)(this.FindControl<NumericUpDown>("DailyBaconMaxPrice")?.Value ?? 1500);
            var pocketText = (this.FindControl<TextBox>("DailyCargoPockets")?.Text ?? "").Trim();
            var playChess = this.FindControl<CheckBox>("DailyPlayChess")?.IsChecked == true;
            var itemError = SaveDailyItemSettings(pocketText, playChess);
            if (itemError is not null)
            {
                ReportDailyStep(itemError + " Nothing was sent.", error: true);
                return;
            }
            _dailyNote = string.Empty;
            var result = await RunDailyRoutineAsync(session, role, cancellationToken,
                hermitClovers, topUp, maxBaconPrice);
            var text = DailyRunReport.Describe(result);
            if (_dailyStatusText is not null) _dailyStatusText.Text = text + _dailyNote;
            ReportDailyStep(text + _dailyNote, error: result is null || result.Completeness is DailyRunCompleteness.Failed);
            // The briefing shows the markers the run just set (toaster, Klaw, Cargo pocket, chess).
            if (result is not null && result.AttemptedSteps > 0) await _daily.LoadAsync(forceRefresh: true, cancellationToken);
        });
    }

    private string _dailyNote = string.Empty;

    /// <summary>
    /// Checks and saves the Cargo pocket list and the chess opt-in (merged into the settings on disk, like the skip list).
    /// Returns an error text for a list that is not pocket numbers or names a pocket that starts a fight.
    /// </summary>
    private string? SaveDailyItemSettings(string pocketText, bool playChess)
    {
        try
        {
            var pockets = CargoPockets.Parse(pocketText);
            var fight = pockets.FirstOrDefault(CargoPockets.StartsFight);
            if (fight != 0) return $"Cargo pocket {CargoPockets.Describe(fight)} starts a fight; the daily routine only picks pockets without a fight.";
        }
        catch (FormatException exception) { return "Cargo pockets: " + exception.Message; }
        if (_settings.DailyCargoPockets == pocketText && _settings.DailyPlayChess == playChess) return null;
        try
        {
            var fresh = _settingsStore.Load();
            fresh.DailyCargoPockets = pocketText;
            fresh.DailyPlayChess = playChess;
            _settingsStore.Save(fresh);
            _settings.DailyCargoPockets = pocketText;
            _settings.DailyPlayChess = playChess;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The run still uses the values on screen; only remembering them failed.
            _settings.DailyCargoPockets = pocketText;
            _settings.DailyPlayChess = playChess;
            _log.Error($"Could not save the daily item settings: {exception.Message}");
        }
        return null;
    }

    /// <summary>
    /// Runs the daily routine for the selected account with the same planning as the CLI's <c>daily run</c>
    /// (<see cref="DailyRun"/>): campfire leaf / Day Shortener, Hermit clovers, Internet Meme Shop and the owned daily
    /// items (toaster, VIP Klaw, Cargo pocket when configured, fancy chess set when opted in). The once-per-day
    /// markers are the session's (<see cref="KolibriSession.Daily"/>, keyed by the player id KoL reports).
    /// The desktop button authorizes this run.
    /// </summary>
    private async Task<global::KoLibri.Daily.DailyRunResult?> RunDailyRoutineAsync(
        KolibriSession session, AccountRole role, CancellationToken cancellationToken,
        int hermitClovers = 0, bool topUpBacon = false, long maxBaconPrice = 1500)
    {
        var live = await DailyRun.ReadAsync(session, cancellationToken);
        if (topUpBacon)
        {
            var needed = DailyInternetShopPlanner.BaconShortfall(
                live.MemeItemsDoneToday, live.Bacon, live.BaconMachines > 0, live.BaconMachineUsedToday);
            if (needed > 0)
            {
                var reserve = role == AccountRole.Mallbot ? 1_000_000L : 200_000L;
                // Same limits as the Buy button: meat per day (shared ledger), per purchase and units per purchase.
                var ledgerAccount = role.ToString();
                var budget = DailyBaconBudget.Reserve(_settings, _spendLedger, ledgerAccount,
                    needed, maxBaconPrice, live.Meat, reserve);
                if (!budget.Allowed)
                {
                    _dailyNote += " " + budget.Note;
                }
                else
                {
                    long spent = 0;
                    var unclear = true;
                    try
                    {
                        var units = DailyBaconBudget.AffordableUnits(budget);
                        var outcome = await session.Mall.QuoteAndBuyAsync(
                            new PurchaseOrder(DailyRun.Ids.Bacon, units, budget.MaxUnitPrice) { MeatReserve = reserve },
                            cancellationToken);
                        spent = outcome.MeatSpent;
                        unclear = outcome.Status == PurchaseStatus.Unclear;
                        _dailyNote += $" BACON top-up: bought {outcome.QuantityBought} of {budget.Units} for {outcome.MeatSpent} meat.";
                        if (outcome.QuantityBought > 0)
                            live = await DailyRun.ReadAsync(session, cancellationToken);
                    }
                    finally
                    {
                        // An exception or an unclear outcome keeps the reserved meat counted (the safe side).
                        DailyBaconBudget.Finish(_spendLedger, ledgerAccount, budget, spent, unclear);
                    }
                }
            }
        }
        var steps = DailyRun.Plan(live, DesktopDailyItems.Options(_settings, hermitClovers));
        return await DailyRun.ExecuteAsync(session, steps, cancellationToken);
    }

    private static KoLmafiaCatalog? TryLoadCatalog()
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
                return new MafiaDataLoader().Load(candidate);
            }
            catch
            {
                // A broken catalog must not take the window down.
            }
        }
        return null;
    }
}
