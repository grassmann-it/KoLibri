using System.Text.Json;
using KoLibri.Core.Basis;
using KolLibri.Mcp;
using LibKolibri.Access;
using Xunit;

namespace KoLibri.Core.Tests;

public sealed class McpWriteToolsTests : IDisposable
{
    private sealed class FakeActions : IAccountActions
    {
        public long Meat = 10_000_000;
        public int BuyCalls, MemeCalls, TransferCalls, MessageCalls, UseCalls, RepriceCommits;
        public long LastMemeReserve;
        public bool TransferConfirms = true;
        public Task<long> MeatAsync(AccountRole role, CancellationToken ct) => Task.FromResult(Meat);
        public Task<(int, long, string)> BuyAsync(AccountRole role, int itemId, string itemName, int quantity, long maxPrice, long reserve, CancellationToken ct)
        { BuyCalls++; return Task.FromResult((quantity, quantity * (maxPrice - 10), "Bought")); }
        public Task<(int, int, long, string, IReadOnlyList<string>)> MemeShopAsync(AccountRole role, bool topUp, long maxBaconPrice, long reserve, CancellationToken ct)
        { MemeCalls++; LastMemeReserve = reserve; return Task.FromResult((2, topUp ? 10 : 0, topUp ? 1_200L : 0L, "Done", (IReadOnlyList<string>)new[] { "x" })); }
        public Task<bool> TransferAsync(AccountRole from, int? itemId, int quantity, long meat, CancellationToken ct)
        { TransferCalls++; return Task.FromResult(TransferConfirms); }
        public Task<bool> MessageAsync(AccountRole from, string recipient, string text, CancellationToken ct)
        { MessageCalls++; return Task.FromResult(true); }
        public Task<(int, string?)> UseItemAsync(AccountRole role, int itemId, int quantity, CancellationToken ct)
        { UseCalls++; return Task.FromResult((Math.Min(quantity, 2), (string?)"stopped")); }
        public Task<IReadOnlyList<RepriceLine>> RepriceAsync(AccountRole role, IReadOnlyList<PriceChange> changes, bool commit, CancellationToken ct)
        {
            if (commit) RepriceCommits++;
            return Task.FromResult<IReadOnlyList<RepriceLine>>(changes.Select(c => new RepriceLine(c.ItemId, c.ExpectedPrice, c.ExpectedPrice, c.NewPrice, commit ? "Updated" : "Ready", null)).ToArray());
        }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kolibri-wt-" + Guid.NewGuid().ToString("N"));
    private readonly GrantStore _grants;
    private readonly UsageLedger _ledger;
    private readonly AccessGate _gate;
    private readonly FakeActions _actions = new();
    private readonly KolibriMcpServer _server;

    public McpWriteToolsTests()
    {
        _grants = new GrantStore(Path.Combine(_dir, "g.json"));
        _ledger = new UsageLedger(Path.Combine(_dir, "u.jsonl"));
        _gate = new AccessGate(_grants, _ledger);
        _server = new KolibriMcpServer();
        _server.EnableWriteTools(_gate, _actions);
        _server.Authorizer = McpAccessPolicy.ForToken(_gate, () => McpRequestContext.Token, "Main");
    }

