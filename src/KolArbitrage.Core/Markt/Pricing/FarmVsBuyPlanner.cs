using KolArbitrage.Core.Markt;

namespace KolArbitrage.Core.Markt;

/// <summary>Guards for a farm-vs-buy preview.</summary>
public sealed record FarmRoiPlannerOptions
{
    public decimal MinimumConfidence { get; init; } = 0.35m;
    public bool ChargeTicketWhenNoRun { get; init; }

    public FarmRoiPlannerOptions Normalize()
    {
        if (MinimumConfidence is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(MinimumConfidence));
        return this;
    }
}

/// <summary>Alias for hosts that use FarmVsBuy terminology.</summary>
public sealed record FarmVsBuyPlannerOptions
{
    public decimal MinimumConfidence { get; init; } = 0.35m;
    public bool ChargeTicketWhenNoRun { get; init; }

    internal FarmRoiPlannerOptions ToRoiOptions()
        => new() { MinimumConfidence = MinimumConfidence, ChargeTicketWhenNoRun = ChargeTicketWhenNoRun };
}

/// <summary>
/// Calculates the expected value of farming a zone and compares it with a
/// direct Mall purchase. This class only consumes snapshots and never calls a
/// game endpoint or spends a ticket, turn or item.
/// </summary>
public class FarmVsBuyPlanner
{
    private readonly FarmRoiPlannerOptions _defaults;

    public FarmVsBuyPlanner(FarmRoiPlannerOptions? options = null)
        => _defaults = (options ?? new FarmRoiPlannerOptions()).Normalize();

    public FarmRoiResult Plan(FarmRoiRequest request, FarmRoiPlannerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = (options ?? _defaults).Normalize();
        var zone = request.Zone;
        var targets = request.Targets;
        var advisories = new List<string>();
        var directMallCost = targets.Sum(x => x.DirectMallCost);
        var remainingTargets = targets.Where(x => x.RemainingQuantity > 0).ToArray();
        var mallConfidence = remainingTargets.Length == 0 ? 1m : remainingTargets.Min(x => x.MallConfidence);

        if (remainingTargets.Length == 0)
        {
            advisories.Add("All target resources are already available.");
            return BuildResult(request, FarmRoiDecision.TargetReached, 0, 0,
                zone.ExpectedDropValuePerAdventure, 0m, directMallCost, 0m, 0m, 0m,
                Math.Clamp(zone.Confidence * mallConfidence, 0m, 1m), advisories);
        }

        var targetDropRates = remainingTargets.ToDictionary(
            target => target.ItemId,
            target => zone.Drops
                .Where(drop => drop.IsTradeable && string.Equals(drop.ItemId, target.ItemId, StringComparison.OrdinalIgnoreCase))
                .Sum(drop => drop.ExpectedQuantityPerAdventure),
            StringComparer.OrdinalIgnoreCase);

        var adventuresRequired = 0;
        foreach (var target in remainingTargets)
        {
            var quantityPerAdventure = targetDropRates[target.ItemId];
            if (quantityPerAdventure <= 0)
            {
                advisories.Add($"Zone {zone.ZoneId} has no expected drop for target {target.ItemId}.");
                return BuildResult(request, FarmRoiDecision.BlockedNoYield, int.MaxValue, 0,
                    zone.ExpectedDropValuePerAdventure, 0m, directMallCost, 0m, 0m, 0m,
                    0m, advisories);
            }
            adventuresRequired = Math.Max(adventuresRequired,
                DivideRoundUp(target.RemainingQuantity, quantityPerAdventure));
        }

        var runCap = Math.Max(0, request.MaxAdventures ?? zone.AvailableTurns ?? int.MaxValue);
        var adventuresPlanned = Math.Min(adventuresRequired, runCap);
        if (adventuresPlanned < adventuresRequired)
            advisories.Add($"Only {adventuresPlanned} adventure turn(s) are available; {adventuresRequired} are needed for the target.");

        var dropValuePerAdventure = zone.ExpectedDropValuePerAdventure;
        var targetValuePerAdventure = GetTargetValuePerAdventure(zone, remainingTargets);
        var expectedFarmValue = SafeProduct(adventuresPlanned, dropValuePerAdventure);
        var expectedTargetValue = SafeProduct(adventuresPlanned, targetValuePerAdventure);
        var expectedTargetAvoidedMallCost = GetExpectedAvoidedMallCost(zone, remainingTargets, adventuresPlanned);
        // Target drops satisfy the requested stock and are valued through the
        // Mall cost avoided above. Only non-target drops are resale value that
        // offsets the farming costs; do not charge a target drop's Mall-vs-sale
        // price delta a second time.
        var expectedExtraValue = SafeProduct(adventuresPlanned, zone.Drops
            .Where(drop => drop.IsTradeable && !remainingTargets.Any(target =>
                string.Equals(target.ItemId, drop.ItemId, StringComparison.OrdinalIgnoreCase)))
            .Sum(drop => drop.ExpectedValuePerAdventure * drop.Confidence));
        var farmCost = (adventuresPlanned > 0 || settings.ChargeTicketWhenNoRun
                ? zone.TicketCost + request.AdditionalOneTimeCost
                : 0m)
            + SafeProduct(adventuresPlanned, zone.CostPerAdventure + request.AdditionalCostPerAdventure);
        var farmAdvantage = expectedTargetAvoidedMallCost + expectedExtraValue - farmCost;
        var confidence = ComputeConfidence(zone, remainingTargets, adventuresPlanned, adventuresRequired);
        if (confidence < settings.MinimumConfidence)
            advisories.Add($"Combined estimate confidence {confidence:0.##} is below the planning threshold {settings.MinimumConfidence:0.##}.");

        var decision = DetermineDecision(adventuresPlanned, adventuresRequired, farmAdvantage,
            confidence, settings.MinimumConfidence, advisories);
        return BuildResult(request, decision, adventuresRequired, adventuresPlanned,
            dropValuePerAdventure, targetValuePerAdventure, directMallCost, farmCost,
            expectedFarmValue, farmAdvantage, confidence, advisories);
    }

