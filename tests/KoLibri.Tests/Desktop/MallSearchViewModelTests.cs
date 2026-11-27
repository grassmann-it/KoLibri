using KoLibri.Desktop;
using Xunit;

namespace KoLibri.Tests.Desktop;

/// <summary>The search view model on the library's mall records (MallSearchResult / MallOffers / MallListing).</summary>
public sealed class MallSearchViewModelTests
{
    internal static MallListing Listing(int item, int shop, long price, int qty = 5, int? limit = null, bool limitKnown = true) =>
        new(shop, $"Shop {shop}", item, price, qty, limit) { LimitKnown = limitKnown };

    internal static global::KoLibri.MallSearchResult Page(string term, params MallOffers[] items) => new(term, items, DateTimeOffset.UtcNow);

    internal static MallOffers Offers(int item, string name, params MallListing[] listings) => new(item, name, listings, DateTimeOffset.UtcNow);

    internal sealed class FakeBackend : IMallSearchBackend
    {
        public List<(string Term, bool Force)> Searches { get; } = new();
        public int PricegunCalls;
        public bool PricegunFails;
        public long? Median = 26_000;
        public Func<string, global::KoLibri.MallSearchResult> Respond = term => Page(term);

        public Task<global::KoLibri.MallSearchResult> SearchAsync(string term, bool force, CancellationToken ct)
        {
            Searches.Add((term, force));
            return Task.FromResult(Respond(term));
        }

        public Task<PricegunInfo> LoadPricegunAsync(string itemId, CancellationToken ct)
        {
            PricegunCalls++;
            return PricegunFails
                ? throw new HttpRequestException("pricegun down")
                : Task.FromResult(new PricegunInfo(Median, DateTimeOffset.UtcNow.AddMinutes(-5), false));
        }
    }

    private static global::KoLibri.MallSearchResult OneItem(string term) =>
        Page(term, Offers(10881, "11-leaf clover", Listing(10881, 1, 25_000), Listing(10881, 2, 29_000, 1460, limit: 3)));

    private static IReadOnlyList<(string, string)> Catalog(string text) =>
        text is "10881" or "11-leaf clover" ? new[] { ("10881", "11-leaf clover") } : Array.Empty<(string, string)>();

    [Fact]
    public async Task An_item_id_resolves_to_an_exact_name_search_and_rows_keep_ids()
    {
        var backend = new FakeBackend { Respond = OneItem };
        var vm = new MallSearchViewModel(backend, Catalog) { SearchText = "10881" };
        await vm.SearchAsync();
        Assert.Equal("\"11-leaf clover\"", backend.Searches.Single().Term);
        Assert.Equal(2, vm.Offers.Count);
        var first = vm.Offers[0];
        Assert.Equal(("10881", "1", 25_000L, 1), (first.ItemId, first.ShopId, first.UnitPrice, first.Rank));
        Assert.Equal("Shop 1", first.ShopName);
        Assert.Equal(26_000, first.PricegunMedian);
        Assert.Equal("1,460 in stock", vm.Offers[1].StockText);
        Assert.Equal("3 / day", vm.Offers[1].LimitText);
        Assert.Equal("unlimited", first.LimitText);
        Assert.All(vm.Offers, o => Assert.False(o.CanBuy)); // no purchase hook yet
        Assert.All(vm.Offers, o => Assert.False(o.IsStale));
    }

    [Fact]
    public async Task Unknown_limits_and_stock_are_shown_as_unknown()
    {
        var backend = new FakeBackend
        {
            Respond = term => Page(term, Offers(10881, "11-leaf clover",
                Listing(10881, 1, 25_000, limitKnown: false) with { QuantityKnown = false }))
        };
        var vm = new MallSearchViewModel(backend, Catalog) { SearchText = "11-leaf clover" };
        await vm.SearchAsync();
        var row = Assert.Single(vm.Offers);
        Assert.Equal("?", row.LimitText);
        Assert.Equal("stock ?", row.StockText);
    }

