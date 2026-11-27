using Xunit;

namespace KoLibri.Core.Tests;

/// <summary>0.4.0: market/shop extras are hidden behind --experimental / KOLIBRI_EXPERIMENTAL=1. No test sends anything.</summary>
[Collection(ConsoleCollection.Name)] // runs the CLI, which writes to Console.Out
public sealed class CliExperimentalGateTests
{
    private static readonly Func<string, string?> NoEnv = _ => null;

    public static IEnumerable<object[]> HiddenCommands() => new[]
    {
        "arbitrage", "bribe-rank", "bribe-plan", "snatch-plan", "crm", "crm-block", "categorize", "workflow-plan",
        "watchlist", "scheduler", "pricegun", "compare", "history", "market-scan", "market", "reprice", "batch",
        "apply-prices", "ARBITRAGE",
    }.Select(c => new object[] { c });

    public static IEnumerable<object[]> CoreCommands() => new[]
    {
        "login", "status", "inventory", "consume", "adventure", "mall", "mall-reconcile", "daily", "relay", "token",
        "agent-mode", "rest", "cast", "combat", "autocombat", "choice", "shop", "repl", "adventure-plan",
    }.Select(c => new object[] { c });

    [Theory]
    [MemberData(nameof(HiddenCommands))]
    public void Hidden_commands_are_blocked_with_a_hint_without_the_switch(string command)
    {
        var error = new StringWriter();
        var code = ExperimentalGate.Check(command, enabled: false, error);
        Assert.NotNull(code);
        Assert.NotEqual(0, code);
        Assert.Contains("experimental", error.ToString());
        Assert.Contains("--experimental", error.ToString());
        Assert.Contains("KOLIBRI_EXPERIMENTAL=1", error.ToString());
        Assert.Contains("Nothing was sent", error.ToString());
    }

    [Theory]
    [MemberData(nameof(HiddenCommands))]
    public async Task Blocked_run_returns_before_any_session_or_request(string command)
    {
        // Exit code 3 comes only from the gate; reaching a session read would fail differently (1) or need a login.
        var code = await KolibriCli.RunAsync([command, "--role", "mallbot", "--item", "8691", "--execute"], NoEnv);
        Assert.Equal(ExperimentalGate.BlockedExitCode, code);
    }

    [Theory]
    [MemberData(nameof(HiddenCommands))]
    public void Switch_or_environment_unlocks_hidden_commands(string command)
    {
        Assert.Null(ExperimentalGate.Check(command, enabled: true, TextWriter.Null));
        Assert.True(ExperimentalGate.Enabled([command, "--experimental"], NoEnv));
        Assert.True(ExperimentalGate.Enabled([command], v => v == "KOLIBRI_EXPERIMENTAL" ? "1" : null));
        Assert.True(ExperimentalGate.Enabled([command], v => v == "KOLIBRI_EXPERIMENTAL" ? "true" : null));
        Assert.False(ExperimentalGate.Enabled([command], v => v == "KOLIBRI_EXPERIMENTAL" ? "0" : null));
        Assert.False(ExperimentalGate.Enabled([command], NoEnv));
    }

    [Fact]
    public void Switch_is_stripped_wherever_it_appears()
    {
        Assert.Equal(new[] { "arbitrage", "plan", "--item", "1" },
            ExperimentalGate.Strip(["--experimental", "arbitrage", "plan", "--EXPERIMENTAL", "--item", "1"]));
    }

    [Theory]
    [MemberData(nameof(CoreCommands))]
    public void Core_commands_are_never_gated(string command)
    {
        Assert.False(ExperimentalGate.IsExperimental(command));
        Assert.Null(ExperimentalGate.Check(command, enabled: false, TextWriter.Null));
    }

    private static string Help(bool experimental)
    {
        var w = new StringWriter();
        KolibriCli.PrintHelp(w, experimental);
        return w.ToString();
    }

    [Fact]
    public void Default_help_hides_experimental_commands_and_keeps_core_ones()
    {
        var help = Help(experimental: false);
        var commandWords = help.Split('\n')
            .Select(l => l.Trim().Split(' ', 2)[0])
            .Where(w => w.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var hidden in ExperimentalGate.Commands)
            Assert.DoesNotContain(hidden, commandWords);
        foreach (var core in new[] { "login", "status", "inventory", "consume", "adventure", "mall", "daily", "relay", "token" })
            Assert.Contains(core, commandWords);
        Assert.DoesNotContain("arbitrage", help);
        Assert.DoesNotContain("bribe", help);
        Assert.DoesNotContain("crm", help);
        Assert.DoesNotContain("watchlist", help);
        Assert.Contains("--experimental", help);
    }

    [Fact]
    public void Experimental_help_lists_the_hidden_commands_in_their_own_section()
    {
        var help = Help(experimental: true);
        var section = help.IndexOf("Experimental (", StringComparison.Ordinal);
        Assert.True(section > 0);
        foreach (var name in new[] { "arbitrage plan", "bribe-rank", "bribe-plan", "snatch-plan", "crm ", "crm-block",
                     "categorize", "workflow-plan", "watchlist", "pricegun", "compare", "reprice", "apply-prices" })
            Assert.True(help.IndexOf(name, StringComparison.Ordinal) > section, name);
    }
}
