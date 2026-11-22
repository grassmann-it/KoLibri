using KoLibri.Core.Markt;
using KoLibri.Core.Spiel;
using Xunit;

namespace KoLibri.Core.Tests;

/// <summary>Money paths: commit-time reserve, stale reads, and the cross-process, fail-closed spend ledger.</summary>
public sealed class MoneyPathTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 18, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }

    private static MallPurchaseObservation Observed(long meat)
        => new(new MallPurchaseOffer("56226", "56226", "10881", 25_000, 10, DateTimeOffset.UtcNow, 5),
            new MallPurchaseAccountState("mallbot", meat, 0, 2_000_000, new Dictionary<string, int>(), DateTimeOffset.UtcNow));

    private static ItemArbitragePlan Plan() => new()
    {
        ItemId = 10881,
        Steps = [new ArbitrageStep("10881", "56226", "Crazy Gordon", 25_000, 2, 50_000, 5_000, 10_000, 5, MallLimitKind.Limited, null)]
    };

    private static ItemArbitrageExecutor Executor(MallPurchaseFixtureTransport t, ArbitrageSpendLedger ledger, long reserve, long? budget = null)
        => new(new MallPurchaseGateway(t, new MallPurchasePolicy
        {
            DryRun = false, RequireApproval = false, MaxDailySpend = 2_000_000, MaxMeatPerPurchase = 500_000,
            MaxQuantityPerPurchase = 10, MeatReserve = reserve
        }), ledger, "mallbot", budget);

    private static string TempDir() => Directory.CreateTempSubdirectory("money-").FullName;

    [Fact]
    public async Task Meat_drop_between_plan_and_commit_cannot_go_below_the_reserve()
    {
        var t = new MallPurchaseFixtureTransport(Observed(1_000_000));
        t.EnqueueRefresh(Observed(1_000_000));   // preview: plenty of meat
        t.EnqueueRefresh(Observed(120_000));     // commit-time reading: 120k - 50k = 70k < reserve 100k
        var ledger = new ArbitrageSpendLedger();
        var run = await Executor(t, ledger, reserve: 100_000).ExecuteAsync(Plan());

        Assert.Equal(ArbitrageStepStatus.NotSent, Assert.Single(run.Steps).Status);
        Assert.Equal(0, t.CommitCount);
        Assert.Equal(0, ledger.SpentToday("mallbot"));
        Assert.Equal(0, ledger.UnclearToday("mallbot"));
    }

    [Fact]
    public async Task Meat_above_the_reserve_still_buys()
    {
        var t = new MallPurchaseFixtureTransport(Observed(1_000_000));
        var run = await Executor(t, new ArbitrageSpendLedger(), reserve: 100_000).ExecuteAsync(Plan());
        Assert.Equal(ArbitrageStepStatus.Purchased, Assert.Single(run.Steps).Status);
    }

    private sealed class Reads(bool staleStatus, bool staleInventory) : IAccountReadClient
    {
        public Task<KoLStatusSnapshot> FetchStatusAsync(bool forceRefresh = false, CancellationToken ct = default)
            => Task.FromResult(new KoLStatusSnapshot("x", 10, 500_000, 1, Now, staleStatus));
        public Task<KoLInventorySnapshot> FetchInventoryAsync(bool forceRefresh = false, CancellationToken ct = default)
            => Task.FromResult(new KoLInventorySnapshot([new KoLInventoryItem("1", "a", 2)], Now, staleInventory));
    }

    [Fact]
    public async Task Stale_status_or_inventory_is_refused_with_a_readable_message()
    {
        var staleMeat = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new LiveArbitrageAccountReads(new Reads(true, false)).MeatAsync(default));
        Assert.Contains("live", staleMeat.Message);
        var staleInv = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new LiveArbitrageAccountReads(new Reads(false, true)).InventoryAsync(default));
        Assert.Contains("live", staleInv.Message);

        var fresh = new LiveArbitrageAccountReads(new Reads(false, false));
        Assert.Equal(500_000, await fresh.MeatAsync(default));
        Assert.Equal(2, (await fresh.InventoryAsync(default))["1"]);
    }

    [Fact]
    public void Two_concurrent_reservations_cannot_both_fit_the_budget()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "ledger.json");
            var a = new ArbitrageSpendLedger(path, new FixedClock());
            var b = new ArbitrageSpendLedger(path, new FixedClock());   // "another process"
            var results = new bool[2];
            Parallel.Invoke(
                () => results[0] = a.TryReserve("mallbot", 60_000, 100_000, out _),
                () => results[1] = b.TryReserve("mallbot", 60_000, 100_000, out _));
            Assert.Equal(1, results.Count(r => r));
            // The loser sees the winner's open reservation as spent.
            var loser = results[0] ? b : a;
            Assert.Equal(60_000, loser.SpentToday("mallbot"));
            Assert.False(loser.TryReserve("mallbot", 50_000, 100_000, out var why));
            Assert.Contains("budget", why);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Invalid_ledger_blocks_reservations_until_fixed()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "ledger.json");
            foreach (var bad in new[] { "{ not json", "[]", "", "{\"accounts\":5}", "{\"accounts\":{\"m\":{\"day\":\"2026-11-22\",\"settled\":\"x\"}}}",
                         "{\"accounts\":{\"m\":{\"day\":\"2026-11-22\",\"settled\":-5}}}" })
            {
                File.WriteAllText(path, bad);
                var ledger = new ArbitrageSpendLedger(path, new FixedClock());
                Assert.False(ledger.TryReserve("m", 1, null, out var error), bad);
                Assert.Contains("unreadable or unexpected", error);
                Assert.True(ledger.SpentToday("m") > 1_000_000_000_000);
                Assert.Throws<SpendLedgerException>(() => ledger.Reserve("m", 1));
            }
            File.WriteAllText(path, "{\"version\":1,\"accounts\":{}}");   // fixed: no restart needed
            Assert.True(new ArbitrageSpendLedger(path, new FixedClock()).TryReserve("m", 1, null, out _));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Invalid_ledger_stops_the_executor_before_any_commit()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "ledger.json");
            File.WriteAllText(path, "garbage");
            var t = new MallPurchaseFixtureTransport(Observed(1_000_000));
            var run = await Executor(t, new ArbitrageSpendLedger(path), 0).ExecuteAsync(Plan());
            var step = Assert.Single(run.Steps);
            Assert.Equal(ArbitrageStepStatus.NotSent, step.Status);
            Assert.Contains("ledger", step.Message);
            Assert.Equal(0, t.CommitCount);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Save_failure_blocks_the_purchase()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "ledger.json");
            Directory.CreateDirectory(path + ".tmp");   // the temp file cannot be created: the save fails
            var ledger = new ArbitrageSpendLedger(path, new FixedClock());
            Assert.False(ledger.TryReserve("m", 10, null, out var error));
            Assert.Contains("could not be saved", error);

            var t = new MallPurchaseFixtureTransport(Observed(1_000_000));
            var run = await Executor(t, new ArbitrageSpendLedger(path), 0).ExecuteAsync(Plan());
            Assert.Equal(ArbitrageStepStatus.NotSent, Assert.Single(run.Steps).Status);
            Assert.Equal(0, t.CommitCount);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Desktop_ledger_shares_the_rules()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "mall.json");
            var a = new KoLibri.Desktop.MallSpendLedger(new FixedClock(), path);
            Assert.True(a.TryReserve("Main", 400, 500, out _));
            var b = new KoLibri.Desktop.MallSpendLedger(new FixedClock(), path);
            Assert.False(b.TryReserve("Main", 200, 500, out _));
            a.Settle("Main", 400);
            Assert.Equal(400, b.SpentToday("Main"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
