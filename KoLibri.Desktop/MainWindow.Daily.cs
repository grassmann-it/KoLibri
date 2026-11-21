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
}
