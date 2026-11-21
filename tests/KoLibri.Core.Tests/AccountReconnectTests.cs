using Xunit;
using KoLibri.Core.Basis;

namespace KoLibri.Core.Tests;

public sealed class AccountReconnectTests
{
    [Fact]
    public async Task Concurrent_reconnects_reuse_the_new_session_and_preserve_the_saved_password()
    {
        var accounts = new AccountSessionManager(new InMemoryAccountStore(), new InMemorySecretStore());
        await accounts.SetProfileAsync(new AccountProfile
        {
            Id = "main", DisplayName = "Fixture", Role = AccountRole.Main
        });
        await accounts.SetCredentialsAsync(AccountRole.Main,
            new AccountCredentials { Username = "Fixture", Password = "saved-password", CookieHeader = "old=1" });
        var expired = await accounts.GetRequiredSessionAsync(AccountRole.Main);
        var attempts = 0;

        async Task<KoLLoginResult> Login(string user, string password, CancellationToken token)
        {
            await Task.Yield();
            Assert.Equal("Fixture", user);
            Assert.Equal("saved-password", password);
            attempts++;
            return new KoLLoginResult(user, new AccountCredentials { CookieHeader = "new=1" }, DateTimeOffset.UtcNow);
        }

        var sessions = await Task.WhenAll(
            accounts.ReconnectAsync(expired, Login), accounts.ReconnectAsync(expired, Login));

        Assert.Equal(1, attempts);
        Assert.All(sessions, session => Assert.Equal("new=1", session?.Credentials.CookieHeader));
        Assert.Equal("saved-password",
            (await accounts.GetRequiredSessionAsync(AccountRole.Main)).Credentials.Password);
    }
}
