using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace KolArbitrage.Core.Adventure;

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
        var choices = ExtractChoices(html);
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
                extraFields);
        }

        var round = ExtractFightRound(html);
        if (path.EndsWith("fight.php", StringComparison.OrdinalIgnoreCase)
            || round is not null
            || Regex.IsMatch(html, @"\b(?:whichskill|fight\.php)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            if (round is null)
                advisories.Add("Combat response has no round marker; manual review required.");
            return Build(adventureId, AdventureEncounterKind.Fight, path, null, [], round is not null,
                advisories, round, responseHash, retryCount, extraFields);
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
            if (!string.IsNullOrWhiteSpace(value) && !choices.Contains(value, StringComparer.Ordinal))
                choices.Add(value);
        }

        foreach (Match match in OptionLinkPattern.Matches(html))
        {
            if (!choices.Contains(match.Groups[1].Value, StringComparer.Ordinal))
                choices.Add(match.Groups[1].Value);
        }
        return choices;
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
