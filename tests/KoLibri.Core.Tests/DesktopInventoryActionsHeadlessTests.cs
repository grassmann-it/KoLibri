using System.Net;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KoLibri.Core.Basis;
using KoLibri.Desktop;
using KoLibri.Core.Spiel;
using Xunit;
using Xunit.Abstractions;

namespace KoLibri.Core.Tests;

/// <summary>Drives the real Inventory tab (button + selection) under a private Xvfb; no network, no stored session.</summary>
public sealed class DesktopInventoryActionsHeadlessTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Inventory_actions_by_button_and_context_menu_always_end_in_a_visible_message()
    {
        await RunOnUiThreadAsync(async () => { await CheckAsync(); await SignedInAsync(); });
    }

    private static async Task RunOnUiThreadAsync(Func<Task> check)
    {
        if (Environment.GetEnvironmentVariable("KOLIBRI_RUN_X11_REPLAY") != "1")
            return; // Needs a private Xvfb: KOLIBRI_RUN_X11_REPLAY=1 xvfb-run -a dotnet test ...

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uiThread = new Thread(() =>
        {
            try
            {
                AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
                using var stop = new CancellationTokenSource();
                Dispatcher.UIThread.Post(async () =>
                {
                    try { await check(); completion.SetResult(); }
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

    private static void Shot(Avalonia.Controls.TopLevel t, string name)
    {
        var dir = Environment.GetEnvironmentVariable("KOLIBRI_QA_SHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        Dispatcher.UIThread.RunJobs();
        var size = t.ClientSize;
        var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)size.Width, (int)size.Height), new Vector(96, 96));
        bmp.Render(t);
        bmp.Save(Path.Combine(dir, name.Replace(' ', '_') + ".png"));
    }

    private static T Priv<T>(object o, string field) => (T)o.GetType()
        .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;

    private async Task SignedInAsync()
    {
        Environment.SetEnvironmentVariable("KOLIBRI_MIN_REQUEST_INTERVAL_MS", "1000");
        var root = Directory.CreateTempSubdirectory("kolibri-inv-signed-");
        try
        {
            var accounts = new AccountSessionManager(new InMemoryAccountStore(), new InMemorySecretStore());
            await accounts.InitializeAsync();
            var kol = new FakeKol();
            // Every network path (live reads, sidebar refresh, character actions) goes to the fake; nothing reaches KoL.
            var window = new MainWindow(null, root.FullName, accounts,
                session => KoLLoginClient.CreateAuthenticatedClient(session.Credentials,
                    new Uri("https://www.kingdomofloathing.com/"), innerHandler: kol),
                new DesktopSettingsStore(Path.Combine(root.FullName, "s.json")), applySettingsToLimiters: false)
            { Width = 1280, Height = 900, CharacterTransportOverride = kol };
            window.Show();
            _current = window;
            try
            {
                await Task.Delay(1500);
                var role = Priv<AccountRole>(window, "_selectedRole");
                await accounts.SetProfileAsync(new AccountProfile { Id = "qa", DisplayName = "QA", Role = role });
                await accounts.SetCredentialsAsync(role, new AccountCredentials
                {
                    AccessToken = "pwdhash", CookieHeader = "PHPSESSID=x", Username = "QA",
                    ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
                });
                window.FindControl<Button>("NavInventory")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(500);
                var inv = Priv<InventoryViewModel>(window, "_inventory");
                inv.ApplySnapshot(new[] { new DesktopInventoryItem { ItemId = "995", Name = "Lucky Surprise Egg", Quantity = 3,
                    AccessFlags = new[] { "t", "d" }, Uses = new[] { "Use" } } }, DateTimeOffset.Now);
                var view = window.FindControl<InventoryView>("InventoryTab")!;
                var list = view.FindControl<ListBox>("InventoryList")!;
                var result = view.FindControl<TextBlock>("InventoryActionResult")!;
                var panel = view.FindControl<StackPanel>("InventoryDetailPanel")!;
                list.SelectedItem = inv.Items[0];
                await Task.Delay(300);

                // 1. Button, fits in the stomach: no prompt, ends with "Ate 1 × ..." and the item count drops.
                var eat = panel.Children.OfType<Button>().Single(b => b.Content is "Eat");
                eat.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitFor(() => result.Text?.StartsWith("Ate") == true, "Ate message after button");
                Assert.Equal(1, kol.EatPosts);
                Assert.Equal(2, inv.Items.Single().Quantity);
                output.WriteLine("button: " + result.Text);
                await WaitIdle(window);

                // 2. Context menu entry: the same path.
                var menu = view.ItemMenu!;
                var entries = menu.OpenForKeyboard()!;
                var eatMenu = entries.First(e => e.Label == "Eat").Submenu!.First(e => e.Label == "Eat 1");
                result.Text = "";
                Assert.True(eatMenu.Invoke());
                await WaitFor(() => kol.EatPosts == 2 && result.Text?.StartsWith("Ate") == true, "second eat via menu");
                Assert.Contains("1 left", result.Text);
                output.WriteLine("menu: " + result.Text);
                await WaitIdle(window);

                // 3. Overfill: the prompt appears; Cancel sends nothing.
                kol.Fullness = 15;
                list.SelectedItem = inv.Items[0];
                await Task.Delay(300);
                result.Text = "";
                panel.Children.OfType<Button>().Single(b => b.Content is "Eat").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var dialog = await WaitForDialog(window);
                dialog.FindDescendantButton("Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitFor(() => result.Text?.StartsWith("Cancelled") == true, "cancel message");
                Assert.Equal(2, kol.EatPosts);
                await WaitIdle(window);

                // 4. Overfill confirmed: sends the request and reports.
                panel.Children.OfType<Button>().Single(b => b.Content is "Eat").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                dialog = await WaitForDialog(window);
                dialog.FindDescendantButton("Eat anyway").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitFor(() => kol.EatPosts == 3 && !string.IsNullOrEmpty(result.Text) && !result.Text.StartsWith("Cancelled"), "confirmed eat");
                output.WriteLine("overfill confirmed: " + result.Text);
            }
            finally { window.Close(); }
        }
        finally { try { root.Delete(true); } catch { } }
    }

    private static MainWindow? _current;

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try { while (!condition()) await Task.Delay(50, timeout.Token); }
        catch (OperationCanceledException) { throw new Xunit.Sdk.XunitException("Timed out waiting for: " + what + " | status: "
            + _current?.FindControl<TextBlock>("StatusText")?.Text); }
    }

    private static Task WaitIdle(MainWindow window)
        => WaitFor(() => Priv<CancellationTokenSource?>(window, "_refreshCancellation") is null, "busy guard released");

    private static async Task<OverfillConfirmDialog> WaitForDialog(MainWindow window)
    {
        OverfillConfirmDialog? found = null;
        await WaitFor(() => (found = window.OwnedWindows.OfType<OverfillConfirmDialog>().FirstOrDefault()) is not null, "overfill dialog");
        await Task.Delay(100);
        return found!;
    }

    private sealed class FakeKol : HttpMessageHandler
    {
        public int Quantity = 3;
        public int Fullness;
        public int EatPosts;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string body;
            if (request.Method == HttpMethod.Post)
            {
                var form = await request.Content!.ReadAsStringAsync(token);
                Assert.Contains("whichitem=995", form);
                EatPosts++; Quantity--; Fullness++;
                body = "You eat the egg.";
            }
            else if (request.RequestUri!.Query.Contains("what=inventory"))
                body = "{\"995\":" + Quantity + "}";
            else
                body = "{\"full\":\"" + Fullness + "\",\"drunk\":0,\"spleen\":0,\"familiar\":\"0\",\"equipment\":{}}";
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(body) };
        }
    }

    private async Task CheckAsync()
    {
        var root = Directory.CreateTempSubdirectory("kolibri-inv-actions-");
        try
        {
            var accounts = new AccountSessionManager(new InMemoryAccountStore(), new InMemorySecretStore());
            var window = new MainWindow(null, root.FullName, accounts, null,
                new DesktopSettingsStore(Path.Combine(root.FullName, "s.json")), applySettingsToLimiters: false)
            { Width = 1280, Height = 900 };
            window.Show();
            try
            {
                await Task.Delay(1500);
                window.FindControl<Button>("NavInventory")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(800);
                var inv = Priv<InventoryViewModel>(window, "_inventory");
                var items = new (string Id, string Name, int Qty, string[] Flags)[]
                {
                    ("995", "Lucky Surprise Egg", 2, new[] { "t", "d" }), ("41", "ice-cold Sir Schlitz", 3, new[] { "t", "d" }),
                    ("14", "moxie weed", 4, new[] { "t", "d" }), ("471", "hot wing", 12, new[] { "t", "d" }),
                    ("297", "tamarind-flavored chewing gum", 2, new[] { "t", "d" })
                };
                inv.ApplySnapshot(items.Select(i => new DesktopInventoryItem
                {
                    ItemId = i.Id, Name = i.Name, Quantity = i.Qty, AccessFlags = i.Flags, Uses = new[] { "Use" }
                }).ToList(), DateTimeOffset.Now);
                await Task.Delay(300);

                var view = window.FindControl<InventoryView>("InventoryTab")!;
                var list = view.FindControl<ListBox>("InventoryList")!;
                var result = view.FindControl<TextBlock>("InventoryActionResult")!;
                var panel = view.FindControl<StackPanel>("InventoryDetailPanel")!;
                foreach (var row in inv.Items.ToList())
                {
                    result.Text = "";
                    list.SelectedItem = inv.Items.First(r => r.ItemId == row.ItemId);
                    await Task.Delay(200);
                    var buttons = panel.Children.OfType<Button>().Select(b => b.Content?.ToString()).ToList();
                    output.WriteLine($"{row.Name}: buttons [{string.Join(", ", buttons)}]");
                    foreach (var b in panel.Children.OfType<Button>().Where(b => b.Content is "Eat" or "Drink" or "Chew" or "Use").ToList())
                    {
                        result.Text = "";
                        Assert.True(b.IsEnabled, $"{b.Content} disabled for {row.Name}");
                        Assert.True(b.IsEffectivelyVisible);
                        b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        while (string.IsNullOrEmpty(result.Text)) await Task.Delay(50, timeout.Token);
                        output.WriteLine($"  {b.Content} -> {result.Text} | status: {window.FindControl<TextBlock>("StatusText")?.Text}");
                        Assert.False(string.IsNullOrWhiteSpace(result.Text));
                        Shot(window, row.Name);
                        // busy guard must be released afterwards
                        await Task.Delay(300);
                    }
                }
            }
            finally { window.Close(); }
        }
        finally { try { root.Delete(true); } catch { } }
    }

    private sealed class InMemoryAccountStore : IAccountStore
    {
        private AccountConfiguration _config = new() { Accounts = new() };
        public Task<AccountConfiguration> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_config);
        public Task SaveAsync(AccountConfiguration configuration, CancellationToken cancellationToken = default)
        { _config = configuration; return Task.CompletedTask; }
    }

    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, AccountCredentials> _secrets = new(StringComparer.OrdinalIgnoreCase);
        public Task<AccountCredentials?> GetAsync(string accountId, CancellationToken cancellationToken = default)
            => Task.FromResult(_secrets.TryGetValue(accountId, out var c) ? c : null);
        public Task SetAsync(string accountId, AccountCredentials credentials, CancellationToken cancellationToken = default)
        { _secrets[accountId] = credentials; return Task.CompletedTask; }
        public Task RemoveAsync(string accountId, CancellationToken cancellationToken = default)
        { _secrets.Remove(accountId); return Task.CompletedTask; }
    }
}

internal static class DialogTestExtensions
{
    public static Button FindDescendantButton(this Avalonia.Controls.Window window, string label)
        => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Button>()
            .Single(b => b.Content?.ToString() == label);
}
