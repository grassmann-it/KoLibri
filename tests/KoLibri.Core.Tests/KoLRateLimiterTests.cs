using System.Diagnostics;
using Xunit;
using System.Net;
using KoLibri.Core.Basis;
using KoLibri.Core.Markt;

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

    private sealed class SendRecorder(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly object _gate = new();
        public List<long> StartsMs { get; } = new();
        private readonly Stopwatch _sw = Stopwatch.StartNew();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (_gate) StartsMs.Add(_sw.ElapsedMilliseconds);
            return Task.FromResult(respond(request));
        }
    }

    // Timers may fire early (Windows ~15 ms granularity) and the stopwatch is read after the handler is scheduled,
    // so allow a third of the interval. Unspaced requests would start within a few ms and still fail by a wide margin.
    private static void AssertSpaced(IReadOnlyList<long> starts, int count, int intervalMs)
    {
        var tolerance = intervalMs / 3;
        Assert.Equal(count, starts.Count);
        for (var i = 1; i < starts.Count; i++)
            Assert.True(starts[i] - starts[i - 1] >= intervalMs - tolerance, $"gap {i}: {starts[i] - starts[i - 1]} ms, starts {string.Join(",", starts)}");
    }

    [Fact]
    public async Task Two_roles_sharing_the_limiter_keep_the_spacing()
    {
        var limiter = new KoLRateLimiter(TimeSpan.FromMilliseconds(150));
        var recorder = new SendRecorder(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var main = new HttpClient(new KoLRateLimitedHandler(recorder, limiter), disposeHandler: false);
        using var mallbot = new HttpClient(new KoLRateLimitedHandler(recorder, limiter), disposeHandler: false);
        var uri = new Uri("https://kol.test/x");
        await Task.WhenAll(main.GetAsync(uri), mallbot.GetAsync(uri), main.GetAsync(uri), mallbot.GetAsync(uri));
        AssertSpaced(recorder.StartsMs, 4, 150);
    }

    [Fact]
    public async Task Manually_followed_redirect_hops_each_count_as_a_request()
    {
        var limiter = new KoLRateLimiter(TimeSpan.FromMilliseconds(150));
        var recorder = new SendRecorder(r => r.RequestUri!.AbsolutePath == "/start"
            ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("/fight.php", UriKind.Relative) } }
            : new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(new KoLRateLimitedHandler(recorder, limiter), disposeHandler: false) { BaseAddress = new Uri("https://kol.test/") };
        using var first = await client.GetAsync("start");
        using var second = await client.GetAsync(first.Headers.Location);
        AssertSpaced(recorder.StartsMs, 2, 150);
    }

    private const string PricegunHtml = "<html><head><title>fixture - Pricegun</title></head><body><ol><li><span>8/27/26, 3:02 AM</span>2 @ 40,000,000</li></ol></body></html>";

    [Fact]
    public async Task Pricegun_instances_share_one_limiter_and_cache_hits_send_nothing()
    {
        var limiter = new KoLRateLimiter(TimeSpan.FromMilliseconds(150));
        var recorder = new SendRecorder(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(PricegunHtml) });
        var policy = new PricegunReadPolicy { MinimumRequestInterval = TimeSpan.Zero };
        using var http = new HttpClient(recorder, disposeHandler: false);
        var a = new PricegunReadClient(http, policy: policy, limiter: limiter);
        var b = new PricegunReadClient(http, policy: policy, limiter: limiter);
        await a.FetchAsync("1");
        await b.FetchAsync("2");
        await a.FetchAsync("3");
        AssertSpaced(recorder.StartsMs, 3, 150);
        await a.FetchAsync("1");
        await a.FetchAsync("3");
        Assert.Equal(3, recorder.StartsMs.Count);
    }

    [Fact]
    public void Pricegun_default_limiter_is_shared_and_at_least_one_second()
    {
        Assert.Same(PricegunRateLimit.Shared, PricegunRateLimit.Shared);
        Assert.True(PricegunRateLimit.Shared.Interval >= TimeSpan.FromSeconds(1));
    }
}
