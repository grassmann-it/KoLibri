using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace KoLibri.Core.Spiel;

/// <summary>One item the fight page reported as acquired.</summary>
public sealed record QuestDrop
{
    public required string ItemName { get; init; }
    public int Quantity { get; init; } = 1;
}

/// <summary>
/// Reads the loot from a fight page.
///
/// KoL announces the loot in the fight narration: "You acquire an item:
/// really thick spine". The quantities sometimes come along in parentheses
/// ("Mer-kin knucklebone (10)"). The tracker counts them so the grind can
/// decide whether the goal is reached without a second inventory read.
/// </summary>
public static class DropTracker
{
    /// <summary>
    /// KoL announces the loot in a table row of the fight narration:
    /// <c>You acquire an item: &lt;b&gt;knucklebone&lt;/b&gt;</c>. The item's
    /// name sits in the bold part, so the reader takes the bold text when
    /// there is one and falls back to the plain text otherwise.
    /// </summary>
    private static readonly Regex AcquirePattern = new(
        @"You acquire(?:(?: an item|\s+items?)?:\s*|\s+(?=<[bi]>))(?:<b>(?<bold>[^<]*)</b>|<i>(?<italic>[^<]*)</i>|(?<plain>[^<\r\n]+))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex QuantityPattern = new(
        @"(?:^\s*(?<quantity>\d+)\s+|\s*\((?<quantity>\d+)\)\s*$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TagPattern = new(
        @"<[^>]+>",
        RegexOptions.Compiled);

    /// <summary>Extracts the acquired items from a fight page.</summary>
    public static IReadOnlyList<QuestDrop> ReadDrops(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return Array.Empty<QuestDrop>();
        var drops = new List<QuestDrop>();
        foreach (Match match in AcquirePattern.Matches(html))
        {
            var raw = match.Groups["bold"].Success ? match.Groups["bold"].Value
                : match.Groups["italic"].Success ? match.Groups["italic"].Value
                : match.Groups["plain"].Value;
            raw = TagPattern.Replace(raw, " ");
            raw = WebUtility.HtmlDecode(raw);
            var quantity = 1;
            var quantityMatch = QuantityPattern.Match(raw);
            if (quantityMatch.Success
                && int.TryParse(quantityMatch.Groups["quantity"].Value,
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                quantity = parsed;
                raw = QuantityPattern.Replace(raw, " ");
            }
            var name = raw.Trim();
            if (name.Length == 0) continue;
            drops.Add(new QuestDrop { ItemName = name, Quantity = quantity });
        }
        return drops;
    }

    /// <summary>Counts how many of the named item the drops hold. The
    /// comparison ignores case and the item's plural "s", because KoL uses
    /// both forms.</summary>
    public static int CountOf(IEnumerable<QuestDrop> drops, string itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName)) return 0;
        var wanted = Normalize(itemName);
        var total = 0;
        foreach (var drop in drops)
        {
            if (Normalize(drop.ItemName) == wanted) total += drop.Quantity;
        }
        return total;
    }

    private static string Normalize(string value)
    {
        var trimmed = value.Trim().ToLowerInvariant();
        return trimmed.EndsWith('s') ? trimmed[..^1] : trimmed;
    }
}
