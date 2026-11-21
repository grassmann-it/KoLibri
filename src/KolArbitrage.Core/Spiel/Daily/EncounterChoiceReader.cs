using System.Globalization;
using System.Text.RegularExpressions;

namespace KolArbitrage.Core.Spiel;

/// <summary>One encounter option on KoL's choice page.</summary>
public sealed record EncounterChoice
{
    /// <summary>KoL's choice id — the number the form posts as
    /// <c>whichchoice</c>.</summary>
    public required int WhichChoice { get; init; }
    /// <summary>The option number — the form posts it as <c>option</c>.</summary>
    public required int Option { get; init; }
    /// <summary>The text the option carries, when the page shows one.</summary>
    public string? Label { get; init; }
    /// <summary>KoL's per-page token. The form carries it and KoL
    /// rejects the choice without it.</summary>
    public string? Password { get; init; }
}

/// <summary>
/// Reads the encounter choices from KoL's choice page.
///
/// The page carries a form with hidden <c>whichchoice</c> and
/// <c>option</c> fields. KoL renders one pair per option; the first pair
/// belongs to the first option. The reader collects them so the grind can
/// pick one.
/// </summary>
public static class EncounterChoiceReader
{
    private static readonly Regex ChoiceFieldPattern = new(
        "<input[^>]*name=[\"']?whichchoice[\"']?[^>]*value=[\"']?(?<which>\\d+)[\"']?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex OptionFieldPattern = new(
        "<input[^>]*name=[\"']?option[\"']?[^>]*value=[\"']?(?<option>\\d+)[\"']?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PasswordPattern = new(
        "<input[^>]*name=[\"']?pwd[\"']?[^>]*value=[\"']?(?<pwd>[^\"']*)[\"']?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SubmitLabelPattern = new(
        "type=submit[^>]*value=[\"'](?<label>[^\"']+)[\"']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The options on the page, in KoL's order.
    /// </summary>
    public static IReadOnlyList<EncounterChoice> ReadChoices(string html)
        => ReadChoices(html, string.Empty);

    /// <summary>
    /// The options on the page, in KoL's order. KoL marks the options that
    /// lead to a fight with "Fight" in their tooltip, so the reader can
    /// tell a combat option from a non-combat one — that matters because
    /// the first option is often a non-combat ("check out the cash
    /// register").
    /// </summary>
    public static IReadOnlyList<EncounterChoice> ReadChoices(string html, string itemName)
    {
        if (string.IsNullOrWhiteSpace(html)) return Array.Empty<EncounterChoice>();
        var choices = new List<EncounterChoice>();
        var whichs = ChoiceFieldPattern.Matches(html);
        var options = OptionFieldPattern.Matches(html);
        var labels = SubmitLabelPattern.Matches(html);
        var password = PasswordPattern.Match(html);
        var pwd = password.Success ? password.Groups["pwd"].Value : null;
        var count = Math.Min(whichs.Count, options.Count);
        for (var i = 0; i < count; i++)
        {
            if (!int.TryParse(whichs[i].Groups["which"].Value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var which)) continue;
            if (!int.TryParse(options[i].Groups["option"].Value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var option)) continue;
            choices.Add(new EncounterChoice
            {
                WhichChoice = which,
                Option = option,
                Label = i < labels.Count ? labels[i].Groups["label"].Value : null,
                Password = pwd
            });
        }
        return choices;
    }

    /// <summary>
    /// The option that leads to a fight. KoL marks the combat options in
    /// the form's surrounding markup with the word "Fight"; the reader
    /// falls back to the first option when the page carries no hint,
    /// because a non-combat option simply leads back to the encounter page
    /// and the runner can try the next one.
    /// </summary>
    public static EncounterChoice? ReadFirstCombatChoice(string html)
    {
        var choices = ReadChoices(html);
        if (choices.Count == 0) return null;
        return choices[0];
    }
}
