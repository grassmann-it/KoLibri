using System.Net;
using System.Text.RegularExpressions;

namespace KolArbitrage.Core.Spiel;

public sealed record KoLClanLoungeFacility(string Action, string Name, int Floor)
{
    public string FloorName => Floor == 2 ? "Attic" : "Ground floor";
}

public sealed record KoLClanLoungeSnapshot(bool HasAccess, IReadOnlyList<KoLClanLoungeFacility> Facilities);

public sealed record KoLCrimboTreeResult(bool ReceivedGift, int? DaysUntilGift);

public enum KoLClanKlawResult { Unconfirmed, Played, Prize, DailyLimit }

public enum KoLClanPoolStance { Aggressive = 1, Strategic = 2, Stylish = 3 }

public enum KoLClanPoolResult { Unconfirmed, Won, Lost, PlayedAgainstSelf, DailyLimit }

public enum KoLClanShowerTemperature { Cold = 1, Cool, Lukewarm, Warm, Hot }

public enum KoLClanShowerResult { Unconfirmed, ShowerTaken, DailyLimit }

public enum KoLClanSwimWorkout { Laps = 1, Sprints }

public sealed record KoLClanSwimResult(int? Repetitions, bool DailyLimit);

public enum KoLClanPoolMove { Handstand, Dive, Blink, Leave }

public sealed record KoLClanPoolChoice(bool IsActive, string? Treasure);

internal static partial class KoLClanLoungeParser
{
    internal static bool IsFloor(string html, int floor)
        => html.Contains(floor == 2 ? "Clan VIP Lounge (Attic)" : "Clan VIP Lounge (Ground Floor)",
            StringComparison.OrdinalIgnoreCase);

    internal static IEnumerable<KoLClanLoungeFacility> ParseFacilities(string html, int floor)
        => FacilityLink().Matches(html).Select(match => new KoLClanLoungeFacility(
            match.Groups["action"].Value, WebUtility.HtmlDecode(match.Groups["name"].Value).Trim(), floor));

    internal static string? ParsePhotocopiedMonster(string html)
    {
        var match = PhotocopyLikeness().Match(html);
        return match.Success ? WebUtility.HtmlDecode(match.Groups["name"].Value).Trim() : null;
    }

    [GeneratedRegex(@"blurry likeness of (?:an? )?(?<name>[^<]+?) on it\s*<!--\s*monsterid:\s*[0-9]+\s*-->", RegexOptions.IgnoreCase)]
    private static partial Regex PhotocopyLikeness();

    internal static KoLClanPoolChoice ParsePoolChoice(string html, bool diving = false)
    {
        var active = KolArbitrage.Core.Spiel.EncounterChoiceReader.ReadChoices(html).Any(choice => choice.WhichChoice == 585);
        var treasure = diving ? PoolTreasure().Match(html).Groups["item"].Value : "";
        return new(active, treasure.Length > 0 ? WebUtility.HtmlDecode(treasure) : null);
    }

    [GeneratedRegex(@"var state =.*?found a (?<item>[^<!]+)!", RegexOptions.IgnoreCase)]
    private static partial Regex PoolTreasure();

    internal static KoLClanSwimResult ParseSwim(string html, KoLClanSwimWorkout workout)
    {
        if (html.Contains("already worked out in the pool today", StringComparison.OrdinalIgnoreCase))
            return new(null, true);

        var match = (workout == KoLClanSwimWorkout.Laps ? SwimLaps() : SwimSprints()).Match(html);
        return new(int.TryParse(match.Groups["count"].Value, System.Globalization.NumberStyles.AllowThousands,
            System.Globalization.CultureInfo.InvariantCulture, out var count) ? count : null, false);
    }

    [GeneratedRegex(@"manage to swim (?<count>[0-9,]+) before")]
    private static partial Regex SwimLaps();

    [GeneratedRegex(@"you do (?<count>[0-9,]+) of them")]
    private static partial Regex SwimSprints();

    internal static KoLClanShowerResult ParseShower(string html)
    {
        if (html.Contains("already had a shower today", StringComparison.OrdinalIgnoreCase))
            return KoLClanShowerResult.DailyLimit;
        if (html.Contains("this is way too hot", StringComparison.OrdinalIgnoreCase)
            || html.Contains("relaxes your muscles", StringComparison.OrdinalIgnoreCase)
            || html.Contains("mind expands", StringComparison.OrdinalIgnoreCase)
            || html.Contains("your goosebumps absorb", StringComparison.OrdinalIgnoreCase)
            || html.Contains("shards of frosty double-ice", StringComparison.OrdinalIgnoreCase))
            return KoLClanShowerResult.ShowerTaken;
        return KoLClanShowerResult.Unconfirmed;
    }

    internal static KoLClanPoolResult ParsePoolGame(string html)
    {
        if (html.Contains("take control of the table", StringComparison.Ordinal))
            return KoLClanPoolResult.Won;
        if (html.Contains("you are unable to defeat", StringComparison.Ordinal))
            return KoLClanPoolResult.Lost;
        if (html.Contains("play a game of pool against yourself", StringComparison.Ordinal))
            return KoLClanPoolResult.PlayedAgainstSelf;
        if (html.Contains("pooled out for today", StringComparison.Ordinal)
            || html.Contains("hands in your pockets", StringComparison.Ordinal))
            return KoLClanPoolResult.DailyLimit;
        return KoLClanPoolResult.Unconfirmed;
    }

    internal static int? ReadHotTubSoaks(string html)
        => int.TryParse(HotTubImage().Match(html).Groups["count"].Value, out var count) ? count : null;

    [GeneratedRegex(@"hottub(?<count>[0-5])\.gif", RegexOptions.IgnoreCase)]
    private static partial Regex HotTubImage();

    internal static KoLCrimboTreeResult ParseCrimboTree(string html)
    {
        if (html.Contains("You look under the Crimbo Tree and find a present", StringComparison.Ordinal))
            return new(true, 7);
        if (html.Contains("Check back tomorrow", StringComparison.Ordinal))
            return new(false, 1);
        if (html.Contains("There's nothing under the Crimbo Tree", StringComparison.Ordinal)
            && int.TryParse(TreeDays().Match(html).Groups["days"].Value, out var days))
            return new(false, days);
        return new(false, null);
    }

    internal static KoLClanKlawResult ParseKlaw(string html)
    {
        if (html.Contains("claw slowly descends", StringComparison.Ordinal))
            return html.Contains("You acquire", StringComparison.Ordinal)
                ? KoLClanKlawResult.Prize : KoLClanKlawResult.Played;
        if (WebUtility.HtmlDecode(html).Contains("you wouldn't want to look greedy", StringComparison.Ordinal))
            return KoLClanKlawResult.DailyLimit;
        return KoLClanKlawResult.Unconfirmed;
    }

    [GeneratedRegex(@"Check back in (?<days>[0-9]+) day")]
    private static partial Regex TreeDays();

    [GeneratedRegex("""<a\b[^>]*?\bhref\s*=\s*["']?clan_viplounge\.php\?action=(?<action>[a-z0-9_]+)(?:[^>]*?)>\s*<img\b[^>]*?\balt\s*=\s*(?<quote>["'])(?<name>.*?)\k<quote>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FacilityLink();
}
