using System.Globalization;
using KoLibri.Core.Basis;
using KoLibri.Core.Markt;
using KoLibri.Core.Spiel;

namespace KoLibri.Desktop;

/// <summary>
/// Meat spent through the desktop mall purchase per account and KoL day, kept in a small JSON file next to
/// the settings (never credentials) so the budget survives a restart. A purchase is reserved before the POST,
/// settled once meat and inventory are verified, released when nothing was sent, and stays reserved
/// ("unclear") until <see cref="Reconcile"/> when the outcome is not proven. A reservation still open at
/// startup counts as unclear. Without a path the ledger is in memory only.
/// </summary>
public sealed class MallSpendLedger
{
    private sealed class Entry
    {
        public string Day { get; set; } = "";
        public long Settled { get; set; }
        public long Unclear { get; set; }
        public long Reserved { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _clock;
    private readonly string? _path;

    public MallSpendLedger(TimeProvider? clock = null, string? path = null)
    {
        _clock = clock ?? TimeProvider.System;
        _path = path;
        Load();
    }

    /// <summary>The ledger file that sits next to the given settings file.</summary>
    public static string PathNextTo(DesktopSettingsStore store)
        => System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(store.Path))!, "mall-spend-ledger.json");

    /// <summary>The KoL day: it rolls over at 03:30 Eastern time (fixed UTC-5 where the zone is unknown).</summary>
    private string Today
    {
        get
        {
            var utc = _clock.GetUtcNow();
            TimeSpan offset;
            try { offset = TimeZoneInfo.FindSystemTimeZoneById("America/New_York").GetUtcOffset(utc); }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
            { offset = TimeSpan.FromHours(-5); }
            return DateOnly.FromDateTime((utc + offset - TimeSpan.FromHours(3.5)).DateTime)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
    }

    private Entry Current(string account)
    {
        var today = Today;
        if (!_entries.TryGetValue(account, out var e) || e.Day != today)
            _entries[account] = e = new Entry { Day = today };
        return e;
    }

    /// <summary>Settled plus unclear spending today. A reservation of a purchase that is being sent right now is not
    /// included: the gateway adds that purchase itself.</summary>
    public long SpentToday(string account)
    {
        lock (_gate)
        {
            var e = Current(account);
            // An unreadable ledger file must not unlock the budget: it counts as fully spent until fixed or removed.
            return e.Settled + e.Unclear + (_unreadable ? long.MaxValue / 4 : 0);
        }
    }

    /// <summary>Meat reserved for outcomes that were never proven.</summary>
    public long UnclearToday(string account)
    {
        lock (_gate) return Current(account).Unclear;
    }

    /// <summary>Written before the POST so a crash in between still counts the meat.</summary>
    public void Reserve(string account, long meat)
    {
        if (meat <= 0) return;
        lock (_gate)
        {
            Current(account).Reserved += meat;
            Save();
        }
    }

    /// <summary>The reservation turned out verified: it becomes spending.</summary>
    public void Settle(string account, long meat)
    {
        if (meat <= 0) return;
        lock (_gate)
        {
            var e = Current(account);
            e.Reserved = Math.Max(0, e.Reserved - meat);
            e.Settled += meat;
            Save();
        }
    }

    /// <summary>Nothing was sent: the reservation is dropped.</summary>
    public void Release(string account, long meat)
    {
        if (meat <= 0) return;
        lock (_gate)
        {
            var e = Current(account);
            e.Reserved = Math.Max(0, e.Reserved - meat);
            Save();
        }
    }

    /// <summary>The outcome is not proven: the reservation stays counted until reconciled.</summary>
    public void MarkUnclear(string account, long meat)
    {
        if (meat <= 0) return;
        lock (_gate)
        {
            var e = Current(account);
            e.Reserved = Math.Max(0, e.Reserved - meat);
            e.Unclear += meat;
            Save();
        }
    }

    /// <summary>The user checked inventory and meat: unclear meat is either spent or not.</summary>
    public void Reconcile(string account, long actuallySpent)
    {
        lock (_gate)
        {
            var e = Current(account);
            e.Settled += Math.Max(0, actuallySpent);
            e.Unclear = 0;
            Save();
        }
    }

    /// <summary>Records meat that was spent and verified without a reservation.</summary>
    public void Add(string account, long meat)
    {
        if (meat <= 0) return;
        lock (_gate)
        {
            Current(account).Settled += meat;
            Save();
        }
    }

    private void Load()
    {
        if (_path is null) return;
        try
        {
            if (!File.Exists(_path)) return;
            if (System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(_path)) is not System.Text.Json.Nodes.JsonObject root
                || root["accounts"] is not System.Text.Json.Nodes.JsonObject accounts) return;
            var today = Today;
            foreach (var (name, node) in accounts)
            {
                if (node is not System.Text.Json.Nodes.JsonObject o
                    || (string?)o["day"] != today) continue;
                var e = new Entry
                {
                    Day = today,
                    Settled = Math.Max(0, (long?)o["settled"] ?? 0),
                    // A reservation that never settled may have been sent: it stays counted.
                    Unclear = Math.Max(0, (long?)o["unclear"] ?? 0) + Math.Max(0, (long?)o["reserved"] ?? 0)
                };
                _entries[name] = e;
            }
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException
                                      or InvalidOperationException or FormatException)
        {
            // An unreadable ledger must not unlock the budget: treat the whole limit as spent today.
            _unreadable = true;
        }
    }

    private bool _unreadable;

    /// <summary>True when the ledger file existed but could not be read.</summary>
    public bool Unreadable => _unreadable;

    private void Save()
    {
        if (_path is null) return;
        try
        {
            var accounts = new System.Text.Json.Nodes.JsonObject();
            foreach (var (name, e) in _entries)
                accounts[name] = new System.Text.Json.Nodes.JsonObject
                {
                    ["day"] = e.Day, ["settled"] = e.Settled, ["unclear"] = e.Unclear, ["reserved"] = e.Reserved
                };
            var json = new System.Text.Json.Nodes.JsonObject { ["version"] = 1, ["accounts"] = accounts }
                .ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_path))!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The in-memory counters still hold for this run.
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
            // Reserved (and written to disk) before the POST; settled only once meat and inventory prove it.
            _ledger.Reserve(_account, chosen.TotalPrice);
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
                case DesktopMallPurchaseStatus.Unclear: _ledger.MarkUnclear(_account, chosen.TotalPrice); break;
                default: _ledger.Release(_account, chosen.TotalPrice); break;
            }
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