    public FarmRoiResult Plan(FarmZone zone, IEnumerable<FarmTargetResource> targets,
        int? maxAdventures = null, FarmRoiPlannerOptions? options = null)
        => Plan(new FarmRoiRequest(zone, targets, maxAdventures), options);

    public Task<FarmRoiResult> PlanAsync(FarmRoiRequest request,
        FarmRoiPlannerOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Plan(request, options));
    }

    private static FarmRoiDecision DetermineDecision(int adventuresPlanned, int adventuresRequired,
        decimal farmAdvantage, decimal confidence, decimal minimumConfidence, ICollection<string> advisories)
    {
        if (confidence < minimumConfidence) return FarmRoiDecision.BlockedLowConfidence;
        if (adventuresPlanned <= 0)
        {
            advisories.Add("No adventure turns are available for this target.");
            return FarmRoiDecision.BlockedNoTurns;
        }
        if (adventuresPlanned < adventuresRequired) return FarmRoiDecision.Buy;
        if (farmAdvantage > 0m) return FarmRoiDecision.Farm;
        if (farmAdvantage < 0m) return FarmRoiDecision.Buy;
        return FarmRoiDecision.Indeterminate;
    }

    private static decimal ComputeConfidence(FarmZone zone, IReadOnlyCollection<FarmTargetResource> targets,
        int adventuresPlanned, int adventuresRequired)
    {
        if (adventuresPlanned <= 0 || adventuresRequired <= 0) return 0m;
        var relevantDrops = zone.Drops
            .Where(drop => targets.Any(target => string.Equals(target.ItemId, drop.ItemId, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var dropWeight = relevantDrops.Sum(x => x.ExpectedQuantityPerAdventure);
        var dropConfidence = relevantDrops.Length == 0 || dropWeight <= 0m
            ? 0m
            : relevantDrops.Sum(x => x.ExpectedQuantityPerAdventure * x.Confidence) / dropWeight;
        var mallConfidence = targets.Count == 0 ? 0m : targets.Min(x => x.MallConfidence);
        var costConfidence = zone.Consumables.Count == 0
            ? zone.TicketConfidence
            : Math.Min(zone.TicketConfidence, zone.Consumables.Min(x => x.Confidence));
        return Math.Clamp(zone.Confidence * dropConfidence * mallConfidence * costConfidence, 0m, 1m);
    }

    private static decimal GetTargetValuePerAdventure(FarmZone zone, IReadOnlyCollection<FarmTargetResource> targets)
        => zone.Drops.Sum(drop =>
        {
            var target = targets.FirstOrDefault(x => string.Equals(x.ItemId, drop.ItemId, StringComparison.OrdinalIgnoreCase));
            return target is null || !drop.IsTradeable
                ? 0m
                : drop.ExpectedQuantityPerAdventure * target.MallUnitPrice * drop.Confidence;
        });

    private static decimal GetExpectedAvoidedMallCost(FarmZone zone,
        IReadOnlyCollection<FarmTargetResource> targets, int adventures)
    {
        if (adventures <= 0) return 0m;
        return targets.Sum(target =>
        {
            var expectedQuantity = zone.Drops
                .Where(drop => drop.IsTradeable && string.Equals(drop.ItemId, target.ItemId, StringComparison.OrdinalIgnoreCase))
                .Sum(drop => drop.ExpectedQuantityPerAdventure) * adventures;
            return Math.Min(target.RemainingQuantity, expectedQuantity) * target.MallUnitPrice;
        });
    }

    private static FarmRoiResult BuildResult(FarmRoiRequest request, FarmRoiDecision decision,
        int adventuresRequired, int adventuresPlanned, decimal expectedDropValuePerAdventure,
        decimal expectedTargetValuePerAdventure, decimal directMallCost, decimal farmCost,
        decimal expectedFarmValue, decimal farmAdvantage, decimal confidence, IReadOnlyList<string> advisories)
        => new()
        {
            Request = request,
            Decision = decision,
            AdventuresRequired = adventuresRequired,
            AdventuresPlanned = adventuresPlanned,
            ExpectedDropValuePerAdventure = expectedDropValuePerAdventure,
            ExpectedTargetValuePerAdventure = expectedTargetValuePerAdventure,
            DirectMallCost = directMallCost,
            FarmCost = farmCost,
            ExpectedFarmValue = expectedFarmValue,
            FarmAdvantage = farmAdvantage,
            Confidence = confidence,
            IsPreviewOnly = true,
            Advisories = advisories.ToArray()
        };

    private static int DivideRoundUp(int quantity, decimal rate)
    {
        try
        {
            var result = decimal.Ceiling(quantity / rate);
            return result >= int.MaxValue ? int.MaxValue : (int)result;
        }
        catch (OverflowException) { return int.MaxValue; }
    }

    private static decimal SafeProduct(int left, decimal right)
    {
        try { return left * right; }
        catch (OverflowException) { return decimal.MaxValue; }
    }
}

/// <summary>Short alias for hosts that call the calculation a Farm ROI planner.</summary>
public sealed class FarmRoiPlanner : FarmVsBuyPlanner
{
    public FarmRoiPlanner(FarmRoiPlannerOptions? options = null) : base(options) { }
}
