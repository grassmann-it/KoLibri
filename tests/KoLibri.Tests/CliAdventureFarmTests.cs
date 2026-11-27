using System.Globalization;
using System.Text.Json;
using Xunit;

namespace KoLibri.Tests;

/// <summary>`adventure run` farm flags (P2-W5): parsers, refusals before a request, the continue command and the farm recipe against the fake KoL.</summary>
[Collection("Cli")]
public sealed class CliAdventureFarmTests : IDisposable
{
    private const int Knucklebone = 12051;
    private readonly string _state = Directory.CreateTempSubdirectory("kolibri-cli-farm-").FullName;
    private readonly FakeKol _kol = new();
    private readonly MemoryAccountStore _store = MemoryAccountStore.WithMain();

    public CliAdventureFarmTests()
    {
        KolibriCli.OptionsOverride = () => new KolibriOptions
        {
            HttpHandler = _kol,
            AccountStore = _store,
            StateDirectory = _state,
            DataDirectory = RepoData(),
            DisableLimiterForTests = true
        };
        AgentGuard.TokenRootOverride = Path.Combine(_state, "token");
        KoLibri.Cli.UnknownPageArchive.DirectoryOverride = Path.Combine(_state, "unknown-pages");
    }

    public void Dispose()
    {
        KolibriCli.OptionsOverride = null;
        AgentGuard.TokenRootOverride = null;
        KoLibri.Cli.UnknownPageArchive.DirectoryOverride = null;
    }

