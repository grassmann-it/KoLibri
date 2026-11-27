using KoLibri;

namespace Cookbook.Status;

public sealed class StatusOptions
{
    /// <summary>Zusätzlich die zehn häufigsten Inventar-Items zeigen.</summary>
    public bool ShowInventory { get; init; }
}

public static class Recipe
{
    public static async Task<int> RunAsync(KolibriSession session, StatusOptions options, TextWriter output, CancellationToken ct = default)
    {
        var s = await session.Character.GetStatusAsync(refresh: true, ct);
        output.WriteLine($"{s.PlayerName}  Level {s.Level}  {s.Meat:N0} Meat  {s.Adventures} Adventures");
        output.WriteLine($"HP {s.Hp}/{s.MaxHp}  MP {s.Mp}/{s.MaxMp}");
        output.WriteLine($"Organe: Fullness {Organ(s.Fullness)}  Inebriety {Organ(s.Inebriety)}  Spleen {Organ(s.Spleen)}");
        if (s.PathName is { Length: > 0 } path) output.WriteLine($"Pfad: {path}");
        output.WriteLine(s.Effects.Count == 0
            ? "Effekte: keine"
            : "Effekte: " + string.Join(", ", s.Effects.Select(e => $"{e.Name} ({e.Turns})")));

        if (options.ShowInventory)
        {
            var inventory = await session.Inventory.GetAsync(refresh: true, ct);
            output.WriteLine("Inventar (Top 10):");
            foreach (var item in inventory.Items.OrderByDescending(i => i.Quantity).Take(10))
            {
                output.WriteLine($"  {item.Quantity,6} x {item.Name}");
            }
        }
        return 0;
    }

    private static string Organ(OrganState o) => $"{o.Current}/{(o.Limit?.ToString() ?? "?")}";
}
