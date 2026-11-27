using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace KoLibri.Desktop;

public sealed record CharacterModifierResult(
    IReadOnlyDictionary<string, decimal> Values,
    IReadOnlyList<string> Unresolved);

public sealed record CharacterGearSuggestion(string ItemId, string Name, EquipSlot Slot, decimal Improvement);

/// <summary>
/// Projects additive catalog modifiers from observed character state.
/// Copied from KoLibri.Core (0.5 step 3) CharacterModifierCalculator, on the library's Status and Equipment.
/// </summary>
public static partial class CharacterModifierCalculator
{
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        "Muscle", "Mysticality", "Moxie", "Muscle Percent", "Mysticality Percent", "Moxie Percent",
        "Maximum HP", "Maximum MP", "Maximum HP Percent", "Maximum MP Percent",
        "Initiative", "Item Drop", "Meat Drop", "Familiar Weight", "Adventures",
        "Hot Resistance", "Cold Resistance", "Spooky Resistance", "Stench Resistance", "Sleaze Resistance"
    };

    /// <summary>KoLmafia's primary-use word for the items a slot takes.</summary>
    public static string ItemKind(EquipSlot slot) => slot switch
    {
        EquipSlot.Hat => "hat",
        EquipSlot.Weapon => "weapon",
        EquipSlot.OffHand => "offhand",
        EquipSlot.Back => "container",
        EquipSlot.Shirt => "shirt",
        EquipSlot.Pants => "pants",
        EquipSlot.Accessory1 or EquipSlot.Accessory2 or EquipSlot.Accessory3 => "accessory",
        EquipSlot.Familiar => "familiar",
        _ => throw new ArgumentOutOfRangeException(nameof(slot))
    };

    public static IReadOnlyList<CharacterGearSuggestion> CompareSlot(
        KoLmafiaCatalog catalog, CharacterSnapshot snapshot, EquipSlot slot, string modifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modifier);
        if (!Supported.Contains(modifier)) return Array.Empty<CharacterGearSuggestion>();
        var itemKind = ItemKind(slot);
        var entries = catalog.Modifiers.Where(row => row.Header == "Item")
            .ToLookup(row => WebUtility.HtmlDecode(row.Description), StringComparer.OrdinalIgnoreCase);
        var candidates = snapshot.Inventory.Items.Where(item => item.Quantity > 0)
            .Select(item => item.Item.Id.ToString(CultureInfo.InvariantCulture)).ToHashSet(StringComparer.Ordinal);
        var worn = DesktopCharacterActions.Worn(snapshot.Equipment, slot);
        var equippedId = worn?.ToString(CultureInfo.InvariantCulture);
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

    public static CharacterModifierResult Calculate(KoLmafiaCatalog catalog, Status status, Equipment? equipment,
        string? familiarSpecies = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(status);
        var values = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var unresolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var raw = catalog.Modifiers.ToLookup(record =>
            $"{record.Header}:{WebUtility.HtmlDecode(record.Description)}", StringComparer.OrdinalIgnoreCase);
        var items = catalog.Items.ToLookup(item => item.Id, StringComparer.Ordinal);

        foreach (var worn in equipment?.Slots.Values ?? Enumerable.Empty<ItemRef?>())
        {
            if (worn is not { Id: > 0 } item) continue;
            var id = item.Id.ToString(CultureInfo.InvariantCulture);
            var record = items[id].FirstOrDefault();
            if (record is null) unresolved.Add($"Unknown equipped item {id}");
            else Add("Item", record.Name);
        }

        foreach (var effect in status.Effects.Select(effect => effect.Name).Distinct(StringComparer.OrdinalIgnoreCase))
            Add("Effect", effect);

        var familiarId = status.FamiliarId is { } fam and > 0 ? fam.ToString(CultureInfo.InvariantCulture) : null;
        if (string.IsNullOrWhiteSpace(familiarSpecies) && familiarId is not null)
            familiarSpecies = catalog.FamiliarSpecies.GetValueOrDefault(familiarId);
        if (!string.IsNullOrWhiteSpace(familiarSpecies)) Add("Familiar", familiarSpecies);
        else if (familiarId is not null) unresolved.Add($"Familiar species for {familiarId}");

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

public sealed record CharacterRecipe(
    string Name, string Method, IReadOnlyList<string> IngredientNames, CombineCharacterItems? CombineAction);

/// <summary>Recipes for an item from the KoLmafia concoctions. Copied from KoLibri.Core (0.5 step 3) CharacterRecipeCatalog.</summary>
public static class CharacterRecipeCatalog
{
    public static IReadOnlyList<CharacterRecipe> Find(KoLmafiaCatalog catalog, string itemName)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemName);
        var target = Decode(itemName);
        var items = catalog.Items.GroupBy(item => Decode(item.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var recipes = new List<CharacterRecipe>();
        foreach (var row in catalog.Concoctions.Where(recipe =>
            string.Equals(Decode(recipe.Name), target, StringComparison.OrdinalIgnoreCase)))
        {
            var ingredients = new[] { row.Description }.Concat(row.Extras)
                .Select(Decode).Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
            CombineCharacterItems? action = null;
            if (row.MixMethod == "COMBINE" && ingredients.Length == 2
                && Resolve(target) is { } output && Resolve(ingredients[0]) is { } first
                && Resolve(ingredients[1]) is { } second)
                action = new CombineCharacterItems(output, first, second);
            recipes.Add(new CharacterRecipe(target, row.MixMethod, ingredients, action));
        }
        return recipes;

        int? Resolve(string name)
            => items.TryGetValue(name, out var matches) && matches.Length == 1
                && int.TryParse(matches[0].Id, out var id) && id > 0 ? id : null;
    }

    private static string Decode(string value)
        => WebUtility.HtmlDecode(value)?.Trim() ?? string.Empty;
}

public sealed record CoinmasterSource(string Currency, string ItemName, int CurrencyCost, bool RequiresTicket = false);

/// <summary>
/// Tradeable airport-shop items and their coinmaster prices, for the "find item sources" preview.
/// Copied from KoLibri.Core (0.5 step 3) ForeignBribeCatalog.Entries.
/// </summary>
public static class CoinmasterItemSources
{
    public static IReadOnlyList<CoinmasterSource> Entries { get; } =
    [
        new("Beach Buck", "History's Most Offensive Jokes, Vol. IX", 300),
        new("Beach Buck", "Now You're Cooking With Grease", 300),
        new("Beach Buck", "Sloppy Seconds Diner Employee Handbook", 300),
        new("Beach Buck", "industrial strength anti-fungal spray", 15),
        new("Beach Buck", "one-day ticket to Spring Break Beach", 100),
        new("Coinspiracy", "mercenary pistol", 50),
        new("Coinspiracy", "mercenary rifle", 50),
        new("Coinspiracy", "specialty ammo bandolier", 1),
        new("Coinspiracy", "Merc Core Field Manual: Sanity Maintenance", 300),
        new("Coinspiracy", "Merc Core Field Manual: Intimidation Techniques", 300),
        new("Coinspiracy", "Merc Core challenge coin", 1000),
        new("FunFunds", "Scratch-and-Sniff Guide to Dinseylandfill", 50),
        new("FunFunds", "Trash, a Memoir", 50),
        new("FunFunds", "Dinsey Maintenance Manual", 50),
        new("FunFunds", "one-day ticket to Dinseylandfill", 20),
        new("Volcoino", "Lava Miner's Daughter", 10),
        new("Volcoino", "Psycho From The Heat", 10),
        new("Volcoino", "The Firegate Tapes", 10),
        new("Volcoino", "one-day ticket to That 70s Volcano", 3),
        new("Wal-Mart gift certificate", "shoulder-warming lotion", 5),
        new("Wal-Mart gift certificate", "iceberg lettuce", 15),
        new("Wal-Mart gift certificate", "ice wine", 15),
        new("Wal-Mart gift certificate", "To Build an Igloo", 150),
        new("Wal-Mart gift certificate", "The Chill of the Wild", 150),
        new("Wal-Mart gift certificate", "Cold Fang", 150),
        new("Wal-Mart gift certificate", "one-day ticket to The Glaciest", 50)
    ];
}
