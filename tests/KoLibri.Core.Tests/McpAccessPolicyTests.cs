using System.Text.Json;
using KolLibri.Mcp;
using LibKolibri.Access;
using Xunit;

namespace KoLibri.Core.Tests;

public sealed class McpAccessPolicyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kolibri-policy-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Adventure_execute_counts_against_the_daily_budget_and_preview_does_not()
    {
        var grants = new GrantStore(Path.Combine(_dir, "g.json"));
        var gate = new AccessGate(grants, new UsageLedger(Path.Combine(_dir, "u.jsonl")));
        var token = grants.Create("ai", ["Main"], new Dictionary<string, RightLimit>
        {
            [Rights.AccountRead] = new(), [Rights.Adventure] = new() { DailyBudget = 30 },
        }, TimeSpan.FromDays(1)).Token;
        var authorize = McpAccessPolicy.ForToken(gate, () => token, "Main");

        Assert.True(authorize("kol_adventure_run_execute", Args("{\"execute\":false,\"maxTurns\":500}")).Allowed);

        ToolAuthorization Run()
        {
            var a = "{\"execute\":true,\"maxTurns\":25";
            var p = authorize("kol_adventure_run_execute", Args(a + "}"));
            if (!p.Allowed || p.Plan is null) return p;
            return authorize("kol_adventure_run_execute", Args(a + ",\"planId\":\"" + p.Plan.PlanId + "\"}"));
        }
        var first = Run();
        Assert.True(first.Allowed);
        first.Reservation!.Commit(25);

        var second = Run();
        Assert.False(second.Allowed);
        Assert.Contains("BudgetExhausted", second.Message);
    }

    [Fact]
    public void Stdio_policy_allows_reads_and_refuses_execution()
    {
        Assert.True(McpAccessPolicy.ReadOnly("kol_status", Args("{}")).Allowed);
        Assert.False(McpAccessPolicy.ReadOnly("kol_adventure_run_execute", Args("{\"execute\":true}")).Allowed);
        Assert.False(McpAccessPolicy.ReadOnly("kol_login_clear", Args("{}")).Allowed);
    }

    private (AccessGate Gate, string Token, Func<string, JsonElement, ToolAuthorization> Authorize) Setup(string[] accounts, long adventures = 30)
    {
        var grants = new GrantStore(Path.Combine(_dir, "g.json"));
        var gate = new AccessGate(grants, new UsageLedger(Path.Combine(_dir, "u.jsonl")));
        var token = grants.Create("ai", accounts, new Dictionary<string, RightLimit>
        {
            [Rights.AccountRead] = new(), [Rights.Adventure] = new() { DailyBudget = adventures },
        }, TimeSpan.FromDays(1)).Token;
        var f = McpAccessPolicy.ForToken(gate, () => token, "Main");
        return (gate, token, (t, a) => f(t, a));
    }

    [Fact]
    public void Execute_must_be_a_boolean_in_policy_and_cannot_be_smuggled_as_a_string()
    {
        var (_, _, authorize) = Setup(["Main"]);
        var r = authorize("kol_adventure_run_execute", Args("{\"execute\":\"true\",\"maxTurns\":5}"));
        Assert.False(r.Allowed);
        Assert.Contains("execute", r.Message);
        Assert.False(McpAccessPolicy.ReadOnly("kol_adventure_run_execute", Args("{\"execute\":\"true\"}")).Allowed);
    }

    [Fact]
    public void Daily_bridge_roles_are_all_authorized_and_a_main_only_grant_cannot_reach_mallbot()
    {
        var (_, _, authorize) = Setup(["Main"]);
        Assert.True(authorize("kol_run_daily_bridge", Args("{\"roles\":[\"Main\"]}")).Allowed);
        var both = authorize("kol_run_daily_bridge", Args("{\"roles\":[\"all\"]}"));
        Assert.False(both.Allowed);
        Assert.Contains("AccountNotGranted", both.Message);
        Assert.False(authorize("kol_run_daily_bridge", Args("{\"roles\":[\"Main\",\"Mallbot\"]}")).Allowed);
        Assert.False(authorize("kol_run_daily_bridge", Args("{\"roles\":\"Mallbot\"}")).Allowed);
    }

    [Fact]
    public void Read_tools_refuse_execute_so_nothing_mutates()
    {
        var (_, _, authorize) = Setup(["Main"]);
        Assert.False(authorize("kol_run_daily_bridge", Args("{\"execute\":true}")).Allowed);
        Assert.False(McpAccessPolicy.ReadOnly("kol_run_daily_bridge", Args("{\"execute\":true}")).Allowed);
    }

    [Fact]
    public void Login_status_without_role_needs_at_least_one_granted_account_and_with_role_that_one()
    {
        var (_, _, authorize) = Setup(["Mallbot"]);
        Assert.True(authorize("kol_login_status", Args("{}")).Allowed);
        Assert.False(authorize("kol_login_status", Args("{\"role\":\"Main\"}")).Allowed);
        Assert.True(authorize("kol_login_status", Args("{\"role\":\"Mallbot\"}")).Allowed);
    }

    [Fact]
    public void Adventure_execute_needs_a_single_use_plan_and_the_plan_is_bound_to_the_arguments()
    {
        var (_, _, authorize) = Setup(["Main"]);
        var a = "{\"execute\":true,\"maxTurns\":5,\"locationId\":\"x\"";
        var preview = authorize("kol_adventure_run_execute", Args(a + "}"));
        Assert.True(preview.Allowed);
        Assert.NotNull(preview.Plan);
        Assert.Null(preview.Reservation);

        var changed = authorize("kol_adventure_run_execute", Args(a.Replace("5", "6") + ",\"planId\":\"" + preview.Plan!.PlanId + "\"}"));
        Assert.False(changed.Allowed);   // consumed by the mismatch attempt too

        var preview2 = authorize("kol_adventure_run_execute", Args(a + "}"));
        var run = authorize("kol_adventure_run_execute", Args(a + ",\"planId\":\"" + preview2.Plan!.PlanId + "\"}"));
        Assert.True(run.Allowed);
        Assert.Null(run.Plan);
        Assert.NotNull(run.Reservation);
        run.Reservation!.Release();
        var replay = authorize("kol_adventure_run_execute", Args(a + ",\"planId\":\"" + preview2.Plan.PlanId + "\"}"));
        Assert.False(replay.Allowed);
        Assert.Contains("PlanInvalid", replay.Message);
    }

    [Fact]
    public void Adventure_budget_uses_the_largest_cap_and_rejects_bad_numbers()
    {
        var (_, _, authorize) = Setup(["Main"], adventures: 10);
        Assert.Contains("BudgetExhausted", authorize("kol_adventure_run_execute", Args("{\"execute\":true,\"maxTurns\":3}")).Message);   // default 20 steps
        Assert.False(authorize("kol_adventure_run_execute", Args("{\"execute\":true,\"maxTurns\":\"3\"}")).Allowed);
        Assert.False(authorize("kol_adventure_run_execute", Args("{\"execute\":true,\"maxTurns\":-1}")).Allowed);
    }

    [Fact]
    public void Single_role_tools_authorize_the_role_they_use_and_reject_roles()
    {
        var (_, _, authorize) = Setup(["Mallbot"]);
        // No role: the handler uses the default (Main), which this grant does not cover.
        Assert.False(authorize("kol_status", Args("{}")).Allowed);
        Assert.False(authorize("kol_inventory", Args("{\"roles\":[\"Mallbot\"]}")).Allowed);   // 'roles' is not accepted here
        Assert.False(authorize("kol_status", Args("{\"role\":\"Main\",\"roles\":[\"Mallbot\"]}")).Allowed);
        Assert.True(authorize("kol_status", Args("{\"role\":\"Mallbot\"}")).Allowed);
        Assert.True(authorize("kol_inventory", Args("{\"role\":\"Mallbot\"}")).Allowed);
    }

    [Fact]
    public void Purchase_preview_needs_the_account_read_right_besides_market_read()
    {
        var grants = new GrantStore(Path.Combine(_dir, "g.json"));
        var gate = new AccessGate(grants, new UsageLedger(Path.Combine(_dir, "u.jsonl")));
        var market = grants.Create("ai", ["Main"], new Dictionary<string, RightLimit> { [Rights.MarketRead] = new() }, TimeSpan.FromDays(1)).Token;
        var both = grants.Create("ai", ["Main"], new Dictionary<string, RightLimit> { [Rights.MarketRead] = new(), [Rights.AccountRead] = new() }, TimeSpan.FromDays(1)).Token;
        var current = market;
        var authorize = McpAccessPolicy.ForToken(gate, () => current, "Main");

        var denied = authorize("kol_preview_purchase", Args("{}"));
        Assert.False(denied.Allowed);
        Assert.Contains("account.read", denied.Message);
        Assert.True(authorize("kol_price_summary", Args("{}")).Allowed);
        current = both;
        Assert.True(authorize("kol_preview_purchase", Args("{}")).Allowed);
    }

    [Fact]
    public void Buyer_ranking_needs_shop_read_on_every_account()
    {
        var grants = new GrantStore(Path.Combine(_dir, "g.json"));
        var gate = new AccessGate(grants, new UsageLedger(Path.Combine(_dir, "u.jsonl")));
        var one = grants.Create("ai", ["Main"], new Dictionary<string, RightLimit> { [Rights.ShopRead] = new() }, TimeSpan.FromDays(1)).Token;
        var two = grants.Create("ai", ["Main", "Mallbot"], new Dictionary<string, RightLimit> { [Rights.ShopRead] = new() }, TimeSpan.FromDays(1)).Token;
        var current = one;
        var authorize = McpAccessPolicy.ForToken(gate, () => current, "Main");
        Assert.False(authorize("kol_shop_buyers", Args("{}")).Allowed);
        current = two;
        Assert.True(authorize("kol_shop_buyers", Args("{}")).Allowed);
    }
}
