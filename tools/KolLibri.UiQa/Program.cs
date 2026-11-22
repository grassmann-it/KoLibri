using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KoLibri.Core.Basis;
using KoLibri.Core.Markt;
using KoLibri.Desktop;

// Offline: in-memory accounts (no stored session, no network), a temp settings file, fixture data.
if (Environment.GetEnvironmentVariable("KOLIBRI_QA_XVFB") != "1")
{
    Console.Error.WriteLine("Run inside xvfb-run with KOLIBRI_QA_XVFB=1. Refusing to open on a real desktop.");
    return 2;
}

var outDir = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "qa");
Directory.CreateDirectory(outDir);
var only = args.FirstOrDefault(a => a.StartsWith("--only="))?[7..];
var done = new TaskCompletionSource();
var thread = new Thread(() =>
{
    try
    {
        AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
        using var stop = new CancellationTokenSource();
        Dispatcher.UIThread.Post(async () =>
        {
            try { await RunAsync(); done.SetResult(); }
            catch (Exception e) { done.TrySetException(e); }
            finally { stop.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(stop.Token);
    }
    catch (Exception e) { done.TrySetException(e); }
});
thread.Start();
try { await done.Task; } catch (Exception e) { Console.Error.WriteLine(e); return 1; }
return 0;

async Task RunAsync()
{
    var tmp = Directory.CreateTempSubdirectory("kolibri-qa-");
    try
    {
        foreach (var (w, h) in new[] { (820, 700), (1280, 900) })
        foreach (var dark in new[] { false, true })
        {
            var theme = dark ? "dark" : "light";
            var settings = new DesktopSettingsStore(Path.Combine(tmp.FullName, $"s-{w}-{theme}.json"));
            var window = new MainWindow(null, tmp.FullName,
                new AccountSessionManager(new InMemoryAccountStore(), new InMemorySecretStore()),
                null, settings, applySettingsToLimiters: false)
            { Width = w, Height = h };
            Application.Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            window.Show();
            await Pump(2500); // Opened handler: sign-in offer opens Settings
            Application.Current.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            string P(string n) => $"{w}-{theme}-{n}";
            var set = (string name) => !(only is not null && !name.Contains(only));

            SeedFixtures(window);
            if (set("settings-signin")) Shot(window, P("settings-signin"));
            await Section(window, "NavAdventure"); Shot(window, P("adventures"));
            if (w == 1280 && !dark)
            {
                window.ShowDecisionForQa(new KoLibri.Core.Spiel.NeedsDecisionInfo
                {
                    RunId = "qa", ChoiceId = "1003", Title = "A Turtle Tamer appears",
                    Options = new[] { new KoLibri.Core.Spiel.ChoiceOptionInfo(1, "Help the turtle"),
                        new KoLibri.Core.Spiel.ChoiceOptionInfo(2, "Leave it alone") },
                    Reason = "No choice policy answers this choice. Pick an option."
                });
                await Pump(400); Shot(window, P("adventures-choice"));
            }
            await Section(window, "NavInventory"); SeedFixtures(window); await Pump(500); Shot(window, P("inventory-items"));
            foreach (var v in new[] { "Gear", "Skills" })
            {
                Click(window, $"InvView{v}"); await Pump(400); Shot(window, P($"inventory-{v.ToLower()}"));
            }
            SeedCharpane(window);
            await Pump(300); Shot(window, P("charpane-skills"));
            Click(window, "InvViewItems");
            await Section(window, "NavMall"); Shot(window, P("mall-empty"));
            await SeedMallResults(window); await Pump(500); Shot(window, P("mall-results"));
            await SeedCloverResults(window); await Pump(500); Shot(window, P("mall-clover"));
            await SeedQueuedBuys(window); await Pump(900); Shot(window, P("mall-queued-buys"));
            await Section(window, "NavDaily"); SeedDaily(window, stale: false); await Pump(400); Shot(window, P("daily"));
            SeedDaily(window, stale: true); await Pump(400); Shot(window, P("daily-stale"));
            SeedDaily(window, stale: false);
            await Section(window, "NavCommunication"); Shot(window, P("chat-mail"));
            await Section(window, "NavBrowser"); await Pump(600); Shot(window, P("browser"));
            if (w == 1280 && !dark)
            {
                var bp = window.FindControl<BrowserView>("BrowserPanel")!;
                foreach (var st in new[] { "opening", "error", "system" }) { bp.ShowStateForQa(st); await Pump(300); Shot(window, P("browser-" + st)); }
            }
            await Section(window, "NavSettings"); Shot(window, P("settings"));
            var qaLog = Priv<SessionLogViewModel>(window, "_log");
            qaLog.RoleProvider = () => "Main"; qaLog.Purchase("Purchased 1 × hot wing at 1,200 Meat");
            qaLog.Purchase("Purchased 1 × hot wing at 1,200 Meat");
            qaLog.RoleProvider = () => "Mallbot"; qaLog.Error("Mall search failed: KoL did not answer.");
            window.FindControl<Expander>("SessionLogExpander")!.IsExpanded = true;
            await Pump(400); Shot(window, P("settings-session-log"));
            window.ShowShortcutHelpForQa(); await Pump(300); Shot(window, P("help"));
            window.FindControl<Border>("ShortcutHelpOverlay")!.IsVisible = false;
            await SeedSettingsDaily(window); Shot(window, P("settings-daily"));
            window.FindControl<Expander>("SessionLogExpander")!.IsExpanded = true;
            await Section(window, "NavMall");
            Shot(window, P("mall-session-log"));
            window.FindControl<Expander>("SessionLogExpander")!.IsExpanded = false;

            if (w == 1280)
            {
                var dialog = new MallPurchaseDialog(
                    new MallPurchasePromptContext("Mallbot", FixtureOffers()[0], 5, 20_000));
                dialog.Show(window);
                await Pump(800);
                Shot(dialog, P("buy-dialog"));
                dialog.Close();
            }
            window.Close();
            await Pump(300);
        }
    }
    finally { try { tmp.Delete(true); } catch { } }
}

static void SeedCharpane(MainWindow w)
{
    var now = DateTimeOffset.UtcNow;
    var status = new KoLibri.Core.Spiel.KoLStatusSnapshot("QA Tester", 87, 2345678, 13, now)
    {
        CurrentHp = 120, MaximumHp = 200, CurrentMp = 85, MaximumMp = 150,
        Fullness = 5, Inebriety = 3, Spleen = 7, FamiliarId = "9", FamiliarWeight = 20,
        TimedEffects = [new("Ode to Booze", 14), new("Ur-Kel's Aria of Annoyance", 120), new("Polka of Plenty", 1)]
    };
    w.FindControl<CharpaneView>("CharacterPane")!.Show(CharpaneState.FromStatus(status, "Main", now));
}

static async Task Pump(int ms) => await Task.Delay(ms);

async Task Section(MainWindow w, string nav) { Click(w, nav); await Pump(700); }

static void Click(MainWindow w, string name)
    => w.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

void Shot(TopLevel t, string name)
{
    Dispatcher.UIThread.RunJobs();
    var size = t.ClientSize;
    var scaling = 1.0;
    var bmp = new RenderTargetBitmap(new PixelSize((int)size.Width, (int)size.Height), new Vector(96 * scaling, 96 * scaling));
    bmp.Render(t);
    bmp.Save(Path.Combine(outDir, name + ".png"));
    Console.WriteLine(name);
}

static T Priv<T>(object o, string field) =>
    (T)o.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;

static List<MallOfferRow> FixtureOffers() =>
[
    Row(1, "Hot wing", 1200, 50), Row(2, "Hot wing", 1350, 12), Row(3, "Hot wing", 1499, 3),
    Row(4, "Hot wing", 2000, null)
];
static MallOfferRow Row(int rank, string item, long price, int? stock) => new()
{
    Rank = rank, ItemId = "471", ItemName = item, ShopId = (100 + rank).ToString(), ShopName = $"Fixture Shop {rank}",
    UnitPrice = price, Stock = stock, LimitKind = rank == 3 ? MallLimitKind.Limited : MallLimitKind.Unlimited,
    DailyLimit = rank == 3 ? 5 : null, PricegunMedian = 1300, CanBuy = true
};

static void SeedFixtures(MainWindow w)
{
    // Real KoLmafia ids and flags; the inventory classifies them from the catalog, so the flags here
    // only mirror the catalog: hot wing (food, t d), Sir Schlitz (booze), moxie weed (spleen),
    // tamarind gum (potion), seal-clubbing club, Knob Goblin harem veil (hat), action figure body (quest, q), meat paste.
    var items = new (string Id, string Name, int Qty, string[] Flags)[]
    {
        ("471", "hot wing", 12, new[] { "t", "d" }), ("41", "ice-cold Sir Schlitz", 3, new[] { "t", "d" }),
        ("14", "moxie weed", 4, new[] { "t", "d" }), ("297", "tamarind-flavored chewing gum", 2, new[] { "t", "d" }),
        ("1", "seal-clubbing club", 1, new[] { "t", "d" }), ("306", "Knob Goblin harem veil", 1, new[] { "t", "d" }),
        ("63", "action figure body", 1, new[] { "q" }), ("25", "meat paste", 340, new[] { "t", "d" })
    };
    var inv = Priv<InventoryViewModel>(w, "_inventory");
    inv.ApplySnapshot(items.Select((i, n) => new DesktopInventoryItem
    {
        ItemId = i.Id, Name = i.Name, Quantity = i.Qty, AccessFlags = i.Flags,
        Uses = new[] { "Use", "Autosell" }, CheapestMallPrice = 100 * (n + 1)
    }).ToList(), DateTimeOffset.Now);
    inv.Organs = new OrganState(3, 2, 5);
    inv.SelectedItem = inv.Items.First(i => i.Name == "hot wing");
    inv.ActionResult = "Ate 2 × hot wing; 10 left. " + inv.OrganSummary;
}

async Task SeedCloverResults(MainWindow w)
{
    var search = w.FindControl<MallSearchView>("MallSearch")!;
    var model = new MallSearchViewModel(new FixtureBackend(FixtureOffers().Select((r, i) => r with { UnitPrice = 25_500 + i * 1_200 }).ToList()));
    model.PurchaseRequest = new AllowAll();
    model.CloverThreshold = () => 27_000;
    search.Attach(model, Priv<LiveMallViewModel>(w, "_mall"));
    await model.SearchCloverAsync();
}

static void SeedDaily(MainWindow w, bool stale)
{
    var daily = Priv<DailyBriefingViewModel>(w, "_daily");
    daily.Steps.Clear();
    if (!stale)
        foreach (var r in new[]
        {
            new DailyStepRow(DailyStepKind.Adventures, "Adventure target", "Knucklebones 1,621 / 100 · target reached", "done", "Plan", true),
            new DailyStepRow(DailyStepKind.LuckyEgg, "Eat Lucky Surprise Egg", "3 in the inventory (item 995)", "open", "Run", true),
            new DailyStepRow(DailyStepKind.FreeRests, "Free rests", "Up to 5 rests; stops at the first rest that costs adventures", "open", "Run", true),
            new DailyStepRow(DailyStepKind.Booze, "Booze", "No booze chosen. Pick them in Settings.", "blocked", "Run", false, "Choose booze in Settings first."),
            new DailyStepRow(DailyStepKind.Spleen, "Spleen", "Fixture spleen item × 1 · spleen 4", "skipped", "Run", false, "Skipped for today."),
            new DailyStepRow(DailyStepKind.CloverCheck, "Clover check", "No 11-leaf clovers in the inventory", "open", "Check", true),
        }) daily.Steps.Add(r);
    typeof(DailyBriefingViewModel).GetProperty(nameof(DailyBriefingViewModel.SessionStale))!.SetValue(daily, stale);
}

async Task SeedSettingsDaily(MainWindow w)
{
    var view = w.FindControl<ContentControl>("SettingsHost")!.Content as SettingsView;
    if (view?.DataContext is not SettingsViewModel model) return;
    model.Booze.Item = "1234"; model.Booze.Quantity = 2; model.Booze.Add.Execute(null);
    model.Spleen.Item = "5678"; model.Spleen.Add.Execute(null);
    await Pump(300);
    var header = view.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == "Daily");
    w.FindControl<Expander>("SessionLogExpander")!.IsExpanded = false;
    if (header?.FindAncestorOfType<ScrollViewer>() is { } scroll && header.TranslatePoint(new Avalonia.Point(0, 0), (Visual)scroll.Content!) is { } at)
        scroll.Offset = new Avalonia.Vector(0, Math.Max(0, at.Y - 8));
    await Pump(500);
}

async Task SeedMallResults(MainWindow w)
{
    var search = w.FindControl<MallSearchView>("MallSearch")!;
    var model = new MallSearchViewModel(new FixtureBackend(FixtureOffers()));
    model.PurchaseRequest = new AllowAll();
    search.Attach(model, Priv<LiveMallViewModel>(w, "_mall"));
    model.SearchText = "Hot wing";
    await model.SearchAsync();
}

/// <summary>Several buys in different states: refused, settled, unclear, sending and two still queued.</summary>
async Task SeedQueuedBuys(MainWindow w)
{
    var search = w.FindControl<MallSearchView>("MallSearch")!;
    var model = new MallSearchViewModel(new FixtureBackend(FixtureOffers()));
    var log = Priv<SessionLogViewModel>(w, "_log");
    var ledger = new MallSpendLedger();
    var queue = new MallBuyQueue("Mallbot", ledger, log, a => Avalonia.Threading.Dispatcher.UIThread.Post(a),
        verifyDelay: TimeSpan.FromMilliseconds(50), batchWait: TimeSpan.FromMilliseconds(50));
    model.PurchaseRequest = new MallPurchaseCoordinator("Mallbot", _ => Task.FromResult<(DesktopMallPurchaseService, MallPurchaseObserver)?>(null),
        null!, log, ledger, queue: queue);
    search.Attach(model, Priv<LiveMallViewModel>(w, "_mall"));
    model.SearchText = "Hot wing";
    await model.SearchAsync();
    var options = new DesktopMallPurchaseOptions { MaxDailySpend = 500_000, MaxMeatPerPurchase = 250_000, MaxQuantityPerPurchase = 10 };
    var engine = new QaEngine();
    var rows = FixtureOffers();
    MallBuyOrder O(int i, int qty) => new(rows[i].ItemId, rows[i].ItemName, rows[i].ShopId, rows[i].ShopName, rows[i].UnitPrice, qty);
    queue.Enqueue(O(0, 1), engine, options);   // refused by KoL
    queue.Enqueue(O(1, 2), engine, options);   // delivered
    queue.Enqueue(O(2, 1), engine, options);   // never shows up: unclear
    queue.Enqueue(O(0, 3), engine, options);   // blocks: sending
    queue.Enqueue(O(1, 1), engine, options);   // queued
    queue.Enqueue(O(2, 2), engine, options);   // queued
    model.RefreshUnclear();
}

sealed class QaEngine : IMallBuyEngine
{
    private readonly Dictionary<string, int> _items = new();
    private int _sends;
    public Task<MallAccountReading> ReadAccountAsync(CancellationToken ct)
        => Task.FromResult(new MallAccountReading(1_000_000, new Dictionary<string, int>(_items), true));
    public async Task<DesktopMallPurchaseResult> SendAsync(MallBuyOrder order, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref _sends);
        if (n == 1)
            return new DesktopMallPurchaseResult { Status = DesktopMallPurchaseStatus.Rejected, Message = "The shop no longer has the item at that price (sold or repriced)." };
        if (n == 4) await Task.Delay(Timeout.Infinite, ct);
        if (n == 2) _items[order.ItemId] = _items.GetValueOrDefault(order.ItemId) + order.Quantity;
        return new DesktopMallPurchaseResult { Status = DesktopMallPurchaseStatus.Sent, Confirmed = true, UnitPrice = order.ShownUnitPrice, Message = "ok" };
    }
}

sealed class AllowAll : IMallPurchaseRequest
{
    public bool CanRequest(MallOfferRow offer) => true;
    public Task RequestAsync(MallOfferRow offer, CancellationToken ct) => Task.CompletedTask;
}

sealed class FixtureBackend(List<MallOfferRow> rows) : IMallSearchBackend
{
    public Task<MallSearchPage> SearchAsync(string term, bool forceRefresh, CancellationToken ct)
    {
        var uri = new Uri("https://www.kingdomofloathing.com/mall.php");
        var listings = rows.Select(r => new MallListing(r.ItemId, r.ShopId, r.ShopName, r.UnitPrice, r.Stock ?? 0, r.DailyLimit,
            DateTimeOffset.Now, uri)
        { ItemName = r.ItemName, QuantityKnown = r.Stock is not null, LimitKind = r.LimitKind }).ToList();
        var page = new MallSearchPage(term, [new MallItemOffers("471", "Hot wing", listings, 40)], DateTimeOffset.Now, uri);
        return Task.FromResult(page);
    }
    public Task<PricegunInfo> LoadPricegunAsync(string itemId, CancellationToken ct)
        => Task.FromResult(new PricegunInfo(1300, DateTimeOffset.Now, false));
}

sealed class InMemoryAccountStore : IAccountStore
{
    private AccountConfiguration _config = new() { Accounts = new() };
    public Task<AccountConfiguration> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_config);
    public Task SaveAsync(AccountConfiguration configuration, CancellationToken cancellationToken = default)
    { _config = configuration; return Task.CompletedTask; }
}

sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, AccountCredentials> _secrets = new(StringComparer.OrdinalIgnoreCase);
    public Task<AccountCredentials?> GetAsync(string accountId, CancellationToken cancellationToken = default)
        => Task.FromResult(_secrets.TryGetValue(accountId, out var c) ? c : null);
    public Task SetAsync(string accountId, AccountCredentials credentials, CancellationToken cancellationToken = default)
    { _secrets[accountId] = credentials; return Task.CompletedTask; }
    public Task RemoveAsync(string accountId, CancellationToken cancellationToken = default)
    { _secrets.Remove(accountId); return Task.CompletedTask; }
}
