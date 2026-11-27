using KoLibri.Desktop;
using Xunit;

namespace KoLibri.Tests.Desktop;

public sealed class BrowserModePolicyTests
{
    [Theory]
    [InlineData(true, null, null, BrowserMode.Embedded)]
    [InlineData(true, "0", null, BrowserMode.SystemBrowser)]
    [InlineData(true, "1", null, BrowserMode.Embedded)]
    [InlineData(true, "1", "1", BrowserMode.SystemBrowser)]
    [InlineData(false, null, null, BrowserMode.Embedded)]
    [InlineData(false, "0", null, BrowserMode.SystemBrowser)]
    [InlineData(false, null, "1", BrowserMode.SystemBrowser)]
    public void Decide(bool linux, string? embed, string? no, BrowserMode expected)
        => Assert.Equal(expected, BrowserModePolicy.Decide(linux, embed, no));

    [Fact]
    public async Task Hanging_work_times_out_without_waiting_for_it()
    {
        var never = new TaskCompletionSource<int>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (timedOut, _) = await BrowserTimeouts.RunAsync(_ => never.Task, TimeSpan.FromMilliseconds(150));
        Assert.True(timedOut);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Fast_work_returns_its_value()
    {
        var (timedOut, v) = await BrowserTimeouts.RunAsync(_ => Task.FromResult(7), TimeSpan.FromSeconds(5));
        Assert.False(timedOut); Assert.Equal(7, v);
    }

    [Fact]
    public async Task Watchdog_fires_when_never_seen_and_not_when_seen()
    {
        Assert.True(await BrowserTimeouts.NeverSeenAsync(() => false, TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(20)));
        var t0 = DateTime.UtcNow;
        Assert.False(await BrowserTimeouts.NeverSeenAsync(() => DateTime.UtcNow - t0 > TimeSpan.FromMilliseconds(60), TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(20)));
    }
}
