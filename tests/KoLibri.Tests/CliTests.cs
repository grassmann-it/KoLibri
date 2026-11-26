using System.Text.Json;
using KoLibri.Daily;
using Xunit;
using CliRole = KoLibri.Cli.AccountRole;

namespace KoLibri.Tests;

/// <summary>Console is process-wide: CLI tests run one after another.</summary>
[CollectionDefinition("Cli", DisableParallelization = true)]
public sealed class CliCollection;

/// <summary>The CLI on the KoLibri API, against the fake KoL (no KoLibri.Core, no network, no user files).</summary>
[Collection("Cli")]
public sealed class CliTests : IDisposable
{
    private readonly string _state = Directory.CreateTempSubdirectory("kolibri-cli-").FullName;
    private readonly FakeKol _kol = new();
    private readonly MemoryAccountStore _store = MemoryAccountStore.WithMain();

    public CliTests()
    {
        KolibriCli.OptionsOverride = () => new KolibriOptions
        {
            HttpHandler = _kol,
            AccountStore = _store,
            StateDirectory = _state,
            DisableLimiterForTests = true
        };
        AgentGuard.TokenRootOverride = Path.Combine(_state, "token");
    }

    public void Dispose()
    {
        KolibriCli.OptionsOverride = null;
        AgentGuard.TokenRootOverride = null;
    }

    private static async Task<(int Code, JsonElement Json, string Error)> Run(Func<string[], Task<int>> command, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var (oldOut, oldErr) = (Console.Out, Console.Error);
        Console.SetOut(output);
        Console.SetError(error);
        int code;
        try { code = await command(args); }
        finally { Console.SetOut(oldOut); Console.SetError(oldErr); }
        var text = output.ToString().Trim();
        var json = text.StartsWith('{') ? JsonDocument.Parse(text).RootElement.Clone() : default;
        return (code, json, error.ToString());
    }

    private Task<(int Code, JsonElement Json, string Error)> Cli(params string[] args)
        => Run(a => KolibriCli.RunAsync(a, _ => null), args);

    private async Task<string> MintTokenAsync()
    {
        var (code, json, _) = await Cli("token", "mint");
        Assert.Equal(0, code);
        return json.GetProperty("token").GetString()!;
    }

    [Fact]
    public async Task Status_keeps_the_0_4_field_names()
    {
        var (code, json, _) = await Cli("status", "--role", "main", "--refresh");

        Assert.Equal(0, code);
        Assert.Equal("Tester", json.GetProperty("playerName").GetString());
        Assert.Equal(50, json.GetProperty("adventures").GetInt32());
        Assert.Equal(100, json.GetProperty("currentHp").GetInt32());
        Assert.Equal(15, json.GetProperty("fullnessLimit").GetInt32());
        Assert.Equal("1001", json.GetProperty("playerId").GetString());
        Assert.Equal("3", json.GetProperty("equipment").GetProperty("Hat").GetString());
    }

    [Fact]
    public async Task Inventory_lists_item_ids_as_strings_with_names()
    {
        _kol.On("GET api.php?what=inventory", """{"2":3}""");
        var (code, json, _) = await Cli("inventory", "--role", "main");

        Assert.Equal(0, code);
        var item = json.GetProperty("items")[0];
        Assert.Equal("2", item.GetProperty("itemId").GetString());
        Assert.Equal("seal tooth", item.GetProperty("name").GetString());
        Assert.Equal(3, item.GetProperty("quantity").GetInt32());
    }

    [Fact]
    public async Task An_unknown_role_account_says_how_to_log_in()
    {
        _store.DeleteAsync(new AccountId("main"), default).GetAwaiter().GetResult();
        var (code, _, error) = await Cli("status", "--role", "main");
        Assert.Equal(1, code);
        Assert.Contains("login --role main", error);
    }

    [Fact]
    public async Task Commands_not_in_0_5_dev_send_nothing_and_exit_3()
    {
        foreach (var args in new[] { new[] { "repl" }, new[] { "mafia-script", "x.txt" }, new[] { "adventure", "start" }, new[] { "daily", "go" },
                     new[] { "combat", "preview" }, new[] { "--experimental", "watchlist", "list" }, new[] { "--experimental", "reprice" } })
        {
            var (code, _, error) = await Cli(args);
            Assert.Equal(3, code);
            Assert.Contains("follow-up PR", error);
        }
        Assert.Empty(_kol.Requests);
    }

