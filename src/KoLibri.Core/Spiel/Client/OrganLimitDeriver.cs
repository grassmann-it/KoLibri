using System.Net;
using System.Text.RegularExpressions;

namespace KoLibri.Core.Spiel;

/// <summary>How far an organ limit can be trusted.</summary>
public enum OrganLimitConfidence
{
    /// <summary>No limit is known; the number must be shown as "?" and overfill checks are "unverified".</summary>
    Unknown = 0,
    /// <summary>Computed from KoLmafia's formula (base capacity + skill/item/familiar modifiers) over observed inputs.</summary>
    Derived = 1,
    /// <summary>KoL itself reported the limits in the status payload.</summary>
    Reported = 2
}

/// <summary>Three organ limits with one honest flag. A limit is null exactly when <see cref="Confidence"/> is Unknown.</summary>
public sealed record OrganLimits(int? Fullness, int? Inebriety, int? Spleen, OrganLimitConfidence Confidence, string Note)
{
    public static OrganLimits Unknown(string note) => new(null, null, null, OrganLimitConfidence.Unknown, note);
}

/// <summary>
/// Port of KoLmafia's KoLCharacter.getStomachCapacity / getLiverCapacity / getSpleenLimit
/// (base capacity of the class or path + the Stomach/Liver/Spleen Capacity modifiers). The modifier table below is the
/// capacity subset of external/kolmafia modifiers.txt; <c>OrganLimitDeriverTests</c> cross-checks it against that file
/// when the bundled data is present.
/// Anything this port cannot represent faithfully (avatar classes, paths with their own capacities, unknown skill list,
/// ambiguous skills, equipment with code-driven capacity) yields Unknown instead of a guess.
/// </summary>
public static class OrganLimitDeriver
{
    public const int DefaultStomach = 15, DefaultLiver = 14, DefaultSpleen = 15;

