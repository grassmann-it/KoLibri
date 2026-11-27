using System.Text.Json;
using KoLibri.Safety.Access;
using KolLibri.Mcp;
using Xunit;

namespace KoLibri.Tests;

/// <summary>
/// 0.5 MCP cleanup (P3-W5): removed tools are gone, merged previews never act, price suggestions use
/// <c>ShopRepricer</c> and never count the own stores as competitors. Only the fake KoL, no live request.
/// </summary>
public sealed class McpParityTests : IDisposable
{
    private const int MallbotPlayerId = 3856805;
    private readonly string _dir = Directory.CreateTempSubdirectory("kolibri-mcp-parity-").FullName;
    private readonly FakeKol _kol = new();
    private readonly MemoryAccountStore _store = MemoryAccountStore.WithMain();
    private readonly McpAccounts _accounts;

    /// <summary>The adventure/combat planners belong to P2 and may still answer "not yet".</summary>
    private static readonly HashSet<string> P2Placeholders = new(StringComparer.Ordinal)
    {
        "kol_autocombat_preview", "kol_autocombat_action_preview", "kol_recovery_plan",
        "kol_adventure_bounded_preview", "kol_adventure_daily_schedule",
    };

    private static readonly string[] Removed =
    [
        "kol_price_recommendation", "kol_underprice_preview", "kol_workflow_plan",
        "kol_plan_daily_adventures", "kol_plan_daily_adventures_execute",
    ];

    public McpParityTests()
    {
        _store.SaveAsync(new AccountRecord(new Account(new AccountId("mallbot"), "Mallbot", MallbotPlayerId, null, Array.Empty<string>()),
            "PHPSESSID=def", null, null), default).GetAwaiter().GetResult();
        _accounts = McpTest.Accounts(_kol, _store, _dir);
    }

