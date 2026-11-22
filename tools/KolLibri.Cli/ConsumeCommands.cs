using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;
using LibKolibri;

internal static partial class KolibriCli
{
    /// <summary>consume --role R --kind food|drink|spleen --item ID [--qty N] [--execute]</summary>
    internal static async Task<int> ConsumeAsync(string[] args)
    {
        StrictOptions.Only(args, new[] { "--role", "--kind", "--item", "--qty" }, new[] { "--execute" });
        var role = StrictOptions.Role(args);
        var kind = (StrictOptions.Value(args, "--kind") ?? "").ToLowerInvariant() switch
        {
            "food" or "eat" => CharacterConsumableKind.Food,
            "drink" or "booze" => CharacterConsumableKind.Drink,
            "spleen" => CharacterConsumableKind.Spleen,
            _ => throw new ArgumentException("--kind food|drink|spleen is required.")
        };
        var item = StrictOptions.RequiredInt(args, "--item", 1, int.MaxValue, "ITEM_ID");
        var qty = StrictOptions.Int(args, "--qty", 1, 1, 1_000);
        if (!HasFlag(args, "--execute"))
        {
            WriteJson(new { command = "consume", previewOnly = true, executed = false, role = role.ToString(), kind = kind.ToString(), item, quantity = qty,
                note = "Preview only. Add --execute to eat, drink or take it." });
            return 0;
        }
        using var kol = await KolibriClient.OpenAsync(role);
        var status = await kol.GetStatusAsync(true);
        var (consumed, rejection) = await kol.ConsumeAsync(item, kind, qty);
        var after = await kol.GetStatusAsync(true);
        WriteJson(new { command = "consume", executed = true, role = role.ToString(), kind = kind.ToString(), item, requested = qty, consumed, rejection, complete = consumed == qty,
            before = new { status.Adventures, status.Fullness, status.Inebriety, status.Spleen },
            after = new { after.Adventures, after.Fullness, after.Inebriety, after.Spleen } });
        return consumed == qty ? 0 : 3;
    }

    /// <summary>rest --role R [--times N] [--execute]</summary>
    internal static async Task<int> RestAsync(string[] args)
    {
        StrictOptions.Only(args, new[] { "--role", "--times" }, new[] { "--execute" });
        var role = StrictOptions.Role(args);
        var times = StrictOptions.Int(args, "--times", 1, 1, 100);
        if (!HasFlag(args, "--execute"))
        {
            WriteJson(new { command = "rest", previewOnly = true, executed = false, role = role.ToString(), times,
                note = "Preview only. Add --execute to rest at the campground." });
            return 0;
        }
        using var kol = await KolibriClient.OpenAsync(role);
        var outcome = await kol.RestAsync(times);
        var done = outcome.Rests.Count;
        WriteJson(new { command = "rest", executed = true, role = role.ToString(), requested = times, done,
            adventuresSpent = outcome.AdventuresSpent, problem = outcome.Problem, complete = done == times,
            afterEach = outcome.Rests.Select(r => new { r.Hp, r.Mp }).ToArray() });
        return done == times && outcome.Problem is null ? 0 : 3;
    }

    /// <summary>cast --role R --skill ID [--times N] [--execute]  (Soul Food = 7185)</summary>
    internal static async Task<int> CastAsync(string[] args)
    {
        StrictOptions.Only(args, new[] { "--role", "--skill", "--times" }, new[] { "--execute" });
        var role = StrictOptions.Role(args);
        var skill = StrictOptions.RequiredInt(args, "--skill", 1, int.MaxValue, "SKILL_ID; Soul Food = 7185");
        var times = StrictOptions.Int(args, "--times", 1, 1, 100);
        if (!HasFlag(args, "--execute"))
        {
            WriteJson(new { command = "cast", previewOnly = true, executed = false, role = role.ToString(), skill, times,
                note = "Preview only. Add --execute to cast." });
            return 0;
        }
        using var kol = await KolibriClient.OpenAsync(role);
        var before = await kol.GetStatusAsync(true);
        // One cast per request, so the confirmed count is real: a single request for N casts could only
        // show that something changed, not how many casts took effect.
        var confirmed = 0;
        var hp = before.CurrentHp;
        var mp = before.CurrentMp;
        string? rejection = null;
        string? page = null;
        for (var i = 0; i < times; i++)
        {
            (hp, mp, rejection, page) = await kol.CastSkillAsync(skill, 1);
            if (rejection is not null) break;
            confirmed++;
        }
        var complete = confirmed == times;
        WriteJson(new { command = "cast", executed = true, role = role.ToString(), skill, times, requested = times, confirmed, complete, rejection, page,
            before = new { before.CurrentHp, before.CurrentMp }, after = new { Hp = hp, Mp = mp } });
        return complete ? 0 : 3;
    }
}
