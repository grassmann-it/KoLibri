using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KoLibri.Internal;
using CoreMarkt = KoLibri.Core.Markt;
using CoreSpiel = KoLibri.Core.Spiel;

namespace KoLibri
{
    public sealed class Mall
    {
        private readonly KolibriSession _session;

        internal Mall(KolibriSession session) => _session = session;

        public async Task<MallSearchResult> SearchAsync(string term, bool refresh = false, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(term);
            var page = await _session.ActAsync(() => _session.MallReader.SearchItemsAsync(term, refresh, cancellationToken)).ConfigureAwait(false);
            if (page.IsStale) throw new KolibriException("The mall could not be searched just now; only an older copy is available.");
            var items = page.Items
                .Select(group => ToOffers(group.ItemId, group.ItemName, group.Listings, page.FetchedAt))
                .OfType<MallOffers>()
                .ToArray();
            return new MallSearchResult(term, items, page.FetchedAt);
        }

        public async Task<MallOffers> GetOffersAsync(ItemRef item, bool refresh = false, CancellationToken cancellationToken = default)
        {
            var key = item.Id.ToString(CultureInfo.InvariantCulture);
            var snapshot = await _session.ActAsync(() => _session.MallReader.SearchAsync(key, refresh, cancellationToken)).ConfigureAwait(false);
            if (snapshot.IsStale) throw new KolibriException("The mall could not be read just now; only an older copy is available.");
            var name = snapshot.Listings.Select(l => l.ItemName).FirstOrDefault(n => n is not null)
                       ?? _session.Client.Data.Item(item)?.Name ?? key;
            return ToOffers(key, name, snapshot.Listings.Where(l => l.ItemId == key).ToArray(), snapshot.FetchedAt)
                   ?? new MallOffers(item, name, Array.Empty<MallListing>(), snapshot.FetchedAt);
        }

        /// <summary>Plant einen Kauf gegen frische Angebote; kein Request an einen Shop.</summary>
        public async Task<PurchaseQuote> QuoteAsync(PurchaseOrder order, CancellationToken cancellationToken = default)
        {
            Validate(order);
            var offers = await GetOffersAsync(order.Item, refresh: true, cancellationToken).ConfigureAwait(false);
            var status = await _session.Character.GetStatusAsync(true, cancellationToken).ConfigureAwait(false);
            return Plan(order, offers, status.Meat);
        }

#pragma warning disable RS0027 // Preserve the existing optional token. The gated overload requires all three arguments.
        /// <summary>
        /// Kauft laut Quote. Liest Preise neu, kauft nie über MaxUnitPrice und nie über den Preis der Quote (den der Guard
        /// freigegeben hat): ist ein Angebot teurer geworden, wird diese Zeile nicht gekauft (PriceChanged). Prüft danach Inventar und Meat.
        /// </summary>
        public Task<PurchaseResult> BuyAsync(PurchaseQuote quote, CancellationToken cancellationToken = default)
            => BuyApprovedAsync(quote, beforeEachSend: null, cancellationToken);
#pragma warning restore RS0027

        /// <summary>Prüft unmittelbar vor jedem Kauf-POST, ob er gesendet werden darf.</summary>
        public Task<PurchaseResult> BuyAsync(
            PurchaseQuote quote,
            Func<CancellationToken, ValueTask<GuardDecision>> beforeEachSend,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(beforeEachSend);
            return BuyApprovedAsync(quote, beforeEachSend, cancellationToken);
        }

        private Task<PurchaseResult> BuyApprovedAsync(PurchaseQuote quote,
            Func<CancellationToken, ValueTask<GuardDecision>>? beforeEachSend, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(quote);
            var (plan, quantity, total, maxUnitPrice) = Approved(quote);
            var action = new GuardedAction(_session.Account.Id, GuardedActionKind.MallBuy,
                $"buy {quantity} x item {quote.Order.Item.Id} at most {maxUnitPrice ?? 0} each")
            {
                Item = quote.Order.Item,
                Quantity = quantity,
                Meat = total,
                MaxUnitPrice = maxUnitPrice,
                Path = "mallstore.php"
            };
            return _session.GuardAsync(action, () => BuyCoreAsync(quote.Order, plan, total, beforeEachSend, cancellationToken),
                r => r.Status is PurchaseStatus.Bought or PurchaseStatus.Partial or PurchaseStatus.Unclear, cancellationToken);
        }

