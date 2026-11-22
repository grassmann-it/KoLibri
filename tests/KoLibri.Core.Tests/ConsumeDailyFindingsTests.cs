using KoLibri.Core.Basis;
using KoLibri.Core.Markt;
using KoLibri.Core.Spiel;
using KoLibri.Desktop;
using KoLibri.Desktop.ContextMenus;
using Xunit;

namespace KoLibri.Core.Tests;

/// <summary>Package W-C: consume, inventory and daily findings (view-model and core level, fake delegates).</summary>
public sealed class ConsumeDailyFindingsTests
{
    private static readonly KoLmafiaCatalog Catalog =
        new MafiaDataLoader().Load(Path.Combine(AppContext.BaseDirectory, "external", "kolmafia-data"));
    private static readonly ItemClassifier Classifier = new(Catalog);

    // ---- H16 / M1 / M8: classifier and planner ----------------------------------------------------------

    [Fact]
    public void Drunki_bear_costs_fullness_and_inebriety_and_both_limits_are_checked()
    {
        var bear = Classifier.Classify("5482");
        Assert.Contains(bear.OrganCosts, c => c.Organ == ItemOrgan.Food);
        Assert.Contains(bear.OrganCosts, c => c.Organ == ItemOrgan.Drink);
        // Empty stomach, liver at 13/14: only the liver blocks it.
        var check = InventoryActionPlanner.CheckOverfill(bear, 1, new OrganState(0, 13, 0));
        Assert.True(check.Known);
        Assert.True(check.Overfills);
        Assert.Contains("inebriety", check.Message);
        Assert.Equal(0, InventoryActionPlanner.MaxFitting(bear, new OrganState(0, 13, 0)));
    }

    [Fact]
    public void Ice_stein_is_used_but_its_inebriety_cost_is_still_checked()
    {
        var stein = Classifier.Classify("1618");
        Assert.Equal(ItemUseKind.Use, InventoryActionPlanner.UseKindOf(stein));
        Assert.True(InventoryActionPlanner.CheckOverfill(stein, 1, new OrganState(0, 10, 0)).Overfills);
        Assert.False(InventoryActionPlanner.CheckOverfill(stein, 1, new OrganState(0, 0, 0)).Overfills);
    }

    [Fact]
    public void Unknown_organ_state_needs_confirmation_instead_of_passing_silently()
    {
        var check = InventoryActionPlanner.CheckOverfill(Classifier.Classify("471"), 1, OrganState.Unknown);
        Assert.False(check.Known);
        Assert.False(check.Overfills);
        Assert.True(InventoryActionPlanner.NeedsConfirmation(check));
        // Items without any organ cost never ask.
        Assert.False(InventoryActionPlanner.NeedsConfirmation(
            InventoryActionPlanner.CheckOverfill(Classifier.Classify("25"), 5, OrganState.Unknown)));
    }

    [Theory]
    [InlineData("637")]   // cheap toaster
    [InlineData("9025")]  // infinite BACON machine
    public void Reusable_items_get_a_use_action(string id)
    {
        var c = Classifier.Classify(id);
        Assert.True(c.IsReusable);
        Assert.Equal(ItemUseKind.Use, InventoryActionPlanner.UseKindOf(c));
    }

    [Fact]
    public void One_handed_weapons_offer_the_offhand_dual_wield_slot()
    {
        var club = Classifier.Classify("1");
        Assert.Contains(InventoryActionPlanner.DualWieldSlot, InventoryActionPlanner.EquipSlotsOf(club));
        Assert.DoesNotContain(InventoryActionPlanner.DualWieldSlot, InventoryActionPlanner.EquipSlotsOf(Classifier.Classify("306")));
    }

    // ---- H18 / M1: the verdict comes from the numbers read around the action -----------------------------

