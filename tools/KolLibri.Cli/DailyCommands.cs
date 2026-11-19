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
            "internet-shop" or "internetshop" => DailyInternetShopAsync(args[1..]),
            "bacon" or "bacons" => DailyBaconAsync(args[1..]),
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
        var wishes = Math.Max(0, ReadInt(args, "--wishes", 0));
        var reserveAdventureValue = long.TryParse(GetOption(args, "--adventure-value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var adv) ? Math.Max(0, adv) : 0;
        var cloverFloor = long.TryParse(GetOption(args, "--clover-floor"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var floor) ? Math.Max(0, floor) : 0;
        var expectedClover = long.TryParse(GetOption(args, "--clover-sale"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sale) ? Math.Max(0, sale) : 0;
        var wishBuy = Math.Max(0, ReadInt(args, "--wish-buy", 0));
        var wishPrice = long.TryParse(GetOption(args, "--wish-price"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var price) ? Math.Max(0, price) : 0;

        var planner = new HermitPlanner();
        var plan = planner.Plan(new HermitSnapshot
        {
            Permits = permits,
            Clovers = clovers,
            WishesAvailable = wishes,
            ReserveAdventureValue = reserveAdventureValue,
            MinimumCloverSaleValue = cloverFloor,
            ExpectedCloverSaleValue = expectedClover,
            WishBuyQuantity = wishBuy,
            WishUnitPrice = wishPrice
        });

        WriteJson(new
        {
            command = "daily hermit",
            readOnly = true,
            previewOnly = true,
            plan.Verdict,
            plan.ShouldUseClover,
            plan.ShouldBuyWishes,
            plan.Rationale,
            plan.Steps,
            plan.Snapshot,
            note = "Preview only; no choice.php POST is sent."
        });
        return 0;
    }

    private static int DailyInternetShopAsync(string[] args)
    {
        var pulled = GetOptions(args, "--already-claimed").ToArray();
        var meat = long.TryParse(GetOption(args, "--meat"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var meatOverride) ? Math.Max(0, meatOverride) : 0;
        var plan = new DailyInternetShopPlanner().Plan(new InternetShopSnapshot
        {
            AlreadyPulledItemIds = pulled,
            AvailableMeat = meat
        });
        WriteJson(new
        {
            command = "daily internet-shop",
            readOnly = true,
            previewOnly = true,
            plan.Verdict,
            plan.Rationale,
            plan.Steps,
            plan.Skipped,
            note = "Preview only; no choice POST is sent. The bounded runner can replay each step after --execute --approval-token."
        });
        return 0;
    }

    private static int DailyBaconAsync(string[] args)
    {
        var snapshot = new BaconSnapshot
        {
            OwnedMachines = ReadInt(args, "--owned", 0),
            MallPricePerMachine = long.TryParse(GetOption(args, "--mall-price"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mall) ? Math.Max(0, mall) : 0,
            PricegunMedianSellPrice = long.TryParse(GetOption(args, "--median"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var median) ? Math.Max(0, median) : 0,
            AvailableMeat = long.TryParse(GetOption(args, "--meat"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var meat) ? Math.Max(0, meat) : 0,
            MinimumProfitPerUnit = long.TryParse(GetOption(args, "--min-profit"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minProfit) ? Math.Max(0, minProfit) : 50_000,
            MaximumBuyQuantity = ReadInt(args, "--max-qty", 10)
        };
        var plan = new BaconArbitragePlanner().Plan(snapshot);
        WriteJson(new
        {
            command = "daily bacon",
            readOnly = true,
            previewOnly = true,
            plan.ShouldBuy,
            plan.Verdict,
            plan.SuggestedQuantity,
            plan.ExpectedProfitPerUnit,
            plan.Rationale,
            plan.Snapshot,
            note = "Preview only; no mall purchase is sent."
        });
        return 0;
    }

    private static void PrintDailyHelp()
    {
        Console.WriteLine("  daily campfire [--leaves N] [--reserve N] [--knucklebones N]");
        Console.WriteLine("                 [--shortner-price N] [--meat N] [--not-useful]");
        Console.WriteLine("  daily hermit [--permits N] [--clovers N] [--adventure-value N]");
        Console.WriteLine("              [--clover-floor N] [--reserve N]");
        Console.WriteLine("  daily internet-shop [--already-claimed NAME] [--meat N]");
        Console.WriteLine("  daily bacon [--owned N] [--mall-price N] [--median N] [--meat N]");
        Console.WriteLine("               [--min-profit N] [--max-qty N]");
    }
}
