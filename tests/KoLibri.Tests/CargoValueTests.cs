using System.Text.Json;
using KoLibri.Daily;
using KoLibri.Desktop;
using Xunit;

namespace KoLibri.Tests;

/// <summary>Cargo pocket by value ("--cargo-pocket auto"), the price cache, and the gains of the daily item steps. FakeKol only.</summary>
public sealed class CargoValueTests
{
    private static readonly DateOnly Today = new(2026, 11, 27);

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "daily", name));

    /// <summary>The pocket page of the #109 fixture plus the onyx knight (4) and the onyx king (88).</summary>
    private static string PocketPage(params int[] extra)
    {
        var page = Fixture("cargo_pockets.html");
        var forms = string.Concat(extra.Select(p =>
            $"<form method=\"post\" action=\"choice.php\" style=\"display: inline\"><input type=hidden name=whichchoice value=1420><input type=hidden name=option value=1><input type=hidden name=\"pocket\" value=\"{p}\"><input class=button type=submit value=\"{p}\"></form>"));
        return page.Replace("Which one would you like to look in?<p>", "Which one would you like to look in?<p>" + forms, StringComparison.Ordinal);
    }

    /// <summary>A price cache file with Pricegun prices of <paramref name="day"/>.</summary>
    internal static string PriceJson(DateOnly day, params (int Item, long Price)[] prices)
        => "{" + string.Join(",", prices.Select(p => $"\"{p.Item}\":{{\"price\":{p.Price},\"source\":\"Pricegun\",\"day\":\"{day:yyyy-MM-dd}\"}}")) + "}";

    private static CargoPriceBook Book(string? path = null, ItemPriceLookup? pricegun = null, int lookups = 0)
        => new(path, Today, pricegun, null, lookups);

    [Fact]
    public void The_pocket_table_covers_all_666_pockets_and_the_fight_pockets()
    {
        Assert.Equal(666, CargoPocketValue.All.Count);
        Assert.Equal(28, CargoPocketValue.All.Count(p => p.StartsFight));
        Assert.All(CargoPockets.FightPockets.Keys, p => Assert.True(CargoPocketValue.Pocket(p)!.StartsFight));
        Assert.Equal(917, CargoPocketValue.Pocket(621)!.Meat);
        Assert.Equal("onyx knight", CargoPocketValue.Pocket(4)!.Items.Single().Name);
        Assert.Equal(5, CargoPocketValue.Pocket(167)!.Items.Single().Quantity); // green pixel (5)
    }

    [Fact]
    public async Task An_item_worth_more_than_the_meat_is_chosen()
    {
        var book = Book();
        book.Set(10627, 25_000, "Pricegun"); // onyx knight
        var ranking = await CargoPocketValue.RankAsync(new HashSet<int> { 4, 54, 565, 621, 1, 2 }, book);
        Assert.Equal(4, ranking.Chosen!.Pocket);
        Assert.Equal(25_000, ranking.Chosen.Value);
        Assert.Equal("Pricegun", ranking.Chosen.Source);
        // Fight (565), stat (1) and effect (2) pockets are no candidates.
        Assert.Equal(new[] { 4, 621, 54 }, ranking.Top.Select(c => c.Pocket));
        Assert.True(ranking.PocketsKnown);
    }

    [Fact]
    public async Task On_a_tie_or_without_a_market_price_the_meat_pocket_wins()
    {
        var tie = Book();
        tie.Set(10627, 917, "Pricegun");
        Assert.Equal(621, (await CargoPocketValue.RankAsync(new HashSet<int> { 4, 621 }, tie)).Chosen!.Pocket);

        // No market price: the onyx knight counts its autosell (6 Meat).
        var unknown = await CargoPocketValue.RankAsync(new HashSet<int> { 4, 139 }, Book());
        Assert.Equal(139, unknown.Chosen!.Pocket);
        var knight = unknown.Top.Single(c => c.Pocket == 4);
        Assert.Equal(6, knight.Value);
        Assert.Contains("Autosell", knight.Source);
    }

    [Fact]
    public async Task Units_multiply_the_unit_price()
    {
        var book = Book();
        var pixel = CargoPocketValue.Pocket(167)!.Items.Single();
        book.Set(pixel.ItemId, 300, "Pricegun");
        var ranking = await CargoPocketValue.RankAsync(new HashSet<int> { 167, 577 }, book);
        var c = ranking.Top.First();
        Assert.Equal(167, c.Pocket);
        Assert.Equal(5, c.Quantity);
        Assert.Equal(300, c.UnitPrice);
        Assert.Equal(1_500, c.Value);
    }

    [Fact]
    public async Task Prices_are_cached_per_KoL_day_and_lookups_are_capped()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("kolibri-cargo-prices-").FullName, "cargo-prices.json");
        var asked = new List<int>();
        Task<long?> Lookup(int id, CancellationToken _) { asked.Add(id); return Task.FromResult<long?>(id == 10627 ? 30_000 : 100); }

        var first = await CargoPocketValue.RankAsync(new HashSet<int> { 4, 88, 26, 621 }, Book(path, Lookup, lookups: 2));
        // Two lookups, favourites first (chess pieces before Yeg's Motel items).
        Assert.Equal(2, asked.Count);
        Assert.Equal(new[] { 10623, 10627 }, asked.OrderBy(i => i));
        Assert.Equal(4, first.Chosen!.Pocket);

        asked.Clear();
        var second = await CargoPocketValue.RankAsync(new HashSet<int> { 4, 88, 26, 621 }, Book(path, Lookup, lookups: 1));
        // Only the item without a price today (Yeg's mouthwash) is looked up; the knight comes from the cache.
        Assert.Equal(new[] { 10611 }, asked);
        Assert.Equal(30_000, second.Chosen!.Value);

        // The next day the cached prices are used with their day and refreshed first, best value first.
        asked.Clear();
        var tomorrow = new CargoPriceBook(path, Today.AddDays(1), Lookup, null, 0);
        var stale = await CargoPocketValue.RankAsync(new HashSet<int> { 4 }, tomorrow);
        Assert.Empty(asked);
        Assert.Contains("vom 2026-11-27", stale.Chosen!.Source);
    }

    [Fact]
    public void Gains_are_read_from_the_page()
    {
        Assert.Equal(new[] { "toast" }, DailyGains.Parse(Fixture("toaster.html")));
        Assert.Equal(new[] { "917 Meat" }, DailyGains.Parse(Fixture("cargo_pick_meat.html")));
        Assert.Equal(new[] { "cheap sunglasses" }, DailyGains.Parse(Fixture("klaw_prize.html")));
        Assert.Equal(new[] { "Effekt: Yeg's Glory" }, DailyGains.Parse(Fixture("chess_played.html")));
        Assert.Equal(new[] { "5× green pixels" }, DailyGains.Parse("You acquire <b>5 green pixels</b>"));
        Assert.Empty(DailyGains.Parse(Fixture("kolmafia_clan_klaw.html")));
    }

    [Fact]
    public async Task Auto_reads_the_pocket_page_picks_the_most_valuable_pocket_and_reports_the_gain()
    {
        var state = Directory.CreateTempSubdirectory("kolibri-cargo-auto-").FullName;
        var kol = new FakeKol()
            .On("GET inventory.php", PocketPage(4))
            .On("POST choice.php", "<html><body>You reach into the pocket.<center><table><tr><td>You acquire an item: <b>onyx knight</b></td></tr></table></center></body></html>");
        await using var client = TestClient.Create(kol, state: state);
        await using var session = await client.OpenAsync("main");
        File.WriteAllText(Path.Combine(state, "cargo-prices.json"),
            PriceJson(session.Daily.GameDay, (10627, 25_000)));
        var options = new DailyRunOptions { StateDirectory = state, CargoPriceLookups = 0 };
        var steps = DailyRun.Plan(new DailyRunLive { HasCargoShorts = true }, options);
        Assert.Equal("auto", Assert.Single(steps).Option);

        var result = await DailyRun.ExecuteAsync(session, steps, options);
        Assert.Empty(result.FailedSteps);
        var pick = Assert.Single(kol.Requests, r => r.Method == "POST" && r.Path.Contains("choice.php", StringComparison.Ordinal));
        Assert.Contains("pocket=4", pick.Form);
        // While choice 1420 is open no KoL page other than choice.php is asked (no mall search).
        Assert.DoesNotContain(kol.Requests, r => r.Path.Contains("mall.php", StringComparison.Ordinal));
        var report = Assert.Single(result.Steps);
        Assert.Equal("done", report.State);
        Assert.Equal(new[] { "onyx knight" }, report.Gains);
        Assert.Contains("25,000", report.Message);
        Assert.True(session.Daily.IsDone(DailyItems.Markers.CargoPocket));
        // The live list is kept for the next preview, without the pocket just emptied.
        var saved = CargoPocketState.Load(Path.Combine(state, "cargo-pockets.json"), "main")!;
        Assert.Equal(session.Daily.GameDay, saved.Day);
        Assert.DoesNotContain(4, saved.Pockets);
        Assert.Contains(621, saved.Pockets);
        Assert.DoesNotContain(kol.Requests, r => r.Path.Contains("item/", StringComparison.Ordinal));
    }

    // Live bug after #111: a stale list of 7 pockets (written by a test into the real folder) was ranked, prices were only read
    // for those, and after the live page the onyx knight had no price, so the oyster egg (1,351) won.
    [Fact]
    public async Task Execute_ranks_the_live_list_with_prices_for_it_and_ignores_a_stale_list()
    {
        var state = Directory.CreateTempSubdirectory("kolibri-cargo-live-").FullName;
        File.WriteAllText(Path.Combine(state, "cargo-pockets.json"), """{"main":[1,2,3,54,139,565,621]}""");
        var kol = new FakeKol()
            .On("GET inventory.php", PocketPage(4))
            .On("GET item/10627", "<html><head><title>onyx knight - Pricegun</title></head><body><ol><li><span>11/26/26, 3:02 AM</span>1 @ 25,000</li></ol></body></html>")
            .On("POST choice.php", "<html><body>You acquire an item: <b>onyx knight</b></body></html>");
        await using var client = TestClient.Create(kol, state: state);
        await using var session = await client.OpenAsync("main");
        File.WriteAllText(Path.Combine(state, "cargo-prices.json"), PriceJson(session.Daily.GameDay, (1058, 1_351)));
        var options = new DailyRunOptions { StateDirectory = state, CargoPriceLookups = 3 };

        var result = await DailyRun.ExecuteAsync(session, DailyRun.Plan(new DailyRunLive { HasCargoShorts = true }, options), options);
        Assert.Empty(result.FailedSteps);
        Assert.Contains("pocket=4", Assert.Single(kol.Requests, r => r.Method == "POST" && r.Path.Contains("choice.php", StringComparison.Ordinal)).Form);
        // Pricegun was asked after the live page (it is not KoL), the mall never.
        var pageAt = kol.Requests.FindIndex(r => r.Path.Contains("inventory.php", StringComparison.Ordinal));
        Assert.True(kol.Requests.FindIndex(r => r.Path.Contains("item/10627", StringComparison.Ordinal)) > pageAt);
        Assert.DoesNotContain(kol.Requests, r => r.Path.Contains("mall.php", StringComparison.Ordinal));
        Assert.Contains("25,000", result.Steps.Single().Message);
    }

    [Fact]
    public async Task Preview_uses_a_list_from_today_or_yesterday_and_reports_its_source()
    {
        var state = Directory.CreateTempSubdirectory("kolibri-cargo-preview-").FullName;
        var file = Path.Combine(state, "cargo-pockets.json");
        await using var client = TestClient.Create(new FakeKol(), state: state);
        await using var session = await client.OpenAsync("main");
        var today = session.Daily.GameDay;
        var options = new DailyRunOptions { StateDirectory = state, CargoPriceLookups = 0 };

        CargoPocketState.Save(file, "main", today.AddDays(-1), new HashSet<int> { 54, 621 });
        var yesterday = await DailyItems.RankCargoAsync(session, options);
        Assert.Equal($"cache {today.AddDays(-1):yyyy-MM-dd}", yesterday.PocketsSource);
        Assert.Equal(new[] { 621, 54 }, yesterday.Top.Select(c => c.Pocket));

        CargoPocketState.Save(file, "main", today.AddDays(-3), new HashSet<int> { 54, 621 });
        var old = await DailyItems.RankCargoAsync(session, options);
        Assert.Equal(CargoPocketState.UnknownSource, old.PocketsSource);
        Assert.False(old.PocketsKnown);

        File.WriteAllText(file, """{"main":[1,2,3,54,139,565,621]}"""); // format of #111, no day
        Assert.Equal(CargoPocketState.UnknownSource, (await DailyItems.RankCargoAsync(session, options)).PocketsSource);
        // Without a folder nothing is read: unknown.
        Assert.Equal(CargoPocketState.UnknownSource, (await DailyItems.RankCargoAsync(session, priceLookups: 0)).PocketsSource);
    }

    [Fact]
    public async Task A_run_without_a_state_folder_writes_nothing_to_the_real_KoLibri_folder()
    {
        var real = Path.Combine(DailyItems.DefaultStateDirectory(), "cargo-pockets.json");
        var before = File.Exists(real) ? File.GetLastWriteTimeUtc(real) : (DateTime?)null;
        var kol = new FakeKol()
            .On("GET inventory.php", Fixture("cargo_pockets.html"))
            .On("POST choice.php", Fixture("cargo_pick_meat.html"));
        await using var client = TestClient.Create(kol);
        await using var session = await client.OpenAsync("main");

        var result = await DailyRun.ExecuteAsync(session, new[] { new DailyStep("daily", DailyItems.StepIds.CargoPocket, "621") });
        Assert.Empty(result.FailedSteps);
        Assert.Equal(before, File.Exists(real) ? File.GetLastWriteTimeUtc(real) : (DateTime?)null);
    }

    [Fact]
    public async Task Pricegun_prices_come_through_the_session_without_a_KoL_request()
    {
        var kol = new FakeKol().On("GET item/10627",
            "<html><head><title>onyx knight - Pricegun</title></head><body><ol><li><span>11/26/26, 3:02 AM</span>2 @ 40,000</li></ol></body></html>");
        await using var client = TestClient.Create(kol);
        await using var session = await client.OpenAsync("main");
        var book = DailyItems.PriceBook(session, null, 1);
        // Without an explicit folder nothing is read or written.
        Assert.Null(DailyItems.StateFile(null, DailyItems.PriceCacheFile));
        Assert.EndsWith("KoLibri", DailyItems.DefaultStateDirectory());

        await book.PriceAsync(new[] { 10627 }, CancellationToken.None);
        Assert.Equal(40_000, book.Get(10627)!.UnitPrice);
        Assert.Equal(1, book.Lookups);
        Assert.DoesNotContain(kol.Requests, r => r.Path.Contains(".php", StringComparison.Ordinal) && !r.Path.Contains("api.php", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_reports_the_toaster_and_klaw_gains()
    {
        var kol = new FakeKol()
            .On("GET inv_use.php", Fixture("toaster.html"))
            .On("POST clan_viplounge.php", Fixture("klaw_prize.html"));
        await using var client = TestClient.Create(kol);
        await using var session = await client.OpenAsync("main");
        var steps = new[] { new DailyStep("daily", DailyItems.StepIds.Toaster, "1"), new DailyStep("daily", DailyItems.StepIds.VipKlaw, "1") };

        var result = await DailyRun.ExecuteAsync(session, steps);
        Assert.Equal(new[] { "toast", "cheap sunglasses" }, result.Steps.SelectMany(s => s.Gains));
        Assert.Contains("Erhalten: toast, cheap sunglasses", DailyRunReport.Describe(result));
    }

    [Fact]
    public void Desktop_cargo_mode_setting_and_top_rows()
    {
        var auto = DesktopDailyItems.Options(new DesktopSettings { DailyCargoPockets = "621" });
        Assert.Equal(CargoPocketMode.Auto, DailyItems.CargoModeOf(auto));
        var list = DesktopDailyItems.Options(new DesktopSettings { DailyCargoPockets = "621", DailyCargoMode = "list" });
        Assert.Equal(CargoPocketMode.List, DailyItems.CargoModeOf(list));
        var off = DesktopDailyItems.Options(new DesktopSettings { DailyCargoMode = "off" });
        Assert.Equal(CargoPocketMode.Off, DailyItems.CargoModeOf(off));

        var chosen = new CargoCandidate(4, "onyx knight", 1, 25_000, 25_000, "Pricegun", false);
        var ranking = new CargoRanking(new[] { chosen, new CargoCandidate(621, "917 Meat", 917, 1, 917, "Meat (fest)", true) }, chosen, true, 3, null);
        var rows = DailyBriefingViewModel.CargoRows(ranking with { PocketsSource = "cache 2026-11-26" }).ToArray();
        Assert.Equal(3, rows.Length);
        Assert.Equal("cache 2026-11-26", rows[0].Detail);
        Assert.Equal("chosen", rows[1].State);
        Assert.Contains("Tasche 4: onyx knight", rows[1].Description);
        Assert.Contains("1 × 25,000 = 25,000 Meat (Pricegun)", rows[1].Detail);
        Assert.Equal("candidate", rows[2].State);
    }

    [Fact]
    public void Desktop_settings_from_109_keep_their_list_and_new_ones_default_to_auto()
    {
        var dir = Directory.CreateTempSubdirectory("kolibri-cargo-mode-").FullName;
        var old = Path.Combine(dir, "old.json");
        File.WriteAllText(old, """{"dailyCargoPockets":"621,484"}""");
        Assert.Equal("list", new DesktopSettingsStore(old).Load().DailyCargoMode);
        var fresh = Path.Combine(dir, "fresh.json");
        File.WriteAllText(fresh, "{}");
        Assert.Equal("auto", new DesktopSettingsStore(fresh).Load().DailyCargoMode);
        var store = new DesktopSettingsStore(Path.Combine(dir, "round.json"));
        store.Save(new DesktopSettings { DailyCargoMode = "off" });
        Assert.Equal("off", store.Load().DailyCargoMode);
    }
}

/// <summary>daily run preview with --cargo-pocket auto (FakeKol, nothing sent).</summary>
[Collection("Cli")]
public sealed class CliCargoValueTests : IDisposable
{
    private readonly string _state = Directory.CreateTempSubdirectory("kolibri-cli-cargo-").FullName;
    private readonly FakeKol _kol = new();

    public CliCargoValueTests()
    {
        var store = MemoryAccountStore.WithMain();
        KolibriCli.OptionsOverride = () => new KolibriOptions
        {
            HttpHandler = _kol, AccountStore = store, StateDirectory = _state, DisableLimiterForTests = true
        };
        AgentGuard.TokenRootOverride = Path.Combine(_state, "token");
        _kol.On("GET api.php?what=inventory", """{"10636":1}""");
    }

    public void Dispose()
    {
        KolibriCli.OptionsOverride = null;
        AgentGuard.TokenRootOverride = null;
    }

    private static async Task<(int Code, JsonElement Json, string Error)> Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var (oldOut, oldErr) = (Console.Out, Console.Error);
        Console.SetOut(output);
        Console.SetError(error);
        int code;
        try { code = await KolibriCli.DailyCommandAsync(args); }
        finally { Console.SetOut(oldOut); Console.SetError(oldErr); }
        var text = output.ToString().Trim();
        var json = text.StartsWith('{') ? JsonDocument.Parse(text).RootElement.Clone() : default;
        return (code, json, error.ToString());
    }

    [Fact]
    public async Task Preview_shows_the_top_five_and_the_chosen_pocket_without_opening_the_choice()
    {
        var day = KolDay.Of(DateTimeOffset.UtcNow);
        File.WriteAllText(Path.Combine(_state, "cargo-prices.json"),
            CargoValueTests.PriceJson(day, (10627, 25_000), (10623, 12_000)));
        CargoPocketState.Save(Path.Combine(_state, "cargo-pockets.json"), "main", day, new HashSet<int> { 4, 88, 54, 139, 621, 577, 565, 1 });

        var (code, json, _) = await Run("run", "--only", "cargo", "--cargo-price-lookups", "0");
        Assert.Equal(0, code);
        var step = Assert.Single(json.GetProperty("steps").EnumerateArray());
        Assert.Equal("auto", step.GetProperty("option").GetString());
        var cargo = json.GetProperty("cargo");
        Assert.True(cargo.GetProperty("pocketsKnown").GetBoolean());
        Assert.Equal($"cache {day:yyyy-MM-dd}", cargo.GetProperty("pocketsSource").GetString());
        Assert.Equal(4, cargo.GetProperty("chosen").GetProperty("pocket").GetInt32());
        var top = cargo.GetProperty("top").EnumerateArray().Select(t => t.GetProperty("pocket").GetInt32()).ToArray();
        Assert.Equal(new[] { 4, 88, 621, 54, 139 }, top);
        var first = cargo.GetProperty("top")[0];
        Assert.Equal("onyx knight", first.GetProperty("content").GetString());
        Assert.Equal(25000, first.GetProperty("unitPrice").GetInt64());
        Assert.Equal("Pricegun", first.GetProperty("source").GetString());
        Assert.DoesNotContain(_kol.Requests, r => r.Method == "POST" || r.Path.Contains("inventory.php", StringComparison.Ordinal));
    }

    [Fact]
    public async Task None_turns_the_pocket_off_and_a_list_wins_over_auto()
    {
        var (c1, j1, _) = await Run("run", "--only", "cargo", "--cargo-pocket", "none");
        Assert.Equal(0, c1);
        Assert.Empty(j1.GetProperty("steps").EnumerateArray());
        Assert.Equal("off", j1.GetProperty("cargo").GetProperty("mode").GetString());

        var (c2, j2, _) = await Run("run", "--only", "cargo", "--cargo-pocket", "621,484");
        Assert.Equal(0, c2);
        Assert.Equal("621,484", Assert.Single(j2.GetProperty("steps").EnumerateArray()).GetProperty("option").GetString());
        Assert.Equal("list", j2.GetProperty("cargo").GetProperty("mode").GetString());

        var (c3, _, e3) = await Run("run", "--cargo-pocket", "best");
        Assert.Equal(2, c3);
        Assert.Contains("auto, none or pocket numbers", e3);
    }
}
