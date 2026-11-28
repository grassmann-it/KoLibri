using KoLibri.Desktop;
using KoLibri.Extras;
using Xunit;

namespace KoLibri.Tests.Desktop;

/// <summary>Unclear purchase Meat is resolved only by an explicit decision; the ledger refuses a changed amount.</summary>
public sealed class MallReconcileTests
{
    private static readonly DateTimeOffset Now = MallKit.Now;

    [Theory]
    [InlineData(MallUnclearDecision.WasSpent, 25_000, 0)]
    [InlineData(MallUnclearDecision.NotSpent, 0, 0)]
    [InlineData(MallUnclearDecision.Cancel, 0, 25_000)]
    public async Task Reconcile_resolves_unclear_meat_only_by_explicit_decision(
        MallUnclearDecision decision, long expectedSpent, long expectedUnclear)
    {
        var log = new SessionLogViewModel(now: () => Now);
        var ledger = new MallSpendLedger(new MallKit.Clock(Now));
        Assert.True(ledger.TryReserve("mallbot", 25_000, null, out _));
        ledger.MarkUnclear("mallbot", 25_000);
        var prompt = new Prompt(decision);
        var coordinator = new MallPurchaseCoordinator("mallbot",
            _ => Task.FromResult<DesktopMallPurchaseService?>(null),
            null!, log, ledger, null, prompt, _ => Task.FromResult("Meat now 5."));

        var result = await coordinator.ReconcileUnclearAsync(default);
        Assert.Equal(decision, result);
        Assert.Equal(25_000, prompt.AmountShown);
        Assert.Equal("Meat now 5.", prompt.EvidenceShown);
        Assert.Equal(expectedSpent, ledger.SpentToday("mallbot") - expectedUnclear);
        Assert.Equal(expectedUnclear, ledger.UnclearToday("mallbot"));
    }

    [Fact]
    public async Task Reconcile_with_nothing_unclear_asks_nothing()
    {
        var prompt = new Prompt(MallUnclearDecision.WasSpent);
        var coordinator = new MallPurchaseCoordinator("mallbot",
            _ => Task.FromResult<DesktopMallPurchaseService?>(null),
            null!, new SessionLogViewModel(now: () => Now), new MallSpendLedger(new MallKit.Clock(Now)), null, prompt, null);
        await coordinator.ReconcileUnclearAsync(default);
        Assert.Equal(0, prompt.Asked);
    }

    [Fact]
    public void ResolveUnclear_refuses_a_changed_amount()
    {
        var ledger = new MallSpendLedger(new MallKit.Clock(Now));
        Assert.True(ledger.TryReserve("mallbot", 100, null, out _));
        ledger.MarkUnclear("mallbot", 100);
        Assert.False(ledger.ResolveUnclear("mallbot", 99, wasSpent: true));
        Assert.Equal(100, ledger.UnclearAmount("mallbot"));
        Assert.True(ledger.ResolveUnclear("mallbot", 100, wasSpent: false));
        Assert.Equal(0, ledger.SpentToday("mallbot"));
    }

