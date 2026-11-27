using KoLibri.Desktop;
using KoLibri.Desktop.ContextMenus;
using KoLibri.Extras;
using Xunit;

namespace KoLibri.Tests.Desktop;

public sealed class MallTabTests
{
    private static readonly Uri Source = new("https://www.kingdomofloathing.com/mall.php");
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Value = now;
        public override DateTimeOffset GetUtcNow() => Value;
    }

    private sealed class Backend : IMallSearchBackend
    {
        public List<string> Terms { get; } = new();
        public Task<global::KoLibri.MallSearchResult> SearchAsync(string term, bool force, CancellationToken ct)
        {
            Terms.Add(term);
            return Task.FromResult(MallSearchViewModelTests.Page(term,
                MallSearchViewModelTests.Offers(10881, "11-leaf clover",
                    MallSearchViewModelTests.Listing(10881, 1, 25_000), MallSearchViewModelTests.Listing(10881, 2, 29_000)),
                MallSearchViewModelTests.Offers(10882, "11-leaf clover pack", MallSearchViewModelTests.Listing(10882, 3, 100))));
        }
        public Task<PricegunInfo> LoadPricegunAsync(string itemId, CancellationToken ct)
            => Task.FromResult(new PricegunInfo(null, DateTimeOffset.UtcNow, false));
    }

    private sealed class AllowAll : IMallPurchaseRequest
    {
        public bool CanRequest(MallOfferRow offer) => true;
        public Task RequestAsync(MallOfferRow offer, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task Coordinator_refuses_a_stale_row_without_asking_or_sending()
    {
        var log = new SessionLogViewModel(now: () => Now);
        var opened = 0;
        var coordinator = new MallPurchaseCoordinator("Mallbot",
            _ => { opened++; return Task.FromResult<DesktopMallPurchaseService?>(null); },
            null!, log, new MallSpendLedger(new Clock(Now)));
        var row = new MallOfferRow
        {
            Rank = 1, ItemId = "1", ItemName = "Thing", ShopId = "42", ShopName = "S", UnitPrice = 10, Stock = 3,
            LimitKind = MallLimitKind.Unlimited, IsStale = true
        };
        Assert.False(coordinator.CanRequest(row));
        await coordinator.RequestAsync(row, default);
        Assert.Equal(0, opened);
        Assert.Contains(log.Entries, e => e.Message.Contains("stale"));
    }

    [Fact]
    public async Task Item_opened_with_an_id_lands_on_that_item_even_if_the_name_matches_several()
    {
        var backend = new Backend();
        var vm = new MallSearchViewModel(backend);
        await vm.SearchItemAsync("10882", "11-leaf");
        Assert.Equal("11-leaf", backend.Terms.Single());
        Assert.Equal("10882", vm.Offers.Single().ItemId);
    }

    [Fact]
    public async Task Clover_quick_action_highlights_offers_under_the_threshold()
    {
        var vm = new MallSearchViewModel(new Backend()) { CloverThreshold = () => 27_000 };
        await vm.SearchCloverAsync();
        Assert.Equal("11-leaf clover", vm.SearchText);
        Assert.True(vm.HasThreshold);
        Assert.Equal(new[] { true, false }, vm.Offers.Select(o => o.UnderThreshold).ToArray());
        await vm.SearchAsync(); // a normal search switches the highlight off
        Assert.False(vm.HasThreshold);
        Assert.All(vm.Offers, o => Assert.False(o.UnderThreshold));
    }

    [Fact]
    public void Clover_threshold_setting_has_default_and_is_clamped()
    {
        var s = new DesktopSettings();
        Assert.Equal(27_000, s.Get("cloverThreshold"));
        s.Set("cloverThreshold", 5);
        Assert.Equal(1_000, s.Get("cloverThreshold"));
        s.Set("cloverThreshold", 999_999_999);
        Assert.Equal(1_000_000, s.Get("cloverThreshold"));
    }

    [Fact]
    public void Spend_persists_per_account_and_kol_day_and_unclear_stays_counted()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var path = Path.Combine(dir, "ledger.json");
            var clock = new Clock(Now);
            var a = new MallSpendLedger(clock, path);
            a.Add("Main", 1_000);
            a.Reserve("Mallbot", 500);
            a.Settle("Mallbot", 500);
            a.Reserve("Mallbot", 700);
            a.MarkUnclear("Mallbot", 700);
            a.Reserve("Main", 300); // still in flight at "crash" time

            var b = new MallSpendLedger(clock, path);
            Assert.Equal(1_300, b.SpentToday("Main")); // in-flight reservation counts as unclear
            Assert.Equal(1_200, b.SpentToday("Mallbot"));
            Assert.Equal(700, b.UnclearToday("Mallbot"));

            b.Reconcile("Mallbot", 0); // verified: not spent
            Assert.Equal(500, new MallSpendLedger(clock, path).SpentToday("Mallbot"));

            clock.Value = Now.AddDays(1); // next KoL day: clean
            Assert.Equal(0, new MallSpendLedger(clock, path).SpentToday("Main"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Unreadable_ledger_counts_as_fully_spent()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var path = Path.Combine(dir, "ledger.json");
            File.WriteAllText(path, "{ not json");
            var ledger = new MallSpendLedger(new Clock(Now), path);
            Assert.True(ledger.Unreadable);
            Assert.True(ledger.SpentToday("Main") > 1_000_000_000);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(MallPurchaseAbortReason.SessionInvalid, "not logged in")]
    [InlineData(MallPurchaseAbortReason.PerPurchaseLimitExceeded, "more expensive")]
    [InlineData(MallPurchaseAbortReason.SoldOut, "sold out")]
    [InlineData(MallPurchaseAbortReason.PurchaseLimitExceeded, "limit")]
    [InlineData(MallPurchaseAbortReason.InsufficientMeat, "enough Meat")]
    public void Every_failure_has_a_readable_message(MallPurchaseAbortReason reason, string part)
        => Assert.Contains(part, MallPurchaseMessages.Describe(reason), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void No_abort_reason_falls_back_to_its_enum_name()
    {
        foreach (var r in Enum.GetValues<MallPurchaseAbortReason>().Where(r => r != MallPurchaseAbortReason.None))
            Assert.NotEqual(r.ToString(), MallPurchaseMessages.Describe(r));
    }

    [Fact]
    public void Offer_menu_has_buy_copy_link_copy_seller_and_search_again()
    {
        var target = new OfferMenuTarget("10881", "11-leaf clover", "Shop 1", 25_000, true) { ShopId = "42", SellerName = "Seller 1" };
        Assert.Equal("https://www.kingdomofloathing.com/mallstore.php?whichstore=42", target.ShopLink);
        var items = new MallOfferContextMenu(null!, () => Task.CompletedTask, () => Task.CompletedTask).Build(target);
        var labels = items.Select(i => i.Label).ToList();
        Assert.Contains("Buy…", labels);
        Assert.Contains("Copy shop link", labels);
        Assert.Contains("Copy seller", labels);
        Assert.Contains("Search this item again", labels);
    }

    [Fact]
    public async Task Rows_get_buyable_with_a_purchase_hook_and_the_only_buyable_filter_hides_the_rest()
    {
        var vm = new MallSearchViewModel(new Backend()) { PurchaseRequest = new OnlyCheap() };
        await vm.SearchItemAsync("10881", "11-leaf clover");
        Assert.Equal(new[] { true, false }, vm.Offers.Select(o => o.CanBuy).ToArray());
        vm.OnlyBuyable = true;
        Assert.Single(vm.Offers);
        Assert.Equal(1, vm.HiddenOfferCount);
    }

    [Fact]
    public async Task Own_shop_lists_the_listings_from_backoffice()
    {
        var kol = new FakeKol().On("GET backoffice.php", OwnShopPage);
        await using var accounts = MallKit.Accounts(kol);
        var shop = new LiveMallViewModel(AccountRole.Mallbot, new DesktopLiveDataProvider(accounts));
        await shop.LoadAsync();
        Assert.Equal(DesktopLoadState.Ready, shop.LoadState);
        var row = Assert.Single(shop.Rows);
        Assert.Equal(("989", "tiny plastic bitchin' meatcar", 73, 17_999L, 1), (row.ItemId, row.ItemName, row.Quantity, row.Price, row.PurchaseLimit!.Value));
        Assert.Equal("17,999 Meat", row.PriceText);
        Assert.Equal("1 / day", row.LimitText);
        Assert.Contains("1 listings", shop.Summary);
        Assert.True(kol.Requests.Any(r => r.Path.Contains("backoffice.php?which=1")));
    }

    [Fact]
    public async Task Own_shop_of_a_signed_out_role_says_how_to_sign_in()
    {
        await using var accounts = DesktopKit.Accounts(new FakeKol());
        var shop = new LiveMallViewModel(AccountRole.Mallbot, new DesktopLiveDataProvider(accounts));
        await shop.LoadAsync();
        Assert.Equal(DesktopLoadState.Empty, shop.LoadState);
        Assert.Equal("Sign in as Mallbot under Accounts to see store listings.", shop.Summary);
    }

    private sealed class OnlyCheap : IMallPurchaseRequest
    {
        public bool CanRequest(MallOfferRow offer) => offer.UnitPrice < 27_000;
        public Task RequestAsync(MallOfferRow offer, CancellationToken ct) => Task.CompletedTask;
    }

    private const string OwnShopPage = """
        <html><body><table id="inv">
        <tr class="deets" rel="624148858" data-after="202"><td valign="center"><img src="https://images.kingdomofloathing.com/itemimages/fig27.gif"></td><td valign="center"><b>tiny plastic bitchin' meatcar</b></td><td valign="center" align="center">73</td valign="center"><td align="center"><span class="tohide">17,999</span><input type="text" class="hideit price" rel="100" style="width:80px" name="price[989]" value="17,999" /></td><td valign="center" align="center"><span class="tohide">1/day</span><input type="text" class="hideit lim" style="width:24px" name="limit[989]" value="1" /><input type="submit" value="Save" class="button hideit pricejax" style="font-size: 8pt"/></td><td align="right" valign="center">[<a href="#" class="update">update</a>]</td></tr>
        </table></body></html>
        """;
}
