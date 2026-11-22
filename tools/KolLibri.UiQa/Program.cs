using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
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
            Click(window, "InvViewItems");
            await Section(window, "NavMall"); Shot(window, P("mall-empty"));
            await SeedMallResults(window); await Pump(500); Shot(window, P("mall-results"));
            await SeedCloverResults(window); await Pump(500); Shot(window, P("mall-clover"));
            await Section(window, "NavDaily"); Shot(window, P("daily"));
            await Section(window, "NavCommunication"); Shot(window, P("chat-mail"));
            await Section(window, "NavSettings"); Shot(window, P("settings"));
            window.FindControl<Expander>("SessionLogExpander")!.IsExpanded = true;
            await Pump(400); Shot(window, P("settings-session-log"));
            await Section(window, "NavMall");
            Shot(window, P("mall-session-log"));
            window.FindControl<Expander>("SessionLogExpander")!.IsExpanded = false;

            if (w == 1280)
            {
                var dialog = new MallPurchaseDialog(
                    new MallPurchasePromptContext("Mallbot", FixtureOffers()[0], 5),
                    (q, _) => Task.FromResult(new DesktopMallPurchasePreview
                    {
                        ActionId = "qa", Role = "Mallbot", ItemId = "1", ShopId = "100", Quantity = q,
                        RequestedUnitPrice = 1200, UnitPrice = 1200, TotalPrice = 1200L * q, Meat = 50000,
                        RemainingDailyBudget = 20000, RemainingPurchaseBudget = 20000,
                        Problems = Array.Empty<MallPurchaseAbortReason>(), Messages = Array.Empty<string>()
                    }));
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
    // Real KoLmafia ids: hot wing, seal-clubbing club, meat paste, disco ball, Knob Goblin harem veil.
    string[] ItemIds = { "471", "1", "25", "10", "306" };
    var inv = Priv<InventoryViewModel>(w, "_inventory");
    var items = new[] { ("hot wing", 12, new[] { "t", "d" }), ("seal-clubbing club", 1, new[] { "t", "d" }),
        ("Meat paste", 340, new[] { "t", "d" }), ("disco ball", 2, new[] { "t", "d" }), ("Knob Goblin harem veil", 1, new[] { "t", "d" }) };
    inv.ApplySnapshot(items.Select((i, n) => new DesktopInventoryItem
    {
        ItemId = ItemIds[n], Name = i.Item1, Quantity = i.Item2, AccessFlags = i.Item3,
        Uses = new[] { "Use", "Autosell" }, CheapestMallPrice = 100 * (n + 1)
    }).ToList(), DateTimeOffset.Now);
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

async Task SeedMallResults(MainWindow w)
{
    var search = w.FindControl<MallSearchView>("MallSearch")!;
    var model = new MallSearchViewModel(new FixtureBackend(FixtureOffers()));
    model.PurchaseRequest = new AllowAll();
    search.Attach(model, Priv<LiveMallViewModel>(w, "_mall"));
    model.SearchText = "Hot wing";
    await model.SearchAsync();
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
