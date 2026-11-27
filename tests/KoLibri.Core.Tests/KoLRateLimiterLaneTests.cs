using System.Net;
using KoLibri.Core.Basis;
using Xunit;

namespace KoLibri.Core.Tests;

public sealed class KoLRateLimiterLaneTests
{
    /// <summary>Virtual time: delays finish only when the test advances the clock.</summary>
    private sealed class FakeClock : IKoLLimiterClock
    {
        private readonly object _gate = new();
        private readonly List<(TimeSpan At, TaskCompletionSource Done)> _waiters = new();
        private TimeSpan _now;
        public TimeSpan Now { get { lock (_gate) return _now; } }

        public Task DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _waiters.Add((_now + delay, tcs));
            return tcs.Task;
        }

        public void Advance(TimeSpan by)
        {
            List<TaskCompletionSource> due;
            lock (_gate)
            {
                _now += by;
                due = _waiters.Where(w => w.At <= _now).Select(w => w.Done).ToList();
                _waiters.RemoveAll(w => w.At <= _now);
            }
            foreach (var d in due) d.SetResult();
        }
    }

    private static KoLRateLimiter Make(FakeClock clock, double actionSeconds = 2, double readSeconds = 1, int concurrency = 2, double perSecond = 0)
        => new(new KoLLimiterOptions
        {
            ActionInterval = TimeSpan.FromSeconds(actionSeconds), ReadInterval = TimeSpan.FromSeconds(readSeconds),
            ReadConcurrency = concurrency, MaxPerSecond = perSecond,
        }, clock);

    /// <summary>Upper bound for something that must complete without the clock moving. Checking IsCompleted after
    /// <see cref="Settle"/> alone fails on a loaded CI machine where the continuation has not run yet.</summary>
    private static readonly TimeSpan Due = TimeSpan.FromSeconds(10);

    private static async Task Settle() { for (var i = 0; i < 20; i++) await Task.Yield(); await Task.Delay(20); }

    [Theory]
    [InlineData("api.php?what=status&for=x", "Read")]
    [InlineData("/charpane.php", "Read")]
    [InlineData("inventory.php?which=1", "Read")]
    [InlineData("searchmall.php?pudnuggler=x", "Read")]
    [InlineData("adventure.php?snarfblat=1", "Action")]
    [InlineData("fight.php?action=attack", "Action")]
    [InlineData("choice.php", "Action")]
    [InlineData("inv_eat.php?which=1&whichitem=2", "Action")]
    [InlineData("inventory.php?action=outfit", "Action")]
    [InlineData("somethingnew.php", "Action")]
    public void Gets_are_classified_and_unknown_pages_are_actions(string url, string lane)
        => Assert.Equal(Enum.Parse<KoLRequestLane>(lane), KoLRequestLanes.Classify(new Uri(url, UriKind.RelativeOrAbsolute)));

    [Fact]
    public void Any_post_is_an_action()
        => Assert.Equal(KoLRequestLane.Action, KoLRequestLanes.Classify(new HttpRequestMessage(HttpMethod.Post, "https://kol.test/charpane.php")));

    [Fact]
    public async Task A_read_does_not_wait_behind_a_pending_action()
    {
        var clock = new FakeClock();
        var limiter = Make(clock);
        using var first = await limiter.AcquireAsync(KoLRequestLane.Action);   // starts at t=0
        var second = limiter.AcquireAsync(KoLRequestLane.Action);               // would start at t=2 s
        first.Dispose();
        await Settle();
        Assert.False(second.IsCompleted);
        var read = limiter.AcquireAsync(KoLRequestLane.Read);                   // t=0, free
        await read.WaitAsync(Due);
        clock.Advance(TimeSpan.FromSeconds(2));
        await second.WaitAsync(Due);
    }

    [Fact]
    public async Task Actions_never_overlap_even_after_the_spacing_has_passed()
    {
        var clock = new FakeClock();
        var limiter = Make(clock, actionSeconds: 1);
        using var running = await limiter.AcquireAsync(KoLRequestLane.Action);
        clock.Advance(TimeSpan.FromSeconds(10));
        var next = limiter.AcquireAsync(KoLRequestLane.Action);
        await Settle();
        Assert.False(next.IsCompleted);            // still in flight, lane held
        running.Dispose();
        await next.WaitAsync(Due);
    }

    [Fact]
    public async Task Reads_overlap_up_to_the_concurrency_and_are_spaced()
    {
        var clock = new FakeClock();
        var limiter = Make(clock, readSeconds: 1, concurrency: 2);
        var a = await limiter.AcquireAsync(KoLRequestLane.Read);                // t=0
        var b = limiter.AcquireAsync(KoLRequestLane.Read);                      // t=1
        var c = limiter.AcquireAsync(KoLRequestLane.Read);                      // blocked: two in flight
        await Settle();
        Assert.False(b.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        await b.WaitAsync(Due);
        Assert.False(c.IsCompleted);
        a.Dispose();
        clock.Advance(TimeSpan.FromSeconds(1));
        await c.WaitAsync(Due);
    }

    [Fact]
    public async Task The_global_cap_holds_across_both_lanes()
    {
        var clock = new FakeClock();
        var limiter = Make(clock, actionSeconds: 1, readSeconds: 0.2, concurrency: 4, perSecond: 2); // one start per 0.5 s overall
        var starts = new List<TimeSpan>();
        var tasks = new List<Task>();
        for (var i = 0; i < 6; i++)
        {
            var lane = i % 2 == 0 ? KoLRequestLane.Read : KoLRequestLane.Action;
            tasks.Add(Task.Run(async () =>
            {
                using var lease = await limiter.AcquireAsync(lane);
                lock (starts) starts.Add(clock.Now);
            }));
        }
        for (var step = 0; step < 80 && tasks.Any(t => !t.IsCompleted); step++)
        {
            await Settle();
            clock.Advance(TimeSpan.FromMilliseconds(100));
        }
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
        var ordered = starts.OrderBy(s => s).ToList();
        Assert.Equal(6, ordered.Count);
        for (var i = 1; i < ordered.Count; i++)
            Assert.True(ordered[i] - ordered[i - 1] >= TimeSpan.FromMilliseconds(499), $"gap {i}: {ordered[i] - ordered[i - 1]}");
    }

    [Fact]
    public async Task Two_roles_through_handlers_share_the_same_caps()
    {
        var clock = new FakeClock();
        var limiter = Make(clock, actionSeconds: 2);
        var inFlight = 0; var maxInFlight = 0;
        var inner = new Func<HttpRequestMessage, Task<HttpResponseMessage>>(async _ =>
        {
            var n = Interlocked.Increment(ref inFlight);
            lock (limiter) maxInFlight = Math.Max(maxInFlight, n);
            await Task.Delay(30);
            Interlocked.Decrement(ref inFlight);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var main = new HttpClient(new KoLRateLimitedHandler(new FuncHandler(inner), limiter));
        using var mall = new HttpClient(new KoLRateLimitedHandler(new FuncHandler(inner), limiter));
        var uri = new Uri("https://kol.test/adventure.php?snarfblat=1");
        var all = Task.WhenAll(main.GetAsync(uri), mall.GetAsync(uri), main.GetAsync(uri));
        for (var i = 0; i < 40 && !all.IsCompleted; i++) { await Settle(); clock.Advance(TimeSpan.FromSeconds(1)); }
        await all.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, maxInFlight);
        Assert.True(clock.Now >= TimeSpan.FromSeconds(4)); // starts at 0, 2, 4
    }

    [Fact]
    public async Task Read_handler_requests_run_next_to_a_held_action()
    {
        var clock = new FakeClock();
        var limiter = Make(clock);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<string>();
        using var http = new HttpClient(new KoLRateLimitedHandler(new FuncHandler(async r =>
        {
            lock (seen) seen.Add(r.RequestUri!.AbsolutePath);
            if (r.RequestUri.AbsolutePath == "/adventure.php") await release.Task;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }), limiter));
        var adventure = http.GetAsync("https://kol.test/adventure.php");
        await Settle();
        var charpane = http.GetAsync("https://kol.test/charpane.php");
        await charpane.WaitAsync(Due);
        Assert.False(adventure.IsCompleted);
        release.SetResult();
        await adventure.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Browser_like_preset_is_faster_than_the_default_but_keeps_the_action_interval()
    {
        var c = KoLLimiterOptions.Conservative; var b = KoLLimiterOptions.Preset("Browser-Like");
        Assert.True(b.ReadInterval < c.ReadInterval);
        Assert.True(b.ReadConcurrency > c.ReadConcurrency);
        Assert.Equal(c.ActionInterval, b.ActionInterval);
        Assert.Same(c, KoLLimiterOptions.Preset("nonsense"));
    }

    [Fact]
    public void Environment_values_are_clamped_to_hard_bounds()
    {
        var o = KoLLimiterOptions.FromEnvironment(k => k switch
        {
            "KOLIBRI_READ_INTERVAL_MS" => "1", "KOLIBRI_READ_CONCURRENCY" => "99", "KOLIBRI_MAX_REQUESTS_PER_SECOND" => "500",
            "KOLIBRI_MIN_REQUEST_INTERVAL_MS" => "10", _ => null
        });
        Assert.Equal(KoLLimiterOptions.MinimumReadInterval, o.ReadInterval);
        Assert.Equal(KoLLimiterOptions.MaximumReadConcurrency, o.ReadConcurrency);
        Assert.Equal(KoLLimiterOptions.MaximumPerSecond, o.MaxPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(1), o.ActionInterval);
    }

    private sealed class FuncHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => f(request);
    }
}