    [Fact]
    public void Desktop_ledger_shares_the_cross_process_rules()
    {
        var dir = Directory.CreateTempSubdirectory("money-").FullName;
        try
        {
            var path = Path.Combine(dir, "mall.json");
            var a = new MallSpendLedger(new MallKit.Clock(Now), path);
            Assert.True(a.TryReserve("Main", 400, 500, out _));
            var b = new MallSpendLedger(new MallKit.Clock(Now), path);
            Assert.False(b.TryReserve("Main", 200, 500, out _));
            a.Settle("Main", 400);
            Assert.Equal(400, b.SpentToday("Main"));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- BUDGET-05: reconcile compare-and-set, day change, crash recovery (KoLibri.Extras ledger) -------

    private static string TempLedger(out string dir)
    {
        dir = Directory.CreateTempSubdirectory("money-").FullName;
        return Path.Combine(dir, "mall.json");
    }

    [Fact]
    public void Reconcile_changes_nothing_when_another_instance_marked_more_meat_unclear()
    {
        var path = TempLedger(out var dir);
        try
        {
            var a = new MallSpendLedger(new MallKit.Clock(Now), path);
            var b = new MallSpendLedger(new MallKit.Clock(Now), path);
            a.Reserve("Mallbot", 700);
            a.MarkUnclear("Mallbot", 700);
            var seen = b.ReadUnclear("Mallbot");
            Assert.Equal(700, seen.Total);

            Assert.True(a.TryReserve("Mallbot", 500, null, out var more, out _));
            a.MarkUnclear(more);
            Assert.False(b.Reconcile("Mallbot", seen, 0));
            Assert.Equal(1_200, b.UnclearAmount("Mallbot"));
            Assert.Equal(1_200, b.SpentToday("Mallbot"));
            Assert.Equal(0, b.ReservedAmount("Mallbot"));

            var current = b.ReadUnclear("Mallbot");
            Assert.Equal(1_200, current.Total);
            Assert.True(b.Reconcile("Mallbot", current, 0));
            Assert.Equal(0, b.SpentToday("Mallbot"));
            Assert.Equal(0, b.UnclearAmount("Mallbot"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Reconcile_changes_nothing_when_another_instance_reserved_meanwhile()
    {
        var path = TempLedger(out var dir);
        try
        {
            var a = new MallSpendLedger(new MallKit.Clock(Now), path);
            var b = new MallSpendLedger(new MallKit.Clock(Now), path);
            a.Reserve("Mallbot", 700);
            a.MarkUnclear("Mallbot", 700);
            var seen = b.ReadUnclear("Mallbot");
            Assert.Equal(700, seen.Total);

            a.Reserve("Mallbot", 500);   // a new purchase in flight in the other instance
            Assert.False(b.Reconcile("Mallbot", seen, 0));
            Assert.Equal(700, b.UnclearAmount("Mallbot"));
            Assert.Equal(500, b.ReservedAmount("Mallbot"));
            Assert.Equal(1_200, b.UnclearToday("Mallbot"));

            var current = b.ReadUnclear("Mallbot");
            Assert.Equal(1_200, current.Total);
            Assert.True(b.Reconcile("Mallbot", current, 0));
            Assert.Equal(0, b.SpentToday("Mallbot"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Reconcile_with_a_snapshot_notices_a_changed_mix_and_a_day_change()
    {
        var path = TempLedger(out var dir);
        try
        {
            var clock = new MallKit.Clock(Now);
            var a = new MallSpendLedger(clock, path);
            var b = new MallSpendLedger(clock, path);
            a.Reserve("Mallbot", 400);
            a.MarkUnclear("Mallbot", 400);
            Assert.True(a.TryReserve("Mallbot", 300, null, out var open, out _));
            var snapshot = b.ReadUnclear("Mallbot");
            Assert.Equal(new UnclearSnapshot("Mallbot", snapshot.Day, 400, 300, snapshot.Revision),
                snapshot with { ForeignIds = null });
            Assert.Equal(open.Id, Assert.Single(snapshot.ForeignIds!));
            Assert.True(snapshot.Revision > 0);

            a.MarkUnclear(open);   // same total (700), different mix: unclear 700, foreign 0
            Assert.False(b.Reconcile("Mallbot", snapshot, 0));
            Assert.Equal(700, b.UnclearAmount("Mallbot"));

            var current = b.ReadUnclear("Mallbot");
            Assert.True(b.Reconcile("Mallbot", current, 100));
            Assert.Equal(100, b.SpentToday("Mallbot"));
            Assert.Equal(0, b.UnclearAmount("Mallbot"));

            a.Reserve("Mallbot", 50);
            a.MarkUnclear("Mallbot", 50);
            var dayOne = b.ReadUnclear("Mallbot");
            clock.Value = Now.AddDays(1);
            Assert.False(b.Reconcile("Mallbot", dayOne, 0));   // the day changed: nothing is booked on day 2
            Assert.Equal(0, b.CountedToday("Mallbot"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Reconcile_refuses_a_snapshot_from_another_account_with_the_same_amounts()
    {
        var path = TempLedger(out var dir);
        try
        {
            var owner = new MallSpendLedger(new MallKit.Clock(Now), path);
            foreach (var account in new[] { "Main", "Mallbot" })
            {
                owner.Reserve(account, 100);
                owner.MarkUnclear(account, 100);
            }
            var reader = new MallSpendLedger(new MallKit.Clock(Now), path);
            var main = reader.ReadUnclear("Main");
            var mallbot = reader.ReadUnclear("Mallbot");
            Assert.Equal(main with { Account = "Mallbot", ForeignIds = null }, mallbot with { ForeignIds = null });
            Assert.Empty(main.ForeignIds!);
            Assert.Empty(mallbot.ForeignIds!);

            Assert.False(reader.Reconcile("Mallbot", main, 0));
            Assert.Equal(100, reader.UnclearAmount("Mallbot"));
            Assert.True(reader.Reconcile("MALLBOT", mallbot, 0));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Reconcile_refuses_a_snapshot_that_identifies_different_foreign_reservations()
    {
        var path = TempLedger(out var dir);
        try
        {
            var a = new MallSpendLedger(new MallKit.Clock(Now), path);
            var b = new MallSpendLedger(new MallKit.Clock(Now), path);
            Assert.True(a.TryReserve("Mallbot", 100, null, out var aOpen, out _));
            Assert.True(b.TryReserve("Mallbot", 100, null, out var bOpen, out _));
            var fromA = a.ReadUnclear("Mallbot");
            var fromB = b.ReadUnclear("Mallbot");
            Assert.Equal(fromA with { ForeignIds = null }, fromB with { ForeignIds = null });
            Assert.Equal(bOpen.Id, Assert.Single(fromA.ForeignIds!));
            Assert.Equal(aOpen.Id, Assert.Single(fromB.ForeignIds!));

            Assert.False(b.Reconcile("Mallbot", fromA, 0));
            Assert.Equal(aOpen.Id, Assert.Single(b.ReadUnclear("Mallbot").ForeignIds!));
            Assert.Equal(200, b.ReservedAmount("Mallbot"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("release")]
    [InlineData("settle")]
    [InlineData("unclear")]
    public void Desktop_ledger_finishing_after_the_day_change_keeps_the_new_days_reservation(string finish)
    {
        var path = TempLedger(out var dir);
        try
        {
            var clockA = new MallKit.Clock(Now);
            var a = new MallSpendLedger(clockA, path);
            Assert.True(a.TryReserve("Mallbot", 300, null, out var dayOne, out _));

            clockA.Value = Now.AddDays(1);
            var b = new MallSpendLedger(new MallKit.Clock(Now.AddDays(1)), path);
            Assert.True(b.TryReserve("Mallbot", 500, null, out _, out _));

            if (finish == "release") a.Release(dayOne);
            else if (finish == "settle") a.Settle(dayOne);
            else a.MarkUnclear(dayOne);

            Assert.Equal(500, b.ReservedAmount("Mallbot"));
            Assert.Equal(finish == "release" ? 500 : 800, b.CountedToday("Mallbot"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Desktop_ledger_counts_the_reservation_of_a_crashed_instance()
    {
        var path = TempLedger(out var dir);
        try
        {
            Assert.True(new MallSpendLedger(new MallKit.Clock(Now), path).TryReserve("Mallbot", 400, 1_000, out _, out _));
            var restarted = new MallSpendLedger(new MallKit.Clock(Now), path);
            Assert.Equal(400, restarted.SpentToday("Mallbot"));
            Assert.Equal(400, restarted.UnclearToday("Mallbot"));
            Assert.Equal(400, restarted.CountedToday("Mallbot"));
            Assert.False(restarted.TryReserve("Mallbot", 601, 1_000, out _));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(MallUnclearDecision.NotSpent, 0)]
    [InlineData(MallUnclearDecision.WasSpent, 400)]
    public async Task Reconcile_in_the_app_also_sees_and_clears_the_reservation_of_a_crashed_run(
        MallUnclearDecision decision, long expectedSpent)   // review #7
    {
        var path = TempLedger(out var dir);
        try
        {
            Assert.True(new MallSpendLedger(new MallKit.Clock(Now), path).TryReserve("Mallbot", 400, 1_000, out _, out _));   // "crash"
            var ledger = new MallSpendLedger(new MallKit.Clock(Now), path);
            var prompt = new Prompt(decision);
            var coordinator = new MallPurchaseCoordinator("Mallbot",
                _ => Task.FromResult<DesktopMallPurchaseService?>(null),
                null!, new SessionLogViewModel(now: () => Now), ledger, null, prompt, null);
            Assert.Equal(400, coordinator.UnclearAmount);

            Assert.Equal(decision, await coordinator.ReconcileUnclearAsync(default));
            Assert.Equal(400, prompt.AmountShown);
            Assert.Equal(0, coordinator.UnclearAmount);
            Assert.Equal(0, ledger.ReservedAmount("Mallbot"));
            Assert.Equal(expectedSpent, new MallSpendLedger(new MallKit.Clock(Now), path).CountedToday("Mallbot"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Evidence_shows_fresh_meat_and_the_inventory_size()
    {
        var kol = MallKit.Kol(25_000, """{"10881":2,"25":3}""");
        await using var accounts = MallKit.Accounts(kol);
        var evidence = await MallUnclearEvidence.ReadAsync(new LiveMallPurchaseReads(new DesktopLiveDataProvider(accounts), AccountRole.Mallbot), default);
        Assert.Equal("Meat now 1,000,000; 5 items in inventory.", evidence);
    }

    [Fact]
    public async Task Evidence_for_a_signed_out_role_says_so_instead_of_throwing()
    {
        await using var accounts = DesktopKit.Accounts(new FakeKol());
        var evidence = await MallUnclearEvidence.ReadAsync(new LiveMallPurchaseReads(new DesktopLiveDataProvider(accounts), AccountRole.Mallbot), default);
        Assert.Equal("Meat unknown; inventory could not be read fresh.", evidence);
    }

    private sealed class Prompt(MallUnclearDecision decision) : IMallReconcilePrompt
    {
        public int Asked { get; private set; }
        public long AmountShown { get; private set; }
        public string? EvidenceShown { get; private set; }
        public Task<MallUnclearDecision> AskAsync(string account, long amount, string evidence, CancellationToken ct)
        {
            Asked++;
            AmountShown = amount;
            EvidenceShown = evidence;
            return Task.FromResult(decision);
        }
    }
}
