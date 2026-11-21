using System.Text.Json;
using Xunit;
using KolArbitrage.Core.Basis;

namespace KolArbitrage.Core.Tests;

public sealed class DurableSnapshotStoreTests
{
    [Fact]
    public async Task WritesVersionedEnvelopeAndReloadsAcrossStoreInstances()
    {
        using var directory = TemporaryDirectory.Create();
        var at = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var key = SnapshotKey.ForItem("mall", "42", "main");
        var payload = new[] { new Quote("42", 123L) };

        var first = new DurableSnapshotStore(directory.Path);
        var written = await first.WriteAsync(key, payload, at);
        var path = first.GetPath(key);
        Assert.True(File.Exists(path));
        Assert.Equal(SnapshotEnvelope<object>.CurrentVersion, written.Version);
        Assert.False(written.Stale);
        Assert.Matches("^[0-9a-f]{64}$", written.Checksum);
        Assert.DoesNotContain(Directory.EnumerateFiles(directory.Path), file => file.EndsWith(".tmp", StringComparison.Ordinal));

        var second = new DurableSnapshotStore(directory.Path);
        var loaded = await second.ReadAsync<Quote[]>(SnapshotKey.ForItem("MALL", "42", "MAIN"), now: at.AddMinutes(1));
        Assert.NotNull(loaded);
        Assert.Equal("42", loaded!.ItemId);
        Assert.Equal("main", loaded.AccountId);
        Assert.Equal("mall", loaded.Source);
        Assert.Equal(123L, Assert.Single(loaded.Payload).Price);
        Assert.False(loaded.Stale);
    }

    [Fact]
    public async Task AppliesTtlAndMaxStaleWithoutChangingPersistedSnapshot()
    {
        using var directory = TemporaryDirectory.Create();
        var key = SnapshotKey.ForAccount("status", "main");
        var fetched = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var store = new DurableSnapshotStore(directory.Path, new SnapshotCachePolicy
        {
            CacheTtl = TimeSpan.FromMinutes(5),
            MaxStale = TimeSpan.FromMinutes(30)
        });
        await store.WriteAsync(key, new Status("ready"), fetched);

        var fresh = await store.ReadAsync<Status>(key, now: fetched.AddMinutes(5));
        Assert.NotNull(fresh);
        Assert.False(fresh!.Stale);

        var stale = await store.ReadAsync<Status>(key, now: fetched.AddMinutes(6));
        Assert.NotNull(stale);
        Assert.True(stale!.Stale);

        var expired = await store.ReadAsync<Status>(key, now: fetched.AddMinutes(31));
        Assert.Null(expired);

        var raw = await store.ReadRawAsync<Status>(key);
        Assert.NotNull(raw);
        Assert.False(raw!.Stale);
    }

    [Fact]
    public async Task IgnoresChecksumTamperingAndSupportsScopedInvalidation()
    {
        using var directory = TemporaryDirectory.Create();
        var store = new DurableSnapshotStore(directory.Path);
        var first = SnapshotKey.ForItem("mall", "1", "a");
        var second = SnapshotKey.ForItem("pricegun", "1", "a");
        var third = SnapshotKey.ForItem("mall", "2", "b");
        await store.WriteAsync(first, new Status("one"));
        await store.WriteAsync(second, new Status("two"));
        await store.WriteAsync(third, new Status("three"));

        var path = store.GetPath(first);
        var json = await File.ReadAllTextAsync(path);
        var document = JsonDocument.Parse(json);
        var root = document.RootElement.Clone();
        var tampered = $"{{\"version\":{root.GetProperty("version").GetInt32()},\"itemId\":\"1\",\"accountId\":\"a\",\"source\":\"mall\",\"fetchedAt\":\"{root.GetProperty("fetchedAt").GetString()}\",\"stale\":false,\"checksum\":\"{root.GetProperty("checksum").GetString()}\",\"payload\":{{\"value\":\"changed\"}}}}";
        await File.WriteAllTextAsync(path, tampered);
        Assert.Null(await store.ReadAsync<Status>(first));

        Assert.Equal(1, await store.InvalidateAsync(source: "pricegun"));
        Assert.Null(await store.ReadAsync<Status>(second));
        Assert.NotNull(await store.ReadAsync<Status>(third));

        Assert.Equal(1, await store.InvalidateItemAsync("2"));
        Assert.Null(await store.ReadAsync<Status>(third));
    }

    [Fact]
    public async Task RejectsUnsupportedEnvelopeVersionOnWrite()
    {
        using var directory = TemporaryDirectory.Create();
        var key = SnapshotKey.ForItem("mall", "42");
        var envelope = new SnapshotEnvelope<Status>(
            version: SnapshotEnvelope<Status>.CurrentVersion + 1,
            itemId: key.ItemId,
            accountId: key.AccountId,
            source: key.Source,
            fetchedAt: DateTimeOffset.UtcNow,
            stale: false,
            checksum: string.Empty,
            payload: new Status("ready"));

        await Assert.ThrowsAsync<SnapshotStoreException>(() => new DurableSnapshotStore(directory.Path).WriteAsync(envelope));
    }

    private sealed record Quote(string ItemId, long Price);
    private sealed record Status(string Value);

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;
        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kol-snapshots-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
