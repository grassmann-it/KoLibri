using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace KolArbitrage.Core.Spiel;

public sealed record CharacterModifierResult(
    IReadOnlyDictionary<string, decimal> Values,
    IReadOnlyList<string> Unresolved);

public sealed record CharacterGearSuggestion(
    string ItemId, string Name, CharacterEquipmentSlot Slot, decimal Improvement);

/// <summary>Projects additive catalog modifiers from observed character state.</summary>
public static partial class CharacterModifierCalculator
{
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        "Muscle", "Mysticality", "Moxie", "Muscle Percent", "Mysticality Percent", "Moxie Percent",
        "Maximum HP", "Maximum MP", "Maximum HP Percent", "Maximum MP Percent",
        "Initiative", "Item Drop", "Meat Drop", "Familiar Weight", "Adventures",
        "Hot Resistance", "Cold Resistance", "Spooky Resistance", "Stench Resistance", "Sleaze Resistance"
    };

    public static IReadOnlyList<CharacterGearSuggestion> CompareSlot(
        KoLmafiaCatalog catalog, CharacterActionSnapshot snapshot, CharacterEquipmentSlot slot, string modifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modifier);
        if (snapshot.Status.IsStale || snapshot.Inventory.IsStale || !Supported.Contains(modifier))
            return Array.Empty<CharacterGearSuggestion>();
        var itemKind = slot switch
        {
            CharacterEquipmentSlot.Accessory1 or CharacterEquipmentSlot.Accessory2 or CharacterEquipmentSlot.Accessory3
                => "accessory",
            _ => CharacterActionClient.SlotName(slot)
        };
        var entries = catalog.Modifiers.Where(row => row.Header == "Item")
            .ToLookup(row => WebUtility.HtmlDecode(row.Description), StringComparer.OrdinalIgnoreCase);
        var candidates = snapshot.Inventory.Items.Where(item => item.Quantity > 0)
            .Select(item => item.ItemId).ToHashSet(StringComparer.Ordinal);
        var equippedId = snapshot.Status.Equipment.GetValueOrDefault(CharacterActionClient.SlotName(slot))
            ?? (slot == CharacterEquipmentSlot.Container ? snapshot.Status.Equipment.GetValueOrDefault("back") : null)
            ?? (slot == CharacterEquipmentSlot.Offhand ? snapshot.Status.Equipment.GetValueOrDefault("off-hand") : null);
        var equipped = catalog.Items.FirstOrDefault(item => item.Id == equippedId);
        if (equipped is null && equippedId is not (null or "0")) return Array.Empty<CharacterGearSuggestion>();
        var current = equipped is null ? 0m : Contribution(equipped);
        if (current is null) return Array.Empty<CharacterGearSuggestion>();

        return catalog.Items.Where(item => candidates.Contains(item.Id) && item.PrimaryUse.Contains(itemKind))
            .Select(item => (Item: item, Value: Contribution(item)))
            .Where(row => row.Value > current)
            .Select(row => new CharacterGearSuggestion(row.Item.Id, row.Item.Name, slot, row.Value!.Value - current.Value))
            .OrderByDescending(row => row.Improvement).ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ToArray();

        decimal? Contribution(MafiaItemRecord item)
        {
            var total = 0m;
            foreach (var record in entries[WebUtility.HtmlDecode(item.Name)])
            foreach (var column in record.Extras)
            foreach (Match term in ModifierTerms().Matches(column))
            {
                if (!string.Equals(term.Groups["key"].Value.Trim(), modifier, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!decimal.TryParse(term.Groups["value"].Value.Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out var parsed))
                    return null;
                total += parsed;
            }
            return total;
        }
    }

    public static CharacterModifierResult Calculate(KoLmafiaCatalog catalog, KoLStatusSnapshot status,
        string? familiarSpecies = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(status);
        var values = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var unresolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var raw = catalog.Modifiers.ToLookup(record =>
            $"{record.Header}:{WebUtility.HtmlDecode(record.Description)}", StringComparer.OrdinalIgnoreCase);
        var items = catalog.Items.ToLookup(item => item.Id, StringComparer.Ordinal);

        foreach (var id in status.Equipment.Values)
        {
            if (id == "0" || string.IsNullOrWhiteSpace(id)) continue;
            var item = items[id].FirstOrDefault();
            if (item is null) unresolved.Add($"Unknown equipped item {id}");
            else Add("Item", item.Name);
        }

        foreach (var effect in status.Effects.Concat(status.TimedEffects.Select(effect => effect.Name))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            Add("Effect", effect);

        if (string.IsNullOrWhiteSpace(familiarSpecies) && status.FamiliarId is { } familiarId)
            familiarSpecies = catalog.FamiliarSpecies.GetValueOrDefault(familiarId);
        if (!string.IsNullOrWhiteSpace(familiarSpecies)) Add("Familiar", familiarSpecies);
        else if (status.FamiliarId is { Length: > 0 } id && id != "0") unresolved.Add($"Familiar species for {id}");

        return new CharacterModifierResult(values, unresolved.Order(StringComparer.OrdinalIgnoreCase).ToArray());

        void Add(string source, string name)
        {
            foreach (var row in raw[$"{source}:{WebUtility.HtmlDecode(name)}"])
            foreach (var column in row.Extras)
            foreach (Match match in ModifierTerms().Matches(column))
            {
                var key = match.Groups["key"].Value.Trim();
                if (!Supported.Contains(key)) continue;
                var text = match.Groups["value"].Value.Trim();
                if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out var number))
                {
                    unresolved.Add($"{name}: {key}");
                    continue;
                }

                values[key] = values.GetValueOrDefault(key) + number;
            }
        }
    }

    // Expressions and quoted strings can contain commas. Consume them as a
    // unit so their internal text cannot turn into an additional modifier.
    [GeneratedRegex("""(?:^|[,]\s*)(?<key>[^,:]+?)(?:\s*:\s*(?<value>\[[^\]]*\]|"[^"]*"|[^,]*))?(?=,|$)""",
        RegexOptions.CultureInvariant)]
    private static partial Regex ModifierTerms();
}
