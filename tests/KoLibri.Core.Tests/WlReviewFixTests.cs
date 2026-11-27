using System.Net;
using Xunit;
using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Tests;

public sealed class WlReviewFixTests
{
    private sealed class Api : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var url = r.RequestUri!.PathAndQuery;
            Requests.Add(url);
            var body = url.StartsWith("/api.php") ? "{\"pwd\":\"abcdef0123456789abcdef\",\"playerid\":\"1\",\"name\":\"X\"}" : "<html>no hash here</html>";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task Session_hash_comes_from_api_status_even_when_pages_carry_none()
    {
        var api = new Api();
        var dispatcher = new FreePullDispatcher(api, new Uri("https://kol.test/"));
        var hash = await dispatcher.ReadSessionHashAsync(new AccountCredentials { CookieHeader = "s=1" });
        Assert.Equal("abcdef0123456789abcdef", hash);
        Assert.Single(api.Requests);
    }

    [Fact]
    public void Relay_host_accepts_a_bare_log_filename()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var old = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(dir);
            var session = new AccountSession(new AccountProfile { Id = "main", DisplayName = "F", Role = AccountRole.Main },
                new AccountCredentials { CookieHeader = "s=1" });
            using var host = RelayHost.Start(session, 0, "relay.jsonl", false, null, new Api());
        }
        finally { Directory.SetCurrentDirectory(old); try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Strict_long_rejects_garbage_and_applies_default_only_when_absent()
    {
        Assert.Equal(40_000, StrictOptions.Long(new[] { "--execute" }, "--max-price", 40_000, 1, 2_000_000_000));
        Assert.Equal(5, StrictOptions.Long(new[] { "--max-price", "5" }, "--max-price", 40_000, 1, 2_000_000_000));
        Assert.Throws<ArgumentException>(() => StrictOptions.Long(new[] { "--max-price", "abc" }, "--max-price", 40_000, 1, 100));
        Assert.Throws<ArgumentException>(() => StrictOptions.Long(new[] { "--max-price" }, "--max-price", 40_000, 1, 100));
    }

    [Fact]
    public async Task Login_clear_with_role_but_no_value_is_rejected_before_clearing()
        => await Assert.ThrowsAsync<ArgumentException>(() => KolibriCli.LoginClearAsync(new[] { "--role" }));
}