    [Fact]
    public async Task Experimental_gate_still_hides_market_commands()
    {
        var (code, _, error) = await Cli("pricegun", "--item", "2");
        Assert.Equal(3, code);
        Assert.Contains("experimental", error);
        Assert.Empty(_kol.Requests);
    }

    [Fact]
    public async Task Session_probe_separates_rejection_from_unreachable()
    {
        await using var client = KolibriCli.CreateClient();
        await using var session = await client.OpenAsync("main");
        Assert.Equal(KolibriCli.SessionProbe.Works, await KolibriCli.ProbeSessionAsync(session));
        _kol.On("GET api.php?what=status", (_, _) => FakeKol.Html("", System.Net.HttpStatusCode.ServiceUnavailable));
        Assert.Equal(KolibriCli.SessionProbe.Unreachable, await KolibriCli.ProbeSessionAsync(session));
        _kol.On("GET api.php?what=status", (_, _) => FakeKol.Redirect("https://www.kingdomofloathing.com/login.php"));
        Assert.Equal(KolibriCli.SessionProbe.Rejected, await KolibriCli.ProbeSessionAsync(session));
        _kol.On("GET api.php?what=status", (_, _) => FakeKol.Html("", System.Net.HttpStatusCode.Forbidden));
        Assert.Equal(KolibriCli.SessionProbe.Rejected, await KolibriCli.ProbeSessionAsync(session));
        Assert.Equal(0, _kol.Count("POST", "login.php"));
    }

