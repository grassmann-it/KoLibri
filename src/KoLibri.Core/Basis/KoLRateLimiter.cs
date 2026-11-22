using System.Diagnostics;

namespace KoLibri.Core.Basis;

/// <summary>
/// Spaces out every request KoLibri sends to the game: one gate for the whole process, so the Main and
/// Mallbot sessions together never exceed it. Default is one request every 2 seconds (0.5 per second); the
/// interval can be raised with <c>KOLIBRI_MIN_REQUEST_INTERVAL_MS</c> but never goes below 1 second.
/// </summary>
public sealed class KoLRateLimiter
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaximumInterval = TimeSpan.FromMinutes(1);

    /// <summary>The limiter every KoL client of this process shares.</summary>
    public static KoLRateLimiter Shared { get; } = new(ResolveInterval(Environment.GetEnvironmentVariable(IntervalEnvironmentVariable)));

    public const string IntervalEnvironmentVariable = "KOLIBRI_MIN_REQUEST_INTERVAL_MS";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _intervalTicks;
    private TimeSpan _interval;
    private long _nextAllowed;

    /// <param name="interval">Time between the starts of two requests. Not clamped here; see <see cref="ResolveInterval"/>.</param>
    public KoLRateLimiter(TimeSpan interval)
    {
        if (interval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        _interval = interval;
        _intervalTicks = (long)(interval.TotalSeconds * Stopwatch.Frequency);
    }

    public TimeSpan Interval => _interval;

    /// <summary>
    /// Applies an interval from the settings file; call before the first request. The environment value
    /// (<paramref name="environmentValue"/>, milliseconds) wins when it is set and parseable. The result is
    /// clamped to <paramref name="floor"/> .. <see cref="MaximumInterval"/> (floor defaults to 1 s and is never lower).
    /// Returns the interval now in effect.
    /// </summary>
    public TimeSpan ApplyConfigured(TimeSpan configured, string? environmentValue, TimeSpan? floor = null)
    {
        var effective = EffectiveInterval(configured, environmentValue, floor);
        _interval = effective;
        Interlocked.Exchange(ref _intervalTicks, (long)(effective.TotalSeconds * Stopwatch.Frequency));
        return effective;
    }

    /// <summary>True when the environment value is set and parses as a number (so it overrides settings).</summary>
    public static bool IsEnvironmentOverride(string? environmentValue)
        => double.TryParse(environmentValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);

    public static TimeSpan EffectiveInterval(TimeSpan configured, string? environmentValue, TimeSpan? floor = null)
    {
        if (IsEnvironmentOverride(environmentValue)) return ResolveInterval(environmentValue);
        var min = floor is { } f && f > MinimumInterval ? f : MinimumInterval;
        return configured < min ? min : configured > MaximumInterval ? MaximumInterval : configured;
    }

    /// <summary>Reads a configured value in milliseconds, clamped to 1 s .. 60 s; empty or invalid means the default.</summary>
    public static TimeSpan ResolveInterval(string? milliseconds)
    {
        if (!double.TryParse(milliseconds, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ms))
            return DefaultInterval;
        var value = TimeSpan.FromMilliseconds(ms);
        return value < MinimumInterval ? MinimumInterval : value > MaximumInterval ? MaximumInterval : value;
    }

    /// <summary>Waits until the next request may start.</summary>
    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = Stopwatch.GetTimestamp();
            var wait = _nextAllowed - now;
            if (wait > 0)
                await Task.Delay(TimeSpan.FromSeconds((double)wait / Stopwatch.Frequency), cancellationToken).ConfigureAwait(false);
            var ticks = Interlocked.Read(ref _intervalTicks);
            _nextAllowed = Math.Max(now, _nextAllowed) + ticks;
            if (wait > 0) _nextAllowed = Stopwatch.GetTimestamp() + ticks;
        }
        finally { _gate.Release(); }
    }
}

/// <summary>Delays each request through a <see cref="KoLRateLimiter"/> before it reaches the network.</summary>
public sealed class KoLRateLimitedHandler : DelegatingHandler
{
    private readonly KoLRateLimiter _limiter;

    public KoLRateLimitedHandler(HttpMessageHandler inner, KoLRateLimiter? limiter = null) : base(inner)
        => _limiter = limiter ?? KoLRateLimiter.Shared;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await _limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
