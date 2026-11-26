using System.Text.Json;
using KolLibri.Mcp;
using Xunit;

namespace KoLibri.Tests;

/// <summary>
/// The MCP read tools on KoLibri 0.5 (replaces 0.4's McpRoleRuntimeTests): role selection opens that account's session,
/// the 0.4 JSON shapes stay, and the placeholder tools send nothing.
/// </summary>
public sealed class McpReadToolsTests
{
    private readonly FakeKol _kol = new();

    private KolibriMcpServer Server(bool experimental = false)
        => new(McpTest.Accounts(_kol, McpTest.BothRoles()), new KolibriMcpServerOptions { ExperimentalTools = experimental });

    internal static async Task<JsonElement> CallAsync(KolibriMcpServer server, string tool, string arguments = "{}")
    {
        using var document = JsonDocument.Parse(
            $$$"""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"{{{tool}}}","arguments":{{{arguments}}}}}""");
        return (await server.HandleAsync(document.RootElement.Clone()))!.Value.Clone();
    }

    [Fact]
    public async Task Status_reads_the_selected_role_with_its_own_cookie_and_keeps_the_0_4_shape()
    {
        var cookies = new List<string>();
        _kol.On("GET api.php?what=status", (request, _) =>
        {
            cookies.Add(string.Join(";", request.Headers.TryGetValues("Cookie", out var values) ? values : Array.Empty<string>()));
            return FakeKol.Html(_kol.StatusJson());
        });

        var response = await CallAsync(Server(), "kol_status", """{"role":"Mallbot"}""");

        var structured = response.GetProperty("result").GetProperty("structuredContent");
        Assert.Equal("Tester", structured.GetProperty("playerName").GetString());
        Assert.Equal(50, structured.GetProperty("adventures").GetInt32());
        Assert.Equal("2002", structured.GetProperty("playerId").GetString()); // the stored account of the role
        Assert.False(structured.GetProperty("isStale").GetBoolean());
        Assert.Contains(cookies, c => c.Contains("PHPSESSID=def"));
        Assert.DoesNotContain(cookies, c => c.Contains("PHPSESSID=abc"));
    }

    [Fact]
    public async Task Inventory_role_selection_needs_no_cookie_or_token_fields()
    {
        _kol.On("GET api.php?what=inventory", """{"2":3}""");

        var response = await CallAsync(Server(), "kol_inventory", """{"role":"main"}""");

        var item = response.GetProperty("result").GetProperty("structuredContent").GetProperty("items")[0];
        Assert.Equal("2", item.GetProperty("itemId").GetString());
        Assert.Equal(3, item.GetProperty("quantity").GetInt32());
    }

    [Fact]
    public async Task Price_summary_has_the_0_4_fields_from_the_mall_listings()
    {
        _kol.On("POST mall.php", CliTests.MallSearchPage);

        var response = await CallAsync(Server(), "kol_price_summary", """{"itemId":"10881","role":"Mallbot"}""");
        Assert.False(response.GetProperty("result").TryGetProperty("isError", out var error) && error.GetBoolean(), response.GetRawText());

        var structured = response.GetProperty("result").GetProperty("structuredContent");
        Assert.Equal("10881", structured.GetProperty("itemId").GetString());
        Assert.Equal(25_000, structured.GetProperty("lowestSellPrice").GetDecimal());
        Assert.Equal(25_000, structured.GetProperty("weightedMedianSellPrice").GetDecimal());
        Assert.Equal(2, structured.GetProperty("quoteCount").GetInt32());
        Assert.False(structured.TryGetProperty("lowestBuyPrice", out _)); // null fields are left out, as in 0.4
    }

    [Fact]
    public async Task A_role_without_a_stored_login_is_a_tool_error_without_a_request()
    {
        var server = new KolibriMcpServer(McpTest.Accounts(_kol, MemoryAccountStore.WithMain()));

        var response = await CallAsync(server, "kol_status", """{"role":"Mallbot"}""");

        Assert.Contains("No valid session for Mallbot", response.GetRawText());
        Assert.Empty(_kol.Requests);
    }

    public static TheoryData<string> Placeholders() => new()
    {
        "kol_price_recommendation", "kol_underprice_preview", "kol_workflow_plan", "kol_plan_daily_adventures",
        "kol_preview_purchase", "kol_preview_transfer", "kol_autocombat_preview", "kol_autocombat_action_preview",
        "kol_recovery_plan", "kol_adventure_bounded_preview", "kol_adventure_daily_schedule"
    };

    [Theory]
    [MemberData(nameof(Placeholders))]
    public async Task Placeholder_tools_stay_listed_answer_not_yet_and_send_nothing(string tool)
    {
        var server = Server(experimental: true);
        using var list = JsonDocument.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        var listed = (await server.HandleAsync(list.RootElement.Clone()))!.Value;
        Assert.Contains(listed.GetProperty("result").GetProperty("tools").EnumerateArray(), t => t.GetProperty("name").GetString() == tool);

        var response = await CallAsync(server, tool, """{"role":"Main","itemId":"10881"}""");

        Assert.Contains(KolibriMcpServer.NotYetMessage, response.GetRawText());
        Assert.Empty(_kol.Requests);
    }
}
