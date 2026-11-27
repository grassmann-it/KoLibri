using KoLibri.Desktop;
using KoLibri.Extras;
using Xunit;

namespace KoLibri.Tests.Desktop;

/// <summary>
/// The desktop farm path on KoLibri 0.5's building blocks, against the fake KoL: healing and upkeep between turns,
/// the opt-in monster and turtle stops, and the curated choice defaults (on by default, Turtle Tamer clicked through).
/// </summary>
public sealed class DesktopFarmBatchTests
{
    private static AutoCombatViewModel Configurator() => new(new MafiaDataLoader().Load(AdventureKit.CatalogDirectory));

    private static FarmOptions Farm(AutoCombatViewModel viewModel)
    {
        Assert.True(viewModel.TryBuildFarmOptions(out var options, out var error), error);
        return options!;
    }

    private static async Task WithSession(FakeKol kol, Func<EncounterBrowserSessionViewModel, EncounterBrowserViewModel, Task> body,
        IReadOnlyDictionary<int, int>? policyTable = null)
    {
        var root = Directory.CreateTempSubdirectory("kolibri-farm-");
        try
        {
            var session = AdventureKit.Session(kol, root.FullName, out var accounts);
            session.ChoiceTableFactory = () => policyTable ?? new Dictionary<int, int>();
            await using var _ = accounts;
            await body(session, AdventureKit.Browser());
        }
        finally { root.Delete(recursive: true); }
    }

    private const string SkeletonStore = """
        <html><body><b>Temporarily Out of Skeletons</b>
        <form action="choice.php" method="post"><input type="hidden" name="whichchoice" value="1060">
        <input type="hidden" name="pwd" value="0123456789abcdef"><input type="hidden" name="option" value="1">
        <input class=button type="submit" value="Look around"></form>
        <form action="choice.php" method="post"><input type="hidden" name="whichchoice" value="1060">
        <input type="hidden" name="pwd" value="0123456789abcdef"><input type="hidden" name="option" value="2">
        <input class=button type="submit" value="Take the bones"></form></body></html>
        """;

    private static FakeKol SkeletonStoreKol()
    {
        var kol = new FakeKol { StatusAdventures = 9 };
        kol.On("POST adventure.php", (_, _) => AdventureKit.To("choice.php")).On("GET choice.php", SkeletonStore)
           .On("POST choice.php", (_, _) => { kol.StatusAdventures--; return FakeKol.Html(AdventureKit.ChoiceDone); });
        return kol;
    }

    /// <summary>adventure.php → a fight against a turtle monster; the attack wins.</summary>
    private static FakeKol TurtleFightKol()
    {
        var kol = new FakeKol { StatusAdventures = 9, StatusHp = 80 };
        kol.On("POST adventure.php", (_, _) => AdventureKit.To("fight.php"))
           .On("GET fight.php", AdventureKit.Fight.Replace(">Skeleton<", ">snapping turtle<"))
           .On("POST fight.php", (_, _) => { kol.StatusAdventures--; return FakeKol.Html(AdventureKit.FightWon); });
        return kol;
    }

    private static string? ChoicePost(FakeKol kol) => kol.Posts().Where(p => p.Path.EndsWith("choice.php")).Select(p => p.Form).SingleOrDefault();

    // ------------------------------------------------------------------ configurator

    [Fact]
    public void Defaults_click_turtles_through_and_use_the_choice_defaults()
    {
        var viewModel = Configurator();
        Assert.True(viewModel.UseChoiceDefaults);
        Assert.False(viewModel.StopOnTurtle);
        Assert.False(viewModel.StopOnMonster);
        Assert.Equal(0, viewModel.HealBelowHpPercent);

        var farm = Farm(viewModel);
        Assert.Empty(farm.BuildStops());
        Assert.Same(RecoveryPolicies.None, farm.BuildRecovery());
        Assert.Contains("turtles clicked through", viewModel.FarmSummary);
        Assert.Contains("choice defaults on", viewModel.FarmSummary);
    }

    [Fact]
    public void Steps_resolve_names_through_the_catalog()
    {
        var viewModel = Configurator();
        viewModel.HealBelowHpPercent = 40;
        viewModel.HealStepsText = "rest, item:Doc Galaktik's Pungent Unguent, skill:Tongue of the Walrus";
        viewModel.UpkeepEffectText = "Polka of Plenty";
        viewModel.UpkeepMinTurns = 3;
        viewModel.UpkeepStepText = "skill:The Polka of Plenty";

        var farm = Farm(viewModel);
        Assert.Equal(new[] { RecoveryStep.Rest, RecoveryStep.UseItem(231), RecoveryStep.Cast(1010) }, farm.HealSteps);
        Assert.Equal(new UpkeepRule(63, "Polka of Plenty", 3, RecoveryStep.Cast(6006)), farm.Upkeep);
        Assert.Contains("heal below 40 % HP (rest → item 231 → skill 1010)", viewModel.FarmSummary);
        Assert.Contains("keep Polka of Plenty ≥ 3 turns", viewModel.FarmSummary);
        viewModel.SelectedTarget = new CombatTarget { Id = "123", Name = "Skeleton", Zone = "undead", Detail = "1 HP" };
        Assert.Contains("between turns: heal below 40", viewModel.PreviewText);
    }

