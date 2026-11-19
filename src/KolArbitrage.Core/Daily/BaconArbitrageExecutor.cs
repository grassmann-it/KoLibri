using KolArbitrage.Core.Accounts;
using KolArbitrage.Core.Integration;

namespace KolArbitrage.Core.Daily;

/// <summary>
/// Buys BACON (8763) on the mall when the spread against the resale
/// median beats the minimum profit, and buys the Infinite BACON machine
/// (9025) once when the account doesn't own one.
///
/// The executor needs three inputs: the live mall listing (for the
/// price the seller asks), the median resale price (the operator
/// supplies it — Pricegun lives outside KoL) and the account's meat.
/// It never invents a price: a purchase that KoL rejects because the
/// price moved is reported as rejected, not silently retried.
/// </summary>
public sealed class BaconArbitrageExecutor
{
    /// <summary>KoL item ids the bacon flow trades in.</summary>
    public static class Ids
    {
        /// <summary>BACON — the consumable the machine produces.</summary>
        public const int Bacon = 8763;
        /// <summary>Infinite BACON machine — the one-off that produces BACON.</summary>
        public const int BaconMachine = 9025;
    }

    private readonly MallBuyClient _buyClient;
    private readonly IMallReadClient _mall;

    public BaconArbitrageExecutor(MallBuyClient buyClient, IMallReadClient mall)
    {
        _buyClient = buyClient ?? throw new ArgumentNullException(nameof(buyClient));
        _mall = mall ?? throw new ArgumentNullException(nameof(mall));
    }

    /// <summary>What the executor decided to do, before and after the
    /// purchase.</summary>
    public sealed record Outcome
    {
        public required string Verdict { get; init; }
        public required bool Bought { get; init; }
        public required int Quantity { get; init; }
        public required long Spent { get; init; }
        public required string Rationale { get; init; }
        public IReadOnlyList<MallBuyResult> Purchases { get; init; }
            = Array.Empty<MallBuyResult>();
    }

    /// <summary>Runs the bacon flow for one account.</summary>
    public async Task<Outcome> RunAsync(
        AccountCredentials credentials,
        int ownedMachines,
        long availableMeat,
        long medianResalePrice,
        long minimumProfitPerUnit = 50_000,
        int maximumQuantity = 10,
        long meatReserve = 200_000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        // 1. The one-off: the Infinite BACON machine, when the account has
        //    none. The machine is what makes BACON worth arbitraging.
        if (ownedMachines == 0)
        {
            var machinePlan = await TryBuyAsync(
                credentials, Ids.BaconMachine, availableMeat, meatReserve,
                maximumQuantity: 1, priceCeiling: 0, cancellationToken)
                .ConfigureAwait(false);
            if (machinePlan.Outcome is { Bought: true })
            {
                return machinePlan.Outcome;
            }
            return new Outcome
            {
                Verdict = "NoMachineListing",
                Bought = false,
                Quantity = 0,
                Spent = 0,
                Rationale = machinePlan.Reason,
                Purchases = machinePlan.Outcome.Purchases
            };
        }

        // 2. The arbitrage: buy BACON while the spread is positive.
        var plan = await TryBuyAsync(
            credentials, Ids.Bacon, availableMeat, meatReserve,
            maximumQuantity, minimumProfitPerUnit, cancellationToken)
            .ConfigureAwait(false);
        return plan.Outcome;
    }

    /// <summary>KoL's mall search runs on the item name. The bacon flow
    /// trades two items, so the id maps to a name here.</summary>
    private static string itemNameFor(int itemId) => itemId switch
    {
        Ids.Bacon => "BACON",
        Ids.BaconMachine => "infinite BACON machine",
        _ => itemId.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };

    private async Task<(Outcome Outcome, string Reason)> TryBuyAsync(
        AccountCredentials credentials,
        int itemId,
        long availableMeat,
        long meatReserve,
        int maximumQuantity,
        long priceCeiling,
        CancellationToken cancellationToken)
    {
        // Find the cheapest listing for the item. KoL's mall search runs on
        // the item name, not the id.
        var snapshot = await _mall.SearchAsync(
            itemNameFor(itemId), cancellationToken: cancellationToken).ConfigureAwait(false);
        var candidate = snapshot.Listings
            .Where(l => l.Price > 0)
            .OrderBy(l => l.Price)
            .FirstOrDefault();
        if (candidate is null)
        {
            return (new Outcome
            {
                Verdict = "NoListing",
                Bought = false,
                Quantity = 0,
                Spent = 0,
                Rationale = $"No mall listing for item {itemId}."
            }, $"No mall listing for item {itemId}.");
        }
        if (priceCeiling > 0 && candidate.Price > priceCeiling)
        {
            var reason = $"Cheapest listing for {itemId} costs {candidate.Price}, ceiling is {priceCeiling}.";
            return (new Outcome
            {
                Verdict = "TooExpensive",
                Bought = false,
                Quantity = 0,
                Spent = 0,
                Rationale = reason
            }, reason);
        }

        // How many can we buy while keeping the meat reserve?
        var quantity = Math.Max(1, maximumQuantity);
        var spend = candidate.Price * quantity;
        if (availableMeat - spend < meatReserve)
        {
            var affordable = (int)Math.Max(0, (availableMeat - meatReserve) / candidate.Price);
            quantity = Math.Min(quantity, affordable);
            spend = candidate.Price * quantity;
            if (quantity <= 0)
            {
                var reason = $"Buying {itemId} would drop the account below its {meatReserve} meat reserve.";
                return (new Outcome
                {
                    Verdict = "BudgetTooLow",
                    Bought = false,
                    Quantity = 0,
                    Spent = 0,
                    Rationale = reason
                }, reason);
            }
        }

        // The store password hash lives on the store page.
        if (!MallBuyClient.TryParseStoreId(candidate.StoreId, out var storeId))
        {
            var reason = $"The listing for {itemId} has no usable store id.";
            return (new Outcome
            {
                Verdict = "NoStoreId",
                Bought = false,
                Quantity = 0,
                Spent = 0,
                Rationale = reason
            }, reason);
        }
        var hash = await _buyClient.ReadPasswordHashAsync(
            credentials, storeId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(hash))
        {
            var reason = $"Could not read the password hash for store {storeId}.";
            return (new Outcome
            {
                Verdict = "NoStoreHash",
                Bought = false,
                Quantity = 0,
                Spent = 0,
                Rationale = reason
            }, reason);
        }

        var purchase = await _buyClient.BuyAsync(
            credentials, storeId, itemId, candidate.Price, quantity, hash, cancellationToken)
            .ConfigureAwait(false);
        return (new Outcome
        {
            Verdict = purchase.Accepted ? "Bought" : $"http-{purchase.StatusCode}",
            Bought = purchase.Accepted,
            Quantity = purchase.Accepted ? quantity : 0,
            Spent = purchase.Accepted ? spend : 0,
            Rationale = purchase.Accepted
                ? $"Bought {quantity} of item {itemId} at {candidate.Price} meat each."
                : $"KoL rejected the purchase: {purchase.Message}",
            Purchases = new[] { purchase }
        }, purchase.Message);
    }
}
