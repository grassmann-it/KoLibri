using KoLibri;

internal static partial class KolibriCli
{
    /// <summary>consume --role R --kind food|drink|spleen --item ID [--qty N] [--execute]</summary>
    internal static async Task<int> ConsumeAsync(string[] args)
    {
        StrictOptions.Only(args, new[] { "--role", "--kind", "--item", "--qty" }, new[] { "--execute" });
        var role = StrictOptions.Role(args);
        var (kind, kindName) = (StrictOptions.Value(args, "--kind") ?? "").ToLowerInvariant() switch
        {
            "food" or "eat" => (ConsumeKind.Eat, "Food"),
            "drink" or "booze" => (ConsumeKind.Drink, "Drink"),
            "spleen" => (ConsumeKind.Chew, "Spleen"),
            _ => throw new ArgumentException("--kind food|drink|spleen is required.")
        };
        var item = StrictOptions.RequiredInt(args, "--item", 1, int.MaxValue, "ITEM_ID");
        var qty = StrictOptions.Int(args, "--qty", 1, 1, 1_000);
        if (!HasFlag(args, "--execute"))
        {
            WriteJson(new { command = "consume", previewOnly = true, executed = false, role = role.ToString(), kind = kindName, item, quantity = qty,
                note = "Preview only. Add --execute to eat, drink or take it." });
            return 0;
        }
        var (client, session) = await OpenForActionAsync(role);
        await using var _ = client;
        await using var __ = session;
        var result = await session.Character.ConsumeAsync(item, kind, qty);
        var status = result.Before ?? await session.Character.GetStatusAsync(refresh: true);
        var after = result.After ?? await session.Character.GetStatusAsync(refresh: true);
        var consumed = result.Completed;
        WriteJson(new { command = "consume", executed = true, role = role.ToString(), kind = kindName, item, requested = qty, consumed, rejection = result.Rejection,
            complete = consumed == qty, unclear = result.Status == ActionStatus.Unclear ? true : (bool?)null,
            before = new { status.Adventures, Fullness = status.Fullness.Current, Inebriety = status.Inebriety.Current, Spleen = status.Spleen.Current },
            after = new { after.Adventures, Fullness = after.Fullness.Current, Inebriety = after.Inebriety.Current, Spleen = after.Spleen.Current } });
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
        var (client, session) = await OpenForActionAsync(role);
        await using var _ = client;
        await using var __ = session;
        var result = await session.Character.RestAsync(times);
        var done = result.Completed;
        var problem = result.Status == ActionStatus.Done ? null : result.Rejection;
        var spent = result.Before is { } b && result.After is { } a ? Math.Max(0, b.Adventures - a.Adventures) : 0;
        WriteJson(new { command = "rest", executed = true, role = role.ToString(), requested = times, done,
            adventuresSpent = spent, problem, complete = done == times,
            afterEach = done > 0 && result.After is { } last ? new object[] { new { last.Hp, last.Mp } } : Array.Empty<object>() });
        return done == times && problem is null ? 0 : 3;
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
        var (client, session) = await OpenForActionAsync(role);
        await using var _ = client;
        await using var __ = session;
        // One cast per request inside the library, so the confirmed count is real.
        var result = await session.Character.CastAsync(skill, times);
        var before = result.Before;
        var after = result.After;
        var confirmed = result.Completed;
        var complete = confirmed == times;
        WriteJson(new { command = "cast", executed = true, role = role.ToString(), skill, times, requested = times, confirmed, complete,
            rejection = complete ? null : result.Rejection, page = (string?)null,
            before = new { CurrentHp = before?.Hp, CurrentMp = before?.Mp }, after = new { Hp = after?.Hp, Mp = after?.Mp } });
        return complete ? 0 : 3;
    }
}
