using KolArbitrage.Core.Accounts;
using LibKolibri;
using Xunit;

namespace KolArbitrage.Core.Tests;

public sealed class KolibriClientTests
{
    private static AccountSessionManager Manager()
        => new(new InMemoryAccountStore(), new InMemorySecretStore());

    [Fact]
    public async Task Open_without_a_stored_account_asks_for_a_login()
    {
        var exception = await Assert.ThrowsAsync<KolibriLoginRequiredException>(
            () => KolibriClient.OpenAsync(AccountRole.Main, Manager()));
        Assert.Contains("LoginAsync", exception.Message);
    }

    [Fact]
    public async Task An_expired_session_without_a_saved_password_asks_for_a_login_and_sends_no_request()
    {
        var accounts = Manager();
        await accounts.SetProfileAsync(new AccountProfile
        {
            Id = "main", DisplayName = "tester", Role = AccountRole.Main, Enabled = true
        });
        await accounts.SetCredentialsAsync(AccountRole.Main, new AccountCredentials
        {
            CookieHeader = "PHPSESSID=x",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1)
        });

        await Assert.ThrowsAsync<KolibriLoginRequiredException>(
            () => KolibriClient.OpenAsync(AccountRole.Main, accounts));
    }
}
