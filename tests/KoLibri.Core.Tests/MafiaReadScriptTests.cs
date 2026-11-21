using Xunit;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Tests;

public sealed class MafiaReadScriptTests
{
    [Fact]
    public async Task Runs_supported_mafia_commands_and_filters_without_opening_mutating_routes()
    {
        using var script = new StringReader("""
            # Check supplies before adventuring
            echo Ready to adventure?
            status
            inv Knucklebone
            effects Empathy
            storage bone
            """);
        using var output = new StringWriter();
        var reads = new List<bool>();
        var status = new KoLStatusSnapshot("Fixture", 12, 100, 3, DateTimeOffset.UnixEpoch)
        {
            TimedEffects = new[] { new KoLTimedEffect("Empathy", 4), new KoLTimedEffect("Ode", 2) }
        };
        await MafiaReadScript.ExecuteAsync(script, output, _ => Task.FromResult(status),
            (storage, _) =>
            {
                reads.Add(storage);
                var items = new[] { new KoLInventoryItem("1", "knucklebone", storage ? 2 : 4),
                    new KoLInventoryItem("2", "broken skull", 1) };
                return Task.FromResult(new KoLInventorySnapshot(items, DateTimeOffset.UnixEpoch));
            });

        Assert.Equal(new[] { false, true }, reads);
        Assert.Equal(new[] { "Ready to adventure?", "Fixture · level 3", "Adventures: 12 · Meat: 100",
            "knucklebone (4)", "Empathy (4)", "knucklebone (2)" },
            output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task Reports_the_line_of_an_unsupported_command()
    {
        using var script = new StringReader("# comment\nadventure 10 The Sleazy Back Alley");
        using var output = new StringWriter();
        var error = await Assert.ThrowsAsync<NotSupportedException>(() =>
            MafiaReadScript.ExecuteAsync(script, output, _ => throw new InvalidOperationException(),
                (_, _) => throw new InvalidOperationException()));
        Assert.Contains("Line 2", error.Message);
        Assert.Contains("adventure", error.Message);
    }
}
