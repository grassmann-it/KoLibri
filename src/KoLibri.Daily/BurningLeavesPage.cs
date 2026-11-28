// Moved from KoLibri.Core (0.5 step 3, docs/lib-api-0.5.md §4.8). The Core copy stays until MCP and Desktop move over.
using System.Text.RegularExpressions;

namespace KoLibri.Daily;

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
        @"<form\b[^>]*\saction\s*=\s*(?:[""'](?<action>[^""']+)[""']|(?<action2>[a-zA-Z0-9_./?=&-]+))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Forms = new(@"<form\b[^>]*>.*?</form\s*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex Inputs = new(@"<input\b[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HiddenPattern = new(
        @"<input\b[^>]*\stype\s*=\s*[""']?hidden[""']?(?=[\s/>])[^>]*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NamePattern = new(
        @"\sname\s*=\s*(?:""(?<name>[^""]+)""|'(?<name>[^']+)'|(?<name>[^\s/>]+))", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ValuePattern = new(
        @"\svalue\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<value>[^\s/>]+))", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

        var fields = HiddenFields(html);
        foreach (var name in new[] { "whichchoice", "option", "leaves" }) fields.Remove(name);

        return new BurningLeavesPage
        {
            Leaves = leaves,
            BurnedToday = burned,
            FormAction = FormActionOf(html),
            Fields = fields
        };
    }

    internal static Dictionary<string, string>? BurnForm(string html, string leaves)
    {
        foreach (Match match in Forms.Matches(html))
        {
            var form = match.Value;
            if (!string.Equals(FormActionOf(form)?.TrimStart('/'), "choice.php", StringComparison.OrdinalIgnoreCase)) continue;
            var fields = HiddenFields(form);
            if (fields.GetValueOrDefault("whichchoice") != "1510" || fields.GetValueOrDefault("option") != "1") continue;
            if (fields.TryGetValue("leaves", out var fixedAmount) && fixedAmount != leaves) continue;
            if (Inputs.Matches(form).Cast<Match>().Any(input => InputName(input).Equals("leaves", StringComparison.OrdinalIgnoreCase)))
                return fields;
        }
        return null;
    }

    private static string? FormActionOf(string html)
    {
        var form = FormPattern.Match(html);
        return form.Success ? (form.Groups["action"].Success ? form.Groups["action"].Value : form.Groups["action2"].Value) : null;
    }

    private static string InputName(Match input) => NamePattern.Match(input.Value).Groups["name"].Value;

    private static Dictionary<string, string> HiddenFields(string html)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match hidden in HiddenPattern.Matches(html))
        {
            var name = InputName(hidden);
            if (name.Length > 0) fields[name] = ValuePattern.Match(hidden.Value).Groups["value"].Value;
        }
        return fields;
    }

    private static int ParseNumber(string value)
        => int.TryParse(value.Replace(",", string.Empty), out var parsed) ? parsed : 0;
}
