using System.Text.Json;
using KolArbitrage.Core.Basis;
using KolLibri.Mcp;
using LibKolibri.Access;
using Xunit;

namespace KolArbitrage.Core.Tests;

public sealed class McpWriteToolsTests : IDisposable
{
    private sealed class FakeActions : IAccountActions
    {
        public long Meat = 10_000_000;
        public int BuyCalls, MemeCalls;
        public Task<long> MeatAsync(AccountRole role, CancellationToken ct) => Task.FromResult(Meat);
        public Task<(int, long, string)> BuyAsync(AccountRole role, int itemId, string itemName, int quantity, long maxPrice, long reserve, CancellationToken ct)
        { BuyCalls++; return Task.FromResult((quantity, quantity * (maxPrice - 10), "Bought")); }
        public Task<(int, int, string, IReadOnlyList<string>)> MemeShopAsync(AccountRole role, bool topUp, long maxBaconPrice, long reserve, CancellationToken ct)
        { MemeCalls++; return Task.FromResult((2, 0, "Done", (IReadOnlyList<string>)new[] { "x" })); }
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
        => _grants.Create("ai", ["Main"], new Dictionary<string, RightLimit>
        {
            [Rights.AccountRead] = new(),
            [Rights.MallBuy] = new() { DailyBudget = budget, MaxUnitPrice = maxPrice },
            [Rights.DailyRun] = new() { DailyBudget = runs },
        }, TimeSpan.FromDays(1)).Token;

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
        Assert.Equal(500, preview.Body.GetProperty("worstCaseMeat").GetInt64());
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
}
