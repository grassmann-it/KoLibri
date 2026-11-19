using System.Globalization;
using KolArbitrage.Core.Daily;

internal static partial class KolibriCli
{
    private static async Task<int> DailyCommandAsync(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintDailyHelp();
            return 0;
        }

        return args[0].ToLowerInvariant() switch
        {
            "campfire" => DailyCampfireAsync(args[1..]),
            "hermit" => DailyHermitAsync(args[1..]),
            _ => Fail($"Unbekannter daily-Befehl: {args[0]}")
        };
    }

    private static int DailyCampfireAsync(string[] args)
    {
        var snapshot = new CampfireLeavesSnapshot
        {
            CampfireLeaves = ReadInt(args, "--leaves", 0),
            MinimumReserve = ReadInt(args, "--reserve", 3),
            DayShortnerPrice = long.TryParse(GetOption(args, "--shortner-price"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var price) ? Math.Max(0, price) : long.MaxValue,
            AvailableMeat = long.TryParse(GetOption(args, "--meat"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var meat) ? Math.Max(0, meat) : 0,
            IsUsefulDay = !HasFlag(args, "--not-useful"),
            Knucklebones = ReadInt(args, "--knucklebones", 0)
        };
        var planner = new CampfireLeavesPlanner();
        var plan = planner.Plan(snapshot);
        WriteJson(new
        {
            command = "daily campfire",
            readOnly = true,
            previewOnly = true,
            plan.ShouldBurn,
            plan.Verdict,
            plan.ShouldBuyDayShortner,
            plan.Rationale,
            plan.Steps,
            plan.Snapshot,
            note = "Preview only; no choice.php POST is sent. Re-run with --execute --approval-token via the bounded-runner bridge."
        });
        return 0;
    }

    private static int DailyHermitAsync(string[] args)
    {
        var permits = Math.Max(0, ReadInt(args, "--permits", 3));
        var clovers = Math.Max(0, ReadInt(args, "--clovers", 0));
        var usefulAdventureValue = Math.Max(0, ReadInt(args, "--adventure-value", 0));
        var cloverFloorValue = Math.Max(0, ReadInt(args, "--clover-floor", 0));
        var reserve = Math.Max(0, ReadInt(args, "--reserve", 0));

        string verdict;
        if (permits == 0) verdict = "NoHermitPermits";
        else if (clovers == 0) verdict = "NoClovers";
        else if (usefulAdventureValue <= reserve) verdict = "KeepCloverReserve";
        else if (cloverFloorValue <= 0 || usefulAdventureValue > cloverFloorValue) verdict = "UseClover";
        else verdict = "KeepCloverReserve";

        var rationale = $"permits={permits}, clovers={clovers}, adventureValue={usefulAdventureValue}, cloverFloor={cloverFloorValue}, reserve={reserve}.";
        WriteJson(new
        {
            command = "daily hermit",
            readOnly = true,
            previewOnly = true,
            verdict,
            rationale,
            permits,
            clovers,
            note = "Hermit purchases stay preview-only. Future bridge will route through the bounded choice chain."
        });
        return 0;
    }

    private static void PrintDailyHelp()
    {
        Console.WriteLine("  daily campfire [--leaves N] [--reserve N] [--knucklebones N]");
        Console.WriteLine("                 [--shortner-price N] [--meat N] [--not-useful]");
        Console.WriteLine("  daily hermit [--permits N] [--clovers N] [--adventure-value N]");
        Console.WriteLine("              [--clover-floor N] [--reserve N]");
    }
}
