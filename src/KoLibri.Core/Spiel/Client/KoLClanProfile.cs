using System.Net;
using System.Text.RegularExpressions;

namespace KoLibri.Core.Spiel;

public sealed record KoLClanProfile(string? ClanId, string? Name, string? Title)
{
    public bool IsMember => ClanId is not null;
}

public sealed record KoLClanMember(string PlayerId, string Name, string Title, int Level)
{
    public string? Rank { get; init; }
}

internal static partial class KoLClanProfileParser
{
    public static KoLClanProfile Parse(string html, string playerId)
    {
        if (!PlayerHeading().Matches(html).Any(match => match.Groups["id"].Value == playerId))
            throw new InvalidOperationException("KoL did not return the selected character's profile.");

        var clan = ClanLink().Match(html);
        if (!clan.Success)
        {
            if (ClanLabel().IsMatch(html))
                throw new InvalidOperationException("KoL returned an unrecognized clan entry.");
            return new(null, null, null);
        }

        var title = clan.Groups["title"];
        return new(clan.Groups["id"].Value, WebUtility.HtmlDecode(clan.Groups["name"].Value).Trim(),
            title.Success ? WebUtility.HtmlDecode(title.Value).Trim() : null);
    }

    public static IReadOnlyList<KoLClanMember> ParseMembers(string html)
        => MemberRow().Matches(html).Select(match => new KoLClanMember(
            match.Groups["id"].Value,
            WebUtility.HtmlDecode(match.Groups["name"].Value).Trim(),
            WebUtility.HtmlDecode(match.Groups["title"].Value).Trim(),
            int.Parse(match.Groups["level"].Value, System.Globalization.CultureInfo.InvariantCulture))).ToArray();

    public static IReadOnlyDictionary<string, string> ParseRanks(string html)
    {
        var ranks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match row in RosterRow().Matches(html))
        {
            var cells = RosterCell().Matches(row.Value);
            // Detailed roster columns: name, class, three stats, power, three run/PvP counts, rank, karma.
            if (cells.Count != 11) continue;
            var name = CellText(cells[0]);
            var rank = CellText(cells[9]);
            if (name.Length > 0 && rank.Length > 0)
                ranks.TryAdd(name, rank);
        }
        return ranks;
    }

    private static string CellText(Match match)
        => WebUtility.HtmlDecode(HtmlTag().Replace(match.Groups["value"].Value, "")).Trim();

    [GeneratedRegex(@"<tr\b[^>]*>(.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex RosterRow();

    [GeneratedRegex(@"<td\b[^>]*>(?<value>.*?)</td>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex RosterCell();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex("""<a\b[^>]*href\s*=\s*["']showplayer\.php\?who=(?<id>[0-9]+)["'][^>]*>(?<name>[^<]+)</a>\s*</b>\s*(?:&nbsp;)?\s*</td>\s*<td\b[^>]*>(?<title>[^<]*)</td>\s*<td\b[^>]*>\s*(?<level>[0-9]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex MemberRow();

    [GeneratedRegex(@"</b>\s*\(#(?<id>[0-9]+)\)\s*<br\b", RegexOptions.IgnoreCase)]
    private static partial Regex PlayerHeading();

    [GeneratedRegex(@"<td\b[^>]*>\s*Clan:", RegexOptions.IgnoreCase)]
    private static partial Regex ClanLabel();

    [GeneratedRegex("""\bClan:\s*<b>\s*<a\b[^>]*\bhref\s*=\s*["']showclan\.php\?whichclan=(?<id>[0-9]+)["'][^>]*>(?<name>[^<]+)</a>\s*</b>(?:\s*<br\s*/?>\s*Title:\s*<b>(?<title>[^<]+)</b>)?""",
        RegexOptions.IgnoreCase)]
    private static partial Regex ClanLink();
}
