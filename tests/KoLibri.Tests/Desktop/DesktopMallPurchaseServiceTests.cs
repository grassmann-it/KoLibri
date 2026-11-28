using KoLibri.Desktop;
using Xunit;

namespace KoLibri.Tests.Desktop;

/// <summary>
/// The desktop purchase on the library: the offer is read fresh, the POST is bound to the price just read
/// (whichitem=item.price), never above the price the user saw, and inventory decides the outcome.
/// </summary>
public sealed class DesktopMallPurchaseServiceTests
{
    [Fact]
    public async Task Buys_once_at_the_price_just_read_with_the_price_bound_form()
    {
        var kol = MallKit.Kol(25_000, "{}", """{"10881":1}""")
            .On("POST mallstore.php", "<table><tr><td>You acquire an item: <b>11-leaf clover</b></td></tr></table>You spent 25,000 Meat.");
        await using var accounts = MallKit.Accounts(kol);

        var result = await MallKit.Service(accounts).BuyAsync("Mallbot", "10881", "56226", 25_500, 1, shopName: "Crazy Gordon");

        Assert.Equal(DesktopMallPurchaseStatus.Purchased, result.Status);
        Assert.Equal(1, result.ReceivedQuantity);
        Assert.Equal(25_000, result.SpentMeat);
        Assert.Equal(25_000, result.UnitPrice);
        var form = Assert.Single(MallKit.Purchases(kol));
        Assert.Contains("whichitem=10881.25000", form);
        Assert.Contains("whichstore=56226", form);
        Assert.Contains("quantity=1", form);
        Assert.Contains("buying=1", form);
        Assert.Contains("pwd=", form);
    }

    [Fact]
    public async Task A_price_above_the_shown_price_sends_nothing()
    {
        var kol = MallKit.Kol(25_000);
        await using var accounts = MallKit.Accounts(kol);

        var result = await MallKit.Service(accounts).BuyAsync("Mallbot", "10881", "56226", 24_000, 1);

        Assert.Equal(DesktopMallPurchaseStatus.PriceChanged, result.Status);
        Assert.Contains("rose from 24,000 to 25,000", result.Message);
        Assert.Empty(MallKit.Purchases(kol));
    }

    [Theory]
    [InlineData(2, 150, MallPurchaseAbortReason.PerPurchaseLimitExceeded)]
    [InlineData(6, 1_000_000, MallPurchaseAbortReason.QuantityLimitExceeded)]
    public async Task Per_purchase_limits_refuse_before_any_request(int quantity, long maxMeat, MallPurchaseAbortReason reason)
    {
        var kol = MallKit.Kol(100);
        await using var accounts = MallKit.Accounts(kol);
        var options = new DesktopMallPurchaseOptions { MaxMeatPerPurchase = maxMeat, MaxQuantityPerPurchase = 5 };

        var result = await MallKit.Service(accounts, options).BuyAsync("Mallbot", "10881", "56226", 100, quantity);

        Assert.Equal(DesktopMallPurchaseStatus.NotSent, result.Status);
        Assert.Contains(reason, result.Reasons);
        Assert.Contains("Nothing was sent", result.Message);
        Assert.Empty(kol.Requests);
    }

    [Fact]
    public async Task A_confirmed_receipt_without_the_item_in_the_inventory_is_unclear_and_never_retried()
    {
        var kol = MallKit.Kol(25_000, "{}")
            .On("POST mallstore.php", "You acquire an item: <b>11-leaf clover</b>. You spent 25,000 Meat.");
        await using var accounts = MallKit.Accounts(kol);

        var result = await MallKit.Service(accounts).BuyAsync("Mallbot", "10881", "56226", 25_000, 1);

        Assert.Equal(DesktopMallPurchaseStatus.Unclear, result.Status);
        Assert.Contains("Reconcile unclear purchases", result.Message);
        Assert.Equal(0, result.SpentMeat);
        Assert.Single(MallKit.Purchases(kol));
    }

