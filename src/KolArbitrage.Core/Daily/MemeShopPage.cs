using System.Globalization;
using System.Text.RegularExpressions;

namespace KolArbitrage.Core.Daily;

/// <summary>One purchasable entry of the Internet Meme Shop (<c>shop.php?whichshop=bacon</c>).</summary>
public sealed record MemeShopRow(string ItemName, int RowId);

/// <summary>
/// The daily items of the Internet Meme Shop. Prices and the once-per-day rule
/// come from the KoL wiki; KoLmafia's MemeShopRequest treats an item that is
/// missing from the shop page as already bought today.
/// </summary>
public static class MemeShopCatalog
{
    public const string ShopId = "bacon";
    public const int BaconItemId = 8763;
    public const int InfiniteBaconMachineItemId = 9025;

    /// <summary>Items sold once per day, in the order they are bought.</summary>
    public static readonly IReadOnlyList<(string Name, int Price)> DailyItems = new[]
    {
        ("viral video", 20),
        ("plus one", 74),
        ("gallon of milk", 100),
        ("print screen button", 111),
        ("daily dungeon malware", 150)
    };

    public static int DailyTotalPrice => DailyItems.Sum(i => i.Price);
}

/// <summary>Parsed Internet Meme Shop page: the BACON balance and the rows still on offer.</summary>
public sealed class MemeShopPage
{
    private static readonly Regex RowBlock = new(
        @"<tr\b[^>]*>(?<body>.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RowId = new(
        @"(?:name\s*=\s*[""']?whichrow[""']?\s+value\s*=\s*[""']?(?<a>\d+)|value\s*=\s*[""']?(?<b>\d+)[""']?\s+name\s*=\s*[""']?whichrow|\brel\s*=\s*[""']?(?<c>\d+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Balance = new(
        @"(?<n>[\d,]+)(?:\s|<[^>]*>|&nbsp;)*BACON", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public required IReadOnlyList<MemeShopRow> Rows { get; init; }

    /// <summary>The session's request hash (<c>pwd</c>) embedded in the page's buy buttons, if any.</summary>
    public string? Pwd { get; init; }

    /// <summary>BACON the page reports, or null when it shows none.</summary>
    public int? Bacon { get; init; }

    public MemeShopRow? Find(string itemName)
        => Rows.FirstOrDefault(r => string.Equals(r.ItemName, itemName, StringComparison.OrdinalIgnoreCase));

    public static MemeShopPage Parse(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var rows = new List<MemeShopRow>();
        foreach (Match block in RowBlock.Matches(html))
        {
            var body = block.Groups["body"].Value;
            var idMatch = RowId.Match(body);
            if (!idMatch.Success) continue;
            var id = new[] { "a", "b", "c" }.Select(g => idMatch.Groups[g]).First(g => g.Success).Value;
            foreach (var (name, _) in MemeShopCatalog.DailyItems.Concat(
                new[] { ("internet point", 0), ("meme generator", 0), ("infinite BACON machine", 0), ("First Post shirt package", 0) }))
            {
                if (body.Contains(name, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rowId))
                {
                    rows.Add(new MemeShopRow(name, rowId));
                    break;
                }
            }
        }
        int? bacon = null;
        var tail = html.IndexOf("You have", StringComparison.OrdinalIgnoreCase);
        var balance = Balance.Match(tail >= 0 ? html[tail..] : html);
        if (balance.Success && int.TryParse(balance.Groups["n"].Value.Replace(",", ""),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            bacon = n;
        var pwd = Regex.Match(html, @"pwd=(?<h>[0-9a-fA-F]{16,})");
        return new MemeShopPage { Rows = rows, Bacon = bacon, Pwd = pwd.Success ? pwd.Groups["h"].Value : null };
    }
}
