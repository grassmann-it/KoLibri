using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace KoLibri.Core.Spiel;

/// <summary>
/// Pure parser for the small part of a KoL response needed to checkpoint an
/// adventure. It never follows redirects or submits a fight/choice request.
/// Unrecognised pages remain <see cref="AdventureEncounterKind.Unknown"/> so
/// an eventual runner can stop for manual review.
/// </summary>
public interface IAdventureEncounterParser
{
    AdventureEncounterState Parse(
        string adventureId,
        Uri? responseUri,
        string html,
        int retryCount = 0);
}

public sealed class KoLAdventureEncounterParser : IAdventureEncounterParser
{
    private static readonly KoLCombatObservationParser CombatParser = new();
    private static readonly Regex TagPattern = new("<input\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex OptionLinkPattern = new("[?&]option=(\\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RoundPattern = new("\\bonturn\\s*=\\s*['\"]?(\\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public AdventureEncounterState Parse(
        string adventureId,
        Uri? responseUri,
        string html,
        int retryCount = 0)
    {
        if (string.IsNullOrWhiteSpace(adventureId))
            throw new ArgumentException("Adventure id is required.", nameof(adventureId));

        html ??= string.Empty;
        var path = responseUri?.AbsolutePath ?? string.Empty;
        var responseHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(html))).ToLowerInvariant();
        var advisories = new List<string>();
        var extraFields = ExtractExtraFields(html);

        if (LooksLikeExpiredSession(path, html))
        {
            advisories.Add("Session appears expired; refresh credentials before continuing.");
            return Build(adventureId, AdventureEncounterKind.Unknown, path, null, [], false,
                advisories, null, responseHash, retryCount, extraFields);
        }

        var choiceId = ExtractInputValue(html, "whichchoice");
        var choices = ExtractChoices(html).ToList();
        var labels = ExtractChoiceLabels(html, choiceId, choices);
        if (path.EndsWith("choice.php", StringComparison.OrdinalIgnoreCase)
            && choiceId is null && choices.Count == 0
            && Regex.IsMatch(html, @"(?:you acquire|you gain|adventure complete|adventure is over)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return Build(adventureId, AdventureEncounterKind.Reward, path, null, [], false,
                advisories, null, responseHash, retryCount, extraFields);
        }
        if (path.EndsWith("choice.php", StringComparison.OrdinalIgnoreCase)
            || choiceId is not null
            || choices.Count > 0)
        {
            if (choiceId is null)
                advisories.Add("Choice page has no whichchoice id; manual review required.");
            if (choices.Count == 0)
                advisories.Add("Choice page has no recognised options; manual review required.");
            return Build(adventureId, AdventureEncounterKind.Choice, path, choiceId, choices,
                choiceId is not null && choices.Count > 0, advisories, null, responseHash, retryCount,
                extraFields) with { ChoiceLabels = labels, ChoiceTitle = ExtractChoiceTitle(html) };
        }

        var round = ExtractFightRound(html);
        if (path.EndsWith("fight.php", StringComparison.OrdinalIgnoreCase)
            || round is not null
            || Regex.IsMatch(html, @"\b(?:whichskill|fight\.php)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var combat = CombatParser.Parse(html, responseUri);
            if (combat.Outcome is CombatOutcome.Victory or CombatOutcome.Defeat or CombatOutcome.Fled)
                return Build(adventureId, AdventureEncounterKind.Reward, path, null, [], false,
                    advisories, null, responseHash, retryCount) with { Combat = combat };
            if (round is null)
                advisories.Add("Combat response has no round marker; manual review required.");
            return Build(adventureId, AdventureEncounterKind.Fight, path, null, [], round is not null,
                advisories, round, responseHash, retryCount, extraFields) with { Combat = combat };
        }

        if (Regex.IsMatch(html, @"(?:you acquire|you gain|adventure complete|adventure is over)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return Build(adventureId, AdventureEncounterKind.Reward, path, null, [], false,
                advisories, null, responseHash, retryCount, extraFields);
        }

        advisories.Add("Response did not match a known fight, choice, or reward; manual review required.");
        return Build(adventureId, AdventureEncounterKind.Unknown, path, null, [], false,
            advisories, null, responseHash, retryCount, extraFields);
    }

    private static AdventureEncounterState Build(
        string adventureId,
        AdventureEncounterKind kind,
        string path,
        string? choiceId,
        IReadOnlyList<string> choices,
        bool canContinue,
        IReadOnlyList<string> advisories,
        int? fightRound,
        string responseHash,
        int retryCount,
        IReadOnlyDictionary<string, string>? extraFields = null)
    {
        var state = new AdventureEncounterState(
            adventureId,
            kind,
            string.IsNullOrWhiteSpace(path) ? null : path,
            choiceId,
            choices,
            canContinue,
            advisories,
            fightRound,
            responseHash,
            Math.Max(0, retryCount));
        return state with
        {
            ExtraFields = extraFields
                ?? new Dictionary<string, string>(StringComparer.Ordinal)
        };
    }

    private static bool LooksLikeExpiredSession(string path, string html)
        => path.Contains("login", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(html, "(?:name=[\"']?loginname|class=[\"'][^\"']*login|please log in|session has expired)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static int? ExtractFightRound(string html)
    {
        var match = RoundPattern.Match(html);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var round))
            return Math.Max(0, round);

        var inputValue = ExtractInputValue(html, "onturn");
        return int.TryParse(inputValue, out var inputRound)
            ? Math.Max(0, inputRound)
            : null;
    }

    private static string? ExtractInputValue(string html, string expectedName)
    {
        foreach (Match tag in TagPattern.Matches(html))
        {
            if (!string.Equals(GetAttribute(tag.Value, "name"), expectedName, StringComparison.OrdinalIgnoreCase))
                continue;
            var value = GetAttribute(tag.Value, "value");
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    private static IReadOnlyList<string> ExtractChoices(string html)
    {
        var choices = new List<string>();
        foreach (Match tag in TagPattern.Matches(html))
        {
            if (!string.Equals(GetAttribute(tag.Value, "name"), "option", StringComparison.OrdinalIgnoreCase))
                continue;
            var value = GetAttribute(tag.Value, "value");
            if (!IsDisabled(tag.Value) && int.TryParse(value, out var option) && option > 0
                && !choices.Contains(value!, StringComparer.Ordinal))
                choices.Add(value!);
        }

        foreach (Match match in OptionLinkPattern.Matches(html))
        {
            if (!choices.Contains(match.Groups[1].Value, StringComparer.Ordinal))
                choices.Add(match.Groups[1].Value);
        }
        return choices;
    }

    private static IReadOnlyDictionary<string, string> ExtractChoiceLabels(
        string html, string? choiceId, List<string> choices)
    {
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline;
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (choiceId is null) return labels;

        foreach (Match form in Regex.Matches(html, @"<form\b[^>]*>(.*?)</form\s*>", options))
        {
            var body = form.Groups[1].Value;
            if (ExtractInputValue(body, "whichchoice") != choiceId) continue;
            var formOption = ExtractInputValue(body, "option");

            foreach (Match control in Regex.Matches(body, @"<input\b[^>]*>|<button\b[^>]*>(.*?)</button\s*>", options))
            {
                var tag = control.Value[..(control.Value.IndexOf('>') + 1)];
                var type = GetAttribute(tag, "type");
                if (IsDisabled(tag)) continue;

                var button = tag.StartsWith("<button", StringComparison.OrdinalIgnoreCase);
                if (!string.Equals(type, "submit", StringComparison.OrdinalIgnoreCase)
                    && !(button && type is null)) continue;

                var namedOption = string.Equals(GetAttribute(tag, "name"), "option", StringComparison.OrdinalIgnoreCase);
                var value = GetAttribute(tag, "value");
                var option = namedOption ? value : formOption;
                if (!int.TryParse(option, out var optionNumber) || optionNumber <= 0) continue;
                if (!choices.Contains(option!, StringComparer.Ordinal)) choices.Add(option!);

                var rawLabel = button ? Regex.Replace(control.Groups[1].Value, "<[^>]*>", "") : value;
                var label = WebUtility.HtmlDecode(rawLabel)?.Trim();
                if (!string.IsNullOrWhiteSpace(label) && label != option)
                    labels.TryAdd(option!, label);
            }
        }

        return labels;
    }

    /// <summary>Heading of a choice page: the first bold text in the blue title bar, else the first bold text.</summary>
    public static string? ExtractChoiceTitle(string html)
    {
        if (string.IsNullOrEmpty(html)) return null;
        const RegexOptions o = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline;
        var m = Regex.Match(html, @"<td[^>]*bgcolor=['""]?blue['""]?[^>]*>.*?<b>(.*?)</b>", o);
        if (!m.Success) m = Regex.Match(html, @"<b>(.*?)</b>", o);
        if (!m.Success) return null;
        var text = WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, "<[^>]*>", "")).Trim();
        return text.Length == 0 ? null : text;
    }

    private static IReadOnlyDictionary<string, string> ExtractExtraFields(string html)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match tag in TagPattern.Matches(html))
        {
            var name = GetAttribute(tag.Value, "name");
            var value = GetAttribute(tag.Value, "value");
            if (string.IsNullOrWhiteSpace(name) || value is null)
                continue;
            // These are selected explicitly by the kernel/transport and must
            // never be replaced by a hidden field copied from a page.
            if (name.Equals("whichchoice", StringComparison.OrdinalIgnoreCase)
                || name.Equals("option", StringComparison.OrdinalIgnoreCase)
                || name.Equals("action", StringComparison.OrdinalIgnoreCase))
                continue;
            if (name.Equals("pwd", StringComparison.OrdinalIgnoreCase)
                || name.Equals("whichskill", StringComparison.OrdinalIgnoreCase)
                || name.Equals("whichitem", StringComparison.OrdinalIgnoreCase)
                || name.Equals("whichplace", StringComparison.OrdinalIgnoreCase)
                || name.Equals("whichfam", StringComparison.OrdinalIgnoreCase))
                fields[name] = value;
        }
        return fields;
    }

    private static bool IsDisabled(string tag)
        => GetAttribute(tag, "disabled") is not null
            || Regex.IsMatch(tag, @"\sdisabled(?:\s|/?>)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string? GetAttribute(string tag, string name)
    {
        var match = Regex.Match(tag,
            $"\\b{Regex.Escape(name)}\\s*=\\s*(?:\"(?<double>[^\"]*)\"|'(?<single>[^']*)'|(?<bare>[^\\s>]+))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success
            ? match.Groups["double"].Success
                ? match.Groups["double"].Value
                : match.Groups["single"].Success
                    ? match.Groups["single"].Value
                    : match.Groups["bare"].Value
            : null;
    }
}
