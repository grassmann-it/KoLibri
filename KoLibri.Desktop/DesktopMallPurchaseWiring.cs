using System.Globalization;

namespace KoLibri.Desktop;

/// <summary>
/// Meat spent through the desktop mall purchase per account and KoL day, kept in a small JSON file next to
/// the settings (never credentials). It shares the cross-process, fail-closed transaction rules of
/// <see cref="FileSpendLedger"/>: a purchase is reserved (limit checked, reservation saved) before the POST,
/// settled once meat and inventory are verified, released when nothing was sent, and stays counted
/// ("unclear") until <see cref="FileSpendLedger.Reconcile"/> when the outcome is not proven.
/// Without a path the ledger is in memory only.
/// </summary>
public sealed class MallSpendLedger(TimeProvider? clock = null, string? path = null) : FileSpendLedger(path, clock)
{
    /// <summary>The ledger file that sits next to the given settings file.</summary>
    public static string PathNextTo(DesktopSettingsStore store)
        => System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(store.Path))!, "mall-spend-ledger.json");
}

/// <summary>The three reads a purchase needs; the live implementation goes through <see cref="DesktopLiveDataProvider"/>.</summary>
public interface IMallPurchaseReads
{
    Task<MallSearchPage> SearchAsync(string term, CancellationToken cancellationToken);
    Task<KoLStatusSnapshot?> StatusAsync(CancellationToken cancellationToken);
    Task<DesktopInventorySnapshot> InventoryAsync(CancellationToken cancellationToken);
}

public sealed class LiveMallPurchaseReads(DesktopLiveDataProvider provider, AccountRole role) : IMallPurchaseReads
{
    public Task<MallSearchPage> SearchAsync(string term, CancellationToken ct)
        => provider.SearchMallItemsAsync(role, term, forceRefresh: true, ct);
    public Task<KoLStatusSnapshot?> StatusAsync(CancellationToken ct)
        => provider.LoadStatusAsync(role, forceRefresh: true, ct);
    public Task<DesktopInventorySnapshot> InventoryAsync(CancellationToken ct)
        => provider.LoadInventoryAsync(role, forceRefresh: true, null, ct);
}

/// <summary>
/// Builds the fresh observation (offer plus account) the purchase gateway checks before and
/// after a purchase. Always forces fresh reads. The daily budget is the configured meat per day
/// minus what this window already spent.
/// </summary>
public sealed class MallPurchaseObserver
{
    private readonly IMallPurchaseReads _reads;
    private readonly string _account;
    private readonly Func<long> _dailyLimit;
    private readonly MallSpendLedger _ledger;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, string> _names = new();

    public MallPurchaseObserver(IMallPurchaseReads reads, string account, Func<long> dailyLimit,
        MallSpendLedger ledger, TimeProvider? clock = null)
    {
        _reads = reads;
        _account = account;
        _dailyLimit = dailyLimit;
        _ledger = ledger;
        _clock = clock ?? TimeProvider.System;
    }

    private MallPurchaseAccountState? _cachedAccount;
    private long _spentSinceCache;

