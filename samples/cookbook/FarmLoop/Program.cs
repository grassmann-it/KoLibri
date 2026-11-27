using Cookbook.FarmLoop;
using KoLibri;

// Dünn: Argumente lesen, einloggen, Rezept ausführen. Die Logik steht in FarmLoopRecipe.cs.
var a = Args.Parse(args);
if (a.Has("--help"))
{
    Console.WriteLine("FarmLoop [--location <snarfblat>] [--turns <n>] [--heal-skill <id>] [--heal-below <pct>] [--yes]   (ohne --yes: Trockenlauf)");
    Console.WriteLine("Konto: --account <id> (Default main, gespeicherte Sitzung) oder --user <Name> (Login, Passwort aus KOLIBRI_PASSWORD oder Prompt).");
    return 0;
}

await using var client = KolibriClient.Create();
await using var session = await Args.OpenAsync(client, a);
var options = new FarmLoopOptions
{
    Snarfblat = a.GetInt("--location", 113),
    MaxTurns = a.GetInt("--turns", 5),
    HealSkillId = a.GetInt("--heal-skill", 0),
    HealBelowPercent = a.GetInt("--heal-below", 40),
    Yes = a.Has("--yes"),
};
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
return await Recipe.RunAsync(session, options, Console.Out, cts.Token);

internal sealed class Args
{
    private readonly Dictionary<string, string?> _values = new();
    public static Args Parse(string[] argv)
    {
        var r = new Args();
        for (var i = 0; i < argv.Length; i++)
        {
            if (!argv[i].StartsWith("--")) continue;
            var hasValue = i + 1 < argv.Length && !argv[i + 1].StartsWith("--");
            r._values[argv[i]] = hasValue ? argv[++i] : null;
        }
        return r;
    }
    public bool Has(string key) => _values.ContainsKey(key);
    public string? Get(string key) => _values.GetValueOrDefault(key);
    public int GetInt(string key, int fallback) => int.TryParse(Get(key), out var v) ? v : fallback;
    public long GetLong(string key, long fallback) => long.TryParse(Get(key), out var v) ? v : fallback;

    public static async Task<KolibriSession> OpenAsync(KolibriClient client, Args a)
    {
        var account = a.Get("--account") ?? "main";
        var user = a.Get("--user");
        if (user is null) return await client.OpenAsync(account);
        var password = Environment.GetEnvironmentVariable("KOLIBRI_PASSWORD");
        if (string.IsNullOrEmpty(password))
        {
            Console.Error.Write("Passwort: ");
            password = Console.ReadLine();
        }
        return await client.LoginAsync(user, password ?? "", new LoginOptions { AccountId = account });
    }
}
