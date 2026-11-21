using KolArbitrage.Core.Adventure;

namespace KolArbitrage.Core.Daily;

/// <summary>
/// A read-only daily "pull" from a per-day free item. The bridge can later
/// route the choice POST through the bounded choice chain; the planner
/// itself never touches HTTP.
/// </summary>
public sealed record InternetShopFreePull
{
    public required string ChoiceId { get; init; }
    public required string OptionId { get; init; }
    public required string DisplayName { get; init; }
    public required string SourcePage { get; init; } // e.g. "shop.php?whichshop=exotic"
    public required bool AlreadyClaimed { get; init; }
}

/// <summary>
/// Inputs the internet-shop planner needs. All values are read-only snapshots
/// supplied by the caller.
/// </summary>
public sealed record InternetShopSnapshot
{
    /// <summary>Items the player has already pulled today.</summary>
    public IReadOnlyList<string> AlreadyPulledItemIds { get; init; } = Array.Empty<string>();

    /// <summary>Meat available today.</summary>
    public long AvailableMeat { get; init; }

    /// <summary>BACON the account holds right now.</summary>
    public int Bacon { get; init; }

    /// <summary>Whether the account owns an Infinite BACON machine (item 9025).</summary>
    public bool BaconMachineOwned { get; init; }

    /// <summary>Whether the machine was already pushed today (it gives 100 BACON once per day).</summary>
    public bool BaconMachineUsedToday { get; init; }

    /// <summary>Optional list of free-pull items. Empty => the planner uses its
    /// built-in catalog of well-known daily pulls.</summary>
    public IReadOnlyList<InternetShopFreePull> Catalog { get; init; } = Array.Empty<InternetShopFreePull>();
}

/// <summary>
/// Result of the internet-shop planner.
/// </summary>
public sealed record InternetShopPlan
{
    public required string Verdict { get; init; }
    public required InternetShopPlanStep[] Steps { get; init; }
    public required string Rationale { get; init; }
    public required IReadOnlyList<string> Skipped { get; init; }
}

public sealed record InternetShopPlanStep
{
    public required string Kind { get; init; } // "free-pull" | "purchase"
    public required string ChoiceId { get; init; }
    public required string OptionId { get; init; }
    public required string Description { get; init; }
}

/// <summary>
/// Plans the daily Internet Meme Shop visit: push the Infinite BACON machine
/// once (100 BACON), then buy each once-per-day item while BACON lasts.
/// Pure and read-only; the bridge performs the requests.
/// </summary>
public sealed class DailyInternetShopPlanner
{
    public const string MachineChoiceId = "meme-bacon-machine";
    public const string BuyChoiceId = "meme-buy";
    public const int MachineYield = 100;

    /// <summary>
    /// BACON still missing for today's unclaimed daily items, counting the machine's yield when it
    /// can still be pushed. Zero when the balance already covers everything.
    /// </summary>
    public static int BaconShortfall(
        IReadOnlyCollection<string> alreadyPulledItemIds, int bacon, bool machineOwned, bool machineUsedToday)
    {
        var remaining = MemeShopCatalog.DailyItems
            .Where(i => !alreadyPulledItemIds.Contains(i.Name, StringComparer.OrdinalIgnoreCase))
            .Sum(i => i.Price);
        var projected = bacon + (machineOwned && !machineUsedToday ? MachineYield : 0);
        return Math.Max(0, remaining - projected);
    }

    public DailyInternetShopPlannerOptions Options { get; }

    public DailyInternetShopPlanner(DailyInternetShopPlannerOptions? options = null)
        => Options = options ?? new DailyInternetShopPlannerOptions();

    public InternetShopPlan Plan(InternetShopSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var bought = new HashSet<string>(snapshot.AlreadyPulledItemIds, StringComparer.OrdinalIgnoreCase);
        var steps = new List<InternetShopPlanStep>();
        var skipped = new List<string>();
        var projected = (long)Math.Max(0, snapshot.Bacon);

        if (snapshot.BaconMachineOwned && !snapshot.BaconMachineUsedToday)
        {
            steps.Add(new InternetShopPlanStep
            {
                Kind = "use-item",
                ChoiceId = MachineChoiceId,
                OptionId = "1",
                Description = $"Push the Infinite BACON machine (+{MachineYield} BACON, once per day)."
            });
            projected += MachineYield;
        }
        else if (snapshot.BaconMachineOwned)
        {
            skipped.Add("Infinite BACON machine (already used today)");
        }

        foreach (var (name, price) in MemeShopCatalog.DailyItems)
        {
            if (bought.Contains(name)) { skipped.Add($"{name} (already bought today)"); continue; }
            if (projected < price) { skipped.Add($"{name} (needs {price} BACON, {projected} left)"); continue; }
            projected -= price;
            steps.Add(new InternetShopPlanStep
            {
                Kind = "purchase",
                ChoiceId = BuyChoiceId,
                OptionId = name,
                Description = $"Buy '{name}' from the Internet Meme Shop for {price} BACON."
            });
        }

        var buys = steps.Count(x => x.Kind == "purchase");
        var missing = MemeShopCatalog.DailyItems.Count(i => !bought.Contains(i.Name)) - buys;
        var verdict = steps.Count == 0 ? (missing > 0 ? "NeedBacon" : "AllClaimed")
            : missing > 0 ? "Partial" : "BuyAll";
        return new InternetShopPlan
        {
            Verdict = verdict,
            Steps = steps.ToArray(),
            Rationale = $"{buys} daily item(s) planned, {missing} short of BACON; {projected} BACON left after the plan.",
            Skipped = skipped
        };
    }
}

/// <summary>
/// Tunable thresholds for <see cref="DailyInternetShopPlanner"/>.
/// </summary>
public sealed record DailyInternetShopPlannerOptions
{
    /// <summary>Free pulls always go first; purchases only happen when there is meat budget left.</summary>
    public long MeatReserveForOptionalPurchases { get; init; } = 100_000;
}
