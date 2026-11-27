using Xunit;

#pragma warning disable KOLIBRI0001 // these tests exercise the experimental macro path on purpose

namespace KoLibri.Tests;

/// <summary>
/// Combat macros (KOLIBRI0001) offline against FakeKol. Fixtures M1–M7 under Fixtures/macro are synthetic until the
/// coordinator records the live answers (docs/macro-live-check.md); the assertions check structure, not exact numbers.
/// </summary>
public sealed class CombatMacroTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "macro", name));

    /// <summary>A fight page as KoL shows it: no player HP (the HP come from api status).</summary>
    private const string FightStart = """
        <html><head><script>var onturn = 1;</script></head><body><!-- MONSTERID: 123 --><span id="monname">Skeleton</span>
        <p>You're fighting a Skeleton.</p>
        <a name="end"><form name=attack action=fight.php method=post><input type=hidden name=action value="attack"><input class=button type=submit value="Attack!"></form>
        </body></html>
        """;

    private static FakeKol Kol(params string[] macroAnswers)
    {
        var kol = new FakeKol()
            .On("GET main.php", (_, _) => FakeKol.Redirect("https://www.kingdomofloathing.com/fight.php"))
            .On("GET fight.php", FightStart);
        foreach (var answer in macroAnswers) kol.On("POST fight.php", answer);
        return kol;
    }

    private static KolibriClient Client(FakeKol kol, bool macros = true, IActionGuard? guard = null)
        => KolibriClient.Create(new KolibriOptions
        {
            HttpHandler = kol,
            AccountStore = MemoryAccountStore.WithMain(),
            ActionGuard = guard,
            StateDirectory = Directory.CreateTempSubdirectory("kolibri-tests-").FullName,
            DisableLimiterForTests = true,
            EnableExperimentalMacros = macros
        });

    private static IReadOnlyList<string> MacroPosts(FakeKol kol)
        => kol.Requests.Where(r => r.Method == "POST" && r.Path == "/fight.php").Select(r => r.Form).ToArray();

    private static IReadOnlyDictionary<string, string> Form(string form)
        => form.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));

    // ------------------------------------------------------------------ opt-in

    [Fact]
    public async Task Without_the_option_macros_are_refused_as_before_and_nothing_is_sent()
    {
        var kol = Kol(Fixture("macro-m2-repeat.html"));
        await using var client = Client(kol, macros: false);
        await using var session = await client.OpenAsync("main");

        var single = await Assert.ThrowsAsync<NotSupportedException>(() => session.Adventure.FightAsync(CombatAction.FromMacro("attack; repeat;")));
        Assert.Contains("KOLIBRI0001", single.Message);
        await Assert.ThrowsAsync<NotSupportedException>(() => session.Adventure.FightToEndAsync(CombatStrategies.Macro("attack; repeat;")));
        Assert.Empty(MacroPosts(kol));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(";;\n ;")]
    [InlineData("# only a comment\n;")]
    public void Empty_macros_are_refused_when_built(string macro)
        => Assert.ThrowsAny<ArgumentException>(() => CombatAction.FromMacro(macro));

    [Fact]
    public async Task Too_long_and_hand_built_empty_macros_are_refused_before_sending()
    {
        var kol = Kol(Fixture("macro-m1-single.html"));
        await using var client = Client(kol);
        await using var session = await client.OpenAsync("main");

        Assert.Throws<ArgumentException>(() => CombatAction.FromMacro(string.Join(";", Enumerable.Repeat("attack", 2000))));
        await Assert.ThrowsAsync<ArgumentException>(() => session.Adventure.FightAsync(new CombatAction(CombatActionKind.Macro, Macro: " ; ")));
        await Assert.ThrowsAsync<ArgumentException>(() => session.Adventure.FightAsync(new CombatAction(CombatActionKind.Macro)));
        Assert.Empty(MacroPosts(kol));
    }

    // ------------------------------------------------------------------ M1–M7

    [Fact]
    public async Task M1_single_attack_sends_action_macro_with_macrotext_and_pwd()
    {
        var kol = Kol(Fixture("macro-m1-single.html"));
        await using var client = Client(kol);
        await using var session = await client.OpenAsync("main");
        await session.Adventure.GetOpenEncounterAsync();

        var round = await session.Adventure.FightAsync(CombatAction.FromMacro("attack;  # one swing\n"));

        var form = Form(Assert.Single(MacroPosts(kol)));
        Assert.Equal("macro", form["action"]);
        Assert.Equal("attack;", form["macrotext"]);
        Assert.Equal("0123456789abcdef", form["pwd"]);
        Assert.Equal(FightOutcome.Ongoing, round.Outcome);
        Assert.Equal(1, round.RoundsInResponse);
        Assert.Null(round.MacroError);
    }

    [Fact]
    public async Task M2_repeat_is_one_request_with_several_rounds_loot_and_hp_from_status()
    {
        var kol = Kol();
        kol.On("POST fight.php", (_, _) => { kol.StatusHp = 90; return FakeKol.Html(Fixture("macro-m2-repeat.html")); });
        await using var client = Client(kol);
        await using var session = await client.OpenAsync("main");
        var ended = new List<FightResult>();
        client.Events.FightEnded += (_, e) => ended.Add(e.Result);

        var result = await session.Adventure.FightToEndAsync(CombatStrategies.Macro("attack; repeat;"));

        Assert.Equal(FightOutcome.Won, result.Outcome);
        Assert.Single(MacroPosts(kol));
        var round = Assert.Single(result.Rounds);
        Assert.Equal(3, round.RoundsInResponse);
        Assert.Equal(3, round.Round);
        Assert.Equal(90, round.Hp);          // fight.php has no player HP: read from api status after the macro
        Assert.Equal(10, round.DamageTaken); // 100 before, 90 after
        Assert.Equal("seal tooth", Assert.Single(result.Items).Name);
        Assert.Equal(15, result.Meat);
        Assert.Single(ended);
    }

    [Fact]
    public async Task M3_abort_keeps_the_fight_open_and_the_same_macro_is_not_sent_again()
    {
        var kol = Kol(Fixture("macro-m3-abort.html"));
        await using var client = Client(kol);
        await using var session = await client.OpenAsync("main");

        var result = await session.Adventure.FightToEndAsync(CombatStrategies.Macro("if monstername Skeleton; abort; endif; attack;"));

        Assert.Equal(FightOutcome.Stopped, result.Outcome);
        Assert.Single(MacroPosts(kol));
        var round = Assert.Single(result.Rounds);
        Assert.Equal(FightOutcome.Ongoing, round.Outcome);
        Assert.Contains("Macro Abort", round.MacroError);
    }

    [Fact]
    public async Task M3_after_an_abort_the_strategy_is_asked_again_and_may_pick_another_action()
    {
        var kol = Kol(Fixture("macro-m3-abort.html"), ActionTests.FightWon);
        await using var client = Client(kol);
        await using var session = await client.OpenAsync("main");
        var macro = CombatAction.FromMacro("if monstername Skeleton; abort; endif; attack;");

        var result = await session.Adventure.FightToEndAsync(CombatStrategies.FromDelegate(c => c.History.Count == 0 ? macro : CombatAction.Attack));

        Assert.Equal(FightOutcome.Won, result.Outcome);
        var posts = MacroPosts(kol);
        Assert.Equal(2, posts.Count);
        Assert.StartsWith("action=macro", posts[0]);
        Assert.Equal("action=attack", posts[1]);
    }

    [Fact]
    public async Task M4_skill_then_attacks_counts_all_rounds_and_reads_mp_from_status()
    {
        var kol = Kol(Fixture("macro-m4-skill.html"));
        await using var client = Client(kol);
        await using var session = await client.OpenAsync("main");

        var result = await session.Adventure.FightToEndAsync(CombatStrategies.Macro("skill Lunging Thrust-Smack; attack; repeat;"));

        Assert.Equal(FightOutcome.Won, result.Outcome);
        var round = Assert.Single(result.Rounds);
        Assert.Equal(3, round.RoundsInResponse);
        Assert.Equal(40, round.Mp);
        Assert.Equal(12, result.Meat);
        Assert.Equal("skill Lunging Thrust-Smack;attack;repeat;", Form(Assert.Single(MacroPosts(kol)))["macrotext"]);
    }

    [Fact]
    public async Task M5_runaway_by_macro_is_fled()
    {
        var kol = Kol(Fixture("macro-m5-runaway.html"));
        await using var client = Client(kol);
        await using var session = await client.OpenAsync("main");

        var result = await session.Adventure.FightToEndAsync(CombatStrategies.Macro("if hpbelow 999999; runaway; endif;"));

        Assert.Equal(FightOutcome.RanAway, result.Outcome);
        Assert.Single(MacroPosts(kol));
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task M6_invalid_macro_is_unknown_with_a_clear_message_and_no_second_request()
    {
        var kol = Kol(Fixture("macro-m6-invalid.html"));
        await using var client = Client(kol);
        await using var session = await client.OpenAsync("main");

        var result = await session.Adventure.FightToEndAsync(CombatStrategies.Macro("frobnicate;"));

        Assert.Equal(FightOutcome.Unknown, result.Outcome);
        Assert.Single(MacroPosts(kol));
        var round = Assert.Single(result.Rounds);
        Assert.Equal(0, round.RoundsInResponse);
        Assert.Contains("invalid", round.MacroError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Invalid Macro", round.MacroError);
    }

    [Fact]
    public async Task M7_win_followed_by_a_choice_is_won_and_the_choice_is_found_afterwards()
    {
        var kol = Kol(Fixture("macro-m7-followup.html"));
        await using var client = Client(kol);
        await using var session = await client.OpenAsync("main");

        var result = await session.Adventure.FightToEndAsync(CombatStrategies.Macro("attack; repeat;"));
        Assert.Equal(FightOutcome.Won, result.Outcome);
        Assert.Equal(2, result.Rounds[0].RoundsInResponse);
        Assert.Equal(10, result.Meat);

        kol.On("GET main.php", (_, _) => FakeKol.Redirect("https://www.kingdomofloathing.com/choice.php"))
           .On("GET choice.php", ActionTests.ChoicePage);
        var next = await session.Adventure.GetOpenEncounterAsync();
        Assert.Equal(1234, Assert.IsType<ChoiceEncounter>(next).ChoiceId);
    }

    // ------------------------------------------------------------------ guard summary

    [Fact]
    public async Task Guard_summary_shows_a_short_macro_with_hash_not_the_full_text()
    {
        var kol = Kol(Fixture("macro-m1-single.html"));
        var guard = new RecordingGuard();
        await using var client = Client(kol, guard: guard);
        await using var session = await client.OpenAsync("main");
        var macro = "if monstername \"a really long monster name\"; skill 1005; endif; attack; repeat;";

        await session.Adventure.FightAsync(CombatAction.FromMacro(macro));

        var summary = Assert.Single(guard.Actions, a => a.Kind == GuardedActionKind.Fight).Summary;
        Assert.StartsWith("fight: macro \"", summary);
        Assert.Matches("#[0-9a-f]{8}", summary);
        Assert.DoesNotContain("repeat", summary);
    }

    private sealed class RecordingGuard : IActionGuard
    {
        public List<GuardedAction> Actions { get; } = new();
        public ValueTask<GuardDecision> CheckAsync(GuardedAction action, CancellationToken cancellationToken)
        {
            Actions.Add(action);
            return ValueTask.FromResult(GuardDecision.Allow);
        }
        public ValueTask CompletedAsync(GuardedAction action, bool succeeded, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
