using System.Text.RegularExpressions;

namespace KoLibri.Core.Spiel;

/// <summary>Catalog location ids such as "adventure=543:Your Mushroom Garden" carry the numeric snarfblat.</summary>
public static partial class AdventureLocationIds
{
    [GeneratedRegex(@"^\s*(?:adventure=)?(\d+)(?::.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex NumericId();

    /// <summary>The numeric snarfblat of a catalog id (<c>543</c>, <c>543:Name</c>, <c>adventure=543:Name</c>); false for names.</summary>
    public static bool TryGetSnarfblat(string? locationId, out int snarfblat)
    {
        snarfblat = 0;
        if (string.IsNullOrWhiteSpace(locationId)) return false;
        var match = NumericId().Match(locationId);
        return match.Success && int.TryParse(match.Groups[1].Value, out snarfblat) && snarfblat > 0;
    }
}