    [Fact]
    public async Task A_partial_name_with_several_items_asks_to_pick_one()
    {
        var backend = new FakeBackend
        {
            Respond = term => Page(term, Offers(1, "A clover", Listing(1, 1, 10)), Offers(2, "B clover", Listing(2, 2, 5)))
        };
        var vm = new MallSearchViewModel(backend, Catalog) { SearchText = "clover" };
        await vm.SearchAsync();
        Assert.True(vm.NeedsPick);
        Assert.Empty(vm.Offers);
        Assert.Contains("1 offers from 5", vm.Choices[1].Label);
        await vm.PickItemAsync(vm.Choices[1]);
        Assert.False(vm.NeedsPick);
        Assert.Equal("2", vm.Offers.Single().ItemId);
        Assert.Single(backend.Searches);
    }

    [Fact]
    public async Task A_pricegun_failure_does_not_hide_the_offers()
    {
        var backend = new FakeBackend { Respond = OneItem, PricegunFails = true };
        var vm = new MallSearchViewModel(backend, Catalog) { SearchText = "11-leaf clover" };
        await vm.SearchAsync();
        Assert.Equal(2, vm.Offers.Count);
        Assert.Null(vm.Offers[0].PricegunMedian);
        Assert.Contains("Pricegun unavailable", vm.PricegunNote);
        Assert.False(vm.HasSearchError);
    }

    [Fact]
    public async Task Pricegun_is_asked_again_on_refresh_and_never_kept_after_it_finished()
    {
        var backend = new FakeBackend { Respond = OneItem };
        var vm = new MallSearchViewModel(backend, Catalog) { SearchText = "11-leaf clover" };
        await vm.SearchAsync();
        await vm.RefreshAsync();
        Assert.Equal(2, backend.PricegunCalls);
        Assert.Equal(new[] { false, true }, backend.Searches.Select(s => s.Force));
    }

    [Fact]
    public async Task A_search_error_is_shown_in_the_tab()
    {
        var backend = new FakeBackend { Respond = _ => throw new KolibriException("The mall could not be searched just now.") };
        var vm = new MallSearchViewModel(backend, Catalog) { SearchText = "thing" };
        await vm.SearchAsync();
        Assert.True(vm.HasSearchError);
        Assert.Contains("could not be searched", vm.SearchError);
    }

    [Fact]
    public async Task A_signed_out_role_shows_the_sign_in_hint()
    {
        await using var accounts = DesktopKit.Accounts(new FakeKol());
        var vm = new MallSearchViewModel(new LiveMallSearchBackend(new DesktopLiveDataProvider(accounts), AccountRole.Mallbot), Catalog)
        {
            SearchText = "11-leaf clover"
        };
        await vm.SearchAsync();
        Assert.Equal("Sign in as Mallbot under Accounts to search the mall.", vm.SearchError);
    }

    [Fact]
    public async Task The_live_backend_searches_the_mall_through_the_library()
    {
        var kol = MallKit.Kol(25_000);
        await using var accounts = MallKit.Accounts(kol);
        var vm = new MallSearchViewModel(new LiveMallSearchBackend(new DesktopLiveDataProvider(accounts), AccountRole.Mallbot), Catalog)
        {
            SearchText = "11-leaf clover"
        };
        await vm.SearchAsync();
        var row = Assert.Single(vm.Offers);
        Assert.Equal(("10881", "56226", "Crazy Gordon", 25_000L, 1_606), (row.ItemId, row.ShopId, row.ShopName, row.UnitPrice, row.Stock!.Value));
        Assert.Equal(1, kol.Count("POST", "mall.php"));
    }

    [Fact]
    public async Task An_unknown_item_id_is_reported_without_a_request()
    {
        var backend = new FakeBackend();
        var vm = new MallSearchViewModel(backend, Catalog) { SearchText = "999999" };
        await vm.SearchAsync();
        Assert.Empty(backend.Searches);
        Assert.True(vm.HasSearchError);
    }
}
