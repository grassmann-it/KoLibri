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
/// Static catalog of well-known daily pulls. The list is intentionally
/// explicit so a reviewer can audit which "free once a day" sources the
/// planner touches. Bacon is included so the routine can claim the
/// Infinite Bacon Machine from the Crackpot Masterson quest page.
/// </summary>
public static class InternetShopCatalog
{
    public static readonly IReadOnlyList<InternetShopFreePull> DefaultPulls = new[]
    {
        new InternetShopFreePull
        {
            ChoiceId = "choice-lucky-surprise-egg",
            OptionId = "1",
            DisplayName = "Lucky Surprise Egg",
            SourcePage = "inventory.php?which=4",
            AlreadyClaimed = false
        },
        new InternetShopFreePull
        {
            ChoiceId = "choice-internet-bacon",
            OptionId = "1",
            DisplayName = "Internet Bacon",
            SourcePage = "choice.php?whichchoice=1119",
            AlreadyClaimed = false
        },
        new InternetShopFreePull
        {
            ChoiceId = "choice-bacon-machine",
            OptionId = "1",
            DisplayName = "Infinite Bacon Machine",
            SourcePage = "choice.php?whichchoice=1118",
            AlreadyClaimed = false
        }
    };
}

/// <summary>
/// Read-only planner for daily free pulls (Hermit, Campfire, Internet Bacon,
/// Lucky Surprise Egg, …). Each free pull becomes a typed choice POST that
/// the bridge can route through the bounded choice chain.
/// </summary>
public sealed class DailyInternetShopPlanner
{
    public DailyInternetShopPlannerOptions Options { get; }

    public DailyInternetShopPlanner(DailyInternetShopPlannerOptions? options = null)
        => Options = options ?? new DailyInternetShopPlannerOptions();

    public InternetShopPlan Plan(InternetShopSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var pulled = new HashSet<string>(snapshot.AlreadyPulledItemIds, StringComparer.OrdinalIgnoreCase);
        var steps = new List<InternetShopPlanStep>();
        var skipped = new List<string>();
        var catalog = snapshot.Catalog.Count == 0
            ? InternetShopCatalog.DefaultPulls
            : snapshot.Catalog;

        foreach (var pull in catalog)
        {
            if (pull.AlreadyClaimed || pulled.Contains(pull.DisplayName))
            {
                skipped.Add(pull.DisplayName);
                continue;
            }
            steps.Add(new InternetShopPlanStep
            {
                Kind = "free-pull",
                ChoiceId = pull.ChoiceId,
                OptionId = pull.OptionId,
                Description = $"Claim daily free pull '{pull.DisplayName}' from {pull.SourcePage}."
            });
        }

        if (steps.Count == 0)
        {
            return new InternetShopPlan
            {
                Verdict = "AllClaimed",
                Steps = steps.ToArray(),
                Rationale = $"All {catalog.Count} daily pulls already claimed.",
                Skipped = skipped
            };
        }

        var verdict = steps.Count == catalog.Count ? "ClaimAll" : "ClaimRemaining";
        var rationale = $"{steps.Count} of {catalog.Count} daily pulls remain.";
        return new InternetShopPlan
        {
            Verdict = verdict,
            Steps = steps.ToArray(),
            Rationale = rationale,
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
