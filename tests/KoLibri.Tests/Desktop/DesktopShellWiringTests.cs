using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using KoLibri.Desktop.ContextMenus;
using KoLibri.Desktop;
using Xunit;

namespace KoLibri.Tests.Desktop;

public sealed class DesktopShellWiringTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void LogKeepsNewestEntriesNeverMergesRepeatsAndCountsErrors()
    {
        var log = new SessionLogViewModel(capacity: 10, now: () => Now);
        log.Search("a");
        log.Search("a");
        log.Error("boom");
        Assert.Equal(3, log.Entries.Count);   // identical lines are two events, never merged
        Assert.Equal(1, log.ErrorCount);
        Assert.Contains("1 errors", log.Summary);
        for (var i = 0; i < 30; i++) log.Adventure($"turn {i}");
        Assert.Equal(10, log.Entries.Count);
        Assert.Equal(0, log.ErrorCount);
        Assert.Equal("turn 29", log.Entries[^1].Message);
        log.Add(SessionLogKind.Info, "  ");
        Assert.Equal(10, log.Entries.Count);
        log.Clear();
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void SettingsMapOntoPurchaseBudget()
    {
        var settings = new DesktopSettings();
        settings.Set("meatPerDay", 40_000);
        settings.Set("meatPerPurchase", 9_000);
        settings.Set("unitsPerPurchase", 3);
        var options = DesktopMallPurchaseSettings.ToOptions(settings);
        Assert.Equal(40_000, options.MaxDailySpend);
        Assert.Equal(9_000, options.MaxMeatPerPurchase);
        Assert.Equal(3, options.MaxQuantityPerPurchase);
    }

    private sealed class FakeHost : IShellHost
    {
        public AccountRole ActiveRole => AccountRole.Main;
        public SessionLogViewModel SessionLog { get; } = new(post: action => action());
        public List<string> Copied { get; } = new();
        public List<string> MallSearches { get; } = new();
        public List<Uri> Opened { get; } = new();
        public List<string> Messages { get; } = new();
        public void ShowMessage(string message) => Messages.Add(message);
        public Task NavigateToAsync(string section) => Task.CompletedTask;
        public Task RefreshStatusAsync(bool forceRefresh) => Task.CompletedTask;
        public Task SearchMallAsync(string term) { MallSearches.Add(term); return Task.CompletedTask; }
        public Task CopyTextAsync(string text) { Copied.Add(text); return Task.CompletedTask; }
        public Task OpenUrlAsync(Uri uri) { Opened.Add(uri); return Task.CompletedTask; }
    }

    private static ContextMenuItem Find(IReadOnlyList<ContextMenuItem> items, string label)
        => items.Single(item => item.Label == label || item.Label.StartsWith(label + " ", StringComparison.Ordinal));

    [Fact]
    public void ItemMenuOffersUseWithQuantityAndOnlyLogsPlaceholders()
    {
        var host = new FakeHost();
        var target = new ItemMenuTarget("4", "Thing", 7, ItemUseKind.Eat, new[] { "hat", "pants" });
        var items = new ItemContextMenu(host).Build(target);

        // Building the menu sent nothing and logged nothing.
        Assert.Empty(host.SessionLog.Entries);
        var eat = Find(items, "Eat");
        Assert.Equal(new[] { "Eat 1", "Eat 5", "Eat all (7)" }, eat.Submenu!.Select(item => item.Label));
        Assert.Contains(items, item => item.Label == "Equip to slot" && item.Submenu!.Count == 2);

        eat.Submenu![0].Invoke();
        Assert.Equal("Eat 1 Thing: not implemented yet.", host.SessionLog.Entries.Single().Message);

        // A row without a use kind has no use entry, and nothing offers Sell.
        var plain = new ItemContextMenu(host).Build(new ItemMenuTarget("5", "Rock", 1));
        Assert.DoesNotContain(plain, item => item.Label.StartsWith("Eat") || item.Label == "Use" || item.Label.Contains("Sell"));
    }

    [Fact]
    public void CopyAndNavigationEntriesAreReal()
    {
        var host = new FakeHost();
        var items = new ItemContextMenu(host).Build(new ItemMenuTarget("4", "Big Thing", 1));
        Find(items, "Copy name").Invoke();
        Find(items, "Copy ID").Invoke();
        Find(items, "Mallsearch").Invoke();
        Find(items, "Wiki").Invoke();
        Assert.Equal(new[] { "Big Thing", "4" }, host.Copied);
        Assert.Equal(new[] { "Big Thing" }, host.MallSearches);
        Assert.EndsWith("Big_Thing", host.Opened.Single().ToString());
    }

    [Fact]
    public void TypeMenusHaveTheirEntriesAndDisableWhatCannotRun()
    {
        var host = new FakeHost();
        var offer = new MallOfferContextMenu(host).Build(new OfferMenuTarget("4", "Thing", "Shop", 100, CanBuy: false));
        Assert.False(Find(offer, "Buy…").IsEnabled);
        Assert.True(Find(offer, "Search this item again").IsEnabled);

        var location = new LocationContextMenu(host).Build(new LocationMenuTarget("1", "The Haunted Pantry", RunTurns: 10));
        Assert.Contains(location, item => item.Label == "Adventure here");
        Assert.Contains(location, item => item.Label == "Run 10 turns");
        Assert.Contains(location, item => item.Label == "Copy id");

        var effect = new EffectContextMenu(host);
        // Uneffect is deliberately not offered until it is wired to a confirmed action.
        Assert.DoesNotContain(effect.Build(new EffectMenuTarget("1", "Hardly Poisoned", true)), item => item.Label == "Uneffect");
        Assert.Contains(effect.Build(new EffectMenuTarget("1", "Hardly Poisoned", false)), item => item.Label == "Copy name");

        var skill = new SkillContextMenu(host).Build(new SkillMenuTarget("3", "Salve", 12));
        Assert.Contains(skill, item => item.Label == "Cast (12 MP)");

        var gear = new GearSlotContextMenu(host);
        var empty = gear.Build(new GearSlotMenuTarget("hat", null, null));
        Assert.False(Find(empty, "Unequip").IsEnabled);
        Assert.Contains(empty, item => item.Label == "Equip…");
        Assert.Contains(gear.Build(new GearSlotMenuTarget("hat", "Fez", "9")), item => item.Label == "Change…");
        Assert.Empty(host.SessionLog.Entries);
    }

    [Fact]
    public void DisabledEntriesNeverRun()
    {
        var ran = 0;
        var item = ContextMenuItem.Action("Go", () => ran++).When(false, "no");
        Assert.False(item.Invoke());
        Assert.Equal(0, ran);
        Assert.True(ContextMenuItem.Action("Go", () => ran++).Invoke());
        Assert.Equal(1, ran);
    }

    private static readonly object AvaloniaGate = new();

    private static Task RunOnAvaloniaAsync(Func<Task> body)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            try
            {
                AppBuilder.Configure<KoLibri.Desktop.App>().UsePlatformDetect().SetupWithoutStarting();
                using var stop = new CancellationTokenSource();
                Dispatcher.UIThread.Post(async () =>
                {
                    try { await body(); completion.SetResult(); }
                    catch (Exception exception) { completion.SetException(exception); }
                    finally { stop.Cancel(); }
                });
                Dispatcher.UIThread.MainLoop(stop.Token);
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        }) { IsBackground = true }.Start();
        return completion.Task.WaitAsync(TimeSpan.FromMinutes(2));
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private sealed class X11Fact : FactAttribute
    {
        public X11Fact()
        {
            if (Environment.GetEnvironmentVariable("KOLIBRI_RUN_X11_REPLAY") != "1")
                Skip = "Run under Xvfb with KOLIBRI_RUN_X11_REPLAY=1.";
        }
    }

    private sealed record MenuRow(string Name);

    /// <summary>One run (Avalonia can be set up once per process): row addressing, keyboard, shared handler.</summary>
    [X11Fact]
    public async Task RowMenuAddressesTheRowUnderThePointerAndTheFocusedRow()
    {
        await RunOnAvaloniaAsync(async () =>
        {
            var rows = new[] { new MenuRow("A"), new MenuRow("B"), new MenuRow("C") };
            var list = new ListBox
            {
                ItemsSource = rows,
                ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<MenuRow>((row, _) => new TextBlock { Text = row.Name })
            };
            var window = new Window { Content = list, Width = 300, Height = 300 };
            window.Show();
            await UntilAsync(() => list.ContainerFromIndex(2) is not null);
            var opened = 0;
            var handled = 0;
            var controller = RowContextMenuController<MenuRow>.Attach(list, row =>
            {
                opened++;
                return new[] { ContextMenuItem.Action("Do " + row.Name, () => handled++) };
            });

            // Right-click on B while A is selected: the menu is for B and B gets selected.
            list.SelectedItem = rows[0];
            var rowB = list.ContainerFromIndex(1)!;
            var items = controller.OpenForPointer(rowB);
            Assert.Equal(rows[1], controller.AddressedRow);
            Assert.Equal(rows[1], list.SelectedItem);
            Assert.Equal("Do B", Assert.Single(items!).Label);

            // Keyboard: the focused row is addressed, not the selected one.
            list.SelectedItem = rows[0];
            list.ContainerFromIndex(2)!.Focus();
            await UntilAsync(() => TopLevel.GetTopLevel(list)!.FocusManager!.GetFocusedElement() == list.ContainerFromIndex(2));
            controller.OpenForKeyboard();
            Assert.Equal(rows[2], controller.AddressedRow);

            // The Menu key and Shift+F10 reach the same path.
            list.SelectedItem = rows[1];
            list.ContainerFromIndex(1)!.Focus();
            list.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F10, KeyModifiers = KeyModifiers.Shift });
            Assert.Equal(rows[1], controller.AddressedRow);
            list.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Apps });
            Assert.Equal(4, opened);

            // Opening ran no handler; the entry and a button share one handler.
            Assert.Equal(0, handled);
            var entry = controller.LastItems![0];
            var button = new Button();
            entry.BindTo(button);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, handled);
            entry.Invoke();
            Assert.Equal(2, handled);
            window.Close();
        });
    }

    [X11Fact]
    public async Task TabSwitchingWorksAfterTheViewSplit()
    {
        await RunOnAvaloniaAsync(async () =>
        {
            var root = Directory.CreateTempSubdirectory("kolibri-shell-split-");
            try
            {
                var accounts = DesktopKit.Accounts(new FakeKol());
                var window = new KoLibri.Desktop.MainWindow(null, root.FullName, accounts,
                    settingsStore: new DesktopSettingsStore(Path.Combine(root.FullName, "settings.json")));
                window.Show();
                bool Visible(string name) => window.FindControl<Control>(name)!.IsVisible;
                void Click(string name) => window.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.NotNull(window.FindControl<ListBox>("EncounterLocationsGrid"));   // AdventuresView
                Assert.NotNull(window.FindControl<ListBox>("InventoryList"));            // InventoryView
                Assert.NotNull(window.FindControl<TextBox>("CharacterOutfitId"));        // GearView
                Assert.NotNull(window.FindControl<TextBox>("CharacterSkillId"));         // SkillsEffectsView
                Assert.NotNull(window.FindControl<ListBox>("DailyRows"));                // DailyView
                Assert.NotNull(window.FindControl<TextBlock>("SidebarName"));            // CharpaneView

                Click("NavInventory");
                Assert.True(Visible("InventoryPanel"));
                Assert.False(Visible("AdventurePanel"));
                Click("InvViewGear");
                Assert.True(Visible("InventoryGearView"));
                Assert.False(Visible("InventoryItemsView"));
                Click("InvViewSkills");
                Assert.True(Visible("InventorySkillsView"));
                Assert.False(Visible("InventoryGearView"));
                Click("InvViewItems");
                Assert.True(Visible("InventoryItemsView"));
                Click("NavDaily");
                Assert.True(Visible("DailyPanel"));
                Assert.False(Visible("InventoryPanel"));
                Click("NavMall");
                Assert.True(Visible("MallSection"));
                Click("NavAdventure");
                Assert.True(Visible("AdventurePanel"));
                Click("NavSettings");
                Assert.True(Visible("SettingsPanel"));
                window.Close();
            }
            finally { try { root.Delete(true); } catch (IOException) { } }
        });
    }
}
