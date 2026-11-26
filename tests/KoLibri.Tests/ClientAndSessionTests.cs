using System.Reflection;
using Xunit;

namespace KoLibri.Tests;

public sealed class ClientAndSessionTests
{
    [Fact]
    public void Public_surface_lives_in_the_KoLibri_namespaces_only()
    {
        var lib = typeof(KolibriClient).Assembly.GetExportedTypes();
        Assert.All(lib, t => Assert.Equal("KoLibri", t.Namespace));
        Assert.Equal(108, lib.Length);
        var safety = typeof(KoLibri.Safety.CompositeGuard).Assembly.GetExportedTypes();
        Assert.All(safety, t => Assert.Equal("KoLibri.Safety", t.Namespace));
        Assert.Equal(13, safety.Length);
        // The transition assembly is not referenced by this test project: Core stays out of reach at compile time.
        Assert.DoesNotContain(typeof(ClientAndSessionTests).Assembly.GetReferencedAssemblies(), a => a.Name == "KoLibri.Core");
    }

    [Fact]
    public async Task Login_stores_the_account_with_the_verified_player()
    {
        var kol = new FakeKol()
            .On("GET login.php", "<html><body><form action=\"login.php\"><input name=loginname></form>Welcome</body></html>")
            .On("POST login.php", (_, form) =>
            {
                Assert.Contains("loginname=Tester", form);
                var response = FakeKol.Redirect("https://www.kingdomofloathing.com/main.php");
                response.Headers.Add("Set-Cookie", "PHPSESSID=fresh; path=/");
                return response;
            })
            .On("GET main.php", "<html><body>The Kingdom</body></html>");
        var store = new MemoryAccountStore();
        await using var client = TestClient.Create(kol, store);

        await using var session = await client.LoginAsync("Tester", "secret", new LoginOptions { AccountId = "main", RememberPassword = true });

        Assert.Equal("main", session.Account.Id.Value);
        Assert.Equal(1001, session.Account.PlayerId);
        var record = store.Records["main"];
        Assert.Equal("PHPSESSID=fresh", record.CookieHeader);
        Assert.Equal("secret", record.Password);
        Assert.Contains(kol.Requests, r => r.Path.StartsWith("/api.php?what=status", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Login_refuses_a_different_player_for_a_known_account()
    {
        var kol = new FakeKol()
            .On("GET login.php", "<html><body>login <input name=loginname></body></html>")
            .On("POST login.php", (_, _) =>
            {
                var response = FakeKol.Redirect("https://www.kingdomofloathing.com/main.php");
                response.Headers.Add("Set-Cookie", "PHPSESSID=other; path=/");
                return response;
            })
            .On("GET main.php", "<html><body>main</body></html>")
            .On("GET api.php?what=status", """{"name":"Someone Else","playerid":"2002","pwd":"0123456789abcdef"}""");
        var store = MemoryAccountStore.WithMain();
        await using var client = TestClient.Create(kol, store);

        var error = await Assert.ThrowsAsync<KolibriException>(() => client.LoginAsync("Someone Else", "pw", new LoginOptions { AccountId = "main" }));

        Assert.Contains("Nothing was saved", error.Message);
        Assert.Equal("PHPSESSID=abc", store.Records["main"].CookieHeader);
    }

    [Fact]
    public async Task Open_finds_by_id_or_player_name_and_rejects_unknown_accounts()
    {
        var kol = new FakeKol();
        await using var client = TestClient.Create(kol);
        await using var byName = await client.OpenAsync("tester");
        Assert.Equal("main", byName.Account.Id.Value);
        await Assert.ThrowsAsync<KolibriException>(() => client.OpenAsync("nobody"));
        Assert.Equal("main", (await client.Accounts.FindAsync("MAIN"))!.Id.Value);
    }

    [Fact]
    public async Task Status_and_inventory_map_to_the_public_records()
    {
        var kol = new FakeKol { Fullness = 4 }.On("GET api.php?what=inventory", """{"2":3,"3":1}""");
        await using var client = TestClient.Create(kol);
        await using var session = await client.OpenAsync("main");

        var status = await session.Character.GetStatusAsync(refresh: true);
        var inventory = await session.Inventory.GetAsync(refresh: true);
        var equipment = await session.Inventory.GetEquipmentAsync();

        Assert.Equal("Tester", status.PlayerName);
        Assert.Equal(50, status.Adventures);
        Assert.Equal(new OrganState(4, 15), status.Fullness);
        Assert.Equal(3, inventory.Count(2));
        Assert.Equal("seal tooth", inventory.Items.Single(i => i.Item.Id == 2).Name);
        Assert.Equal(new ItemRef(3), equipment.Slots[EquipSlot.Hat]);
        Assert.Null(equipment.Slots[EquipSlot.Weapon]);
    }

    [Fact]
    public async Task Request_events_carry_a_redacted_path_and_handler_failures_do_not_break_the_call()
    {
        var kol = new FakeKol().On("POST desc.php", "<html><body>ok</body></html>");
        await using var client = TestClient.Create(kol);
        await using var session = await client.OpenAsync("main");
        var paths = new List<string>();
        var failures = new List<string>();
        client.Events.Request += (_, e) => paths.Add(e.Path);
        session.Events.Request += (_, _) => throw new InvalidOperationException("boom");
        session.Events.HandlerFailed += (_, e) => failures.Add(e.EventName);

        await session.GetPageAsync("charpane.php", new Dictionary<string, string> { ["pwd"] = "0123456789abcdef", ["x"] = "1" });

        Assert.Contains(paths, p => p.Contains("pwd=***", StringComparison.Ordinal) && !p.Contains("0123456789abcdef", StringComparison.Ordinal));
        Assert.Contains("Request", failures);
    }

    [Fact]
    public async Task Raw_post_adds_pwd_and_goes_through_the_guard()
    {
        var kol = new FakeKol().On("POST closet.php", "<html><body>done</body></html>");
        var guard = new RecordingGuard();
        await using var client = TestClient.Create(kol, guard: guard);
        await using var session = await client.OpenAsync("main");

        var page = await session.PostAsync("closet.php", new Dictionary<string, string> { ["action"] = "put" });

        Assert.Equal(PageKind.Normal, page.Kind);
        Assert.Contains(kol.Requests, r => r.Method == "POST" && r.Form.Contains("pwd=0123456789abcdef"));
        Assert.Equal(GuardedActionKind.RawPost, Assert.Single(guard.Checked).Kind);
        Assert.True(Assert.Single(guard.Completed).Succeeded);
    }

    [Fact]
    public async Task A_denied_action_sends_nothing()
    {
        var kol = new FakeKol();
        var guard = new RecordingGuard { Decide = _ => GuardDecision.Deny("not today") };
        await using var client = TestClient.Create(kol, guard: guard);
        await using var session = await client.OpenAsync("main");

        var denied = await Assert.ThrowsAsync<ActionDeniedException>(() => session.Adventure.AdventureAsync(new Location(1)));

        Assert.Equal("not today", denied.Reason);
        Assert.Equal(0, kol.Count("POST", "adventure.php"));
    }

    [Fact]
    public async Task A_login_page_on_an_action_is_never_repeated()
    {
        var kol = new FakeKol().On("POST adventure.php", (_, _) => FakeKol.Redirect("https://www.kingdomofloathing.com/login.php"));
        await using var client = TestClient.Create(kol, MemoryAccountStore.WithMain(password: "pw"));
        await using var session = await client.OpenAsync("main");

        await Assert.ThrowsAsync<SessionExpiredException>(() => session.Adventure.AdventureAsync(new Location(1)));

        Assert.Equal(1, kol.Count("POST", "adventure.php"));
        Assert.Equal(0, kol.Count("POST", "login.php"));
        Assert.True(session.IsExpired);
    }

    [Fact]
    public async Task Maintenance_is_an_unexpected_page()
    {
        var kol = new FakeKol().On("GET main.php", "<html><body>The game is down for nightly maintenance.</body></html>");
        await using var client = TestClient.Create(kol);
        await using var session = await client.OpenAsync("main");

        var error = await Assert.ThrowsAsync<UnexpectedPageException>(() => session.GetPageAsync("main.php", lane: RequestLane.Read));
        Assert.Equal(PageKind.Maintenance, error.Page.Kind);
    }

    [Fact]
    public void Game_data_comes_from_the_bundled_kolmafia_files()
    {
        var client = KolibriClient.Create(new KolibriOptions { AccountStore = new MemoryAccountStore() });
        Assert.Equal("seal tooth", client.Data.Item(2)!.Name);
        Assert.Equal(ItemUse.Equipment, client.Data.FindItem("helmet turtle")!.Use);
        Assert.Equal(EquipSlot.Hat, client.Data.FindItem("helmet turtle")!.Slot);
        Assert.NotNull(client.Data.Skill(2));
    }

    [Fact]
    public async Task Daily_markers_are_per_account_and_kol_day()
    {
        var state = Directory.CreateTempSubdirectory("kolibri-daily-").FullName;
        await using var client = TestClient.Create(new FakeKol(), state: state);
        await using var session = await client.OpenAsync("main");

        Assert.False(session.Daily.IsDone("campfire-leaf"));
        session.Daily.MarkDone("campfire-leaf");
        Assert.True(session.Daily.IsDone("campfire-leaf"));
        Assert.Equal(new[] { "campfire-leaf" }, session.Daily.DoneToday());
        Assert.True(File.Exists(Path.Combine(state, "daily-done.json")));
    }

    [Fact]
    public async Task Default_store_keeps_the_0_4_ids_main_and_mallbot()
    {
        var state = Directory.CreateTempSubdirectory("kolibri-store-").FullName;
        var kol = new FakeKol()
            .On("GET login.php", "<html><body>login <input name=loginname></body></html>")
            .On("POST login.php", (_, _) =>
            {
                var response = FakeKol.Redirect("https://www.kingdomofloathing.com/main.php");
                response.Headers.Add("Set-Cookie", "PHPSESSID=s1; path=/");
                return response;
            })
            .On("GET main.php", "<html><body>main</body></html>");
        await using var client = KolibriClient.Create(new KolibriOptions { HttpHandler = kol, StateDirectory = state, DisableLimiterForTests = true });

        await using (await client.LoginAsync("Tester", "pw", new LoginOptions { AccountId = "main", Label = "Hauptkonto" })) { }
        var listed = Assert.Single(await client.Accounts.ListAsync());
        await Assert.ThrowsAsync<KolibriException>(() => client.LoginAsync("Tester", "pw", new LoginOptions { AccountId = "alt-2" }));

        Assert.Equal("main", listed.Id.Value);
        Assert.Equal("Hauptkonto", listed.Label);
        Assert.True(listed.HasSession);
        Assert.True(File.Exists(Path.Combine(state, "accounts.json")));
        await client.Accounts.LogoutAsync(listed.Id);
        Assert.False(Assert.Single(await client.Accounts.ListAsync()).HasSession);
    }
}
