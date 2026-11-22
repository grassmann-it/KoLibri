using System.Globalization;
using KoLibri.Core.Basis;
using KoLibri.Core.Markt;
using KoLibri.Core.Spiel;

namespace KoLibri.Desktop;

/// <summary>Meat spent through this window per account and day. In memory only: it restarts with the app.</summary>
public sealed class MallSpendLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (DateOnly Day, long Spent)> _spent = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _clock;

    public MallSpendLedger(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    private DateOnly Today => DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

    public long SpentToday(string account)
    {
        lock (_gate)
            return _spent.TryGetValue(account, out var e) && e.Day == Today ? e.Spent : 0;
    }

    public void Add(string account, long meat)
    {
        if (meat <= 0) return;
        lock (_gate)
        {
            var current = _spent.TryGetValue(account, out var e) && e.Day == Today ? e.Spent : 0;
            _spent[account] = (Today, current + meat);
        }
    }
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

        var listing = page.Items.FirstOrDefault(i => i.ItemId == request.ItemId)?.Listings
            .FirstOrDefault(l => l.StoreId == request.StoreId);
        var offer = listing is null
            ? new MallPurchaseOffer(request.SellerId, request.StoreId, request.ItemId,
                request.ExpectedUnitPrice, 0, now)
            : new MallPurchaseOffer(listing.SellerId ?? request.SellerId, listing.StoreId, listing.ItemId,
                listing.Price, listing.QuantityKnown ? listing.Quantity : 0, now,
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
            isStale: (status?.IsStale ?? true) || inventory.IsStale);
        return new MallPurchaseObservation(offer, account, now);
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
    private int _busy;

    public MallPurchaseCoordinator(
        string account,
        Func<CancellationToken, Task<(DesktopMallPurchaseService, MallPurchaseObserver)?>> open,
        IMallPurchasePrompt prompt,
        SessionLogViewModel log,
        MallSpendLedger ledger,
        Action<string>? report = null)
    {
        _account = account;
        _open = open;
        _prompt = prompt;
        _log = log;
        _ledger = ledger;
        _report = report;
    }

    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    public bool CanRequest(MallOfferRow offer)
        => !IsBusy && offer.UnitPrice > 0 && offer.Stock != 0 && long.TryParse(offer.ShopId, out _);

    public async Task RequestAsync(MallOfferRow offer, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try
        {
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
                Say(SessionLogKind.Error, $"Preview for {what} failed: {exception.Message}");
                return;
            }
            if (chosen is null)
            {
                _log.Purchase($"Purchase of {what} cancelled; nothing was sent.");
                return;
            }

            _log.Purchase(string.Create(CultureInfo.InvariantCulture,
                $"Confirmed: {chosen.Quantity} x {offer.ItemName} at {chosen.UnitPrice:N0}, total {chosen.TotalPrice:N0}."));
            var result = await session.Service.ConfirmAsync(chosen, cancellationToken).ConfigureAwait(true);
            if (result.Succeeded) _ledger.Add(_account, chosen.TotalPrice);
            Say(result.Succeeded ? SessionLogKind.Purchase : SessionLogKind.Error,
                $"{result.Status}: {result.Message}");
        }
        catch (OperationCanceledException)
        {
            _log.Purchase("Purchase cancelled.");
        }
        catch (Exception exception)
        {
            Say(SessionLogKind.Error, $"Purchase failed: {exception.Message}");
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