        /// <summary>Quote und Kauf in einem Schritt.</summary>
        public async Task<PurchaseResult> QuoteAndBuyAsync(PurchaseOrder order, CancellationToken cancellationToken = default)
        {
            var quote = await QuoteAsync(order, cancellationToken).ConfigureAwait(false);
            return await BuyAsync(quote, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Preisverlauf von pricegun.loathers.net (gecacht, gedrosselt). Null bei Fehler.</summary>
        public Task<PriceHistory?> GetPriceHistoryAsync(ItemRef item, CancellationToken cancellationToken = default)
            => _session.Client.GetPriceHistoryAsync(item, cancellationToken);

        internal static async Task<PriceHistory?> FetchPriceHistoryAsync(KolibriClient client, ItemRef item, CancellationToken cancellationToken)
        {
            try
            {
                var snapshot = await client.Pricegun.FetchAsync(item.Id.ToString(CultureInfo.InvariantCulture), false, cancellationToken)
                    .ConfigureAwait(false);
                if (snapshot.Sales.Count == 0 && snapshot.IsStale) return null;
                var points = snapshot.Sales.OrderBy(s => s.SoldAt).Select(s => new PricePoint(s.SoldAt, s.UnitPrice, s.Quantity)).ToArray();
                long volume = points.Sum(p => p.Volume);
                long? value = volume > 0 ? (long)Math.Round(points.Sum(p => (decimal)p.Price * p.Volume) / volume) : null;
                return new PriceHistory(item, value, volume, points, snapshot.FetchedAt);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or FormatException
                                              && !cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }

        // ------------------------------------------------------------------ internals

        private static void Validate(PurchaseOrder order)
        {
            ArgumentNullException.ThrowIfNull(order);
            if (order.Item.Id <= 0) throw new ArgumentOutOfRangeException(nameof(order), "Item id must be positive.");
            if (order.Quantity <= 0) throw new ArgumentOutOfRangeException(nameof(order), "Quantity must be positive.");
            if (order.MaxUnitPrice <= 0) throw new ArgumentOutOfRangeException(nameof(order), "MaxUnitPrice must be positive.");
            if (order.MeatReserve < 0) throw new ArgumentOutOfRangeException(nameof(order), "MeatReserve must not be negative.");
        }

        private static (IReadOnlyList<MallListing> Plan, int Quantity, long Total, long? MaxUnitPrice) Approved(PurchaseQuote quote)
        {
            var order = quote.Order;
            Validate(order);
            ArgumentNullException.ThrowIfNull(quote.Plan);
            var plan = quote.Plan.ToArray();
            var stores = new HashSet<int>();
            var excluded = order.ExcludedStoreIds.ToHashSet();
            int quantity = 0;
            long total = 0;
            long maxUnitPrice = 0;
            foreach (var line in plan)
            {
                if (line is null || line.StoreId <= 0 || line.Quantity <= 0 || line.UnitPrice <= 0
                    || line.UnitPrice > order.MaxUnitPrice || line.UnitPrice > CoreMarkt.MallBuyClient.MaxBindablePrice
                    || line.Item != order.Item || excluded.Contains(line.StoreId) || !stores.Add(line.StoreId))
                    throw new ArgumentException("The purchase plan contains an invalid listing.", nameof(quote));
                if (line.Quantity > order.Quantity - quantity)
                    throw new ArgumentException("The purchase plan exceeds the ordered quantity.", nameof(quote));
                quantity += line.Quantity;
                total = checked(total + (long)line.Quantity * line.UnitPrice);
                maxUnitPrice = Math.Max(maxUnitPrice, line.UnitPrice);
            }
            if (quote.TotalCost != total || quote.PlannedQuantity != quantity)
                throw new ArgumentException("The quote's total cost or quantity does not match its purchase plan.", nameof(quote));
            return (plan, quantity, total, plan.Length == 0 ? null : maxUnitPrice);
        }

        internal static PurchaseQuote Plan(PurchaseOrder order, MallOffers offers, long meat)
        {
            var excluded = order.ExcludedStoreIds.ToHashSet();
            var plan = new List<MallListing>();
            var remaining = order.Quantity;
            var budget = meat - order.MeatReserve;
            long total = 0;
            string? shortfall = null;
            var maxPrice = Math.Min(order.MaxUnitPrice, CoreMarkt.MallBuyClient.MaxBindablePrice);
            foreach (var listing in offers.Listings.Where(l => l.StoreId > 0 && l.Quantity > 0 && l.UnitPrice > 0 && l.Item == order.Item).OrderBy(l => l.UnitPrice))
            {
                if (remaining <= 0) break;
                if (listing.UnitPrice > maxPrice) break;
                if (!excluded.Add(listing.StoreId)) continue;
                var take = Math.Min(remaining, listing.Quantity);
                if (listing.LimitPerDay is > 0 and var limit) take = Math.Min(take, limit);
                var affordable = budget - total <= 0 ? 0 : (int)Math.Min(take, (budget - total) / listing.UnitPrice);
                if (affordable < take)
                {
                    take = affordable;
                    shortfall = $"The meat reserve of {order.MeatReserve} allows only part of the order.";
                }
                if (take <= 0) break;
                plan.Add(listing with { Quantity = take });
                total += take * listing.UnitPrice;
                remaining -= take;
            }
            if (remaining > 0)
                shortfall ??= $"Only {order.Quantity - remaining} of {order.Quantity} are offered at or below {maxPrice} Meat.";
            return new PurchaseQuote(order, plan, total, DateTimeOffset.UtcNow)
            {
                PlannedQuantity = order.Quantity - remaining,
                Shortfall = shortfall
            };
        }

        private async Task<PurchaseResult> BuyCoreAsync(PurchaseOrder order, IReadOnlyList<MallListing> plan, long approvedTotal,
            Func<CancellationToken, ValueTask<GuardDecision>>? beforeEachSend, CancellationToken cancellationToken)
        {
            var lines = new List<PurchaseLine>();
            if (plan.Count == 0)
                return new PurchaseResult(PurchaseStatus.NothingBought, 0, 0, lines);

            // Read again: prices and stock may have moved since the quote.
            var fresh = await GetOffersAsync(order.Item, refresh: true, cancellationToken).ConfigureAwait(false);
            var status = await _session.Character.GetStatusAsync(true, cancellationToken).ConfigureAwait(false);
            var inventoryBefore = await _session.Inventory.GetAsync(true, cancellationToken).ConfigureAwait(false);
            var hash = await _session.GetPasswordHashAsync(cancellationToken).ConfigureAwait(false);
            var buyer = new CoreMarkt.MallBuyClient(_session.Http, _session.Client.Options.BaseUri);

            var bought = 0;
            long spent = 0;
            var priceMoved = false;
            var unclear = false;
            var refused = false;
            var attemptedPost = false;
            foreach (var planned in plan)
            {
                if (bought >= order.Quantity) break;
                var current = fresh.Listings.FirstOrDefault(l => l.StoreId == planned.StoreId);
                if (current is null || current.Quantity <= 0 || current.UnitPrice <= 0)
                {
                    lines.Add(new PurchaseLine(planned.StoreId, planned.UnitPrice, planned.Quantity, 0, "The listing is gone."));
                    continue;
                }
                if (current.UnitPrice > order.MaxUnitPrice)
                {
                    priceMoved = true;
                    lines.Add(new PurchaseLine(planned.StoreId, current.UnitPrice, planned.Quantity, 0,
                        $"The price rose to {current.UnitPrice}, above the limit of {order.MaxUnitPrice}."));
                    continue;
                }
                if (current.UnitPrice > planned.UnitPrice)
                {
                    // The guard approved the quote's prices and total; a dearer line needs a new quote.
                    priceMoved = true;
                    lines.Add(new PurchaseLine(planned.StoreId, current.UnitPrice, planned.Quantity, 0,
                        $"The price rose from {planned.UnitPrice} to {current.UnitPrice} since the quote; nothing above the approved price is bought. Quote again."));
                    continue;
                }
                var quantity = Math.Min(Math.Min(planned.Quantity, current.Quantity), order.Quantity - bought);
                var cost = quantity * current.UnitPrice;
                if (cost > approvedTotal - spent)
                {
                    lines.Add(new PurchaseLine(planned.StoreId, current.UnitPrice, quantity, 0, "The approved total would be exceeded."));
                    refused = true;
                    break;
                }
                if (status.Meat - spent - cost < order.MeatReserve)
                {
                    lines.Add(new PurchaseLine(planned.StoreId, current.UnitPrice, quantity, 0, $"The meat reserve of {order.MeatReserve} would be broken."));
                    refused = true;
                    continue;
                }
                CoreMarkt.MallBuyResult result;
                try
                {
                    result = await SessionPipeline.WithMallSendGateAsync(
                        () => _session.ActAsync(() => buyer.BuyAsync(_session.Credentials, planned.StoreId, order.Item.Id,
                            current.UnitPrice, quantity, hash, cancellationToken)),
                        beforeEachSend, () => attemptedPost = true).ConfigureAwait(false);
                }
                catch (PreSendBlockedException exception)
                {
                    lines.Add(new PurchaseLine(planned.StoreId, current.UnitPrice, quantity, 0, exception.Message));
                    refused = true;
                    break;
                }
                if (result.Unverified)
                {
                    unclear = true;
                    lines.Add(new PurchaseLine(planned.StoreId, current.UnitPrice, quantity, 0, result.Message));
                    break; // never continue after a purchase whose outcome is unknown
                }
                var got = result.Accepted ? result.Quantity : 0;
                bought += got;
                spent += got * current.UnitPrice;
                lines.Add(new PurchaseLine(planned.StoreId, current.UnitPrice, quantity, got, result.Message));
            }

            var noPurchase = refused ? PurchaseStatus.Refused : priceMoved ? PurchaseStatus.PriceChanged : PurchaseStatus.NothingBought;
            if (!attemptedPost)
                return new PurchaseResult(noPurchase, 0, 0, lines);

            // Check afterwards: the inventory must show what the receipts claim.
            try
            {
                var inventoryAfter = await _session.Inventory.GetAsync(true, cancellationToken).ConfigureAwait(false);
                var delta = inventoryAfter.Count(order.Item) - inventoryBefore.Count(order.Item);
                if (delta != bought) unclear = true;
                if (delta > 0)
                    _session.RaiseItems(new[] { new InventoryItem(order.Item, fresh.Name, delta) }, ItemSource.Mall);
            }
            catch (KolibriException)
            {
                unclear |= bought > 0;
            }
            // Shop sales can offset the debit. Only a larger debit contradicts the receipts.
            try
            {
                var after = await _session.Character.GetStatusAsync(true, cancellationToken).ConfigureAwait(false);
                var meatDelta = status.Meat - after.Meat;
                unclear |= meatDelta > spent;
                spent = Math.Max(spent, meatDelta);
            }
            catch (KolibriException)
            {
                unclear |= bought > 0;
            }
            var statusOut = unclear ? PurchaseStatus.Unclear
                : bought >= order.Quantity ? PurchaseStatus.Bought
                : bought > 0 ? PurchaseStatus.Partial
                : noPurchase;
            return new PurchaseResult(statusOut, bought, spent, lines);
        }

        private static MallOffers? ToOffers(string itemId, string? name, IReadOnlyList<CoreMarkt.MallListing> listings, DateTimeOffset at)
        {
            if (!int.TryParse(itemId, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return null;
            var mapped = listings
                .Select(l => int.TryParse(l.StoreId, NumberStyles.None, CultureInfo.InvariantCulture, out var store)
                    ? new MallListing(store, l.StoreName ?? l.SellerName ?? store.ToString(CultureInfo.InvariantCulture), id, l.Price,
                        l.Quantity, l.PurchaseLimit is > 0 ? l.PurchaseLimit : null)
                    {
                        LimitKnown = l.LimitKind != CoreMarkt.MallLimitKind.Unknown || l.PurchaseLimit is > 0,
                        QuantityKnown = l.QuantityKnown
                    }
                    : null)
                .OfType<MallListing>()
                .OrderBy(l => l.UnitPrice)
                .ToArray();
            return new MallOffers(id, name ?? itemId, mapped, at);
        }
    }

    public sealed record MallListing(int StoreId, string StoreName, ItemRef Item, long UnitPrice, int Quantity, int? LimitPerDay)
    {
        /// <summary>False when KoL's page did not show whether the store has a daily limit (LimitPerDay is then null but not "unlimited").</summary>
        public bool LimitKnown { get; init; } = true;
        /// <summary>False when the stock could not be read from the page.</summary>
        public bool QuantityKnown { get; init; } = true;
    }
    public sealed record MallOffers(ItemRef Item, string Name, IReadOnlyList<MallListing> Listings, DateTimeOffset FetchedAt);
    public sealed record MallSearchResult(string Term, IReadOnlyList<MallOffers> Items, DateTimeOffset FetchedAt);

    public sealed record PurchaseOrder(ItemRef Item, int Quantity, long MaxUnitPrice)
    {
        /// <summary>Meat, das nach dem Kauf mindestens übrig bleiben muss.</summary>
        public long MeatReserve { get; init; }
        public IReadOnlyList<int> ExcludedStoreIds { get; init; } = Array.Empty<int>();
    }

    public sealed record PurchaseQuote(PurchaseOrder Order, IReadOnlyList<MallListing> Plan, long TotalCost, DateTimeOffset QuotedAt)
    {
        public int PlannedQuantity { get; init; }
        public string? Shortfall { get; init; }
    }

    public enum PurchaseStatus { Bought, Partial, NothingBought, PriceChanged, Refused, Unclear }

    public sealed record PurchaseResult(PurchaseStatus Status, int QuantityBought, long MeatSpent, IReadOnlyList<PurchaseLine> Lines);
    public sealed record PurchaseLine(int StoreId, long UnitPrice, int Requested, int Bought, string? Message);

    public sealed record PriceHistory(ItemRef Item, long? Value, long? Volume, IReadOnlyList<PricePoint> Points, DateTimeOffset FetchedAt);
    public sealed record PricePoint(DateTimeOffset At, long Price, long Volume);

    public sealed class Shop
    {
        private readonly KolibriSession _session;

        internal Shop(KolibriSession session) => _session = session;

        public async Task<IReadOnlyList<ShopListing>> GetListingsAsync(bool refresh = false, CancellationToken cancellationToken = default)
        {
            var listings = await _session.ActAsync(() => _session.MallReader.FetchOwnShopAsync(refresh, cancellationToken)).ConfigureAwait(false);
            return listings
                .Select(l => int.TryParse(l.ItemId, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                    ? new ShopListing(id, l.DisplayName ?? _session.Client.Data.Item(id)?.Name ?? l.ItemId, l.Quantity, l.Price, l.PurchaseLimit ?? 0)
                    : null)
                .OfType<ShopListing>()
                .ToArray();
        }

        public Task<ActionResult> AddAsync(ItemRef item, int quantity, long price, int limitPerDay = 0, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(price);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(price, int.MaxValue);
            ArgumentOutOfRangeException.ThrowIfNegative(limitPerDay);
            var action = new GuardedAction(_session.Account.Id, GuardedActionKind.ShopChange,
                FormattableString.Invariant($"list {quantity} x item {item.Id} at {price}, limit {limitPerDay}"))
            {
                Item = item,
                Quantity = quantity,
                Path = "managestore.php"
            };
            return _session.GuardAsync(action, async () =>
            {
                var outcome = await _session.ActAsync(() => CoreMarkt.ShopStocker.AddAsync(
                    new CoreSpiel.StoreLister(_session.Http, _session.Client.Options.BaseUri), _session.Credentials,
                    ct => _session.MallReader.FetchOwnShopAsync(true, ct), item.Id, quantity, (int)price, limitPerDay, cancellationToken))
                    .ConfigureAwait(false);
                var status = outcome.Confirmed ? ActionStatus.Done : outcome.Unclear ? ActionStatus.Unclear : ActionStatus.Rejected;
                return new ActionResult(status, outcome.Confirmed ? quantity : 0,
                    status == ActionStatus.Done ? null : outcome.Problem ?? outcome.StoreMessage ?? "KoL did not confirm the listing.", null);
            }, r => r.Status is ActionStatus.Done or ActionStatus.Unclear, cancellationToken);
        }

        public Task<ActionResult> RepriceAsync(IReadOnlyList<ShopPriceChange> changes, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(changes);
            changes = changes.ToArray();
            if (changes.Count == 0) throw new ArgumentException("No price changes given.", nameof(changes));
            var sorted = changes.OrderBy(c => c.Item.Id).Select(c => (Change: c, Price: FormattableString.Invariant(
                $"{c.Item.Id}@{c.ExpectedPrice?.ToString(CultureInfo.InvariantCulture) ?? "*"}->{c.Price}"))).ToArray();
            var summary = string.Join(", ", sorted.Select(c => c.Price));
            var action = new GuardedAction(_session.Account.Id, GuardedActionKind.ShopChange, $"reprice: {summary}")
            {
                Item = changes.Count == 1 ? changes[0].Item : null,
                Quantity = changes.Count,
                Path = "backoffice.php",
                Binding = string.Join(", ", sorted.Select(c =>
                    $"{c.Price};limit={c.Change.LimitPerDay?.ToString(CultureInfo.InvariantCulture) ?? "*"}"))
            };
            return _session.GuardAsync(action, async () =>
            {
                var current = await GetListingsAsync(refresh: true, cancellationToken).ConfigureAwait(false);
                var updates = new List<CoreMarkt.ShopPriceUpdate>();
                foreach (var change in changes)
                {
                    var listing = current.FirstOrDefault(l => l.Item == change.Item);
                    if (listing is null)
                        return new ActionResult(ActionStatus.Rejected, 0, $"Item {change.Item.Id} is not in the store. Nothing was changed.", null);
                    if (change.ExpectedPrice is { } expected && expected != listing.Price)
                        return new ActionResult(ActionStatus.Rejected, 0,
                            $"{change.Item.Id}: Conflict (the shop shows {listing.Price}, expected {expected}). Nothing was changed.", null);
                    if (change.LimitPerDay is { } limit && limit != listing.LimitPerDay)
                        return new ActionResult(ActionStatus.Rejected, 0, "Changing the daily limit is not supported yet. Nothing was changed.", null);
                    // The caller's expected price, when given, is what the core checks against its own fresh read:
                    // a price that moved in between is a Conflict and nothing is written.
                    updates.Add(new CoreMarkt.ShopPriceUpdate(change.Item.Id.ToString(CultureInfo.InvariantCulture), change.ExpectedPrice ?? listing.Price, change.Price,
                        listing.LimitPerDay > 0 ? listing.LimitPerDay : null));
                }
                var result = await _session.ActAsync(() => new CoreMarkt.KoLShopMutationClient(_session.Http, _session.Client.Options.BaseUri)
                    .ApplyPriceUpdatesAsync(updates, commit: true, cancellationToken)).ConfigureAwait(false);
                var done = result.Results.Count(r => r.Status == CoreMarkt.ShopPriceUpdateStatus.Updated);
                var ok = done + result.Results.Count(r => r.Status == CoreMarkt.ShopPriceUpdateStatus.Hold);
                var failed = result.Results.Where(r => r.Status is not (CoreMarkt.ShopPriceUpdateStatus.Updated or CoreMarkt.ShopPriceUpdateStatus.Hold))
                    .Select(r => $"{r.ItemId}: {r.Status}{(r.Message is null ? "" : " (" + r.Message + ")")}").ToArray();
                var status = ok == changes.Count && result.Succeeded ? ActionStatus.Done
                    : result.Results.Any(r => r.Status == CoreMarkt.ShopPriceUpdateStatus.VerificationFailed) ? ActionStatus.Unclear
                    : done > 0 ? ActionStatus.Partial : ActionStatus.Rejected;
                return new ActionResult(status, done, status == ActionStatus.Done ? null : string.Join("; ", failed), null);
            }, r => r.Status is ActionStatus.Done or ActionStatus.Partial or ActionStatus.Unclear, cancellationToken);
        }

        /// <summary>Nicht in KoLibri 0.5: der Core hat dafür noch keinen geprüften Request (siehe docs/lib-api-0.5.md §11).</summary>
        public Task<ActionResult> TakeBackAsync(ItemRef item, int quantity, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Taking items back from the store is not implemented in KoLibri 0.5; nothing was sent.");

        public async Task<IReadOnlyList<ShopSale>> GetSalesAsync(bool refresh = false, CancellationToken cancellationToken = default)
        {
            var snapshot = await _session.ActAsync(() => new CoreMarkt.KoLShopSalesReadClient(_session.Http, _session.Client.Options.BaseUri)
                .FetchSalesAsync(refresh, cancellationToken)).ConfigureAwait(false);
            if (snapshot.IsStale) throw new KolibriException("The sales log could not be read just now.");
            return snapshot.Sales
                .Select(s => new ShopSale(s.SoldAt ?? s.ObservedAt, s.RawBuyerName ?? s.BuyerKey,
                    _session.Client.Data.FindItem(s.ItemName)?.Item ?? new ItemRef(0), s.Quantity, s.TotalPrice) { ItemName = s.ItemName })
                .ToArray();
        }
    }

    public sealed record ShopListing(ItemRef Item, string Name, int Quantity, long Price, int LimitPerDay);
    public sealed record ShopPriceChange(ItemRef Item, long Price, int? LimitPerDay = null)
    {
        /// <summary>Preis, den der Aufrufer im Shop erwartet. Weicht der Live-Preis ab, ändert KoLibri nichts (Conflict). Null: der aktuelle Preis gilt.</summary>
        public long? ExpectedPrice { get; init; }
    }
    public sealed record ShopSale(DateTimeOffset At, string Buyer, ItemRef Item, int Quantity, long Meat)
    {
        /// <summary>Item name as the sales log shows it (the id is looked up from it and may be 0).</summary>
        public string ItemName { get; init; } = string.Empty;
    }

    public sealed class PirateKeys
    {
        private readonly KolibriSession _session;

        internal PirateKeys(KolibriSession session) => _session = session;

        /// <summary>Parts = complete part sets (one of each of the four parts), Keys = complete keys.</summary>
        public async Task<PirateKeyCounts> GetCountsAsync(CancellationToken cancellationToken = default)
        {
            var inventory = await _session.ReadAsync(r => r.FetchInventoryAsync(true, cancellationToken), cancellationToken).ConfigureAwait(false);
            return Counts(CoreSpiel.PirateKeyClient.CountKeys(inventory));
        }

        public Task<PirateKeyResult> AssembleAsync(int count, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
            var action = new GuardedAction(_session.Account.Id, GuardedActionKind.Use, $"assemble {count} pirate key(s)")
            {
                Item = CoreSpiel.PirateKeyClient.Alpha,
                Quantity = count
            };
            return _session.GuardAsync(action, async () =>
            {
                var client = await OpenAsync(cancellationToken).ConfigureAwait(false);
                return ToResult(await _session.ActAsync(() => client.AssembleAsync(count, cancellationToken)).ConfigureAwait(false));
            }, r => r.Done > 0 || r.Unclear, cancellationToken);
        }

        public async Task<IReadOnlyList<ChoiceOption>> GetRareOptionsAsync(CancellationToken cancellationToken = default)
        {
            var client = await OpenAsync(cancellationToken).ConfigureAwait(false);
            var form = await _session.ActAsync(() => client.GetRareOptionsAsync(cancellationToken)).ConfigureAwait(false);
            return form.Options.Select(o => new ChoiceOption(o.Value, o.Label)).ToArray();
        }

        public Task<PirateKeyResult> RedeemAsync(ItemRef rare, int count, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
            var action = new GuardedAction(_session.Account.Id, GuardedActionKind.Use, $"redeem {count} pirate key(s) for item {rare.Id}")
            {
                Item = rare,
                Quantity = count
            };
            return _session.GuardAsync(action, async () =>
            {
                var client = await OpenAsync(cancellationToken).ConfigureAwait(false);
                return ToResult(await _session.ActAsync(() => client.RedeemAsync(rare.Id, count, cancellationToken)).ConfigureAwait(false));
            }, r => r.Done > 0 || r.Unclear, cancellationToken);
        }

        private async Task<CoreSpiel.PirateKeyClient> OpenAsync(CancellationToken cancellationToken)
            => new(_session.Http, await _session.GetPasswordHashAsync(cancellationToken).ConfigureAwait(false), _session.Client.Policy);

        /// <summary>The rare items a complete key can become (offline list, id and name).</summary>
        public static IReadOnlyList<ChoiceOption> RareOptions { get; } =
            CoreSpiel.PirateKeyClient.RareOptions.Select(o => new ChoiceOption(o.Value, o.Label)).ToArray();

        private static PirateKeyCounts Counts(CoreSpiel.PirateKeyCounts counts) => new(counts.AssemblableSets, counts.Complete)
        {
            Alpha = counts.Alpha,
            Bravo = counts.Bravo,
            Charlie = counts.Charlie,
            Delta = counts.Delta
        };

        private static PirateKeyResult ToResult(CoreSpiel.PirateKeyRunResult run)
            => new(run.Requested, run.Confirmed, Counts(run.Before), Counts(run.After ?? run.Before),
                run.Unclear ? $"Unclear: {run.Problem ?? "a request was sent but its effect could not be read back."}" : run.Problem)
            {
                Unclear = run.Unclear,
                Steps = run.Steps.Select(st => new PirateKeyStep(st.Number, st.Confirmed, st.Message, st.InventoryDelta)).ToArray()
            };
    }

    public sealed record PirateKeyCounts(int Parts, int Keys)
    {
        /// <summary>Single parts in the inventory (added in 0.5 step 3 for the CLI's `keys status`).</summary>
        public int Alpha { get; init; }
        public int Bravo { get; init; }
        public int Charlie { get; init; }
        public int Delta { get; init; }
    }

    public sealed record PirateKeyResult(int Requested, int Done, PirateKeyCounts Before, PirateKeyCounts After, string? Message)
    {
        /// <summary>A request went out but its effect could not be read back: check the account before repeating.</summary>
        public bool Unclear { get; init; }
        public IReadOnlyList<PirateKeyStep> Steps { get; init; } = Array.Empty<PirateKeyStep>();
    }

    /// <summary>One sent request and the inventory change it caused (item id → delta).</summary>
    public sealed record PirateKeyStep(int Number, bool Confirmed, string? Message, IReadOnlyDictionary<int, int> InventoryDelta);

    public enum MailFolder { Inbox, Saved, Outbox, Pvp }

    public sealed class Mailbox
    {
        private readonly KolibriSession _session;

        internal Mailbox(KolibriSession session) => _session = session;

        public async Task<MailPage> GetAsync(MailFolder folder = MailFolder.Inbox, int page = 1, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
            var core = CoreFolder(folder);
            var snapshot = await _session.ReadAsync(r => r.FetchMailboxPageAsync(core, page, cancellationToken), cancellationToken).ConfigureAwait(false);
            var entries = snapshot.Entries.Select(e => new MailEntry(e.Id, e.Recipient is null ? e.Sender : e.Recipient,
                DateTimeOffset.TryParse(e.Date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null,
                e.Text, Array.Empty<InventoryItem>(), e.AttachedMeat)).ToArray();
            return new MailPage(folder, snapshot.Page, snapshot.HasNextPage ? snapshot.Page + 1 : snapshot.Page, entries);
        }

        public Task<MailSendResult> SendAsync(MailMessage message, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(message);
            ArgumentException.ThrowIfNullOrWhiteSpace(message.Recipient);
            ArgumentOutOfRangeException.ThrowIfNegative(message.Meat);
            var recipient = message.Recipient.Trim();
            var attachments = message.Items.OrderBy(i => i.Item.Id)
                .Select(i => new CoreSpiel.KoLMailAttachment(i.Item.Id, i.Quantity)).ToArray();
            var items = attachments.Length == 0 ? "none"
                : string.Join(",", attachments.Select(i => FormattableString.Invariant($"{i.ItemId}x{i.Quantity}")));
            var summary = FormattableString.Invariant($"kmail to {recipient.ToLowerInvariant()}: items {items}; meat {message.Meat}");
            var text = message.Text ?? string.Empty;
            var textHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            var action = new GuardedAction(_session.Account.Id, GuardedActionKind.SendMail, summary)
            {
                Quantity = attachments.Sum(i => i.Quantity),
                Meat = message.Meat,
                Path = "sendmessage.php",
                Binding = $"{textHash}:{summary}"
            };
            return _session.GuardAsync(action, async () =>
            {
                var hash = await _session.GetPasswordHashAsync(cancellationToken).ConfigureAwait(false);
                var outcome = await _session.ActAsync(() => CoreSpiel.KoLMailbox.SendWithOutcomeAsync(
                    _session.Http, hash, recipient, text, attachments, message.Meat, _session.Client.Policy, cancellationToken)).ConfigureAwait(false);
                return outcome switch
                {
                    CoreSpiel.KoLMailSendOutcome.Sent => new MailSendResult(MailSendStatus.Sent, null),
                    CoreSpiel.KoLMailSendOutcome.NotSent => new MailSendResult(MailSendStatus.Refused, "KoL refused the message."),
                    _ => new MailSendResult(MailSendStatus.Unclear, "The answer was lost; the message may have been sent.")
                };
            }, r => r.Status is MailSendStatus.Sent or MailSendStatus.Unclear, cancellationToken);
        }

        internal static bool MayHaveDispatched(OperationCanceledException exception)
            => exception is CoreSpiel.KoLMailCanceledAfterSendException;

        public Task SaveAsync(string messageId, CancellationToken cancellationToken = default)
            => ChangeAsync(GuardedActionKind.Other, $"save kmail {messageId}", hash =>
                _session.Reader.SaveInboxMessageAsync(messageId, hash, cancellationToken), cancellationToken);

        public Task DeleteAsync(MailFolder folder, string messageId, CancellationToken cancellationToken = default)
        {
            var core = CoreFolder(folder);
            return ChangeAsync(GuardedActionKind.Other, $"delete kmail {messageId}", hash =>
                _session.Reader.DeleteMailboxMessageAsync(core, messageId, hash, cancellationToken), cancellationToken,
                binding: $"{core}:{messageId}");
        }

        private Task ChangeAsync(GuardedActionKind kind, string summary, Func<string, Task> change, CancellationToken cancellationToken,
            string? binding = null)
        {
            var action = new GuardedAction(_session.Account.Id, kind, summary) { Path = "messages.php", Binding = binding };
            return _session.GuardAsync(action, async () =>
            {
                var hash = await _session.GetPasswordHashAsync(cancellationToken).ConfigureAwait(false);
                await _session.ActAsync(async () => { await change(hash).ConfigureAwait(false); return true; }).ConfigureAwait(false);
                return true;
            }, ok => ok, cancellationToken);
        }

        private static CoreSpiel.KoLMailFolder CoreFolder(MailFolder folder) => folder switch
        {
            MailFolder.Inbox => CoreSpiel.KoLMailFolder.Inbox,
            MailFolder.Saved => CoreSpiel.KoLMailFolder.Saved,
            MailFolder.Outbox => CoreSpiel.KoLMailFolder.Outbox,
            _ => throw new NotSupportedException($"The {folder} folder is not supported in KoLibri 0.5.")
        };
    }

    public sealed record MailMessage(string Recipient, string Text)
    {
        public IReadOnlyList<InventoryItem> Items { get; init; } = Array.Empty<InventoryItem>();
        public long Meat { get; init; }
    }
    public enum MailSendStatus { Sent, Refused, Unclear }
    public sealed record MailSendResult(MailSendStatus Status, string? Message);
    public sealed record MailEntry(string Id, string From, DateTimeOffset? At, string Text, IReadOnlyList<InventoryItem> Items, long Meat);
    public sealed record MailPage(MailFolder Folder, int Page, int PageCount, IReadOnlyList<MailEntry> Entries);
}
