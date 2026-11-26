using KoLibri;
using KoLibri.Daily;

internal static partial class KolibriCli
{
    internal static async Task<int> DailyCommandAsync(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintDailyHelp();
            return 0;
        }

        return args[0].ToLowerInvariant() switch
        {
            "run" => await DailyRunLiveAsync(args[1..]),
            "campfire" or "hermit" or "internet-shop" or "internetshop" or "meme-shop-read" or "meme-shop" or "bribes-sample"
                or "send-items" or "item-arbitrage" or "routine" or "daily-go" or "go" or "daily-bridge" or "bridge" or "breakfast"
                => NotYet($"daily {args[0]}"),
            _ => Fail($"Unknown daily command: {args[0]}")
        };
    }

    private static object LiveDocument(DailyRunLive live) => new
    {
        leaves = live.Leaves, clovers = live.Clovers, permits = live.Permits, worthlessItems = live.WorthlessItems,
        bacon = live.Bacon, baconMachines = live.BaconMachines, meat = live.Meat,
        dayShortenerDoneToday = live.DayShortenerDoneToday,
        baconMachineUsedToday = live.BaconMachineUsedToday,
        memeItemsDoneToday = live.MemeItemsDoneToday
    };

    /// <summary>daily run [--role R] [--hermit-clovers N] [--skip campfire|hermit|internet-shop] [--execute --approval-token T]</summary>
    private static async Task<int> DailyRunLiveAsync(string[] args)
    {
        var role = ParseRole(GetOption(args, "--role") ?? "main");
        var execute = HasFlag(args, "--execute");
        var token = GetOption(args, "--approval-token");
        if (execute && !await AgentGuard.ApprovalTokenValidAsync(token).ConfigureAwait(false))
            throw new ArgumentException("The supplied approval token is missing or does not match the stored approval token. Nothing was sent.");
        var options = new DailyRunOptions
        {
            HermitCloversWanted = Math.Max(0, ReadInt(args, "--hermit-clovers", 0)),
            Skip = GetOptions(args, "--skip").Select(s => s.Trim().ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase)
        };

        var (client, session) = await OpenAsync(role);
        await using var _ = client;
        await using var __ = session;
        var live = await DailyRun.ReadAsync(session);
        var steps = DailyRun.Plan(live, options);
        object Step(DailyStep s) => new { kind = s.Kind, id = s.Id, option = s.Option };
        var liveDoc = LiveDocument(live);
        if (!execute)
        {
            WriteJson(new
            {
                command = "daily run", readOnly = true, previewOnly = true, role = role.ToString(), live = liveDoc,
                steps = steps.Select(Step).ToArray(),
                note = "Preview: only status and inventory were read. Add --execute --approval-token to send these steps."
            });
            return 0;
        }

        var result = await DailyRun.ExecuteAsync(session, steps);
        var after = await DailyRun.ReadAsync(session);
        WriteJson(new
        {
            command = "daily run", readOnly = false, executed = result.Executed, role = role.ToString(),
            completeness = result.Completeness.ToString(),
            attempted = result.AttemptedSteps, submitted = result.SubmittedSteps, failed = result.FailedSteps,
            steps = steps.Select(Step).ToArray(),
            before = liveDoc,
            after = new
            {
                leaves = after.Leaves, clovers = after.Clovers, permits = after.Permits, worthlessItems = after.WorthlessItems,
                bacon = after.Bacon, meat = after.Meat,
                dayShortenerDoneToday = after.DayShortenerDoneToday,
                baconMachineUsedToday = after.BaconMachineUsedToday,
                memeItemsDoneToday = after.MemeItemsDoneToday
            },
            auditPath = (string?)null
        });
        return result.Completeness is DailyRunCompleteness.Failed ? 3
            : result.Completeness is DailyRunCompleteness.Partial ? 4 : 0;
    }

    private static void PrintDailyHelp()
    {
        Console.WriteLine("  daily run [--role main|mallbot] [--hermit-clovers N] [--skip campfire|hermit|internet-shop]");
        Console.WriteLine("            [--execute --approval-token TOKEN]   campfire leaf / Day Shortener, Hermit clovers, Internet Meme Shop;");
        Console.WriteLine("            preview reads status and inventory only; each step runs at most once per KoL day");
        Console.WriteLine("  daily campfire|hermit|internet-shop|routine|go|bridge|breakfast: not in 0.5-dev yet (follow-up PR).");
    }
}
