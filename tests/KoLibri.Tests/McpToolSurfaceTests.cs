using System.Text.Json;
using KolLibri.Mcp;
using KoLibri.Safety.Access;
using Xunit;

namespace KoLibri.Tests;

/// <summary>
/// Pins the tools a default MCP host offers. Adding a tool to the core is a deliberate
/// change of this list (and of docs/mcp-access.md); everything else needs --experimental.
/// </summary>
public sealed class McpToolSurfaceTests : IDisposable
{
    private static readonly string[] ExpectedCore =
    [
        "kol_adventure_locations",
        "kol_adventure_run_execute",
        "kol_choice_chain_execute",
        "kol_choice_options",
        "kol_choice_pending",
        "kol_combat_decision",
        "kol_combat_inspect",
        "kol_consume",
        "kol_daily_audit",
        "kol_inventory",
        "kol_login_status",
        "kol_mall_buy",
        "kol_meme_shop",
        "kol_price_summary",
        "kol_rest",
        "kol_run_daily_bridge",
        "kol_send_message",
        "kol_status",
        "kol_use_item",
    ];

    private static readonly string[] Experimental =
    [
        "kol_arbitrage_plan", "kol_reprice_shop", "kol_reward_buyer", "kol_shop_sales", "kol_shop_buyers",
        "kol_shop_price_suggestions", "kol_transfer", "kol_daily_routine",
    ];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kolibri-surface-" + Guid.NewGuid().ToString("N"));
    private readonly AdventureMcpExecutionBridge _bridge;

    private readonly McpAccounts _accounts = McpTest.Accounts();

    public McpToolSurfaceTests() => _bridge = new AdventureMcpExecutionBridge(_accounts);

    public void Dispose()
    {
        _bridge.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>A server wired like <c>kolibri-mcp</c> over HTTP: execution bridge plus write tools.</summary>
    private KolibriMcpServer FullHost(bool experimental)
    {
        var server = new KolibriMcpServer(_accounts, options: new KolibriMcpServerOptions { ExperimentalTools = experimental }, executionBridge: _bridge);
        var gate = new AccessGate(new GrantStore(Path.Combine(_dir, "g.json")), new UsageLedger(Path.Combine(_dir, "u.jsonl")));
        // Registration only stores the actions; no handler runs in these tests.
        server.EnableWriteTools(gate, null!);
        return server;
    }

    private static async Task<string[]> ListedAsync(KolibriMcpServer server)
    {
        var res = (await server.HandleAsync(JsonDocument.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}").RootElement))!.Value;
        return res.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!).Order(StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public async Task Default_tools_list_is_exactly_the_core()
    {
        var listed = await ListedAsync(FullHost(experimental: false));
        Assert.Equal(ExpectedCore, listed);
        Assert.Equal(ExpectedCore, McpToolSurface.Core.Keys.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Experimental_tools_are_not_callable_by_default_not_even_through_an_alias()
    {
        var server = FullHost(experimental: false);
        foreach (var name in Experimental.Append("kol_preview_purchase").Append("kol_preview_transfer").Append("preview-purchase"))
        {
            var res = (await server.HandleAsync(JsonDocument.Parse(
                $"{{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{{\"name\":\"{name}\",\"arguments\":{{}}}}}}").RootElement))!.Value;
            Assert.Contains("Unknown MCP tool", res.GetRawText());
        }
    }

    [Fact]
    public async Task Experimental_flag_registers_the_extra_tools_and_offers_every_right()
    {
        var server = FullHost(experimental: true);
        var listed = await ListedAsync(server);
        Assert.Subset(listed.ToHashSet(), ExpectedCore.ToHashSet());
        Assert.Subset(listed.ToHashSet(), Experimental.ToHashSet());
        Assert.Equal(Rights.All, server.OfferedRights);
    }

    [Fact]
    public void Default_consent_offers_only_rights_of_core_tools()
    {
        var offered = FullHost(experimental: false).OfferedRights;
        Assert.Equal(
            [Rights.AccountRead, Rights.MarketRead, Rights.Adventure, Rights.MallBuy, Rights.DailyRun, Rights.MessageSend, Rights.ItemUse],
            offered);
        Assert.DoesNotContain(Rights.ShopRead, offered);
        Assert.DoesNotContain(Rights.ShopReprice, offered);
        Assert.DoesNotContain(Rights.TransferMeat, offered);
        Assert.DoesNotContain(Rights.TransferItems, offered);
    }

    [Fact]
    public void Every_core_tool_is_mapped_by_the_access_policy()
    {
        // An unmapped tool is refused over HTTP; a core tool must reach its grant check instead.
        var gate = new AccessGate(new GrantStore(Path.Combine(_dir, "g2.json")), new UsageLedger(Path.Combine(_dir, "u2.jsonl")));
        var authorize = McpAccessPolicy.ForToken(gate, () => "no-such-token", "Main");
        var empty = JsonDocument.Parse("{}").RootElement;
        foreach (var tool in ExpectedCore)
        {
            var decision = authorize(tool, empty);
            Assert.DoesNotContain("not available to AI clients", decision.Message ?? "");
        }
    }

    [Fact]
    public void Experimental_switch_reads_flag_and_environment()
    {
        Assert.True(McpToolSurface.ExperimentalRequested(["--experimental"]));
        var before = Environment.GetEnvironmentVariable(McpToolSurface.ExperimentalVariable);
        try
        {
            Environment.SetEnvironmentVariable(McpToolSurface.ExperimentalVariable, null);
            Assert.False(McpToolSurface.ExperimentalRequested([]));
            Environment.SetEnvironmentVariable(McpToolSurface.ExperimentalVariable, "1");
            Assert.True(McpToolSurface.ExperimentalRequested([]));
            Environment.SetEnvironmentVariable(McpToolSurface.ExperimentalVariable, "0");
            Assert.False(McpToolSurface.ExperimentalRequested([]));
        }
        finally { Environment.SetEnvironmentVariable(McpToolSurface.ExperimentalVariable, before); }
    }
}
