using System.Globalization;
using System.Text.Json;
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

    private static ItemArbitrageExecutor Executor(MallPurchaseFixtureTransport t, ArbitrageSpendLedger ledger, long reserve,
        long? budget = null, bool gatewayDailySpentIncludesLedger = false)
        => new(new MallPurchaseGateway(t, new MallPurchasePolicy
        {
            DryRun = false, RequireApproval = false, MaxDailySpend = 2_000_000, MaxMeatPerPurchase = 500_000,
            MaxQuantityPerPurchase = 10, MeatReserve = reserve
        }), ledger, "mallbot", budget, gatewayDailySpentIncludesLedger);

    private static string TempDir() => Directory.CreateTempSubdirectory("money-").FullName;

    private sealed class ExtrasLedger(string path, TimeProvider clock, Func<int, bool>? isOwnerAlive = null)
        : KoLibri.Extras.FileSpendLedger(path, clock, isOwnerAlive: isOwnerAlive);

    [Theory]
    [InlineData("2026-11-22T03:29:59Z", "2026-11-21")]
    [InlineData("2026-11-22T03:30:00Z", "2026-11-22")]
    [InlineData("2026-07-01T03:29:59Z", "2026-06-30")]
    [InlineData("2026-07-01T03:30:00Z", "2026-07-01")]
    public void Core_and_Extras_ledger_days_change_at_0330_utc_year_round(string instant, string expectedDay)
    {
        var clock = new MovableClock(DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture));
        var core = new ArbitrageSpendLedger(clock: clock);
        var extras = new KoLibri.Extras.ArbitrageSpendLedger(clock: clock);

        Assert.Equal(expectedDay, core.ReadUnclear("Mallbot").Day);
        Assert.Equal(expectedDay, extras.ReadUnclear("Mallbot").Day);
    }

    [Theory]
    [InlineData("settle", 300, 0)]
    [InlineData("unclear", 500, 500)]
    [InlineData("release", 0, 0)]
    [InlineData("reconcile", 200, 0)]
    public void Core_and_Extras_preserve_in_flight_reservations_across_rollover(string finish, long counted, long unclear)
    {
        var dir = Directory.CreateTempSubdirectory("kolibri-mall-");
        try
        {
            var path = Path.Combine(dir.FullName, "mall.json");
            var rollover = new DateTimeOffset(2026, 11, 22, 3, 30, 0, TimeSpan.Zero);
            var clock = new MovableClock(rollover.AddSeconds(-1));
            var owner = new ExtrasLedger(path, clock);
            var core = new SharedMallSpendLedger(path, clock);
            owner.Add("Mallbot", 100);
            Assert.True(owner.TryReserve("Mallbot", 60, 1_000, out var oldUnclear, out _));
            owner.MarkUnclear(oldUnclear);
            Assert.True(owner.TryReserve("Mallbot", 70, 1_000, out var queued, out _));
            Assert.True(owner.TryReserve("Mallbot", 500, 1_000, out var sending, out _));
            Assert.True(owner.BeginSend(ref sending, 1_000, out _));

            var before = core.ReadUnclear("Mallbot");
            Assert.Equal("2026-11-21", before.Day);
            Assert.Equal("2026-11-21", sending.Day);
            Assert.Equal(queued.Id, Assert.Single(before.ForeignIds!));
            core.Add("Other", 1);
            CheckSendingFile(730);

            clock.Value = rollover;
            Assert.Equal(500, core.CountedToday("Mallbot"));
            var current = core.ReadUnclear("Mallbot");
            Assert.Equal(before.Revision, current.Revision);
            Assert.Equal("2026-11-22", current.Day);
            Assert.Equal(0, current.Total);
            Assert.False(core.TryReserve("Mallbot", 501, 1_000, out _, out _));
            core.Add("Other", 1);
            CheckSendingFile(500);

            if (finish == "reconcile")
            {
                var orphanReader = new ExtrasLedger(path, clock, _ => false);
                var orphan = orphanReader.ReadUnclear("Mallbot");
                Assert.Equal(sending.Id, Assert.Single(orphan.ForeignIds!));
                Assert.Equal(500, orphan.Foreign);
                Assert.True(orphanReader.Reconcile("Mallbot", orphan, 200));
            }

            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (finish == "release") owner.Release(sending);
                else if (finish == "unclear") owner.MarkUnclear(sending);
                else owner.SettlePartial(sending, 300);
            }
            Assert.Equal(counted, core.CountedToday("Mallbot"));
            Assert.Equal(unclear, core.UnclearAmount("Mallbot"));
            Assert.Equal(0, core.ReservedAmount("Mallbot"));
            using var completed = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(1, completed.RootElement.GetProperty("version").GetInt32());
            Assert.False(completed.RootElement.GetProperty("accounts").GetProperty("Mallbot").TryGetProperty("sending", out _));

            void CheckSendingFile(long expectedCounted)
            {
                using var file = JsonDocument.Parse(File.ReadAllText(path));
                var root = file.RootElement;
                Assert.Equal(2, root.GetProperty("version").GetInt32());
                var mallbot = root.GetProperty("accounts").GetProperty("Mallbot");
                Assert.Equal(Environment.ProcessId, mallbot.GetProperty("sending").GetProperty(sending.Id).GetInt32());
                Assert.Equal(500, mallbot.GetProperty("reservations").GetProperty(sending.Id).GetInt64());
                Assert.Equal(expectedCounted, core.CountedToday("Mallbot"));
            }
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Core_and_Extras_fail_closed_on_a_future_ledger_format()
    {
        var dir = Directory.CreateTempSubdirectory("kolibri-mall-");
        try
        {
            var path = Path.Combine(dir.FullName, "mall.json");
            const string contents = """{"version":3,"accounts":{}}""";
            File.WriteAllText(path, contents);
            var clock = new MovableClock(Now);
            var extras = new ExtrasLedger(path, clock);
            var core = new SharedMallSpendLedger(path, clock);

            Assert.IsType<FormatException>(Assert.Throws<KoLibri.Extras.SpendLedgerException>(() => extras.ReadUnclear("Mallbot")).InnerException);
            Assert.IsType<FormatException>(Assert.Throws<SpendLedgerException>(() => core.ReadUnclear("Mallbot")).InnerException);
            Assert.False(extras.TryReserve("Mallbot", 100, 1_000, out _, out _));
            Assert.False(core.TryReserve("Mallbot", 100, 1_000, out _, out _));
            Assert.Equal(contents, File.ReadAllText(path));
        }
        finally { dir.Delete(recursive: true); }
    }

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

    // ---- BUDGET-05: day change, crash recovery, gateway limits, reconcile CAS ---------------------------

    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        private int _readCount;
        public DateTimeOffset Value = now;
        public int ReadCount => Volatile.Read(ref _readCount);
        public override DateTimeOffset GetUtcNow()
        {
            Interlocked.Increment(ref _readCount);
            return Value;
        }
    }

    [Theory]
    [InlineData("release")]
    [InlineData("settle")]
    [InlineData("partial")]
    [InlineData("unclear")]
    public void Finishing_a_reservation_after_the_day_change_never_touches_the_new_days_reservations(string finish)
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "ledger.json");
            var clockA = new MovableClock(Now);
            var clockB = new MovableClock(Now);
            var a = new ArbitrageSpendLedger(path, clockA);
            var b = new ArbitrageSpendLedger(path, clockB);
            Assert.True(a.TryReserve("mallbot", 300, null, out var dayOne, out _));

            clockA.Value = clockB.Value = Now.AddDays(1);   // KoL day 2
            Assert.True(b.TryReserve("mallbot", 500, null, out var dayTwo, out _));
            Assert.NotEqual(dayOne.Day, dayTwo.Day);

            switch (finish)
            {
                case "release": a.Release(dayOne); break;
                case "settle": a.Settle(dayOne); break;
                case "partial": a.SettlePartial(dayOne, 200); break;
                default: a.MarkUnclear(dayOne); break;
            }

            var fresh = new ArbitrageSpendLedger(path, new MovableClock(Now.AddDays(1)));
            Assert.Equal(500, fresh.ReservedAmount("mallbot"));   // B's reservation is intact
            Assert.Equal(0, b.UnclearToday("mallbot") - b.UnclearAmount("mallbot"));   // still B's own, not foreign
            var booked = finish switch { "settle" => 300, "partial" => 200, "unclear" => 300, _ => 0 };
            Assert.Equal(500 + booked, fresh.CountedToday("mallbot"));   // only conservative booking on day 2
            Assert.Equal(finish == "unclear" ? 300 : 0, fresh.UnclearAmount("mallbot"));

            b.Release(dayTwo);
            Assert.Equal(0, fresh.ReservedAmount("mallbot"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Legacy_finish_after_the_day_change_books_on_the_oldest_open_own_day()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "ledger.json");
            var clock = new MovableClock(Now);
            var a = new ArbitrageSpendLedger(path, clock);
            a.Reserve("mallbot", 300);
            clock.Value = Now.AddDays(1);
            var b = new ArbitrageSpendLedger(path, new MovableClock(Now.AddDays(1)));
            b.Reserve("mallbot", 500);

            a.Release("mallbot", 300);   // the day-1 reservation, not B's day-2 one
            Assert.Equal(500, b.ReservedAmount("mallbot"));
            Assert.Equal(500, a.SpentToday("mallbot"));   // B's reservation is foreign for A
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_reservation_of_a_crashed_instance_stays_counted_by_a_new_instance()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "ledger.json");
            var crashed = new ArbitrageSpendLedger(path, new FixedClock());
            Assert.True(crashed.TryReserve("mallbot", 400, 1_000, out _, out _));   // no settle/release: "crash"

            // The file format stays the one older builds read and write.
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            Assert.Equal(1, json["version"]!.GetValue<int>());
            Assert.Equal(400, json["accounts"]!["mallbot"]!["reserved"]!.GetValue<long>());

            var restarted = new ArbitrageSpendLedger(path, new FixedClock());
            Assert.Equal(400, restarted.SpentToday("mallbot"));
            Assert.Equal(400, restarted.UnclearToday("mallbot"));
            Assert.Equal(400, restarted.CountedToday("mallbot"));
            Assert.False(restarted.TryReserve("mallbot", 700, 1_000, out _, out var error));
            Assert.Contains("daily budget", error);
            Assert.True(restarted.TryReserve("mallbot", 600, 1_000, out _, out _));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_file_written_by_an_older_build_still_counts_its_reservation()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "ledger.json");
            // 2026-11-22 18:00 UTC is KoL day 2026-11-22.
            File.WriteAllText(path, """{"version":1,"accounts":{"mallbot":{"day":"2026-11-22","settled":100,"unclear":0,"reserved":250}}}""");
            var ledger = new ArbitrageSpendLedger(path, new FixedClock());
            Assert.Equal(350, ledger.SpentToday("mallbot"));
            Assert.Equal(250, ledger.UnclearToday("mallbot"));
            Assert.False(ledger.TryReserve("mallbot", 651, 1_000, out _, out _));
        }
        finally { Directory.Delete(dir, true); }
    }

    private static MallPurchaseFixtureTransport Gateway(long dailySpent, long dailyLimit)
        => new(new MallPurchaseObservation(
            new MallPurchaseOffer("56226", "56226", "10881", 25_000, 10, DateTimeOffset.UtcNow, 5),
            new MallPurchaseAccountState("mallbot", 1_000_000, dailySpent, dailyLimit, new Dictionary<string, int>(), DateTimeOffset.UtcNow),
            DateTimeOffset.UtcNow));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_gateway_daily_limit_counts_the_ledger_even_without_an_own_budget(bool settledBefore)
    {
        // The gateway reports nothing spent and a limit of 100k; the ledger already holds 60k; the step needs 50k.
        var ledger = new ArbitrageSpendLedger();
        if (settledBefore) ledger.Add("mallbot", 60_000); else ledger.Reserve("mallbot", 60_000);
        var t = Gateway(dailySpent: 0, dailyLimit: 100_000);
        var executor = Executor(t, ledger, 0);   // no dailyBudget

        var preview = Assert.Single((await executor.PreviewAsync(Plan())).Steps);
        Assert.Equal(ArbitrageStepStatus.NotSent, preview.Status);
        Assert.Contains(MallPurchaseAbortReason.DailyLimitExceeded, preview.Reasons);

        var run = Assert.Single((await executor.ExecuteAsync(Plan())).Steps);
        Assert.Equal(ArbitrageStepStatus.NotSent, run.Status);
        Assert.Equal(0, t.CommitCount);
        Assert.Equal(60_000, ledger.CountedToday("mallbot"));   // nothing new reserved
    }

    [Fact]
    public async Task A_higher_spend_reported_by_the_gateway_blocks_the_purchase()
    {
        var ledger = new ArbitrageSpendLedger();
        var t = Gateway(dailySpent: 80_000, dailyLimit: 2_000_000);
        var executor = Executor(t, ledger, 0, budget: 100_000);

        Assert.Equal(ArbitrageStepStatus.NotSent, Assert.Single((await executor.PreviewAsync(Plan())).Steps).Status);
        var run = Assert.Single((await executor.ExecuteAsync(Plan())).Steps);
        Assert.Equal(ArbitrageStepStatus.NotSent, run.Status);
        Assert.Equal(0, t.CommitCount);
        Assert.Equal(0, ledger.CountedToday("mallbot"));
    }

    [Fact]
    public async Task Gateway_spend_that_already_includes_the_ledger_is_counted_once()
    {
        // The arbitrage observer reports the ledger's own count as the gateway's daily spend: 30k + 50k fits 100k.
        var ledger = new ArbitrageSpendLedger();
        ledger.Add("mallbot", 30_000);
        var t = Gateway(dailySpent: 30_000, dailyLimit: 2_000_000);
        var executor = Executor(t, ledger, 0, budget: 100_000, gatewayDailySpentIncludesLedger: true);
        Assert.Equal(ArbitrageStepStatus.Previewed, Assert.Single((await executor.PreviewAsync(Plan())).Steps).Status);
        var run = Assert.Single((await executor.ExecuteAsync(Plan())).Steps);
        Assert.Equal(ArbitrageStepStatus.Purchased, run.Status);
        Assert.Equal(1, t.CommitCount);
        Assert.Equal(80_000, ledger.SpentToday("mallbot"));
    }

    [Fact]
    public async Task A_preview_with_an_unreadable_ledger_is_not_ready()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "ledger.json");
            File.WriteAllText(path, "garbage");
            var t = new MallPurchaseFixtureTransport(Observed(1_000_000));
            var step = Assert.Single((await Executor(t, new ArbitrageSpendLedger(path), 0).PreviewAsync(Plan())).Steps);
            Assert.Equal(ArbitrageStepStatus.NotSent, step.Status);
            Assert.Contains("ledger", step.Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Reconcile_changes_nothing_when_unclear_meat_was_added_meanwhile()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "ledger.json");
            var a = new ArbitrageSpendLedger(path, new FixedClock());
            var b = new ArbitrageSpendLedger(path, new FixedClock());
            a.Reserve("mallbot", 700);
            a.MarkUnclear("mallbot", 700);
            var seen = b.ReadUnclear("mallbot");
            Assert.Equal(700, seen.Total);

            a.Reserve("mallbot", 500);
            a.MarkUnclear("mallbot", 500);
            Assert.False(b.Reconcile("mallbot", seen, 0));
            Assert.Equal(1_200, b.UnclearAmount("mallbot"));
            Assert.Equal(1_200, b.SpentToday("mallbot"));

            var current = b.ReadUnclear("mallbot");
            Assert.Equal(1_200, current.Total);
            Assert.True(b.Reconcile("mallbot", current, 0));
            Assert.Equal(0, b.SpentToday("mallbot"));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- BUDGET-05 pass 2: one finding of the independent review per test ------------------------------

    private static readonly DateTime OldTime = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static MallPurchaseFixtureTransport Fixture(long price, long dailySpent, long dailyLimit)
        => new(new MallPurchaseObservation(
            new MallPurchaseOffer("56226", "56226", "10881", price, 10, DateTimeOffset.UtcNow, 5),
            new MallPurchaseAccountState("mallbot", 1_000_000, dailySpent, dailyLimit, new Dictionary<string, int>(),
                DateTimeOffset.UtcNow),
            DateTimeOffset.UtcNow));

    private static ArbitrageStep Step(long price, int quantity, long? total = null)
        => new("10881", "56226", "Crazy Gordon", price, quantity, total ?? price * quantity, 0, 0, 5, MallLimitKind.Limited, null);

    private static ItemArbitragePlan PlanOf(params ArbitrageStep[] steps) => new() { ItemId = 10881, Steps = steps };

    [Fact]
    public void A_refused_compare_and_set_and_other_no_op_paths_never_rewrite_the_file()   // review #1
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "ledger.json");
            var a = new ArbitrageSpendLedger(path, new FixedClock());
            var b = new ArbitrageSpendLedger(path, new FixedClock());
            a.Reserve("mallbot", 700);
            a.MarkUnclear("mallbot", 700);
            var seen = b.ReadUnclear("mallbot");
            Assert.True(a.TryReserve("mallbot", 100, 1_000, out var open, out _));   // changes the revision

            var before = File.ReadAllText(path);
            File.SetLastWriteTimeUtc(path, OldTime);

            Assert.False(b.Reconcile("mallbot", seen, 0));                  // stale snapshot
            Assert.False(b.ResolveUnclear("mallbot", 1, wasSpent: true));   // wrong amount
            Assert.False(b.TryReserve("mallbot", 500, 1_000, out _, out _)); // over the limit
            b.Release(open);                                                // not b's reservation
            b.Release("mallbot", 100);                                      // b holds nothing
            b.Settle(new SpendReservation("mallbot", open.Day, 100, "forged"));
            _ = b.SpentToday("mallbot") + b.CountedToday("mallbot") + b.UnclearToday("mallbot") + b.ReservedAmount("mallbot");
            _ = b.ReadUnclear("other");                                     // no empty entry is created
            Assert.False(b.Unreadable);
            var active = open;
            Assert.True(a.EnsureActiveOrRenew(ref active, 1_000, out var activeError), activeError);
            Assert.Equal(open, active);

            Assert.Equal(before, File.ReadAllText(path));
            Assert.Equal(OldTime, File.GetLastWriteTimeUtc(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("settle")]
    [InlineData("release")]
    public async Task The_kol_day_is_taken_only_once_the_lock_is_held(string finish)   // review #1
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "ledger.json");
            var clock = new MovableClock(Now);
            var a = new ArbitrageSpendLedger(path, clock);
            Assert.True(a.TryReserve("mallbot", 300, null, out var dayOne, out _));

            var readsBefore = clock.ReadCount;
            var finishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var worker = new Thread(() =>
            {
                try
                {
                    if (finish == "settle") a.Settle(dayOne); else a.Release(dayOne);
                    finishing.SetResult();
                }
                catch (Exception e) { finishing.SetException(e); }
            }) { IsBackground = true };
            bool waitedForLock;
            int readsWhileWaiting;
            using (new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                worker.Start();
                // The worker has no other waits before AcquireLock's retry sleep.
                waitedForLock = SpinWait.SpinUntil(() => (worker.ThreadState & ThreadState.WaitSleepJoin) != 0,
                    TimeSpan.FromSeconds(5));
                readsWhileWaiting = clock.ReadCount;
                clock.Value = Now.AddDays(1);
                // Meanwhile "B" holds the lock on day 2 and reserves 500.
                File.WriteAllText(path,
                    """{"version":1,"accounts":{"mallbot":{"day":"2026-11-23","revision":1,"settled":0,"unclear":0,"reserved":500,"reservations":{"b-500":500}}}}""");
                File.SetLastWriteTimeUtc(path, OldTime);
            }
            await finishing.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(waitedForLock, "The finishing operation did not reach the contended file lock.");
            Assert.Equal(readsBefore, readsWhileWaiting);
            Assert.Equal(readsBefore + 1, clock.ReadCount);

            var fresh = new ArbitrageSpendLedger(path, new MovableClock(Now.AddDays(1)));
            Assert.Equal(500, fresh.ReservedAmount("mallbot"));   // B's 500 survive
            if (finish == "settle")
                Assert.Equal(800, fresh.CountedToday("mallbot"));   // A's settle is booked on day 2 (the safe side)
            else
                Assert.Equal(OldTime, File.GetLastWriteTimeUtc(path));   // a release after the day change writes nothing
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void An_account_finish_with_own_reservations_on_two_days_changes_neither()   // review #2
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "ledger.json");
            var clock = new MovableClock(Now);
            var a = new ArbitrageSpendLedger(path, clock);
            Assert.True(a.TryReserve("mallbot", 300, null, out var yesterday, out _));
            clock.Value = Now.AddDays(1);
            Assert.True(a.TryReserve("mallbot", 500, null, out var today, out _));

            a.Release("mallbot", 300);
            a.Settle("mallbot", 800);
            a.MarkUnclear("mallbot", 800);
            var fresh = new ArbitrageSpendLedger(path, new MovableClock(Now.AddDays(1)));
            Assert.Equal(500, fresh.ReservedAmount("mallbot"));
            Assert.Equal(500, fresh.SpentToday("mallbot"));
            Assert.Equal(500, a.CountedToday("mallbot"));

            a.Settle(yesterday);
            Assert.Equal(800, fresh.CountedToday("mallbot"));   // the older handle was still open
            a.Release(today);
            Assert.Equal(0, fresh.ReservedAmount("mallbot"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_reservation_is_finished_at_most_once()   // review #3
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "ledger.json");
            var a = new ArbitrageSpendLedger(path, new FixedClock());
            Assert.True(a.TryReserve("mallbot", 100, null, out var r1, out _));
            Assert.True(a.TryReserve("mallbot", 100, null, out var r2, out _));
            Assert.NotEqual(r1.Id, r2.Id);

            a.Release(r1);
            a.Release(r1);
            var fresh = new ArbitrageSpendLedger(path, new FixedClock());
            Assert.Equal(100, fresh.ReservedAmount("mallbot"));   // r2 is intact

            a.Settle(r2);
            a.Settle(r2);
            a.Settle(r1);   // already released: nothing
            Assert.Equal(100, fresh.SpentToday("mallbot"));   // settled exactly once
            Assert.Equal(0, fresh.ReservedAmount("mallbot"));
            Assert.Equal(100, fresh.CountedToday("mallbot"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void An_idless_handle_has_no_effect_and_legacy_settle_only_books_the_amount_held()
    {
        var ledger = new ArbitrageSpendLedger(clock: new FixedClock());
        Assert.True(ledger.TryReserve("mallbot", 100, null, out var first, out _));
        Assert.True(ledger.TryReserve("mallbot", 10, null, out _, out _));
        var idless = new SpendReservation("mallbot", first.Day, 100);

        ledger.Release(idless);
        ledger.Settle(idless);
        Assert.Equal(110, ledger.ReservedAmount("mallbot"));
        Assert.Equal(0, ledger.SpentToday("mallbot"));

        ledger.Release(first);
        ledger.Settle("mallbot", 100);
        Assert.Equal(10, ledger.SpentToday("mallbot"));
        Assert.Equal(0, ledger.ReservedAmount("mallbot"));
    }

    [Fact]
    public void Independent_external_spend_counts_in_full_when_supplied()   // review #4
    {
        var ledger = new ArbitrageSpendLedger();
        Assert.True(ledger.TryReserve("mallbot", 15, 100, out var first, out _, externalSpent: 80));
        ledger.Settle(first);
        // The gateway still reports 80 (it does not count this purchase): 80 + 15 + 15 > 100.
        Assert.False(ledger.TryReserve("mallbot", 15, 100, out _, out var error, externalSpent: 80));
        Assert.Contains("daily budget", error);
        Assert.Equal(15, ledger.CountedToday("mallbot"));
        Assert.Equal(95, ledger.CountedToday("mallbot", 80));
        Assert.Equal(100, ledger.CountedToday("mallbot", 85));
        Assert.Equal(15, ledger.CountedToday("mallbot", -1));
        Assert.True(ledger.TryReserve("mallbot", 15, 100, out _, out _));
    }

    [Fact]
    public void Parallel_reservations_with_an_external_spend_cannot_both_fit()   // review #4
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "ledger.json");
            var a = new ArbitrageSpendLedger(path, new FixedClock());
            var b = new ArbitrageSpendLedger(path, new FixedClock());
            var results = new bool[2];
            Parallel.Invoke(
                () => results[0] = a.TryReserve("mallbot", 15, 100, out _, out _, externalSpent: 80),
                () => results[1] = b.TryReserve("mallbot", 15, 100, out _, out _, externalSpent: 80));
            Assert.Equal(1, results.Count(r => r));
            var fresh = new ArbitrageSpendLedger(path, new FixedClock());
            Assert.Equal(15, fresh.CountedToday("mallbot"));
            Assert.Equal(95, fresh.CountedToday("mallbot", 80));
            Assert.DoesNotContain("\"external\"", File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task An_independent_gateway_spend_increase_blocks_the_second_step()
    {
        var ledger = new ArbitrageSpendLedger();
        var t = Fixture(price: 5, dailySpent: 80, dailyLimit: 2_000_000);
        var executor = Executor(t, ledger, 0, budget: 100, gatewayDailySpentIncludesLedger: false);
        var first = Assert.Single((await executor.ExecuteAsync(PlanOf(Step(5, 3)))).Steps);
        Assert.Equal(ArbitrageStepStatus.Purchased, first.Status);
        Assert.Equal(15, ledger.SpentToday("mallbot"));
        Assert.Equal(1, t.CommitCount);

        var state = t.State;
        t.SetState(state with { Account = state.Account with { DailySpent = 85 } });
        var secondPlan = PlanOf(Step(5, 1));
        Assert.Equal(ArbitrageStepStatus.NotSent, Assert.Single((await executor.PreviewAsync(secondPlan)).Steps).Status);
        var second = Assert.Single((await executor.ExecuteAsync(secondPlan)).Steps);
        Assert.Equal(ArbitrageStepStatus.NotSent, second.Status);
        Assert.Contains(MallPurchaseAbortReason.DailyLimitExceeded, second.Reasons);
        Assert.Equal(1, t.CommitCount);
        Assert.Equal(15, ledger.CountedToday("mallbot"));
    }

    [Fact]
    public void Limit_checks_never_overflow()   // review #4
    {
        var ledger = new ArbitrageSpendLedger();
        Assert.True(ledger.TryReserve("mallbot", 10, long.MaxValue, out var small, out _));
        Assert.False(ledger.TryReserve("mallbot", 10, long.MaxValue, out _, out _, externalSpent: long.MaxValue));
        Assert.False(ledger.TryReserve("mallbot", 10, long.MinValue, out _, out _));
        Assert.False(ledger.TryReserve("mallbot", long.MaxValue, long.MaxValue, out _, out _));
        Assert.True(ledger.TryReserve("mallbot", long.MaxValue / 4 - 100, null, out _, out _));
        Assert.False(ledger.TryReserve("mallbot", long.MaxValue / 4 - 100, null, out _, out _));   // the total would leave the valid range
        Assert.True(ledger.CountedToday("mallbot") > 0);
        ledger.Release(small);
    }

    [Fact]
    public void Amounts_at_the_supported_boundary_are_refused_without_changing_the_file()
    {
        const long blocked = long.MaxValue / 4;
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "ledger.json");
            var a = new ArbitrageSpendLedger(path, new FixedClock());
            var b = new ArbitrageSpendLedger(path, new FixedClock());
            Assert.True(a.TryReserve("mallbot", blocked - 1, null, out var open, out _));
            a.Settle(open);
            a.Add("main", blocked - 2);
            Assert.True(b.TryReserve("main", 1, null, out _, out _));
            var snapshot = a.ReadUnclear("main");
            var before = File.ReadAllText(path);
            File.SetLastWriteTimeUtc(path, OldTime);

            Assert.False(a.TryReserve("mallbot", 1, null, out _, out _));
            Assert.Throws<SpendLedgerException>(() => a.Add("mallbot", 1));
            Assert.Throws<SpendLedgerException>(() => a.Add("main", 1));
            Assert.Throws<SpendLedgerException>(() => a.Add("main", blocked - 1));
            Assert.Throws<SpendLedgerException>(() => a.Reconcile("main", snapshot, 2));

            Assert.Equal(before, File.ReadAllText(path));
            Assert.Equal(OldTime, File.GetLastWriteTimeUtc(path));
            Assert.False(b.Unreadable);
            Assert.Equal(blocked - 1, b.SpentToday("mallbot"));
            Assert.Equal(1, b.ReservedAmount("main"));

            // High revision values (>= Blocked) stay readable; long.MaxValue refuses further mutations without corrupting the file.
            var revPath = Path.Combine(dir, "high-rev.json");
            File.WriteAllText(revPath, """{"version":1,"accounts":{"mallbot":{"day":"2026-11-22","revision":REV,"settled":0,"unclear":0,"reserved":0,"reservations":{}}}}""".Replace("REV", (blocked - 1).ToString()));
            var revA = new ArbitrageSpendLedger(revPath, new FixedClock());
            Assert.True(revA.TryReserve("mallbot", 1, null, out _, out _));
            var revB = new ArbitrageSpendLedger(revPath, new FixedClock());
            Assert.False(revB.Unreadable);
            Assert.Equal(1, revB.ReservedAmount("mallbot"));

            var maxRevPath = Path.Combine(dir, "max-rev.json");
            File.WriteAllText(maxRevPath, """{"version":1,"accounts":{"mallbot":{"day":"2026-11-22","revision":REV,"settled":0,"unclear":0,"reserved":0,"reservations":{}}}}""".Replace("REV", long.MaxValue.ToString()));
            var maxA = new ArbitrageSpendLedger(maxRevPath, new FixedClock());
            Assert.False(maxA.TryReserve("mallbot", 1, null, out _, out _));
            Assert.False(new ArbitrageSpendLedger(maxRevPath, new FixedClock()).Unreadable);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task The_gateway_policy_daily_limit_counts_too()   // review #5
    {
        var ledger = new ArbitrageSpendLedger();
        ledger.Add("mallbot", 80_000);
        var t = Fixture(price: 15_000, dailySpent: 0, dailyLimit: 1_000_000);
        var executor = new ItemArbitrageExecutor(new MallPurchaseGateway(t, new MallPurchasePolicy
        {
            DryRun = false, RequireApproval = false, MaxDailySpend = 100_000, MaxMeatPerPurchase = 500_000, MaxQuantityPerPurchase = 10
        }), ledger, "mallbot");
        var plan = PlanOf(Step(15_000, 2));   // 30k: 80k + 30k > 100k

        var preview = Assert.Single((await executor.PreviewAsync(plan)).Steps);
        Assert.Equal(ArbitrageStepStatus.NotSent, preview.Status);
        Assert.Contains(MallPurchaseAbortReason.DailyLimitExceeded, preview.Reasons);
        var run = Assert.Single((await executor.ExecuteAsync(plan)).Steps);
        Assert.Equal(ArbitrageStepStatus.NotSent, run.Status);
        Assert.Equal(0, t.CommitCount);
        Assert.Equal(80_000, ledger.CountedToday("mallbot"));
    }

    [Fact]
    public void A_release_after_a_reconcile_elsewhere_never_takes_another_processes_reservation()   // review #6
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "ledger.json");
            var a = new ArbitrageSpendLedger(path, new FixedClock());
            var b = new ArbitrageSpendLedger(path, new FixedClock());
            var c = new ArbitrageSpendLedger(path, new FixedClock());
            Assert.True(a.TryReserve("mallbot", 100, null, out var resA, out _));
            var snapshot = b.ReadUnclear("mallbot");
            Assert.Equal(100, snapshot.Foreign);
            Assert.True(b.Reconcile("mallbot", snapshot, 0));   // a's reservation counted as stuck and cleared
            Assert.True(c.TryReserve("mallbot", 100, null, out var resC, out _));

            a.Release(resA);
            var fresh = new ArbitrageSpendLedger(path, new FixedClock());
            Assert.Equal(100, fresh.ReservedAmount("mallbot"));   // c's 100 are intact
            Assert.Equal(100, fresh.SpentToday("mallbot"));

            c.Release(resC);
            Assert.Equal(0, fresh.ReservedAmount("mallbot"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("settle")]
    [InlineData("partial")]
    [InlineData("unclear")]
    public void A_finish_after_a_reconcile_elsewhere_does_not_book_again(string finish)
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "ledger.json");
            var a = new ArbitrageSpendLedger(path, new FixedClock());
            var b = new ArbitrageSpendLedger(path, new FixedClock());
            Assert.True(a.TryReserve("mallbot", 100, null, out var open, out _));
            Assert.True(b.Reconcile("mallbot", b.ReadUnclear("mallbot"), 100));
            File.SetLastWriteTimeUtc(path, OldTime);

            if (finish == "settle") a.Settle(open);
            else if (finish == "partial") a.SettlePartial(open, 50);
            else a.MarkUnclear(open);

            Assert.Equal(100, b.SpentToday("mallbot"));
            Assert.Equal(0, b.UnclearAmount("mallbot"));
            Assert.Equal(0, b.ReservedAmount("mallbot"));
            Assert.Equal(OldTime, File.GetLastWriteTimeUtc(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_snapshot_of_yesterday_never_reconciles_an_equal_amount_of_today()   // review #7
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "ledger.json");
            var clock = new MovableClock(Now);
            var a = new ArbitrageSpendLedger(path, clock);
            var b = new ArbitrageSpendLedger(path, clock);
            a.Reserve("mallbot", 700);
            a.MarkUnclear("mallbot", 700);
            var dayOne = b.ReadUnclear("mallbot");
            Assert.Equal(700, dayOne.Total);

            clock.Value = Now.AddDays(1);
            a.Reserve("mallbot", 700);   // the same amount unclear again on day 2
            a.MarkUnclear("mallbot", 700);
            Assert.Equal(dayOne.Unclear, b.ReadUnclear("mallbot").Unclear);

            Assert.False(b.Reconcile("mallbot", dayOne, 0));
            Assert.Equal(700, new ArbitrageSpendLedger(path, new MovableClock(Now.AddDays(1))).UnclearAmount("mallbot"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task A_step_whose_total_does_not_match_is_refused()   // review #9
    {
        var ledger = new ArbitrageSpendLedger();
        var t = new MallPurchaseFixtureTransport(Observed(1_000_000));
        var plan = PlanOf(Step(25_000, 2, total: 0));

        var preview = Assert.Single((await Executor(t, ledger, 0).PreviewAsync(plan)).Steps);
        Assert.Equal(ArbitrageStepStatus.NotSent, preview.Status);
        var run = Assert.Single((await Executor(t, ledger, 0).ExecuteAsync(plan)).Steps);
        Assert.Equal(ArbitrageStepStatus.NotSent, run.Status);
        Assert.Contains(MallPurchaseAbortReason.InvalidRequest, run.Reasons);
        Assert.Equal(0, t.CommitCount);
        Assert.Equal(0, ledger.CountedToday("mallbot"));
    }

    private sealed class ReadyGateway(MallPurchaseObservation observation) : IMallPurchaseGateway
    {
        public int CommitCount { get; private set; }

        public Task<MallPurchasePreview> PreviewAsync(MallPurchaseRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new MallPurchasePreview { Request = request, Observation = observation });

        public Task<MallPurchaseCommitResult> CommitAsync(MallPurchasePreview preview, string? approvalToken = null,
            CancellationToken cancellationToken = default)
        {
            CommitCount++;
            return Task.FromResult(new MallPurchaseCommitResult { Status = MallPurchaseVerificationStatus.Verified });
        }

        public Task<MallPurchaseRefreshResult> RefreshAsync(MallPurchasePreview preview, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public MallPurchaseVerificationResult Verify(MallPurchasePreview preview, MallPurchaseObservation before,
            MallPurchaseObservation after, bool commitAcknowledged = true) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(MallPurchaseAbortReason.StaleAccount)]
    [InlineData(MallPurchaseAbortReason.AccountChanged)]
    [InlineData(MallPurchaseAbortReason.SessionInvalid)]
    public async Task The_executor_refuses_a_ready_preview_with_invalid_account_state(MallPurchaseAbortReason reason)
    {
        var ledger = new ArbitrageSpendLedger();
        var observation = Observed(1_000_000);
        var gateway = new ReadyGateway(observation with
        {
            Account = observation.Account with
            {
                IsStale = reason == MallPurchaseAbortReason.StaleAccount,
                AccountId = reason == MallPurchaseAbortReason.AccountChanged ? "otherbot" : "mallbot",
                SessionValid = reason != MallPurchaseAbortReason.SessionInvalid
            }
        });
        var executor = new ItemArbitrageExecutor(gateway, ledger, "mallbot", 1_000_000);

        var preview = Assert.Single((await executor.PreviewAsync(Plan())).Steps);
        Assert.Equal(ArbitrageStepStatus.NotSent, preview.Status);
        Assert.Equal(reason, Assert.Single(preview.Reasons));
        var run = Assert.Single((await executor.ExecuteAsync(Plan())).Steps);
        Assert.Equal(ArbitrageStepStatus.NotSent, run.Status);
        Assert.Equal(reason, Assert.Single(run.Reasons));
        Assert.Equal(0, gateway.CommitCount);
        Assert.Equal(0, ledger.CountedToday("mallbot"));
    }

    [Fact]
    public async Task A_preview_counts_the_earlier_steps_of_the_same_run()   // review #11
    {
        var t = Fixture(price: 30_000, dailySpent: 0, dailyLimit: 2_000_000);
        var run = await Executor(t, new ArbitrageSpendLedger(), 0, budget: 100_000)
            .PreviewAsync(PlanOf(Step(30_000, 2), Step(30_000, 2)));   // 60k + 60k > 100k

        Assert.Equal(new[] { ArbitrageStepStatus.Previewed, ArbitrageStepStatus.NotSent }, run.Steps.Select(s => s.Status));
        Assert.Contains(MallPurchaseAbortReason.DailyLimitExceeded, run.Steps[1].Reasons);
        Assert.Equal(0, t.CommitCount);
    }
}