    /// <summary>
    /// Reads status and inventory once (forced fresh) and keeps them as the account state for the following
    /// before-reads of a queued batch, so each queued buy only needs the Mall search (one request).
    /// </summary>
    public async Task<MallAccountReading> ReadAccountAsync(CancellationToken ct)
    {
        var status = await _reads.StatusAsync(ct).ConfigureAwait(false);
        var inventory = await _reads.InventoryAsync(ct).ConfigureAwait(false);
        var items = inventory.Items.GroupBy(i => i.ItemId)
            .ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity), StringComparer.Ordinal);
        var fresh = status is not null && inventory.HasSession && !status.IsStale && !inventory.IsStale;
        var meat = status?.Meat ?? 0;
        var now = _clock.GetUtcNow();
        lock (_names)
        {
            _spentSinceCache = 0;
            _cachedAccount = fresh
                ? new MallPurchaseAccountState(_account, meat, 0, _dailyLimit(), items, now, sessionValid: true, isStale: false)
                : null;
        }
        return new MallAccountReading(meat, items, fresh);
    }

    /// <summary>A queued buy was sent: later cached before-reads must show less Meat.</summary>
    public void NoteSent(long meat) { lock (_names) _spentSinceCache += meat; }

    public void ClearAccountCache() { lock (_names) _cachedAccount = null; }

    /// <summary>The mall search works on names; the purchase only knows the item id.</summary>
    public void Remember(string itemId, string itemName) => _names[itemId] = itemName;

    public Task<MallPurchaseObservation> BeforeAsync(MallPurchaseRequest request, CancellationToken ct)
        => ReadAsync(request, null, ct);

    public Task<MallPurchaseObservation> AfterAsync(MallPurchaseTransportCommit commit, CancellationToken ct)
        => ReadAsync(commit.Preview.Request, commit.Before, ct);

    private async Task<MallPurchaseObservation> ReadAsync(
        MallPurchaseRequest request, MallPurchaseObservation? before, CancellationToken ct)
    {
        if (!_names.TryGetValue(request.ItemId, out var name))
            throw new InvalidOperationException("The item name is unknown; search for the item again.");
        // After the POST the offer is not needed (inventory is the proof): skip the Mall search, saving a request.
        var page = before is null ? await _reads.SearchAsync($"\"{name}\"", ct).ConfigureAwait(false) : null;
        MallPurchaseAccountState? cached;
        long spentSince;
        lock (_names) { cached = _cachedAccount; spentSince = _spentSinceCache; }
        var status = cached is null ? await _reads.StatusAsync(ct).ConfigureAwait(false) : null;
        var inventory = cached is null ? await _reads.InventoryAsync(ct).ConfigureAwait(false) : null;
        var now = _clock.GetUtcNow();
        // A cached page must never be re-dated as fresh: before the POST it blocks the purchase.
        if (page is not null && before is null && page.IsStale)
            throw new InvalidOperationException("The mall offer is stale; search again for a fresh offer.");
        var observedAt = page is null ? now : page.FetchedAt < now ? page.FetchedAt : now;

        var listing = page?.Items.FirstOrDefault(i => i.ItemId == request.ItemId)?.Listings
            .FirstOrDefault(l => l.StoreId == request.StoreId);
        var offer = page is null ? before!.Offer : listing is null
            ? new MallPurchaseOffer(request.SellerId, request.StoreId, request.ItemId,
                request.ExpectedUnitPrice, 0, observedAt)
            : new MallPurchaseOffer(listing.SellerId ?? request.SellerId, listing.StoreId, listing.ItemId,
                listing.Price, listing.QuantityKnown ? listing.Quantity : 0, observedAt,
                listing.LimitKind == MallLimitKind.Limited ? listing.PurchaseLimit : null,
                listing.SellerName, listing.StoreName);

        MallPurchaseAccountState account;
        if (cached is not null)
        {
            // Queued batch: account state from the batch's single read, Meat reduced by what was sent since.
            account = new MallPurchaseAccountState(_account, Math.Max(0, cached.Meat - spentSince),
                _ledger.SpentToday(_account), _dailyLimit(), cached.Inventory, now, sessionValid: true,
                isStale: page?.IsStale ?? false);
        }
        else
        {
            var meat = status?.Meat ?? 0;
            var spent = before is null
                ? _ledger.SpentToday(_account)
                : before.Account.DailySpent + Math.Max(0, before.Account.Meat - meat);
            var items = inventory!.Items.GroupBy(i => i.ItemId)
                .ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity), StringComparer.Ordinal);
            account = new MallPurchaseAccountState(_account, meat, spent, _dailyLimit(), items, now,
                sessionValid: status is not null && inventory.HasSession,
                isStale: (status?.IsStale ?? true) || inventory.IsStale || (page?.IsStale ?? false));
        }
        return new MallPurchaseObservation(offer, account, observedAt);
    }
}

/// <summary>Maps the settings numbers onto the purchase budget.</summary>
public static class DesktopMallPurchaseSettings
{
    public static DesktopMallPurchaseOptions ToOptions(DesktopSettings settings) => new()
    {
        MaxDailySpend = (long)settings.Get("meatPerDay"),
        MaxMeatPerPurchase = (long)settings.Get("meatPerPurchase"),
        MaxQuantityPerPurchase = (int)settings.Get("unitsPerPurchase")
    };
}

/// <summary>The user's decision about unclear purchase meat.</summary>
public enum MallUnclearDecision { Cancel, WasSpent, NotSpent }

/// <summary>Dialog seam for reconciling unclear meat: shows the amount and fresh evidence, returns the explicit decision.</summary>
public interface IMallReconcilePrompt
{
    Task<MallUnclearDecision> AskAsync(string account, long amount, string evidence, CancellationToken cancellationToken);
}