    private static string RepoData()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "external", "kolmafia-data");
            if (File.Exists(Path.Combine(path, "skills.jsonl"))) return path;
        }
        throw new InvalidOperationException("external/kolmafia-data not found");
    }

    private GameData Data()
    {
        var client = KolibriClient.Create(new KolibriOptions
        {
            HttpHandler = new FakeKol(), AccountStore = MemoryAccountStore.WithMain(), StateDirectory = _state, DataDirectory = RepoData(), DisableLimiterForTests = true
        });
        return client.Data;
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

    // ---- parsers ----

    [Fact]
    public void Names_resolve_to_ids_through_the_game_data()
    {
        var data = Data();
        var stops = KolibriCli.BuildFarmStops(new[] { "--stop-item", "knucklebone:200", "--stop-monster", "Skeleton", "--stop-title", "^Out of", "--stop-on-turtle" }, data);
        Assert.Equal(StopConditionKind.ItemCountAtLeast, stops[0].Kind);
        Assert.Equal(Knucklebone, stops[0].Id);
        Assert.Equal(200, stops[0].Value);
        Assert.Equal(new[] { StopConditionKind.ItemCountAtLeast, StopConditionKind.MonsterSeen, StopConditionKind.EncounterTitle, StopConditionKind.TurtleEncounter },
            stops.Select(s => s.Kind));
        Assert.Equal(Knucklebone, KolibriCli.BuildFarmStops(new[] { "--stop-item", $"{Knucklebone}:5" }, data)[0].Id);

        var (percent, steps) = KolibriCli.ParseRecoverSpec("40:rest,item:knucklebone,skill:7", data, "--recover-hp");
        Assert.Equal(40, percent);
        Assert.Equal(RecoveryStepKind.Rest, steps[0].Kind);
        Assert.Equal(Knucklebone, steps[1].Id);
        Assert.Equal(RecoveryStepKind.Cast, steps[2].Kind);
        Assert.Equal(7, steps[2].Id);
    }

    [Theory]
    [InlineData("--stop-item", "knucklebone")]
    [InlineData("--stop-item", "no such item at all:5")]
    [InlineData("--stop-item", "knucklebone:0")]
    [InlineData("--stop-title", "([unclosed")]
    [InlineData("--recover-hp", "rest")]
    [InlineData("--recover-hp", "0:rest")]
    [InlineData("--recover-hp", "150:rest")]
    [InlineData("--recover-hp", "40:dance")]
    [InlineData("--recover-mp", "40:skill:No Such Skill Anywhere")]
    [InlineData("--upkeep", "63:20")]
    [InlineData("--upkeep", "Polka of Plenty:20:rest")]
    [InlineData("--upkeep", "63:0:rest")]
    public void Invalid_flags_are_refused(string flag, string value)
    {
        var args = new[] { flag, value };
        var data = Data();
        Assert.Throws<ArgumentException>(() =>
        {
            KolibriCli.BuildFarmStops(args, data);
            KolibriCli.BuildRecovery(args, data);
        });
    }

    [Fact]
    public void Rules_and_fight_action_exclude_each_other_and_unknown_names_fail()
    {
        var data = Data();
        Assert.Throws<ArgumentException>(() => KolibriCli.BuildRulesStrategy(new[] { "--combat-rules", "attack", "--fight-action", "attack" }, data));
        Assert.Throws<ArgumentException>(() => KolibriCli.BuildRulesStrategy(new[] { "--combat-rules", "skill \"No Such Skill Anywhere\"" }, data));
        Assert.Throws<ArgumentException>(() => KolibriCli.BuildRulesStrategy(new[] { "--combat-rules-file", Path.Combine(_state, "missing.txt") }, data));
        Assert.Null(KolibriCli.BuildRulesStrategy(new[] { "--fight-action", "attack" }, data));
        var file = Path.Combine(_state, "rules.txt");
        File.WriteAllText(file, "attack");
        Assert.NotNull(KolibriCli.BuildRulesStrategy(new[] { "--combat-rules-file", file }, data));
    }

    [Fact]
    public void Upkeep_and_both_recoveries_combine()
    {
        var data = Data();
        Assert.Same(RecoveryPolicies.None, KolibriCli.BuildRecovery(Array.Empty<string>(), data));
        Assert.NotSame(RecoveryPolicies.None, KolibriCli.BuildRecovery(new[] { "--recover-hp", "40:rest", "--upkeep", "63:20:rest" }, data));
    }

    [Fact]
    public void Continue_command_carries_every_farm_flag()
    {
        var args = new[]
        {
            "run", "--location", "439", "--combat-rules", "if monster \"Skeleton\" then attack", "--recover-hp", "40:rest", "--recover-mp", "20:rest",
            "--upkeep", "63:20:rest", "--upkeep", "64:10:rest", "--stop-item", "knucklebone:200", "--stop-monster", "Skeleton", "--stop-title", "^Out of",
            "--stop-on-turtle", "--no-choice-defaults", "--max-turns", "50"
        };
        var command = KolibriCli.BuildContinueCommand(args, "439", KoLibri.Cli.AccountRole.Main, 439);
        foreach (var expected in new[]
        {
            "--combat-rules \"if monster \\\"Skeleton\\\" then attack\"", "--recover-hp 40:rest", "--recover-mp 20:rest", "--upkeep 63:20:rest", "--upkeep 64:10:rest",
            "--stop-item knucklebone:200", "--stop-monster Skeleton", "--stop-title \"^Out of\"", "--stop-on-turtle", "--no-choice-defaults", "--max-turns 50"
        })
            Assert.Contains(expected, command);
    }

    // ---- commands: nothing is sent on a bad flag ----

    [Theory]
    [InlineData("--recover-hp", "banana")]
    [InlineData("--stop-item", "knucklebone")]
    [InlineData("--combat-rules", "skill \"No Such Skill Anywhere\"")]
    public async Task A_bad_flag_sends_nothing(string flag, string value)
    {
        var token = await MintTokenAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => Run("run", "--location", "439", flag, value, "--execute", "--approval-token", token));
        Assert.Empty(_kol.Requests);
    }

    // ---- the documented farm recipe ----

    private sealed class Farm
    {
        public int Hp = 100, Adventures = 50, Calls, Bones, Rests;
        public readonly List<string> Choices = new();
        private const string Fight = """<html><body><span id="monname" monsterid="123">skeleton</span><form action="fight.php" method="post"><input type="hidden" name="onturn" value="1"><button name="action" value="attack">Attack</button></form></body></html>""";
        private const string Choice = """<html><body><b>Temporarily Out of Skeletons</b><form action="choice.php" method="post"><input type="hidden" name="whichchoice" value="1060"><input type="hidden" name="pwd" value="0123456789abcdef"><input type="submit" name="option" value="1"> <input type="submit" name="option" value="2"></form></body></html>""";

        public Farm(FakeKol kol)
        {
            kol.On("GET api.php?what=status", (_, _) => FakeKol.Html(string.Create(CultureInfo.InvariantCulture,
                $$"""{"name":"Tester","playerid":"1001","level":7,"meat":10000,"adventures":{{Adventures}},"hp":{{Hp}},"maxhp":100,"mp":40,"maxmp":50,"full":0,"fullnessmax":15,"drunk":0,"drunkmax":14,"spleen":0,"spleenmax":15,"pwd":"0123456789abcdef","familiar":"0","equipment":{"hat":"3","weapon":"0"},"effects":{} }""")));
            kol.On("GET api.php?what=inventory", (_, _) => FakeKol.Html(Bones > 0 ? $"{{\"{Knucklebone}\":{Bones}}}" : "{}"));
            // Turn 2 is the Skeleton Store choice; every other turn is a fight that costs 70 HP.
            kol.On("POST adventure.php", (_, _) => FakeKol.Redirect(++Calls == 2 ? "https://www.kingdomofloathing.com/choice.php" : "https://www.kingdomofloathing.com/fight.php"));
            kol.On("GET fight.php", (_, _) => FakeKol.Html(Fight));
            kol.On("GET choice.php", (_, _) => FakeKol.Html(Choice));
            kol.On("POST fight.php", (_, _) =>
            {
                Adventures--; Hp -= 70; Bones++;
                return FakeKol.Html("<html><body><!--WINWINWIN--><p>You win the fight!</p><p>You acquire an item: <b>knucklebone</b></p></body></html>");
            });
            kol.On("POST choice.php", (_, form) =>
            {
                Adventures--; Choices.Add(form);
                return FakeKol.Html("<html><body><b>Bone Store</b><p>You leave.</p></body></html>");
            });
            kol.On("POST campground.php", (_, _) => { Hp = 100; Rests++; return FakeKol.Html("<html><body>You rest and feel much better.</body></html>"); });
        }
    }

    [Fact]
    public async Task Farm_recipe_heals_resolves_1060_and_stops_at_the_item_count()
    {
        var token = await MintTokenAsync();
        var farm = new Farm(_kol);

        var (code, json, error) = await Run("run", "--location", "439", "--max-turns", "50", "--combat-rules", "attack", "--recover-hp", "40:rest",
            "--stop-item", "knucklebone:3", "--execute", "--approval-token", token);

        Assert.True(code == 0, error + json);
        Assert.Equal("ConditionsSatisfied", json.GetProperty("stopReason").GetString());
        // fight, choice 1060, fight, fight (3rd bone); the 5th run only notices the goal (the library checks stops after recovery) and spends no adventure.
        Assert.Equal(5, json.GetProperty("turnsDone").GetInt32());
        Assert.Equal(46, farm.Adventures);
        Assert.Equal(3, farm.Bones);
        Assert.Single(farm.Choices);
        Assert.Contains("whichchoice=1060", farm.Choices[0]);
        Assert.Contains("option=2", farm.Choices[0]);
        Assert.True(farm.Rests >= 1);                                    // HP fell to 30, below 40 %
        Assert.True(json.GetProperty("recoverySteps").GetArrayLength() >= 1);
        Assert.Equal("Rest", json.GetProperty("recoverySteps")[0].GetProperty("kind").GetString());
        Assert.Equal(1, json.GetProperty("autoChoices").GetArrayLength());
        Assert.Equal("BuiltIn", json.GetProperty("autoChoices")[0].GetProperty("source").GetString());
        Assert.Equal("Temporarily Out of Skeletons", json.GetProperty("autoChoices")[0].GetProperty("title").GetString());
        // The output names the stops and the recovery the run used (live 2026-11-27 it printed "conditions": []).
        var condition = Assert.Single(json.GetProperty("conditions").EnumerateArray());
        Assert.Equal("ItemCountAtLeast", condition.GetProperty("kind").GetString());
        Assert.Equal(Knucklebone, condition.GetProperty("itemId").GetInt32());
        Assert.Equal("knucklebone", condition.GetProperty("item").GetString());
        Assert.Equal(3, condition.GetProperty("count").GetInt32());
        var hp = json.GetProperty("recoveryPolicy").GetProperty("hp");
        Assert.Equal(40, hp.GetProperty("belowPercent").GetInt32());
        Assert.Equal("Rest", hp.GetProperty("steps")[0].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Monster_flag_names_its_stop_reason()
    {
        var token = await MintTokenAsync();
        _ = new Farm(_kol);

        var (code, json, _) = await Run("run", "--location", "439", "--max-turns", "5", "--stop-monster", "skeleton", "--recover-hp", "40:rest",
            "--execute", "--approval-token", token);

        Assert.Equal(0, code);
        Assert.Equal("MonsterSeen", json.GetProperty("stopReason").GetString());
        Assert.Equal(1, json.GetProperty("turnsDone").GetInt32());
        var condition = Assert.Single(json.GetProperty("conditions").EnumerateArray());
        Assert.Equal("MonsterSeen", condition.GetProperty("kind").GetString());
        Assert.Equal("skeleton", condition.GetProperty("monster").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("recoveryPolicy").GetProperty("mp").ValueKind);
    }

    [Fact]
    public async Task Run_help_documents_the_farm_recipe()
    {
        var (code, _, _) = await Run("--help");
        Assert.Equal(0, code);
        var output = new StringWriter();
        var old = Console.Out;
        Console.SetOut(output);
        try { await KolibriCli.AdventureCommandAsync(new[] { "--help" }); }
        finally { Console.SetOut(old); }
        foreach (var text in new[] { "--combat-rules", "--recover-hp", "--stop-item", "--stop-on-turtle", "--no-choice-defaults", "--max-turns 50 --combat-rules \"attack\" --recover-hp 40:rest --stop-item knucklebone:200" })
            Assert.Contains(text, output.ToString());
    }
}