    [Fact]
    public void Farm_setters_announce_their_own_property()
    {
        var viewModel = Configurator();
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        viewModel.HealBelowHpPercent = 40;
        viewModel.HealStepsText = "rest";
        viewModel.HealStepsText = "rest, skill:1010";
        viewModel.StopOnTurtle = true;
        viewModel.UseChoiceDefaults = false;
        Assert.Contains(nameof(AutoCombatViewModel.HealBelowHpPercent), changed);
        Assert.Contains(nameof(AutoCombatViewModel.HealStepsText), changed);
        Assert.Contains(nameof(AutoCombatViewModel.StopOnTurtle), changed);
        Assert.Contains(nameof(AutoCombatViewModel.UseChoiceDefaults), changed);
        Assert.Contains(nameof(AutoCombatViewModel.FarmSummary), changed);
        Assert.Contains(nameof(AutoCombatViewModel.PreviewText), changed);
        Assert.DoesNotContain("SetFarm", changed);
    }

    [Theory]
    [InlineData("dance", "Heal steps")]
    [InlineData("item:no such thing at all", "Unknown item")]
    [InlineData("skill:", "Heal steps")]
    public void Bad_heal_steps_are_refused_with_a_reason(string steps, string expected)
    {
        var viewModel = Configurator();
        viewModel.HealBelowHpPercent = 50;
        viewModel.HealStepsText = steps;
        Assert.False(viewModel.TryBuildFarmOptions(out _, out var error));
        Assert.Contains(expected, error);
        Assert.Equal(error, viewModel.FarmError);
    }

    [Fact]
    public void Healing_without_a_step_and_bad_overrides_are_refused()
    {
        var viewModel = Configurator();
        viewModel.HealBelowHpPercent = 50;
        viewModel.HealStepsText = "";
        Assert.Contains("at least one step", viewModel.FarmError);

        viewModel.HealBelowHpPercent = 0;
        viewModel.ChoiceOverridesText = "1060=2";
        Assert.Contains("Choice overrides", viewModel.FarmError);

        viewModel.ChoiceOverridesText = "1060:1, 1234:2";
        Assert.Null(viewModel.FarmError);
        Assert.Equal(new Dictionary<int, int> { [1060] = 1, [1234] = 2 }, Farm(viewModel).ChoiceOverrides);
    }

    [Fact]
    public void Stop_on_monster_uses_the_target_when_no_name_is_given()
    {
        var viewModel = Configurator();
        viewModel.StopOnMonster = true;
        Assert.Contains("monster name", viewModel.FarmError);

        viewModel.SelectedTarget = new CombatTarget { Id = "123", Name = "Skeleton", Zone = "undead", Detail = "" };
        var stop = Assert.Single(Farm(viewModel).BuildStops());
        Assert.Equal(StopConditionKind.MonsterSeen, stop.Kind);
        Assert.Equal("Skeleton", stop.Pattern);

        viewModel.StopMonsterName = "  Skeletal Sommelier ";
        Assert.Equal("Skeletal Sommelier", Assert.Single(Farm(viewModel).BuildStops()).Pattern);

        viewModel.StopOnTurtle = true;
        Assert.Contains(Farm(viewModel).BuildStops(), s => s.Kind == StopConditionKind.TurtleEncounter);
    }

    // ------------------------------------------------------------------ choices

    [Fact]
    public Task Choice_defaults_answer_the_skeleton_store_without_a_policy()
    {
        var kol = SkeletonStoreKol();
        return WithSession(kol, async (session, browser) =>
        {
            await session.RunTurnsAsync(browser, AdventureKit.Garden, 1, null, farm: FarmOptions.Default);

            Assert.Contains("option=2", ChoicePost(kol));
            Assert.Contains("whichchoice=1060", ChoicePost(kol));
            Assert.Equal(nameof(BoundedAdventureRunStopReason.Complete), session.LastReport?.StopReason);
        });
    }

    [Fact]
    public Task An_override_in_the_form_beats_the_default()
    {
        var kol = SkeletonStoreKol();
        return WithSession(kol, async (session, browser) =>
        {
            var farm = FarmOptions.Default with { ChoiceOverrides = new Dictionary<int, int> { [1060] = 1 } };
            await session.RunTurnsAsync(browser, AdventureKit.Garden, 1, null, farm: farm);
            Assert.Contains("option=1", ChoicePost(kol));
        });
    }

