using System.Text.RegularExpressions;
using KolArbitrage.Core.Accounts;

namespace KolArbitrage.Core.Daily;

/// <summary>
/// Reads the campground's "Pile of Burning Leaves" page and extracts the
/// form KoL expects for burning leaves.
///
/// KoLmafia visits <c>campground.php?preaction=leaves</c> and then submits
/// the creation request. The page carries the leaf count and the "stoked
/// the fire" counter, plus the exact form fields — which changed over the
/// years, so we read them from the page instead of hardcoding them.
/// </summary>
public sealed class BurningLeavesPage
{
    /// <summary>Leaves the operator currently holds.</summary>
    public int Leaves { get; init; }

    /// <summary>How many leaves were already burned today.</summary>
    public int BurnedToday { get; init; }

    /// <summary>The form action the page posts to.</summary>
    public string? FormAction { get; init; }

    /// <summary>The hidden form fields the page carries (without the
    /// whichchoice/option pair, which the caller supplies).</summary>
    public IReadOnlyDictionary<string, string> Fields { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex LeavesPattern = new(
        @"You have <b>(?<count>[\d,]+)</b> leaves", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex StokedPattern = new(
        @"stoked the fire with <b>(?<count>[\d,]+)</b>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FormPattern = new(
        @"<form[^>]*action\s*=\s*[""'](?<action>[^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HiddenPattern = new(
        @"<input[^>]*type\s*=\s*[""']?hidden[""']?[^>]*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NamePattern = new(
        @"name\s*=\s*[""'](?<name>[^""']+)[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ValuePattern = new(
        @"value\s*=\s*[""'](?<value>[^""']*)[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Parses the burning-leaves page.</summary>
    public static BurningLeavesPage Parse(string html)
    {
        var leaves = 0;
        var burned = 0;
        if (string.IsNullOrWhiteSpace(html)) return new BurningLeavesPage();

        var leavesMatch = LeavesPattern.Match(html);
        if (leavesMatch.Success)
            leaves = ParseNumber(leavesMatch.Groups["count"].Value);
        var stokedMatch = StokedPattern.Match(html);
        if (stokedMatch.Success)
            burned = ParseNumber(stokedMatch.Groups["count"].Value);

        var formMatch = FormPattern.Match(html);
        var action = formMatch.Success ? formMatch.Groups["action"].Value : null;

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match hidden in HiddenPattern.Matches(html))
        {
            var tag = hidden.Value;
            var nameMatch = NamePattern.Match(tag);
            if (!nameMatch.Success) continue;
            var valueMatch = ValuePattern.Match(tag);
            var name = nameMatch.Groups["name"].Value;
            // whichchoice/option/leaves are supplied by the caller.
            if (name is "whichchoice" or "option" or "leaves") continue;
            fields[name] = valueMatch.Success ? valueMatch.Groups["value"].Value : string.Empty;
        }

        return new BurningLeavesPage
        {
            Leaves = leaves,
            BurnedToday = burned,
            FormAction = action,
            Fields = fields
        };
    }

    private static int ParseNumber(string value)
        => int.TryParse(value.Replace(",", string.Empty), out var parsed) ? parsed : 0;
}
