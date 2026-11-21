using KolArbitrage.Core.Daily;
using Xunit;

namespace KolArbitrage.Core.Tests;

public sealed class DailyDoneStoreTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Mark_is_per_account_and_resets_after_kol_rollover()
    {
        var path = Path.Combine(Path.GetTempPath(), $"daily-done-{Guid.NewGuid():N}.json");
        try
        {
            var clock = new FixedClock(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));
            var store = new DailyDoneStore(path, clock);
            Assert.False(store.IsDone("Main", DailyDoneStore.DayShortener));

            store.MarkDone("Main", DailyDoneStore.DayShortener);
            Assert.True(store.IsDone("Main", DailyDoneStore.DayShortener));
            Assert.False(store.IsDone("Mallbot", DailyDoneStore.DayShortener));

            // Same KoL day later in the evening: still done.
            clock.Now = new DateTimeOffset(2026, 11, 22, 4, 0, 0, TimeSpan.Zero);
            Assert.True(store.IsDone("Main", DailyDoneStore.DayShortener));

            // Past rollover (03:30 Eastern = 08:30 UTC): a new KoL day.
            clock.Now = new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
            Assert.False(store.IsDone("Main", DailyDoneStore.DayShortener));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