    [Fact]
    public Task The_choice_policy_file_beats_the_default()
    {
        var kol = SkeletonStoreKol();
        return WithSession(kol, async (session, browser) =>
        {
            await session.RunTurnsAsync(browser, AdventureKit.Garden, 1, null, farm: FarmOptions.Default);
            Assert.Contains("option=1", ChoicePost(kol));
        }, policyTable: new Dictionary<int, int> { [1060] = 1 });
    }

    [Fact]
    public Task Without_choice_defaults_the_skeleton_store_pauses()
    {
        var kol = SkeletonStoreKol();
        return WithSession(kol, async (session, browser) =>
        {
            await session.RunTurnsAsync(browser, AdventureKit.Garden, 1, null, farm: FarmOptions.Default with { UseChoiceDefaults = false });

            Assert.Null(ChoicePost(kol));
            Assert.Equal(nameof(BoundedAdventureRunStopReason.NeedsDecision), session.LastReport?.StopReason);
            Assert.Equal("1060", session.Decisions.ChoiceId);
        });
    }

    [Fact]
    public Task A_turtle_choice_is_clicked_through_by_default()
    {
        var kol = AdventureKit.ChoiceKol(); // "A Turtle", choice 1234, not in the table
        return WithSession(kol, async (session, browser) =>
        {
            await session.RunTurnsAsync(browser, AdventureKit.Garden, 1, null, farm: FarmOptions.Default);

            Assert.Contains("whichchoice=1234", ChoicePost(kol));
            Assert.Contains("option=1", ChoicePost(kol));
            Assert.NotEqual(nameof(BoundedAdventureRunStopReason.NeedsDecision), session.LastReport?.StopReason);
            Assert.False(session.IsSessionActive);
        });
    }

    [Fact]
    public Task Stop_on_turtle_leaves_the_turtle_choice_open()
    {
        var kol = AdventureKit.ChoiceKol();
        return WithSession(kol, async (session, browser) =>
        {
            // Even an auto-safe policy file must not answer it once the operator asked to stop on turtles.
            session.ChoiceResolverFactory = () => ChoiceResolvers.FromTable(new Dictionary<int, int>());
            await session.RunTurnsAsync(browser, AdventureKit.Garden, 5, null, farm: FarmOptions.Default with { StopOnTurtle = true });

            Assert.Null(ChoicePost(kol));
            Assert.Equal(nameof(BoundedAdventureRunStopReason.NeedsDecision), session.LastReport?.StopReason);
            Assert.True(session.IsSessionActive);
            Assert.Equal("1234", session.Decisions.ChoiceId);
        });
    }

    [Fact]
    public void A_choice_the_table_cannot_answer_falls_back_to_the_policy_resolver()
    {
        var context = new ChoiceContext(null, 4242, "A Fork in the Road", new[] { new ChoiceOption(1, "Left"), new ChoiceOption(2, "Right") });
        var resolver = FarmOptions.Default.BuildChoiceResolver(new Dictionary<int, int>(),
            () => ChoiceResolvers.FromTable(new Dictionary<int, int> { [4242] = 2 }));
        Assert.Equal(2, resolver.Resolve(context).Option);

        var strict = FarmOptions.Default.BuildChoiceResolver(new Dictionary<int, int>(), () => ChoiceResolvers.AlwaysAsk);
        Assert.Null(strict.Resolve(context).Option);
    }

    // ------------------------------------------------------------------ fights and stops

    [Fact]
    public Task Stop_on_turtle_hands_a_turtle_fight_back()
    {
        var kol = TurtleFightKol();
        return WithSession(kol, async (session, browser) =>
        {
            await session.RunTurnsAsync(browser, AdventureKit.Garden, 5, null, farm: FarmOptions.Default with { StopOnTurtle = true });

            Assert.DoesNotContain(kol.Posts(), p => p.Path.EndsWith("fight.php"));
            Assert.Equal(nameof(BoundedAdventureRunStopReason.CombatPolicyStopped), session.LastReport?.StopReason);
            Assert.True(session.IsSessionActive);
        });
    }

    [Fact]
    public Task A_turtle_fight_is_fought_by_default()
    {
        var kol = TurtleFightKol();
        return WithSession(kol, async (session, browser) =>
        {
            await session.RunTurnsAsync(browser, AdventureKit.Garden, 1, null, farm: FarmOptions.Default);

            Assert.Single(kol.Posts(), p => p.Path.EndsWith("fight.php"));
            Assert.Equal(nameof(BoundedAdventureRunStopReason.Complete), session.LastReport?.StopReason);
        });
    }

