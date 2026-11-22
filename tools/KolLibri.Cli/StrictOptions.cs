using System.Globalization;
using KoLibri.Core.Basis;

/// <summary>
/// Strict option parsing for commands that change game state. Defaults apply only when an option is
/// left out; a flag without a value, an unparsable number or a value out of range is an error, raised
/// before any session is opened or request is sent. Callers turn the <see cref="ArgumentException"/> into exit code 2.
/// </summary>
internal static class StrictOptions
{
    /// <summary>Value of <paramref name="name"/>, or null when the option is absent. Throws when it has no value.</summary>
    public static string? Value(string[] args, string name)
    {
        string? found = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) continue;
            if (found is not null) throw new ArgumentException($"{name} was given more than once.");
            if (i + 1 >= args.Length || IsOptionLike(args[i + 1]) || string.IsNullOrWhiteSpace(args[i + 1]))
                throw new ArgumentException($"{name} requires a value.");
            found = args[++i];
        }
        return found;
    }

    private static bool IsOptionLike(string text) => text.StartsWith("--", StringComparison.Ordinal);

    public static int Int(string[] args, string name, int fallback, int min, int max)
        => Value(args, name) is { } text ? Parse(name, text, min, max) : fallback;

    public static int RequiredInt(string[] args, string name, int min, int max, string? hint = null)
        => Value(args, name) is { } text
            ? Parse(name, text, min, max)
            : throw new ArgumentException($"{name} is required{(hint is null ? "" : " (" + hint + ")")}.");

    public static long Long(string[] args, string name, long fallback, long min, long max)
    {
        if (Value(args, name) is not { } text) return fallback;
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new ArgumentException($"{name} must be a whole number, got '{text}'.");
        if (value < min || value > max)
            throw new ArgumentException($"{name} must be between {min} and {max}, got {value}.");
        return value;
    }

    private static int Parse(string name, string text, int min, int max)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new ArgumentException($"{name} must be a whole number, got '{text}'.");
        if (value < min || value > max)
            throw new ArgumentException($"{name} must be between {min} and {max}, got {value}.");
        return value;
    }

    /// <summary>Role from --role; main only when the option is left out.</summary>
    public static AccountRole Role(string[] args)
        => Value(args, "--role")?.Trim().ToLowerInvariant() switch
        {
            null => AccountRole.Main,
            "main" => AccountRole.Main,
            "mallbot" => AccountRole.Mallbot,
            var other => throw new ArgumentException($"--role must be main or mallbot, got '{other}'.")
        };

    /// <summary>Rejects anything that is not one of the known value options or flags (typos like --qtty).</summary>
    public static void Only(string[] args, IReadOnlyCollection<string> valueOptions, IReadOnlyCollection<string> flags)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (valueOptions.Contains(arg, StringComparer.OrdinalIgnoreCase)) { i++; continue; }
            if (flags.Contains(arg, StringComparer.OrdinalIgnoreCase)) continue;
            throw new ArgumentException($"Unknown argument '{arg}'.");
        }
    }
}
