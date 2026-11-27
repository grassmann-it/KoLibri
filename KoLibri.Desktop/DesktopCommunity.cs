using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

// Copied from KoLibri.Core (0.5 step 3): clan profile/members, clan VIP lounge, clan stash and KoL chat
// (Spiel/Client/KoLClanProfile.cs, KoLClanLounge.cs, KoLClanStash.cs, KoLChat.cs and the matching
// KoLAccountReadClient methods). The KoLibri library has no clan or chat API, so the desktop sends the same
// requests (same paths and form fields) through session.GetPageAsync/PostAsync: allowlist, limiter, guard and
// the automatic pwd are the library's. A login page ends in SessionExpiredException there.
namespace KoLibri.Desktop;

public sealed record KoLClanProfile(string? ClanId, string? Name, string? Title)
{
    public bool IsMember => ClanId is not null;
}

public sealed record KoLClanMember(string PlayerId, string Name, string Title, int Level)
{
    public string? Rank { get; init; }
}

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

public sealed record KoLClanStashItem(string ItemId, string Name, int Quantity);

public sealed record KoLClanStashSnapshot(bool CanRead, IReadOnlyList<KoLClanStashItem> Items);

public sealed record KoLChatMessage(string? Id, string Channel, string Sender, string Text)
{
    public string? SenderId { get; init; }
}

public sealed record KoLChatBatch(long Cursor, IReadOnlyList<KoLChatMessage> Messages)
{
    public string? Output { get; init; }
    public bool DeliveryConfirmed { get; init; }
}

public sealed record KoLChatContact(string PlayerId, string Name);

/// <summary>
/// Clan and chat for the desktop, as extensions of <see cref="DesktopLiveDataProvider"/> (they were provider methods
/// before 0.5). Reads return null for a signed-out role; actions throw <see cref="InvalidOperationException"/> then.
/// </summary>
public static class DesktopCommunity
{
    private const string DrinkMePotionId = "4508";

    // ---------------------------------------------------------------- clan profile and members

    public static async Task<KoLClanProfile?> ReadClanProfileAsync(
        this DesktopLiveDataProvider live, AccountRole role, CancellationToken cancellationToken)
    {
        if (await live.SessionAsync(role, cancellationToken).ConfigureAwait(false) is not { } session) return null;
        var status = await session.Character.GetStatusAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (status.PlayerId is not { } playerId || playerId <= 0)
            throw new InvalidOperationException("KoL status did not include the character ID.");
        var id = playerId.ToString(CultureInfo.InvariantCulture);
        var html = await ReadAsync(session, $"showplayer.php?who={id}", cancellationToken).ConfigureAwait(false);
        return KoLClanProfileParser.Parse(html, id);
    }

    public static async Task<IReadOnlyList<KoLClanMember>?> ReadClanMembersAsync(
        this DesktopLiveDataProvider live, AccountRole role, string clanId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clanId);
        if (clanId.Any(character => character is < '0' or > '9'))
            throw new ArgumentException("Clan ID must contain only digits.", nameof(clanId));
        if (await live.SessionAsync(role, cancellationToken).ConfigureAwait(false) is not { } session) return null;

        var members = new Dictionary<string, KoLClanMember>(StringComparer.Ordinal);
        for (var page = 0; ; page++)
        {
            var html = await ReadAsync(session, FormattableString.Invariant($"showclan.php?whichclan={clanId}&page={page}"),
                cancellationToken).ConfigureAwait(false);
            var previousCount = members.Count;
            foreach (var member in KoLClanProfileParser.ParseMembers(html))
                members.TryAdd(member.PlayerId, member);
            if (members.Count == previousCount)
                throw new InvalidOperationException("KoL did not return a readable clan roster page.");
            if (!html.Contains("next page &gt;&gt;", StringComparison.OrdinalIgnoreCase))
                break;
        }

