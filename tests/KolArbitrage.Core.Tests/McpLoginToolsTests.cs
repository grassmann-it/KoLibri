using KolArbitrage.Core.Accounts;
using Xunit;

namespace KolArbitrage.Core.Tests;

/// <summary>
/// The `kol_login_*` MCP tools are thin wrappers over AccountSessionManager
/// + KoLLoginClient. The MCP wiring is covered by KolibriMcpServer's
/// registration smoke test (HandleAsync on a tools/list request). This
/// file focuses on the per-tool behaviour that is independent of the
/// JSON-RPC envelope: status rows and clear-with-sentinel semantics.
/// </summary>
public sealed class McpLoginToolsTests
{
    [Fact]
    public async Task Login_status_rows_have_cookie_and_expiry_columns()
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

        var profiles = await accounts.GetAccountsAsync();
        Assert.Equal(2, profiles.Count);
        var mainSession = await accounts.GetSessionAsync(AccountRole.Main);
        Assert.NotNull(mainSession);
        Assert.True(!string.IsNullOrWhiteSpace(mainSession!.Credentials.CookieHeader));
        Assert.True(mainSession.Credentials.ExpiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Login_clear_writes_sentinel_expiry_so_GetSessionAsync_returns_null()
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
        // simulate kol_login_clear
        await accounts.SetCredentialsAsync(AccountRole.Main, new AccountCredentials
        {
            Password = string.Empty,
            CookieHeader = string.Empty,
            ExpiresAt = DateTimeOffset.MinValue
        });
        var session = await accounts.GetSessionAsync(AccountRole.Main);
        Assert.Null(session);
        var expired = await accounts.GetSessionAsync(AccountRole.Main, includeExpired: true);
        Assert.NotNull(expired);
        Assert.True(expired!.IsExpired());
    }
}
