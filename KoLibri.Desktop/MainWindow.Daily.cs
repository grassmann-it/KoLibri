using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using KoLibri.Core.Basis;
using KoLibri.Core.Markt;
using KoLibri.Core.Spiel;


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
        AccountRole role, DailyStepRow row, IReadOnlyList<CharacterAction> actions, CancellationToken token)
    {
        using var session = await OpenCharacterSessionAsync(role, token);
        var classifier = _catalog is null ? null : new ItemClassifier(_catalog);
        var result = await DailyStepRunner.RunAsync(row.Kind, actions,
            session.ReadAsync, session.ExecuteAsync, classifier,
            (title, body) => OverfillConfirmDialog.AskAsync(this, title, body, "Send anyway"), token);
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
            var hermitClovers = (int)(this.FindControl<NumericUpDown>("DailyHermitClovers")?.Value ?? 0);
            var topUp = this.FindControl<CheckBox>("DailyBaconTopUp")?.IsChecked == true;
            var maxBaconPrice = (long)(this.FindControl<NumericUpDown>("DailyBaconMaxPrice")?.Value ?? 1500);
            _dailyNote = string.Empty;
            var result = await RunDailyRoutineAsync(account, token, cancellationToken,
                hermitClovers, topUp, maxBaconPrice);
            var text = DailyRunReport.Describe(result);
            if (_dailyStatusText is not null) _dailyStatusText.Text = text + _dailyNote;
            ReportDailyStep(text + _dailyNote, error: result is null || result.Completeness is KoLibri.Core.Spiel.AutoCombatDailyBridge.BridgeCompleteness.Failed);
        });
    }

    /// <summary>Runs the daily routine for the selected account through the
    /// same bridge the CLI uses.</summary>
    private string _dailyNote = string.Empty;

    private async Task<KoLibri.Core.Spiel.AutoCombatDailyBridge.BridgeExecutionResult?> RunDailyRoutineAsync(
        AccountSession account, string approvalToken, CancellationToken cancellationToken,
        int hermitClovers = 0, bool topUpBacon = false, long maxBaconPrice = 1500)
    {
        using var http = KoLLoginClient.CreateAuthenticatedClient(
            account.Credentials, new Uri("https://www.kingdomofloathing.com/"));
        var readClient = new KoLAccountReadClient(http);
        var doneStore = new KoLibri.Core.Spiel.DailyDoneStore();
        var live = await new LiveDailySnapshotBuilder(readClient, doneStore)
            .BuildAsync(new KoLibri.Core.Spiel.DailyRoutineSnapshot(), cancellationToken,
                role: account.Profile.Role);
        if (topUpBacon)
        {
            var needed = DailyInternetShopPlanner.BaconShortfall(
                live.Snapshot.AlreadyPulledItemIds, live.Snapshot.Bacon,
                live.Snapshot.BaconMachineOwned, live.Snapshot.BaconMachineUsedToday);
            if (needed > 0)
            {
                var reserve = account.Profile.Role == AccountRole.Mallbot ? 1_000_000L : 200_000L;
                // Same limits as the Buy button: meat per day (shared ledger), per purchase and units per purchase.
                var ledgerAccount = account.Profile.Role.ToString();
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
                        var outcome = await new BaconArbitrageExecutor(
                                new MallBuyClient(http, MallBuyClient.DefaultBaseUri), new KoLMallReadClient(http))
                            .TopUpBaconAsync(account.Credentials, budget.Units, budget.MeatToOffer, budget.MaxUnitPrice,
                                reserve, cancellationToken: cancellationToken);
                        spent = outcome.Spent;
                        unclear = outcome.Unclear;
                        _dailyNote += $" BACON top-up: bought {outcome.Quantity} of {budget.Units} for {outcome.Spent} meat.";
                        if (outcome.Quantity > 0)
                            live = await new LiveDailySnapshotBuilder(readClient, doneStore)
                                .BuildAsync(new KoLibri.Core.Spiel.DailyRoutineSnapshot(), cancellationToken,
                                    role: account.Profile.Role);
                    }
                    finally
                    {
                        // An exception or an unclear outcome keeps the reserved meat counted (the safe side).
                        DailyBaconBudget.Finish(_spendLedger, ledgerAccount, budget, spent, unclear);
                    }
                }
            }
        }
        var snapshot = live.Snapshot with { HermitCloversWanted = hermitClovers };
        var bridge = new KoLibri.Core.Spiel.AutoCombatDailyBridge();
        var plan = bridge.Plan(live.State, snapshot);
        // The desktop button authorizes this run. The token correlates its requests.
        var approval = new DelegateAdventureApprovalHook((_, _) =>
            ValueTask.FromResult(AdventureApprovalDecision.Allow(approvalToken)));
        var verification = new DelegateAdventureVerificationHook((context, _) =>
            ValueTask.FromResult(context.Response.Accepted != false
                ? AdventureVerificationResult.Success()
                : AdventureVerificationResult.Failed("The live response was rejected by KoL.")));
        await using var session = KoLAdventureSessionFactory.Create(account,
            KoLLoginClient.CreateAuthenticatedClient(account.Credentials, new Uri("https://www.kingdomofloathing.com/")),
            approval, verification);
        var dispatcher = new KoLibri.Core.Spiel.FreePullDispatcher(
            http, KoLibri.Core.Spiel.FreePullDispatcher.DefaultBaseUri);
        using var audit = new KoLibri.Core.Spiel.BridgeAuditLog(
            KoLibri.Core.Spiel.BridgeAuditLog.DefaultPath());
        return await bridge.ExecuteAsync(
            plan.Steps, session.Gateway,
            freePullDispatcher: dispatcher,
            freePullCredentials: account.Credentials,
            audit: audit,
            accountId: account.Profile.Id,
            cancellationToken: cancellationToken,
            doneStore: doneStore,
            doneAccountKey: live.DoneAccountKey);
    }

    private static KoLibri.Core.Spiel.KoLmafiaCatalog? TryLoadCatalog()
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
                return new KoLibri.Core.Spiel.MafiaDataLoader().Load(candidate);
            }
            catch
            {
                // A broken catalog must not take the window down.
            }
        }
        return null;
    }
}
