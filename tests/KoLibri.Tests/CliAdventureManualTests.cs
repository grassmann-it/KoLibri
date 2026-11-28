using System.Text.Json;
using Xunit;

namespace KoLibri.Tests;

/// <summary>`adventure inspect|step|fight|locations` and the retired aliases, against the fake KoL.</summary>
[Collection("Cli")]
public sealed class CliAdventureManualTests : IDisposable
{
    private readonly string _state = Directory.CreateTempSubdirectory("kolibri-cli-manual-").FullName;
    private readonly FakeKol _kol = new();
    private readonly MemoryAccountStore _store = MemoryAccountStore.WithMain();

    public CliAdventureManualTests()
    {
        var data = Path.Combine(_state, "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "adventures.jsonl"),
            """{"id": "adventure=543:Your Mushroom Garden", "name": "Your Mushroom Garden", "sniffProperty": "adventure=543", "sniff": 543, "zone": "Campground", "desc": "", "type": "", "monsters": []}""" + "\n"
            + """{"id": "adventure=439:The Haunted Pantry", "name": "The Haunted Pantry", "sniffProperty": "adventure=439", "sniff": 439, "zone": "Manor", "desc": "", "type": "", "monsters": []}""" + "\n");
        KolibriCli.OptionsOverride = () => new KolibriOptions
        {
            HttpHandler = _kol,
            AccountStore = _store,
            StateDirectory = _state,
            DataDirectory = data,
            DisableLimiterForTests = true
        };
        AgentGuard.TokenRootOverride = Path.Combine(_state, "token");
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
        try { code = await KolibriCli.AdventureCommandAsync(args); }
        finally { Console.SetOut(oldOut); Console.SetError(oldErr); }
        var text = output.ToString().Trim();
        return (code, text.StartsWith('{') ? JsonDocument.Parse(text).RootElement.Clone() : default, error.ToString());
    }

    private async Task<string> MintTokenAsync()
    {
        var output = new StringWriter();
        var old = Console.Out;
        Console.SetOut(output);
        try { Assert.Equal(0, await KolibriCli.RunAsync(new[] { "token", "mint" }, _ => null)); }
        finally { Console.SetOut(old); }
        return JsonDocument.Parse(output.ToString()).RootElement.GetProperty("token").GetString()!;
    }

    private const string FightWithSkills = """
        <html><body><span id="monname" monsterid="123">Skeleton</span>
        <span id="playerhp">80/100</span><span id="playermp">40/50</span>
        <form action="fight.php" method="post"><input type="hidden" name="onturn" value="1">
        <select name="whichskill"><option value="1">Lunge</option><option value="7">Toss</option></select>
        <select name="whichitem"><option value="2">seal tooth</option></select>
        <button name="action" value="attack">Attack</button></form></body></html>
        """;

    /// <summary>KoL's real fight page shows no player HP: the HP then come from the status.</summary>
    private const string FightWithoutHp = """
        <html><body><span id="monname" monsterid="2047">Skeleton</span>
        <form action="fight.php" method="post"><input type="hidden" name="onturn" value="1">
        <button name="action" value="attack">Attack</button></form></body></html>
        """;

    private FakeKol OpenFight(string page = ActionTests.FightRound1)
        => _kol.On("GET main.php", (_, _) => FakeKol.Redirect("https://www.kingdomofloathing.com/fight.php")).On("GET fight.php", page);

    // ---- locations ----

    [Fact]
    public async Task Locations_reads_the_catalog_without_any_request()
    {
        var (code, json, _) = await Run("locations", "--name", "pantry");
        Assert.Equal(0, code);
        Assert.Equal(1, json.GetProperty("count").GetInt32());
        Assert.Equal(439, json.GetProperty("locations")[0].GetProperty("snarfblat").GetInt32());

        (code, json, _) = await Run("locations", "--id", "543");
        Assert.Equal("Your Mushroom Garden", json.GetProperty("locations")[0].GetProperty("name").GetString());
        Assert.Empty(_kol.Requests);
    }

    // ---- inspect ----

    [Fact]
    public async Task Inspect_without_an_open_encounter_says_open_false()
    {
        var (code, json, _) = await Run("inspect");
        Assert.Equal(0, code);
        Assert.False(json.GetProperty("open").GetBoolean());
        Assert.Equal(0, _kol.Count("POST", "fight.php"));
    }

