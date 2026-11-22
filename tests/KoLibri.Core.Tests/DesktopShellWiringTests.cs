using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using KoLibri.Core.Basis;
using KoLibri.Desktop.ContextMenus;
using LibKolibri;
using KoLibri.Core.Markt;
using KoLibri.Core.Spiel;
using KoLibri.Desktop;
using Xunit;

namespace KoLibri.Core.Tests;

public sealed class DesktopShellWiringTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void LogKeepsNewestEntriesCollapsesRepeatsAndCountsErrors()
    {
        var log = new SessionLogViewModel(capacity: 10, now: () => Now);
        log.Search("a");
        log.Search("a");
        log.Error("boom");
        Assert.Equal(2, log.Entries.Count);
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

    private sealed class FakeReads(long meat, int owned, bool stale = false) : IMallPurchaseReads
    {
        public int Searches;
        public Task<MallSearchPage> SearchAsync(string term, CancellationToken ct)
        {
            Searches++;
            var listing = new MallListing("789", "42", "Shop", 100, 5, null, Now, new Uri("https://www.kingdomofloathing.com/"))
            { SellerId = "42", QuantityKnown = true, LimitKind = MallLimitKind.Unlimited };
            return Task.FromResult(new MallSearchPage(term,
                [new MallItemOffers("789", "Thing", [listing], 1)], stale ? Now.AddHours(-3) : Now,
                new Uri("https://www.kingdomofloathing.com/"), stale));
        }
        public Task<KoLStatusSnapshot?> StatusAsync(CancellationToken ct)
            => Task.FromResult<KoLStatusSnapshot?>(new KoLStatusSnapshot("Bot", 10, meat, 1, Now));
        public Task<DesktopInventorySnapshot> InventoryAsync(CancellationToken ct)
            => Task.FromResult(new DesktopInventorySnapshot
            {
                Role = AccountRole.Mallbot,
                Items = [new DesktopInventoryItem { ItemId = "789", Name = "Thing", Quantity = owned }],
                ObservedAt = Now
            });
    }

    [Fact]
    public async Task ObserverReadsOfferAndAccountAndTracksDailySpend()
    {
        var ledger = new MallSpendLedger(new FixedTime());
        ledger.Add("Mallbot", 1_000);
        var observer = new MallPurchaseObserver(new FakeReads(10_000, 2), "Mallbot", () => 50_000, ledger, new FixedTime());
        var request = new MallPurchaseRequest("42", "42", "789", 100, 2, accountId: "Mallbot");
        await Assert.ThrowsAsync<InvalidOperationException>(() => observer.BeforeAsync(request, default));
        observer.Remember("789", "Thing");

        var before = await observer.BeforeAsync(request, default);
        Assert.Equal(100, before.Offer.UnitPrice);
        Assert.Equal(5, before.Offer.Quantity);
        Assert.Equal(10_000, before.Account.Meat);
        Assert.Equal(1_000, before.Account.DailySpent);
        Assert.Equal(50_000, before.Account.DailyLimit);
        Assert.Equal(2, before.InventoryQuantity("789"));

        // After a purchase meat fell by 200: daily spend follows the observed change.
        var after = new MallPurchaseObserver(new FakeReads(9_800, 4), "Mallbot", () => 50_000, ledger, new FixedTime());
        after.Remember("789", "Thing");
        var preview = new MallPurchasePreview("a", request, before, Now, Now.AddMinutes(5), false, false);
        var observed = await after.AfterAsync(new MallPurchaseTransportCommit(preview, before, null, Now), default);
        Assert.Equal(1_200, observed.Account.DailySpent);
        Assert.Equal(4, observed.InventoryQuantity("789"));
    }

    private sealed class Prompt(bool confirm) : IMallPurchasePrompt
    {
        public int Asked;
        public int? Quantity;
        public async Task<DesktopMallPurchasePreview?> AskAsync(MallPurchasePromptContext context,
            Func<int, CancellationToken, Task<DesktopMallPurchasePreview>> preview, CancellationToken ct)
        {
            Asked++;
            var p = await preview(2, ct);
            Quantity = p.Quantity;
            return confirm ? p : null;
        }
    }

    private static MallOfferRow Row(int? stock = 5) => new()
    {
        Rank = 1, ItemId = "789", ItemName = "Thing", ShopId = "42", ShopName = "Shop",
        UnitPrice = 100, Stock = stock, LimitKind = MallLimitKind.Unlimited
    };

    private static (MallPurchaseCoordinator Coordinator, FixtureMallPurchaseTransport Fixture, SessionLogViewModel Log, MallSpendLedger Ledger)
        Build(Prompt prompt, bool signedIn = true)
    {
        var fixture = new FixtureMallPurchaseTransport(new MallPurchaseObservation(
            new MallPurchaseOffer("42", "42", "789", 100, 5, Now),
            new MallPurchaseAccountState("Mallbot", 10_000, 0, 1_000_000,
                new Dictionary<string, int> { ["789"] = 0 }, Now)));
        var log = new SessionLogViewModel(now: () => Now);
        var ledger = new MallSpendLedger(new FixedTime());
        var service = new DesktopMallPurchaseService(fixture, null, null, new FixedTime());
        var observer = new MallPurchaseObserver(new FakeReads(10_000, 0), "Mallbot", () => 1_000_000, ledger, new FixedTime());
        var coordinator = new MallPurchaseCoordinator("Mallbot",
            _ => Task.FromResult<(DesktopMallPurchaseService, MallPurchaseObserver)?>(signedIn ? (service, observer) : null),
            prompt, log, ledger);
        return (coordinator, fixture, log, ledger);
    }

    [Fact]
    public async Task ConfirmedBuySendsOnePurchaseAndRecordsSpend()
    {
        var prompt = new Prompt(true);
        var (coordinator, fixture, log, ledger) = Build(prompt);
        Assert.True(coordinator.CanRequest(Row()));
        await coordinator.RequestAsync(Row(), default);
        Assert.Equal(1, fixture.CommitCount);
        Assert.Equal(2, prompt.Quantity);
        Assert.Equal(200, ledger.SpentToday("Mallbot"));
        Assert.Contains(log.Entries, e => e.Kind == SessionLogKind.Purchase && e.Message.Contains("Purchased"));
    }

    [Fact]
    public async Task CancelledPromptSendsNothing()
    {
        var (coordinator, fixture, log, ledger) = Build(new Prompt(false));
        await coordinator.RequestAsync(Row(), default);
        Assert.Equal(0, fixture.CommitCount);
        Assert.Equal(0, ledger.SpentToday("Mallbot"));
        Assert.Contains(log.Entries, e => e.Message.Contains("nothing was sent"));
    }

    [Fact]
    public async Task SignedOutAndSoldOutOffersDoNotBuy()
    {
        var prompt = new Prompt(true);
        var (coordinator, fixture, log, _) = Build(prompt, signedIn: false);
        Assert.False(coordinator.CanRequest(Row(stock: 0)));
        await coordinator.RequestAsync(Row(), default);
        Assert.Equal(0, prompt.Asked);
        Assert.Equal(0, fixture.CommitCount);
        Assert.Equal(1, log.ErrorCount);
    }

    private static string TempLedger() => Path.Combine(Path.GetTempPath(), "kolibri-ledger-" + Guid.NewGuid().ToString("N"), "ledger.json");

    [Fact]
    public async Task StaleOfferBlocksTheBuyBeforeAnyPostAndIsNeverRedatedAsFresh()
    {
        var ledger = new MallSpendLedger(new FixedTime());
        var observer = new MallPurchaseObserver(new FakeReads(10_000, 0, stale: true), "Mallbot", () => 1_000_000, ledger, new FixedTime());
        // The observer feeds the gateway before the POST: a stale page throws there, so nothing is sent.
        await Assert.ThrowsAsync<InvalidOperationException>(() => Observe(observer));

        // A fresh page keeps its own fetch time instead of "now".
        var fresh = new MallPurchaseObserver(new FakeReads(10_000, 0), "Mallbot", () => 1_000_000, ledger,
            new OffsetTime(TimeSpan.FromMinutes(2)));
        var observation = await Observe(fresh);
        Assert.Equal(Now, observation.Offer.ObservedAt);
        Assert.Equal(Now, observation.ObservedAt);
    }

    private static Task<MallPurchaseObservation> Observe(MallPurchaseObserver observer)
    {
        observer.Remember("789", "Thing");
        return observer.BeforeAsync(new MallPurchaseRequest("42", "42", "789", 100, 2, accountId: "Mallbot"), default);
    }

    [Fact]
    public void BudgetSurvivesANewLedgerInstanceAndRollsOverWithTheDay()
    {
        var path = TempLedger();
        try
        {
            var first = new MallSpendLedger(new FixedTime(), path);
            first.Reserve("Mallbot", 300);
            first.Settle("Mallbot", 300);
            first.Reserve("Mallbot", 50); // crash while the POST was in flight
            Assert.Equal(300, first.SpentToday("Mallbot"));

            var second = new MallSpendLedger(new FixedTime(), path);
            Assert.Equal(350, second.SpentToday("Mallbot"));
            Assert.Equal(50, second.UnclearToday("Mallbot"));

            var nextDay = new MallSpendLedger(new OffsetTime(TimeSpan.FromDays(1)), path);
            Assert.Equal(0, nextDay.SpentToday("Mallbot"));
            second.Reconcile("Mallbot", 0);
            Assert.Equal(300, new MallSpendLedger(new FixedTime(), path).SpentToday("Mallbot"));
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    private sealed class OffsetTime(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now + offset;
    }

    [Fact]
    public void UnreadableLedgerFileBlocksSpending()
    {
        var path = TempLedger();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ not json");
            var ledger = new MallSpendLedger(new FixedTime(), path);
            Assert.True(ledger.Unreadable);
            Assert.True(ledger.SpentToday("Mallbot") > 1_000_000_000_000);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public async Task UnclearPurchaseStaysCountedAndNotSentIsReleased()
    {
        var path = TempLedger();
        try
        {
            var ledger = new MallSpendLedger(new FixedTime(), path);
            // Meat did not move although KoL accepted: the gateway cannot prove the purchase.
            var fixture = new FixtureMallPurchaseTransport(new MallPurchaseObservation(
                new MallPurchaseOffer("42", "42", "789", 100, 5, Now),
                new MallPurchaseAccountState("Mallbot", 10_000, 0, 1_000_000, new Dictionary<string, int> { ["789"] = 0 }, Now)));
            var service = new DesktopMallPurchaseService(fixture, null, null, new FixedTime());
            var observer = new MallPurchaseObserver(new FakeReads(10_000, 0), "Mallbot", () => 1_000_000, ledger, new FixedTime());
            var coordinator = new MallPurchaseCoordinator("Mallbot",
                _ => Task.FromResult<(DesktopMallPurchaseService, MallPurchaseObserver)?>((service, observer)),
                new Prompt(true), new SessionLogViewModel(now: () => Now), ledger);
            await coordinator.RequestAsync(Row(), default);
            Assert.Equal(1, fixture.CommitCount);
            var spent = new MallSpendLedger(new FixedTime(), path).SpentToday("Mallbot");
            // Either verified (settled) or unclear: in both cases the 200 meat stay counted after a restart.
            Assert.Equal(200, spent);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    // ---- context menus -------------------------------------------------------------------

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
        Assert.False(Find(effect.Build(new EffectMenuTarget("1", "Hardly Poisoned", false)), "Uneffect").IsEnabled);
        Assert.True(Find(effect.Build(new EffectMenuTarget("1", "Hardly Poisoned", true)), "Uneffect").IsEnabled);

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
                var accounts = new AccountSessionManager(new InMemoryAccountStore(), new InMemorySecretStore());
                var window = new KoLibri.Desktop.MainWindow(null, root.FullName, accounts,
                    settingsStore: new DesktopSettingsStore(Path.Combine(root.FullName, "settings.json")),
                    applySettingsToLimiters: false);
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