    [Fact]
    public void Consume_verdict_uses_the_stock_read_before_the_action_not_the_stale_row()
    {
        // UI row said 10, another client ate 4, KoLibri read 6 before and 5 after eating one.
        var ok = InventoryActionPlanner.JudgeConsume("Ate", "Eat", "hot wing", 1, heldBefore: 6, heldAfter: 5, false, null, false);
        Assert.True(ok.Ok);
        Assert.Contains("Ate 1", ok.Message);
        // Stock dropped by 5 although one was requested: unexplained, not a success.
        var odd = InventoryActionPlanner.JudgeConsume("Ate", "Eat", "hot wing", 1, 6, 1, false, null, false);
        Assert.False(odd.Ok);
        Assert.Contains("another client", odd.Message);
        // Nothing changed: not a success.
        Assert.False(InventoryActionPlanner.JudgeConsume("Ate", "Eat", "hot wing", 1, 6, 6, false, null, false).Ok);
    }

    [Fact]
    public void Reusable_item_with_unchanged_stock_is_a_success_and_a_stale_read_is_not()
    {
        Assert.True(InventoryActionPlanner.JudgeConsume("Used", "Use", "cheap toaster", 1, 1, 1, true, null, false).Ok);
        var stale = InventoryActionPlanner.JudgeConsume("Used", "Use", "cheap toaster", 1, 1, 1, true, null, true);
        Assert.False(stale.Ok);
        Assert.Contains("may have happened", stale.Message);
    }

    // ---- H17: settings can never hold the egg as booze -----------------------------------------------------

    [Fact]
    public void Lucky_surprise_egg_cannot_be_saved_as_booze_or_spleen_and_food_is_no_booze()
    {
        var booze = new ConsumableListEditor(new List<DailyConsumable>(), null, Classifier, ItemOrgan.Drink);
        Assert.NotNull(booze.Validate(995));
        Assert.NotNull(booze.Validate(471));   // hot wing is food
        Assert.Null(booze.Validate(41));       // Sir Schlitz is booze
        Assert.NotNull(booze.Validate(999_999_999));
        var spleen = new ConsumableListEditor(new List<DailyConsumable>(), null, Classifier, ItemOrgan.Spleen);
        Assert.NotNull(spleen.Validate(995));
        booze.Item = "995";
        booze.Add.Execute(null);
        Assert.Empty(booze.Items);
        Assert.Contains("food", booze.Message);
    }

    // ---- H17 / H21 / M23: the daily runner -------------------------------------------------------------------

    private static KoLStatusSnapshot Status(int? full = 0, int? drunk = 0, int hp = 50, int mp = 50, int adv = 10)
        => new("Tester", adv, 1000, 10, DateTimeOffset.UnixEpoch) { Fullness = full, Inebriety = drunk, Spleen = 0, CurrentHp = hp, CurrentMp = mp };

    private static KoLInventorySnapshot Inventory(params (int Id, int Qty)[] items)
        => new(items.Select(i => new KoLInventoryItem(i.Id.ToString(), i.Id.ToString(), i.Qty)).ToArray(), DateTimeOffset.UnixEpoch);

    private static CharacterActionSnapshot Snap(KoLStatusSnapshot status, KoLInventorySnapshot inventory, string? rejection = null, string? excerpt = null)
        => new(status, inventory) { RejectionReason = rejection, ResponseExcerpt = excerpt };

    [Fact]
    public async Task Daily_drink_that_would_overfill_asks_and_a_no_skips_it_without_sending()
    {
        var sent = 0;
        var asked = 0;
        var result = await DailyStepRunner.RunAsync(DailyStepKind.Booze,
            [new ConsumeCharacterItem(41, CharacterConsumableKind.Drink, 1)],
            _ => Task.FromResult(Snap(Status(0, 14), Inventory((41, 1)))),
            (_, _) => { sent++; return Task.FromResult(Snap(Status(), Inventory())); },
            Classifier, (_, _) => { asked++; return Task.FromResult(false); }, CancellationToken.None);
        Assert.Equal(1, asked);
        Assert.Equal(0, sent);
        Assert.Equal(DailyRunState.Failed, result.State);
    }

    [Fact]
    public async Task Daily_booze_list_with_the_egg_never_sends_the_egg_as_a_drink()
    {
        var sent = 0;
        var result = await DailyStepRunner.RunAsync(DailyStepKind.Booze,
            [new ConsumeCharacterItem(995, CharacterConsumableKind.Drink, 1)],
            _ => Task.FromResult(Snap(Status(), Inventory((995, 1)))),
            (_, _) => { sent++; return Task.FromResult(Snap(Status(), Inventory())); },
            Classifier, (_, _) => Task.FromResult(true), CancellationToken.None);
        Assert.Equal(0, sent);
        Assert.Contains("not drink", result.Summary);
    }

