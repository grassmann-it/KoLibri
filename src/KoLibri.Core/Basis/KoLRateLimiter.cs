using System.Diagnostics;
using System.Globalization;

namespace KoLibri.Core.Basis;

/// <summary>Which lane a request travels in. Actions are serialized; reads may overlap a little.</summary>
public enum KoLRequestLane
{
    /// <summary>Mutating or turn-consuming request (adventure, fight, choice, consume, buy, send): one at a time.</summary>
    Action = 0,
    /// <summary>Idempotent display read (api.php status, charpane, inventory, mall search): small concurrency.</summary>
    Read = 1
}

/// <summary>Decides the lane of a request. Anything not known to be a plain read is an action (the safe default).</summary>
public static class KoLRequestLanes
{
    private static readonly HashSet<string> ReadPages = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.php", "charpane.php", "inventory.php", "charsheet.php", "questlog.php", "desc_item.php", "desc_effect.php",
        "desc_skill.php", "mall.php", "searchmall.php", "storage.php", "campground.php", "skills.php", "showplayer.php",
        "familiar.php", "account.php", "newchatmessages.php",
    };

    public static KoLRequestLane Classify(HttpRequestMessage request)
    {
        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head) return KoLRequestLane.Action;
        return Classify(request.RequestUri);
    }

    public static KoLRequestLane Classify(Uri? uri)
    {
        if (uri is null) return KoLRequestLane.Action;
        string path, query;
        if (uri.IsAbsoluteUri) { path = uri.AbsolutePath; query = uri.Query; }
        else
        {
            var text = uri.OriginalString;
            var q = text.IndexOf('?');
            path = q < 0 ? text : text[..q];
            query = q < 0 ? "" : text[q..];
        }
        var page = path[(path.LastIndexOf('/') + 1)..];
        if (!ReadPages.Contains(page)) return KoLRequestLane.Action;
        // A page that is normally a read but is asked to do something is an action.
        if (query.Contains("action=", StringComparison.OrdinalIgnoreCase)
            && !page.Equals("api.php", StringComparison.OrdinalIgnoreCase)) return KoLRequestLane.Action;
        return KoLRequestLane.Read;
    }
}

/// <summary>Limits of the lanes. Defaults are conservative; <see cref="BrowserLike"/> is the documented faster preset.</summary>
public sealed record KoLLimiterOptions
{
    public const string PresetEnvironmentVariable = "KOLIBRI_LIMITER_PRESET";
    public const string ReadIntervalEnvironmentVariable = "KOLIBRI_READ_INTERVAL_MS";
    public const string ReadConcurrencyEnvironmentVariable = "KOLIBRI_READ_CONCURRENCY";
    public const string MaxPerSecondEnvironmentVariable = "KOLIBRI_MAX_REQUESTS_PER_SECOND";

    public static readonly TimeSpan MinimumReadInterval = TimeSpan.FromMilliseconds(200);
    public const int MaximumReadConcurrency = 4;
    public const int MaximumPerSecond = 5;

