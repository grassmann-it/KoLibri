using Avalonia.Input;
using KoLibri.Desktop;
using Xunit;

namespace KoLibri.Tests.Desktop;

/// <summary>Package W-F: startup role, shortcut catalog, settings presentation, mall sort/filter, session log, charpane labels.</summary>
public sealed class DesktopUxPackageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kolibri-ux-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
    private static readonly DateTimeOffset Now = new(2026, 3, 4, 18, 0, 0, TimeSpan.Zero);

    // ---- startup role ----
    [Theory]
    [InlineData("Main", "Mallbot", AccountRole.Main)]       // configured default wins
    [InlineData(null, "Mallbot", AccountRole.Mallbot)]      // then the last used
    [InlineData(null, null, AccountRole.Main)]              // nothing known: Main, not Mallbot
    [InlineData("nonsense", "7", AccountRole.Main)]
    public void Startup_role_order_is_default_then_last_used_then_main(string? def, string? last, AccountRole expected)
        => Assert.Equal(expected, StartupRole.Resolve(def, last, null));

    [Fact]
    public void Startup_role_takes_the_only_configured_account_when_nothing_else_is_set()
    {
        Assert.Equal(AccountRole.Mallbot, StartupRole.Resolve(null, null, [AccountRole.Mallbot]));
        Assert.Equal(AccountRole.Main, StartupRole.Resolve(null, null, [AccountRole.Main, AccountRole.Mallbot]));
        Assert.False(StartupRole.IsSettled(null, null));
        Assert.True(StartupRole.IsSettled(null, "Main"));
    }

    [Fact]
    public void Last_used_role_is_kept_next_to_the_settings_and_survives_a_settings_save()
    {
        var store = new DesktopSettingsStore(Path.Combine(_dir, "desktop-settings.json"));
        StartupRole.WriteLast(store, AccountRole.Mallbot);
        store.Save(new DesktopSettings());
        Assert.Equal("Mallbot", StartupRole.ReadLast(store));
    }

    // ---- shortcut catalog ----
    [Fact]
    public void Shortcut_catalog_has_no_duplicate_gestures_and_uses_cmd_on_mac()
    {
        foreach (var mac in new[] { true, false })
        {
            var shown = DesktopShortcuts.All.Select(s => s.Display(mac)).ToArray();
            Assert.Equal(shown.Length, shown.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        Assert.Equal("Cmd+1", DesktopShortcuts.All.First(s => s.Command == DesktopCommand.OpenAdventures).Display(true));
        Assert.Equal("Ctrl+1", DesktopShortcuts.All.First(s => s.Command == DesktopCommand.OpenAdventures).Display(false));
        Assert.Equal(DesktopCommand.OpenSettings, DesktopShortcuts.Find(Key.OemComma, KeyModifiers.Meta, isMac: true)?.Command);
        Assert.Equal(DesktopCommand.CancelLoad, DesktopShortcuts.Find(Key.OemPeriod, KeyModifiers.Meta, isMac: true)?.Command);
        Assert.Null(DesktopShortcuts.Find(Key.D1, KeyModifiers.Control, isMac: true));   // Ctrl+digit belongs to macOS Spaces
        Assert.Equal(DesktopCommand.Help, DesktopShortcuts.Find(Key.F1, KeyModifiers.None, isMac: false)?.Command);
        Assert.Equal(DesktopCommand.Help, DesktopShortcuts.Find(Key.OemQuestion, KeyModifiers.Meta, isMac: true)?.Command);
    }

    [Fact]
    public void Shortcut_catalog_avoids_keys_macos_reserves()
    {
        var reserved = new[] { Key.Q, Key.H, Key.M, Key.W, Key.Space, Key.Tab };
        Assert.DoesNotContain(DesktopShortcuts.All, s => s.NeedsCommandKey && s.Keys.Any(reserved.Contains));
    }

    [Fact]
    public void Other_catalogs_read_the_same_list_and_Ctrl_Escape_is_gone()
    {
        var gestures = KoLibriUxCatalog.Default.Shortcuts.Select(s => s.Gesture).ToArray();
        Assert.DoesNotContain("Ctrl+Escape", gestures);
        foreach (var s in DesktopShortcuts.All)
            Assert.Contains(s.Display(OperatingSystem.IsMacOS()), gestures);
    }

    // ---- settings presentation ----
    [Fact]
    public void Settings_default_account_is_a_choice_and_clover_threshold_is_editable_and_clamped()
    {
        var vm = new SettingsViewModel(new DesktopSettingsStore(Path.Combine(_dir, "s.json")));
        Assert.Equal(SettingsViewModel.LastUsedChoice, vm.DefaultAccountChoice);
        vm.DefaultAccountChoice = "Main";
        Assert.Equal("Main", vm.Settings.DefaultAccount);
        vm.DefaultAccountChoice = SettingsViewModel.LastUsedChoice;
        Assert.Null(vm.Settings.DefaultAccount);
        vm.CloverThreshold = 5;
        Assert.Equal(vm.CloverThresholdMin, vm.CloverThreshold);
        vm.CloverThreshold = 30_000;
        vm.Save.Execute(null);
        Assert.Equal(30_000, new DesktopSettingsStore(Path.Combine(_dir, "s.json")).Load().Get("cloverThreshold"));
        vm.ResetDefaults.Execute(null);
        Assert.Equal(27_000, vm.CloverThreshold);
        Assert.Equal(250_000, vm.MeatPerPurchase);
        Assert.Equal(500_000, vm.MeatPerDay);
        Assert.Equal(10, vm.UnitsPerPurchase);
    }

    [Fact]
    public void Header_theme_change_and_settings_theme_are_one_value()
    {
        var vm = new SettingsViewModel(new DesktopSettingsStore(Path.Combine(_dir, "t.json")));
        vm.SetTheme("Dark");
        Assert.Equal("Dark", vm.Theme);
    }

    // ---- session log ----
    [Fact]
    public void Log_entries_carry_the_role_never_merge_and_filter_by_level_and_role()
    {
        var role = "Main";
        var log = new SessionLogViewModel(now: () => Now) { RoleProvider = () => role };
        log.Purchase("Bought 1 thing");
        log.Purchase("Bought 1 thing");
        role = "Mallbot";
        log.Error("boom");
        log.Search("looking");
        Assert.Equal(4, log.Entries.Count);
        Assert.Equal("Main", log.Entries[0].Role);
        Assert.Contains("Mallbot", log.Entries[2].Text);

        log.RoleFilter = "Main";
        Assert.Equal(2, log.Visible.Count);
        log.RoleFilter = SessionLogViewModel.AllRoles;
        log.Level = SessionLogLevelFilter.ErrorsOnly;
        Assert.Equal("boom", log.Visible.Single().Message);
        Assert.Equal("1 of 4 shown", log.FilterSummary);
    }

    [Fact]
    public void Log_copy_and_save_use_the_same_selection_and_hide_credentials()
    {
        var log = new SessionLogViewModel(now: () => Now, directory: _dir) { RoleProvider = () => "Main" };
        log.Info("login ok password=hunter2 cookie: PHPSESSID=abc123; x");
        log.Error("failed pwd=s3cret&phash=deadbeef");
        log.Level = SessionLogLevelFilter.ErrorsOnly;
        var copied = log.ToText();
        var path = log.SaveSelection();
        Assert.NotNull(path);
        Assert.Equal(copied + Environment.NewLine, File.ReadAllText(path!));
        var everything = File.ReadAllText(path!) + string.Join("", Directory.GetFiles(_dir).Select(File.ReadAllText));
        Assert.DoesNotContain("hunter2", everything);
        Assert.DoesNotContain("s3cret", everything);
        Assert.DoesNotContain("deadbeef", everything);
        Assert.DoesNotContain("abc123", everything);
    }

    [Fact]
    public void Log_day_file_is_per_kol_day_and_reloads_after_a_restart()
    {
        var log = new SessionLogViewModel(now: () => Now, directory: _dir) { RoleProvider = () => "Mallbot" };
        log.Adventure("turn 1");
        log.Adventure("turn 1");
        var file = Assert.Single(Directory.GetFiles(_dir, "session-log-*.jsonl"));
        Assert.Contains(SessionLogViewModel.KolDay(Now), file);

        var restarted = new SessionLogViewModel(now: () => Now, directory: _dir);
        Assert.Equal(2, restarted.LoadToday());
        Assert.Equal(2, restarted.Entries.Count);
        Assert.All(restarted.Entries, e => Assert.Equal("Mallbot", e.Role));
        Assert.Single(Directory.GetFiles(_dir, "session-log-*.jsonl"));   // loading does not write again

        var nextDay = new SessionLogViewModel(now: () => Now.AddDays(1), directory: _dir);
        Assert.Equal(0, nextDay.LoadToday());
    }

    // ---- charpane ----
    [Fact]
    public void Charpane_shows_reported_organ_limits_and_familiar_name_and_marks_assumptions()
    {
        var reported = new Status("T", 1, 1, 1, Now)
        {
            Fullness = new global::KoLibri.OrganState(4, 20), Inebriety = new global::KoLibri.OrganState(2, 19),
            Spleen = new global::KoLibri.OrganState(1, 25), FamiliarId = 9, FamiliarName = "Fluffy", FamiliarWeight = 30
        };
        var s = CharpaneState.FromStatus(reported, "Main", Now);
        Assert.Equal("Fullness 4 / 20", s.Fullness);
        Assert.Equal("Drunkenness 2 / 19", s.Drunk);
        Assert.Equal("Spleen 1 / 25", s.Spleen);
        Assert.Equal("Fluffy · 30 lb", s.Familiar);
        var plain = CharpaneState.FromStatus(new Status("T", 1, 1, 1, Now) { Fullness = new global::KoLibri.OrganState(4, null), FamiliarId = 9 }, "Main", Now);
        Assert.Equal("Fullness 4 / ?", plain.Fullness);
        Assert.Equal("Familiar #9", plain.Familiar);
    }

    // ---- mall sort and filter ----
    private sealed class OnlyShop2 : IMallPurchaseRequest
    {
        public bool CanRequest(MallOfferRow o) => o.ShopId == "2";
        public Task RequestAsync(MallOfferRow o, CancellationToken ct) => Task.CompletedTask;
    }

    private static MallListing L(string shop, long price, int qty) =>
        new(int.Parse(shop), $"Shop {shop}", 10881, price, qty, null);

    [Fact]
    public async Task Mall_offers_sort_by_column_flip_on_second_click_and_filter_to_buyable()
    {
        var page = new global::KoLibri.MallSearchResult("x", [new MallOffers(10881, "11-leaf clover", [L("1", 25_000, 5), L("2", 29_000, 900), L("3", 30_000, 40)], DateTimeOffset.UtcNow)],
            DateTimeOffset.UtcNow);
        var backend = new MallBackend(page);
        var vm = new MallSearchViewModel(backend, t => t == "11-leaf clover" || t == "10881" ? [("10881", "11-leaf clover")] : []) { SearchText = "10881", PurchaseRequest = new OnlyShop2() };
        await vm.SearchAsync();
        Assert.Equal(["1", "2", "3"], vm.Offers.Select(o => o.ShopId));

        vm.SortBy(MallOfferSort.Stock);
        Assert.Equal(["1", "3", "2"], vm.Offers.Select(o => o.ShopId));
        Assert.Equal("Stock ▲", vm.StockHeader);
        vm.SortBy(MallOfferSort.Stock);
        Assert.Equal(["2", "3", "1"], vm.Offers.Select(o => o.ShopId));
        Assert.Equal("Stock ▼", vm.StockHeader);

        vm.OnlyBuyable = true;
        Assert.Equal("2", vm.Offers.Single().ShopId);
        Assert.Equal(2, vm.HiddenOfferCount);
        Assert.True(vm.HasOffers);
        vm.OnlyBuyable = false;
        Assert.Equal(3, vm.Offers.Count);
    }

    private sealed class MallBackend(global::KoLibri.MallSearchResult page) : IMallSearchBackend
    {
        public Task<global::KoLibri.MallSearchResult> SearchAsync(string term, bool force, CancellationToken ct) => Task.FromResult(page);
        public Task<PricegunInfo> LoadPricegunAsync(string itemId, CancellationToken ct)
            => Task.FromResult(new PricegunInfo(26_000, DateTimeOffset.UtcNow, false));
    }
}
