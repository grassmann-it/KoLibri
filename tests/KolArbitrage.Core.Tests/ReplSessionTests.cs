using KolArbitrage.Core.Accounts;
using KolArbitrage.Core.Adventure;
using KolArbitrage.Core.Data;
using KolArbitrage.Core.Integration;
using KoLibri.Cli.Repl;
using Xunit;

namespace KolArbitrage.Core.Tests;

public sealed class ReplSessionTests
{
    private static KoLmafiaCatalog LoadCatalog()
        => new MafiaDataLoader().Load(Path.Combine(AppContext.BaseDirectory, "external", "kolmafia-data"));

    private static KoLibriReplContext BuildContext()
    {
        // The REPL context requires HTTP factories; for the in-memory tests
        // we never call them because every command that needs HTTP is guarded
        // by an explicit dispatcher branch (status/inv). Tests below only
        // exercise the offline branches.
        return new KoLibriReplContext(
            AccountRole.Main,
            LoadCatalog(),
            _ => throw new InvalidOperationException("status reads are not exercised here"),
            _ => throw new InvalidOperationException("mall reads are not exercised here"));
    }

    [Fact]
    public void ParseArgs_handles_quoted_segments()
    {
        var args = ReplSession.ParseArgs("look \"haunted  skull\"  --only first");
        Assert.Equal(new[] { "look", "haunted  skull", "--only", "first" }, args);
    }

    [Fact]
    public async Task DispatchLineAsync_routes_help_to_plain_text()
    {
        var session = new ReplSession(BuildContext());
        var result = await session.DispatchLineAsync("help", CancellationToken.None);
        Assert.Contains("Kolibri REPL", result.Text);
    }

    [Fact]
    public async Task DispatchLineAsync_routes_look_to_lookup()
    {
        var session = new ReplSession(BuildContext());
        var result = await session.DispatchLineAsync("look seal tooth", CancellationToken.None);
        Assert.Contains("seal tooth", result.Text);
    }

    [Fact]
    public async Task DispatchLineAsync_routes_monster_to_lookup()
    {
        var session = new ReplSession(BuildContext());
        var result = await session.DispatchLineAsync("monster 141", CancellationToken.None);
        Assert.Contains("HaXx0r", result.Text);
    }

    [Fact]
    public async Task DispatchLineAsync_routes_unknown_command_to_error()
    {
        var session = new ReplSession(BuildContext());
        var result = await session.DispatchLineAsync("notacommand", CancellationToken.None);
        Assert.Contains("unknown command", result.Text);
    }

    [Fact]
    public async Task DispatchLineAsync_routes_campfire_with_real_planner()
    {
        var session = new ReplSession(BuildContext());
        var result = await session.DispatchLineAsync(
            "campfire --leaves 10 --main-meat 1500000 --shortner-price 75000", CancellationToken.None);
        Assert.Contains("campfire", result.Text);
        Assert.Contains("BurnOnly", result.Text);
    }

    [Fact]
    public async Task DispatchLineAsync_routes_routine_to_full_plan()
    {
        var session = new ReplSession(BuildContext());
        var result = await session.DispatchLineAsync(
            "routine --leaves 7 --main-meat 1500000 --permits 3 --clovers 1 --clover-floor 80000 --clover-sale 250000 --mallbot-meat 12000000 --bag-price 8500000 --bag-stock 5",
            CancellationToken.None);
        Assert.Contains("daily routine", result.Text);
        // 7 leaves, knucklebones-style default; we expect at least one
        // campfire or hermit or internet-shop step.
        Assert.True(result.Text.Contains("burn") || result.Text.Contains("clover") || result.Text.Contains("Lucky Surprise Egg"));
    }

    [Fact]
    public async Task DispatchLineAsync_routes_exit_to_flag()
    {
        var session = new ReplSession(BuildContext());
        var result = await session.DispatchLineAsync("exit", CancellationToken.None);
        Assert.Contains("exiting", result.Text);
    }

    [Fact]
    public async Task DispatchLineAsync_routes_role_to_active_role()
    {
        var session = new ReplSession(BuildContext());
        var result = await session.DispatchLineAsync("role", CancellationToken.None);
        Assert.Contains("Main", result.Text);
    }

    [Fact]
    public async Task DispatchLineAsync_empty_line_returns_empty_result()
    {
        var session = new ReplSession(BuildContext());
        var result = await session.DispatchLineAsync("   ", CancellationToken.None);
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public async Task DispatchLineAsync_routes_hermit_with_real_planner()
    {
        var session = new ReplSession(BuildContext());
        var result = await session.DispatchLineAsync(
            "hermit --permits 3 --clovers 1 --clover-floor 80000 --clover-sale 250000",
            CancellationToken.None);
        Assert.Contains("hermit", result.Text);
        Assert.Contains("UseClover", result.Text);
    }

    [Fact]
    public async Task DispatchLineAsync_routes_internet_shop_with_full_catalog()
    {
        var session = new ReplSession(BuildContext());
        var result = await session.DispatchLineAsync(
            "internet-shop --bacon 355 --machine", CancellationToken.None);
        Assert.Contains("internet-shop", result.Text);
        Assert.Contains("BuyAll", result.Text);
    }

    [Fact]
    public async Task DispatchLineAsync_routes_tokens_to_path_table()
    {
        var session = new ReplSession(BuildContext());
        var result = await session.DispatchLineAsync("tokens", CancellationToken.None);
        Assert.Contains("manual-browser", result.Text);
        Assert.Contains("app-default", result.Text);
    }

    [Fact]
    public async Task DispatchLineAsync_routes_login_status_to_known_roles()
    {
        var session = new ReplSession(BuildContext());
        var result = await session.DispatchLineAsync("login status", CancellationToken.None);
        Assert.Contains("Main", result.Text);
        Assert.Contains("Mallbot", result.Text);
    }

    [Fact]
    public async Task DispatchLineAsync_routes_login_unknown_subcommand_to_help()
    {
        var session = new ReplSession(BuildContext());
        var result = await session.DispatchLineAsync("login banana", CancellationToken.None);
        Assert.Contains("status, clear, refresh", result.Text);
    }

    [Fact]
    public async Task DispatchLineAsync_routes_login_refresh_with_unknown_role()
    {
        var session = new ReplSession(BuildContext());
        var result = await session.DispatchLineAsync("login refresh ghostuser me pw", CancellationToken.None);
        Assert.Contains("unknown role", result.Text);
    }
}
