namespace KolArbitrage.Core.Pricing;

/// <summary>
/// A read-only estimate for one item dropped by an adventure.  Quantity is the
/// expected quantity per adventure, so fractional values (drop rates) are
/// valid.  Prices are net of the normal 1% Mall fee and any per-item costs
/// when <see cref="NetUnitValue"/> is used.
/// </summary>
public sealed record FarmDrop
{
    public string ItemId { get; init; }
    public decimal ExpectedQuantityPerAdventure { get; init; }
    public decimal ExpectedSellPrice { get; init; }
    public decimal Confidence { get; init; }
    public decimal SaleFeePercent { get; init; }
    public decimal ListingFeePerItem { get; init; }
    public decimal PackagingCostPerItem { get; init; }
    public bool IsTradeable { get; init; }

    public FarmDrop(
        string itemId,
        decimal expectedQuantityPerAdventure,
        decimal expectedSellPrice,
        decimal confidence = 1m,
        decimal saleFeePercent = 0.01m,
        decimal listingFeePerItem = 0m,
        decimal packagingCostPerItem = 0m,
        bool isTradeable = true)
    {
        if (string.IsNullOrWhiteSpace(itemId)) throw new ArgumentException("Item id is required.", nameof(itemId));
        if (expectedQuantityPerAdventure < 0) throw new ArgumentOutOfRangeException(nameof(expectedQuantityPerAdventure));
        if (expectedSellPrice < 0) throw new ArgumentOutOfRangeException(nameof(expectedSellPrice));
        if (confidence is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(confidence));
        if (saleFeePercent is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(saleFeePercent));
        if (listingFeePerItem < 0) throw new ArgumentOutOfRangeException(nameof(listingFeePerItem));
        if (packagingCostPerItem < 0) throw new ArgumentOutOfRangeException(nameof(packagingCostPerItem));

        ItemId = itemId.Trim();
        ExpectedQuantityPerAdventure = expectedQuantityPerAdventure;
        ExpectedSellPrice = expectedSellPrice;
        Confidence = confidence;
        SaleFeePercent = saleFeePercent;
        ListingFeePerItem = listingFeePerItem;
        PackagingCostPerItem = packagingCostPerItem;
        IsTradeable = isTradeable;
    }

    /// <summary>Expected proceeds for one item after Mall fee and handling costs.</summary>
    public decimal NetUnitValue
        => Math.Max(0m, ExpectedSellPrice * (1m - SaleFeePercent) - ListingFeePerItem - PackagingCostPerItem);

    public decimal UnitValueInMeat => NetUnitValue;

    /// <summary>Expected (confidence-unadjusted) value per adventure.</summary>
    public decimal ExpectedValuePerAdventure
        => IsTradeable ? ExpectedQuantityPerAdventure * NetUnitValue : 0m;

    /// <summary>Expected value per adventure after applying source confidence.</summary>
    public decimal ExpectedConfidenceAdjustedValue
        => ExpectedValuePerAdventure * Confidence;
}

/// <summary>A target item and the quantity still required by the caller.</summary>
public sealed record FarmTargetResource
{
    public string ItemId { get; init; }
    public int TargetQuantity { get; init; }
    public int CurrentQuantity { get; init; }
    public decimal MallUnitPrice { get; init; }
    public decimal MallConfidence { get; init; }

    public FarmTargetResource(
        string itemId,
        int targetQuantity,
        decimal mallUnitPrice,
        int currentQuantity = 0,
        decimal mallConfidence = 1m)
    {
        if (string.IsNullOrWhiteSpace(itemId)) throw new ArgumentException("Item id is required.", nameof(itemId));
        if (targetQuantity < 0) throw new ArgumentOutOfRangeException(nameof(targetQuantity));
        if (currentQuantity < 0) throw new ArgumentOutOfRangeException(nameof(currentQuantity));
        if (mallUnitPrice < 0) throw new ArgumentOutOfRangeException(nameof(mallUnitPrice));
        if (mallConfidence is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(mallConfidence));

        ItemId = itemId.Trim();
        TargetQuantity = targetQuantity;
        CurrentQuantity = currentQuantity;
        MallUnitPrice = mallUnitPrice;
        MallConfidence = mallConfidence;
    }

    public int RemainingQuantity => Math.Max(0, TargetQuantity - CurrentQuantity);
    public decimal DirectMallCost => RemainingQuantity * MallUnitPrice;
    public int Quantity => TargetQuantity;
    public int ExistingQuantity => CurrentQuantity;
    public decimal MallUnitPriceInMeat => MallUnitPrice;
    public decimal Confidence => MallConfidence;
}

/// <summary>A consumable used by each adventure, represented by its Meat cost.</summary>
public sealed record FarmConsumableCost
{
    public string ItemId { get; init; }
    public decimal QuantityPerAdventure { get; init; }
    public decimal UnitCost { get; init; }
    public decimal Confidence { get; init; }

