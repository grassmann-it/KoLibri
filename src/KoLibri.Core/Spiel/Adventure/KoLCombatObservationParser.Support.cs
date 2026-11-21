using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace KoLibri.Core.Spiel;

public partial class KoLCombatObservationParser
{
    private static ResourceValue ParseResource(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return ResourceValue.Empty;
        var pair = PairPattern.Match(text);
        if (pair.Success)
            return new ResourceValue(ParseNumber(pair.Groups["current"].Value), ParseNumber(pair.Groups["maximum"].Value));
        var number = NumberPattern.Match(text);
        return number.Success ? new ResourceValue(ParseNumber(number.Value), null) : ResourceValue.Empty;
    }

    private static int? ExtractNamedNumber(string body, params string[] names)
    {
        foreach (var name in names)
        {
            var match = Regex.Match(body,
                $"[\\\"']?{Regex.Escape(name)}[\\\"']?\\s*:\\s*[\\\"']?(?<value>\\d[\\d,]*)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success && TryParseNumber(match.Groups["value"].Value, out var value)) return value;
        }
        return null;
    }

    private static int? ParseNumber(string? value)
        => TryParseNumber(value, out var result) ? result : null;

    private static bool TryParseNumber(string? value, out int result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        return int.TryParse(value.Replace(",", string.Empty, StringComparison.Ordinal),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }

    private static string? FindValue(IReadOnlyList<HtmlTag> tags, string name)
    {
        foreach (var tag in tags)
        {
            if (!tag.Attributes.TryGetValue("name", out var actual)
                || !actual.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (tag.Attributes.TryGetValue("value", out var value) && !string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    private static CombatActionKind ActionKindFrom(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return CombatActionKind.Unknown;
        var normalized = CleanText(value).Trim().ToLowerInvariant();
        if (normalized.Contains("attack", StringComparison.Ordinal) || normalized.Equals("hit", StringComparison.Ordinal)) return CombatActionKind.Attack;
        if (normalized.Contains("skill", StringComparison.Ordinal) || normalized.Contains("cast", StringComparison.Ordinal)) return CombatActionKind.Skill;
        if (normalized.Contains("useitem", StringComparison.Ordinal) || normalized.Contains("item", StringComparison.Ordinal) || normalized.Contains("use item", StringComparison.Ordinal)) return CombatActionKind.Item;
        if (normalized.Contains("runaway", StringComparison.Ordinal) || normalized.Equals("run", StringComparison.Ordinal) || normalized.Contains("flee", StringComparison.Ordinal) || normalized.Contains("escape", StringComparison.Ordinal)) return CombatActionKind.Run;
        return CombatActionKind.Unknown;
    }

    /// <summary>
    /// KoL writes form attributes without quotes: <c>&lt;form name=attack
    /// action=fight.php method=post&gt;</c>. The attribute reader has to
    /// cope with that, otherwise the action kind stays unknown and the
    /// fight page offers no attack.
    /// </summary>
    internal static string? ReadUnquotedAttribute(string tag, string attribute)
    {
        var pattern = new Regex(
            $@"(?:^|\s){Regex.Escape(attribute)}\s*=\s*(?:""(?<q>[^""]*)""|'(?<s>[^']*)'|(?<u>[^\s>]+))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var match = pattern.Match(tag);
        if (!match.Success) return null;
        if (match.Groups["q"].Success) return match.Groups["q"].Value;
        if (match.Groups["s"].Success) return match.Groups["s"].Value;
        return match.Groups["u"].Value;
    }

    private static string? ValidActionId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Equals("0", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("none", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("select", StringComparison.OrdinalIgnoreCase) ? null : trimmed;
    }

    private static bool HasAnyName(HtmlTag tag, params string[] values)
    {
        var name = tag.Attributes.TryGetValue("name", out var n) ? n : null;
        var id = tag.Attributes.TryGetValue("id", out var i) ? i : null;
        foreach (var expected in values)
            if (string.Equals(name, expected, StringComparison.OrdinalIgnoreCase)
                || string.Equals(id, expected, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool HasIdOrClass(HtmlTag tag, string expected)
    {
        var id = tag.Attributes.TryGetValue("id", out var idValue) ? idValue : null;
        var cls = tag.Attributes.TryGetValue("class", out var classValue) ? classValue : null;
        return string.Equals(id, expected, StringComparison.OrdinalIgnoreCase)
            || (cls?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Any(x => x.Equals(expected, StringComparison.OrdinalIgnoreCase)) ?? false);
    }

    private static Dictionary<string, string> GetAttributes(string? raw)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw)) return result;
        var matches = Regex.Matches(raw,
            @"(?<name>[A-Za-z_:][A-Za-z0-9_:\-]*)\s*(?:=\s*(?:""(?<double>[^""]*)""|'(?<single>[^']*)'|(?<bare>[^\s>]+)))?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (Match match in matches)
        {
            var name = match.Groups["name"].Value;
            var value = match.Groups["double"].Success ? match.Groups["double"].Value
                : match.Groups["single"].Success ? match.Groups["single"].Value
                : match.Groups["bare"].Success ? match.Groups["bare"].Value : string.Empty;
            result[name] = WebUtility.HtmlDecode(value);
        }
        return result;
    }

    private static string? GetAttribute(string raw, string name)
        => GetAttributes(raw).TryGetValue(name, out var value) ? value : null;

    private static bool HasAttribute(string raw, string name)
        => GetAttributes(raw).ContainsKey(name);

    private static string ElementText(string html, HtmlTag tag)
    {
        if (tag.End > html.Length) return string.Empty;
        var closing = Regex.Match(html[tag.End..], $"</{Regex.Escape(tag.Name)}\\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!closing.Success) return tag.Attributes.TryGetValue("value", out var value) ? value : string.Empty;
        return html.Substring(tag.End, closing.Index);
    }

    private static string CleanText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var noTags = Regex.Replace(value, "<[^>]*>", " ", RegexOptions.Singleline);
        noTags = WebUtility.HtmlDecode(noTags);
        return Regex.Replace(noTags, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
    }

    private static string? TrimMonsterPrefix(string value)
    {
        var clean = CleanText(value).Trim(' ', ':', '-', '\u2013', '\u2014');
        clean = Regex.Replace(clean, @"^(?:the\s+monster|monster|enemy|opponent)\s*[:\-]\s*", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return clean.Trim(' ', '.', '!', '?');
    }

    private static string CleanEffectName(string? value)
    {
        var clean = CleanText(value);
        clean = Regex.Replace(clean, @"^(?:effect|buff|debuff)\s*[:\-]\s*", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return Regex.Replace(clean, @"\s*\(?\s*\d+\s*(?:turns?|rounds?)\s*\)?\s*$", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
    }

    private static int? ExtractDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = Regex.Match(value, @"(?:for\s+)?(?<duration>\d+)\s*(?:turns?|rounds?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && TryParseNumber(match.Groups["duration"].Value, out var result) ? result : null;
    }

    private readonly record struct HtmlTag(string Name, string RawAttributes, int Start, int End, IReadOnlyDictionary<string, string> Attributes);
    private readonly record struct OptionValue(string Id, string Name);
    private readonly record struct ResourceValue(int? Current, int? Maximum, int? Mp = null, int? MaxMp = null)
    {
        public static ResourceValue Empty => new(null, null);
    }
    private enum ResourceKind { PlayerHp, PlayerMp, EnemyHp, EnemyMp }
}