    [Fact]
    public async Task Daily_step_that_half_worked_is_reported_as_partial()
    {
        var calls = 0;
        var result = await DailyStepRunner.RunAsync(DailyStepKind.Booze,
            [new ConsumeCharacterItem(41, CharacterConsumableKind.Drink, 1), new ConsumeCharacterItem(41, CharacterConsumableKind.Drink, 1)],
            _ => Task.FromResult(Snap(Status(), Inventory((41, 2)))),
            (_, _) => Task.FromResult(++calls == 1
                ? Snap(Status(0, 1), Inventory((41, 1)))
                : Snap(Status(0, 1), Inventory((41, 1)), rejection: "KoL refused the drink because you are too drunk.")),
            Classifier, (_, _) => Task.FromResult(true), CancellationToken.None);
        Assert.Equal(1, result.Done);
        Assert.Equal(DailyRunState.Partial, result.State);
        Assert.StartsWith("Partly done", result.Summary);
    }

    [Fact]
    public async Task Daily_action_whose_follow_up_read_failed_says_it_may_have_happened()
    {
        var result = await DailyStepRunner.RunAsync(DailyStepKind.Booze,
            [new ConsumeCharacterItem(41, CharacterConsumableKind.Drink, 1)],
            _ => Task.FromResult(Snap(Status(), Inventory((41, 1)))),
            (action, _) => throw new CharacterActionOutcomeUnknownException(action, new HttpRequestException("reset")),
            Classifier, (_, _) => Task.FromResult(true), CancellationToken.None);
        Assert.Contains("may have happened", result.Summary);
        Assert.DoesNotContain("Nothing was changed", result.Summary);
    }

    [Fact]
    public void Network_error_text_no_longer_claims_that_nothing_was_changed()
        => Assert.DoesNotContain("Nothing was changed",
            DailyStepBoard.ReadableError("Booze", new HttpRequestException("boom")));

    [Fact]
    public async Task Daily_free_rest_at_full_hp_and_mp_counts_when_kol_answers_with_a_rest()
    {
        var result = await DailyStepRunner.RunAsync(DailyStepKind.FreeRests, [new RestAtCampground()],
            _ => Task.FromResult(Snap(Status(), Inventory())),
            (_, _) => Task.FromResult(Snap(Status(), Inventory(), excerpt: "You rest in your tent.")),
            Classifier, (_, _) => Task.FromResult(true), CancellationToken.None);
        Assert.Equal(1, result.Done);
        Assert.Equal(DailyRunState.Complete, result.State);
    }

    [Fact]
    public void Routine_report_distinguishes_complete_partial_failed_and_nothing()
    {
        AutoCombatDailyBridge.BridgeExecutionResult R(int submitted, params string[] failed) => new()
        {
            Executed = submitted > 0, Steps = [], AllowlistedChoiceIds = [], AuditPath = null,
            FailedSteps = failed, SubmittedSteps = submitted, AttemptedSteps = submitted + failed.Length
        };
        Assert.Contains("only PARTLY", DailyRunReport.Describe(R(1, "hermit:clover (http-500)")));
        Assert.Contains("FAILED", DailyRunReport.Describe(R(0, "hermit:clover (http-500)")));
        Assert.Contains("hermit:clover", DailyRunReport.Describe(R(1, "hermit:clover (http-500)")));
        Assert.Contains("executed", DailyRunReport.Describe(R(2)));
        Assert.Contains("nothing to do", DailyRunReport.Describe(R(0)));
    }

    // ---- H2: BACON top-up budget ----------------------------------------------------------------------------

