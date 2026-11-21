
namespace KoLibri.Core.Markt;

/// <summary>
/// Read-only observations used to select a single rotating flash-sale listing.
/// KoL exposes mall listings, not a native buy-order book, so this worker plans
/// one discounted sell listing at a time and never submits it by itself.
/// </summary>
public sealed record RotatingSaleCandidate
{
    public required string ItemId { get; init; }
    public string? DisplayName { get; init; }
    public long CurrentAsk { get; init; }
    public int OwnedQuantity { get; init; }
    public decimal? AcquisitionCost { get; init; }
    public long? MallLowest { get; init; }
    public long? MallSecond { get; init; }
    public decimal? MallMedian { get; init; }
    public decimal? PricegunMedian { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
    public bool IsStale { get; init; }
    public bool OwnStoreContaminatesMarket { get; init; }
    public DateTimeOffset? LastRotatedAt { get; init; }
}

public sealed record RotatingSalePolicy
{
    public int MaxOffersPerRun { get; init; } = 1;
    public decimal DiscountPercent { get; init; } = 0.05m;
    public decimal MinimumMarginPercent { get; init; } = 0.10m;
    public long MinimumPrice { get; init; } = 100;
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromHours(4);
    public bool RequireIndependentReference { get; init; } = true;

    public void Validate()
    {
        if (MaxOffersPerRun <= 0) throw new ArgumentOutOfRangeException(nameof(MaxOffersPerRun));
        if (DiscountPercent is < 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(DiscountPercent));
        if (MinimumMarginPercent < 0) throw new ArgumentOutOfRangeException(nameof(MinimumMarginPercent));
        if (MinimumPrice < 100) throw new ArgumentOutOfRangeException(nameof(MinimumPrice));
        if (Cooldown < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(Cooldown));
    }
}

public enum RotatingSaleStatus
{
    Ready,
    HoldNoInventory,
    HoldNoMarketReference,
    HoldStale,
    HoldOwnStoreContamination,
    HoldCooldown,
    HoldNoMargin,
    HoldAlreadyCompetitive
}

public sealed record RotatingSaleProposal(
    string ItemId,
    string? DisplayName,
    RotatingSaleStatus Status,
    long CurrentAsk,
    long? ProposedPrice,
    long? ReferencePrice,
    decimal? AcquisitionCost,
    decimal? ExpectedGrossProfit,
    ShopPriceUpdate? PriceUpdate,
    bool RequiresApproval,
    string Rationale);

public sealed record RotatingSalePlan(
    DateTimeOffset GeneratedAt,
    int MaxOffersPerRun,
    IReadOnlyList<RotatingSaleProposal> Proposals)
{
    public IReadOnlyList<RotatingSaleProposal> Ready =>
        Proposals.Where(x => x.Status == RotatingSaleStatus.Ready).ToArray();

    public bool RequiresApproval => Ready.Count > 0;
}

/// <summary>
/// Selects at most a bounded number of one-unit promotional listings. Every
/// returned mutation is expected-price bound and must go through an approval
/// adapter such as <see cref="KoLShopMutationClient"/>.
/// </summary>
public sealed class RotatingSalePlanner
{
    private readonly RotatingSalePolicy _policy;

    public RotatingSalePlanner(RotatingSalePolicy? policy = null)
    {
        _policy = policy ?? new RotatingSalePolicy();
        _policy.Validate();
    }

    public RotatingSalePlan Build(
        IEnumerable<RotatingSaleCandidate> candidates,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var timestamp = now ?? DateTimeOffset.UtcNow;
        var ordered = candidates
            .Select(candidate => BuildProposal(candidate, timestamp))
            .OrderByDescending(x => x.ExpectedGrossProfit ?? decimal.MinValue)
            .ThenBy(x => x.ItemId, StringComparer.Ordinal)
            .ToArray();

        var readyIds = ordered
            .Where(x => x.Status == RotatingSaleStatus.Ready)
            .Take(_policy.MaxOffersPerRun)
            .Select(x => x.ItemId)
            .ToHashSet(StringComparer.Ordinal);
        var bounded = ordered.Select(x => x.Status == RotatingSaleStatus.Ready && !readyIds.Contains(x.ItemId)
            ? x with
            {
                Status = RotatingSaleStatus.HoldCooldown,
                RequiresApproval = false,
                PriceUpdate = null,
                Rationale = $"Run-Limit {_policy.MaxOffersPerRun} erreicht; beim nächsten Rotationslauf erneut prüfen."
            }
            : x).ToArray();
        return new RotatingSalePlan(timestamp, _policy.MaxOffersPerRun, bounded);
    }