    /// <summary>Start-to-start spacing of the action lane (never below 1 s once resolved).</summary>
    public TimeSpan ActionInterval { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Start-to-start spacing of the read lane.</summary>
    public TimeSpan ReadInterval { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Reads allowed in flight at once.</summary>
    public int ReadConcurrency { get; init; } = 2;
    /// <summary>Safety net over both lanes: most request starts per second (1 to 5 once configured); 0 means no global cap and is only for tests and fakes.</summary>
    public double MaxPerSecond { get; init; } = 2;

    public static KoLLimiterOptions Conservative { get; } = new();

    /// <summary>Roughly what a browser does: the main frame plus the sidebar side by side. Actions keep their spacing.</summary>
    public static KoLLimiterOptions BrowserLike { get; } = new()
    {
        ReadInterval = TimeSpan.FromMilliseconds(400), ReadConcurrency = 3, MaxPerSecond = 4,
    };

    /// <summary>Preset by name ("conservative" or "browser-like"); unknown or empty gives the conservative one.</summary>
    public static KoLLimiterOptions Preset(string? name)
        => string.Equals(name?.Trim(), "browser-like", StringComparison.OrdinalIgnoreCase) ? BrowserLike : Conservative;

    /// <summary>Preset, then individual environment overrides, clamped to the hard bounds.</summary>
    public static KoLLimiterOptions FromEnvironment(Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var o = Preset(env(PresetEnvironmentVariable)) with
        {
            ActionInterval = KoLRateLimiter.ResolveInterval(env(KoLRateLimiter.IntervalEnvironmentVariable))
        };
        if (Num(env(ReadIntervalEnvironmentVariable)) is { } ms) o = o with { ReadInterval = TimeSpan.FromMilliseconds(ms) };
        if (Num(env(ReadConcurrencyEnvironmentVariable)) is { } c) o = o with { ReadConcurrency = (int)c };
        if (Num(env(MaxPerSecondEnvironmentVariable)) is { } m) o = o with { MaxPerSecond = Math.Max(m, 1) }; // the environment cannot switch the cap off
        return o.Clamped();
    }

    public KoLLimiterOptions Clamped() => this with
    {
        ReadInterval = ReadInterval < MinimumReadInterval ? MinimumReadInterval
            : ReadInterval > KoLRateLimiter.MaximumInterval ? KoLRateLimiter.MaximumInterval : ReadInterval,
        ReadConcurrency = Math.Clamp(ReadConcurrency, 1, MaximumReadConcurrency),
        MaxPerSecond = double.IsNaN(MaxPerSecond) ? 2 : MaxPerSecond == 0 ? 0 : Math.Clamp(MaxPerSecond, 1, MaximumPerSecond),
    };

    private static double? Num(string? text)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && !double.IsNaN(v) && !double.IsInfinity(v) ? v : null;
}

/// <summary>Time source of the limiter; replaced by a fake in tests.</summary>
public interface IKoLLimiterClock
{
    TimeSpan Now { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class StopwatchLimiterClock : IKoLLimiterClock
{
    public static readonly StopwatchLimiterClock Instance = new();
    public TimeSpan Now => TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

/// <summary>
/// Spaces out every request KoLibri sends to the game: one limiter for the whole process, so the Main and
/// Mallbot sessions together never exceed it. Two lanes share it:
/// <list type="bullet">
/// <item><b>Action</b>: mutating or turn-consuming requests. Strictly one at a time (the lane is held until the
/// response has arrived), started at least <see cref="Interval"/> apart. Default 2 s, never below 1 s.</item>
/// <item><b>Read</b>: idempotent display reads. A few in flight at once with their own, shorter spacing, so a sidebar or
/// status refresh runs next to an adventure step instead of queueing behind it.</item>
/// </list>
/// A global cap on request starts per second covers both lanes. A read never waits for the action lane's spacing.
/// </summary>
public sealed class KoLRateLimiter
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaximumInterval = TimeSpan.FromMinutes(1);

    /// <summary>The limiter every KoL client of this process shares.</summary>
    public static KoLRateLimiter Shared { get; } = new(KoLLimiterOptions.FromEnvironment());

    public const string IntervalEnvironmentVariable = "KOLIBRI_MIN_REQUEST_INTERVAL_MS";

    private readonly IKoLLimiterClock _clock;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private readonly SemaphoreSlim _readGate = new(KoLLimiterOptions.MaximumReadConcurrency, KoLLimiterOptions.MaximumReadConcurrency);
    private readonly List<TimeSpan> _starts = new();
    private int _readSlotsToRetire;
    private KoLLimiterOptions _options = null!;
    private TimeSpan _actionInterval;
    private TimeSpan _readInterval;
    private TimeSpan _gap;
    private TimeSpan _actionNext = TimeSpan.MinValue;
    private TimeSpan _readNext = TimeSpan.MinValue;

    /// <summary>Legacy form: the given interval for actions, reads at most as slow, no global cap (tests and fakes).</summary>
    public KoLRateLimiter(TimeSpan interval) : this(interval, null) { }

    internal KoLRateLimiter(TimeSpan interval, IKoLLimiterClock? clock)
        : this(new KoLLimiterOptions
        {
            ActionInterval = interval,
            ReadInterval = interval < TimeSpan.FromSeconds(1) ? interval : TimeSpan.FromSeconds(1),
            MaxPerSecond = 0,
        }, clock, validate: false)
    {
        if (interval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
    }

    public KoLRateLimiter(KoLLimiterOptions options, IKoLLimiterClock? clock = null) : this(options, clock, validate: true) { }

    private KoLRateLimiter(KoLLimiterOptions options, IKoLLimiterClock? clock, bool validate)
    {
        _clock = clock ?? StopwatchLimiterClock.Instance;
        Configure(validate ? options.Clamped() : options);
    }

    public KoLLimiterOptions Options { get { lock (_lock) return _options; } }
    public TimeSpan Interval => _actionInterval;
    public TimeSpan ReadInterval => _readInterval;

    private void Configure(KoLLimiterOptions o)
    {
        lock (_lock)
        {
            _options = o;
            _actionInterval = o.ActionInterval;
            _readInterval = o.ReadInterval;
            _gap = o.MaxPerSecond > 0 ? TimeSpan.FromSeconds(1 / o.MaxPerSecond) : TimeSpan.Zero;
            // The read gate starts with every slot free; take away what the concurrency does not allow.
            var target = Math.Clamp(o.ReadConcurrency, 1, KoLLimiterOptions.MaximumReadConcurrency);
            var blocked = KoLLimiterOptions.MaximumReadConcurrency - target;
            while (_readSlotsToRetire < blocked)
            {
                if (_readGate.Wait(0)) { _readSlotsToRetire++; }
                else break; // slots are in flight; applied at startup in practice
            }
            while (_readSlotsToRetire > blocked) { _readGate.Release(); _readSlotsToRetire--; }
        }
    }

    /// <summary>
    /// Applies an interval from the settings file; call before the first request. The environment value
    /// (<paramref name="environmentValue"/>, milliseconds) wins when it is set and parseable. The result is
    /// clamped to <paramref name="floor"/> .. <see cref="MaximumInterval"/> (floor defaults to 1 s and is never lower).
    /// Returns the interval now in effect.
    /// </summary>
    public TimeSpan ApplyConfigured(TimeSpan configured, string? environmentValue, TimeSpan? floor = null)
    {
        var effective = EffectiveInterval(configured, environmentValue, floor);
        Configure(Options with { ActionInterval = effective });
        return effective;
    }

    /// <summary>Applies the whole lane configuration (settings plus environment); call before the first request.</summary>
    public void ApplyOptions(KoLLimiterOptions options) => Configure(options.Clamped());

    /// <summary>True when the environment value is set and parses as a number (so it overrides settings).</summary>
    public static bool IsEnvironmentOverride(string? environmentValue)
        => double.TryParse(environmentValue, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    public static TimeSpan EffectiveInterval(TimeSpan configured, string? environmentValue, TimeSpan? floor = null)
    {
        if (IsEnvironmentOverride(environmentValue)) return ResolveInterval(environmentValue);
        var min = floor is { } f && f > MinimumInterval ? f : MinimumInterval;
        return configured < min ? min : configured > MaximumInterval ? MaximumInterval : configured;
    }

    /// <summary>Reads a configured value in milliseconds, clamped to 1 s .. 60 s; empty or invalid means the default.</summary>
    public static TimeSpan ResolveInterval(string? milliseconds)
    {
        if (!double.TryParse(milliseconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
            return DefaultInterval;
        var value = TimeSpan.FromMilliseconds(ms);
        return value < MinimumInterval ? MinimumInterval : value > MaximumInterval ? MaximumInterval : value;
    }

    /// <summary>Waits until the next action-lane request may start (does not hold the lane afterwards).</summary>
    public async Task WaitAsync(CancellationToken cancellationToken = default)
        => (await AcquireAsync(KoLRequestLane.Action, cancellationToken).ConfigureAwait(false)).Dispose();

    /// <summary>
    /// Waits for a start slot in <paramref name="lane"/> and returns a lease. Dispose it when the response has arrived:
    /// an action lease keeps every other action out until then, a read lease frees one of the read slots.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(KoLRequestLane lane, CancellationToken cancellationToken = default)
    {
        var gate = lane == KoLRequestLane.Action ? _actionGate : _readGate;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var lease = new Lease(gate);
        try
        {
            var start = Reserve(lane);
            var wait = start - _clock.Now;
            if (wait > TimeSpan.Zero) await _clock.DelayAsync(wait, cancellationToken).ConfigureAwait(false);
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    /// <summary>Picks the start time of the next request of a lane: lane spacing first, then a free spot under the global cap.</summary>
    private TimeSpan Reserve(KoLRequestLane lane)
    {
        lock (_lock)
        {
            var now = _clock.Now;
            var t = now;
            if (lane == KoLRequestLane.Action) { if (_actionNext > t) t = _actionNext; }
            else if (_readNext > t) t = _readNext;

            if (_gap > TimeSpan.Zero)
            {
                _starts.RemoveAll(s => s <= now - _gap);
                foreach (var s in _starts) // sorted ascending
                    if (t > s - _gap && t < s + _gap) t = s + _gap;
                var at = _starts.FindIndex(s => s > t);
                if (at < 0) _starts.Add(t); else _starts.Insert(at, t);
            }

            if (lane == KoLRequestLane.Action) _actionNext = t + _actionInterval;
            else _readNext = t + _readInterval;
            return t;
        }
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) gate.Release(); }
    }
}

/// <summary>Delays each request through the lane of a <see cref="KoLRateLimiter"/> before it reaches the network; the lane is held until the response headers arrive.</summary>
public sealed class KoLRateLimitedHandler : DelegatingHandler
{
    private readonly KoLRateLimiter _limiter;

    public KoLRateLimitedHandler(HttpMessageHandler inner, KoLRateLimiter? limiter = null) : base(inner)
        => _limiter = limiter ?? KoLRateLimiter.Shared;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var lease = await _limiter.AcquireAsync(KoLRequestLanes.Classify(request), cancellationToken).ConfigureAwait(false);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
