using System.Text.Json;
using KoLibri.Daily;
using KoLibri.Desktop;
using Xunit;

namespace KoLibri.Tests;

/// <summary>Daily items (toaster, VIP Klaw, Cargo Cultist Shorts, fancy chess set): planning, markers, CLI and desktop rows. FakeKol only.</summary>
public sealed class DailyItemsTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "daily", name));

    private static readonly DailyRunLive Owned = new()
    {
        HasToaster = true, HasVipKey = true, HasCargoShorts = true, HasFancyChessSet = true, Adventures = 50
    };

    private static string[] Ids(IEnumerable<DailyStep> steps) => steps.Where(s => s.Kind == "daily").Select(s => s.Id + ":" + s.Option).ToArray();

    [Fact]
    public void Only_owned_items_are_planned()
    {
        Assert.Empty(Ids(DailyRun.Plan(new DailyRunLive(), new DailyRunOptions { CargoPockets = new[] { 621 }, PlayChess = true })));
        Assert.Empty(DailyItems.Notes(new DailyRunLive()));
        var onlyToaster = Ids(DailyRun.Plan(new DailyRunLive { HasToaster = true }));
        Assert.Equal(new[] { "toaster:1" }, onlyToaster);
    }

    [Fact]
    public void Defaults_plan_toaster_three_klaw_pulls_and_the_pocket_by_value_but_no_chess()
    {
        var ids = Ids(DailyRun.Plan(Owned));
        Assert.Equal(new[] { "toaster:1", "vip-klaw:1", "vip-klaw:2", "vip-klaw:3", "cargo-pocket:auto" }, ids);
        var notes = DailyItems.Notes(Owned);
        Assert.Contains(notes, n => n.Step == "cargo" && n.Text.Contains("nach Wert"));
        Assert.DoesNotContain(Ids(DailyRun.Plan(Owned, new DailyRunOptions { CargoMode = CargoPocketMode.Off })), i => i.StartsWith("cargo", StringComparison.Ordinal));
        Assert.Contains(notes, n => n.Step == "chess" && n.Text.Contains("--chess") && n.Text.Contains("10 Abenteuer"));
    }

    [Fact]
    public void Configured_pockets_and_chess_opt_in_add_steps_and_fight_pockets_are_dropped()
    {
        var options = new DailyRunOptions { CargoPockets = new[] { 565, 621, 54, 621 }, PlayChess = true };
        var ids = Ids(DailyRun.Plan(Owned, options));
        Assert.Contains("cargo-pocket:621,54", ids);
        Assert.Contains("fancy-chess:1", ids);
        Assert.Contains(DailyItems.Notes(Owned, options), n => n.Text.Contains("565 (Kampf: mountain man)"));

        // A list of only fight pockets is no list: the default (by value) applies; it never sends 565.
        var onlyFight = Ids(DailyRun.Plan(Owned, new DailyRunOptions { CargoPockets = new[] { 565 } }));
        Assert.Contains("cargo-pocket:auto", onlyFight);
    }

    [Fact]
    public void Markers_skip_names_and_too_few_adventures_remove_steps()
    {
        var done = Owned with { ToasterDoneToday = true, KlawPullsDoneToday = new[] { 1, 2 }, CargoPocketDoneToday = true, FancyChessDoneToday = true };
        var options = new DailyRunOptions { CargoPockets = new[] { 621 }, PlayChess = true };
        Assert.Equal(new[] { "vip-klaw:3" }, Ids(DailyRun.Plan(done, options)));

        var skip = options with { Skip = new HashSet<string>(DailyItems.StepNames.All, StringComparer.OrdinalIgnoreCase) };
        Assert.Empty(Ids(DailyRun.Plan(Owned, skip)));

        var tired = Owned with { Adventures = 9 };
        Assert.DoesNotContain("fancy-chess:1", Ids(DailyRun.Plan(tired, options)));
        Assert.Contains(DailyItems.Notes(tired, options), n => n.Step == "chess" && n.Text.Contains("mindestens 10"));
    }

    [Fact]
    public void Pocket_list_parsing_and_the_pocket_page()
    {
        Assert.Equal(new[] { 621, 484, 54 }, CargoPockets.Parse("621, 484 54"));
        Assert.Throws<FormatException>(() => CargoPockets.Parse("621,abc"));
        Assert.Throws<FormatException>(() => CargoPockets.Parse("667"));
        var available = CargoPockets.AvailablePockets(Fixture("cargo_pockets.html"));
        Assert.NotNull(available);
        Assert.Equal(new[] { 1, 2, 3, 54, 139, 565, 621 }, available!.OrderBy(p => p));
        Assert.Null(CargoPockets.AvailablePockets(Fixture("toaster.html")));
    }

    [Fact]
    public async Task ReadAsync_sees_items_in_the_inventory_and_worn_cargo_shorts()
    {
        var kol = new FakeKol()
            .On("GET api.php?what=inventory", """{"637":1,"10622":1,"3947":1}""")
            .On("GET api.php?what=status", StatusWithPants(10636));
        await using var client = TestClient.Create(kol);
        await using var session = await client.OpenAsync("main");

        var live = await DailyRun.ReadAsync(session);
        Assert.True(live.HasToaster);
        Assert.True(live.HasFancyChessSet);
        Assert.True(live.HasVipKey);
        Assert.True(live.HasCargoShorts);
        Assert.Equal(50, live.Adventures);
        Assert.Equal(2, kol.Requests.Count(r => r.Path.Contains("api.php", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Toaster_sets_its_marker_only_when_the_toast_is_on_the_page()
    {
        var kol = new FakeKol()
            .On("GET inv_use.php", "<html><body>nothing here</body></html>")
            .On("GET inv_use.php", Fixture("toaster.html"));
        await using var client = TestClient.Create(kol);
        await using var session = await client.OpenAsync("main");
        var step = new[] { new DailyStep("daily", DailyItems.StepIds.Toaster, "1") };

        var unclear = await DailyRun.ExecuteAsync(session, step);
        Assert.False(session.Daily.IsDone(DailyItems.Markers.Toaster));
        Assert.Contains(unclear.FailedSteps, f => f.Contains("unklar", StringComparison.Ordinal));

        var done = await DailyRun.ExecuteAsync(session, step);
        Assert.Empty(done.FailedSteps);
        Assert.Equal(1, done.SubmittedSteps);
        Assert.True(session.Daily.IsDone(DailyItems.Markers.Toaster));
        Assert.Contains(kol.Requests, r => r.Path.Contains("whichitem=637", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Klaw_counts_a_slipped_claw_and_stops_when_KoL_says_greedy()
    {
        var kol = new FakeKol()
            .On("POST clan_viplounge.php", Fixture("kolmafia_clan_klaw.html"))
            .On("POST clan_viplounge.php", Fixture("klaw_greedy.html"));
        await using var client = TestClient.Create(kol);
        await using var session = await client.OpenAsync("main");
        var steps = DailyRun.Plan(new DailyRunLive { HasVipKey = true });

        var result = await DailyRun.ExecuteAsync(session, steps);
        Assert.Equal(1, result.SubmittedSteps);
        Assert.Empty(result.FailedSteps);
        // Pull 1 counted, the greedy answer marks all three; pull 3 is not sent.
        Assert.Equal(2, kol.Count("POST", "clan_viplounge.php"));
        Assert.Equal(3, DailyItems.KlawPullsDone(session.Daily));
        Assert.Contains(kol.Requests, r => r.Method == "POST" && r.Form.Contains("action=klaw", StringComparison.Ordinal));
        Assert.Empty(Ids(DailyRun.Plan(await DailyRun.ReadAsync(session) with { HasVipKey = true })));
    }

    [Fact]
    public async Task Klaw_answer_without_a_pull_sets_no_marker()
    {
        var kol = new FakeKol().On("POST clan_viplounge.php", "<html><body>The lounge is closed.</body></html>");
        await using var client = TestClient.Create(kol);
        await using var session = await client.OpenAsync("main");

        var result = await DailyRun.ExecuteAsync(session, new[] { new DailyStep("daily", DailyItems.StepIds.VipKlaw, "1") });
        Assert.Single(result.FailedSteps);
        Assert.Equal(0, DailyItems.KlawPullsDone(session.Daily));
    }

    [Fact]
    public async Task Cargo_picks_the_first_configured_pocket_that_is_still_full()
    {
        var kol = new FakeKol()
            .On("GET inventory.php", (_, _) => FakeKol.Redirect("https://www.kingdomofloathing.com/choice.php?forceoption=0"))
            .On("GET choice.php", Fixture("cargo_pockets.html"))
            .On("POST choice.php", Fixture("cargo_pick_meat.html"));
        await using var client = TestClient.Create(kol);
        await using var session = await client.OpenAsync("main");
        // 484 is already emptied this ascension (not on the page), 565 would start a fight.
        var steps = DailyRun.Plan(new DailyRunLive { HasCargoShorts = true }, new DailyRunOptions { CargoPockets = new[] { 565, 484, 621 } });

        var result = await DailyRun.ExecuteAsync(session, steps);
        Assert.Empty(result.FailedSteps);
        Assert.Equal(1, result.SubmittedSteps);
        Assert.True(session.Daily.IsDone(DailyItems.Markers.CargoPocket));
        var pick = Assert.Single(kol.Requests, r => r.Method == "POST");
        Assert.Contains("whichchoice=1420", pick.Form);
        Assert.Contains("pocket=621", pick.Form);
        Assert.DoesNotContain("pocket=565", pick.Form);
    }

    [Fact]
    public async Task Cargo_leaves_the_choice_and_sets_no_marker_for_an_empty_pocket()
    {
        var empty = Fixture("cargo_pockets.html").Replace("There appear to be 666", "That pocket is empty.  There appear to be 666", StringComparison.Ordinal);
        var kol = new FakeKol()
            .On("GET inventory.php", Fixture("cargo_pockets.html"))
            .On("POST choice.php", empty)
            .On("POST choice.php", "<html><body>You decide to leave your pockets unplundered for now.</body></html>");
        await using var client = TestClient.Create(kol);
        await using var session = await client.OpenAsync("main");

        var result = await DailyRun.ExecuteAsync(session, new[] { new DailyStep("daily", DailyItems.StepIds.CargoPocket, "54") });
        Assert.Contains(result.FailedSteps, f => f.Contains("empty", StringComparison.Ordinal));
        Assert.False(session.Daily.IsDone(DailyItems.Markers.CargoPocket));
        var posts = kol.Requests.Where(r => r.Method == "POST").ToArray();
        Assert.Equal(2, posts.Length);
        Assert.Contains("option=2", posts[1].Form);
    }

    [Fact]
    public async Task Cargo_exhausted_page_marks_the_day_without_a_pick()
    {
        var kol = new FakeKol().On("GET inventory.php", Fixture("cargo_exhausted.html"));
        await using var client = TestClient.Create(kol);
        await using var session = await client.OpenAsync("main");

        var result = await DailyRun.ExecuteAsync(session, new[] { new DailyStep("daily", DailyItems.StepIds.CargoPocket, "621") });
        Assert.Empty(result.FailedSteps);
        Assert.Equal(0, result.SubmittedSteps);
        Assert.True(session.Daily.IsDone(DailyItems.Markers.CargoPocket));
        Assert.DoesNotContain(kol.Requests, r => r.Method == "POST");
    }

    [Fact]
    public async Task Cargo_with_all_configured_pockets_emptied_sends_no_pick()
    {
        var kol = new FakeKol().On("GET inventory.php", Fixture("cargo_pockets.html"));
        await using var client = TestClient.Create(kol);
        await using var session = await client.OpenAsync("main");

        var result = await DailyRun.ExecuteAsync(session, new[] { new DailyStep("daily", DailyItems.StepIds.CargoPocket, "484,504") });
        Assert.Contains(result.FailedSteps, f => f.Contains("already emptied", StringComparison.Ordinal));
        // No pick; only the choice is closed again (option 2).
        var leave = Assert.Single(kol.Requests, r => r.Method == "POST");
        Assert.Contains("option=2", leave.Form);
        Assert.DoesNotContain("pocket=", leave.Form);
        Assert.False(session.Daily.IsDone(DailyItems.Markers.CargoPocket));
    }

    [Fact]
    public async Task Chess_marks_a_played_game_and_not_a_refusal()
    {
        var kol = new FakeKol()
            .On("GET inv_use.php", Fixture("chess_no_time.html"))
            .On("GET inv_use.php", Fixture("chess_played.html"));
        await using var client = TestClient.Create(kol);
        await using var session = await client.OpenAsync("main");
        var step = new[] { new DailyStep("daily", DailyItems.StepIds.FancyChess, "1") };

        var refused = await DailyRun.ExecuteAsync(session, step);
        Assert.Contains(refused.FailedSteps, f => f.Contains("10 adventures", StringComparison.Ordinal));
        Assert.False(session.Daily.IsDone(DailyItems.Markers.FancyChess));

        var played = await DailyRun.ExecuteAsync(session, step);
        Assert.Empty(played.FailedSteps);
        Assert.True(session.Daily.IsDone(DailyItems.Markers.FancyChess));
        Assert.Contains(kol.Requests, r => r.Path.Contains("whichitem=10622", StringComparison.Ordinal));
    }

    [Fact]
    public void Desktop_briefing_rows_show_owned_items_with_the_saved_settings()
    {
        var settings = new DesktopSettings { DailyCargoPockets = "621,54", DailyCargoMode = "list", DailyPlayChess = false };
        var rows = DailyBriefingViewModel.ItemRows(Owned with { ToasterDoneToday = true }, settings).ToArray();
        Assert.Equal(new[] { "Toaster", "VIP Lounge", "Cargo Shorts", "Chess set" }, rows.Select(r => r.Surface));
        Assert.Equal("done", rows[0].State);
        Assert.Equal("open", rows[1].State);
        Assert.Equal("open", rows[2].State);
        Assert.Contains("621 (917 Meat)", rows[2].Detail);
        Assert.Equal("skipped", rows[3].State);
        Assert.Empty(DailyBriefingViewModel.ItemRows(new DailyRunLive(), settings));

        // A broken saved list picks no pocket.
        var broken = DesktopDailyItems.Options(new DesktopSettings { DailyCargoPockets = "abc" });
        Assert.Empty(broken.CargoPockets);
    }

    [Fact]
    public void Desktop_settings_keep_the_pocket_list_and_the_chess_opt_in()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("kolibri-daily-items-").FullName, "settings.json");
        var store = new DesktopSettingsStore(path);
        store.Save(new DesktopSettings { DailyCargoPockets = "621,484", DailyPlayChess = true });
        var loaded = store.Load();
        Assert.Equal("621,484", loaded.DailyCargoPockets);
        Assert.True(loaded.DailyPlayChess);
    }

    private static string StatusWithPants(int pants) =>
        $$"""{"name":"Tester","playerid":"1001","level":7,"meat":10000,"adventures":50,"hp":100,"maxhp":100,"mp":40,"maxmp":50,"full":0,"fullnessmax":15,"drunk":0,"drunkmax":14,"spleen":0,"spleenmax":15,"pwd":"0123456789abcdef","familiar":"0","equipment":{"hat":"3","pants":"{{pants}}"},"effects":{} }""";
}

/// <summary>daily run preview with the item steps (FakeKol only, nothing sent).</summary>
[Collection("Cli")]
public sealed class CliDailyItemsTests : IDisposable
{
    private readonly string _state = Directory.CreateTempSubdirectory("kolibri-cli-daily-items-").FullName;
    private readonly FakeKol _kol = new();

    public CliDailyItemsTests()
    {
        var store = MemoryAccountStore.WithMain();
        KolibriCli.OptionsOverride = () => new KolibriOptions
        {
            HttpHandler = _kol, AccountStore = store, StateDirectory = _state, DisableLimiterForTests = true
        };
        AgentGuard.TokenRootOverride = Path.Combine(_state, "token");
        _kol.On("GET api.php?what=inventory", """{"637":1,"10622":1,"3947":1,"10636":1}""");
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

    private static string[] Ids(JsonElement json)
        => json.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("id").GetString() + ":" + s.GetProperty("option").GetString()).ToArray();

    private static string? Note(JsonElement json, string step)
        => json.GetProperty("itemNotes").EnumerateArray().Single(n => n.GetProperty("step").GetString() == step)
            .GetProperty("note").GetString();

    [Fact]
    public async Task Preview_lists_the_item_steps_and_notes()
    {
        var (code, json, _) = await Run("run", "--cargo-pocket", "621,54", "--chess");
        Assert.Equal(0, code);
        var ids = Ids(json);
        Assert.Contains("toaster:1", ids);
        Assert.Contains("vip-klaw:3", ids);
        Assert.Contains("cargo-pocket:621,54", ids);
        Assert.Contains("fancy-chess:1", ids);
        var notes = json.GetProperty("itemNotes").EnumerateArray().Select(n => n.GetProperty("step").GetString()).ToArray();
        Assert.Contains("cargo", notes);
        Assert.True(json.GetProperty("live").GetProperty("items").GetProperty("toaster").ValueKind == JsonValueKind.Object);
        Assert.DoesNotContain(_kol.Requests, r => r.Method == "POST" || r.Path.Contains("inv_use", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Only_and_aliases_know_the_item_steps()
    {
        var (code, json, _) = await Run("run", "--only", "cargo", "--cargo-pocket", "621");
        Assert.Equal(0, code);
        Assert.Equal(new[] { "cargo-pocket:621" }, Ids(json));
        foreach (var step in new[] { "toaster", "vip", "chess" })
            Assert.Equal("nicht ausgewählt (--only)", Note(json, step));

        var (c2, j2, err) = await Run("toaster");
        Assert.Equal(0, c2);
        Assert.Contains("daily run --only toaster", err);
        Assert.Equal(new[] { "toaster:1" }, Ids(j2));
        Assert.Equal("nicht ausgewählt (--only)", Note(j2, "cargo"));

        var (c3, j3, _) = await Run("run", "--skip", "vip", "--skip", "toaster", "--cargo-pocket", "none");
        Assert.Equal(0, c3);
        Assert.DoesNotContain(Ids(j3), i => i.StartsWith("vip", StringComparison.Ordinal) || i.StartsWith("toaster", StringComparison.Ordinal));
        Assert.Equal("übersprungen (--skip toaster)", Note(j3, "toaster"));
        Assert.Equal("übersprungen (--skip vip)", Note(j3, "vip"));
    }

    [Fact]
    public async Task Fight_pockets_and_unknown_steps_are_refused()
    {
        var (code, _, err) = await Run("run", "--cargo-pocket", "565");
        Assert.Equal(2, code);
        Assert.Contains("fight", err);
        var (c2, _, e2) = await Run("run", "--skip", "klaw");
        Assert.Equal(2, c2);
        Assert.Contains("--skip takes", e2);
    }
}
