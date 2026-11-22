using System.Globalization;
using KoLibri.Core.Basis;
using KoLibri.Core.Markt;
using KoLibri.Core.Spiel;

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
        var page = await _reads.SearchAsync($"\"{name}\"", ct).ConfigureAwait(false);
        var status = await _reads.StatusAsync(ct).ConfigureAwait(false);
        var inventory = await _reads.InventoryAsync(ct).ConfigureAwait(false);
        var now = _clock.GetUtcNow();
        // A cached page must never be re-dated as fresh: before the POST it blocks the purchase.
        if (before is null && page.IsStale)
            throw new InvalidOperationException("The mall offer is stale; search again for a fresh offer.");
        var observedAt = page.FetchedAt < now ? page.FetchedAt : now;

        var listing = page.Items.FirstOrDefault(i => i.ItemId == request.ItemId)?.Listings
            .FirstOrDefault(l => l.StoreId == request.StoreId);
        var offer = listing is null
            ? new MallPurchaseOffer(request.SellerId, request.StoreId, request.ItemId,
                request.ExpectedUnitPrice, 0, observedAt)
            : new MallPurchaseOffer(listing.SellerId ?? request.SellerId, listing.StoreId, listing.ItemId,
                listing.Price, listing.QuantityKnown ? listing.Quantity : 0, observedAt,
                listing.LimitKind == MallLimitKind.Limited ? listing.PurchaseLimit : null,
                listing.SellerName, listing.StoreName);

        var meat = status?.Meat ?? 0;
        var spent = before is null
            ? _ledger.SpentToday(_account)
            : before.Account.DailySpent + Math.Max(0, before.Account.Meat - meat);
        var items = inventory.Items.GroupBy(i => i.ItemId)
            .ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity), StringComparer.Ordinal);
        var account = new MallPurchaseAccountState(_account, meat, spent, _dailyLimit(), items, now,
            sessionValid: status is not null && inventory.HasSession,
            isStale: (status?.IsStale ?? true) || inventory.IsStale || page.IsStale);
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

/// <summary>The dialog seam: shows the preview, lets the user change the quantity, returns the preview to confirm or null.</summary>
public interface IMallPurchasePrompt
{
    Task<DesktopMallPurchasePreview?> AskAsync(
        MallPurchasePromptContext context,
        Func<int, CancellationToken, Task<DesktopMallPurchasePreview>> preview,
        CancellationToken cancellationToken);
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

public sealed record MallPurchasePromptContext(string Account, MallOfferRow Offer, int MaxQuantity);

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
        Func<CancellationToken, Task<string>>? evidence = null)
    {
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

    public bool CanRequest(MallOfferRow offer)
        => !IsBusy && !offer.IsStale && offer.UnitPrice > 0 && offer.Stock != 0 && long.TryParse(offer.ShopId, out _);

    public async Task RequestAsync(MallOfferRow offer, CancellationToken cancellationToken)
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
            var opened = await _open(cancellationToken).ConfigureAwait(true);
            if (opened is not { } session)
            {
                Say(SessionLogKind.Error, $"Cannot buy {what}: {_account} is not signed in.");
                return;
            }
            session.Observer.Remember(offer.ItemId, offer.ItemName);
            _log.Purchase($"Preview requested for {what}.");

            DesktopMallPurchasePreview? chosen;
            try
            {
                chosen = await _prompt.AskAsync(
                    new MallPurchasePromptContext(_account, offer, session.Service.Options.MaxQuantityPerPurchase),
                    (quantity, ct) => session.Service.PreviewAsync(_account, offer.ItemId, offer.ShopId, offer.UnitPrice, quantity, ct),
                    cancellationToken).ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Say(SessionLogKind.Error, $"Preview for {what} failed: {MallPurchaseMessages.Describe(exception)}");
                return;
            }
            if (chosen is null)
            {
                _log.Purchase($"Purchase of {what} cancelled; nothing was sent.");
                return;
            }

            // A progress line only; the outcome gets exactly one final line below (Settled, Unclear, Rejected, ...).
            _log.Purchase(string.Create(CultureInfo.InvariantCulture,
                $"Sending purchase: {chosen.Quantity} x {offer.ItemName} at {chosen.UnitPrice:N0}, total {chosen.TotalPrice:N0}."));
            // Reserved (and written to disk) before the POST; settled only once meat and inventory prove it.
            // A save failure, an invalid ledger or an exceeded daily budget stops the purchase here.
            if (!_ledger.TryReserve(_account, chosen.TotalPrice, session.Service.Options.MaxDailySpend, out var ledgerError))
            {
                Say(SessionLogKind.Error, $"Nothing was bought: {ledgerError}");
                return;
            }
            DesktopMallPurchaseResult result;
            try
            {
                result = await session.Service.ConfirmAsync(chosen, cancellationToken).ConfigureAwait(true);
            }
            catch
            {
                // Unknown whether a POST went out: keep the meat counted until reconciled.
                _ledger.MarkUnclear(_account, chosen.TotalPrice);
                throw;
            }
            switch (result.Status)
            {
                case DesktopMallPurchaseStatus.Purchased: _ledger.Settle(_account, chosen.TotalPrice); break;
                case DesktopMallPurchaseStatus.Partial:
                    _ledger.SettlePartial(_account, chosen.TotalPrice, chosen.UnitPrice * result.ReceivedQuantity); break;
                case DesktopMallPurchaseStatus.Unclear: _ledger.MarkUnclear(_account, chosen.TotalPrice); break;
                default: _ledger.Release(_account, chosen.TotalPrice); break;
            }
            var label = result.Status switch
            {
                DesktopMallPurchaseStatus.Purchased => "Settled",
                DesktopMallPurchaseStatus.NotSent => "Not sent",
                _ => result.Status.ToString()
            };
            Say(result.Succeeded ? SessionLogKind.Purchase : SessionLogKind.Error, $"{label}: {result.Message}");
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
