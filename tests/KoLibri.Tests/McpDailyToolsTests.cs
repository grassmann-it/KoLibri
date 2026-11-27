using System.Text.Json;
using KoLibri.Daily;
using KolLibri.Mcp;
using Xunit;

namespace KoLibri.Tests;

/// <summary>The daily MCP tools plan on the live path (DailyRun: status, inventory, today's markers) and never send.</summary>
public sealed class McpDailyToolsTests : IDisposable
{
    private readonly string _state = Directory.CreateTempSubdirectory("kolibri-mcp-daily-").FullName;
    private readonly FakeKol _kol = new();
    private readonly KolibriMcpServer _server;

    public McpDailyToolsTests()
    {
        _kol.On("GET api.php?what=inventory", """{"11341":230,"8763":120,"9025":1,"43":2,"10881":1}""");
        _server = new KolibriMcpServer(McpTest.Accounts(_kol, McpTest.BothRoles(), _state), new KolibriMcpServerOptions { ExperimentalTools = true });
    }

    public void Dispose()
    {
        try { Directory.Delete(_state, true); } catch (IOException) { }
    }

    private async Task<JsonElement> Structured(string tool, string arguments)
    {
        var result = (await McpReadToolsTests.CallAsync(_server, tool, arguments)).GetProperty("result");
        Assert.False(result.GetProperty("isError").GetBoolean(), result.GetRawText());
        return result.GetProperty("structuredContent");
    }

    private static string[] StepIds(JsonElement payload)
        => payload.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("id").GetString()!).ToArray();

    [Fact]
    public async Task Daily_routine_plans_from_live_numbers_without_sending()
    {
        var payload = await Structured("kol_daily_routine", """{"role":"Main"}""");

        Assert.True(payload.GetProperty("readOnly").GetBoolean());
        Assert.True(payload.GetProperty("previewOnly").GetBoolean());
        Assert.Equal("Act", payload.GetProperty("verdict").GetString());
        Assert.Equal(230, payload.GetProperty("live").GetProperty("leaves").GetInt32());
        var ids = StepIds(payload);
        Assert.Contains("choice-campfire", ids);
        Assert.Contains("worthless-item", ids);
        Assert.DoesNotContain(_kol.Requests, r => r.Method == "POST");
    }

    [Fact]
    public async Task Campfire_plan_is_already_done_with_todays_0_4_markers()
    {
        string today;
        await using (var client = TestClient.Create(_kol, McpTest.BothRoles(), state: _state))
        await using (var session = await client.OpenAsync("main"))
            today = session.Daily.GameDay.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        // The 0.4 DailyDoneStore file: same name, same JSON shape, key "<role>:<playerId>|<marker>".
        File.WriteAllText(Path.Combine(_state, "daily-done.json"), JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["main:1001|" + DailyRun.Markers.CampfireLeaf] = today,
            ["main:1001|day-shortener"] = today,
        }));

        var payload = await Structured("kol_daily_campfire_plan", """{"role":"Main"}""");

        Assert.Equal("AlreadyDoneToday", payload.GetProperty("verdict").GetString());
        Assert.Empty(StepIds(payload));
        var hermit = await Structured("kol_daily_hermit_plan", """{"role":"Main","hermitClovers":3}""");
        Assert.Equal("hermit", hermit.GetProperty("surface").GetString());
        Assert.DoesNotContain(_kol.Requests, r => r.Method == "POST");
    }

    [Fact]
    public async Task Daily_bridge_previews_both_roles_and_refuses_execute()
    {
        var payload = await Structured("kol_run_daily_bridge", """{"roles":["Main","Mallbot"]}""");

        Assert.True(payload.GetProperty("readOnly").GetBoolean());
        Assert.Equal(2, payload.GetProperty("roles").GetArrayLength());
        Assert.Contains(payload.GetProperty("roles")[0].GetProperty("steps").EnumerateArray(), s => s.GetProperty("kind").GetString() == "ChoiceStepSpec");

        var refused = (await McpReadToolsTests.CallAsync(_server, "kol_run_daily_bridge", """{"execute":true}""")).GetProperty("result");
        Assert.True(refused.GetProperty("isError").GetBoolean());
        Assert.DoesNotContain(_kol.Requests, r => r.Method == "POST");
    }
}
