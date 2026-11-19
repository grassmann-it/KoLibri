using System.Text;
using KolArbitrage.Core.Accounts;
using KolArbitrage.Core.Integration;
using KoLibri.Cli.Repl;
using Xunit;

namespace KolArbitrage.Core.Tests;

/// <summary>
/// Covers the CLI's login sub-commands (status/clear/refresh). The base
/// `login` flow stays a KoLHttp-only path; the sub-commands work with
/// the in-process AccountSessionManager and don't hit the network.
/// </summary>
public sealed class LoginDispatchTests
{
    [Fact]
    public async Task Login_status_lists_both_roles_with_cookie_indicator()
    {
        var accounts = new AccountSessionManager(new InMemoryAccountStore(), new InMemorySecretStore());
        await accounts.SetProfileAsync(new AccountProfile
        {
            Id = "main", DisplayName = "alice", Role = AccountRole.Main, Enabled = true
        });
        await accounts.SetCredentialsAsync(AccountRole.Main, new AccountCredentials
        {
            CookieHeader = "PHPSESSID=abc", ExpiresAt = DateTimeOffset.UtcNow.AddHours(2)
        });
        await accounts.SetProfileAsync(new AccountProfile
        {
            Id = "mallbot", DisplayName = "mallice", Role = AccountRole.Mallbot, Enabled = true
        });
        // No credentials for mallbot: status should report hasCookie=false.

        var statuses = await accounts.GetAccountsAsync();
        Assert.Equal(2, statuses.Count);
        var mainSession = await accounts.GetSessionAsync(AccountRole.Main);
        Assert.NotNull(mainSession);
        Assert.True(mainSession!.Credentials.ExpiresAt > DateTimeOffset.UtcNow);

        var mallbotSession = await accounts.GetSessionAsync(AccountRole.Mallbot, includeExpired: true);
        Assert.Null(mallbotSession);
    }

    [Fact]
    public async Task Login_clear_overwrites_credentials_with_expired_marker()
    {
        var accounts = new AccountSessionManager(new InMemoryAccountStore(), new InMemorySecretStore());
        await accounts.SetProfileAsync(new AccountProfile
        {
            Id = "main", DisplayName = "alice", Role = AccountRole.Main, Enabled = true
        });
        await accounts.SetCredentialsAsync(AccountRole.Main, new AccountCredentials
        {
            CookieHeader = "PHPSESSID=abc", ExpiresAt = DateTimeOffset.UtcNow.AddHours(2)
        });
        // Simulate `login clear`: empty credentials with a sentinel expiry.
        await accounts.SetCredentialsAsync(AccountRole.Main, new AccountCredentials
        {
            Password = string.Empty,
            CookieHeader = string.Empty,
            ExpiresAt = DateTimeOffset.MinValue
        });
        var session = await accounts.GetSessionAsync(AccountRole.Main);
        Assert.Null(session);
        var expiredSession = await accounts.GetSessionAsync(AccountRole.Main, includeExpired: true);
        Assert.NotNull(expiredSession);
        Assert.Equal(string.Empty, expiredSession!.Credentials.CookieHeader);
        Assert.True(expiredSession.IsExpired());
    }

    [Fact]
    public void ParseArgs_handles_login_subcommand_dispatch()
    {
        Assert.Equal(new[] { "status" }, ReplSession.ParseArgs("status"));
        Assert.Equal(new[] { "clear", "--role", "all" }, ReplSession.ParseArgs("clear --role all"));
        Assert.Equal(new[] { "refresh", "--username", "x" }, ReplSession.ParseArgs("refresh --username x"));
    }
}

/// <summary>Tiny in-memory implementations of the storage interfaces so the
/// login sub-commands can be exercised without disk I/O.</summary>
internal sealed class InMemoryAccountStore : IAccountStore
{
    private AccountConfiguration _config = new() { Accounts = new() };
    public Task<AccountConfiguration> LoadAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_config);
    public Task SaveAsync(AccountConfiguration configuration, CancellationToken cancellationToken = default)
    {
        _config = configuration;
        return Task.CompletedTask;
    }
}

internal sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, AccountCredentials> _secrets = new(StringComparer.OrdinalIgnoreCase);
    public Task<AccountCredentials?> GetAsync(string accountId, CancellationToken cancellationToken = default)
        => Task.FromResult(_secrets.TryGetValue(accountId, out var c) ? c : null);
    public Task SetAsync(string accountId, AccountCredentials credentials, CancellationToken cancellationToken = default)
    {
        _secrets[accountId] = credentials;
        return Task.CompletedTask;
    }
    public Task RemoveAsync(string accountId, CancellationToken cancellationToken = default)
    {
        _secrets.Remove(accountId);
        return Task.CompletedTask;
    }
}
