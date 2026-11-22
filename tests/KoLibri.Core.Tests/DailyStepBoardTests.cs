using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;
using KoLibri.Desktop;
using Xunit;

namespace KoLibri.Core.Tests;

public sealed class DailyStepBoardTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 11, 22);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kolibri-daily-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static LiveDailySnapshot Live(int bones = 1621, int eggs = 3, int clovers = 0, int adventures = 40, bool stale = false)
    {
        var items = new List<KoLInventoryItem>
        {
            new("12051", "knucklebone", bones), new("995", "Lucky Surprise Egg", eggs),
        };
        return new LiveDailySnapshot
        {
            Snapshot = new DailyRoutineSnapshot(),
            State = new AccountDailyState
            {
                Role = AccountRole.Main,
                Status = new KoLStatusSnapshot("Fixture", adventures, 1000, 13, DateTimeOffset.UtcNow, stale),
                Inventory = new KoLInventorySnapshot(items, DateTimeOffset.UtcNow, stale),
                ObservedAt = DateTimeOffset.UtcNow
            },
            Leaves = 0, Clovers = clovers, Permits = 0, WorthlessItems = 0, Bacon = 0, BaconMachines = 0,
            Meat = 1000, Adventures = adventures, Level = 13, ClassId = "1"
        };
    }

    [Fact]
    public void Target_default_is_100_and_clamped_to_0_and_10000()
    {
        var s = new DesktopSettings();
        Assert.Equal(100, s.Get("dailyKnucklebonesTarget"));
        s.Set("dailyKnucklebonesTarget", 99_999);
        Assert.Equal(10_000, s.Get("dailyKnucklebonesTarget"));
        s.Set("dailyKnucklebonesTarget", -5);
        Assert.Equal(0, s.Get("dailyKnucklebonesTarget"));
    }

    [Fact]
    public void Adventure_row_is_done_when_the_target_is_met_and_open_below_it()
    {
        var settings = new DesktopSettings();
        var done = DailyStepBoard.Build(Live(bones: 1621), settings, Today).First(r => r.Kind == DailyStepKind.Adventures);
        Assert.Equal("done", done.Status);
        Assert.Contains("1,621 / 100", done.Detail);

        settings.Set("dailyKnucklebonesTarget", 5000);
        var open = DailyStepBoard.Build(Live(bones: 1621), settings, Today).First(r => r.Kind == DailyStepKind.Adventures);
        Assert.Equal("open", open.Status);
        Assert.Contains("1,621 / 5,000", open.Detail);
    }

    [Fact]
    public void Booze_and_spleen_are_blocked_until_the_user_picks_items()
    {
        var settings = new DesktopSettings();
        var rows = DailyStepBoard.Build(Live(), settings, Today);
        Assert.All(new[] { DailyStepKind.Booze, DailyStepKind.Spleen }, k =>
        {
            var row = rows.First(r => r.Kind == k);
            Assert.False(row.CanRun);
            Assert.Equal("blocked", row.Status);
            Assert.Empty(DailyStepBoard.ActionsFor(row, settings, 0));
        });

        Assert.True(DesktopSettings.AddConsumable(settings.BoozeItems, 1234, "Test Booze", 2));
        var booze = DailyStepBoard.Build(Live(), settings, Today).First(r => r.Kind == DailyStepKind.Booze);
        Assert.True(booze.CanRun);
        var action = Assert.IsType<ConsumeCharacterItem>(Assert.Single(DailyStepBoard.ActionsFor(booze, settings, 0)));
        Assert.Equal((1234, CharacterConsumableKind.Drink, 2), (action.ItemId, action.Kind, action.Quantity));
    }

    [Fact]
    public void Egg_row_uses_item_995_and_is_not_runnable_without_eggs()
    {
        var settings = new DesktopSettings();
        var row = DailyStepBoard.Build(Live(eggs: 3), settings, Today).First(r => r.Kind == DailyStepKind.LuckyEgg);
        var action = Assert.IsType<ConsumeCharacterItem>(Assert.Single(DailyStepBoard.ActionsFor(row, settings, 3)));
        Assert.Equal(995, action.ItemId);
        Assert.Equal(CharacterConsumableKind.Food, action.Kind);
        Assert.Equal(3, action.Quantity);
        Assert.False(DailyStepBoard.Build(Live(eggs: 0), settings, Today).First(r => r.Kind == DailyStepKind.LuckyEgg).CanRun);
    }

    [Fact]
    public void Rests_follow_the_setting_and_zero_blocks_the_step()
    {
        var settings = new DesktopSettings();
        settings.Set("dailyRests", 3);
        var row = DailyStepBoard.Build(Live(), settings, Today).First(r => r.Kind == DailyStepKind.FreeRests);
        Assert.Equal(3, DailyStepBoard.ActionsFor(row, settings, 0).Count);
        settings.Set("dailyRests", 0);
        Assert.False(DailyStepBoard.Build(Live(), settings, Today).First(r => r.Kind == DailyStepKind.FreeRests).CanRun);
    }

    [Fact]
    public void Skip_lasts_one_day_and_is_per_account()
    {
        var settings = new DesktopSettings();
        settings.SkipToday("Main", "free-rests", Today);
        Assert.Equal("skipped", DailyStepBoard.Build(Live(), settings, Today).First(r => r.Kind == DailyStepKind.FreeRests).Status);
        Assert.NotEqual("skipped", DailyStepBoard.Build(Live(), settings, Today.AddDays(1)).First(r => r.Kind == DailyStepKind.FreeRests).Status);
        Assert.False(settings.IsSkipped("Mallbot", "free-rests", Today));
        settings.UnskipToday("Main", "free-rests");
        Assert.False(settings.IsSkipped("Main", "free-rests", Today));
    }

    [Fact]
    public void Consumable_lists_and_skips_survive_a_save_and_load()
    {
        var store = new DesktopSettingsStore(Path.Combine(_dir, "s.json"));
        var s = new DesktopSettings();
        DesktopSettings.AddConsumable(s.BoozeItems, 11, "Booze A", 3);
        DesktopSettings.AddConsumable(s.SpleenItems, 22, "Spleen B", 1);
        s.SkipToday("Main", "booze", Today);
        s.Set("dailyKnucklebonesTarget", 250);
        store.Save(s);
        var back = store.Load();
        Assert.Equal(new DailyConsumable(11, "Booze A", 3), Assert.Single(back.BoozeItems));
        Assert.Equal(new DailyConsumable(22, "Spleen B", 1), Assert.Single(back.SpleenItems));
        Assert.True(back.IsSkipped("Main", "booze", Today));
        Assert.Equal(250, back.Get("dailyKnucklebonesTarget"));
    }

    [Fact]
    public void Consumable_list_merges_duplicates_clamps_quantity_and_caps_entries()
    {
        var list = new List<DailyConsumable>();
        Assert.True(DesktopSettings.AddConsumable(list, 5, "X", 500));
        Assert.Equal(DesktopSettings.MaxConsumableQuantity, list[0].Quantity);
        Assert.True(DesktopSettings.AddConsumable(list, 5, "X", 2));
        Assert.Single(list);
        Assert.False(DesktopSettings.AddConsumable(list, 0, "bad", 1));
        for (var i = 100; list.Count < DesktopSettings.MaxConsumableEntries; i++) DesktopSettings.AddConsumable(list, i, "i", 1);
        Assert.False(DesktopSettings.AddConsumable(list, 9999, "overflow", 1));
    }

    [Fact]
    public void Editor_resolves_names_through_the_lookup_and_explains_failures()
    {
        var list = new List<DailyConsumable>();
        var editor = new ConsumableListEditor(list, text => text == "Known" ? [("77", "Known")] : text == "Twin" ? [("1", "Twin"), ("2", "Twin")] : []);
        editor.Item = "Known"; editor.Add.Execute(null);
        Assert.Equal(77, Assert.Single(list).ItemId);
        editor.Item = "Twin"; editor.Add.Execute(null);
        Assert.Contains("ambiguous", editor.Message);
        editor.Item = "Nothing"; editor.Add.Execute(null);
        Assert.Contains("No item named", editor.Message);
        Assert.Single(list);
    }

    [Fact]
    public void Errors_are_one_readable_sentence()
    {
        var text = DailyStepBoard.ReadableError("Booze", new HttpRequestException("boom"));
        Assert.StartsWith("Booze: could not reach Kingdom of Loathing", text);
        Assert.Contains("session expired", DailyStepBoard.ReadableError("Booze",
            new KoLHtmlResponseException(KoLHtmlResponseKind.LoginPage, "login page")));
    }

    [Fact]
    public async Task Expired_session_with_relogin_off_shows_the_stale_banner_and_no_steps()
    {
        var accounts = new AccountSessionManager(new InMemoryAccountStore(), new InMemorySecretStore());
        await accounts.SetProfileAsync(new AccountProfile { Id = "main", DisplayName = "Fixture", Role = AccountRole.Main });
        await accounts.SetCredentialsAsync(AccountRole.Main, new AccountCredentials
        {
            Username = "Fixture", Password = "saved-password", CookieHeader = "old=1",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1)
        });
        using var provider = new DesktopLiveDataProvider(accounts, reloginAutomatically: () => false);
        Assert.True(await provider.HasExpiredSessionAsync(AccountRole.Main));
        var model = new DailyBriefingViewModel(AccountRole.Main, provider);
        await model.LoadAsync();
        Assert.True(model.SessionStale);
        Assert.Contains("expired", model.SessionBannerText);
        Assert.Empty(model.Steps);
        Assert.Equal("old=1", (await accounts.GetSessionAsync(AccountRole.Main, includeExpired: true))!.Credentials.CookieHeader);
    }

    [Fact]
    public async Task No_stored_session_is_not_reported_as_expired()
    {
        using var provider = new DesktopLiveDataProvider(new AccountSessionManager(new InMemoryAccountStore(), new InMemorySecretStore()));
        Assert.False(await provider.HasExpiredSessionAsync(AccountRole.Main));
        var model = new DailyBriefingViewModel(AccountRole.Main, provider);
        await model.LoadAsync();
        Assert.False(model.SessionStale);
    }
}
