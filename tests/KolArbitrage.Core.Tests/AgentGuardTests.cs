using Xunit;

namespace KolArbitrage.Core.Tests;

public sealed class AgentGuardTests
{
    private static string? NoEnv(string _) => null;
    private static string? ClaudeEnv(string name) => name == "CLAUDECODE" ? "1" : null;

    private static int? Run(string[] args, Func<string, string?> env, bool redirected, bool allowed, bool? answer, out string message)
    {
        var error = new StringWriter();
        var result = AgentGuard.Check(args, env, redirected, redirected, allowed, _ => answer, error);
        message = error.ToString();
        return result;
    }

    [Fact]
    public void A_human_at_a_terminal_is_never_asked()
        => Assert.Null(Run(["daily-go", "--execute"], NoEnv, redirected: false, allowed: false, answer: null, out _));

    [Fact]
    public void Reads_under_an_agent_run_freely()
        => Assert.Null(Run(["status"], ClaudeEnv, redirected: true, allowed: false, answer: null, out _));

    [Fact]
    public void A_write_under_an_agent_without_terminal_is_refused_and_sends_nothing()
    {
        Assert.Equal(3, Run(["daily-go", "--execute"], ClaudeEnv, redirected: true, allowed: false, answer: null, out var message));
        Assert.Contains("Nichts wurde gesendet", message);
    }

    [Fact]
    public void A_write_under_an_agent_needs_a_yes_at_the_terminal()
    {
        Assert.Null(Run(["market-scan", "--sell"], ClaudeEnv, redirected: true, allowed: false, answer: true, out _));
        Assert.Equal(3, Run(["market-scan", "--sell"], ClaudeEnv, redirected: true, allowed: false, answer: false, out _));
    }

    [Fact]
    public void Redirected_input_and_output_count_as_unattended_even_without_a_known_harness()
        => Assert.Equal(3, Run(["apply-prices", "--commit"], NoEnv, redirected: true, allowed: false, answer: null, out _));

    [Fact]
    public void The_users_own_switch_lets_scripts_write()
        => Assert.Null(Run(["daily-go", "--execute"], ClaudeEnv, redirected: true, allowed: true, answer: null, out _));
}
