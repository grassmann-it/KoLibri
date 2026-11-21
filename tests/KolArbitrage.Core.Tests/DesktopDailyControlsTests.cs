using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using KolArbitrage.Core.Accounts;
using KoLibri.Desktop;
using Xunit;

namespace KolArbitrage.Core.Tests;

public sealed class DesktopDailyControlsTests
{
    [Fact]
    public async Task Daily_tab_offers_the_hermit_clover_and_bacon_top_up_controls()
    {
        if (Environment.GetEnvironmentVariable("KOLIBRI_RUN_X11_REPLAY") != "1")
            return; // Needs a private Xvfb; see the X11 replay tests.

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uiThread = new Thread(() =>
        {
            try
            {
                AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
                using var stop = new CancellationTokenSource();
                Dispatcher.UIThread.Post(async () =>
                {
                    try { await CheckAsync(); completion.SetResult(); }
                    catch (Exception exception) { completion.SetException(exception); }
                    finally { stop.Cancel(); }
                });
                Dispatcher.UIThread.MainLoop(stop.Token);
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        }) { IsBackground = true };
        uiThread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromMinutes(2));
    }

    private static async Task CheckAsync()
    {
        var root = Directory.CreateTempSubdirectory("kolibri-daily-controls-");
        try
        {
            var accounts = new AccountSessionManager(new InMemoryAccountStore(), new InMemorySecretStore());
            var window = new MainWindow(null, root.FullName, accounts);
            window.Show();
            try
            {
                window.FindControl<Button>("NavDaily")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var hermit = window.FindControl<NumericUpDown>("DailyHermitClovers")!;
                var topUp = window.FindControl<CheckBox>("DailyBaconTopUp")!;
                var price = window.FindControl<NumericUpDown>("DailyBaconMaxPrice")!;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while (!hermit.IsEffectivelyVisible) await Task.Delay(20, timeout.Token);

                Assert.Equal(0, hermit.Value);
                Assert.True(topUp.IsChecked == false);
                Assert.Equal(1500, price.Value);
                Assert.True(topUp.IsEffectivelyVisible && price.IsEffectivelyVisible);
                Assert.True(window.FindControl<Button>("DailyRunButton")!.IsEffectivelyVisible);
            }
            finally { window.Close(); }
        }
        finally { root.Delete(recursive: true); }
    }
}
