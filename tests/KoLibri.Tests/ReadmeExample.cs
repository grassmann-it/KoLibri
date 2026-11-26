using Xunit;

namespace KoLibri.Tests;

/// <summary>The minimal example from src/KoLibri/README.md. It must compile; it is not run (it would talk to KoL).</summary>
public static class ReadmeExample
{
    // README-BEGIN
    public static async Task RunAsync(string password)
    {
        await using var kol = KolibriClient.Create();

        // First run: log in once. "main" is a free local id; later runs use OpenAsync("main").
        await using var session = await kol.LoginAsync("MyCharacter", password,
            new LoginOptions { AccountId = "main", RememberPassword = true });

        var status = await session.Character.GetStatusAsync(refresh: true);
        Console.WriteLine($"{status.PlayerName}: {status.Meat} Meat, {status.Adventures} adventures, " +
                          $"fullness {status.Fullness.Current}/{status.Fullness.Limit?.ToString() ?? "?"}");

        // Up to 5 turns in the Haunted Pantry (snarfblat 113): attack in fights, stop at choices
        // without an answer, stop early when HP drops below 30 %.
        var run = await session.Adventure.RunAsync(new RunRequest(113, 5)
        {
            Combat = CombatStrategies.AttackOnly,
            Choices = ChoiceResolvers.Safe(),
            StopWhen = new[] { StopCondition.HpBelowPercent(30) }
        });
        Console.WriteLine($"{run.StopReason} after {run.TurnsSpent} turns, {run.MeatGained} Meat");
        if (run.Open is ChoiceEncounter choice)
            Console.WriteLine($"Choice {choice.ChoiceId} is open: {string.Join(", ", choice.Options.Select(o => o.Option + "=" + o.Text))}");

        // Mall: three seal teeth (item 2), never above 500 Meat each, keep 100 000 Meat.
        var bought = await session.Mall.QuoteAndBuyAsync(new PurchaseOrder(new ItemRef(2), 3, 500) { MeatReserve = 100_000 });
        Console.WriteLine($"{bought.Status}: {bought.QuantityBought} bought for {bought.MeatSpent} Meat");
    }
    // README-END
}

public sealed class ReadmeExampleTests
{
    [Fact]
    public void Readme_shows_the_compiled_example()
    {
        static string Normalize(string s) => string.Join('\n', s.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "KoLibri.slnx"))) root = Path.GetDirectoryName(root)!;
        var source = File.ReadAllText(Path.Combine(root, "tests", "KoLibri.Tests", "ReadmeExample.cs"));
        var start = source.IndexOf("// README-BEGIN", StringComparison.Ordinal) + "// README-BEGIN".Length;
        var snippet = source[start..source.IndexOf("// README-END", StringComparison.Ordinal)];
        var readme = File.ReadAllText(Path.Combine(root, "src", "KoLibri", "README.md"));
        Assert.Contains(Normalize(snippet), Normalize(readme));
    }
}
