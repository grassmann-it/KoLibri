using System.Net;
using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;
using LibKolibri;
using Xunit;

namespace KoLibri.Core.Tests;

public sealed class AccountIdentityAndPwdTests
{
    private const string RealHash = "0123456789abcdef0123456789abcdef";
    private static readonly Uri Base = new("https://www.kingdomofloathing.com/");

    private sealed class FakeKol(Func<HttpRequestMessage, string, string> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var form = request.Content is null ? "" : await request.Content.ReadAsStringAsync(token);
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery, form));
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(respond(request, form)) };
        }
    }

    private static string Status(string extra = "") =>
        "{" + extra + "\"full\":\"0\",\"drunk\":0,\"spleen\":0,\"familiar\":\"0\",\"equipment\":{}}";

    private static AccountCredentials Creds(string cookie) => new()
    {
        CookieHeader = cookie, AccessToken = "cookie-token-not-the-hash", Username = "x",
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
    };

    private static async Task<AccountSessionManager> ManagerWithAsync(AccountRole role, string name, string? playerId, string cookie)
    {
        var accounts = new AccountSessionManager(new InMemoryAccountStore(), new InMemorySecretStore());
        await accounts.InitializeAsync();
        await accounts.SetProfileAsync(new AccountProfile { Id = role.ToString().ToLowerInvariant(), DisplayName = name, Role = role, PlayerId = playerId });
        await accounts.SetCredentialsAsync(role, Creds(cookie));
        return accounts;
    }

    [Fact]
    public async Task Pwd_comes_from_the_status_api_and_is_cached_per_session()
    {
        var kol = new FakeKol((_, _) => Status($"\"pwd\":\"{RealHash}\","));
        using var http = KoLLoginClient.CreateAuthenticatedClient(Creds("charpwd=c1-unique"), Base, innerHandler: kol);
        var first = await KoLPwdHashResolver.RequireAsync(http, "charpwd=c1-unique");
        var second = await KoLPwdHashResolver.RequireAsync(http, "charpwd=c1-unique");
        Assert.Equal(RealHash, first);
        Assert.Equal(first, second);
        Assert.NotEqual("cookie-token-not-the-hash", first);
        Assert.Single(kol.Requests);
    }

    [Fact]
    public async Task Pwd_falls_back_to_the_charpane_and_a_missing_hash_is_refused()
    {
        var withPane = new FakeKol((r, _) => r.RequestUri!.AbsolutePath == "/charpane.php"
            ? $"<script>var pwdhash = \"{RealHash}\";</script>" : Status());
        using var http = KoLLoginClient.CreateAuthenticatedClient(Creds("charpwd=c2-unique"), Base, innerHandler: withPane);
        Assert.Equal(RealHash, await KoLPwdHashResolver.RequireAsync(http, "charpwd=c2-unique"));

        var none = new FakeKol((_, _) => Status());
        using var http2 = KoLLoginClient.CreateAuthenticatedClient(Creds("charpwd=c3-unique"), Base, innerHandler: none);
        await Assert.ThrowsAsync<InvalidOperationException>(() => KoLPwdHashResolver.RequireAsync(http2, "charpwd=c3-unique"));
    }

    [Fact]
    public async Task Character_actions_post_the_real_hash_not_the_login_cookie()
    {
        var kol = new FakeKol((r, form) => r.Method == HttpMethod.Post ? "You eat the thing."
            : r.RequestUri!.Query.Contains("what=inventory") ? "{\"995\":3}" : Status($"\"pwd\":\"{RealHash}\","));
        var accounts = await ManagerWithAsync(AccountRole.Main, "A", "1", "charpwd=c4-unique");
        using var session = await CharacterSession.OpenAsync(AccountRole.Main, accounts, innerHandler: kol);
        await session.ExecuteAsync(new ConsumeCharacterItem(995, CharacterConsumableKind.Food));
        var post = Assert.Single(kol.Requests, x => x.Method == HttpMethod.Post);
        Assert.Contains($"pwd={RealHash}", post.Body);
        Assert.DoesNotContain("cookie-token", post.Body);
    }

    [Fact]
    public async Task Without_a_resolvable_hash_nothing_is_posted()
    {
        var kol = new FakeKol((_, _) => Status());
        var accounts = await ManagerWithAsync(AccountRole.Main, "A", "1", "charpwd=c5-unique");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CharacterSession.OpenAsync(AccountRole.Main, accounts, innerHandler: kol));
        Assert.DoesNotContain(kol.Requests, x => x.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task A_200_error_page_is_shown_as_the_exact_rejection_text()
    {
        var kol = new FakeKol((r, _) => r.Method == HttpMethod.Post ? "<html><body>You can't eat that right now.</body></html>"
            : r.RequestUri!.Query.Contains("what=inventory") ? "{\"995\":3}" : Status($"\"pwd\":\"{RealHash}\","));
        var accounts = await ManagerWithAsync(AccountRole.Main, "A", "1", "charpwd=c6-unique");
        using var session = await CharacterSession.OpenAsync(AccountRole.Main, accounts, innerHandler: kol);
        var after = await session.ExecuteAsync(new UseInventoryItem(995));
        Assert.Contains("You can't eat that right now.", after.RejectionReason);
    }

    [Fact]
    public async Task Refresh_with_another_player_is_refused_and_nothing_changes()
    {
        var accounts = await ManagerWithAsync(AccountRole.Main, "Alice", "100", "charpwd=old");
        var fresh = Creds("charpwd=new-bob");
        await Assert.ThrowsAsync<AccountIdentityMismatchException>(() =>
            accounts.ApplyVerifiedLoginAsync(AccountRole.Main, new KoLPlayerIdentity("200", "Bob"), fresh, allowAccountChange: false));
        var profile = await accounts.GetProfileAsync(AccountRole.Main);
        Assert.Equal("Alice", profile!.DisplayName);
        Assert.Equal("100", profile.PlayerId);
        var session = await accounts.GetSessionAsync(AccountRole.Main);
        Assert.Equal("charpwd=old", session!.Credentials.CookieHeader);
    }

    [Fact]
    public async Task An_explicit_switch_updates_name_player_and_credentials_together()
    {
        var accounts = await ManagerWithAsync(AccountRole.Main, "Alice", "100", "charpwd=old");
        var change = await accounts.ApplyVerifiedLoginAsync(AccountRole.Main, new KoLPlayerIdentity("200", "Bob"),
            Creds("charpwd=new-bob"), allowAccountChange: true);
        Assert.Equal(AccountIdentityChange.Switched, change);
        var session = await accounts.GetSessionAsync(AccountRole.Main);
        Assert.Equal("Bob", session!.Profile.DisplayName);
        Assert.Equal("200", session.Profile.PlayerId);
        Assert.Equal("charpwd=new-bob", session.Credentials.CookieHeader);
    }

    [Fact]
    public async Task A_legacy_profile_without_player_id_adopts_the_id_for_the_same_name_only()
    {
        var accounts = await ManagerWithAsync(AccountRole.Main, "Alice", null, "charpwd=old");
        Assert.Equal(AccountIdentityChange.Adopted, await accounts.ApplyVerifiedLoginAsync(
            AccountRole.Main, new KoLPlayerIdentity("100", "alice"), Creds("charpwd=n"), allowAccountChange: false));
        Assert.Equal("100", (await accounts.GetProfileAsync(AccountRole.Main))!.PlayerId);
        var other = await ManagerWithAsync(AccountRole.Main, "Alice", null, "charpwd=old");
        await Assert.ThrowsAsync<AccountIdentityMismatchException>(() => other.ApplyVerifiedLoginAsync(
            AccountRole.Main, new KoLPlayerIdentity("200", "Bob"), Creds("charpwd=n"), allowAccountChange: false));
    }

    [Fact]
    public async Task Transfer_target_is_verified_against_the_live_session()
    {
        var accounts = await ManagerWithAsync(AccountRole.Main, "Alice", "100", "charpwd=t1");
        var ok = new FakeKol((_, _) => "{\"playerid\":\"100\",\"name\":\"Alice\"}");
        var identity = await AccountIdentityVerifier.VerifyRoleAsync(accounts, AccountRole.Main, innerHandler: ok);
        Assert.Equal("Alice", identity.Name);

        // The session now belongs to somebody else: the stored name must not be used as the recipient.
        var swapped = new FakeKol((_, _) => "{\"playerid\":\"200\",\"name\":\"Bob\"}");
        await Assert.ThrowsAsync<AccountIdentityMismatchException>(
            () => AccountIdentityVerifier.VerifyRoleAsync(accounts, AccountRole.Main, innerHandler: swapped));

        var silent = new FakeKol((_, _) => Status());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => AccountIdentityVerifier.VerifyRoleAsync(accounts, AccountRole.Main, innerHandler: silent));
    }
}
