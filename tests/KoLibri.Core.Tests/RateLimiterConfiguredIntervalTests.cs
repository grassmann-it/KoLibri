using KoLibri.Core.Basis;
using Xunit;

namespace KoLibri.Core.Tests;

/// <summary>The Core limiter's configured interval (moved here from the desktop settings tests in 0.5).</summary>
public sealed class RateLimiterConfiguredIntervalTests
{
    [Fact]
    public async Task LimiterPicksUpConfiguredIntervalAndKeepsFloor()
    {
        var limiter = new KoLRateLimiter(TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(1), limiter.ApplyConfigured(TimeSpan.FromMilliseconds(5), null));
        Assert.Equal(TimeSpan.FromSeconds(3), limiter.ApplyConfigured(TimeSpan.FromSeconds(3), "garbage"));
        Assert.Equal(TimeSpan.FromSeconds(4), limiter.ApplyConfigured(TimeSpan.FromSeconds(3), "4000"));
        // Small interval via an explicit floor-free instance still spaces requests.
        var fast = new KoLRateLimiter(TimeSpan.FromMilliseconds(60));
        await fast.WaitAsync();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await fast.WaitAsync();
        Assert.True(sw.ElapsedMilliseconds >= 40);
    }

}
