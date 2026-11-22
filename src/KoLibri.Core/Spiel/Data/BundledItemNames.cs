namespace KoLibri.Core.Spiel;

/// <summary>Item id to name lookup from the bundled KoLmafia data (null when the data is not found).</summary>
public static class BundledItemNames
{
    private static readonly Lazy<Func<string, string?>?> Shared = new(Build);

    public static Func<string, string?>? Resolver => Shared.Value;

    private static Func<string, string?>? Build()
    {
        try
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "kolmafia-data"),
                Path.Combine(AppContext.BaseDirectory, "external", "kolmafia-data"),
                Path.Combine(Directory.GetCurrentDirectory(), "external", "kolmafia-data")
            };
            var path = candidates.FirstOrDefault(Directory.Exists);
            if (path is null) return null;
            var catalog = new MafiaDataLoader().Load(path);
            var dict = catalog.Items
                .Where(i => !string.IsNullOrWhiteSpace(i.Id) && !string.IsNullOrWhiteSpace(i.Name))
                .GroupBy(i => i.Id!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Name!, StringComparer.OrdinalIgnoreCase);
            return id => dict.TryGetValue(id, out var n) ? n : null;
        }
        catch
        {
            return null;
        }
    }
}