    public FarmConsumableCost(
        string itemId,
        decimal quantityPerAdventure,
        decimal unitCost,
        decimal confidence = 1m)
    {
        if (string.IsNullOrWhiteSpace(itemId)) throw new ArgumentException("Item id is required.", nameof(itemId));
        if (quantityPerAdventure < 0) throw new ArgumentOutOfRangeException(nameof(quantityPerAdventure));
        if (unitCost < 0) throw new ArgumentOutOfRangeException(nameof(unitCost));
        if (confidence is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(confidence));

        ItemId = itemId.Trim();
        QuantityPerAdventure = quantityPerAdventure;
        UnitCost = unitCost;
        Confidence = confidence;
    }

    public decimal CostPerAdventure => QuantityPerAdventure * UnitCost;
    public decimal UnitCostInMeat => UnitCost;
}

/// <summary>
/// A zone snapshot used by the farm planner.  It contains no executable
/// action; all values are read estimates and can be supplied by a Mall,
/// Pricegun or a local fixture.
/// </summary>
public sealed record FarmZone
{
    public string ZoneId { get; init; }
    public string Name { get; init; }
    public IReadOnlyList<FarmDrop> Drops { get; init; }
    /// <summary>One-time ticket cost for the planned run, in Meat.</summary>
    public decimal TicketCost { get; init; }
    /// <summary>Ticket cost charged on every adventure, if a zone works that way.</summary>
    public decimal TicketCostPerAdventure { get; init; }
    /// <summary>Opportunity cost of spending one adventure turn.</summary>
    public decimal TurnOpportunityCostPerAdventure { get; init; }
    public IReadOnlyList<FarmConsumableCost> Consumables { get; init; }
    /// <summary>Confidence in the zone/drop model, independent of item prices.</summary>
    public decimal Confidence { get; init; }
    /// <summary>Confidence in the ticket and cost snapshot.</summary>
    public decimal TicketConfidence { get; init; } = 1m;
    /// <summary>Optional turn cap from a fresh account snapshot.</summary>
    public int? AvailableTurns { get; init; }

    public FarmZone(
        string zoneId,
        string name,
        IEnumerable<FarmDrop> drops,
        decimal ticketCost = 0m,
        decimal turnOpportunityCostPerAdventure = 0m,
        decimal consumableCostPerAdventure = 0m,
        decimal confidence = 1m,
        int? availableTurns = null,
        decimal ticketCostPerAdventure = 0m,
        IEnumerable<FarmConsumableCost>? consumables = null,
        decimal ticketConfidence = 1m)
    {
        if (string.IsNullOrWhiteSpace(zoneId)) throw new ArgumentException("Zone id is required.", nameof(zoneId));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Zone name is required.", nameof(name));
        if (drops is null) throw new ArgumentNullException(nameof(drops));
        if (ticketCost < 0) throw new ArgumentOutOfRangeException(nameof(ticketCost));
        if (ticketCostPerAdventure < 0) throw new ArgumentOutOfRangeException(nameof(ticketCostPerAdventure));
        if (turnOpportunityCostPerAdventure < 0) throw new ArgumentOutOfRangeException(nameof(turnOpportunityCostPerAdventure));
        if (consumableCostPerAdventure < 0) throw new ArgumentOutOfRangeException(nameof(consumableCostPerAdventure));
        if (confidence is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(confidence));
        if (ticketConfidence is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(ticketConfidence));
        if (availableTurns is < 0) throw new ArgumentOutOfRangeException(nameof(availableTurns));

        ZoneId = zoneId.Trim();
        Name = name.Trim();
        Drops = drops.ToArray();
        TicketCost = ticketCost;
        TicketCostPerAdventure = ticketCostPerAdventure;
        TurnOpportunityCostPerAdventure = turnOpportunityCostPerAdventure;
        Consumables = consumables?.ToArray() ?? Array.Empty<FarmConsumableCost>();
        // The scalar exists for callers that only have a total consumable
        // estimate; it is folded into the list as a synthetic cost.
        if (consumableCostPerAdventure > 0)
        {
            var list = Consumables.ToList();
            list.Add(new FarmConsumableCost("<aggregate>", 1m, consumableCostPerAdventure));
            Consumables = list;
        }
        Confidence = confidence;
        TicketConfidence = ticketConfidence;
        AvailableTurns = availableTurns;
    }

    public FarmZone(string zoneId, IEnumerable<FarmDrop> drops,
        decimal ticketCost = 0m, decimal turnOpportunityCostPerAdventure = 0m,
        decimal consumableCostPerAdventure = 0m, decimal confidence = 1m,
        int? availableTurns = null)
        : this(zoneId, zoneId, drops, ticketCost, turnOpportunityCostPerAdventure,
            consumableCostPerAdventure, confidence, availableTurns)
    {
    }

    public decimal ConsumableCostPerAdventure
        => Consumables.Sum(x => x.CostPerAdventure);
    public decimal TicketCostInMeat => TicketCost;
    public decimal TurnOpportunityCostInMeat => TurnOpportunityCostPerAdventure;
    public decimal ConsumableCostInMeat => ConsumableCostPerAdventure;

