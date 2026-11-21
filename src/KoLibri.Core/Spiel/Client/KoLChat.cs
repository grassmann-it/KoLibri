using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KoLibri.Core.Spiel;

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