    private RotatingSaleProposal BuildProposal(RotatingSaleCandidate candidate, DateTimeOffset now)
    {
        var displayName = candidate.DisplayName;
        if (candidate.OwnedQuantity <= 0)
            return Hold(candidate, RotatingSaleStatus.HoldNoInventory, "Kein Bestand für eine einzelne Rotationslistung.");
        if (candidate.IsStale)
            return Hold(candidate, RotatingSaleStatus.HoldStale, "Preisquelle ist stale; erst Mall/Pricegun aktualisieren.");
        if (candidate.OwnStoreContaminatesMarket)
            return Hold(candidate, RotatingSaleStatus.HoldOwnStoreContamination, "Eigene Listings verunreinigen den Mallboden; unabhängige Referenz fehlt.");
        if (candidate.LastRotatedAt is { } last && now - last < _policy.Cooldown)
            return Hold(candidate, RotatingSaleStatus.HoldCooldown, "Cooldown seit der letzten Rotationslistung ist noch nicht abgelaufen.");

        var reference = SelectReference(candidate);
        if (reference is null || reference <= 0)
            return Hold(candidate, RotatingSaleStatus.HoldNoMarketReference, "Keine unabhängige Mall-/Pricegun-Referenz.");
        var cost = candidate.AcquisitionCost;
        var marginFloor = cost is null
            ? _policy.MinimumPrice
            : Math.Max(_policy.MinimumPrice, decimal.ToInt64(decimal.Ceiling(cost.Value * (1m + _policy.MinimumMarginPercent))));
        var discounted = decimal.ToInt64(decimal.Floor(reference.Value * (1m - _policy.DiscountPercent)));
        var proposed = Math.Max(marginFloor, discounted);
        if (proposed >= candidate.CurrentAsk)
            return Hold(candidate, RotatingSaleStatus.HoldAlreadyCompetitive, "Aktueller Ask ist bereits mindestens so günstig wie das Rotationsziel.", reference);
        if (cost is not null && proposed < marginFloor)
            return Hold(candidate, RotatingSaleStatus.HoldNoMargin, "Der Promo-Preis würde die Mindestmarge unterschreiten.", reference);

        var profit = cost is null ? (decimal?)null : (decimal)proposed - cost.Value;
        var update = new ShopPriceUpdate(candidate.ItemId, candidate.CurrentAsk, proposed);
        return new RotatingSaleProposal(
            candidate.ItemId,
            displayName,
            RotatingSaleStatus.Ready,
            candidate.CurrentAsk,
            proposed,
            reference,
            cost,
            profit,
            update,
            RequiresApproval: true,
            "Ein Stück auf den unabhängigen Referenzpreis minus Rotationsrabatt setzen; Commit erst nach frischem Shop-Read.");
    }

    private long? SelectReference(RotatingSaleCandidate candidate)
    {
        if (_policy.RequireIndependentReference && candidate.MallSecond is null && candidate.PricegunMedian is null)
            return null;
        var values = new[]
        {
            candidate.MallSecond,
            candidate.MallMedian is > 0 ? decimal.ToInt64(decimal.Floor(candidate.MallMedian.Value)) : null,
            candidate.PricegunMedian is > 0 ? decimal.ToInt64(decimal.Floor(candidate.PricegunMedian.Value)) : null
        }.Where(x => x is > 0).Select(x => x!.Value).ToArray();
        return values.Length == 0 ? candidate.MallLowest : values.Min();
    }

    private static RotatingSaleProposal Hold(
        RotatingSaleCandidate candidate,
        RotatingSaleStatus status,
        string rationale,
        long? reference = null)
        => new(candidate.ItemId, candidate.DisplayName, status, candidate.CurrentAsk, null,
            reference ?? candidate.MallLowest, candidate.AcquisitionCost, null, null, false, rationale);
}
