using KoLibri.Daily;
using KoLibri.Desktop;
using Xunit;

namespace KoLibri.Tests.Desktop;

public sealed class DailyStepBoardTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 11, 22);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kolibri-daily-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static DesktopDailySnapshot Live(int bones = 1621, int eggs = 3, int clovers = 0, int adventures = 40)
    {
        var items = new List<InventoryItem>
        {
            new(12051, "knucklebone", bones), new(995, "Lucky Surprise Egg", eggs),
        };
        return new DesktopDailySnapshot
        {
            Role = AccountRole.Main,
            Status = new Status("Fixture", 13, 1000, adventures, DateTimeOffset.UtcNow),
            Inventory = new Inventory(items, DateTimeOffset.UtcNow),
            Run = new DailyRunLive { Clovers = clovers, Meat = 1000 }
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
        Assert.Equal((1234, ConsumeKind.Drink, 2), (action.ItemId, action.Kind, action.Quantity));
    }

    [Fact]
    public void Egg_row_uses_item_995_and_is_not_runnable_without_eggs()
    {
        var settings = new DesktopSettings();
        var row = DailyStepBoard.Build(Live(eggs: 3), settings, Today).First(r => r.Kind == DailyStepKind.LuckyEgg);
        var action = Assert.IsType<ConsumeCharacterItem>(Assert.Single(DailyStepBoard.ActionsFor(row, settings, 3)));
        Assert.Equal(995, action.ItemId);
        Assert.Equal(ConsumeKind.Eat, action.Kind);
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
        Assert.StartsWith("Booze: the request to Kingdom of Loathing failed", text);
        Assert.Contains("session expired", DailyStepBoard.ReadableError("Booze",
            new SessionExpiredException(new AccountId("main"))));
        Assert.Contains("refused by the guard", DailyStepBoard.ReadableError("Booze",
            new ActionDeniedException(new GuardedAction(new AccountId("main"), GuardedActionKind.Consume, "drink"), "budget")));
    }

    [Fact]
    public async Task Expired_session_with_relogin_off_shows_the_stale_banner_and_no_steps()
    {
        var kol = new FakeKol().WithLogin();
        var store = DesktopKit.Store(AccountRole.Main, expired: true, password: "saved-password", cookie: "old=1");
        await using var accounts = DesktopKit.Accounts(kol, store, autoReconnect: () => false);
        using var provider = new DesktopLiveDataProvider(accounts);
        Assert.True(await provider.HasExpiredSessionAsync(AccountRole.Main));
        var model = new DailyBriefingViewModel(AccountRole.Main, provider);
        await model.LoadAsync();
        Assert.True(model.SessionStale);
        Assert.Contains("expired", model.SessionBannerText);
        Assert.Empty(model.Steps);
        Assert.Empty(kol.Requests);
        Assert.Equal("old=1", store.Records["main"].CookieHeader);
    }

    [Fact]
    public async Task No_stored_session_is_not_reported_as_expired()
    {
        var kol = new FakeKol();
        await using var accounts = DesktopKit.Accounts(kol);
        using var provider = new DesktopLiveDataProvider(accounts);
        Assert.False(await provider.HasExpiredSessionAsync(AccountRole.Main));
        var model = new DailyBriefingViewModel(AccountRole.Main, provider);
        await model.LoadAsync();
        Assert.False(model.SessionStale);
        Assert.Empty(model.Steps);
    }

    [Fact]
    public async Task Signed_in_briefing_reads_the_account_and_builds_the_steps()
    {
        var kol = new FakeKol { StatusAdventures = 30 }
            .On("GET api.php?what=inventory", """{"12051":40,"995":2,"10881":1}""");
        await using var accounts = DesktopKit.Accounts(kol, DesktopKit.Store(AccountRole.Main));
        using var provider = new DesktopLiveDataProvider(accounts);
        var model = new DailyBriefingViewModel(AccountRole.Main, provider, () => new DesktopSettings(), () => Today);
        await model.LoadAsync();
        Assert.False(model.SessionStale);
        Assert.Equal(2, model.EggsOwned);
        Assert.Contains("30 adventures", model.SnapshotText);
        Assert.Contains("1 Clovers", model.SnapshotText);
        var adventure = model.Steps.First(r => r.Kind == DailyStepKind.Adventures);
        Assert.Equal("open", adventure.Status);
        Assert.Contains("40 / 100", adventure.Detail);
        Assert.Equal("done", model.Steps.First(r => r.Kind == DailyStepKind.CloverCheck).Status);
        Assert.Equal(0, kol.Count("POST", "inv_eat.php"));
    }

    [Fact]
    public void Adventure_planner_copy_plans_eggs_then_turns_and_reports_the_target()
    {
        var plan = new DailyAdventurePlanner().Plan(new AdventureState { Knucklebones = 40, AdventureTurnsRemaining = 200, LuckySurpriseEggs = 2 },
            Today, DailyStepBoard.PlannerOptions(new DesktopSettings()));
        Assert.Equal(DailyAdventurePlanStatus.Ready, plan.Status);
        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal(120, plan.Steps[1].Quantity);   // (100 - 40) / 0.5
        Assert.Equal(DailyAdventurePlanStatus.BlockedStaleSnapshot,
            new DailyAdventurePlanner().Plan(new AdventureState { IsStale = true }).Status);
    }
}