    [Fact]
    public void Status_row_does_not_call_unreachable_a_rejection_and_has_no_loggedInAt()
    {
        var account = new Account(new AccountId("main"), "x", 1, null, Array.Empty<string>()) { HasSession = true };
        var json = JsonSerializer.Serialize(KolibriCli.LoginStatusRow(CliRole.Main, account, KolibriCli.SessionProbe.Unreachable));
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("rejectedByServer").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("serverUnreachable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("usable").ValueKind);
        Assert.False(doc.RootElement.TryGetProperty("loggedInAt", out _));
        json = JsonSerializer.Serialize(KolibriCli.LoginStatusRow(CliRole.Main, account, KolibriCli.SessionProbe.Rejected));
        using var doc2 = JsonDocument.Parse(json);
        Assert.True(doc2.RootElement.GetProperty("rejectedByServer").GetBoolean());
        Assert.False(doc2.RootElement.GetProperty("usable").GetBoolean());
    }

    [Fact]
    public async Task Login_clear_forgets_cookie_and_password_and_keeps_the_account()
    {
        var (code, json, _) = await Cli("login", "clear", "--role", "all");
        Assert.Equal(0, code);
        Assert.Equal(new[] { "Main" }, json.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.Null(_store.Records["main"].CookieHeader);
        Assert.Equal("Tester", _store.Records["main"].Account.PlayerName);
    }

    [Fact]
    public async Task Consume_refused_by_the_organ_check_sends_nothing_and_exits_3()
    {
        _kol.Fullness = 15;
        var (code, json, _) = await Run(KolibriCli.ConsumeAsync, "--role", "main", "--kind", "food", "--item", "61", "--qty", "1", "--execute");
        Assert.Equal(3, code);
        Assert.Equal(0, json.GetProperty("consumed").GetInt32());
        Assert.Contains("Nothing was sent", json.GetProperty("rejection").GetString());
        Assert.Equal(15, json.GetProperty("before").GetProperty("fullness").GetInt32());
        Assert.Equal(0, _kol.Count("POST", "inv_eat.php"));
    }

    [Fact]
    public async Task Adventure_run_without_a_valid_token_sends_nothing()
    {
        await MintTokenAsync();
        var refused = await Assert.ThrowsAsync<ArgumentException>(() => Run(KolibriCli.AdventureCommandAsync,
            "run", "--location", "113", "--execute", "--approval-token", "wrong"));
        Assert.Contains("approval token", refused.Message);
        Assert.Empty(_kol.Requests);
    }

    [Fact]
    public async Task Adventure_run_answers_the_choice_from_the_command_line_and_reports_turns()
    {
        var token = await MintTokenAsync();
        _kol.StatusAdventures = 10;
        _kol.On("POST adventure.php", (_, _) => FakeKol.Redirect("https://www.kingdomofloathing.com/choice.php"))
            .On("GET choice.php", ActionTests.ChoicePage)
            .On("POST choice.php", (_, _) => { _kol.StatusAdventures--; return FakeKol.Html(ActionTests.ChoiceDone); });

        var (code, json, _) = await Run(KolibriCli.AdventureCommandAsync,
            "run", "--location", "adventure=113:The Haunted Pantry", "--max-turns", "2", "--choice-action", "1234:2",
            "--execute", "--approval-token", token);

        Assert.Equal(0, code);
        Assert.Equal("Complete", json.GetProperty("stopReason").GetString());
        Assert.Equal(2, json.GetProperty("turnsDone").GetInt32());
        Assert.Equal(2, json.GetProperty("autoChoices").GetArrayLength());
        var turn = json.GetProperty("turns")[0];
        Assert.Equal(10, turn.GetProperty("adventuresBefore").GetInt32());
        Assert.Equal(9, turn.GetProperty("adventuresAfter").GetInt32());
    }

    [Fact]
    public async Task Adventure_run_stops_at_an_unknown_choice_with_exit_4_and_the_resume_command()
    {
        var token = await MintTokenAsync();
        _kol.On("POST adventure.php", (_, _) => FakeKol.Redirect("https://www.kingdomofloathing.com/choice.php"))
            .On("GET choice.php", ActionTests.ChoicePage);

        var (code, json, _) = await Run(KolibriCli.AdventureCommandAsync,
            "run", "--location", "113", "--execute", "--approval-token", token);

        Assert.Equal(4, code);
        Assert.Equal("NeedsDecision", json.GetProperty("stopReason").GetString());
        var needs = json.GetProperty("needsDecision");
        Assert.Equal("1234", needs.GetProperty("choiceId").GetString());
        Assert.Contains("kolibri choice submit --role main --choice 1234", needs.GetProperty("resumeCommand").GetString());
        Assert.Equal(0, _kol.Count("POST", "choice.php"));
    }

    [Fact]
    public async Task Adventure_run_fights_with_attack_by_default()
    {
        var token = await MintTokenAsync();
        _kol.On("POST adventure.php", (_, _) => FakeKol.Redirect("https://www.kingdomofloathing.com/fight.php"))
            .On("GET fight.php", ActionTests.FightRound1)
            .On("POST fight.php", (_, form) => { Assert.Equal("action=attack", form); _kol.StatusAdventures--; return FakeKol.Html(ActionTests.FightWon); });

        var (code, json, _) = await Run(KolibriCli.AdventureCommandAsync, "run", "--location", "113", "--execute", "--approval-token", token);

        Assert.Equal(0, code);
        Assert.Equal("Skeleton", json.GetProperty("turns")[0].GetProperty("monster").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("openFight").ValueKind);
    }

    [Fact]
    public async Task Choice_submit_for_another_choice_sends_nothing_and_exits_3()
    {
        var token = await MintTokenAsync();
        _kol.On("GET main.php", (_, _) => FakeKol.Redirect("https://www.kingdomofloathing.com/choice.php")).On("GET choice.php", ActionTests.ChoicePage);
        var (code, json, _) = await Run(KolibriCli.ChoiceCommandAsync, "submit", "--choice", "999", "--option", "1", "--execute", "--approval-token", token);
        Assert.Equal(3, code);
        Assert.False(json.GetProperty("executed").GetBoolean());
        Assert.Equal("1234", json.GetProperty("next").GetProperty("choiceId").GetString());
        Assert.Equal(0, _kol.Count("POST", "choice.php"));
    }

    [Fact]
    public async Task Choice_pending_reports_an_open_choice_with_exit_4()
    {
        _kol.On("GET main.php", (_, _) => FakeKol.Redirect("https://www.kingdomofloathing.com/choice.php")).On("GET choice.php", ActionTests.ChoicePage);
        var (code, json, _) = await Cli("choice", "pending", "--role", "main");
        Assert.Equal(4, code);
        Assert.Equal("Choice", json.GetProperty("result").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Daily_run_preview_plans_from_live_numbers_without_sending()
    {
        _kol.On("GET api.php?what=inventory", """{"11341":230,"8763":120,"9025":1,"43":2,"10881":1}""");
        var (code, json, _) = await Cli("daily", "run", "--role", "main");

        Assert.Equal(0, code);
        Assert.True(json.GetProperty("previewOnly").GetBoolean());
        var ids = json.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("id").GetString()).ToArray();
        Assert.Contains("choice-campfire", ids);
        Assert.Contains("choice-day-shortner", ids);
        Assert.Contains("worthless-item", ids);
        Assert.Contains(DailyInternetShopPlanner.MachineChoiceId, ids);
        Assert.Equal(230, json.GetProperty("live").GetProperty("leaves").GetInt32());
        Assert.DoesNotContain(_kol.Requests, r => r.Method == "POST");
    }

    [Fact]
    public async Task Daily_run_finds_the_markers_the_0_4_cli_wrote_under_the_player_id()
    {
        // As in the live store of 0.4: the profile has no player id, only the status reports it ("playerid":"1001").
        var stored = _store.Records["main"];
        await _store.SaveAsync(stored with { Account = stored.Account with { PlayerId = null } }, default);
        string today;
        await using (var client = KolibriCli.CreateClient())
        await using (var session = await client.OpenAsync("main"))
            today = session.Daily.GameDay.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var keys = new[] { "campfire-leaf", "day-shortener", "hermit-clovers", "bacon-machine", "meme-buy:viral video", "meme-buy:plus one" };
        var markers = keys.ToDictionary(k => "main:1001|" + k, _ => today);
        markers["mallbot:2002|campfire-leaf"] = today;
        // The file the 0.4 DailyDoneStore wrote (same name and JSON shape; KolibriOptions.StateDirectory points here).
        File.WriteAllText(Path.Combine(_state, "daily-done.json"), JsonSerializer.Serialize(markers));
        _kol.On("GET api.php?what=inventory", """{"11341":230,"8763":400,"9025":1,"42":3,"10881":1}""");

        var (code, json, _) = await Cli("daily", "run", "--role", "main", "--hermit-clovers", "3");

        Assert.Equal(0, code);
        var live = json.GetProperty("live");
        Assert.True(live.GetProperty("dayShortenerDoneToday").GetBoolean());
        Assert.True(live.GetProperty("baconMachineUsedToday").GetBoolean());
        Assert.Equal(new[] { "viral video", "plus one" }, live.GetProperty("memeItemsDoneToday").EnumerateArray().Select(e => e.GetString()));
        var steps = json.GetProperty("steps").EnumerateArray()
            .Select(s => s.GetProperty("id").GetString() + ":" + s.GetProperty("option").GetString()).ToArray();
        Assert.DoesNotContain(steps, s => s.StartsWith("choice-campfire") || s.StartsWith("choice-day-shortner") || s.StartsWith("choice-clover")
                                          || s.StartsWith(DailyInternetShopPlanner.MachineChoiceId));
        Assert.DoesNotContain("meme-buy:viral video", steps);
        Assert.DoesNotContain("meme-buy:plus one", steps);
    }

    [Fact]
    public async Task Daily_run_execute_burns_one_leaf_and_marks_the_day()
    {
        var token = await MintTokenAsync();
        _kol.On("GET api.php?what=inventory", """{"11341":10}""")
            .On("GET campground.php", "<html><body>Pile of Burning Leaves <form><input type=hidden name=whichchoice value=1510></form> You have 10 leaves</body></html>")
            .On("POST choice.php", (_, form) => { Assert.Contains("leaves=1", form); return FakeKol.Html("<html><body>You burn 1 leaf. The flames rise.</body></html>"); });

        var (code, json, _) = await Run(KolibriCli.DailyCommandAsync, "run", "--role", "main", "--execute", "--approval-token", token);

        Assert.Equal(0, code);
        Assert.Equal("Complete", json.GetProperty("completeness").GetString());
        Assert.Equal(1, json.GetProperty("submitted").GetInt32());
        await using var client = KolibriCli.CreateClient();
        await using var session = await client.OpenAsync("main");
        Assert.True(session.Daily.IsDone(DailyRun.Markers.CampfireLeaf));
    }

    private const string MallSearchPage = """
        <center><b>Search Results:</b> <b>(Items 1-2 of 2)</b></center>
        <div id='searchresults'>
        <a name="searchresults"></a>
        <div class="jumplinks"><a href="#results_10881">11-leaf clover Results</a></div>
        <br /><a name="results_10881"></a>
        <table class="itemtable" summary="Stores selling 11-leaf clover">
        <tr class="blackabove" id="item_10881">
          <td><img src='x.gif' alt="11-leaf clover" title="11-leaf clover"></td>
          <td><b><a href="/mall.php?pudnuggler=%2211-leaf+clover%22">11-leaf clover</a></b></td>
        </tr>
        <tr class="graybelow" id="stock_56226_10881">
          <td style="border:none"></td>
          <td class="small store"><a href="mallstore.php?whichstore=56226"><b>Crazy Gordon</b></a></td>
          <td class="small stock">1,606</td>
          <td class="small">1&nbsp;/&nbsp;day</td>
          <td class="small price"><a href="mallstore.php?whichstore=56226">25,000&nbsp;Meat</a></td>
        </tr>
        <tr class="graybelow" id="stock_3566900_10881">
          <td style="border:none"></td>
          <td class="small store"><a href="mallstore.php?whichstore=3566900"><b>Brad's gut</b></a></td>
          <td class="small stock">110</td>
          <td class="small">1&nbsp;/&nbsp;day</td>
          <td class="small price"><a href="mallstore.php?whichstore=3566900">29,029&nbsp;Meat</a></td>
        </tr>
        </table>
        </div>
        """;

    [Fact]
    public async Task Mall_item_lists_offers_in_the_0_4_shape()
    {
        _kol.On("POST mall.php", MallSearchPage);
        var (code, json, _) = await Cli("mall", "--role", "main", "--item", "10881");
        Assert.Equal(0, code);
        Assert.Equal("10881", json.GetProperty("itemId").GetString());
        var first = json.GetProperty("listings")[0];
        Assert.Equal("56226", first.GetProperty("storeId").GetString());
        Assert.Equal(1606, first.GetProperty("quantity").GetInt32());
        Assert.False(json.GetProperty("isStale").GetBoolean());
    }

    [Fact]
    public async Task Keys_status_shows_parts_and_sets()
    {
        _kol.On("GET api.php?what=inventory", """{"11386":2,"11387":1,"11388":3,"11389":1,"11473":4}""");
        var (code, json, _) = await Cli("keys", "status", "--role", "main");
        Assert.Equal(0, code);
        var counts = json.GetProperty("counts");
        Assert.Equal(2, counts.GetProperty("parts").GetProperty("alpha").GetInt32());
        Assert.Equal(1, counts.GetProperty("assemblableSets").GetInt32());
        Assert.Equal(4, counts.GetProperty("completeKeys").GetInt32());
    }

    [Fact]
    public void Keys_redeem_resolves_items_by_id_or_unique_name_offline()
    {
        Assert.NotNull(KolibriCli.ResolveRare(PirateKeys.RareOptions[0].Option.ToString(), out _));
        Assert.Null(KolibriCli.ResolveRare("99999", out var problem));
        Assert.Contains("not one of the", problem);
    }

    [Fact]
    public void Apply_prices_preview_uses_the_0_4_rules()
    {
        var listings = new[] { new ShopListing(2, "seal tooth", 5, 150, 0), new ShopListing(3, "helmet turtle", 1, 900, 0) };
        var results = KolibriCli.PlanPriceUpdates(new[]
        {
            new KolibriCli.PriceUpdate("2", 150, 140), new KolibriCli.PriceUpdate("3", 800, 700),
            new KolibriCli.PriceUpdate("4", 100, 200), new KolibriCli.PriceUpdate("2", 150, 150)
        }, listings);
        Assert.Equal(new[] { "Ready", "Conflict", "Missing", "Hold" }, results.Select(r => r.Status));
    }

    [Fact]
    public void Limiter_environment_of_0_4_is_translated_and_clamped()
    {
        var env = new Dictionary<string, string?> { ["KOLIBRI_LIMITER_PRESET"] = "browser-like", ["KOLIBRI_MIN_REQUEST_INTERVAL_MS"] = "200" };
        var options = KolibriCli.LimiterFromEnvironment(k => env.GetValueOrDefault(k));
        Assert.Equal(TimeSpan.FromSeconds(1), options.ActionInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(400), options.ReadInterval);
        Assert.Equal(3, options.ReadConcurrency);
    }
}
