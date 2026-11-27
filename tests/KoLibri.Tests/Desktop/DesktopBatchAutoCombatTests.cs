using KoLibri.Desktop;
using Xunit;

namespace KoLibri.Tests.Desktop;

/// <summary>
/// Desktop adventure batch with the configured combat plan (rule text for CombatStrategies.FromRules), all against
/// the fake KoL: the plan's action is sent, a choice without an answer pauses, a defeat stops.
/// </summary>
public sealed class DesktopBatchAutoCombatTests
{
    private static string SkillThenAttack(string skillId) => AutoCombatViewModel.BuildRules(new[] { skillId }, Array.Empty<string>());

    private static async Task WithSession(FakeKol kol, Func<EncounterBrowserSessionViewModel, EncounterBrowserViewModel, Task> body)
    {
        var root = Directory.CreateTempSubdirectory("kolibri-batch-");
        try
        {
            var session = AdventureKit.Session(kol, root.FullName, out var accounts);
            await using var _ = accounts;
            await body(session, AdventureKit.Browser());
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public Task Batch_sends_the_plan_skill_in_a_fight()
    {
        var kol = AdventureKit.FightKol(AdventureKit.FightWon);
        return WithSession(kol, async (session, browser) =>
        {
            await session.RunTurnsAsync(browser, AdventureKit.Garden, 1, null, combatRules: SkillThenAttack("1001"));

            var fight = Assert.Single(kol.Posts(), post => post.Path.EndsWith("fight.php"));
            Assert.Contains("whichskill=1001", fight.Form);
            Assert.DoesNotContain("action=attack", fight.Form);
            Assert.Equal(1, session.LastReport?.TurnsUsed);
            Assert.Equal(nameof(BoundedAdventureRunStopReason.Complete), session.LastReport?.StopReason);
            Assert.Contains("1 × knucklebone", session.LastMessage);
        });
    }

    [Fact]
    public Task Batch_without_a_plan_still_attacks()
    {
        var kol = AdventureKit.FightKol(AdventureKit.FightWon);
        return WithSession(kol, async (session, browser) =>
        {
            await session.RunTurnsAsync(browser, AdventureKit.Garden, 1, null);

            var fight = Assert.Single(kol.Posts(), post => post.Path.EndsWith("fight.php"));
            Assert.Contains("action=attack", fight.Form);
        });
    }

    [Fact]
    public Task Plan_that_hands_the_fight_back_leaves_it_open_and_shown()
    {
        var kol = AdventureKit.FightKol();
        return WithSession(kol, async (session, browser) =>
        {
            const string manual = """{"rules":[],"defaultAction":{"kind":"manual"}}""";
            await session.RunTurnsAsync(browser, AdventureKit.Garden, 5, null, combatRules: manual);

            Assert.DoesNotContain(kol.Posts(), post => post.Path.EndsWith("fight.php"));
            Assert.Equal(nameof(BoundedAdventureRunStopReason.CombatPolicyStopped), session.LastReport?.StopReason);
            Assert.True(session.LastReport?.NeedsUser);
            Assert.True(session.IsSessionActive); // the fight stays open for the user
            Assert.Contains("fight is still open", session.LastMessage);
            Assert.Contains(browser.Actions, action => action.Kind == "skill"); // the encounter view shows the fight
        });
    }

    [Fact]
    public Task Invalid_rule_text_is_refused_before_anything_is_sent()
        => WithSession(AdventureKit.FightKol(), async (session, browser) =>
        {
            Assert.Null(await session.RunTurnsAsync(browser, AdventureKit.Garden, 5, null, combatRules: "dance wildly"));
            Assert.Contains("Invalid combat rules", session.LastMessage);
        });

    [Fact]
    public Task Batch_stops_before_a_round_when_hp_is_below_the_minimum()
    {
        var kol = AdventureKit.FightKol(AdventureKit.FightWon);
        return WithSession(kol, async (session, browser) =>
        {
            await session.RunTurnsAsync(browser, AdventureKit.Garden, 5, null, minimumHpPercent: 90); // HP 80/100

            Assert.Equal(new[] { "/adventure.php" }, kol.Posts().Select(p => p.Path));
            Assert.Equal(nameof(BoundedAdventureRunStopReason.HpBelowMinimum), session.LastReport?.StopReason);
            Assert.Contains("HP is below", session.LastMessage);
            Assert.True(session.IsSessionActive);
        });
    }

    [Fact]
    public Task A_fight_page_without_hp_uses_the_fresh_status_hp()
    {
        var kol = new FakeKol { StatusAdventures = 9 };
        kol.On("POST adventure.php", (_, _) => AdventureKit.To("fight.php")).On("GET fight.php", AdventureKit.FightWithoutHp)
           .On("POST fight.php", (_, _) => { kol.StatusAdventures--; return FakeKol.Html(AdventureKit.FightWon); });
        kol.StatusHp = 494;
        kol.StatusMaxHp = 504;
        return WithSession(kol, async (session, browser) =>
        {
            await session.RunTurnsAsync(browser, AdventureKit.Garden, 1, null, minimumHpPercent: 50);

            Assert.Single(kol.Posts(), post => post.Path.EndsWith("fight.php")); // the attack was sent
            Assert.NotEqual(nameof(BoundedAdventureRunStopReason.HpBelowMinimum), session.LastReport?.StopReason);
        });
    }

    [Fact]
    public Task The_fight_round_limit_leaves_the_fight_open()
    {
        var kol = AdventureKit.FightKol(AdventureKit.Fight);
        return WithSession(kol, async (session, browser) =>
        {
            await session.RunTurnsAsync(browser, AdventureKit.Garden, 3, null, maxFightRounds: 1);

            Assert.Single(kol.Posts(), post => post.Path.EndsWith("fight.php")); // exactly one round
            Assert.Equal(nameof(BoundedAdventureRunStopReason.FightRoundLimitReached), session.LastReport?.StopReason);
            Assert.True(session.IsSessionActive);
        });
    }

    [Fact]
    public Task Batch_pauses_at_a_choice_without_a_saved_answer()
    {
        var kol = AdventureKit.ChoiceKol();
        return WithSession(kol, async (session, browser) =>
        {
            await session.RunTurnsAsync(browser, AdventureKit.Garden, 5, null, combatRules: SkillThenAttack("1001"));

            Assert.Equal(new[] { "/adventure.php" }, kol.Posts().Select(post => post.Path));
            Assert.Equal(nameof(BoundedAdventureRunStopReason.NeedsDecision), session.LastReport?.StopReason);
            Assert.True(session.IsSessionActive);
            Assert.Equal("1234", session.Decisions.ChoiceId);
            Assert.Equal(new[] { "Help the turtle", "Leave it alone" }, session.Decisions.Options.Select(o => o.Text));
        });
    }

    [Fact]
    public Task Batch_stops_after_a_defeat_and_starts_no_further_turn()
    {
        var kol = AdventureKit.FightKol(AdventureKit.FightLost);
        return WithSession(kol, async (session, browser) =>
        {
            await session.RunTurnsAsync(browser, AdventureKit.Garden, 5, null, combatRules: SkillThenAttack("1001"));

            Assert.Single(kol.Posts(), post => post.Path.EndsWith("adventure.php"));
            Assert.Single(kol.Posts(), post => post.Path.EndsWith("fight.php"));
            Assert.Equal(nameof(BoundedAdventureRunStopReason.GatewayStopped), session.LastReport?.StopReason);
            Assert.Equal(1, session.LastReport?.TurnsUsed);
            Assert.Equal(4, session.LastReport?.TurnsLeft);
            Assert.Contains("Lost the fight", session.LastMessage);
            Assert.False(session.IsSessionActive);
        });
    }
}
