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
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (skip) _settings.SkipToday(role, row.Id, today);
        else _settings.UnskipToday(role, row.Id);
        try { _settingsStore.Save(_settings); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ReportDailyStep($"Could not save the skip: {exception.Message}", error: true);
        }
        _daily.RebuildSteps();
        ReportDailyStep(skip ? $"{row.Title}: skipped for today." : $"{row.Title}: back on today's list.");
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
        using var session = await LibKolibri.CharacterSession.OpenAsync(role, _accounts, token);
        var done = 0;
        var problems = new List<string>();
        var current = await session.ReadAsync(token);
        foreach (var action in actions)
        {
            token.ThrowIfCancellationRequested();
            var after = await session.ExecuteAsync(action, token);
            if (after.RejectionReason is { } refused) { problems.Add(refused); break; }
            switch (action)
            {
                case ConsumeCharacterItem consume:
                    var key = consume.ItemId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    int Count(KoLInventorySnapshot i) => i.Items.Where(x => x.ItemId == key).Sum(x => x.Quantity);
                    var used = after.Inventory.IsStale ? 0 : Math.Max(0, Count(current.Inventory) - Count(after.Inventory));
                    if (used < consume.Quantity)
                        problems.Add($"item {consume.ItemId}: KoL confirmed {used} of {consume.Quantity}"
                            + (after.ResponseExcerpt is { Length: > 0 } excerpt ? $" ({excerpt})" : ""));
                    done += used;
                    break;
                case RestAtCampground:
                    var changed = !after.Status.IsStale && !current.Status.IsStale
                        && (after.Status.CurrentHp != current.Status.CurrentHp || after.Status.CurrentMp != current.Status.CurrentMp
                            || after.Status.Adventures != current.Status.Adventures);
                    if (!changed) { problems.Add("KoL did not confirm the rest."); break; }
                    done++;
                    var cost = current.Status.Adventures - after.Status.Adventures;
                    if (cost > 0) problems.Add($"that rest cost {cost} adventure(s), so no more rests were taken");
                    break;
            }
            current = after;
            if (problems.Count > 0 && action is RestAtCampground) break;
        }
        var summary = row.Kind == DailyStepKind.FreeRests ? $"{done} rest(s) taken" : $"{done} item(s) used";
        return problems.Count == 0 ? summary + "." : $"{summary}. {string.Join("; ", problems)}.";
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
            var ok = await RunDailyRoutineAsync(account, token, cancellationToken,
                hermitClovers, topUp, maxBaconPrice);
            if (_dailyStatusText is not null)
                _dailyStatusText.Text = (ok
                    ? "Daily routine executed. Details are in the audit log."
                    : "The daily routine could not be completed.") + _dailyNote;
        });
    }

    /// <summary>Runs the daily routine for the selected account through the
    /// same bridge the CLI uses.</summary>
    private string _dailyNote = string.Empty;

    private async Task<bool> RunDailyRoutineAsync(
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
                var outcome = await new BaconArbitrageExecutor(
                        new MallBuyClient(http, MallBuyClient.DefaultBaseUri), new KoLMallReadClient(http))
                    .TopUpBaconAsync(account.Credentials, needed, live.Meat, maxBaconPrice, reserve,
                        cancellationToken: cancellationToken);
                _dailyNote += $" BACON top-up: bought {outcome.Quantity} of {needed} for {outcome.Spent} meat.";
                if (outcome.Quantity > 0)
                    live = await new LiveDailySnapshotBuilder(readClient, doneStore)
                        .BuildAsync(new KoLibri.Core.Spiel.DailyRoutineSnapshot(), cancellationToken,
                            role: account.Profile.Role);
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
