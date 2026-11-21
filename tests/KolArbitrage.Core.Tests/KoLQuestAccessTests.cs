using KolArbitrage.Core.Adventure;
using KolArbitrage.Core.Integration;
using Xunit;

namespace KolArbitrage.Core.Tests;

public sealed class KoLQuestAccessTests
{
    private static readonly KoLStatusSnapshot Status = new("Fixture", 20, 0, 6, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Opening_the_knob_changes_access_for_all_four_inner_areas()
    {
        var started = KoLQuestAccess.Parse(QuestLog(
            "<b>The Goblin Who Wouldn&#39;t Be King</b><br>Find your way inside Cobb's Knob."), QuestLog(""));
        var opened = KoLQuestAccess.Parse(QuestLog(
            "<b>The Goblin Who Wouldn't Be King</b><br>Find some way to infiltrate the Throne Room."), QuestLog(""));

        foreach (var area in new[] { 257, 258, 259, 260 })
        {
            Assert.False(KoLQuestAccess.Evaluate(area, Status, started).Allowed);
            Assert.True(KoLQuestAccess.Evaluate(area, Status, opened).Allowed);
        }
    }

    [Fact]
    public void A_finished_friar_quest_closes_the_three_ritual_areas()
    {
        const string title = "<b>Trial By Friar</b><br>";
        var active = KoLQuestAccess.Parse(QuestLog(title + "Recover the ritual items"), QuestLog(""));
        var completed = KoLQuestAccess.Parse(QuestLog(""), QuestLog(title + "You have cleansed the taint."));

        foreach (var area in new[] { 539, 540, 541 })
        {
            Assert.True(KoLQuestAccess.Evaluate(area, Status, active).Allowed);
            Assert.False(KoLQuestAccess.Evaluate(area, Status, completed).Allowed);
        }
    }

    [Fact]
    public void Pandamonium_follows_the_current_path_and_distinguishes_missing_status()
    {
        Assert.False(KoLQuestAccess.Evaluate(438, Status with { PathId = 0 }, null).Allowed);
        Assert.True(KoLQuestAccess.Evaluate(438, Status with { PathId = 23 }, null).Allowed);
        Assert.Null(KoLQuestAccess.Evaluate(438, Status, null).Allowed);
        Assert.Null(KoLQuestAccess.Evaluate(438, Status with { PathId = 23, IsStale = true }, null).Allowed);
    }

    [Theory]
    [InlineData(50, "339", false)]
    [InlineData(101, "339", false)]
    [InlineData(51, "4947", true)]
    [InlineData(52, "4947", true)]
    [InlineData(53, "4947", true)]
    public void Lab_and_menagerie_follow_inventory_and_knob_quest_changes(int area, string key, bool needsQuest)
    {
        var closed = new KoLQuestLogSnapshot(KoLQuestProgress.Started, KoLQuestProgress.NotListed);
        var opened = closed with { Goblin = KoLQuestProgress.Advanced };
        var empty = new KoLInventorySnapshot(Array.Empty<KoLInventoryItem>(), DateTimeOffset.UnixEpoch);
        var withKey = empty with { Items = new[] { new KoLInventoryItem(key, "Key", 1) } };

        Assert.False(KoLQuestAccess.Evaluate(area, Status, opened, empty).Allowed);
        Assert.Equal(!needsQuest, KoLQuestAccess.Evaluate(area, Status, closed, withKey).Allowed);
        Assert.True(KoLQuestAccess.Evaluate(area, Status, opened, withKey).Allowed);
        Assert.Null(KoLQuestAccess.Evaluate(area, Status, opened, withKey with { IsStale = true }).Allowed);
    }

    [Theory]
    [InlineData(32, 1, 2)]
    [InlineData(33, 2, 3)]
    [InlineData(34, 3, 4)]
    public void Bat_hole_progress_follows_the_map_after_sonar(int area, int before, int after)
    {
        Assert.False(KoLQuestAccess.EvaluateBatHole(area, Status, $"<img src=bathole_{before}.gif>").Allowed);
        Assert.True(KoLQuestAccess.EvaluateBatHole(area, Status, $"<img src=bathole_{after}.gif>").Allowed);
        Assert.Null(KoLQuestAccess.EvaluateBatHole(area, Status, "<html>Unknown</html>").Allowed);
    }

    [Fact]
    public void Boss_lair_closes_after_defeat_and_guano_requires_observed_resistance()
    {
        Assert.False(KoLQuestAccess.EvaluateBatHole(34, Status, "<img src=bathole_5.gif>").Allowed);
        Assert.Null(KoLQuestAccess.EvaluateBatHole(31, Status, "<img src=bathole_1.gif>").Allowed);
        var equipped = Status with { Equipment = new Dictionary<string, string> { ["acc1"] = "556" } };
        Assert.True(KoLQuestAccess.EvaluateBatHole(31, equipped, "<img src=bathole_1.gif>").Allowed);
    }

    private static string QuestLog(string entries)
        => $"""<b>Your Quest Log</b><a href="questlog.php?which=1">Current</a><blockquote>{entries}</blockquote>""";
}
