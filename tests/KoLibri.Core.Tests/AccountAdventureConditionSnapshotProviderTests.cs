using Xunit;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Tests;

public sealed class AccountAdventureConditionSnapshotProviderTests
{
    [Fact]
    public async Task MapsFreshStatusAndInventoryToTypedConditions()
    {
        var client = new FakeAccountReadClient(
            new KoLStatusSnapshot("main", 7, 1000, 12, DateTimeOffset.UtcNow),
            new KoLInventorySnapshot(new[] { new KoLInventoryItem("123", "bone", 4) }, DateTimeOffset.UtcNow));
        var provider = new AccountAdventureConditionSnapshotProvider(client, new[]
        {
            new AdventureCondition { Kind = AdventureConditionKind.ItemCount, Key = "123", Target = 1 },
            new AdventureCondition { Kind = AdventureConditionKind.Stat, Key = "level", Target = 1 }
        });

        var snapshot = await provider.ReadAsync();

        Assert.Equal(4, snapshot.Items["123"]);
        Assert.Equal(12, snapshot.Stats["level"]);
        Assert.Equal(7, snapshot.TurnsRemaining);
    }

    [Fact]
    public void RejectsUnsupportedConditionKindsBeforeReading()
    {
        Assert.Throws<ArgumentException>(() => new AccountAdventureConditionSnapshotProvider(
            new FakeAccountReadClient(),
            new[] { new AdventureCondition { Kind = AdventureConditionKind.HitPoints, Target = 1 } }));
    }

    private sealed class FakeAccountReadClient(KoLStatusSnapshot? status = null, KoLInventorySnapshot? inventory = null) : IAccountReadClient
    {
        public Task<KoLStatusSnapshot> FetchStatusAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
            => Task.FromResult(status ?? new KoLStatusSnapshot("main", 0, 0, 0, DateTimeOffset.UtcNow));

        public Task<KoLInventorySnapshot> FetchInventoryAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
            => Task.FromResult(inventory ?? new KoLInventorySnapshot(Array.Empty<KoLInventoryItem>(), DateTimeOffset.UtcNow));
    }
}
