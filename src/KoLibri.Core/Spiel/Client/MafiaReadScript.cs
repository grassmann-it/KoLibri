namespace KoLibri.Core.Spiel;

/// <summary>Runs the supported read commands from a KoLmafia CLI script.</summary>
public static class MafiaReadScript
{
    public static async Task ExecuteAsync(
        TextReader script,
        TextWriter output,
        Func<CancellationToken, Task<KoLStatusSnapshot>> status,
        Func<bool, CancellationToken, Task<KoLInventorySnapshot>> inventory,
        CancellationToken cancellationToken = default)
    {
        var lineNumber = 0;
        while (await script.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } raw)
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var separator = line.IndexOfAny([' ', '\t']);
            var command = (separator < 0 ? line : line[..separator]).ToLowerInvariant();
            var argument = separator < 0 ? "" : line[(separator + 1)..].Trim();
            switch (command)
            {
                case "echo":
                case "print":
                    await output.WriteLineAsync(argument).ConfigureAwait(false);
                    break;
                case "status":
                case "effects":
                    if (command == "status" && argument.Length > 0)
                        throw new NotSupportedException($"Line {lineNumber}: status output files are not supported.");
                    var character = await status(cancellationToken).ConfigureAwait(false);
                    if (command == "status")
                    {
                        await output.WriteLineAsync($"{character.PlayerName} · level {character.Level}").ConfigureAwait(false);
                        await output.WriteLineAsync($"Adventures: {character.Adventures} · Meat: {character.Meat}").ConfigureAwait(false);
                    }
                    else
                    {
                        foreach (var effect in character.TimedEffects.Where(effect => Matches(effect.Name, argument)))
                            await output.WriteLineAsync($"{effect.Name} ({effect.TurnsRemaining})").ConfigureAwait(false);
                    }
                    break;
                case "inv":
                case "inventory":
                case "storage":
                    var items = await inventory(command == "storage", cancellationToken).ConfigureAwait(false);
                    foreach (var item in items.Items.Where(item => Matches(item.Name, argument)).OrderBy(item => item.Name,
                                 StringComparer.OrdinalIgnoreCase))
                        await output.WriteLineAsync($"{item.Name} ({item.Quantity})").ConfigureAwait(false);
                    break;
                default:
                    throw new NotSupportedException($"Line {lineNumber}: KoLmafia command '{command}' is not supported.");
            }
        }
    }

    private static bool Matches(string name, string filter)
        => name.Contains(filter, StringComparison.OrdinalIgnoreCase);
}
