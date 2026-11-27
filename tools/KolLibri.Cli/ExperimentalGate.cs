/// <summary>
/// 0.5.0 Early Alpha: market-analysis and shop-management extras (arbitrage, bribes, CRM, watchlist, ...)
/// are hidden, not removed. They run only with <c>--experimental</c> or <c>KOLIBRI_EXPERIMENTAL=1</c>.
/// Without the switch the command prints a hint, sends nothing and exits with <see cref="BlockedExitCode"/>.
/// </summary>
internal static class ExperimentalGate
{
    public const string Flag = "--experimental";
    public const string EnvironmentVariable = "KOLIBRI_EXPERIMENTAL";
    public const int BlockedExitCode = 3;

    /// <summary>Top-level commands (and aliases) that need the experimental switch.</summary>
    public static readonly IReadOnlySet<string> Commands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Removed commands stay listed: the gate comes first (exit 3 hidden), the removal message (exit 2) only behind --experimental.
        "arbitrage", "bribe-rank", "bribe-plan", "snatch-plan", "crm", "crm-block", "categorize", "workflow-plan",
        "watchlist", "scheduler",
        // further pure market analysis / shop repricing
        "pricegun", "compare", "history", "market-scan", "market", "reprice", "batch", "apply-prices",
    };

    public static bool IsExperimental(string command) => Commands.Contains(command);

    public static bool Enabled(string[] args, Func<string, string?> env)
        => args.Any(a => string.Equals(a, Flag, StringComparison.OrdinalIgnoreCase))
           || env(EnvironmentVariable) is { } value && (value.Trim() == "1" || value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));

    /// <summary>Removes the global <c>--experimental</c> switch so subcommands never see it.</summary>
    public static string[] Strip(string[] args)
        => args.Where(a => !string.Equals(a, Flag, StringComparison.OrdinalIgnoreCase)).ToArray();

    /// <summary>Returns an exit code when <paramref name="command"/> is blocked; null when it may run.</summary>
    public static int? Check(string command, bool enabled, TextWriter error)
    {
        if (enabled || !IsExperimental(command)) return null;
        error.WriteLine($"'{command}' is experimental and hidden in KoLibri 0.5.0 (Early Alpha). Nothing was sent.");
        error.WriteLine($"To run it anyway, add {Flag} or set {EnvironmentVariable}=1.");
        return BlockedExitCode;
    }
}
