using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;
using Xunit;

namespace KoLibri.Core.Tests;

/// <summary>Package W-C, Core part: the once-per-day marker store. The desktop findings moved to KoLibri.Tests/Desktop.</summary>
public sealed class ConsumeDailyFindingsTests
{
    [Fact]
    public void Daily_markers_are_per_player_and_concurrent_writers_do_not_lose_each_other()
    {
        var path = Path.Combine(Path.GetTempPath(), $"daily-{Guid.NewGuid():N}.json");
        try
        {
            var first = new KoLStatusSnapshot("Alpha", 1, 0, 1, DateTimeOffset.UnixEpoch) { PlayerId = "11" };
            var second = new KoLStatusSnapshot("Beta", 1, 0, 1, DateTimeOffset.UnixEpoch) { PlayerId = "22" };
            var keyA = DailyDoneStore.AccountKey(AccountRole.Main, first);
            var keyB = DailyDoneStore.AccountKey(AccountRole.Main, second);
            Assert.NotEqual(keyA, keyB);

            var a = new DailyDoneStore(path);
            var b = new DailyDoneStore(path);                       // a second "process"
            Parallel.For(0, 20, i =>
            {
                var store = i % 2 == 0 ? a : b;
                store.MarkDone(keyA, "action-" + i);
            });
            a.MarkDone(keyA, DailyDoneStore.BaconMachine);
            for (var i = 0; i < 20; i++) Assert.True(b.IsDone(keyA, "action-" + i), "lost action-" + i);
            Assert.True(b.IsDone(keyA, DailyDoneStore.BaconMachine));
            Assert.False(b.IsDone(keyB, DailyDoneStore.BaconMachine));   // another player on the same role
        }
        finally { foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*")) File.Delete(f); }
    }
}
