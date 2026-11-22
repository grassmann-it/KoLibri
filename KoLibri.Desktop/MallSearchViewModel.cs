using System.Collections.ObjectModel;
using System.Globalization;
using KoLibri.Core.Markt;

namespace KoLibri.Desktop;

/// <summary>Pricegun median of an item with the age of the data it came from.</summary>
public sealed record PricegunInfo(long? Median, DateTimeOffset FetchedAt, bool IsStale);

/// <summary>Everything the search view model needs from the outside world.</summary>
public interface IMallSearchBackend
{
    Task<MallSearchPage> SearchAsync(string term, bool forceRefresh, CancellationToken cancellationToken);
    Task<PricegunInfo> LoadPricegunAsync(string itemId, CancellationToken cancellationToken);
}

/// <summary>
/// Hook for the purchase flow (wired up later). The search view model only
/// asks; it never buys. Without an implementation every Buy button stays disabled.
/// </summary>
public interface IMallPurchaseRequest
{
    bool CanRequest(MallOfferRow offer);
    Task RequestAsync(MallOfferRow offer, CancellationToken cancellationToken);
}

/// <summary>One offer with the identifiers the purchase flow needs.</summary>
public sealed record MallOfferRow
{
    public required int Rank { get; init; }
    public required string ItemId { get; init; }
    public required string ItemName { get; init; }
    public required string ShopId { get; init; }
    public required string ShopName { get; init; }
    public required long UnitPrice { get; init; }
    /// <summary>Null when the page showed no readable stock.</summary>
    public required int? Stock { get; init; }
    public required MallLimitKind LimitKind { get; init; }
    public int? DailyLimit { get; init; }
    public long? PricegunMedian { get; init; }
    public bool CanBuy { get; init; }
    public string? SellerName { get; init; }
    /// <summary>The page this offer came from was served from a stale cache: it is never bought from.</summary>
    public bool IsStale { get; init; }
    /// <summary>At or under the active price threshold (the 11-leaf clover quick action).</summary>
    public bool UnderThreshold { get; init; }

    public string ShopLink => $"https://www.kingdomofloathing.com/mallstore.php?whichstore={ShopId}";

    public string RankText => Rank.ToString(CultureInfo.InvariantCulture);
    public string PriceText => $"{UnitPrice:N0}";
    public string StockText => Stock is { } s ? $"{s:N0} in stock" : "stock ?";
    public string LimitText => LimitKind switch
    {
        MallLimitKind.Limited => $"{DailyLimit:N0} / day",
        MallLimitKind.Unlimited => "unlimited",
        _ => "?"
    };
    public string PricegunText => PricegunMedian is { } m ? $"{m:N0}" : "—";
}

/// <summary>An item a partial search matched, offered in the "pick item" list.</summary>
public sealed record MallItemChoice(string ItemId, string Name, int OfferCount, long? CheapestPrice)
{
    public string Label => CheapestPrice is { } p
        ? $"{Name}  ·  #{ItemId}  ·  {OfferCount:N0} offers from {p:N0}"
        : $"{Name}  ·  #{ItemId}";
}

/// <summary>Search state for the Mall tab: latest search wins, errors are visible, F5 refreshes it.</summary>
public sealed class MallSearchViewModel : DesktopViewModel
{
    private readonly IMallSearchBackend _backend;
    private readonly Func<string, IReadOnlyList<(string Id, string Name)>> _catalog;
    private readonly Dictionary<string, Task<PricegunInfo?>> _pricegun = new();
    private CancellationTokenSource? _cancellation;
    private long _generation;
    private MallSearchPage? _page;
    private string _lastTerm = string.Empty;
    private string _searchText = string.Empty;
    private string _status = "Enter an item name or ID.";
    private string? _error;
    private string? _pricegunNote;
    private string? _notice;
    private bool _isSearching;
    private string? _selectedItemId;
    private string? _preferredItemId;
    private long? _threshold;

    public const string CloverName = "11-leaf clover";

    /// <summary>Reads the configured clover price limit (Meat) when the quick action runs.</summary>
    public Func<long>? CloverThreshold { get; set; }

