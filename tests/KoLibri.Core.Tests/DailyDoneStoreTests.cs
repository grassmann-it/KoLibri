using System.Globalization;
using Xunit;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Tests;

public sealed class DailyDoneStoreTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Theory]
    [InlineData("2026-11-28T03:29:59Z", "2026-11-27")]
    [InlineData("2026-11-28T03:30:00Z", "2026-11-28")]
    [InlineData("2026-07-01T03:29:59Z", "2026-06-30")]
    [InlineData("2026-07-01T03:30:00Z", "2026-07-01")]
    [InlineData("2026-11-27T20:30:00-07:00", "2026-11-28")]
    public void KolDay_changes_at_0330_utc_year_round(string instant, string expected)
        => Assert.Equal(expected, DailyDoneStore.KolDay(DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture)));

    [Fact]
    public void Mark_is_per_account_and_resets_after_kol_rollover()
    {
        var directory = Directory.CreateTempSubdirectory("daily-done-");
        var path = Path.Combine(directory.FullName, "daily-done.json");
        try
        {
            var clock = new FixedClock(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));
            var store = new DailyDoneStore(path, clock);
            Assert.False(store.IsDone("Main", DailyDoneStore.DayShortener));

            store.MarkDone("Main", DailyDoneStore.DayShortener);
            Assert.True(store.IsDone("Main", DailyDoneStore.DayShortener));
            Assert.False(store.IsDone("Mallbot", DailyDoneStore.DayShortener));

            // One second before rollover, the marker still applies.
            clock.Now = new DateTimeOffset(2026, 11, 22, 3, 29, 59, TimeSpan.Zero);
            Assert.True(store.IsDone("Main", DailyDoneStore.DayShortener));

            // At 03:30 UTC the new KoL day starts.
            clock.Now = new DateTimeOffset(2026, 11, 22, 3, 30, 0, TimeSpan.Zero);
            Assert.False(store.IsDone("Main", DailyDoneStore.DayShortener));
        }
        finally { directory.Delete(recursive: true); }
    }
}
