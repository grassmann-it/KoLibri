using KoLibri.Desktop;
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
