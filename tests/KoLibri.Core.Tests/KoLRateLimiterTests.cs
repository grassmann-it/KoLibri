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

    /// <summary>
    /// Virtual time that moves only when the limiter waits: a delay finishes at once and sets the clock to its end.
    /// The recorded start of a request is then exactly the start the limiter granted, so the spacing checks below
    /// are exact and do not depend on how quickly a loaded CI machine schedules timers and continuations.
    /// </summary>
    private sealed class AutoAdvanceClock : IKoLLimiterClock
    {
        private readonly object _gate = new();
        private TimeSpan _now = TimeSpan.FromHours(1);
        public TimeSpan Now { get { lock (_gate) return _now; } }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate) _now += delay;
            return Task.CompletedTask;
        }
    }

    private sealed class SendRecorder(IKoLLimiterClock clock, Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly object _gate = new();
        public List<TimeSpan> Starts { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (_gate) Starts.Add(clock.Now);
            return Task.FromResult(respond(request));
        }
    }

    private static void AssertSpaced(IReadOnlyList<TimeSpan> starts, int count, int intervalMs)
    {
        Assert.Equal(count, starts.Count);
        for (var i = 1; i < starts.Count; i++)
            Assert.True(starts[i] - starts[i - 1] >= TimeSpan.FromMilliseconds(intervalMs),
                $"gap {i}: {(starts[i] - starts[i - 1]).TotalMilliseconds} ms, starts {string.Join(",", starts.Select(x => x.TotalMilliseconds))}");
    }

    [Fact]
    public async Task Two_roles_sharing_the_limiter_keep_the_spacing()
    {
        var clock = new AutoAdvanceClock();
        var limiter = new KoLRateLimiter(TimeSpan.FromMilliseconds(150), clock);
        var recorder = new SendRecorder(clock, _ => new HttpResponseMessage(HttpStatusCode.OK));
        using var main = new HttpClient(new KoLRateLimitedHandler(recorder, limiter), disposeHandler: false);
        using var mallbot = new HttpClient(new KoLRateLimitedHandler(recorder, limiter), disposeHandler: false);
        var uri = new Uri("https://kol.test/x");
        await Task.WhenAll(main.GetAsync(uri), mallbot.GetAsync(uri), main.GetAsync(uri), mallbot.GetAsync(uri));
        AssertSpaced(recorder.Starts, 4, 150);
    }

    [Fact]
    public async Task Manually_followed_redirect_hops_each_count_as_a_request()
    {
        var clock = new AutoAdvanceClock();
        var limiter = new KoLRateLimiter(TimeSpan.FromMilliseconds(150), clock);
        var recorder = new SendRecorder(clock, r => r.RequestUri!.AbsolutePath == "/start"
            ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("/fight.php", UriKind.Relative) } }
            : new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(new KoLRateLimitedHandler(recorder, limiter), disposeHandler: false) { BaseAddress = new Uri("https://kol.test/") };
        using var first = await client.GetAsync("start");
        using var second = await client.GetAsync(first.Headers.Location);
        AssertSpaced(recorder.Starts, 2, 150);
    }

    private const string PricegunHtml = "<html><head><title>fixture - Pricegun</title></head><body><ol><li><span>8/27/26, 3:02 AM</span>2 @ 40,000,000</li></ol></body></html>";

    [Fact]
    public async Task Pricegun_instances_share_one_limiter_and_cache_hits_send_nothing()
    {
        var clock = new AutoAdvanceClock();
        var limiter = new KoLRateLimiter(TimeSpan.FromMilliseconds(150), clock);
        var recorder = new SendRecorder(clock, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(PricegunHtml) });
        var policy = new PricegunReadPolicy { MinimumRequestInterval = TimeSpan.Zero };
        using var http = new HttpClient(recorder, disposeHandler: false);
        var a = new PricegunReadClient(http, policy: policy, limiter: limiter);
        var b = new PricegunReadClient(http, policy: policy, limiter: limiter);
        await a.FetchAsync("1");
        await b.FetchAsync("2");
        await a.FetchAsync("3");
        AssertSpaced(recorder.Starts, 3, 150);
        await a.FetchAsync("1");
        await a.FetchAsync("3");
        Assert.Equal(3, recorder.Starts.Count);
    }

    [Fact]
    public void Pricegun_default_limiter_is_shared_and_at_least_one_second()
    {
        Assert.Same(PricegunRateLimit.Shared, PricegunRateLimit.Shared);
        Assert.True(PricegunRateLimit.Shared.Interval >= TimeSpan.FromSeconds(1));
    }
}
