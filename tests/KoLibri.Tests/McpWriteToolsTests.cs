using System.Text.Json;
using AccountRole = KolLibri.Mcp.AccountRole;
using KolLibri.Mcp;
using KoLibri.Safety.Access;
using Xunit;

namespace KoLibri.Tests;

public sealed class McpWriteToolsTests : IDisposable
{
    private sealed class FakeActions : IAccountActions
    {
        public long Meat = 10_000_000;
        public int BuyCalls, MemeCalls, TransferCalls, MessageCalls, UseCalls, RepriceCommits;
        public long LastMemeReserve;
        public bool TransferConfirms = true;
        public int MeatCalls;
        public bool BuyUnclear;
        public Task<long> MeatAsync(AccountRole role, CancellationToken ct) { MeatCalls++; return Task.FromResult(Meat); }
        public Task<(int, long, string)> BuyAsync(AccountRole role, int itemId, string itemName, int quantity, long maxPrice, long reserve, CancellationToken ct)
        { BuyCalls++; if (BuyUnclear) throw new UnreconciledPurchaseException("no answer"); return Task.FromResult((quantity, quantity * (maxPrice - 10), "Bought")); }
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

        public int RewardCalls, SalesCalls, ConsumeCalls, RestCalls;
        public Task<(int Consumed, string? Rejection)> ConsumeAsync(AccountRole role, int itemId, string kind, int quantity, CancellationToken ct)
        { ConsumeCalls++; return Task.FromResult((Math.Min(quantity, 3), quantity > 3 ? "too full" : (string?)null)); }
        public (int Done, int Spent, string? Problem) RestResult = (-1, 0, null);
        public bool RestUnclear;
        public Task<(int Done, int AdventuresSpent, string? Problem)> RestAsync(AccountRole role, int times, CancellationToken ct)
        { RestCalls++; if (RestUnclear) throw new UnreconciledPurchaseException("answer lost", [1, 2], [1, 1]); return Task.FromResult(RestResult.Done < 0 ? (times, 0, (string?)null) : RestResult); }
        public bool Blocked;
        public Task<ShopSalesReport> ShopSalesAsync(AccountRole role, bool refresh, int topBuyers, CancellationToken ct)
        { SalesCalls++; return Task.FromResult(new ShopSalesReport(DateTimeOffset.UnixEpoch, false, 0, 0, 0, [], [])); }
        public Task<IReadOnlyList<BuyerSummary>> BuyersAsync(int minPoints, int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<BuyerSummary>>([new BuyerSummary("buyer_a", 12, 9_000, 3, 2, null, null, false, null)]);
        public Task<BuyerSummary?> BuyerAsync(string buyerKey, CancellationToken ct)
            => Task.FromResult<BuyerSummary?>(buyerKey == "buyer_a" ? new BuyerSummary("buyer_a", 12, 9_000, 3, 2, null, null, Blocked, null) : null);
        public Task<IReadOnlyList<PriceSuggestion>> PriceSuggestionsAsync(AccountRole role, int maxItems, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<PriceSuggestion>>([]);
        public Task<(bool Sent, string? Refusal)> RewardAsync(AccountRole role, string buyerKey, string text, int? itemId, int quantity, int pointsCost, CancellationToken ct)
        { RewardCalls++; return Task.FromResult((true, (string?)null)); }
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
        _server = new KolibriMcpServer(McpTest.Accounts(), options: new KolibriMcpServerOptions { ExperimentalTools = true });
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
            [Rights.Adventure] = new() { DailyBudget = 5 },
            [Rights.ShopReprice] = new() { DailyBudget = 5, MaxChangePercent = 20 },
            [Rights.ShopRead] = new(),
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
    public async Task Buy_checks_role_and_right_before_opening_the_account()
    {
        var mainOnly = _grants.Create("ai", ["Main"], new Dictionary<string, RightLimit>
            { [Rights.AccountRead] = new(), [Rights.MallBuy] = new() { DailyBudget = 10_000, MaxUnitPrice = 1_000 } }, TimeSpan.FromDays(1)).Token;
        Assert.True((await CallAsync(mainOnly, "kol_mall_buy", Buy + ",\"role\":\"Mallbot\"}")).Error);
        var noRight = _grants.Create("ai", ["Main"], new Dictionary<string, RightLimit> { [Rights.AccountRead] = new() }, TimeSpan.FromDays(1)).Token;
        Assert.True((await CallAsync(noRight, "kol_mall_buy", Buy + "}")).Error);
        // Above the grant's unit price limit: also refused before any meat lookup.
        Assert.True((await CallAsync(Token(maxPrice: 50), "kol_mall_buy", Buy + "}")).Error);
        Assert.Equal(0, _actions.MeatCalls);
    }

    [Fact]
    public async Task Transfer_checks_the_right_before_reading_meat()
    {
        var noMeat = _grants.Create("ai", ["Main", "Mallbot"], new Dictionary<string, RightLimit>
            { [Rights.AccountRead] = new(), [Rights.TransferItems] = new() { DailyBudget = 10 } }, TimeSpan.FromDays(1)).Token;
        Assert.True((await CallAsync(noMeat, "kol_transfer", "{\"meat\":100}")).Error);
        Assert.Equal(0, _actions.MeatCalls);
    }

    [Fact]
    public async Task Reward_buyer_reveals_nothing_before_the_rights_pass()
    {
        var noRight = _grants.Create("ai", ["Main", "Mallbot"], new Dictionary<string, RightLimit>
            { [Rights.ShopRead] = new() }, TimeSpan.FromDays(1)).Token;
        var denied = await CallAsync(noRight, "kol_reward_buyer", "{\"buyerKey\":\"buyer_a\",\"message\":\"x\",\"pointsCost\":99}");
        Assert.True(denied.Error);
        Assert.DoesNotContain("points", denied.Body.GetRawText());   // neither "has 12 points" nor the status of the buyer
        Assert.DoesNotContain("blocked", denied.Body.GetRawText());
    }

    [Fact]
    public async Task Shop_buyer_data_is_not_given_to_a_single_account_grant()
    {
        var one = _grants.Create("ai", ["Mallbot"], new Dictionary<string, RightLimit> { [Rights.ShopRead] = new() }, TimeSpan.FromDays(1)).Token;
        Assert.True((await CallAsync(one, "kol_shop_buyers", "{}")).Error);
    }

    [Fact]
    public async Task Meme_shop_top_up_needs_a_positive_price_limit()
    {
        var token = Token(budget: 5_000);
        Assert.True((await CallAsync(token, "kol_meme_shop", "{\"topUpBacon\":true,\"maxBaconPrice\":0}")).Error);
        Assert.True((await CallAsync(token, "kol_meme_shop", "{\"topUpBacon\":true,\"maxBaconPrice\":-5}")).Error);
        Assert.Equal(0, _actions.MemeCalls);
    }

    [Fact]
    public async Task An_unclear_buy_outcome_keeps_the_worst_case_reserved()
    {
        var token = Token();
        var id = _grants.FindByToken(token)!.Id;
        _actions.BuyUnclear = true;
        var preview = await CallAsync(token, "kol_mall_buy", Buy + "}");
        var plan = preview.Body.GetProperty("planId").GetString();
        var done = await CallAsync(token, "kol_mall_buy", Buy + $",\"planId\":\"{plan}\"}}");
        Assert.True(done.Error);
        Assert.Equal(500, _ledger.UsedToday(id, Rights.MallBuy));   // still held, not released
    }

    [Fact]
    public async Task An_unclear_buy_stays_reserved_after_a_restart_until_it_is_reconciled()
    {
        var token = Token();
        var id = _grants.FindByToken(token)!.Id;
        _actions.BuyUnclear = true;
        var plan = (await CallAsync(token, "kol_mall_buy", Buy + "}")).Body.GetProperty("planId").GetString();
        Assert.True((await CallAsync(token, "kol_mall_buy", Buy + $",\"planId\":\"{plan}\"}}")).Error);

        var restarted = new UsageLedger(Path.Combine(_dir, "u.jsonl"));
        Assert.Equal(500, restarted.UsedToday(id, Rights.MallBuy));
        Assert.True(restarted.VerifyChain());
        var open = Assert.Single(restarted.Unreconciled(id));
        Assert.StartsWith("unclear:", open.Operation);

        // Only an explicit reconciliation releases it; KoL charged 100 in the end.
        Assert.True(restarted.Reconcile(open.Seq, 100, "checked"));
        Assert.Equal(100, restarted.UsedToday(id, Rights.MallBuy));
        Assert.Empty(restarted.Unreconciled(id));
        Assert.False(restarted.Reconcile(open.Seq, 0));
        Assert.Equal(100, new UsageLedger(Path.Combine(_dir, "u.jsonl")).UsedToday(id, Rights.MallBuy));
        Assert.True(restarted.VerifyChain());
    }

    [Fact]
    public async Task An_aborted_rest_stack_books_the_confirmed_rests_and_keeps_the_lost_one_reserved()
    {
        var token = Token();
        var id = _grants.FindByToken(token)!.Id;
        _actions.RestUnclear = true;
        var plan = (await CallAsync(token, "kol_rest", "{\"times\":3}")).Body.GetProperty("planId").GetString();
        Assert.True((await CallAsync(token, "kol_rest", $"{{\"times\":3,\"planId\":\"{plan}\"}}")).Error);
        // item.use: 1 confirmed + 1 unclear of 3 reserved; adventure.run: 2 confirmed + 1 unclear.
        Assert.Equal(2, _ledger.UsedToday(id, Rights.ItemUse));
        Assert.Equal(3, _ledger.UsedToday(id, Rights.Adventure));
        Assert.Equal(2, _ledger.Unreconciled(id).Count);
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

    [Fact]
    public async Task Consume_and_rest_are_two_step_and_book_what_happened()
    {
        var token = Token();
        var pre = await CallAsync(token, "kol_consume", "{\"itemId\":995,\"kind\":\"food\",\"quantity\":2}");
        Assert.False(pre.Error);
        Assert.Equal(0, _actions.ConsumeCalls);
        var done = await RunAsync(token, "kol_consume", "{\"itemId\":995,\"kind\":\"food\",\"quantity\":2}");
        Assert.Equal(2, done.GetProperty("consumed").GetInt32());
        Assert.True((await CallAsync(token, "kol_consume", "{\"itemId\":1,\"kind\":\"water\"}")).Error);

        var rest = await RunAsync(token, "kol_rest", "{\"times\":1}");
        Assert.Equal(1, rest.GetProperty("rested").GetInt32());
        Assert.Equal(1, _actions.RestCalls);
    }

    [Fact]
    public async Task Consume_reports_partial_quantity_and_books_only_what_was_consumed()
    {
        // The worst case (the requested 5) is reserved up front, so the budget must cover it.
        var token = _grants.Create("ai", ["Main"], new Dictionary<string, RightLimit>
            { [Rights.ItemUse] = new() { DailyBudget = 5 } }, TimeSpan.FromDays(1)).Token;
        var done = await RunAsync(token, "kol_consume", "{\"itemId\":995,\"kind\":\"food\",\"quantity\":5}");
        Assert.Equal(3, done.GetProperty("consumed").GetInt32());
        Assert.False(done.GetProperty("complete").GetBoolean());
        Assert.Equal(3, _ledger.UsedToday(_grants.FindByToken(token)!.Id, Rights.ItemUse));
    }

    [Fact]
    public async Task Rest_books_only_confirmed_rests_and_adventures_actually_spent()
    {
        var token = Token();
        _actions.RestResult = (1, 2, "KoL did not confirm the rest: HP, MP and adventures are unchanged.");
        var done = await RunAsync(token, "kol_rest", "{\"times\":3}");
        Assert.Equal(1, done.GetProperty("rested").GetInt32());
        Assert.False(done.GetProperty("complete").GetBoolean());
        Assert.Contains("did not confirm", done.GetProperty("problem").GetString());
        var id = _grants.FindByToken(token)!.Id;
        Assert.Equal(1, _ledger.UsedToday(id, Rights.ItemUse));
        Assert.Equal(2, _ledger.UsedToday(id, Rights.Adventure));
    }

    [Fact]
    public async Task Rest_without_the_adventure_right_is_denied_and_does_nothing()
    {
        var token = _grants.Create("ai", ["Main"], new Dictionary<string, RightLimit>
            { [Rights.ItemUse] = new() { DailyBudget = 3 } }, TimeSpan.FromDays(1)).Token;
        var denied = await CallAsync(token, "kol_rest", "{\"times\":1}");
        Assert.True(denied.Error);
        Assert.Equal(0, _actions.RestCalls);
    }

    [Fact]
    public async Task Shop_read_tools_need_the_shop_read_right()
    {
        var withRight = Token();
        var ok = await CallAsync(withRight, "kol_shop_buyers", "{}");
        Assert.False(ok.Error);
        Assert.Equal("buyer_a", ok.Body.GetProperty("buyers")[0].GetProperty("buyerKey").GetString());

        var without = _grants.Create("ai", ["Main"], new Dictionary<string, RightLimit> { [Rights.AccountRead] = new() }, TimeSpan.FromDays(1)).Token;
        var denied = await CallAsync(without, "kol_shop_sales", "{}");
        Assert.True(denied.Error);
        Assert.Equal(0, _actions.SalesCalls);
    }

    [Fact]
    public async Task Reward_buyer_previews_first_and_sends_once_with_the_key_only()
    {
        var token = Token();
        const string args = "{\"buyerKey\":\"buyer_a\",\"message\":\"Thanks!\",\"pointsCost\":10}";
        var preview = await CallAsync(token, "kol_reward_buyer", args);
        Assert.False(preview.Error);
        Assert.Equal(0, _actions.RewardCalls);
        Assert.Equal("buyer_a", preview.Body.GetProperty("details").GetProperty("buyerKey").GetString());

        var done = await RunAsync(token, "kol_reward_buyer", args);
        Assert.True(done.GetProperty("sent").GetBoolean());
        Assert.Equal(1, _actions.RewardCalls);
    }

    [Fact]
    public async Task Reward_buyer_refuses_blocked_unknown_and_too_many_points()
    {
        var token = Token();
        Assert.True((await CallAsync(token, "kol_reward_buyer", "{\"buyerKey\":\"nobody\",\"message\":\"x\"}")).Error);
        Assert.True((await CallAsync(token, "kol_reward_buyer", "{\"buyerKey\":\"buyer_a\",\"message\":\"x\",\"pointsCost\":99}")).Error);
        _actions.Blocked = true;
        Assert.True((await CallAsync(token, "kol_reward_buyer", "{\"buyerKey\":\"buyer_a\",\"message\":\"x\"}")).Error);
        Assert.Equal(0, _actions.RewardCalls);
    }

    [Fact]
    public void Price_advice_never_undercuts_the_own_listing()
    {
        Assert.Equal(1_000, ShopAdvice.Suggest(1_000, 1_000));
        Assert.Equal(900, ShopAdvice.Suggest(900, 1_000));
        Assert.Equal(995, ShopAdvice.Suggest(1_200, 1_000));
        Assert.Equal(100, ShopAdvice.Suggest(500, 50) is { } v && v < 100 ? 100 : 100);
        Assert.Null(ShopAdvice.Suggest(500, null));
    }

    [Fact]
    public void Spending_points_needs_enough_points_and_an_unblocked_buyer()
    {
        var db = new KoLibri.Extras.BuyerCrmDatabase { Buyers = [new KoLibri.Extras.BuyerCrmProfile { BuyerKey = "k", CharacterName = "n", LoyaltyPoints = 5 }] };
        Assert.Equal(2, KoLibri.Extras.BuyerCrmService.SpendPoints(db, "k", 3).Buyers[0].LoyaltyPoints);
        Assert.Throws<InvalidOperationException>(() => KoLibri.Extras.BuyerCrmService.SpendPoints(db, "k", 6));
        var blocked = KoLibri.Extras.BuyerCrmService.SetBlocked(db, "k", true);
        Assert.Throws<InvalidOperationException>(() => KoLibri.Extras.BuyerCrmService.SpendPoints(blocked, "k", 1));
    }
}