    public void Dispose() { McpRequestContext.Token = null; try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string Token(long budget = 10_000, long? maxPrice = 1_000, long runs = 1)
        => _grants.Create("ai", ["Main", "Mallbot"], new Dictionary<string, RightLimit>
        {
            [Rights.AccountRead] = new(),
            [Rights.MallBuy] = new() { DailyBudget = budget, MaxUnitPrice = maxPrice },
            [Rights.DailyRun] = new() { DailyBudget = runs },
            [Rights.TransferItems] = new() { DailyBudget = 10 },
            [Rights.TransferMeat] = new() { DailyBudget = 1_000 },
            [Rights.MessageSend] = new() { DailyBudget = 1 },
            [Rights.ItemUse] = new() { DailyBudget = 3 },
            [Rights.ShopReprice] = new() { DailyBudget = 5, MaxChangePercent = 20 },
        }, TimeSpan.FromDays(1)).Token;

    private async Task<JsonElement> RunAsync(string token, string tool, string argsJson)
    {
        var preview = await CallAsync(token, tool, argsJson);
        Assert.False(preview.Error, preview.Body.GetRawText());
        var plan = preview.Body.GetProperty("planId").GetString();
        var withPlan = argsJson == "{}" ? $"{{\"planId\":\"{plan}\"}}" : argsJson[..^1] + $",\"planId\":\"{plan}\"}}";
        var done = await CallAsync(token, tool, withPlan);
        return done.Body;
    }

    private async Task<(bool Error, JsonElement Body)> CallAsync(string? token, string tool, string argsJson)
    {
        McpRequestContext.Token = token;
        var req = JsonDocument.Parse($"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{{\"name\":\"{tool}\",\"arguments\":{argsJson}}}}}").RootElement;
        var res = (await _server.HandleAsync(req))!.Value.GetProperty("result");
        var text = res.GetProperty("content")[0].GetProperty("text").GetString()!;
        var body = text.TrimStart().StartsWith('{') ? JsonDocument.Parse(text).RootElement.Clone() : JsonDocument.Parse(JsonSerializer.Serialize(new { message = text })).RootElement.Clone();
        return (res.GetProperty("isError").GetBoolean(), body);
    }

    private const string Buy = "{\"itemId\":7,\"itemName\":\"thing\",\"quantity\":5,\"maxPricePerUnit\":100";

    [Fact]
    public async Task Preview_spends_nothing_and_execute_books_what_was_spent_once()
    {
        var token = Token();
        var preview = await CallAsync(token, "kol_mall_buy", Buy + "}");
        Assert.False(preview.Error);
        Assert.Equal(0, _actions.BuyCalls);
        Assert.Equal(500, preview.Body.GetProperty("details").GetProperty("worstCaseMeat").GetInt64());
        var plan = preview.Body.GetProperty("planId").GetString();

        var done = await CallAsync(token, "kol_mall_buy", Buy + $",\"planId\":\"{plan}\"}}");
        Assert.False(done.Error);
        Assert.Equal(450, done.Body.GetProperty("spentMeat").GetInt64());     // 5 x 90, booked as confirmed
        Assert.Equal(450, _ledger.UsedToday(_grants.FindByToken(token)!.Id, Rights.MallBuy));

        var again = await CallAsync(token, "kol_mall_buy", Buy + $",\"planId\":\"{plan}\"}}");
        Assert.True(again.Error);
        Assert.Equal(1, _actions.BuyCalls);
    }

    [Fact]
    public async Task Plan_does_not_work_for_changed_arguments()
    {
        var token = Token();
        var plan = (await CallAsync(token, "kol_mall_buy", Buy + "}")).Body.GetProperty("planId").GetString();
        var other = await CallAsync(token, "kol_mall_buy", Buy.Replace("\"quantity\":5", "\"quantity\":9") + $",\"planId\":\"{plan}\"}}");
        Assert.True(other.Error);
        Assert.Equal(0, _actions.BuyCalls);
    }

    [Fact]
    public async Task Price_ceiling_budget_and_reserve_refuse_at_preview()
    {
        var token = Token(budget: 400, maxPrice: 100);
        Assert.Contains("PriceAboveLimit", (await CallAsync(token, "kol_mall_buy", Buy.Replace("100", "101") + "}")).Body.GetProperty("message").GetString());
        Assert.Contains("BudgetExhausted", (await CallAsync(token, "kol_mall_buy", Buy + "}")).Body.GetProperty("message").GetString()); // 500 > 400
    }

    [Fact]
    public async Task Without_a_token_nothing_runs()
    {
        var res = await CallAsync(null, "kol_mall_buy", Buy + "}");
        Assert.True(res.Error);
        Assert.Equal(0, _actions.BuyCalls);
    }

    [Fact]
    public async Task Meme_shop_counts_one_run_and_a_second_is_over_budget()
    {
        var token = Token(runs: 1);
        var plan = (await CallAsync(token, "kol_meme_shop", "{}")).Body.GetProperty("planId").GetString();
        var run = await CallAsync(token, "kol_meme_shop", $"{{\"planId\":\"{plan}\"}}");
        Assert.False(run.Error);
        Assert.Equal(1, _actions.MemeCalls);

        var second = await CallAsync(token, "kol_meme_shop", "{}");
        Assert.True(second.Error);
        Assert.Contains("BudgetExhausted", second.Body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Write_tools_are_absent_over_stdio_policy()
    {
        Assert.False(McpAccessPolicy.ReadOnly("kol_mall_buy", JsonDocument.Parse("{}").RootElement).Allowed);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task All_write_tools_are_listed_with_valid_schemas()
    {
        var res = (await _server.HandleAsync(JsonDocument.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}").RootElement))!.Value;
        var names = res.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Where(t => t.GetProperty("inputSchema").GetProperty("type").GetString() == "object")
            .Select(t => t.GetProperty("name").GetString()).ToHashSet();
        foreach (var n in new[] { "kol_mall_buy", "kol_meme_shop", "kol_transfer", "kol_send_message", "kol_use_item", "kol_reprice_shop" })
            Assert.Contains(n, names);
    }

    [Fact]
    public async Task Transfer_books_items_and_meat_only_when_kol_confirms()
    {
        var token = Token();
        _actions.TransferConfirms = false;
        var failed = await RunAsync(token, "kol_transfer", "{\"role\":\"Mallbot\",\"itemId\":5,\"quantity\":3,\"meat\":100}");
        Assert.False(failed.GetProperty("confirmed").GetBoolean());
        var id = _grants.FindByToken(token)!.Id;
        Assert.Equal(0, _ledger.UsedToday(id, Rights.TransferItems));

        _actions.TransferConfirms = true;
        var ok = await RunAsync(token, "kol_transfer", "{\"role\":\"Mallbot\",\"itemId\":5,\"quantity\":3,\"meat\":100}");
        Assert.True(ok.GetProperty("confirmed").GetBoolean());
        Assert.Equal(3, _ledger.UsedToday(id, Rights.TransferItems));
        Assert.Equal(100, _ledger.UsedToday(id, Rights.TransferMeat));
    }

    [Fact]
    public async Task Transfer_needs_both_accounts_in_the_approval()
    {
        var only = _grants.Create("ai", ["Main"], new Dictionary<string, RightLimit>
        { [Rights.AccountRead] = new(), [Rights.TransferItems] = new() { DailyBudget = 10 } }, TimeSpan.FromDays(1)).Token;
        var res = await CallAsync(only, "kol_transfer", "{\"itemId\":5,\"quantity\":1}");
        Assert.True(res.Error);
        Assert.Equal(0, _actions.TransferCalls);
    }

    [Fact]
    public async Task Message_budget_is_one_and_text_is_in_the_preview()
    {
        var token = Token();
        var preview = await CallAsync(token, "kol_send_message", "{\"recipient\":\"Bob\",\"message\":\"hi\"}");
        Assert.Equal("hi", preview.Body.GetProperty("details").GetProperty("message").GetString());
        await RunAsync(token, "kol_send_message", "{\"recipient\":\"Bob\",\"message\":\"hi\"}");
        Assert.Equal(1, _actions.MessageCalls);
        var second = await CallAsync(token, "kol_send_message", "{\"recipient\":\"Bob\",\"message\":\"again\"}");
        Assert.True(second.Error);
        Assert.Contains("BudgetExhausted", second.Body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Item_use_books_only_confirmed_uses()
    {
        var token = Token();
        await RunAsync(token, "kol_use_item", "{\"itemId\":9,\"quantity\":3}");
        Assert.Equal(2, _ledger.UsedToday(_grants.FindByToken(token)!.Id, Rights.ItemUse));
        Assert.True((await CallAsync(token, "kol_use_item", "{\"itemId\":9,\"quantity\":2}")).Error);   // 2 + 2 > 3
    }

    [Fact]
    public async Task Reprice_refuses_changes_above_the_percent_limit_and_commits_otherwise()
    {
        var token = Token();
        var tooBig = await CallAsync(token, "kol_reprice_shop", "{\"updates\":[{\"itemId\":4,\"expectedPrice\":1000,\"newPrice\":1300}]}");
        Assert.True(tooBig.Error);
        Assert.Contains("ChangeAboveLimit", tooBig.Body.GetProperty("message").GetString());

        var ok = await RunAsync(token, "kol_reprice_shop", "{\"updates\":[{\"itemId\":4,\"expectedPrice\":1000,\"newPrice\":1100}]}");
        Assert.Equal(1, ok.GetProperty("updated").GetInt32());
        Assert.Equal(1, _actions.RepriceCommits);
    }

    [Fact]
    public async Task Bacon_top_up_needs_mall_buy_and_counts_against_its_budget()
    {
        var noMall = _grants.Create("ai", ["Main"], new Dictionary<string, RightLimit>
        { [Rights.AccountRead] = new(), [Rights.DailyRun] = new() { DailyBudget = 1 } }, TimeSpan.FromDays(1)).Token;
        Assert.True((await CallAsync(noMall, "kol_meme_shop", "{\"topUpBacon\":true}")).Error);

        var token = Token(budget: 5_000);
        await RunAsync(token, "kol_meme_shop", "{\"topUpBacon\":true,\"maxBaconPrice\":500}");
        Assert.Equal(1_200, _ledger.UsedToday(_grants.FindByToken(token)!.Id, Rights.MallBuy));
        Assert.Equal(10_000_000 - 5_000, _actions.LastMemeReserve);   // reserve keeps the spend inside the budget
    }
}
