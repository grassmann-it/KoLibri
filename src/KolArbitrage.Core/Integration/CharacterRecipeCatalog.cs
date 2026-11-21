using System.Net;
using KolArbitrage.Core.Data;

namespace KolArbitrage.Core.Integration;

public sealed record CharacterRecipe(
    string Name, string Method, IReadOnlyList<string> IngredientNames, CombineCharacterItems? CombineAction);

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