    [Fact]
    public async Task Inspect_shows_the_open_fight_with_skills_and_exit_4()
    {
        OpenFight(FightWithSkills);
        var (code, json, _) = await Run("inspect");
        Assert.Equal(4, code);
        var encounter = json.GetProperty("encounter");
        Assert.Equal("Skeleton", encounter.GetProperty("monster").GetString());
        Assert.Equal(2, encounter.GetProperty("skills").GetArrayLength());
        Assert.Equal(7, encounter.GetProperty("skills")[1].GetProperty("id").GetInt32());
        Assert.Contains("adventure fight", json.GetProperty("nextCommand").GetString());
        Assert.DoesNotContain(_kol.Requests, r => r.Method == "POST");
    }

    [Fact]
    public async Task Inspect_shows_an_open_choice_with_its_options()
    {
        _kol.On("GET main.php", (_, _) => FakeKol.Redirect("https://www.kingdomofloathing.com/choice.php")).On("GET choice.php", ActionTests.ChoicePage);
        var (code, json, _) = await Run("inspect");
        Assert.Equal(4, code);
        Assert.Equal("1234", json.GetProperty("encounter").GetProperty("choiceId").GetString());
        Assert.Equal(2, json.GetProperty("encounter").GetProperty("options").GetArrayLength());
    }

    // ---- step ----

    [Fact]
    public async Task Step_preview_sends_nothing()
    {
        var (code, json, _) = await Run("step", "--location", "439");
        Assert.Equal(0, code);
        Assert.False(json.GetProperty("executed").GetBoolean());
        Assert.Empty(_kol.Requests);
    }

    [Fact]
    public async Task Step_without_or_with_a_wrong_token_sends_nothing()
    {
        await MintTokenAsync();
        var missing = await Assert.ThrowsAsync<ArgumentException>(() => Run("step", "--location", "439", "--execute"));
        Assert.Contains("--approval-token", missing.Message);
        var wrong = await Assert.ThrowsAsync<ArgumentException>(() => Run("advance", "--location", "439", "--execute", "--approval-token", "nope"));
        Assert.Contains("approval token", wrong.Message);
        Assert.Empty(_kol.Requests);
    }

    [Fact]
    public async Task Step_into_a_fight_sends_one_adventure_and_names_the_fight_command()
    {
        var token = await MintTokenAsync();
        _kol.On("POST adventure.php", (_, _) => FakeKol.Redirect("https://www.kingdomofloathing.com/fight.php")).On("GET fight.php", ActionTests.FightRound1);

        var (code, json, _) = await Run("step", "--location", "439", "--execute", "--approval-token", token);

        Assert.Equal(4, code);
        Assert.Equal("Fight", json.GetProperty("encounter").GetProperty("kind").GetString());
        Assert.Contains("kolibri adventure fight", json.GetProperty("nextCommand").GetString());
        Assert.Equal(1, _kol.Count("POST", "adventure.php"));
        Assert.Equal(0, _kol.Count("POST", "fight.php"));
    }

    [Fact]
    public async Task Step_into_a_choice_resolves_nothing_and_names_choice_submit()
    {
        var token = await MintTokenAsync();
        _kol.On("POST adventure.php", (_, _) => FakeKol.Redirect("https://www.kingdomofloathing.com/choice.php")).On("GET choice.php", ActionTests.ChoicePage);

        var (code, json, _) = await Run("step", "--location", "439", "--execute", "--approval-token", token);

        Assert.Equal(4, code);
        Assert.Contains("kolibri choice submit --role main --choice 1234", json.GetProperty("nextCommand").GetString());
        Assert.Equal(0, _kol.Count("POST", "choice.php"));
    }

    // ---- fight ----