        var ranks = KoLClanProfileParser.ParseRanks(
            await ReadAsync(session, "clan_detailedroster.php", cancellationToken).ConfigureAwait(false));
        return members.Values.Select(member => ranks.TryGetValue(member.Name, out var rank)
            ? member with { Rank = rank }
            : member).ToArray();
    }

    // ---------------------------------------------------------------- clan VIP lounge (experimental tab)

    public static async Task<KoLClanLoungeSnapshot?> ReadClanLoungeAsync(
        this DesktopLiveDataProvider live, AccountRole role, CancellationToken cancellationToken)
    {
        if (await live.SessionAsync(role, cancellationToken).ConfigureAwait(false) is not { } session) return null;
        var ground = await ReadClanLoungeFloorAsync(session, 1, cancellationToken).ConfigureAwait(false);
        if (ground is null || !KoLClanLoungeParser.IsFloor(ground, 1))
            return new(false, []);

        var attic = await ReadClanLoungeFloorAsync(session, 2, cancellationToken).ConfigureAwait(false);
        if (attic is null || !KoLClanLoungeParser.IsFloor(attic, 2))
            throw new InvalidOperationException("KoL did not return the clan lounge attic.");

        return new(true, KoLClanLoungeParser.ParseFacilities(ground, 1)
            .Concat(KoLClanLoungeParser.ParseFacilities(attic, 2))
            .DistinctBy(facility => facility.Action, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public static async Task<bool> UseClanLookingGlassAsync(
        this DesktopLiveDataProvider live, AccountRole role, CancellationToken cancellationToken)
    {
        var session = await RequireAsync(live, role, "use the clan looking glass", cancellationToken).ConfigureAwait(false);
        var before = await ReadLookingGlassPotionCountAsync(session, cancellationToken).ConfigureAwait(false);
        await SubmitClanLoungeActionAsync(session, "lookingglass", "2", cancellationToken).ConfigureAwait(false);
        return await ReadLookingGlassPotionCountAsync(session, cancellationToken).ConfigureAwait(false) > before;
    }

    public static async Task<KoLCrimboTreeResult> UseClanCrimboTreeAsync(
        this DesktopLiveDataProvider live, AccountRole role, CancellationToken cancellationToken)
    {
        var session = await RequireAsync(live, role, "check the clan Crimbo Tree", cancellationToken).ConfigureAwait(false);
        var html = await SubmitClanLoungeActionAsync(session, "crimbotree", null, cancellationToken).ConfigureAwait(false);
        return KoLClanLoungeParser.ParseCrimboTree(html);
    }

    public static async Task<KoLClanKlawResult> PlayClanKlawAsync(
        this DesktopLiveDataProvider live, AccountRole role, CancellationToken cancellationToken)
    {
        var session = await RequireAsync(live, role, "play the clan Klaw", cancellationToken).ConfigureAwait(false);
        var html = await SubmitClanLoungeActionAsync(session, "klaw", null, cancellationToken).ConfigureAwait(false);
        return KoLClanLoungeParser.ParseKlaw(html);
    }

    public static async Task<string> UseClanHotTubAsync(
        this DesktopLiveDataProvider live, AccountRole role, bool allowRemovingCurse, CancellationToken cancellationToken)
    {
        var session = await RequireAsync(live, role, "use the clan Hot Tub", cancellationToken).ConfigureAwait(false);
        var ground = await ReadClanLoungeFloorAsync(session, 1, cancellationToken).ConfigureAwait(false);
        var before = ground is null ? null : KoLClanLoungeParser.ReadHotTubSoaks(ground);
        if (before is null) return "Hot Tub availability could not be verified.";
        if (before == 0) return "No Hot Tub soaks left today.";

        if (!allowRemovingCurse)
        {
            Status status;
            try { status = await session.Character.GetStatusAsync(refresh: true, cancellationToken).ConfigureAwait(false); }
            catch (KolibriException) { return "Live effects could not be verified."; }
            if (status.Effects.Any(effect => effect.Name is "Once-Cursed" or "Twice-Cursed" or "Thrice-Cursed"))
                return "The Hot Tub would remove a Cursed effect. Allow curse removal to proceed.";
        }

        var html = await SubmitClanLoungeActionAsync(session, "hottub", null, cancellationToken).ConfigureAwait(false);
        var after = KoLClanLoungeParser.ReadHotTubSoaks(html);
        if (after is null)
        {
            ground = await ReadClanLoungeFloorAsync(session, 1, cancellationToken).ConfigureAwait(false);
            after = ground is null ? null : KoLClanLoungeParser.ReadHotTubSoaks(ground);
        }
        return after < before ? $"Hot Tub soak used. {after} left today." : "KoL did not confirm a Hot Tub soak.";
    }

    public static async Task<KoLClanPoolResult> PlayClanPoolAsync(
        this DesktopLiveDataProvider live, AccountRole role, KoLClanPoolStance stance, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(stance)) throw new ArgumentOutOfRangeException(nameof(stance));
        var session = await RequireAsync(live, role, "play clan pool", cancellationToken).ConfigureAwait(false);
        var html = await SubmitClanLoungeActionAsync(session, "poolgame", "2", cancellationToken,
            ("stance", ((int)stance).ToString(CultureInfo.InvariantCulture))).ConfigureAwait(false);
        return KoLClanLoungeParser.ParsePoolGame(html);
    }

    public static async Task<KoLClanShowerResult> TakeClanShowerAsync(
        this DesktopLiveDataProvider live, AccountRole role, KoLClanShowerTemperature temperature, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(temperature)) throw new ArgumentOutOfRangeException(nameof(temperature));
        var session = await RequireAsync(live, role, "use the April Shower", cancellationToken).ConfigureAwait(false);
        var html = await SubmitClanLoungeActionAsync(session, "takeshower", "2", cancellationToken,
            ("temperature", ((int)temperature).ToString(CultureInfo.InvariantCulture))).ConfigureAwait(false);
        return KoLClanLoungeParser.ParseShower(html);
    }

    public static async Task<KoLClanSwimResult> SwimInClanPoolAsync(
        this DesktopLiveDataProvider live, AccountRole role, KoLClanSwimWorkout workout, CancellationToken cancellationToken)
    {
        var subaction = workout switch
        {
            KoLClanSwimWorkout.Laps => "laps",
            KoLClanSwimWorkout.Sprints => "submarine",
            _ => throw new ArgumentOutOfRangeException(nameof(workout))
        };
        var session = await RequireAsync(live, role, "use the clan swimming pool", cancellationToken).ConfigureAwait(false);
        var html = await SubmitClanLoungeActionAsync(session, "goswimming", "2", cancellationToken,
            ("subaction", subaction)).ConfigureAwait(false);
        return KoLClanLoungeParser.ParseSwim(html, workout);
    }

    public static async Task<KoLClanPoolChoice?> ReadClanPoolChoiceAsync(
        this DesktopLiveDataProvider live, AccountRole role, CancellationToken cancellationToken)
    {
        if (await live.SessionAsync(role, cancellationToken).ConfigureAwait(false) is not { } session) return null;
        return await ReadClanPoolChoiceAsync(session, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Without a <paramref name="move"/>: the cannonball that opens the pool choice; otherwise one move in it.</summary>
    public static async Task<KoLClanPoolChoice> SubmitClanPoolChoiceAsync(
        this DesktopLiveDataProvider live, AccountRole role, KoLClanPoolMove? move, CancellationToken cancellationToken)
    {
        var session = await RequireAsync(live, role, "use the clan swimming pool", cancellationToken).ConfigureAwait(false);
        if (move is not { } selected)
        {
            var html = await SubmitClanLoungeActionAsync(session, "goswimming", "2", cancellationToken,
                ("subaction", "screwaround"), followPoolRedirect: true).ConfigureAwait(false);
            return KoLClanLoungeParser.ParsePoolChoice(html);
        }

        var action = selected switch
        {
            KoLClanPoolMove.Handstand => "flip",
            KoLClanPoolMove.Dive => "treasure",
            KoLClanPoolMove.Blink => "blink",
            KoLClanPoolMove.Leave => "leave",
            _ => throw new ArgumentOutOfRangeException(nameof(move))
        };
        if (!(await ReadClanPoolChoiceAsync(session, cancellationToken).ConfigureAwait(false)).IsActive)
            return new(false, null);
        var page = await session.PostAsync("choice.php", new Dictionary<string, string>
        {
            ["whichchoice"] = "585", ["option"] = "1", ["action"] = action
        }, RequestLane.Action, cancellationToken).ConfigureAwait(false);
        EnsurePoolPage(page);
        return KoLClanLoungeParser.ParsePoolChoice(page.Html, selected == KoLClanPoolMove.Dive);
    }

    public static async Task<string?> ReadClanPhotocopyMonsterAsync(
        this DesktopLiveDataProvider live, AccountRole role, CancellationToken cancellationToken)
    {
        const string photocopyDescriptionId = "835898159";
        if (await live.SessionAsync(role, cancellationToken).ConfigureAwait(false) is not { } session) return null;
        var html = await ReadAsync(session, $"desc_item.php?whichitem={photocopyDescriptionId}", cancellationToken).ConfigureAwait(false);
        return KoLClanLoungeParser.ParsePhotocopiedMonster(html);
    }

    public static async Task<bool> TransferClanFaxAsync(
        this DesktopLiveDataProvider live, AccountRole role, bool send, CancellationToken cancellationToken)
    {
        var session = await RequireAsync(live, role, "use the clan fax machine", cancellationToken).ConfigureAwait(false);
        var html = await SubmitClanLoungeActionAsync(session, send ? "sendfax" : "receivefax", "2", cancellationToken,
            usePreaction: true).ConfigureAwait(false);
        return html.Contains(send ? "Your photocopy slowly slides into the machine" : "top half of a document prints out",
            StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- clan stash (experimental tab)

    public static async Task<KoLClanStashSnapshot?> ReadClanStashAsync(
        this DesktopLiveDataProvider live, AccountRole role, CancellationToken cancellationToken)
    {
        if (await live.SessionAsync(role, cancellationToken).ConfigureAwait(false) is not { } session) return null;
        return KoLClanStashParser.Parse(await ReadAsync(session, "clan_stash.php", cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Takes (<paramref name="deposit"/> false) or deposits one item; true when KoL's answer confirms it.</summary>
    public static async Task<bool> TransferClanStashItemAsync(
        this DesktopLiveDataProvider live, AccountRole role, string itemId, int quantity, bool deposit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        if (itemId.Any(character => character is < '0' or > '9'))
            throw new ArgumentException("Item ID must contain only digits.", nameof(itemId));
        var session = await RequireAsync(live, role, "transfer an item to or from the clan stash", cancellationToken).ConfigureAwait(false);
        var page = await session.PostAsync("clan_stash.php", new Dictionary<string, string>
        {
            ["action"] = deposit ? "addgoodies" : "takegoodies",
            [deposit ? "item1" : "whichitem"] = itemId,
            [deposit ? "qty1" : "quantity"] = quantity.ToString(CultureInfo.InvariantCulture),
            ["ajax"] = "1"
        }, RequestLane.Action, cancellationToken).ConfigureAwait(false);
        return page.Html.Contains(deposit ? "to the Goodies Hoard" : "You acquire", StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- chat

    public static async Task<IReadOnlyList<KoLChatContact>?> ReadChatContactsAsync(
        this DesktopLiveDataProvider live, AccountRole role, CancellationToken cancellationToken)
    {
        if (await live.SessionAsync(role, cancellationToken).ConfigureAwait(false) is not { } session) return null;
        return KoLChatParser.ParseContacts(await ReadAsync(session, "account_contactlist.php", cancellationToken).ConfigureAwait(false));
    }

    public static async Task<KoLChatBatch?> ReadChatAsync(
        this DesktopLiveDataProvider live, AccountRole role, long cursor, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cursor);
        if (await live.SessionAsync(role, cancellationToken).ConfigureAwait(false) is not { } session) return null;
        var json = await ReadAsync(session, FormattableString.Invariant($"newchatmessages.php?j=1&lasttime={cursor}"),
            cancellationToken).ConfigureAwait(false);
        return KoLChatParser.Parse(json, cursor);
    }

    public static async Task<KoLChatBatch> SendChatAsync(
        this DesktopLiveDataProvider live, AccountRole role, string target, string message, long cursor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentOutOfRangeException.ThrowIfNegative(cursor);
        target = target.Trim();
        message = message.Trim();
        var channel = target.StartsWith('/');
        var name = channel ? target[1..] : target.Replace(' ', '_');
        if (name.Length == 0 || !name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))
            throw new ArgumentException("Choose a chat channel or enter a player name or ID.", nameof(target));
        if (message.StartsWith('/') || message.Any(character => char.IsControl(character) || character > 255))
            throw new ArgumentException("Enter one line of chat text using characters supported by KoL.", nameof(message));

        var session = await RequireAsync(live, role, "send KoL chat", cancellationToken).ConfigureAwait(false);
        var status = await session.Character.GetStatusAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (status.PlayerId is not { } id || id <= 0)
            throw new InvalidOperationException("KoL did not confirm the active character ID.");
        var playerId = id.ToString(CultureInfo.InvariantCulture);
        var hash = await session.GetPasswordHashAsync(cancellationToken).ConfigureAwait(false);
        var graf = channel ? $"/{name} {message}" : $"/msg {name} {message}";
        var encoded = string.Concat(System.Text.Encoding.Latin1.GetBytes(graf).Select(value => $"%{value:X2}"));
        var route = $"submitnewchat.php?j=1&pwd={Uri.EscapeDataString(hash)}&playerid={playerId}&graf={encoded}";
        var reply = await session.GetPageAsync(route, null, RequestLane.Action, cancellationToken).ConfigureAwait(false);
        var batch = KoLChatParser.Parse(reply.Html, cursor, requireCursor: false);
        var expectedChannel = channel ? "/" + name : "Private";
        return batch with
        {
            DeliveryConfirmed = batch.Messages.Any(entry => entry.SenderId == playerId
                && entry.Channel.Equals(expectedChannel, StringComparison.OrdinalIgnoreCase) && entry.Text == message)
        };
    }

    // ---------------------------------------------------------------- requests

    private static async Task<KolibriSession> RequireAsync(
        DesktopLiveDataProvider live, AccountRole role, string what, CancellationToken cancellationToken)
        => await live.SessionAsync(role, cancellationToken).ConfigureAwait(false)
           ?? throw new InvalidOperationException($"Sign in to {what}.");

    private static async Task<string> ReadAsync(KolibriSession session, string route, CancellationToken cancellationToken)
        => (await session.GetPageAsync(route, null, RequestLane.Read, cancellationToken).ConfigureAwait(false)).Html;

    /// <summary>A lounge floor, or null when KoL sends the character to the clan sign-up page (no clan / no VIP key).</summary>
    private static async Task<string?> ReadClanLoungeFloorAsync(KolibriSession session, int floor, CancellationToken cancellationToken)
    {
        var page = await session.GetPageAsync(floor == 2 ? "clan_viplounge.php?whichfloor=2" : "clan_viplounge.php",
            null, RequestLane.Read, cancellationToken).ConfigureAwait(false);
        return page.Url.AbsolutePath.Equals("/clan_signup.php", StringComparison.OrdinalIgnoreCase) ? null : page.Html;
    }

    private static async Task<string> SubmitClanLoungeActionAsync(
        KolibriSession session, string action, string? floor, CancellationToken cancellationToken,
        (string Field, string Value)? option = null, bool followPoolRedirect = false, bool usePreaction = false)
    {
        var fields = new Dictionary<string, string> { [option is not null || usePreaction ? "preaction" : "action"] = action };
        if (option is { } selectedOption)
            fields[selectedOption.Field] = selectedOption.Value;
        if (floor is not null) fields["whichfloor"] = floor;
        var page = await session.PostAsync("clan_viplounge.php", fields, RequestLane.Action, cancellationToken).ConfigureAwait(false);
        if (followPoolRedirect) EnsurePoolPage(page);
        return page.Html;
    }

    /// <summary>The library follows redirects; the pool may only lead to the choice or back to the lounge (as in Core).</summary>
    private static void EnsurePoolPage(Page page)
    {
        if (page.Url.AbsolutePath is not ("/choice.php" or "/clan_viplounge.php"))
            throw new InvalidOperationException("Unexpected clan pool redirect.");
    }

    private static async Task<KoLClanPoolChoice> ReadClanPoolChoiceAsync(KolibriSession session, CancellationToken cancellationToken)
        => KoLClanLoungeParser.ParsePoolChoice(await ReadAsync(session, "choice.php", cancellationToken).ConfigureAwait(false));

    private static async Task<int> ReadLookingGlassPotionCountAsync(KolibriSession session, CancellationToken cancellationToken)
    {
        var inventory = await session.Inventory.GetAsync(refresh: true, cancellationToken).ConfigureAwait(false);
        return inventory.Count(int.Parse(DrinkMePotionId, CultureInfo.InvariantCulture));
    }
}

// ---------------------------------------------------------------- parsers (copied from KoLibri.Core, 0.5 step 3)

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
            int.Parse(match.Groups["level"].Value, CultureInfo.InvariantCulture))).ToArray();

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
        var active = HasChoice(html, 585);
        var treasure = diving ? PoolTreasure().Match(html).Groups["item"].Value : "";
        return new(active, treasure.Length > 0 ? WebUtility.HtmlDecode(treasure) : null);
    }

    /// <summary>As Core's EncounterChoiceReader: whichchoice fields paired with option fields, in page order.</summary>
    private static bool HasChoice(string html, int whichChoice)
    {
        if (string.IsNullOrWhiteSpace(html)) return false;
        var whichs = ChoiceField().Matches(html);
        var count = Math.Min(whichs.Count, OptionField().Matches(html).Count);
        for (var i = 0; i < count; i++)
            if (int.TryParse(whichs[i].Groups["which"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var which)
                && which == whichChoice)
                return true;
        return false;
    }

    [GeneratedRegex("<input[^>]*name=[\"']?whichchoice[\"']?[^>]*value=[\"']?(?<which>\\d+)[\"']?", RegexOptions.IgnoreCase)]
    private static partial Regex ChoiceField();

    [GeneratedRegex("<input[^>]*name=[\"']?option[\"']?[^>]*value=[\"']?(?<option>\\d+)[\"']?", RegexOptions.IgnoreCase)]
    private static partial Regex OptionField();

    [GeneratedRegex(@"var state =.*?found a (?<item>[^<!]+)!", RegexOptions.IgnoreCase)]
    private static partial Regex PoolTreasure();

    internal static KoLClanSwimResult ParseSwim(string html, KoLClanSwimWorkout workout)
    {
        if (html.Contains("already worked out in the pool today", StringComparison.OrdinalIgnoreCase))
            return new(null, true);

        var match = (workout == KoLClanSwimWorkout.Laps ? SwimLaps() : SwimSprints()).Match(html);
        return new(int.TryParse(match.Groups["count"].Value, NumberStyles.AllowThousands,
            CultureInfo.InvariantCulture, out var count) ? count : null, false);
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

internal static partial class KoLClanStashParser
{
    public static KoLClanStashSnapshot Parse(string html)
    {
        var form = TakeForm().Match(html);
        if (!form.Success) return new(false, []);

        var items = new List<KoLClanStashItem>();
        foreach (Match match in Item().Matches(form.Value))
        {
            var count = match.Groups["count"];
            var quantity = 1;
            if (count.Success && (!int.TryParse(count.Value, NumberStyles.AllowThousands,
                    CultureInfo.InvariantCulture, out quantity) || quantity <= 0))
                continue;
            items.Add(new(match.Groups["id"].Value, WebUtility.HtmlDecode(match.Groups["name"].Value).Trim(), quantity));
        }

        return new(true, items);
    }

    [GeneratedRegex("""<form\b[^>]*\bname\s*=\s*["']?takegoodies\b[^>]*>.*?</select>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TakeForm();

    [GeneratedRegex("""<option\b[^>]*\bvalue\s*=\s*["']?(?<id>[0-9]+)["']?\s+descid\s*=\s*["']?[0-9]+["']?[^>]*>(?<name>[^<]*?)(?:\s+\((?<count>[0-9,]+)\))?(?:\s+\(-[0-9,]*\))?</option>""",
        RegexOptions.IgnoreCase)]
    private static partial Regex Item();
}

internal static partial class KoLChatParser
{
    internal static KoLChatBatch Parse(string json, long previousCursor, bool requireCursor = true)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("msgs", out var entries) || entries.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("KoL did not return a readable chat feed.");

        if (!long.TryParse(Value(root, "last"), NumberStyles.None, CultureInfo.InvariantCulture, out var last)
            && requireCursor)
            throw new InvalidOperationException("KoL did not return a chat cursor.");

        var messages = new List<KoLChatMessage>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || Value(entry, "msg") is not { } message) continue;
            var id = Value(entry, "mid");
            if (long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var numericId)
                && numericId > 0 && numericId <= previousCursor) continue;
            var type = Value(entry, "type");
            var channel = type switch
            {
                "public" => "/" + (Value(entry, "channel") ?? "public").TrimStart('/'),
                "private" => "Private",
                _ => "System"
            };
            var sender = entry.TryGetProperty("who", out var who) && who.ValueKind == JsonValueKind.Object
                ? Value(who, "name") : null;
            messages.Add(new(id, channel, Clean(sender ?? type ?? "KoL"), Clean(message))
            {
                SenderId = who.ValueKind == JsonValueKind.Object ? Value(who, "id") : null
            });
        }
        return new(Math.Max(previousCursor, last), messages) { Output = Value(root, "output") is { } output ? Clean(output) : null };
    }

    internal static IReadOnlyList<KoLChatContact> ParseContacts(string html)
    {
        var list = ContactList().Match(html);
        if (!list.Success)
            throw new InvalidOperationException("KoL did not return a readable contact list.");

        return ContactEntry().Matches(list.Value)
            .Select(match => new KoLChatContact(match.Groups["id"].Value, Clean(match.Groups["name"].Value)))
            .DistinctBy(contact => contact.PlayerId).ToArray();
    }

    [GeneratedRegex(@"<b\b[^>]*>\s*Contact List\s*</b>.*?</table>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ContactList();

    [GeneratedRegex("""<a\b[^>]*?\bhref\s*=\s*["']showplayer\.php\?who=(?<id>[0-9]+)["'][^>]*>\s*<b\b[^>]*>(?<name>.*?)</b>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ContactEntry();

    private static string? Value(JsonElement entry, string property)
        => entry.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? value.ToString() : null;

    private static string Clean(string html) => WebUtility.HtmlDecode(HtmlTags().Replace(html, "")).Trim();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTags();
}
