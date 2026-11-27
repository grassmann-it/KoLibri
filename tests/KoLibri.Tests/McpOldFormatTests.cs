using System.Text.Json;
using KolLibri.Mcp;
using KoLibri.Safety.Access;
using Xunit;

namespace KoLibri.Tests;

/// <summary>
/// The internal access layer in KoLibri.Safety reads the files 0.4 wrote (Fixtures/access-0.4, produced by
/// LibKolibri.Access and the 0.4 MCP OAuth server): grants, usage ledger with holds, its lock and the OAuth clients.
/// </summary>
public sealed class McpOldFormatTests : IDisposable
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // The fixtures were written on KoL day 2026-11-26 (ET, rollover 03:30); this is still that day.
    private static readonly TimeProvider Clock = new FixedClock(new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.Zero));

    private readonly string _dir = Directory.CreateTempSubdirectory("kolibri-access-04-").FullName;
    private readonly string _token;

    public McpOldFormatTests()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "access-0.4");
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(_dir, Path.GetFileName(file)));
        _token = File.ReadAllText(Path.Combine(_dir, "grant-token.txt")).Trim();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    [Fact]
    public void Grants_of_0_4_are_found_by_their_token_with_all_limits()
    {
        var grants = new GrantStore(P("grants.json"), Clock);

        var grant = grants.FindByToken(_token);

        Assert.NotNull(grant);
        Assert.Equal("ai-04", grant!.ClientName);
        Assert.Equal(new[] { "Main" }, grant.Accounts);
        Assert.Equal(100_000, grant.Rights[Rights.MallBuy].DailyBudget);
        Assert.Equal(50_000, grant.Rights[Rights.MallBuy].MaxUnitPrice);
        Assert.Equal(10, grant.Rights[Rights.Adventure].DailyBudget);
        Assert.Null(grants.FindByToken(_token + "x"));
    }

    [Fact]
    public void Usage_ledger_of_0_4_keeps_its_chain_counts_unclear_spends_and_adopts_a_dead_hold()
    {
        var grantId = new GrantStore(P("grants.json"), Clock).FindByToken(_token)!.Id;

        var ledger = new UsageLedger(P("usage.jsonl"), Clock);

        Assert.True(ledger.VerifyChain());
        Assert.Equal(3, ledger.UsedToday(grantId, Rights.Adventure));
        // 5,000 confirmed + 15,000 unclear + 7,000 held by the ended 0.4 process (adopted as unclear).
        Assert.Equal(27_000, ledger.UsedToday(grantId, Rights.MallBuy));
        Assert.Equal(2, ledger.Unreconciled(grantId).Count);
        Assert.Equal("[]", File.ReadAllText(P("usage.jsonl.holds")).Trim());

        // New entries continue the same chain in the same line format.
        ledger.TryReserve(grantId, Rights.Adventure, "kol_adventure_run_execute", "Main", 2, 10)!.Commit(2, "0.5");
        Assert.True(new UsageLedger(P("usage.jsonl"), Clock).VerifyChain());
        var last = JsonDocument.Parse(File.ReadAllLines(P("usage.jsonl")).Last()).RootElement;
        Assert.Equal(5, last.GetProperty("seq").GetInt64());
        Assert.Equal("2026-11-26", last.GetProperty("kolDay").GetString());
        Assert.Equal(64, last.GetProperty("prevHash").GetString()!.Length);
    }

    [Fact]
    public void Access_gate_on_the_0_4_files_counts_the_old_usage_against_the_budget()
    {
        var grants = new GrantStore(P("grants.json"), Clock);
        var gate = new AccessGate(grants, new UsageLedger(P("usage.jsonl"), Clock), Clock);

        Assert.Equal(73_000, gate.RemainingToday(_token, Rights.MallBuy));
        Assert.Equal(7, gate.RemainingToday(_token, Rights.Adventure));
    }

    [Fact]
    public async Task The_0_4_lock_file_still_serialises_writers_across_processes()
    {
        var grantId = new GrantStore(P("grants.json"), Clock).FindByToken(_token)!.Id;
        var ledger = new UsageLedger(P("usage.jsonl"), Clock);

        Task<UsageReservation?> reserve;
        using (new FileStream(P("usage.jsonl.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            reserve = Task.Run(() => ledger.TryReserve(grantId, Rights.Adventure, "kol_adventure_run_execute", "Main", 1, 10));
            await Task.Delay(300);
            Assert.False(reserve.IsCompleted); // another process (0.4 or 0.5) holds the lock
        }

        var reservation = await reserve.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(reservation);
        reservation!.Release();
    }

    [Fact]
    public void OAuth_clients_of_0_4_are_found_and_rewritten_in_the_same_shape()
    {
        var grants = new GrantStore(P("grants.json"), Clock);
        var oauth = new McpOAuthServer(grants, P("mcp-oauth-clients.json"), Clock);

        var client = oauth.FindClient("mcp_c_Y2xpZW50LTA0");
        Assert.NotNull(client);
        Assert.Equal("Claude", client!.ClientName);
        Assert.True(McpOAuthServer.RedirectMatches(client, "https://claude.ai/api/mcp/auth_callback"));

        Assert.NotNull(oauth.Register("second", ["http://127.0.0.1:5000/cb"]));
        var saved = JsonDocument.Parse(File.ReadAllText(P("mcp-oauth-clients.json"))).RootElement;
        Assert.Equal(2, saved.GetArrayLength());
        Assert.All(saved.EnumerateArray(), c =>
        {
            Assert.True(c.TryGetProperty("clientId", out _));
            Assert.True(c.TryGetProperty("clientName", out _));
            Assert.True(c.TryGetProperty("redirectUris", out _));
        });
        Assert.NotNull(new McpOAuthServer(grants, P("mcp-oauth-clients.json"), Clock).FindClient("mcp_c_Y2xpZW50LTA0"));
    }
}
