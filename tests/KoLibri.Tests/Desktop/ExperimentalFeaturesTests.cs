using KoLibri.Desktop;
using Xunit;

namespace KoLibri.Tests.Desktop;

public sealed class ExperimentalFeaturesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kolibri-experimental-" + Guid.NewGuid().ToString("N"));

    private static Func<string, string?> Env(string? value)
        => name => name == ExperimentalFeatures.EnvironmentVariable ? value : null;

    [Fact]
    public void Off_by_default()
    {
        Assert.False(new DesktopSettings().ExperimentalTools);
        Assert.False(ExperimentalFeatures.IsEnabled(new DesktopSettings(), Env(null)));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData(" ON ", true)]
    [InlineData("yes", true)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData("false", false)]
    public void Environment_variable_turns_it_on(string value, bool expected)
        => Assert.Equal(expected, ExperimentalFeatures.IsEnabled(new DesktopSettings(), Env(value)));

    [Fact]
    public void Settings_box_turns_it_on_without_the_variable()
        => Assert.True(ExperimentalFeatures.IsEnabled(new DesktopSettings { ExperimentalTools = true }, Env(null)));

    [Fact]
    public void Old_optional_tools_flag_does_not_turn_it_on()
        => Assert.False(ExperimentalFeatures.IsEnabled(new DesktopSettings { OptionalTools = true }, Env(null)));

    [Fact]
    public void Setting_round_trips_and_old_files_load_as_off()
    {
        var store = new DesktopSettingsStore(Path.Combine(_dir, "s.json"));
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.Path, "{\"optionalTools\":true}");
        Assert.False(store.Load().ExperimentalTools);
        store.Save(new DesktopSettings { ExperimentalTools = true });
        Assert.True(store.Load().ExperimentalTools);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