    public decimal CostPerAdventure
        => TicketCostPerAdventure + TurnOpportunityCostPerAdventure + ConsumableCostPerAdventure;

    public decimal ExpectedDropValuePerAdventure
        => Drops.Sum(x => x.ExpectedValuePerAdventure);

    public decimal ExpectedConfidenceAdjustedDropValuePerAdventure
        => Drops.Sum(x => x.ExpectedConfidenceAdjustedValue) * Confidence;
}

/// <summary>Input to <see cref="FarmVsBuyPlanner"/>.</summary>
public record FarmRoiRequest
{
    public FarmZone Zone { get; init; }
    public IReadOnlyList<FarmTargetResource> Targets { get; init; }
    /// <summary>Optional run cap; otherwise the zone's available turns are used.</summary>
    public int? MaxAdventures { get; init; }
    /// <summary>Additional one-time cost, such as a transfer or setup fee.</summary>
    public decimal AdditionalOneTimeCost { get; init; }
    /// <summary>Additional cost charged on every adventure.</summary>
    public decimal AdditionalCostPerAdventure { get; init; }

    public FarmRoiRequest(
        FarmZone zone,
        IEnumerable<FarmTargetResource> targets,
        int? maxAdventures = null,
        decimal additionalOneTimeCost = 0m,
        decimal additionalCostPerAdventure = 0m)
    {
        Zone = zone ?? throw new ArgumentNullException(nameof(zone));
        if (targets is null) throw new ArgumentNullException(nameof(targets));
        if (maxAdventures is < 0) throw new ArgumentOutOfRangeException(nameof(maxAdventures));
        if (additionalOneTimeCost < 0) throw new ArgumentOutOfRangeException(nameof(additionalOneTimeCost));
        if (additionalCostPerAdventure < 0) throw new ArgumentOutOfRangeException(nameof(additionalCostPerAdventure));
        Targets = targets.ToArray();
        MaxAdventures = maxAdventures;
        AdditionalOneTimeCost = additionalOneTimeCost;
        AdditionalCostPerAdventure = additionalCostPerAdventure;
    }
}

/// <summary>Terminology alias for callers that name the request FarmVsBuy.</summary>
public sealed record FarmVsBuyRequest(
    FarmZone Zone,
    IReadOnlyList<FarmTargetResource> Targets,
    int? MaxAdventures = null,
    decimal AdditionalOneTimeCost = 0m,
    decimal AdditionalCostPerAdventure = 0m)
    : FarmRoiRequest(Zone, Targets, MaxAdventures, AdditionalOneTimeCost, AdditionalCostPerAdventure);

public enum FarmRoiDecision
{
    TargetReached,
    Farm,
    Buy,
    Indeterminate,
    BlockedNoYield,
    BlockedNoTurns,
    BlockedLowConfidence
}

/// <summary>Equivalent decision names for callers using farm-vs-buy terminology.</summary>
public enum FarmVsBuyDecision
{
    TargetReached,
    Farm,
    Buy,
    Indeterminate,
    BlockedNoYield,
    BlockedNoTurns,
    BlockedLowConfidence
}

/// <summary>Pure result of a farm-vs-buy calculation; always preview-only.</summary>
public sealed record FarmRoiResult
{
    public required FarmRoiRequest Request { get; init; }
    public required FarmRoiDecision Decision { get; init; }
    public required int AdventuresRequired { get; init; }
    public required int AdventuresPlanned { get; init; }
    public required decimal ExpectedDropValuePerAdventure { get; init; }
    public required decimal ExpectedTargetValuePerAdventure { get; init; }
    public required decimal DirectMallCost { get; init; }
    public required decimal FarmCost { get; init; }
    public required decimal ExpectedFarmValue { get; init; }
    public required decimal FarmAdvantage { get; init; }
    public required decimal Confidence { get; init; }
    public required bool IsPreviewOnly { get; init; }
    public required IReadOnlyList<string> Advisories { get; init; }

    public bool IsActionable => Decision is FarmRoiDecision.Farm or FarmRoiDecision.Buy;
    public bool IsProfitableToFarm => FarmAdvantage > 0m;
    public decimal MallBuyCost => DirectMallCost;
    public decimal FarmValuePerAdventure => ExpectedDropValuePerAdventure;
    public decimal FarmNetValue => ExpectedFarmValue - FarmCost;
    public bool PreviewOnly => IsPreviewOnly;
    public FarmVsBuyDecision Recommendation => (FarmVsBuyDecision)Decision;
    public decimal TicketCostInMeat => AdventuresPlanned > 0 ? Request.Zone.TicketCost : 0m;
    public decimal TurnOpportunityCostInMeat => AdventuresPlanned * Request.Zone.TurnOpportunityCostPerAdventure;
    public decimal ConsumableCostInMeat => AdventuresPlanned * Request.Zone.ConsumableCostPerAdventure;
    public decimal AdditionalCostInMeat =>
        (AdventuresPlanned > 0 ? Request.AdditionalOneTimeCost : 0m)
        + AdventuresPlanned * Request.AdditionalCostPerAdventure;
}
