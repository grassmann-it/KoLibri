using System.Text.Json;
using KoLibri.Daily;
using Xunit;

namespace KoLibri.Tests;

/// <summary>daily run --only / --role all, the 0.4 aliases, removed subcommands and daily audit (FakeKol only).</summary>
[Collection("Cli")]
public sealed class CliDailyTests : IDisposable
{
    private readonly string _state = Directory.CreateTempSubdirectory("kolibri-cli-daily-").FullName;
    private readonly FakeKol _kol = new();
    private readonly MemoryAccountStore _store = MemoryAccountStore.WithMain();

    public CliDailyTests()
    {
        KolibriCli.OptionsOverride = () => new KolibriOptions
        {
            HttpHandler = _kol, AccountStore = _store, StateDirectory = _state, DisableLimiterForTests = true
        };
        AgentGuard.TokenRootOverride = Path.Combine(_state, "token");
    }

    public void Dispose()
    {
        KolibriCli.OptionsOverride = null;
        AgentGuard.TokenRootOverride = null;
        KolibriCli.AuditPathOverride = null;
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

    private const string Inventory = """{"11341":230,"8763":120,"9025":1,"43":2,"42":3,"10881":1}""";

    private static string[] Ids(JsonElement json)
        => json.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("id").GetString()!).ToArray();

    [Fact]
    public async Task Daily_only_campfire_plans_only_campfire_steps()
    {
        _kol.On("GET api.php?what=inventory", Inventory);
        var (code, json, _) = await Run("run", "--only", "campfire");
        Assert.Equal(0, code);
        var ids = Ids(json);
        Assert.Contains("choice-day-shortner", ids);
        Assert.DoesNotContain("worthless-item", ids);
        Assert.DoesNotContain(DailyInternetShopPlanner.MachineChoiceId, ids);
        Assert.DoesNotContain(ids, i => i.Contains("clover"));
        Assert.DoesNotContain(_kol.Requests, r => r.Method == "POST");

        var (c2, _, e2) = await Run("run", "--only", "campfire", "--skip", "hermit");
        Assert.Equal(2, c2);
        Assert.Contains("--only", e2);
    }

    [Theory]
    [InlineData("campfire", "run --only campfire")]
    [InlineData("hermit", "run --only hermit")]
    [InlineData("internet-shop", "run --only internet-shop")]
    [InlineData("internetshop", "run --only internet-shop")]
    [InlineData("meme-shop", "run --only internet-shop")]
    [InlineData("meme-shop-read", "run --only internet-shop")]
    [InlineData("routine", "run")]
    [InlineData("go", "run")]
    [InlineData("daily-go", "run")]
    [InlineData("bridge", "run")]
    [InlineData("daily-bridge", "run")]
    [InlineData("breakfast", "run")]
    public async Task Daily_aliases_map_to_run_and_warn(string alias, string target)
    {
        _kol.On("GET api.php?what=inventory", Inventory);
        var (code, json, err) = await Run(alias);
        Assert.Equal(0, code);
        Assert.Contains($"'daily {alias}' is now 'daily {target}", err);
        Assert.True(json.GetProperty("previewOnly").GetBoolean());
        Assert.DoesNotContain(_kol.Requests, r => r.Method == "POST");
        if (target.Contains("internet-shop"))
            Assert.DoesNotContain("choice-day-shortner", Ids(json));
    }

    [Fact]
    public async Task Daily_alias_execute_checks_the_approval_token_unchanged()
    {
        _kol.On("GET api.php?what=inventory", Inventory);
        await Assert.ThrowsAsync<ArgumentException>(() => Run("campfire", "--execute", "--approval-token", "wrong"));
        Assert.DoesNotContain(_kol.Requests, r => r.Method == "POST");
    }

    [Theory]
    [InlineData("--leaves")]
    [InlineData("--meat")]
    public async Task Daily_old_snapshot_flags_are_rejected(string flag)
    {
        var (code, _, err) = await Run("campfire", flag, "5");
        Assert.Equal(2, code);
        Assert.Contains("live state", err);
        Assert.Empty(_kol.Requests);
    }

    [Fact]
    public async Task Daily_role_all_runs_both_roles_in_order()
    {
        await _store.SaveAsync(new KoLibri.AccountRecord(
            new KoLibri.Account(new KoLibri.AccountId("mallbot"), "Bot", 2002, null, Array.Empty<string>()), "PHPSESSID=def", null, null), default);
        _kol.On("GET api.php?what=inventory", Inventory);
        var (code, json, _) = await Run("run", "--role", "all");
        Assert.Equal(0, code);
        Assert.Equal("all", json.GetProperty("role").GetString());
        Assert.Equal(new[] { "Main", "Mallbot" },
            json.GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("role").GetString()));
    }

    [Theory]
    [InlineData("bribes-sample")]
    [InlineData("send-items")]
    [InlineData("item-arbitrage")]
    public async Task Daily_removed_subcommands_exit_2(string sub)
    {
        var (code, _, err) = await Run(sub);
        Assert.Equal(2, code);
        Assert.Contains("removed", err);
        Assert.Empty(_kol.Requests);
    }

    private static string Row(string action, string hash)
        => "{\"auditEvent\":{\"phase\":\"Commit\",\"occurredAt\":\"2026-11-27T10:00:00+00:00\",\"action\":\"" + action
           + "\",\"accountId\":\"Main\",\"requestId\":\"r-" + action + "\"},\"hash\":\"" + hash + "\",\"prevHash\":null}";

    [Fact]
    public async Task Daily_audit_reads_and_verifies_fixture_log()
    {
        var path = Path.Combine(_state, "bridge-audit.jsonl");
        File.WriteAllLines(path, new[] { Row("one", "aa"), Row("two", "bb") });
        KolibriCli.AuditPathOverride = path;

        var (code, json, _) = await Run("audit", "--tail", "1");
        Assert.Equal(0, code);
        Assert.Equal("daily-audit", json.GetProperty("command").GetString());
        Assert.Equal(1, json.GetProperty("rowCount").GetInt32());
        Assert.Equal("two", json.GetProperty("rows")[0].GetProperty("action").GetString());

        var (c2, verified, _) = await Run("audit", "--verify");
        Assert.Equal(0, c2);
        Assert.Equal("broken", verified.GetProperty("state").GetString());
        Assert.Equal(2, verified.GetProperty("rowCount").GetInt32());
        Assert.Equal(0, verified.GetProperty("breakIndex").GetInt32());
    }

    [Fact]
    public async Task Daily_audit_rejects_a_path_argument()
    {
        var (c1, _, e1) = await Run("audit", "--path", "/etc/passwd");
        var (c2, _, _) = await Run("audit", "/etc/passwd");
        Assert.Equal(2, c1);
        Assert.Equal(2, c2);
        Assert.Contains("fixed", e1);
    }
}