    /// <summary>Offers at or under this price are highlighted; null switches the highlight off.</summary>
    public long? HighlightThreshold
    {
        get => _threshold;
        private set { if (SetProperty(ref _threshold, value)) OnPropertyChanged(nameof(HasThreshold)); }
    }
    public bool HasThreshold => _threshold is not null;

    public MallSearchViewModel(
        IMallSearchBackend backend,
        Func<string, IReadOnlyList<(string Id, string Name)>>? catalog = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _catalog = catalog ?? (_ => Array.Empty<(string, string)>());
    }

    public ObservableCollection<MallOfferRow> Offers { get; } = new();
    public ObservableCollection<MallItemChoice> Choices { get; } = new();

    /// <summary>Set by the host once the purchase flow exists.</summary>
    public IMallPurchaseRequest? PurchaseRequest { get; set; }

    public string SearchText { get => _searchText; set => SetProperty(ref _searchText, value); }
    public string StatusText { get => _status; private set => SetProperty(ref _status, value); }
    public string? SearchError
    {
        get => _error;
        private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasSearchError)); }
    }
    public bool HasSearchError => !string.IsNullOrEmpty(_error);
    public string? PricegunNote
    {
        get => _pricegunNote;
        private set { if (SetProperty(ref _pricegunNote, value)) OnPropertyChanged(nameof(HasPricegunNote)); }
    }
    public bool HasPricegunNote => !string.IsNullOrEmpty(_pricegunNote);
    /// <summary>Partial / truncated / stale remarks.</summary>
    public string? Notice
    {
        get => _notice;
        private set { if (SetProperty(ref _notice, value)) OnPropertyChanged(nameof(HasNotice)); }
    }
    public bool HasNotice => !string.IsNullOrEmpty(_notice);
    public bool IsSearching
    {
        get => _isSearching;
        private set { if (SetProperty(ref _isSearching, value)) OnPropertyChanged(nameof(CanCancel)); }
    }
    public bool CanCancel => IsSearching;
    public bool NeedsPick => Choices.Count > 1 && _selectedItemId is null;
    public bool HasOffers => Offers.Count > 0;
    public bool HasSearched => _page is not null;

    /// <summary>Starts a search for <see cref="SearchText"/>. A search that is still running is replaced, never ignored.</summary>
    public Task SearchAsync()
    {
        _preferredItemId = null;
        HighlightThreshold = null;
        return RunAsync(SearchText, forceRefresh: false);
    }

    /// <summary>Searches an item the caller already identified (for example from Inventory): the id is kept,
    /// so a name that matches several items still lands on this one.</summary>
    public Task SearchItemAsync(string itemId, string name)
    {
        SearchText = name;
        HighlightThreshold = null;
        _preferredItemId = itemId;
        return RunAsync(name, forceRefresh: false);
    }

    /// <summary>Quick action: searches the 11-leaf clover and highlights offers at or under the threshold.</summary>
    public Task SearchCloverAsync()
    {
        SearchText = CloverName;
        _preferredItemId = null;
        HighlightThreshold = CloverThreshold?.Invoke() ?? 27_000;
        return RunAsync(CloverName, forceRefresh: false);
    }

    /// <summary>F5: runs the active search again, bypassing the cache. False when there is nothing to refresh.</summary>
    public bool CanRefresh => _lastTerm.Length > 0;

    public Task RefreshAsync() => CanRefresh ? RunAsync(_lastTerm, forceRefresh: true) : Task.CompletedTask;

    public void Cancel() => _cancellation?.Cancel();

    /// <summary>Picks one of several matched items without a new request.</summary>
    public Task PickItemAsync(MallItemChoice choice)
    {
        if (_page is null) return Task.CompletedTask;
        return ShowItemAsync(_page, choice.ItemId, CancellationToken.None);
    }

    public async Task RequestBuyAsync(MallOfferRow row)
    {
        var hook = PurchaseRequest;
        if (hook is null || !hook.CanRequest(row)) return;
        await hook.RequestAsync(row, CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>Resolves what the user typed: an item id or an exact name becomes an exact (quoted) search.</summary>
    private (string? Term, string? Problem) Resolve(string typed)
    {
        var text = typed.Trim();
        if (text.Length == 0) return (null, "Enter an item name or ID.");
        if (text.All(char.IsDigit))
        {
            var byId = _catalog(text).FirstOrDefault(i => i.Id == text);
            return byId.Name is null
                ? (null, $"Item ID {text} is not in the item catalog. Search by name instead.")
                : (Exact(byId.Name), null);
        }
        var exact = _catalog(text).FirstOrDefault(i => string.Equals(i.Name, text, StringComparison.OrdinalIgnoreCase));
        return (exact.Name is null ? text : Exact(exact.Name), null);
    }

    private static string Exact(string name) => $"\"{name}\"";

    private async Task RunAsync(string typed, bool forceRefresh)
    {
        var (term, problem) = Resolve(typed);
        if (term is null)
        {
            SearchError = problem;
            StatusText = problem!;
            return;
        }

        // The newest request wins; the old one is cancelled, not dropped silently.
        _cancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        var generation = ++_generation;
        IsSearching = true;
        SearchError = null;
        StatusText = $"Searching for {typed.Trim()}…";
        try
        {
            var page = await _backend.SearchAsync(term, forceRefresh, cancellation.Token).ConfigureAwait(true);
            if (generation != _generation) return;
            var preferred = _preferredItemId is { } p && page.Items.Any(i => i.ItemId == p) ? p : null;
            _page = page;
            _lastTerm = typed.Trim();
            Choices.Clear();
            foreach (var group in page.Items)
                Choices.Add(new MallItemChoice(group.ItemId, group.ItemName ?? $"Item #{group.ItemId}",
                    group.TotalOffers, group.Listings.Count > 0 ? group.Listings[0].Price : null));
            OnPropertyChanged(nameof(HasSearched));
            OnPropertyChanged(nameof(CanRefresh));

            _selectedItemId = null;
            Offers.Clear();
            if (page.Items.Count == 0)
            {
                Notice = page.IsStale ? "Showing cached data (stale)." : null;
                StatusText = $"No offers for {typed.Trim()}.";
                PricegunNote = null;
            }
            else if (preferred is not null)
                await ShowItemAsync(page, preferred, cancellation.Token).ConfigureAwait(true);
            else if (page.Items.Count == 1)
            {
                await ShowItemAsync(page, page.Items[0].ItemId, cancellation.Token).ConfigureAwait(true);
            }
            else
            {
                var bare = term.Trim('"');
                var exact = page.Items.Where(i => string.Equals(i.ItemName, bare, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (exact.Length == 1)
                    await ShowItemAsync(page, exact[0].ItemId, cancellation.Token).ConfigureAwait(true);
                else
                {
                    Notice = $"“{typed.Trim()}” matches {page.Items.Count} items. Pick one.";
                    StatusText = $"{page.Items.Count} items match. Pick the item you mean.";
                    PricegunNote = null;
                }
            }
            OnPropertyChanged(nameof(NeedsPick));
            OnPropertyChanged(nameof(HasOffers));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (generation == _generation) StatusText = "Search cancelled.";
        }
        catch (Exception exception)
        {
            if (generation != _generation) return;
            SearchError = MallSearchErrors.Describe(exception);
            StatusText = "Search failed.";
        }
        finally
        {
            if (generation == _generation) IsSearching = false;
        }
    }

    private async Task ShowItemAsync(MallSearchPage page, string itemId, CancellationToken cancellationToken)
    {
        var group = page.Items.First(i => i.ItemId == itemId);
        _selectedItemId = itemId;
        var name = group.ItemName ?? $"Item #{itemId}";
        FillOffers(group, name, median: null, page.IsStale);
        var notes = new List<string>();
        if (page.Items.Count > 1) notes.Add($"One of {page.Items.Count} matching items.");
        if (group.IsTruncated) notes.Add($"Showing the cheapest {group.Listings.Count:N0} of {group.TotalOffers:N0} offers.");
        if (page.IsStale) notes.Add("Cached data (stale).");
        Notice = notes.Count == 0 ? null : string.Join(" ", notes);
        StatusText = group.Listings.Count == 0
            ? $"No offers for {name}."
            : $"{name} · {group.TotalOffers:N0} offers · {page.FetchedAt.ToLocalTime():HH:mm}";
        OnPropertyChanged(nameof(NeedsPick));
        OnPropertyChanged(nameof(HasOffers));

        // Pricegun is loaded once per item; a failure only adds a note.
        PricegunNote = null;
        if (group.Listings.Count == 0) return;
        if (!_pricegun.TryGetValue(itemId, out var task))
            _pricegun[itemId] = task = LoadPricegunQuietAsync(itemId, cancellationToken);
        var info = await task.ConfigureAwait(true);
        if (_selectedItemId != itemId) return;
        if (info is not null)
        {
            FillOffers(group, name, info.Median, page.IsStale);
            var age = DateTimeOffset.UtcNow - info.FetchedAt;
            PricegunNote = info.Median is null
                ? "Pricegun has no sales for this item."
                : $"Pricegun median {info.Median:N0} · data {FormatAge(age)} old" + (info.IsStale ? " (stale)" : "");
        }
    }

    private async Task<PricegunInfo?> LoadPricegunQuietAsync(string itemId, CancellationToken cancellationToken)
    {
        try { return await _backend.LoadPricegunAsync(itemId, cancellationToken).ConfigureAwait(true); }
        catch (Exception exception)
        {
            PricegunNote = cancellationToken.IsCancellationRequested ? null : $"Pricegun unavailable: {exception.Message}";
            _pricegun.Remove(itemId); // retry on the next search
            return null;
        }
    }

    private void FillOffers(MallItemOffers group, string name, long? median, bool stale)
    {
        Offers.Clear();
        var rank = 0;
        foreach (var l in group.Listings.OrderBy(l => l.Price))
        {
            var row = new MallOfferRow
            {
                Rank = ++rank,
                ItemId = l.ItemId,
                ItemName = name,
                ShopId = l.StoreId,
                ShopName = l.StoreName ?? $"Store #{l.StoreId}",
                UnitPrice = l.Price,
                Stock = l.QuantityKnown ? l.Quantity : null,
                LimitKind = l.LimitKind,
                DailyLimit = l.PurchaseLimit,
                PricegunMedian = median,
                SellerName = l.SellerName,
                IsStale = stale,
                UnderThreshold = _threshold is { } limit && l.Price <= limit
            };
            Offers.Add(row with { CanBuy = !stale && (PurchaseRequest?.CanRequest(row) ?? false) });
        }
        OnPropertyChanged(nameof(HasOffers));
    }

    private static string FormatAge(TimeSpan age) =>
        age.TotalMinutes < 1 ? "under a minute"
        : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min"
        : age.TotalDays < 1 ? $"{(int)age.TotalHours} h"
        : $"{(int)age.TotalDays} d";
}

/// <summary>Readable text for a failed mall search.</summary>
public static class MallSearchErrors
{
    public static string Describe(Exception exception) => exception switch
    {
        MallSearchParseException e => $"The mall page could not be read: {e.Message}",
        HttpRequestException => "Mall search failed: KoL could not be reached. Check your connection and that you are logged in.",
        _ => $"Mall search failed: {exception.Message}"
    };
}

/// <summary>Adapter from the live data provider to the search view model.</summary>
public sealed class LiveMallSearchBackend(DesktopLiveDataProvider provider, KoLibri.Core.Basis.AccountRole role) : IMallSearchBackend
{
    public Task<MallSearchPage> SearchAsync(string term, bool forceRefresh, CancellationToken cancellationToken)
        => provider.SearchMallItemsAsync(role, term, forceRefresh, cancellationToken);

    public Task<PricegunInfo> LoadPricegunAsync(string itemId, CancellationToken cancellationToken)
        => provider.LoadPricegunAsync(itemId, cancellationToken);
}