    [Fact]
    public void Bacon_top_up_buys_nothing_when_meat_per_day_is_zero_and_books_the_spend_otherwise()
    {
        var settings = new DesktopSettings();
        settings.Set("meatPerDay", 0);
        var ledger = new MallSpendLedger();
        Assert.False(DailyBaconBudget.Reserve(settings, ledger, "Main", 5, 1500, 10_000_000, 200_000).Allowed);

        settings.Set("meatPerDay", 100_000);
        settings.Set("meatPerPurchase", 50_000);
        settings.Set("unitsPerPurchase", 4);
        var budget = DailyBaconBudget.Reserve(settings, ledger, "Main", 20, 1500, 10_000_000, 200_000);
        Assert.True(budget.Allowed);
        Assert.Equal(4, budget.Units);                       // capped by units per purchase
        Assert.Equal(6_000, budget.SpendCap);                // 4 x 1500
        Assert.Equal(206_000, budget.MeatToOffer);           // reserve + cap, never the whole purse
        DailyBaconBudget.Finish(ledger, "Main", budget, spent: 3_000, unclear: false);
        Assert.Equal(3_000, ledger.SpentToday("Main"));
    }

    [Fact]
    public void Bacon_top_up_is_refused_when_the_day_limit_is_already_used()
    {
        var settings = new DesktopSettings();
        settings.Set("meatPerDay", 5_000);
        var ledger = new MallSpendLedger();
        ledger.Add("Main", 4_500);
        var budget = DailyBaconBudget.Reserve(settings, ledger, "Main", 5, 1500, 10_000_000, 0);
        Assert.False(budget.Allowed);
        Assert.Contains("budget", budget.Note);
    }

    // ---- M5 / M6 / M7: catalog tags -----------------------------------------------------------------------------

    [Fact]
    public void Cast_picker_uses_catalog_tags_and_keeps_names_that_start_with_a_symbol()
    {
        var names = SkillsEffectsViewModel.FromCatalog(Catalog.Skills).Select(s => s.Name).ToHashSet();
        Assert.DoesNotContain("Liver of Steel", names);          // passive
        Assert.DoesNotContain("Chronic Indigestion", names);     // combat only
        Assert.Contains("%fn, sing a song of my prowess", names);
    }

    [Fact]
    public async Task Auto_combat_lists_all_monsters_combat_items_and_no_passive_skills()
    {
        var vm = new AutoCombatViewModel(Catalog);
        await vm.LoadAsync();
        Assert.Equal(Catalog.Monsters.Count(m => !string.IsNullOrWhiteSpace(m.Name)), vm.Targets.Count);
        Assert.True(vm.Targets.Count > 300);
        Assert.Contains(vm.Items, i => i.Name == "seal tooth");
        Assert.DoesNotContain(vm.Skills, s => s.Name == "Liver of Steel");
        Assert.Contains(vm.Skills, s => s.Name == "Chronic Indigestion");
    }

    // ---- M15 / M14 / M16: menus and selection ----------------------------------------------------------------

    private sealed class Wired(List<string> calls) : IItemMenuActions
    {
        public void Consume(ItemMenuTarget item, ItemUseKind kind, int quantity) { }
        public void Equip(ItemMenuTarget item, string slot) { }
        public bool SupportsCloset => true;
        public void MoveToCloset(ItemMenuTarget item) => calls.Add("closet");
        public bool SupportsInfo => true;
        public void ShowInfo(ItemMenuTarget item) => calls.Add("info");
    }

    private sealed class Bare : IItemMenuActions
    {
        public void Consume(ItemMenuTarget item, ItemUseKind kind, int quantity) { }
        public void Equip(ItemMenuTarget item, string slot) { }
    }

    [Fact]
    public void Closet_and_info_entries_run_the_wired_action_or_are_hidden()
    {
        var target = new ItemMenuTarget("471", "hot wing", 3, ItemUseKind.None);
        var calls = new List<string>();
        var wired = new ItemContextMenu(null!, new Wired(calls)).Build(target);
        wired.First(i => i.Label == "Closet").Invoke();
        wired.First(i => i.Label == "Info").Invoke();
        Assert.Equal(new[] { "closet", "info" }, calls);

        var bare = new ItemContextMenu(null!, new Bare()).Build(target);
        Assert.DoesNotContain(bare, i => i.Label is "Closet" or "Info");
    }

