using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace KolArbitrage.Core.Integration;

public sealed record KoLClanStashItem(string ItemId, string Name, int Quantity);

public sealed record KoLClanStashSnapshot(bool CanRead, IReadOnlyList<KoLClanStashItem> Items);

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
