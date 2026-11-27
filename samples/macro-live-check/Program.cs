using System.Text.RegularExpressions;
using KoLibri;

// Macro live check (docs/macro-live-check.md): one fight, one macro, a few requests at most.
// Without --yes nothing is sent that changes the game: the app only reads status and the open fight and prints the plan.

var a = Args.Parse(args);
if (a.Has("--help") || args.Length == 0)
{
    Console.WriteLine("""
        macro-live-check (--case M1..M7 | --macro TEXT | --macro-file PATH) [options]
          --account ID        stored KoLibri account (default main; log in once with the CLI)
          --location N        start a fight there if none is open (one adventure; default: none, fight must be open)
          --skill NAME        skill name for case M4
          --requests N        send the macro at most N times while the fight goes on (default 1, max 30)
          --save DIR          save each fight.php answer as DIR/<case file>.html (pwd hash and player name removed)
          --force             also run when HP < 30 % or the monster is a turtle
          --yes               really send; without it: dry run (reads only)
        """);
    return 0;
}

var account = a.Get("--account") ?? "main";
var caseName = a.Get("--case")?.ToUpperInvariant();
var skill = a.Get("--skill");
var location = a.Get("--location") is { } loc ? int.Parse(loc) : (int?)null;
var maxRequests = Math.Clamp(a.GetInt("--requests", 1), 1, 30);
var saveDir = a.Get("--save");
var yes = a.Has("--yes");
var force = a.Has("--force");

string? template = caseName switch
{
    "M1" => "attack;",
    "M2" or "M7" => "attack; repeat;",
    "M3" => "if monstername {monster}; abort; endif; attack;",
    "M4" => skill is null ? throw new ArgumentException("--case M4 needs --skill NAME (a cheap combat skill, MP <= 5).") : $"skill {skill}; attack; repeat;",
    "M5" => "if hpbelow 999999; runaway; endif;",
    "M6" => "frobnicate;",
    null => null,
    _ => throw new ArgumentException($"Unknown case {caseName}; use M1..M7.")
};
template ??= a.Get("--macro") ?? (a.Get("--macro-file") is { } file ? File.ReadAllText(file) : null)
    ?? throw new ArgumentException("Give --case, --macro or --macro-file.");
var saveName = caseName switch
{
    "M1" => "macro-m1-single", "M2" => "macro-m2-repeat", "M3" => "macro-m3-abort", "M4" => "macro-m4-skill",
    "M5" => "macro-m5-runaway", "M6" => "macro-m6-invalid", "M7" => "macro-m7-followup", _ => "macro-custom"
};

await using var client = KolibriClient.Create(new KolibriOptions { EnableExperimentalMacros = true });
await using var session = await client.OpenAsync(account);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;

var before = await session.Character.GetStatusAsync(refresh: true, ct);
Print("status before", before);

var open = await session.Adventure.GetOpenEncounterAsync(ct);
if (open is not null and not FightEncounter)
{
    Console.WriteLine($"An open {open.GetType().Name} ('{open.Title}') is in the way. Finish it in the browser first. Nothing was sent.");
    return 2;
}
var fight = open as FightEncounter;
Console.WriteLine(fight is null ? "open fight: none" : $"open fight: {fight.MonsterName} (id {fight.MonsterId}, round {fight.Round})");

if (!force && before.MaxHp > 0 && before.Hp * 100 < before.MaxHp * 30)
{
    Console.WriteLine("HP below 30 %: stop (checklist abort rule). Heal first or pass --force. Nothing was sent.");
    return 2;
}

if (fight is null)
{
    if (location is null)
    {
        Console.WriteLine("No fight is open and no --location given. Start a fight by hand (or pass --location 113). Nothing was sent.");
        return 2;
    }
    if (!yes)
    {
        Console.WriteLine($"Dry run: would adventure once at {location} and then send the macro. Add --yes to do it.");
        Console.WriteLine($"macro (template): {template.Trim()}");
        return 0;
    }
    var encounter = await session.Adventure.AdventureAsync(location.Value, ct);
    if (encounter is not FightEncounter started)
    {
        Console.WriteLine($"The adventure gave a {encounter.GetType().Name} ('{encounter.Title}'), not a fight. Handle it in the browser and run again.");
        return 3;
    }
    fight = started;
    Console.WriteLine($"fight started: {fight.MonsterName} (id {fight.MonsterId})");
}

var turtle = Regex.IsMatch(fight.MonsterName, "turtle|tortoise|terrapin", RegexOptions.IgnoreCase);
if (turtle && !force)
{
    Console.WriteLine($"{fight.MonsterName} is a turtle: hand it to the Turtle Tamer in the browser (or pass --force). Nothing was sent.");
    return 2;
}

// KoL's fight page names the monster with its article ("an undead elbow macaroni"); the macro predicate `monstername`
// most likely compares against the bare name: "if monstername an undead elbow macaroni" did not match live (M3, 2026-11-27).
var monsterForMacro = Regex.Replace(fight.MonsterName.Trim(), @"^(?:a|an|the)\s+", "", RegexOptions.IgnoreCase);
var macroText = template.Replace("{monster}", monsterForMacro, StringComparison.Ordinal);
var action = CombatAction.FromMacro(macroText); // normalizes like KoLmafia; throws on empty / too long
Console.WriteLine($"macrotext to send: {action.Macro}");
if (!yes)
{
    Console.WriteLine($"Dry run: would POST fight.php action=macro (at most {maxRequests} time(s)). Add --yes to send.");
    return 0;
}

var pwd = await session.GetPasswordHashAsync(ct);
for (var i = 1; i <= maxRequests; i++)
{
    var round = await session.Adventure.FightAsync(action, ct);
    Console.WriteLine($"answer {i}: outcome={round.Outcome} roundsInResponse={round.RoundsInResponse} round={round.Round} "
        + $"hp={round.Hp} mp={round.Mp} damageDealt={round.DamageDealt} meat={round.Meat} items=[{string.Join(", ", round.Items.Select(x => $"{x.Name} x{x.Quantity}"))}]");
    if (round.MacroError is { } error) Console.WriteLine($"macro error: {error}");
    if (saveDir is not null)
    {
        Directory.CreateDirectory(saveDir);
        var path = Path.Combine(saveDir, maxRequests == 1 ? $"{saveName}.html" : $"{saveName}-{i}.html");
        var html = round.Page.Html.Replace(pwd, "PWDHASH", StringComparison.OrdinalIgnoreCase);
        if (before.PlayerName.Length > 0) html = Regex.Replace(html, Regex.Escape(before.PlayerName), "PLAYER", RegexOptions.IgnoreCase);
        await File.WriteAllTextAsync(path, html, ct);
        Console.WriteLine($"saved: {path}");
    }
    if (round.Outcome != FightOutcome.Ongoing || round.MacroError is not null) break;
}

var after = await session.Character.GetStatusAsync(refresh: true, ct);
Print("status after", after);
var still = await session.Adventure.GetOpenEncounterAsync(ct);
Console.WriteLine(still switch
{
    null => "open encounter after: none",
    FightEncounter f => $"open encounter after: fight vs {f.MonsterName} (round {f.Round})",
    var e => $"open encounter after: {e.GetType().Name} '{e.Title}'"
});
return 0;

static void Print(string label, Status s)
    => Console.WriteLine($"{label}: HP {s.Hp}/{s.MaxHp}, MP {s.Mp}/{s.MaxMp}, adventures {s.Adventures}, meat {s.Meat}");

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
}
