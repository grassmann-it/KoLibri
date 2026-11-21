using KoLibri.Core.Basis;
using KoLibri.Core.Markt;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Markt;

/// <summary>
/// Restocks the main account with 11-leaf clovers so the hermit trade can
/// run every day.
///
/// The flow is: the buying account (usually the mallbot, which holds the
/// meat) buys cheap clovers on the mall, then transfers them to the main
/// account, which trades them at the hermit. Because both accounts may buy,
/// the caller can run the planner per role — each account has its own
/// daily shop limit and its own meat pool.
///
/// The transfer goes through the regular approval flow: the executor only
/// previews and commits when the caller supplies an approval token.
/// </summary>
public sealed class CloverRestockExecutor
{
    /// <summary>The 11-leaf clover — the item the hermit trades for.</summary>
    public const int ElevenLeafClover = 10881;

    private readonly MallBuyClient _buyClient;
    private readonly IMallReadClient _mall;
    private readonly RestockPlanner _planner;

    public CloverRestockExecutor(
        MallBuyClient buyClient,
        IMallReadClient mall,
        RestockPlanner? planner = null)
    {
        _buyClient = buyClient ?? throw new ArgumentNullException(nameof(buyClient));
        _mall = mall ?? throw new ArgumentNullException(nameof(mall));
        _planner = planner ?? new RestockPlanner();
    }

    /// <summary>What the restock did.</summary>
    public sealed record Outcome
    {
        public required string Verdict { get; init; }
        public required bool Bought { get; init; }
        public required int Quantity { get; init; }
        public required long Spent { get; init; }
        public required string Rationale { get; init; }
        public RestockPlan? Plan { get; init; }
        public IReadOnlyList<MallBuyResult> Purchases { get; init; }
            = Array.Empty<MallBuyResult>();
    }

    /// <summary>Plans the restock without buying anything.</summary>
    public async Task<RestockPlan> PlanAsync(
        int ownedQuantity,
        int targetQuantity,
        long availableMeat,
        long maximumUnitPrice,
        long meatReserve = 200_000,
        CancellationToken cancellationToken = default)
    {
        var candidate = await FindCheapestAsync(cancellationToken).ConfigureAwait(false);
        return _planner.Plan(new RestockSnapshot
        {
            CheapestListing = candidate,
            AvailableMeat = availableMeat,
            MeatReserve = meatReserve,
            OwnedQuantity = ownedQuantity,
            TargetQuantity = targetQuantity,
            MaximumUnitPrice = maximumUnitPrice
        });
    }

    /// <summary>Buys the clovers the plan asks for.</summary>
    public async Task<Outcome> RunAsync(
        AccountCredentials credentials,
        int ownedQuantity,
        int targetQuantity,
        long availableMeat,
        long maximumUnitPrice,
        long meatReserve = 200_000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var plan = await PlanAsync(
            ownedQuantity, targetQuantity, availableMeat, maximumUnitPrice,
            meatReserve, cancellationToken).ConfigureAwait(false);
        if (!plan.ShouldBuy || plan.Candidate is null)
        {
            return new Outcome
            {
                Verdict = plan.Verdict,
                Bought = false,
                Quantity = 0,
                Spent = 0,
                Rationale = plan.Rationale,
                Plan = plan
            };
        }
        var candidate = plan.Candidate;
        var storeId = candidate.StoreId;
        if (storeId <= 0)
        {
            return new Outcome
            {
                Verdict = "NoStoreId",
                Bought = false,
                Quantity = 0,
                Spent = 0,
                Rationale = $"The listing has no usable store id ('{candidate.StoreId}').",
                Plan = plan
            };
        }
        var hash = await _buyClient.ReadPasswordHashAsync(
            credentials, storeId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(hash))
        {
            return new Outcome
            {
                Verdict = "NoStoreHash",
                Bought = false,
                Quantity = 0,
                Spent = 0,
                Rationale = $"Could not read the password hash for store {storeId}.",
                Plan = plan
            };
        }
        var purchase = await _buyClient.BuyAsync(
            credentials, storeId, candidate.ItemId, candidate.UnitPrice,
            plan.Quantity, hash, cancellationToken).ConfigureAwait(false);
        return new Outcome
        {
            Verdict = purchase.Accepted ? "Bought" : $"http-{purchase.StatusCode}",
            Bought = purchase.Accepted,
            Quantity = purchase.Accepted ? plan.Quantity : 0,
            Spent = purchase.Accepted ? plan.EstimatedSpend : 0,
            Rationale = purchase.Accepted
                ? $"Bought {plan.Quantity} clovers at {candidate.UnitPrice} meat each."
                : $"KoL rejected the purchase: {purchase.Message}",
            Plan = plan,
            Purchases = new[] { purchase }
        };
    }

    private async Task<RestockCandidate?> FindCheapestAsync(
        CancellationToken cancellationToken)
    {
        var snapshot = await _mall.SearchAsync(
            "11-leaf clover", cancellationToken: cancellationToken).ConfigureAwait(false);
        var listing = snapshot.Listings
            .Where(l => l.Price > 0)
            .OrderBy(l => l.Price)
            .FirstOrDefault();
        if (listing is null) return null;
        if (!MallBuyClient.TryParseStoreId(listing.StoreId, out var storeId))
            return null;
        return new RestockCandidate
        {
            StoreId = storeId,
            ItemId = ElevenLeafClover,
            ItemName = "11-leaf clover",
            UnitPrice = listing.Price,
            AvailableQuantity = listing.Quantity
        };
    }
}
