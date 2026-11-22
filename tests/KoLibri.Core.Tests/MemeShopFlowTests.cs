using System.Net;
using Xunit;
using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Tests;

/// <summary>
/// The Internet Meme Shop flow against recorded-shape pages: purchase success needs KoL's own
/// confirmation, a refused buy must not mark the day as done, and the BACON machine works even
/// when the closed shop page carries no request hash.
/// </summary>
public sealed class MemeShopFlowTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef";

    private static readonly AccountCredentials Credentials = new() { CookieHeader = "session=1", AccessToken = "not-the-hash" };

    private static string ShopPage(int bacon, params (string name, int row)[] rows)
        => "<table>" + string.Concat(rows.Select(r =>
            $"<tr rel=\"1\"><td><input type=radio name=whichrow value={r.row}></td><td><b>{r.name}</b></td>" +
            $"<td><input class=\"button doit\" type=button rel='shop.php?whichshop=bacon&action=buyitem&quantity=1&whichrow={r.row}&pwd={Hash}' value='Buy'></td></tr>"))
           + $"</table><p>You have: <b>{bacon}</b> BACON</p>";

    private sealed class FakeKoL : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();
        public string ShopHtml { get; set; } = "";
        public string InventoryHtml { get; set; } = "<a href='x.php?pwd=" + Hash + "'>menu</a>";
        public string BuyResponse { get; set; } = "";
        public HttpStatusCode MachineStatus { get; set; } = HttpStatusCode.Found;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.PathAndQuery;
            Requests.Add(request.Method + " " + url);
            HttpResponseMessage Reply(HttpStatusCode code, string body, string? location = null)
            {
                var response = new HttpResponseMessage(code) { Content = new StringContent(body) };
                if (location is not null) response.Headers.Location = new Uri(location, UriKind.Relative);
                return response;
            }
            if (url.StartsWith("/shop.php?whichshop=bacon&action=buyitem"))
                return Task.FromResult(Reply(HttpStatusCode.OK, BuyResponse));
            if (url.StartsWith("/shop.php"))
                return Task.FromResult(Reply(HttpStatusCode.OK, ShopHtml));
            if (url.StartsWith("/inv_use.php"))
                return Task.FromResult(MachineStatus == HttpStatusCode.Found
                    ? Reply(HttpStatusCode.Found, "", "inventory.php?which=3&action=message")
                    : Reply(MachineStatus, "You push the button, but nothing happens."));
            if (url.StartsWith("/inventory.php") || url.StartsWith("/charpane.php"))
                return Task.FromResult(Reply(HttpStatusCode.OK, InventoryHtml));
            return Task.FromResult(Reply(HttpStatusCode.NotFound, ""));
        }
    }

    private static (FreePullDispatcher dispatcher, FakeKoL kol) Create()
    {
        var kol = new FakeKoL();
        return (new FreePullDispatcher(kol, new Uri("https://kol.test/")), kol);
    }

    private static DailyDoneStore TempStore(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), $"meme-{Guid.NewGuid():N}.json");
        return new DailyDoneStore(path);
    }

    [Fact]
    public async Task Buy_uses_a_get_with_the_row_and_the_pages_hash_and_marks_the_day_only_on_confirmation()
    {
        var (dispatcher, kol) = Create();
        kol.ShopHtml = ShopPage(455, ("viral video", 821), ("plus one", 824));
        kol.BuyResponse = "You acquire an item: <b>viral video</b>";
        var store = TempStore(out var path);
        try
        {
            var result = await AutoCombatDailyBridge.ExecuteMemeStepAsync(
                new ChoiceStepSpec(DailyInternetShopPlanner.BuyChoiceId, "viral video"),
                dispatcher, Credentials, store, "Main", CancellationToken.None);

            Assert.True(result.Accepted);
            Assert.Contains($"GET /shop.php?whichshop=bacon&action=buyitem&quantity=1&whichrow=821&pwd={Hash}", kol.Requests);
            Assert.DoesNotContain(kol.Requests, r => r.StartsWith("POST"));
            Assert.True(store.IsDone("Main", DailyDoneStore.MemeBuy("viral video")));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task A_refused_buy_is_reported_and_does_not_mark_the_day()
    {
        var (dispatcher, kol) = Create();
        kol.ShopHtml = ShopPage(455, ("viral video", 821));
        kol.BuyResponse = "<html>You can't afford that.</html>";
        var store = TempStore(out var path);
        try
        {
            var result = await AutoCombatDailyBridge.ExecuteMemeStepAsync(
                new ChoiceStepSpec(DailyInternetShopPlanner.BuyChoiceId, "viral video"),
                dispatcher, Credentials, store, "Main", CancellationToken.None);

            Assert.False(result.Accepted);
            Assert.False(store.IsDone("Main", DailyDoneStore.MemeBuy("viral video")));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task An_item_missing_from_the_shop_counts_as_bought_today_and_sends_no_buy()
    {
        var (dispatcher, kol) = Create();
        kol.ShopHtml = ShopPage(455, ("plus one", 824));
        var store = TempStore(out var path);
        try
        {
            var result = await AutoCombatDailyBridge.ExecuteMemeStepAsync(
                new ChoiceStepSpec(DailyInternetShopPlanner.BuyChoiceId, "viral video"),
                dispatcher, Credentials, store, "Main", CancellationToken.None);

            Assert.False(result.Accepted);
            Assert.DoesNotContain(kol.Requests, r => r.Contains("action=buyitem"));
            Assert.True(store.IsDone("Main", DailyDoneStore.MemeBuy("viral video")));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task Too_little_bacon_sends_no_buy()
    {
        var (dispatcher, kol) = Create();
        kol.ShopHtml = ShopPage(50, ("gallon of milk", 825));
        var result = await AutoCombatDailyBridge.ExecuteMemeStepAsync(
            new ChoiceStepSpec(DailyInternetShopPlanner.BuyChoiceId, "gallon of milk"),
            dispatcher, Credentials, null, "Main", CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Contains("50", result.Message);
        Assert.DoesNotContain(kol.Requests, r => r.Contains("action=buyitem"));
    }

    [Fact]
    public async Task Machine_works_when_the_closed_shop_page_has_no_hash_and_a_redirect_means_success()
    {
        var (dispatcher, kol) = Create();
        kol.ShopHtml = "<html>Uh Oh! Where's the bacon?</html>";
        var store = TempStore(out var path);
        try
        {
            var result = await AutoCombatDailyBridge.ExecuteMemeStepAsync(
                new ChoiceStepSpec(DailyInternetShopPlanner.MachineChoiceId, "1"),
                dispatcher, Credentials, store, "Mallbot", CancellationToken.None);

            Assert.True(result.Accepted);
            Assert.Contains($"GET /inv_use.php?which=3&whichitem=9025&pwd={Hash}", kol.Requests);
            Assert.True(store.IsDone("Mallbot", DailyDoneStore.BaconMachine));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task A_second_push_that_does_nothing_still_marks_the_machine_as_used()
    {
        var (dispatcher, kol) = Create();
        kol.ShopHtml = ShopPage(455, ("viral video", 821));
        kol.MachineStatus = HttpStatusCode.OK;
        var store = TempStore(out var path);
        try
        {
            var result = await AutoCombatDailyBridge.ExecuteMemeStepAsync(
                new ChoiceStepSpec(DailyInternetShopPlanner.MachineChoiceId, "1"),
                dispatcher, Credentials, store, "Main", CancellationToken.None);

            Assert.False(result.Accepted);
            Assert.True(store.IsDone("Main", DailyDoneStore.BaconMachine));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Bacon_shortfall_counts_unclaimed_items_and_the_machine_yield()
    {
        // All five daily items cost 455 BACON together.
        Assert.Equal(455, DailyInternetShopPlanner.BaconShortfall(Array.Empty<string>(), 0, false, false));
        Assert.Equal(355, DailyInternetShopPlanner.BaconShortfall(Array.Empty<string>(), 0, true, false));
        Assert.Equal(455, DailyInternetShopPlanner.BaconShortfall(Array.Empty<string>(), 0, true, true));
        Assert.Equal(0, DailyInternetShopPlanner.BaconShortfall(Array.Empty<string>(), 500, false, false));
        Assert.Equal(361, DailyInternetShopPlanner.BaconShortfall(new[] { "viral video", "plus one" }, 0, false, false));
    }
}