    // Skill name -> (stomach, liver, spleen).
    internal static readonly IReadOnlyDictionary<string, (int Stomach, int Liver, int Spleen)> SkillModifiers =
        new Dictionary<string, (int, int, int)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Drinking to Drink"] = (0, 1, 0),
            ["Hollow Leg"] = (0, 1, 0),
            ["Liver of Steel"] = (0, 5, 0),
            ["Nightcap"] = (0, 5, 0),
            ["Replacement Liver"] = (0, 4, 0),
            ["Another Extra Spleen"] = (0, 0, 5),
            ["Extra Spleen"] = (0, 0, 5),
            ["Just One More Extra Spleen"] = (0, 0, 5),
            ["Okay Seriously, This is the Last Spleen"] = (0, 0, 5),
            ["Spleen of Steel"] = (0, 0, 5),
            ["Still Another Extra Spleen"] = (0, 0, 5),
            ["Tolerant Constitution"] = (0, 0, 5),
            ["Yet Another Extra Spleen"] = (0, 0, 5),
            ["Gluttony"] = (2, 0, 0),
            ["Insatiable Hunger"] = (5, 0, 0),
            ["Legendary Appetite"] = (5, 0, 0),
            ["Lunch Like a King"] = (5, 0, 0),
            ["Prodigious Appetite"] = (5, 0, 0),
            ["Ravenous Pounce"] = (5, 0, 0),
            ["Replacement Stomach"] = (5, 0, 0),
            ["Stomach of Steel"] = (5, 0, 0),
            ["Pride"] = (-1, 0, 0),
        };

    /// <summary>Two different skills share this name with different bonuses (+5 and +10): never guess.</summary>
    private static readonly HashSet<string> AmbiguousSkills = new(StringComparer.OrdinalIgnoreCase) { "Hard Drinker" };

    // Equipped items, by KoL item id (api.php equipment values) -> (stomach, liver, spleen).
    internal static readonly IReadOnlyDictionary<string, (string Name, int Stomach, int Liver, int Spleen)> ItemModifiers =
        new Dictionary<string, (string, int, int, int)>
        {
            ["12069"] = ("angelbone totem", 0, 0, 1),
            ["12070"] = ("angelbone chopsticks", 1, 0, 0),
            ["12072"] = ("angelbone dice", 0, 1, 0),
            ["12077"] = ("devilbone greaves", 0, 0, 1),
            ["12079"] = ("devilbone corset", 1, 0, 0),
            ["12081"] = ("devilbone rosary", 0, 1, 0),
        };

    private const string StooperFamiliarId = "261";

    // Paths whose capacities differ from 15/14/15 or are rewritten in code (AscensionPath.Path).
    private static readonly HashSet<int> NonDefaultPaths =
        [1, 2, 3, 28, 30, 35, 38, 40, 41, 44, 49, 50, 53, 56];

    private static readonly HashSet<string> CodeDrivenItems = new(StringComparer.OrdinalIgnoreCase)
    {
        "Pantsgiving", "6860" // Pantsgiving raises capacity in KoL but not through modifiers.txt
    };

    /// <summary>The limits for one status snapshot. KoL-reported limits win; otherwise derive; otherwise Unknown.</summary>
    public static OrganLimits Derive(KoLStatusSnapshot status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.FullnessLimit is > 0 || status.InebrietyLimit is > 0 || status.SpleenLimit is > 0)
            return new(Pos(status.FullnessLimit), Pos(status.InebrietyLimit), Pos(status.SpleenLimit),
                OrganLimitConfidence.Reported, "Reported by KoL.");

        if (status.Skills is not { } skills)
            return OrganLimits.Unknown("The skill list was not read, so capacity skills (Liver of Steel and similar) are unknown.");
        if (!int.TryParse(status.ClassId, out var classId))
            return OrganLimits.Unknown("The character class is not known.");
        if (classId is < 1 or > 6)
            return OrganLimits.Unknown("This class has its own organ capacities that are not ported.");
        if (status.PathId is not { } pathId)
            return OrganLimits.Unknown("The ascension path is not known.");
        if (NonDefaultPaths.Contains(pathId))
            return OrganLimits.Unknown($"Path {status.PathName ?? pathId.ToString()} changes organ capacities in ways that are not ported.");

        int stomach = DefaultStomach, liver = DefaultLiver, spleen = DefaultSpleen;
        foreach (var raw in skills)
        {
            var name = NormalizeSkill(raw);
            if (AmbiguousSkills.Contains(name))
                return OrganLimits.Unknown($"Skill \"{name}\" exists in two versions with different capacity; the limit is ambiguous.");
            if (!SkillModifiers.TryGetValue(name, out var m)) continue;
            stomach += m.Stomach; liver += m.Liver; spleen += m.Spleen;
        }

        foreach (var value in status.Equipment.Values)
        {
            if (CodeDrivenItems.Contains(value.Trim()))
                return OrganLimits.Unknown($"Equipped item {value} changes capacity in ways that are not ported.");
            if (ItemModifiers.TryGetValue(value.Trim(), out var m)) { stomach += m.Stomach; liver += m.Liver; spleen += m.Spleen; }
            else
            {
                var byName = ItemModifiers.Values.FirstOrDefault(i => i.Name.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
                if (byName.Name is not null) { stomach += byName.Stomach; liver += byName.Liver; spleen += byName.Spleen; }
            }
        }

        if (string.Equals(status.FamiliarId, StooperFamiliarId, StringComparison.Ordinal)) liver += 1;

        return new(stomach, liver, spleen, OrganLimitConfidence.Derived,
            "Derived from KoLmafia's capacity formula (base 15/14/15 + skills, equipment, familiar); KoL did not report the limits.");
    }

    /// <summary>The snapshot with its limit fields and confidence filled in.</summary>
    public static KoLStatusSnapshot Apply(KoLStatusSnapshot status)
    {
        var l = Derive(status);
        return status with
        {
            FullnessLimit = l.Fullness, InebrietyLimit = l.Inebriety, SpleenLimit = l.Spleen,
            OrganLimitConfidence = l.Confidence, OrganLimitNote = l.Note
        };
    }

    /// <summary>Skill names found on a charsheet.php page: link text equal to a known capacity skill (a "(P)" suffix is ignored).
    /// Returns null when the page has no skills section at all, so a login or error page is never read as "no skills".</summary>
    public static IReadOnlyList<string>? ParseCharsheetSkills(string html)
    {
        if (string.IsNullOrWhiteSpace(html) || html.IndexOf("Skills", StringComparison.OrdinalIgnoreCase) < 0) return null;
        var found = new List<string>();
        foreach (Match m in Regex.Matches(html, @"<a\b[^>]*>(?<t>[^<]{2,80})</a>", RegexOptions.IgnoreCase))
        {
            var name = NormalizeSkill(WebUtility.HtmlDecode(m.Groups["t"].Value));
            if ((SkillModifiers.ContainsKey(name) || AmbiguousSkills.Contains(name)) && !found.Contains(name, StringComparer.OrdinalIgnoreCase))
                found.Add(name);
        }
        return found;
    }

    private static string NormalizeSkill(string raw)
        => Regex.Replace(raw.Trim(), @"\s*\((?:P|HP|SP)\)\s*$", "", RegexOptions.IgnoreCase).Trim();

    private static int? Pos(int? v) => v is > 0 ? v : null;
}
