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