    [Fact]
    public Task Stop_on_monster_ends_the_batch_after_that_fight()
    {
        var kol = AdventureKit.FightKol(AdventureKit.FightWon);
        return WithSession(kol, async (session, browser) =>
        {
            await session.RunTurnsAsync(browser, AdventureKit.Garden, 5, null, farm: FarmOptions.Default with { StopOnMonster = "Skeleton" });

            Assert.Single(kol.Posts(), p => p.Path.EndsWith("adventure.php"));
            Assert.Equal(nameof(BoundedAdventureRunStopReason.ConditionsSatisfied), session.LastReport?.StopReason);
            Assert.Contains("Monster seen: Skeleton", session.LastMessage);
        });
    }

    // ------------------------------------------------------------------ recovery

    [Fact]
    public Task Heals_with_rest_before_the_turn_instead_of_stopping()
    {
        var kol = AdventureKit.FightKol(AdventureKit.FightWon);
        kol.StatusHp = 30;
        kol.On("POST campground.php", (_, _) => { kol.StatusHp = 100; return FakeKol.Html("<html><body>You rest and feel much better.</body></html>"); });
        return WithSession(kol, async (session, browser) =>
        {
            var farm = FarmOptions.Default with { HealBelowHpPercent = 50, HealSteps = new[] { RecoveryStep.Rest } };
            await session.RunTurnsAsync(browser, AdventureKit.Garden, 1, null, minimumHpPercent: 40, farm: farm);

            var posts = kol.Posts().Select(p => p.Path).ToArray();
            Assert.Equal("/campground.php", posts[0]);
            Assert.Contains("/fight.php", posts);
            Assert.Equal(nameof(BoundedAdventureRunStopReason.Complete), session.LastReport?.StopReason);
        });
    }

    [Fact]
    public Task A_rest_that_does_not_heal_stops_the_batch_with_a_reason()
    {
        var kol = AdventureKit.FightKol(AdventureKit.FightWon);
        kol.StatusHp = 30;
        kol.On("POST campground.php", (_, _) => FakeKol.Html("<html><body>Nothing happens.</body></html>"));
        return WithSession(kol, async (session, browser) =>
        {
            var farm = FarmOptions.Default with { HealBelowHpPercent = 50, HealSteps = new[] { RecoveryStep.Rest } };
            await session.RunTurnsAsync(browser, AdventureKit.Garden, 5, null, farm: farm);

            Assert.DoesNotContain(kol.Posts(), p => p.Path.EndsWith("adventure.php"));
            Assert.Equal(nameof(BoundedAdventureRunStopReason.RecoveryStopped), session.LastReport?.StopReason);
            Assert.True(session.LastReport?.NeedsUser);
            Assert.Contains("healing or buff upkeep could not continue", session.LastMessage);
        });
    }

    [Fact]
    public Task Upkeep_casts_the_buff_once_before_the_turn()
    {
        var kol = AdventureKit.FightKol(AdventureKit.FightWon);
        var buffed = false;
        kol.On("GET api.php?what=status", (_, _) => FakeKol.Html(buffed
            ? kol.StatusJson().Replace("\"effects\":{}", "\"effects\":{\"e0\":[\"Polka of Plenty\",5]}")
            : kol.StatusJson()));
        kol.On("POST runskillz.php", (_, _) => { buffed = true; return FakeKol.Html("<html><body>You acquire an effect: Polka of Plenty</body></html>"); });
        return WithSession(kol, async (session, browser) =>
        {
            var farm = FarmOptions.Default with { Upkeep = new UpkeepRule(63, "Polka of Plenty", 3, RecoveryStep.Cast(6006)) };
            await session.RunTurnsAsync(browser, AdventureKit.Garden, 1, null, farm: farm);

            var posts = kol.Posts().Select(p => p.Path).ToArray();
            Assert.Equal(1, posts.Count(p => p == "/runskillz.php"));
            Assert.True(Array.IndexOf(posts, "/runskillz.php") < Array.IndexOf(posts, "/adventure.php"));
            Assert.Contains(kol.Posts(), p => p.Path == "/runskillz.php" && p.Form.Contains("whichskill=6006"));
        });
    }

    [Fact]
    public Task Every_turn_of_a_batch_uses_the_override()
    {
        var kol = SkeletonStoreKol();
        return WithSession(kol, async (session, browser) =>
        {
            var farm = FarmOptions.Default with { ChoiceOverrides = new Dictionary<int, int> { [1060] = 1 } };
            await session.RunTurnsAsync(browser, AdventureKit.Garden, 2, null, farm: farm);
            Assert.Equal(2, kol.Posts().Count(p => p.Path.EndsWith("choice.php") && p.Form.Contains("option=1")));
        });
    }
}