/// <summary>Fresh Meat and inventory size as text, so the user can judge an unclear purchase. Never throws for read problems.</summary>
public static class MallUnclearEvidence
{
    public static async Task<string> ReadAsync(IMallPurchaseReads reads, CancellationToken ct)
    {
        try
        {
            var status = await reads.StatusAsync(ct).ConfigureAwait(false);
            var inventory = await reads.InventoryAsync(ct).ConfigureAwait(false);
            var meat = status is null ? "Meat unknown" : string.Create(CultureInfo.InvariantCulture, $"Meat now {status.Meat:N0}");
            var items = inventory.IsStale || !inventory.HasSession
                ? "inventory could not be read fresh"
                : string.Create(CultureInfo.InvariantCulture, $"{inventory.Items.Sum(i => (long)i.Quantity):N0} items in inventory");
            return $"{meat}; {items}.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return "Fresh Meat and inventory could not be read: " + MallPurchaseMessages.Describe(exception);
        }
    }
}

/// <summary>The dialog seam: one compact confirmation; returns the quantity to buy, or null when cancelled.</summary>
public interface IMallPurchasePrompt
{
    Task<int?> AskAsync(MallPurchasePromptContext context, CancellationToken cancellationToken);
}

/// <param name="RemainingDailyBudget">Meat left of today's budget (shown only; the ledger decides when buying). Negative = unknown.</param>
public sealed record MallPurchasePromptContext(
    string Account, MallOfferRow Offer, int MaxQuantity, long RemainingDailyBudget = -1);

/// <summary>
/// Connects the Buy button of the search view to the purchase service: build a preview for the
/// offer, ask the user, then send at most one confirmed purchase. Every step is written to the log.
/// </summary>
public sealed class MallPurchaseCoordinator : IMallPurchaseRequest
{
    private readonly Func<CancellationToken, Task<(DesktopMallPurchaseService Service, MallPurchaseObserver Observer)?>> _open;
    private readonly string _account;
    private readonly IMallPurchasePrompt _prompt;
    private readonly SessionLogViewModel _log;
    private readonly MallSpendLedger _ledger;
    private readonly Action<string>? _report;
    private readonly IMallReconcilePrompt? _reconcilePrompt;
    private readonly Func<CancellationToken, Task<string>>? _evidence;
    private int _busy;

    public MallPurchaseCoordinator(
        string account,
        Func<CancellationToken, Task<(DesktopMallPurchaseService, MallPurchaseObserver)?>> open,
        IMallPurchasePrompt prompt,
        SessionLogViewModel log,
        MallSpendLedger ledger,
        Action<string>? report = null,
        IMallReconcilePrompt? reconcilePrompt = null,
        Func<CancellationToken, Task<string>>? evidence = null,
        Action<Action>? ui = null,
        MallBuyQueue? queue = null)
    {
        Queue = queue ?? new MallBuyQueue(account, ledger, log, ui);
        _reconcilePrompt = reconcilePrompt;
        _evidence = evidence;
        _account = account;
        _open = open;
        _prompt = prompt;
        _log = log;
        _ledger = ledger;
        _report = report;
    }

    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    /// <summary>Meat of today still counted as unclear for this account (0 when none or unreadable).</summary>
    public long UnclearAmount => Math.Max(0, _ledger.UnclearAmount(_account));

