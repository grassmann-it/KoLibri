using KoLibri.Desktop;
using Xunit;

namespace KoLibri.Tests.Desktop;

public sealed class DesktopSettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kolibri-settings-" + Guid.NewGuid().ToString("N"));
    private DesktopSettingsStore Store => new(Path.Combine(_dir, "s.json"));

    [Fact]
    public void RoundTripKeepsValues()
    {
        var s = new DesktopSettings { DefaultAccount = "Main", Theme = "Dark", OptionalTools = false };
        s.Set("meatPerDay", 123_456);
        Store.Save(s);
        var back = Store.Load();
        Assert.Equal("Main", back.DefaultAccount);
        Assert.Equal("Dark", back.Theme);
        Assert.False(back.OptionalTools);
        Assert.Equal(123_456, back.Get("meatPerDay"));
        Assert.Equal(10, back.Get("unitsPerPurchase"));
    }

    [Fact]
    public void ClampsAndToleratesBadValues()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Store.Path, "{\"theme\":\"Neon\",\"numbers\":{\"kolIntervalSeconds\":0.1,\"pricegunIntervalSeconds\":9999,\"hpStopPercent\":\"x\"}}");
        var s = Store.Load();
        Assert.Equal(1, s.Get("kolIntervalSeconds"));
        Assert.Equal(60, s.Get("pricegunIntervalSeconds"));
        Assert.Equal(30, s.Get("hpStopPercent"));
        Assert.Equal("Light", s.Theme);
        File.WriteAllText(Store.Path, "not json");
        Assert.Equal(2, Store.Load().Get("kolIntervalSeconds"));
    }

    [Fact]
    public void EnvironmentOverrideIsReported()
    {
        var s = new DesktopSettings();
        s.Set("kolIntervalSeconds", 5);
        var plain = s.EffectiveKolInterval(_ => null);
        Assert.Equal(5, plain.Value);
        Assert.False(plain.OverriddenByEnvironment);
        var over = s.EffectiveKolInterval(k => k == "KOLIBRI_MIN_REQUEST_INTERVAL_MS" ? "7000" : null);
        Assert.Equal(7, over.Value);
        Assert.True(over.OverriddenByEnvironment);
        Assert.Equal("KOLIBRI_MIN_REQUEST_INTERVAL_MS", over.EnvironmentVariable);
    }

    [Fact]
    public void LimiterOptionsFollowPresetThenSettingsThenEnvironment()
    {
        var s = new DesktopSettings();
        Assert.Equal(2, s.ToLimiterOptions(_ => null).ReadConcurrency);
        s.LimiterPreset = "browser-like";
        Assert.Equal(3, s.ToLimiterOptions(_ => null).ReadConcurrency);
        s.Set("kolReadConcurrency", 1);
        Assert.Equal(1, s.ToLimiterOptions(_ => null).ReadConcurrency);
        Assert.Equal(4, s.ToLimiterOptions(k => k == "KOLIBRI_READ_CONCURRENCY" ? "4" : null).ReadConcurrency);
        Assert.Equal(2, s.ToLimiterOptions(k => k == "KOLIBRI_LIMITER_PRESET" ? "conservative" : null).MaxPerSecond);
    }

    [Fact]
    public void LimiterOptionsCarryIntervalsAndPerSecondCapIntoTheLibrary()
    {
        var s = new DesktopSettings();
        s.Set("kolIntervalSeconds", 3);
        var o = s.ToLimiterOptions(_ => null);
        Assert.Equal(TimeSpan.FromSeconds(3), o.ActionInterval);
        Assert.Equal(TimeSpan.FromSeconds(1), o.ReadInterval);
        Assert.Equal(2, o.MaxPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(4), s.ToLimiterOptions(k => k == "KOLIBRI_MIN_REQUEST_INTERVAL_MS" ? "4000" : null).ActionInterval);
        Assert.Equal(TimeSpan.FromSeconds(1), s.ToLimiterOptions(k => k == "KOLIBRI_MIN_REQUEST_INTERVAL_MS" ? "5" : null).ActionInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(200), s.ToLimiterOptions(k => k == "KOLIBRI_READ_INTERVAL_MS" ? "10" : null).ReadInterval);
        Assert.Equal(1, s.ToLimiterOptions(k => k == "KOLIBRI_MAX_REQUESTS_PER_SECOND" ? "0" : null).MaxPerSecond);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
}
