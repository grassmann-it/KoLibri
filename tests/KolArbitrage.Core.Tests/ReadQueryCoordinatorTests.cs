using Xunit;
using KolArbitrage.Core.Basis;
using KolArbitrage.Core.Markt;
using KolArbitrage.Core.Spiel;

namespace KolArbitrage.Core.Tests;

public sealed class ReadQueryCoordinatorTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task BatchComposesAccountAndMarketSnapshotsWithOneRequestId()
    {
        var clock = new TestClock(At);
        var account = new FakeAccountClient(At);
        var mall = new FakeMallClient(At);
        var pricegun = new FakePricegunClient(At);
        var shop = new FakeShopClient(At);
        using var coordinator = new ReadQueryCoordinator(
            new ReadAccountSource("main", account),
            clock: clock,
            mall: mall,
            pricegun: pricegun,
            shop: shop);

        var result = await coordinator.ObserveBatchAsync(new ArbitrageObservationRequest(
            new[] { "main" }, new[] { "42" }, new ReadQueryOptions { RequestId = "query-42" }));

        var accountRead = Assert.Single(result.Accounts);
        var item = Assert.Single(result.Items);
        Assert.Equal("query-42", result.RequestId);
        Assert.Equal(result.RequestId, accountRead.RequestId);
        Assert.Equal(result.RequestId, accountRead.Status.RequestId);
        Assert.Equal(result.RequestId, item.Mall.RequestId);
        Assert.Equal("player", accountRead.Status.Value!.PlayerName);
        Assert.Equal(3, accountRead.Inventory.Value!.Items.Single().Quantity);
        Assert.Equal(900, item.Mall.Value!.Listings.Single().Price);
        Assert.Equal(850, item.Pricegun.Value!.Sales.Single().UnitPrice);
        Assert.Single(accountRead.OwnShop.Value!);
        Assert.Single(accountRead.ShopSales.Value!.Sales);
        Assert.True(result.IsUsable);
        Assert.Equal(1, account.StatusCalls);
        Assert.Equal(1, account.InventoryCalls);
        Assert.Equal(1, mall.SearchCalls);
        Assert.Equal(1, pricegun.FetchCalls);
    }

    [Fact]
    public async Task StaleGateMarksValuesAndCanRejectThemWithoutThrowing()
    {
        var clock = new TestClock(At);
        var old = At.AddMinutes(-10);
        var account = new FakeAccountClient(old);
        var policy = new ReadQueryPolicy
        {
            Status = new SnapshotCachePolicy { CacheTtl = TimeSpan.FromMinutes(1), MaxStale = TimeSpan.FromHours(1) },
            Inventory = new SnapshotCachePolicy { CacheTtl = TimeSpan.FromMinutes(1), MaxStale = TimeSpan.FromHours(1) },
            Mall = new SnapshotCachePolicy { CacheTtl = TimeSpan.FromMinutes(1), MaxStale = TimeSpan.FromHours(1) },
            Pricegun = new SnapshotCachePolicy { CacheTtl = TimeSpan.FromMinutes(1), MaxStale = TimeSpan.FromHours(1) },
            OwnShop = new SnapshotCachePolicy { CacheTtl = TimeSpan.FromMinutes(1), MaxStale = TimeSpan.FromHours(1) },
            ShopSales = new SnapshotCachePolicy { CacheTtl = TimeSpan.FromMinutes(1), MaxStale = TimeSpan.FromHours(1) }
        };
        using var coordinator = new ReadQueryCoordinator(
            new ReadAccountSource("main", account),
            policy: policy,
            clock: clock,
            mall: new FakeMallClient(old),
            pricegun: new FakePricegunClient(old));

        var result = await coordinator.ObserveBatchAsync(
            new ArbitrageObservationRequest(new[] { "main" }, new[] { "42" }, new ReadQueryOptions
            {
                RequestId = "stale-check",
                AllowStale = false,
                RequireFresh = true
            }));

        Assert.Equal("stale-check", result.RequestId);
        Assert.Equal(ReadFreshness.Stale, result.Accounts.Single().Status.Freshness);
        Assert.False(result.Accounts.Single().Status.IsUsable);
        Assert.False(result.IsUsable);
        Assert.Contains(result.Diagnostics, x => x.Code == "stale_rejected");
    }

    [Fact]
    public async Task LocalCacheIsSharedAndInvalidationForcesARead()
    {
        var account = new FakeAccountClient(At);
        var mall = new FakeMallClient(At);
        using var coordinator = new ReadQueryCoordinator(
            new ReadAccountSource("main", account),
            clock: new TestClock(At),
            mall: mall);

        await coordinator.ObserveBatchAsync(new ArbitrageObservationRequest(new[] { "main" }, new[] { "42" }));
        await coordinator.ObserveBatchAsync(new ArbitrageObservationRequest(new[] { "main" }, new[] { "42" }));
        Assert.Equal(1, account.StatusCalls);
        Assert.Equal(1, mall.SearchCalls);

        coordinator.Invalidate("main", "42");
        await coordinator.ObserveBatchAsync(new ArbitrageObservationRequest(new[] { "main" }, new[] { "42" }));
        Assert.Equal(2, account.StatusCalls);
        Assert.Equal(2, mall.SearchCalls);
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now;
        public TestClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class FakeAccountClient : IAccountReadClient
    {
        private readonly DateTimeOffset _fetchedAt;
        public FakeAccountClient(DateTimeOffset fetchedAt) => _fetchedAt = fetchedAt;
        public int StatusCalls { get; private set; }
        public int InventoryCalls { get; private set; }

        public Task<KoLStatusSnapshot> FetchStatusAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            StatusCalls++;
            return Task.FromResult(new KoLStatusSnapshot("player", 20, 1_000, 5, _fetchedAt));
        }

        public Task<KoLInventorySnapshot> FetchInventoryAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            InventoryCalls++;
            return Task.FromResult(new KoLInventorySnapshot(
                new[] { new KoLInventoryItem("42", "Test item", 3) }, _fetchedAt));
        }
    }

    private sealed class FakeMallClient : IMallReadClient
    {
        private readonly DateTimeOffset _fetchedAt;
        private static readonly Uri Source = new("https://fixture.invalid/mall");
        public FakeMallClient(DateTimeOffset fetchedAt) => _fetchedAt = fetchedAt;
        public int SearchCalls { get; private set; }

        public Task<MallSnapshot> SearchAsync(string itemId, bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            SearchCalls++;
            return Task.FromResult(new MallSnapshot(itemId, new[]
            {
                new MallListing(itemId, "store", "Store", 900, 2, null, _fetchedAt, Source)
            }, _fetchedAt, Source));
        }

        public Task<IReadOnlyList<OwnShopListing>> FetchOwnShopAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OwnShopListing>>(new[]
            {
                new OwnShopListing("42", 1_100, 1, null, _fetchedAt, "Test item")
            });
    }

    private sealed class FakePricegunClient : IPricegunReadClient
    {
        private readonly DateTimeOffset _fetchedAt;
        private static readonly Uri Source = new("https://fixture.invalid/pricegun");
        public FakePricegunClient(DateTimeOffset fetchedAt) => _fetchedAt = fetchedAt;
        public int FetchCalls { get; private set; }

        public Task<PricegunSnapshot> FetchAsync(string itemId, bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            FetchCalls++;
            return Task.FromResult(new PricegunSnapshot(itemId, "Test item",
                new[] { new PricegunSale(_fetchedAt, 850, 1) }, _fetchedAt, Source));
        }
    }

    private sealed class FakeShopClient : IShopSalesReadClient
    {
        private readonly DateTimeOffset _fetchedAt;
        private static readonly Uri Source = new("https://fixture.invalid/shop");
        public FakeShopClient(DateTimeOffset fetchedAt) => _fetchedAt = fetchedAt;

        public Task<ShopSalesSnapshot> FetchSalesAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
            => Task.FromResult(new ShopSalesSnapshot(new[]
            {
                new ShopSale("buyer", "Test item", 1, 850, 850, _fetchedAt, _fetchedAt)
            }, _fetchedAt, Source));
    }
}