    [Fact]
    public async Task A_partial_receipt_backed_by_the_inventory_counts_only_what_arrived()
    {
        var kol = MallKit.Kol(25_000, "{}", """{"10881":1}""")
            .On("POST mallstore.php", "You acquire an item: <b>11-leaf clover</b> (1). You spent 25,000 Meat.");
        await using var accounts = MallKit.Accounts(kol);

        var result = await MallKit.Service(accounts).BuyAsync("Mallbot", "10881", "56226", 25_000, 2);

        Assert.Equal(DesktopMallPurchaseStatus.Partial, result.Status);
        Assert.Equal(1, result.ReceivedQuantity);
        Assert.Equal(25_000, result.SpentMeat);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task A_refusal_by_KoL_is_rejected_with_its_text()
    {
        var kol = MallKit.Kol(25_000, "{}")
            .On("POST mallstore.php", "<center>This store doesn't have that item at that price.</center>");
        await using var accounts = MallKit.Accounts(kol);

        var result = await MallKit.Service(accounts).BuyAsync("Mallbot", "10881", "56226", 25_000, 1);

        Assert.Equal(DesktopMallPurchaseStatus.Rejected, result.Status);
        Assert.Contains("doesn't have that item", result.Message);
    }

    [Fact]
    public async Task A_listing_that_is_gone_sends_nothing()
    {
        var kol = MallKit.Kol(25_000);
        await using var accounts = MallKit.Accounts(kol);

        var result = await MallKit.Service(accounts).BuyAsync("Mallbot", "10881", "99999", 25_000, 1);

        Assert.Equal(DesktopMallPurchaseStatus.NotSent, result.Status);
        Assert.Contains(MallPurchaseAbortReason.MissingOffer, result.Reasons);
        Assert.Empty(MallKit.Purchases(kol));
    }

    [Fact]
    public async Task A_guard_denial_is_not_sent()
    {
        var service = new DesktopMallPurchaseService(
            (_, _) => throw new ActionDeniedException(new GuardedAction(new AccountId("mallbot"), GuardedActionKind.MallBuy, "buy"), "no budget left"),
            new NoReads());

        var result = await service.BuyAsync("Mallbot", "10881", "56226", 100, 1);

        Assert.Equal(DesktopMallPurchaseStatus.NotSent, result.Status);
        Assert.Contains("no budget left", result.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Method_group_and_role_adapter_check_the_gate_at_the_session_transport(bool forRole)
    {
        var kol = MallKit.Kol(100);
        await using var accounts = MallKit.Accounts(kol);
        var live = new DesktopLiveDataProvider(accounts);
        var session = await live.RequireSessionAsync(AccountRole.Mallbot, "buy in the mall");
        var service = forRole ? MallKit.Service(accounts) : new DesktopMallPurchaseService(session.Mall.BuyAsync, new NoReads());
        var checks = 0;

        var result = await service.BuyAsync("Mallbot", "10881", "56226", 100, 1, default, null, _ =>
        {
            checks++;
            Assert.Equal(1, kol.Count("POST", "mall.php"));
            Assert.Empty(MallKit.Purchases(kol));
            return ValueTask.FromResult(GuardDecision.Deny("Budget changed."));
        });

        Assert.Equal(1, checks);
        Assert.Equal(DesktopMallPurchaseStatus.NotSent, result.Status);
        Assert.Contains("Budget changed.", result.Message);
        Assert.Empty(MallKit.Purchases(kol));
    }

    [Fact]
    public async Task A_two_argument_delegate_cannot_send_with_a_gate()
    {
        var buys = 0;
        var service = new DesktopMallPurchaseService((_, _) =>
        {
            buys++;
            return Task.FromResult(new PurchaseResult(PurchaseStatus.Bought, 1, 100, []));
        }, new NoReads());

        var result = await service.BuyAsync("Mallbot", "10881", "56226", 100, 1, default, null,
            _ => ValueTask.FromResult(GuardDecision.Allow));

        Assert.Equal(DesktopMallPurchaseStatus.NotSent, result.Status);
        Assert.Contains("The buy delegate cannot verify pre-send gates at transport time.", result.Message);
        Assert.Equal(0, buys);
    }

    [Fact]
    public void Library_statuses_map_onto_the_desktop_states()
    {
        Assert.Equal(DesktopMallPurchaseStatus.NotSent,
            DesktopMallPurchaseService.Map(new PurchaseResult(PurchaseStatus.Refused, 0, 0, []), 1, 100).Status);
        Assert.Equal(DesktopMallPurchaseStatus.NotSent,
            DesktopMallPurchaseService.Map(new PurchaseResult(PurchaseStatus.NothingBought, 0, 0, []), 1, 100).Status);
        var partial = DesktopMallPurchaseService.Map(
            new PurchaseResult(PurchaseStatus.Partial, 2, 200, [new PurchaseLine(1, 100, 3, 2, null)]), 3, 100);
        Assert.Equal((DesktopMallPurchaseStatus.Partial, 2, 200L, 100L), (partial.Status, partial.ReceivedQuantity, partial.SpentMeat, partial.UnitPrice));
    }

    private sealed class NoReads : IMallPurchaseReads
    {
        public Task<Status?> StatusAsync(CancellationToken cancellationToken) => Task.FromResult<Status?>(null);
        public Task<DesktopInventorySnapshot> InventoryAsync(CancellationToken cancellationToken)
            => Task.FromResult(new DesktopInventorySnapshot { Role = AccountRole.Mallbot, Items = [], ObservedAt = DateTimeOffset.UtcNow, HasSession = false });
    }
}