    [Fact]
    public async Task Fight_preview_and_missing_token_send_nothing()
    {
        var (code, json, _) = await Run("fight", "--action", "item:2,3");
        Assert.Equal(0, code);
        Assert.False(json.GetProperty("executed").GetBoolean());
        var token = await MintTokenAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => Run("fight", "--action", "attack", "--execute"));
        await Assert.ThrowsAsync<ArgumentException>(() => Run("fight", "--action", "attack", "--execute", "--approval-token", token + "x"));
        await Assert.ThrowsAsync<ArgumentException>(() => Run("fight", "--action", "dance"));
        Assert.Empty(_kol.Requests);
    }

    [Fact]
    public async Task Fight_sends_exactly_one_round_and_reports_it_ongoing()
    {
        var token = await MintTokenAsync();
        _kol.On("POST fight.php", (_, form) => { Assert.Equal("action=skill&whichskill=7", form); return FakeKol.Html(ActionTests.FightRound1); });

        var (code, json, _) = await Run("fight", "--action", "skill:7", "--execute", "--approval-token", token);

        Assert.Equal(4, code);
        Assert.Equal("Ongoing", json.GetProperty("outcome").GetString());
        Assert.True(json.GetProperty("openFight").GetBoolean());
        Assert.Equal(1, _kol.Count("POST", "fight.php"));
    }

    [Fact]
    public async Task Fight_one_round_that_wins_ends_with_exit_0()
    {
        var token = await MintTokenAsync();
        _kol.On("POST fight.php", (_, form) => { Assert.Equal("action=attack", form); return FakeKol.Html(ActionTests.FightWon); })
            .On("GET main.php", "<html><body>main</body></html>");

        var (code, json, _) = await Run("fight", "--action", "attack", "--execute", "--approval-token", token);

        Assert.Equal(0, code);
        Assert.Equal("Won", json.GetProperty("outcome").GetString());
        Assert.Equal("seal tooth", json.GetProperty("items")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Fight_until_end_stops_at_the_hp_floor_and_reports_the_open_fight()
    {
        var token = await MintTokenAsync();
        _kol.StatusHp = 10;
        OpenFight(FightWithoutHp);

        var (code, json, _) = await Run("fight", "--action", "attack", "--until-end", "--min-hp-percent", "20", "--execute", "--approval-token", token);

        Assert.Equal(4, code);
        Assert.Equal("HpBelowMinimum", json.GetProperty("stopReason").GetString());
        Assert.True(json.GetProperty("openFight").GetBoolean());
        Assert.Contains("--until-end", json.GetProperty("nextCommand").GetString());
        Assert.Equal(0, _kol.Count("POST", "fight.php"));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(32)]
    public async Task Fight_until_end_stops_at_the_round_limit(int limit)
    {
        var token = await MintTokenAsync();
        OpenFight().On("POST fight.php", ActionTests.FightRound1);

        var (code, json, _) = await Run("fight", "--action", "attack", "--until-end", "--max-rounds",
            limit.ToString(System.Globalization.CultureInfo.InvariantCulture), "--execute", "--approval-token", token);

        Assert.Equal(4, code);
        Assert.Equal("FightRoundLimitReached", json.GetProperty("stopReason").GetString());
        Assert.Equal(limit, _kol.Count("POST", "fight.php"));
        Assert.Equal(limit, json.GetProperty("rounds").GetArrayLength());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fight_until_end_with_no_open_fight_or_an_unreadable_answer_exits_3(bool startsOpen)
    {
        var token = await MintTokenAsync();
        if (startsOpen) OpenFight().On("POST fight.php", "<html><body>Not in a Fight</body></html>");

        var (code, json, _) = await Run("fight", "--action", "attack", "--until-end", "--execute", "--approval-token", token);

        Assert.Equal(3, code);
        Assert.Equal("Unknown", json.GetProperty("outcome").GetString());
        Assert.Equal(startsOpen ? 1 : 0, _kol.Count("POST", "fight.php"));
    }

    [Fact]
    public async Task Fight_until_end_fights_to_the_win()
    {
        var token = await MintTokenAsync();
        OpenFight().On("GET main.php", "<html><body>main</body></html>").On("POST fight.php", ActionTests.FightWon);

        var (code, json, _) = await Run("fight", "--action", "attack", "--until-end", "--execute", "--approval-token", token);

        Assert.Equal(0, code);
        Assert.Equal("Won", json.GetProperty("outcome").GetString());
        Assert.Equal(1, _kol.Count("POST", "fight.php"));
    }

    // ---- aliases ----

    [Theory]
    [InlineData("start")]
    [InlineData("begin")]
    [InlineData("bounded-preview")]
    [InlineData("run-preview")]
    public async Task Run_aliases_are_a_preview_without_execute(string alias)
    {
        var (code, json, _) = await Run(alias, "--location", "439");
        Assert.Equal(0, code);
        Assert.Equal("adventure run", json.GetProperty("command").GetString());
        Assert.True(json.GetProperty("previewOnly").GetBoolean());
        Assert.Empty(_kol.Requests);
    }

    [Theory]
    [InlineData("stop", "Ctrl+C")]
    [InlineData("abort", "Ctrl+C")]
    [InlineData("choice-chain", "--choice-action")]
    public async Task Retired_commands_exit_2_with_the_replacement(string name, string replacement)
    {
        var (code, _, error) = await Run(name);
        Assert.Equal(2, code);
        Assert.Contains(replacement, error);
        Assert.Empty(_kol.Requests);
    }
}
