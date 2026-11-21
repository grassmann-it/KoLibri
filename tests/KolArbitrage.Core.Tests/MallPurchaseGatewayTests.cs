using Xunit;
using KolArbitrage.Core.Markt;

namespace KolArbitrage.Core.Tests;

public sealed class MallPurchaseGatewayTests
{
    [Fact]
    public async Task PreviewBindsSellerStorePriceQuantityAndDailyLimitWithoutMutation()
    {
        var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var fixture = NewFixture(now, meat: 50_000, dailySpent: 1_000, dailyLimit: 100_000);
        var gateway = new MallPurchaseGateway(
            fixture,
            new MallPurchasePolicy
            {
                DryRun = false,
                RequireApproval = true,
                MaxDailySpend = 100_000,
                MaxMeatPerPurchase = 20_000,
                MaxQuantityPerPurchase = 5
            },
            new SerialAccountLane("mallbot"),
            new FixedTimeProvider(now));

        var preview = await gateway.PreviewAsync(
            new MallPurchaseRequest("mallbot", "seller-1", "store-1", "item-1", 100, 2, 100_000));

        Assert.True(preview.IsReady);
        Assert.Equal("seller-1", preview.Offer.SellerId);
        Assert.Equal("store-1", preview.Offer.StoreId);
        Assert.Equal("item-1", preview.Offer.ItemId);
        Assert.Equal(100, preview.Offer.UnitPrice);
        Assert.Equal(2, preview.Request.Quantity);
        Assert.Equal(100_000, preview.Account.DailyLimit);
        Assert.Equal(1, fixture.RefreshCount);
        Assert.Equal(0, fixture.CommitCount);
    }

    [Fact]
    public async Task RefreshAbortsWhenPriceChangesAndCommitNeverRuns()
    {
        var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var fixture = NewFixture(now, meat: 50_000, dailySpent: 1_000, dailyLimit: 100_000);
        var gateway = new MallPurchaseGateway(
            fixture,
            new MallPurchasePolicy { DryRun = false, RequireApproval = false },
            new SerialAccountLane("mallbot"),
            new FixedTimeProvider(now));
        var preview = await gateway.PreviewAsync(
            new MallPurchaseRequest("mallbot", "seller-1", "store-1", "item-1", 100, 2, 100_000));

        fixture.EnqueueRefresh(fixture.State with
        {
            Offer = fixture.State.Offer with { UnitPrice = 125 }
        });

        var refreshed = await gateway.RefreshAsync(preview);

        Assert.False(refreshed.IsReady);
        Assert.Contains(MallPurchaseAbortReason.PriceChanged, refreshed.AbortReasons);
        var result = await gateway.CommitAsync(preview);
        Assert.False(result.Committed);
        Assert.Equal(0, fixture.CommitCount);
    }

    [Fact]
    public async Task CommitFixtureMutatesMeatInventoryAndVerifiesExactDeltas()
    {
        var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var fixture = NewFixture(now, meat: 50_000, dailySpent: 1_000, dailyLimit: 100_000);
        var gateway = new MallPurchaseGateway(
            fixture,
            new MallPurchasePolicy { DryRun = false, RequireApproval = true },
            new SerialAccountLane("mallbot"),
            new FixedTimeProvider(now));
        var preview = await gateway.PreviewAsync(
            new MallPurchaseRequest("mallbot", "seller-1", "store-1", "item-1", 100, 2, 100_000));

        var result = await gateway.CommitAsync(preview, "approval-token");

        Assert.True(result.Committed);
        Assert.True(result.Accepted);
        Assert.True(result.Verified);
        Assert.Equal(1, fixture.CommitCount);
        Assert.Equal(49_800, fixture.State.Account.Meat);
        Assert.Equal(1_200, fixture.State.Account.DailySpent);
        Assert.Equal(2, fixture.State.Account.InventoryQuantity("item-1"));
        Assert.Equal(3, fixture.State.Offer.Quantity);
    }

    [Fact]
    public async Task MissingApprovalAndDailyLimitProduceStructuredAbortReasons()
    {
        var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var fixture = NewFixture(now, meat: 50_000, dailySpent: 99_950, dailyLimit: 100_000);
        var gateway = new MallPurchaseGateway(
            fixture,
            new MallPurchasePolicy { DryRun = false, RequireApproval = true },
            new SerialAccountLane("mallbot"),
            new FixedTimeProvider(now));
        var preview = await gateway.PreviewAsync(
            new MallPurchaseRequest("mallbot", "seller-1", "store-1", "item-1", 100, 2, 100_000));

        Assert.Contains(MallPurchaseAbortReason.DailyLimitExceeded, preview.AbortReasons);
        var result = await gateway.CommitAsync(preview);

        Assert.False(result.Committed);
        Assert.Contains(MallPurchaseAbortReason.ApprovalRequired, result.AbortReasons);
        Assert.Equal(0, fixture.CommitCount);
    }

    [Fact]
    public async Task SerialLaneKeepsConcurrentCommitsFromRacing()
    {
        var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var fixture = NewFixture(now, meat: 50_000, dailySpent: 0, dailyLimit: 100_000);
        var lane = new SerialAccountLane("mallbot");
        var gateway = new MallPurchaseGateway(
            fixture,
            new MallPurchasePolicy { DryRun = false, RequireApproval = false },
            lane,
            new FixedTimeProvider(now));
        var first = await gateway.PreviewAsync(
            new MallPurchaseRequest("mallbot", "seller-1", "store-1", "item-1", 100, 2, 100_000));
        var second = await gateway.PreviewAsync(
            new MallPurchaseRequest("mallbot", "seller-1", "store-1", "item-1", 100, 2, 100_000));

        var results = await Task.WhenAll(gateway.CommitAsync(first), gateway.CommitAsync(second));

        Assert.Single(results, result => result.Verified);
        Assert.Single(results, result => result.Aborted);
        Assert.Equal(1, fixture.CommitCount);
        Assert.Equal(49_800, fixture.State.Account.Meat);
    }

    private static FixtureMallPurchaseTransport NewFixture(
        DateTimeOffset now,
        long meat,
        long dailySpent,
        long dailyLimit)
        => new(
            new MallPurchaseOffer("seller-1", "store-1", "item-1", 100, 5, now),
            new MallPurchaseAccountState(
                "mallbot", meat, dailySpent, dailyLimit,
                new Dictionary<string, int>(), now));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
