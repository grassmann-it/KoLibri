using Xunit;
using KoLibri.Core.Markt;

namespace KoLibri.Core.Tests;

public sealed class ItemArbitrageExecutorTests
{
    private static MallPurchaseFixtureTransport Transport(long price = 25_000, int stock = 10, long meat = 1_000_000)
    {
        var now = DateTimeOffset.UtcNow;
        var offer = new MallPurchaseOffer("56226", "56226", "10881", price, stock, now, 5);
        var account = new MallPurchaseAccountState("mallbot", meat, 0, 2_000_000, new Dictionary<string, int>(), now);
        return new MallPurchaseFixtureTransport(offer, account);
    }

    private static ItemArbitragePlan Plan(int quantity = 2, long price = 25_000) => new()
    {
        ItemId = 10881,
        Steps = [new ArbitrageStep("10881", "56226", "Crazy Gordon", price, quantity, price * quantity, 5_000, 5_000L * quantity, 5, MallLimitKind.Limited, null)]
    };

    private static ItemArbitrageExecutor Executor(MallPurchaseFixtureTransport transport, ArbitrageSpendLedger ledger)
        => new(new MallPurchaseGateway(transport, new MallPurchasePolicy
        {
            DryRun = false, RequireApproval = false, MaxDailySpend = 2_000_000, MaxMeatPerPurchase = 500_000, MaxQuantityPerPurchase = 10
        }), ledger, "mallbot");

    [Fact]
    public async Task A_preview_sends_nothing()
    {
        var transport = Transport();
        var ledger = new ArbitrageSpendLedger();
        var run = await Executor(transport, ledger).PreviewAsync(Plan());

        Assert.False(run.Executed);
        Assert.Equal(ArbitrageStepStatus.Previewed, Assert.Single(run.Steps).Status);
        Assert.Equal(0, transport.CommitCount);
        Assert.Equal(0, ledger.SpentToday("mallbot"));
    }

    [Fact]
    public async Task A_verified_purchase_sends_one_commit_and_settles_the_ledger()
    {
        var transport = Transport();
        var ledger = new ArbitrageSpendLedger();
        var run = await Executor(transport, ledger).ExecuteAsync(Plan());

        Assert.Equal(ArbitrageStepStatus.Purchased, Assert.Single(run.Steps).Status);
        Assert.Equal(1, transport.CommitCount);
        Assert.Equal(2, run.UnitsBought);
        Assert.Equal(50_000, run.MeatSpent);
        Assert.Equal(50_000, ledger.SpentToday("mallbot"));
    }

    [Fact]
    public async Task A_moved_price_aborts_before_any_commit_and_releases_nothing_to_the_ledger()
    {
        var transport = Transport(price: 26_500);
        var ledger = new ArbitrageSpendLedger();
        var run = await Executor(transport, ledger).ExecuteAsync(Plan(price: 25_000));

        Assert.Equal(ArbitrageStepStatus.NotSent, Assert.Single(run.Steps).Status);
        Assert.Equal(0, transport.CommitCount);
        Assert.Equal(0, ledger.SpentToday("mallbot"));
    }

    [Fact]
    public async Task Too_little_meat_aborts_before_any_commit()
    {
        var transport = Transport(meat: 10_000);
        var run = await Executor(transport, new ArbitrageSpendLedger()).ExecuteAsync(Plan());
        Assert.Equal(ArbitrageStepStatus.NotSent, Assert.Single(run.Steps).Status);
        Assert.Equal(0, transport.CommitCount);
    }

    [Fact]
    public void The_ledger_is_persisted_and_an_open_reservation_counts_as_unclear_after_a_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), "arb-ledger-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var ledger = new ArbitrageSpendLedger(path);
            ledger.Reserve("mallbot", 50_000);
            ledger.Settle("mallbot", 20_000);

            var reloaded = new ArbitrageSpendLedger(path);
            Assert.Equal(50_000, reloaded.SpentToday("mallbot"));
            Assert.Equal(30_000, reloaded.UnclearToday("mallbot"));

            File.WriteAllText(path, "not json");
            Assert.True(new ArbitrageSpendLedger(path).SpentToday("mallbot") > 1_000_000_000_000);
        }
        finally { File.Delete(path); }
    }
}