    [Fact]
    public void Effect_menu_has_no_placeholder_entries()
    {
        var items = new EffectContextMenu(null!).Build(new EffectMenuTarget("1", "Empathy", true, 5));
        Assert.DoesNotContain(items, i => i.Label is "Uneffect" or "Info");
    }

    [Fact]
    public void Filtering_the_selected_item_away_clears_selection_and_detail()
    {
        var vm = InventoryTestCatalog.Build(
            InventoryTestCatalog.Item("471", "hot wing", 3), InventoryTestCatalog.Item("41", "ice-cold Sir Schlitz", 2));
        vm.SelectedItem = vm.Items.First(i => i.Name == "hot wing");
        Assert.True(vm.HasSelection);
        vm.SearchText = "schlitz";
        Assert.Null(vm.SelectedItem);
        Assert.Null(vm.Detail);
    }

    [Fact]
    public void Unqueried_mall_price_is_not_called_not_listed()
    {
        var detail = new DesktopItemDetail { ItemId = "1", Name = "x", Quantity = 1, Uses = [] };
        Assert.DoesNotContain("not listed", detail.MallPriceText);
        Assert.Equal("1,000 Meat".Replace(",", System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberGroupSeparator),
            (detail with { CheapestMallPrice = 1000 }).MallPriceText);
    }

    // ---- M19 / M20 / M21 / M22 ----------------------------------------------------------------------------------

    [Fact]
    public void Daily_and_adventures_use_the_same_knucklebone_target()
    {
        var settings = new DesktopSettings();
        settings.Set("dailyKnucklebonesTarget", 250);
        Assert.Equal(250, DailyStepBoard.PlannerOptions(settings).DailyKnucklebonesTarget);
    }

    [Fact]
    public void Saving_settings_keeps_a_skip_made_since_the_page_was_opened()
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        try
        {
            var store = new DesktopSettingsStore(path);
            var page = new SettingsViewModel(store);                // opened before the skip
            var daily = store.Load();
            daily.SkipToday("Main", "booze", new DateOnly(2026, 11, 22));
            store.Save(daily);
            page.Theme = "Dark";
            page.Save.Execute(null);
            Assert.True(store.Load().IsSkipped("Main", "booze", new DateOnly(2026, 11, 22)));
            Assert.Equal("Dark", store.Load().Theme);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Skip_day_follows_the_kol_rollover_not_local_midnight()
    {
        // 02:00 Eastern on the 22nd is still KoL day the 21st (rollover 03:30).
        var before = new DateTimeOffset(2026, 11, 22, 7, 0, 0, TimeSpan.Zero);
        var after = new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2026, 11, 21), DailyStepBoard.KolToday(before));
        Assert.Equal(new DateOnly(2026, 11, 22), DailyStepBoard.KolToday(after));
    }

    [Fact]
    public void Daily_markers_are_per_player_and_concurrent_writers_do_not_lose_each_other()
    {
        var path = Path.Combine(Path.GetTempPath(), $"daily-{Guid.NewGuid():N}.json");
        try
        {
            var first = new KoLStatusSnapshot("Alpha", 1, 0, 1, DateTimeOffset.UnixEpoch) { PlayerId = "11" };
            var second = new KoLStatusSnapshot("Beta", 1, 0, 1, DateTimeOffset.UnixEpoch) { PlayerId = "22" };
            var keyA = DailyDoneStore.AccountKey(AccountRole.Main, first);
            var keyB = DailyDoneStore.AccountKey(AccountRole.Main, second);
            Assert.NotEqual(keyA, keyB);

            var a = new DailyDoneStore(path);
            var b = new DailyDoneStore(path);                       // a second "process"
            Parallel.For(0, 20, i =>
            {
                var store = i % 2 == 0 ? a : b;
                store.MarkDone(keyA, "action-" + i);
            });
            a.MarkDone(keyA, DailyDoneStore.BaconMachine);
            for (var i = 0; i < 20; i++) Assert.True(b.IsDone(keyA, "action-" + i), "lost action-" + i);
            Assert.True(b.IsDone(keyA, DailyDoneStore.BaconMachine));
            Assert.False(b.IsDone(keyB, DailyDoneStore.BaconMachine));   // another player on the same role
        }
        finally { foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*")) File.Delete(f); }
    }
}
