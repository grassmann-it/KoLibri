using KoLibri.Desktop;

namespace KoLibri.Tests.Desktop;

/// <summary>Desktop accounts against the fake KoL: in-memory accounts, temp state, no limiter waits, no network.</summary>
internal static class DesktopKit
{
    public static DesktopAccounts Accounts(FakeKol kol, MemoryAccountStore? store = null, Func<bool>? autoReconnect = null)
        => new(new KolibriOptions
        {
            HttpHandler = kol,
            AccountStore = store ?? new MemoryAccountStore(),
            StateDirectory = Directory.CreateTempSubdirectory("kolibri-desktop-tests-").FullName,
            DataDirectory = Path.Combine(AppContext.BaseDirectory, "external", "kolmafia-data"),
            AutoReconnect = autoReconnect,
            DisableLimiterForTests = true
        });

    /// <summary>A stored account for a role. <paramref name="expired"/>: the cookie is past its expiry (as 0.4's IsExpired).</summary>
    public static MemoryAccountStore Store(AccountRole role, bool expired = false, string? password = "pw", string? cookie = "PHPSESSID=old")
    {
        var store = new MemoryAccountStore();
        var id = DesktopAccounts.IdOf(role);
        store.SaveAsync(new AccountRecord(new Account(new AccountId(id), "Tester", 1001, null, Array.Empty<string>()),
            cookie, password, expired ? DateTimeOffset.UtcNow.AddHours(-1) : DateTimeOffset.UtcNow.AddHours(3)), CancellationToken.None).Wait();
        return store;
    }

    /// <summary>KoL's login page and a successful POST that sets a fresh cookie.</summary>
    public static FakeKol WithLogin(this FakeKol kol, string cookie = "PHPSESSID=fresh", bool fail = false)
        => kol.On("GET login.php", "<html><body><form action=\"login.php\"><input name=loginname></form>Welcome</body></html>")
            .On("POST login.php", (_, _) =>
            {
                if (fail) throw new HttpRequestException("secret detail https://x");
                var response = FakeKol.Redirect("https://www.kingdomofloathing.com/main.php");
                response.Headers.Add("Set-Cookie", cookie + "; path=/");
                return response;
            })
            .On("GET main.php", "<html><body>The Kingdom</body></html>");
}