    /// <summary>
    /// Resolves unclear meat by explicit user action: shows the amount and fresh Meat/inventory, then either
    /// settles it (it was bought) or releases it (it was not). Cancel changes nothing. Never silent.
    /// Returns the decision (Cancel when nothing was changed).
    /// </summary>
    public async Task<MallUnclearDecision> ReconcileUnclearAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return MallUnclearDecision.Cancel;
        try
        {
            var amount = _ledger.UnclearAmount(_account);
            if (amount < 0)
            {
                Say(SessionLogKind.Error, $"Reconcile: the spend ledger cannot be read ({_ledger.Problem}). Nothing was changed.");
                return MallUnclearDecision.Cancel;
            }
            if (amount == 0)
            {
                Say(SessionLogKind.Purchase, "Reconcile: no unclear purchases for today.");
                return MallUnclearDecision.Cancel;
            }
            if (_reconcilePrompt is null) return MallUnclearDecision.Cancel;
            var evidence = _evidence is null ? "" : await _evidence(cancellationToken).ConfigureAwait(true);
            var decision = await _reconcilePrompt.AskAsync(_account, amount, evidence, cancellationToken).ConfigureAwait(true);
            if (decision == MallUnclearDecision.Cancel)
            {
                Say(SessionLogKind.Purchase, string.Create(CultureInfo.InvariantCulture,
                    $"Reconcile cancelled: {amount:N0} Meat stays counted as unclear."));
                return decision;
            }
            var spent = decision == MallUnclearDecision.WasSpent;
            if (!_ledger.ResolveUnclear(_account, amount, spent))
            {
                Say(SessionLogKind.Error, "Reconcile: the unclear amount changed meanwhile. Nothing was changed; try again.");
                return MallUnclearDecision.Cancel;
            }
            var shown = amount.ToString("N0", CultureInfo.InvariantCulture);
            Say(SessionLogKind.Purchase, spent
                ? $"Reconciled: {shown} Meat counted as spent."
                : $"Reconciled: {shown} Meat released (not bought).");
            return decision;
        }
        catch (OperationCanceledException)
        {
            return MallUnclearDecision.Cancel;
        }
        catch (Exception exception)
        {
            Say(SessionLogKind.Error, $"Reconcile failed: {MallPurchaseMessages.Describe(exception)}");
            return MallUnclearDecision.Cancel;
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    /// <summary>The background queue whose tickets the Mall tab lists.</summary>
    public MallBuyQueue Queue { get; }

    public bool CanRequest(MallOfferRow offer)
        => !IsBusy && !offer.IsStale && offer.UnitPrice > 0 && offer.Stock != 0 && long.TryParse(offer.ShopId, out _);

    private (DesktopMallPurchaseService Service, MallPurchaseObserver Observer, DateTimeOffset At)? _session;

    private async Task<(DesktopMallPurchaseService Service, MallPurchaseObserver Observer)?> SessionAsync(CancellationToken ct)
    {
        // The signed-in session (and its password hash) is reused for a few minutes: opening it costs a request.
        if (_session is { } cached && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(5))
            return (cached.Service, cached.Observer);
        var opened = await _open(ct).ConfigureAwait(true);
        if (opened is not { } fresh) { _session = null; return null; }
        _session = (fresh.Service, fresh.Observer, DateTimeOffset.UtcNow);
        return (fresh.Service, fresh.Observer);
    }

    /// <summary>
    /// One click: the compact confirmation, then the buy is queued and this returns at once. Several buys can be
    /// queued; they are sent in the background and each shows its own status.
    /// </summary>
    public Task RequestAsync(MallOfferRow offer, CancellationToken cancellationToken)
        => RequestAsync(offer, cancellationToken, null);

    /// <summary>With a <paramref name="quantity"/> the confirmation window is skipped (a quick 'Buy 1').</summary>
    public async Task RequestAsync(MallOfferRow offer, CancellationToken cancellationToken, int? quantity)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try
        {
            if (offer.IsStale)
            {
                Say(SessionLogKind.Error, $"Cannot buy {offer.ItemName}: the offer is stale. Search again for a fresh offer.");
                return;
            }
            var what = string.Create(CultureInfo.InvariantCulture,
                $"{offer.ItemName} from {offer.ShopName} at {offer.UnitPrice:N0}");
            if (await SessionAsync(cancellationToken).ConfigureAwait(true) is not { } session)
            {
                Say(SessionLogKind.Error, $"Cannot buy {what}: {_account} is not signed in.");
                return;
            }
            session.Observer.Remember(offer.ItemId, offer.ItemName);
            var options = session.Service.Options;
            int? chosen = quantity;
            if (chosen is null)
            {
                // Open reservations of queued buys are already counted, so the budget shown is what is really left.
                var remaining = Math.Max(0, options.MaxDailySpend - _ledger.SpentToday(_account) - _ledger.ReservedAmount(_account));
                try
                {
                    chosen = await _prompt.AskAsync(
                        new MallPurchasePromptContext(_account, offer, options.MaxQuantityPerPurchase, remaining),
                        cancellationToken).ConfigureAwait(true);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Say(SessionLogKind.Error, $"Could not ask for {what}: {MallPurchaseMessages.Describe(exception)}");
                    return;
                }
            }
            if (chosen is not { } count || count <= 0)
            {
                _log.Purchase($"Purchase of {what} cancelled; nothing was sent.");
                return;
            }
            Queue.Enqueue(
                new MallBuyOrder(offer.ItemId, offer.ItemName, offer.ShopId, offer.ShopName, offer.UnitPrice, count),
                new ServiceMallBuyEngine(_account, session.Service, session.Observer), options);
        }
        catch (OperationCanceledException)
        {
            _log.Purchase("Purchase cancelled.");
        }
        catch (Exception exception)
        {
            Say(SessionLogKind.Error, $"Purchase failed: {MallPurchaseMessages.Describe(exception)}");
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private void Say(SessionLogKind kind, string message)
    {
        _log.Add(kind, message);
        _report?.Invoke(message);
    }
}
