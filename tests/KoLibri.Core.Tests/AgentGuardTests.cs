using Xunit;

namespace KoLibri.Core.Tests;

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
        Assert.Contains("Nothing was sent", message);
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

    [Theory]
    [InlineData("relay", "--role", "main")]
    [InlineData("cast", "--skill", "7185", "--execute")]
    [InlineData("consume", "--item", "1", "--execute")]
    [InlineData("rest", "--execute")]
    [InlineData("adventure", "run", "--execute")]
    [InlineData("adventure", "choice-chain", "--execute")]
    public void New_write_capable_commands_need_a_terminal_yes_under_an_agent(params string[] args)
    {
        Assert.Equal(3, Run(args, ClaudeEnv, redirected: true, allowed: false, answer: null, out _));
        Assert.Equal(3, Run(args, ClaudeEnv, redirected: true, allowed: false, answer: false, out _));
        Assert.Null(Run(args, ClaudeEnv, redirected: true, allowed: false, answer: true, out _));
    }

    [Fact]
    public void Previews_of_cast_consume_rest_are_not_writes()
    {
        Assert.False(AgentGuard.IsWrite(["cast", "--skill", "7185"]));
        Assert.False(AgentGuard.IsWrite(["rest"]));
    }

    [Fact]
    public void A_preview_never_purchases()
    {
        Assert.False(AgentGuard.MayPurchase(execute: false, tokenValid: true));
        Assert.False(AgentGuard.MayPurchase(execute: true, tokenValid: false));
        Assert.True(AgentGuard.MayPurchase(execute: true, tokenValid: true));
    }

    [Fact]
    public async Task Invalid_or_missing_approval_token_is_rejected_before_any_http()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kolibri-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new KoLibri.Cli.ApprovalTokenStore(dir);
            Assert.False(await AgentGuard.ApprovalTokenValidAsync("anything", store)); // no stored token
            await store.SaveAsync(new KoLibri.Cli.ApprovalToken("secret-token", DateTimeOffset.UtcNow));
            Assert.False(await AgentGuard.ApprovalTokenValidAsync("wrong", store));
            Assert.False(await AgentGuard.ApprovalTokenValidAsync("  ", store));
            Assert.False(await AgentGuard.ApprovalTokenValidAsync(null, store));
            Assert.True(await AgentGuard.ApprovalTokenValidAsync("secret-token", store));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
