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

        var first = authorize("kol_adventure_run_execute", Args("{\"execute\":true,\"maxTurns\":25}"));
        Assert.True(first.Allowed);
        first.Reservation!.Commit(25);

        var second = authorize("kol_adventure_run_execute", Args("{\"execute\":true,\"maxTurns\":25}"));
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
}
