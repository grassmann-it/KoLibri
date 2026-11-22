using System.Diagnostics;
using Xunit;
using KoLibri.Core.Basis;

namespace KoLibri.Core.Tests;

public sealed class KoLRateLimiterTests
{
    [Theory]
    [InlineData(null, 2000)]
    [InlineData("", 2000)]
    [InlineData("abc", 2000)]
    [InlineData("100", 1000)]
    [InlineData("1500", 1500)]
    [InlineData("999999", 60000)]
    public void Interval_is_clamped_between_one_second_and_one_minute(string? value, int expectedMs)
        => Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), KoLRateLimiter.ResolveInterval(value));

    [Fact]
    public async Task Requests_are_spaced_by_the_interval()
    {
        var limiter = new KoLRateLimiter(TimeSpan.FromMilliseconds(200));
        var sw = Stopwatch.StartNew();
        await limiter.WaitAsync();
        await limiter.WaitAsync();
        await limiter.WaitAsync();
        Assert.True(sw.ElapsedMilliseconds >= 380, $"elapsed {sw.ElapsedMilliseconds} ms");
    }
}
