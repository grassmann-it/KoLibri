using System.Globalization;
using KoLibri;

namespace KoLibri.Cli
{
    /// <summary>The two accounts of the CLI. In 0.5 they are plain account ids ("main", "mallbot") of the KoLibri library.</summary>
    internal enum AccountRole { Main, Mallbot }
}

internal static partial class KolibriCli
{
    /// <summary>For tests: replaces the options the CLI builds its <see cref="KolibriClient"/> from.</summary>
    internal static Func<KolibriOptions>? OptionsOverride { get; set; }

    internal static string AccountIdOf(AccountRole role) => role == AccountRole.Main ? "main" : "mallbot";

    internal static string? ConfiguredStateDirectory => OptionsOverride?.Invoke().StateDirectory;

    private static string AgentConfigPath() => ConfiguredStateDirectory is { } state
        ? Path.Combine(state, "agent-mode.json") : AgentGuard.ConfigPath;

    /// <summary>One client per command; the limiter comes from the same environment variables as in 0.4.</summary>
    internal static KolibriClient CreateClient(Func<string, string?>? env = null)
        => KolibriClient.Create(OptionsOverride?.Invoke() ?? new KolibriOptions
        {
            Limiter = LimiterFromEnvironment(env ?? Environment.GetEnvironmentVariable),
            DataDirectory = BundledDataDirectory()
        });

    /// <summary>Opens the stored session of a role; the caller disposes both.</summary>
    internal static async Task<(KolibriClient Client, KolibriSession Session)> OpenAsync(AccountRole role, CancellationToken cancellationToken = default)
    {
        var client = CreateClient();
        try
        {
            var session = await client.OpenAsync(AccountIdOf(role), cancellationToken).ConfigureAwait(false);
            return (client, session);
        }
        catch (KolibriException exception) when (exception is not SessionExpiredException && exception.Message.StartsWith("No stored account", StringComparison.Ordinal))
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"{role} is not logged in. Run 'login --role {role.ToString().ToLowerInvariant()}'.");
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Like 0.4's LibKolibri OpenAsync before an action: one fresh status read first. A session KoL no longer accepts
    /// is renewed there with the saved password (reads may log in again; actions never do).
    /// </summary>
    internal static async Task<(KolibriClient Client, KolibriSession Session)> OpenForActionAsync(AccountRole role, CancellationToken cancellationToken = default)
    {
        var (client, session) = await OpenAsync(role, cancellationToken).ConfigureAwait(false);
        try
        {
            await session.Character.GetStatusAsync(refresh: true, cancellationToken).ConfigureAwait(false);
            return (client, session);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static string? BundledDataDirectory()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "external", "kolmafia-data");
        return Directory.Exists(path) ? path : null;
    }

    /// <summary>
    /// The 0.4 environment knobs, translated into <see cref="LimiterOptions"/> (the library no longer reads them):
    /// KOLIBRI_LIMITER_PRESET (conservative|browser-like), KOLIBRI_MIN_REQUEST_INTERVAL_MS, KOLIBRI_READ_INTERVAL_MS,
    /// KOLIBRI_READ_CONCURRENCY. The library clamps them (actions at least 1 s apart).
    /// </summary>
    internal static LimiterOptions LimiterFromEnvironment(Func<string, string?> env)
    {
        var browserLike = string.Equals(env("KOLIBRI_LIMITER_PRESET")?.Trim(), "browser-like", StringComparison.OrdinalIgnoreCase);
        var action = TimeSpan.FromSeconds(2);
        var read = browserLike ? TimeSpan.FromMilliseconds(400) : TimeSpan.FromSeconds(1);
        var concurrency = browserLike ? 3 : 2;
        if (Number(env("KOLIBRI_MIN_REQUEST_INTERVAL_MS")) is { } a) action = TimeSpan.FromMilliseconds(Math.Clamp(a, 1000, 60_000));
        if (Number(env("KOLIBRI_READ_INTERVAL_MS")) is { } r) read = TimeSpan.FromMilliseconds(Math.Clamp(r, 200, 60_000));
        if (Number(env("KOLIBRI_READ_CONCURRENCY")) is { } c) concurrency = (int)Math.Clamp(c, 1, 4);
        return new LimiterOptions { ActionInterval = action, ReadInterval = read, ReadConcurrency = concurrency };
    }

    private static double? Number(string? text)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
}
