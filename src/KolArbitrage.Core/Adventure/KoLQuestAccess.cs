using System.Net;
using System.Text.RegularExpressions;
using KolArbitrage.Core.Integration;

namespace KolArbitrage.Core.Adventure;

public enum KoLQuestProgress { Unknown, NotListed, Started, Advanced, Finished }

public sealed record KoLQuestLogSnapshot(KoLQuestProgress Goblin, KoLQuestProgress Friars);

public sealed record KoLLocationAccess(bool? Allowed, string Reason);

public static class KoLQuestAccess
{
    public static bool HasRule(int? snarfblat)
        => snarfblat is 257 or 258 or 259 or 260 or 539 or 540 or 541 or 438 || RequiresInventory(snarfblat) || IsBatHole(snarfblat);

    public static bool RequiresInventory(int? snarfblat) => snarfblat is 50 or 101 or 51 or 52 or 53;
    public static bool RequiresQuestLog(int? snarfblat)
        => HasRule(snarfblat) && !IsBatHole(snarfblat) && snarfblat is not (438 or 50 or 101);
    public static bool IsBatHole(int? snarfblat) => snarfblat is >= 30 and <= 34;

    public static KoLQuestLogSnapshot Parse(string active, string completed)
        => new(Progress("The Goblin Who Wouldn't Be King", "infiltrate the Throne Room", active, completed),
            Progress("Trial By Friar", "Recover the ritual items", active, completed));

    public static KoLLocationAccess Evaluate(int? snarfblat, KoLStatusSnapshot status, KoLQuestLogSnapshot? quests,
        KoLInventorySnapshot? inventory = null)
    {
        if (!HasRule(snarfblat)) return new(null, "Local access requirements have not been cataloged.");
        if (status.IsStale) return new(null, "Character status is stale.");
        if (IsBatHole(snarfblat)) return new(null, "The Bat Hole map is required to check access.");
        if (snarfblat is 438)
            return status.PathId is { } id
                ? new(id == 23, id == 23 ? "Actually Ed the Undying." : "Requires Actually Ed the Undying.")
                : new(null, "Current path is unknown.");

        if (RequiresInventory(snarfblat))
        {
            if (inventory is null || inventory.IsStale)
                return new(null, "A fresh inventory snapshot is needed to check the location key.");
            var laboratory = snarfblat is 50 or 101;
            var key = laboratory ? "339" : "4947";
            if (!inventory.Items.Any(item => item.ItemId == key && item.Quantity > 0))
                return new(false, laboratory ? "Requires the Knob lab key." : "Requires the Cobb's Knob Menagerie key.");
            if (laboratory) return new(true, "The Knob lab key is in inventory.");
            return quests?.Goblin switch
            {
                KoLQuestProgress.Advanced or KoLQuestProgress.Finished => new(true, "Cobb's Knob is open and the Menagerie key is in inventory."),
                KoLQuestProgress.Started or KoLQuestProgress.NotListed => new(false, "Open Cobb's Knob before entering the Menagerie."),
                _ => new(null, "Quest log progress could not be read.")
            };
        }

        var progress = snarfblat is 257 or 258 or 259 or 260 ? quests?.Goblin : quests?.Friars;
        if (progress is null or KoLQuestProgress.Unknown)
            return new(null, "Quest log progress could not be read.");
        if (snarfblat is 257 or 258 or 259 or 260)
            return progress is KoLQuestProgress.Advanced or KoLQuestProgress.Finished
                ? new(true, "Cobb's Knob has been opened.")
                : new(false, "Open Cobb's Knob using the quest map and encryption key first.");
        return progress is KoLQuestProgress.Started or KoLQuestProgress.Advanced
            ? new(true, "Trial By Friar is active.")
            : new(false, "Requires an active Trial By Friar quest.");
    }

    public static KoLLocationAccess EvaluateBatHole(int? snarfblat, KoLStatusSnapshot status, string html)
    {
        if (!IsBatHole(snarfblat) || status.IsStale)
            return new(null, "Bat Hole progress could not be verified.");
        var map = Regex.Match(html, @"bathole_([1-5])\.gif", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!map.Success) return new(null, "Bat Hole progress could not be verified.");
        var stage = map.Groups[1].Value[0] - '0';
        if (snarfblat == 31)
        {
            var freshener = status.Equipment.Values.Contains("556", StringComparer.Ordinal);
            return freshener ? new(true, "Pine-Fresh air freshener is equipped.")
                : new(null, "Guano Junction requires at least one level of stench resistance.");
        }
        if (snarfblat == 34 && stage == 5) return new(false, "The Boss Bat has already been defeated.");
        var requiredStage = snarfblat switch { 32 => 2, 33 => 3, 34 => 4, _ => 1 };
        return stage >= requiredStage
            ? new(true, "The Bat Hole map shows this area is open.")
            : new(false, $"Use {requiredStage - stage} more sonar-in-a-biscuit to open this area.");
    }

    private static KoLQuestProgress Progress(string title, string advancedMarker, string active, string completed)
    {
        if (!LooksLikeQuestLog(active) || !LooksLikeQuestLog(completed)) return KoLQuestProgress.Unknown;
        if (Section(completed, title) is not null) return KoLQuestProgress.Finished;
        var section = Section(active, title);
        if (section is null) return KoLQuestProgress.NotListed;
        var description = WebUtility.HtmlDecode(Regex.Replace(section, "<[^>]+>", " "));
        return description.Contains(advancedMarker, StringComparison.OrdinalIgnoreCase)
            ? KoLQuestProgress.Advanced : KoLQuestProgress.Started;
    }

    private static bool LooksLikeQuestLog(string html)
        => html.Contains("Your Quest Log", StringComparison.OrdinalIgnoreCase)
            && html.Contains("questlog.php", StringComparison.OrdinalIgnoreCase);

    private static string? Section(string html, string title)
    {
        var decoded = WebUtility.HtmlDecode(html);
        var pattern = $@"<b>\s*{Regex.Escape(title)}\s*</b>\s*<br\s*/?>(.*?)(?=<p>\s*<b>|</blockquote>|$)";
        var match = Regex.Match(decoded, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value : null;
    }
}