    public void Dispose()
    {
        McpRequestContext.Token = null;
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>Wired like the HTTP host: execution bridge, write tools, token authorizer.</summary>
    private (KolibriMcpServer Server, GrantStore Grants, AdventureMcpExecutionBridge Bridge) FullHost(bool experimental)
    {
        var bridge = new AdventureMcpExecutionBridge(_accounts);
        var server = new KolibriMcpServer(_accounts, new KolibriMcpServerOptions { ExperimentalTools = experimental }, executionBridge: bridge);
        var grants = new GrantStore(Path.Combine(_dir, "g.json"));
        var gate = new AccessGate(grants, new UsageLedger(Path.Combine(_dir, "u.jsonl")));
        server.EnableWriteTools(gate, new KolibriClientActions(_accounts));
        server.Authorizer = McpAccessPolicy.ForToken(gate, () => McpRequestContext.Token, "Main");
        return (server, grants, bridge);
    }

    private static async Task<string[]> ListedAsync(KolibriMcpServer server)
    {
        using var doc = JsonDocument.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        var res = (await server.HandleAsync(doc.RootElement.Clone()))!.Value;
        return res.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToArray();
    }

    // --- price suggestions -------------------------------------------------------------------------------------

    private static string ShopPage(long price) => $$"""
        <table><tr class="deets"><td><input type="text" name="price[10881]" value="{{price}}"></td>
        <td><b>11-leaf clover</b></td><td>3</td>
        <td><span class="tohide">{{price:N0}}</span><input type="text" class="hideit price" value="{{price}}"></td>
        <td><input name="limit[10881]" value="0"></td></tr></table>
        """;

    private static string Row(int store, string name, long price, int stock) => $"""
        <tr class="graybelow" id="stock_{store}_10881">
          <td style="border:none"></td>
          <td class="small store"><a href="mallstore.php?whichstore={store}"><b>{name}</b></a></td>
          <td class="small stock">{stock}</td>
          <td class="small">&nbsp;</td>
          <td class="small price"><a href="mallstore.php?whichstore={store}">{price:N0}&nbsp;Meat</a></td>
        </tr>
        """;

    private static string Mall(params string[] rows) => $"""
        <center><b>Search Results:</b> <b>(Items 1-{rows.Length} of {rows.Length})</b></center>
        <div id='searchresults'><a name="searchresults"></a>
        <br /><a name="results_10881"></a>
        <table class="itemtable" summary="Stores selling 11-leaf clover">
        <tr class="blackabove" id="item_10881"><td><img src='x.gif' alt="11-leaf clover" title="11-leaf clover"></td>
        <td><b><a href="/mall.php?pudnuggler=%2211-leaf+clover%22">11-leaf clover</a></b></td></tr>
        {string.Join("\n", rows)}
        </table></div>
        """;

    [Fact]
    public async Task Price_suggestions_ignore_own_store()
    {
        // Mallbot's own store is the cheapest listing (500); the only competitor sits at 2,000.
        _kol.On("GET backoffice.php", ShopPage(500));
        _kol.On("POST mall.php", Mall(Row(MallbotPlayerId, "Mallbot", 500, 3), Row(56226, "Crazy Gordon", 2000, 10)));

        var suggestions = await new KolibriClientActions(_accounts).PriceSuggestionsAsync(AccountRole.Mallbot, 20, default);

        var s = Assert.Single(suggestions);
        Assert.Equal("10881", s.ItemId);
        Assert.Equal(500, s.CurrentPrice);
        Assert.Equal(2000, s.CompetitorFloor);
        Assert.Equal(2000, s.MarketLowest);           // 0.4 field = competitor floor, not our own 500
        Assert.Equal(1990, s.SuggestedPrice);          // just below the competitor, i.e. a raise
        Assert.Equal("Up", s.Direction);
        Assert.Equal(1, s.OwnListingsExcluded);
        Assert.DoesNotContain(_kol.Requests, r => r.Method == "POST" && !r.Path.StartsWith("/mall.php", StringComparison.Ordinal));
        Assert.DoesNotContain(_kol.Requests, r => r.Path.Contains("managestore", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Price_suggestions_exclude_the_other_own_account_store_too()
    {
        // Main (player 1001 in MemoryAccountStore.WithMain) also sells; it must not be the floor for Mallbot either.
        var mainId = (await _accounts.Client.Accounts.ListAsync()).Single(a => a.Id.Value == "main").PlayerId!.Value;
        _kol.On("GET backoffice.php", ShopPage(500));
        _kol.On("POST mall.php", Mall(Row(mainId, "Main", 400, 1), Row(MallbotPlayerId, "Mallbot", 500, 3), Row(56226, "Crazy Gordon", 2000, 10)));

        var s = Assert.Single(await new KolibriClientActions(_accounts).PriceSuggestionsAsync(AccountRole.Mallbot, 20, default));

        Assert.Equal(2000, s.CompetitorFloor);
        Assert.Equal(2, s.OwnListingsExcluded);
        Assert.Equal("Up", s.Direction);
    }

    // --- removed / merged tools --------------------------------------------------------------------------------

    [Fact]
    public async Task No_tool_outside_adventure_answers_not_yet()
    {
        var (server, _, bridge) = FullHost(experimental: true);
        using var _ = bridge;
        var listed = await ListedAsync(server);
        Assert.DoesNotContain(listed, Removed.Contains);

        foreach (var tool in listed.Where(t => !P2Placeholders.Contains(t)))
        {
            McpRequestContext.Token = null;   // no grant: write tools refuse before anything is sent
            var response = await McpReadToolsTests.CallAsync(server, tool, """{"role":"Main"}""");
            Assert.DoesNotContain(KolibriMcpServer.NotYetMessage, response.GetRawText());
        }
        foreach (var tool in Removed.Append("price-recommendation").Append("underprice").Append("workflow-plan").Append("daily-plan-execute"))
            Assert.Contains("Unknown MCP tool", (await McpReadToolsTests.CallAsync(server, tool)).GetRawText());
        Assert.DoesNotContain(_kol.Requests, r => r.Method == "POST");
    }

    [Fact]
    public async Task Preview_purchase_alias_never_buys()
    {
        var (server, grants, bridge) = FullHost(experimental: true);
        using var _ = bridge;
        McpRequestContext.Token = grants.Create("ai", ["Main", "Mallbot"], new Dictionary<string, RightLimit>
        {
            [Rights.AccountRead] = new(),
            [Rights.MallBuy] = new() { DailyBudget = 100_000, MaxUnitPrice = 10_000 },
        }, TimeSpan.FromDays(1)).Token;

        // 0.4 argument shape (itemId as string, unitPrice) plus a planId that would execute kol_mall_buy: it is dropped.
        var response = await McpReadToolsTests.CallAsync(server, "kol_preview_purchase",
            """{"role":"Main","itemId":"10881","itemName":"11-leaf clover","quantity":2,"unitPrice":1500,"planId":"forged"}""");

        var result = response.GetProperty("result");
        Assert.False(result.GetProperty("isError").GetBoolean(), response.GetRawText());
        var body = result.GetProperty("structuredContent");
        Assert.True(body.GetProperty("preview").GetBoolean());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("planId").GetString()));
        Assert.Equal(1500, body.GetProperty("details").GetProperty("maxPricePerUnit").GetInt64());
        Assert.Equal(3000, body.GetProperty("details").GetProperty("worstCaseMeat").GetInt64());
        Assert.DoesNotContain(_kol.Requests, r => r.Method == "POST");
        Assert.DoesNotContain(_kol.Requests, r => r.Path.Contains("mallstore.php", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Preview_transfer_alias_only_previews()
    {
        var (server, grants, bridge) = FullHost(experimental: true);
        using var _ = bridge;
        McpRequestContext.Token = grants.Create("ai", ["Main", "Mallbot"], new Dictionary<string, RightLimit>
        {
            [Rights.AccountRead] = new(),
            [Rights.TransferItems] = new() { DailyBudget = 10 },
        }, TimeSpan.FromDays(1)).Token;

        var response = await McpReadToolsTests.CallAsync(server, "kol_preview_transfer",
            """{"itemId":"10881","quantity":1,"from":"Main","to":"Mallbot","reason":"x","planId":"forged"}""");

        var result = response.GetProperty("result");
        Assert.False(result.GetProperty("isError").GetBoolean(), response.GetRawText());
        Assert.True(result.GetProperty("structuredContent").GetProperty("preview").GetBoolean());
        Assert.DoesNotContain(_kol.Requests, r => r.Method == "POST");

        var same = await McpReadToolsTests.CallAsync(server, "kol_preview_transfer", """{"itemId":"1","quantity":1,"from":"Main","to":"Main"}""");
        Assert.True(same.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task Merged_previews_are_not_offered_by_default()
    {
        var (server, _, bridge) = FullHost(experimental: false);
        using var _ = bridge;
        foreach (var tool in new[] { "kol_preview_purchase", "kol_preview_transfer" })
            Assert.Contains("Unknown MCP tool", (await McpReadToolsTests.CallAsync(server, tool, """{"itemId":"1","quantity":1,"unitPrice":1}""")).GetRawText());
        Assert.Empty(_kol.Requests);
    }

    [Fact]
    public async Task Core_surface_still_has_19_tools()
    {
        Assert.Equal(19, McpToolSurface.Core.Count);
        var (server, _, bridge) = FullHost(experimental: false);
        using var _ = bridge;
        Assert.Equal(McpToolSurface.Core.Keys.Order(StringComparer.Ordinal), (await ListedAsync(server)).Order(StringComparer.Ordinal));
    }
}
