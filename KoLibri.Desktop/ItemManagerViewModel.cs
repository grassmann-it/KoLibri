using System.Collections.ObjectModel;
using KolArbitrage.Core.Accounts;
using KolArbitrage.Core.Data;

namespace KoLibri.Desktop;

/// <summary>
/// The item manager: the KoLmafia "Guts" screen. It lists everything the
/// account owns, groups it the way KoLmafia does and exposes the actions
/// that belong to an item — use, equip, discard, sell.
///
/// The actions are declared here but not executed: the write path goes
/// through the approval flow, which the window drives. A manager that
/// cannot act is still useful — it answers "what do I have and what can
/// I do with it".
/// </summary>
public sealed class ItemManagerViewModel : DesktopViewModel
{
    private readonly DesktopLiveDataProvider _provider;
    private readonly AccountRole _role;

    private ObservableCollection<DesktopInventoryItem> _allItems = new();
    private DesktopInventoryItem? _selectedItem;
    private string _searchText = string.Empty;
    private string _selectedCategoryId = InventoryFilterCategory.AllId;
    private DateTimeOffset? _loadedAt;

    public ItemManagerViewModel(
        AccountRole role = AccountRole.Main,
        DesktopLiveDataProvider? provider = null,
        KoLmafiaCatalog? catalog = null)
    {
        _role = role;
        _provider = provider ?? new DesktopLiveDataProvider();
        _catalog = catalog;
        var categories = BuildCategories();
        Categories = new ObservableCollection<DesktopInventoryCategory>(categories);
        SelectedCategory = categories.First(c => c.Id == InventoryFilterCategory.AllId);
    }

    /// <summary>The catalog the detail pane reads its item data from.</summary>
    private readonly KoLmafiaCatalog? _catalog;

    public AccountRole Role => _role;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            ApplyFilter();
        }
    }

    public DesktopInventoryCategory SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (value is null || !SetProperty(ref _selectedCategory, value)) return;
            ApplyFilter();
        }
    }
    private DesktopInventoryCategory _selectedCategory = null!;

    public DesktopInventoryItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetProperty(ref _selectedItem, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(Detail));
        }
    }

    public ObservableCollection<DesktopInventoryItem> Items { get; } = new();

    public ObservableCollection<DesktopInventoryCategory> Categories { get; }

    public bool HasSelection => SelectedItem is not null;

    public DesktopItemDetail? Detail => SelectedItem is null
        ? null
        : new DesktopItemDetail
        {
            ItemId = SelectedItem.ItemId,
            Name = SelectedItem.Name,
            Quantity = SelectedItem.Quantity,
            ImageUrl = SelectedItem.ImageUrl,
            Uses = SelectedItem.Uses,
            CheapestMallPrice = SelectedItem.CheapestMallPrice
        };

    /// <summary>The actions the operator can take on the selected item.
    /// The write path runs through the approval flow.</summary>
    public ObservableCollection<ItemAction> Actions { get; } = new();

    public DateTimeOffset? LoadedAt
    {
        get => _loadedAt;
        private set
        {
            if (SetProperty(ref _loadedAt, value)) OnPropertyChanged(nameof(LoadedAtText));
        }
    }

    public string LoadedAtText => LoadedAt is { } at
        ? at.ToLocalTime().ToString("HH:mm:ss")
        : "—";

    public int TotalCount => _allItems.Count;

    public string SummaryText => Items.Count == 0
        ? "No entries."
        : $"{Items.Count} of {TotalCount} entries";

    /// <summary>Replaces the item set and re-applies the filter.</summary>
    public void ApplySnapshot(IReadOnlyList<DesktopInventoryItem> items, DateTimeOffset? observedAt = null)
    {
        _allItems = new ObservableCollection<DesktopInventoryItem>(items);
        if (observedAt is { } at) LoadedAt = at;
        ApplyFilter();
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(SummaryText));
    }

    /// <summary>Loads the live inventory.</summary>
    public async Task LoadAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        LoadState = DesktopLoadState.Loading;
        try
        {
            var snapshot = await _provider
                .LoadInventoryAsync(_role, forceRefresh, _catalog, cancellationToken)
                .ConfigureAwait(false);
            ApplySnapshot(snapshot.Items, snapshot.ObservedAt);
            LoadState = snapshot.Items.Count == 0
                ? DesktopLoadState.Empty
                : snapshot.IsStale ? DesktopLoadState.Stale : DesktopLoadState.Ready;
            IsStale = snapshot.IsStale;
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            LoadState = DesktopLoadState.Error;
        }
        finally
        {
            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(SummaryText));
        }
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
        => LoadAsync(forceRefresh: true, cancellationToken);

    /// <summary>Recomputes the action list for the selected item.</summary>
    public void RefreshActions()
    {
        Actions.Clear();
        if (SelectedItem is null) return;
        var item = SelectedItem;
        var isConsumable = item.AccessFlags.Any(f =>
            string.Equals(f, "q", StringComparison.OrdinalIgnoreCase));
        var isTransferable = item.AccessFlags.Any(f =>
            string.Equals(f, "t", StringComparison.OrdinalIgnoreCase));
        if (isConsumable)
            Actions.Add(new ItemAction("use", "Use", "Use the item (needs approval)."));
        if (isTransferable)
            Actions.Add(new ItemAction("transfer", "Transfer", "Send to the other account."));
        Actions.Add(new ItemAction("sell", "Offer in the mall", "Create an own listing."));
        Actions.Add(new ItemAction("details", "Details", "Show catalog data and origin."));
    }

    private void ApplyFilter()
    {
        var query = SearchText?.Trim() ?? string.Empty;
        var category = SelectedCategory;
        var filtered = _allItems.Where(item =>
        {
            if (category is not null && !category.Matches(item)) return false;
            if (query.Length == 0) return true;
            return item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.ItemId.Contains(query, StringComparison.OrdinalIgnoreCase);
        }).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();

        Items.Clear();
        foreach (var item in filtered) Items.Add(item);
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(TotalCount));
    }

    private static IReadOnlyList<DesktopInventoryCategory> BuildCategories()
    {
        static bool HasFlag(DesktopInventoryItem item, string flag)
            => item.AccessFlags.Any(f => string.Equals(f, flag, StringComparison.OrdinalIgnoreCase));

        return new[]
        {
            new DesktopInventoryCategory
            {
                Id = InventoryFilterCategory.AllId,
                Label = "All",
                Matches = _ => true
            },
            new DesktopInventoryCategory
            {
                Id = InventoryFilterCategory.ConsumableId,
                Label = "Consumable",
                Matches = item => HasFlag(item, "q")
            },
            new DesktopInventoryCategory
            {
                Id = InventoryFilterCategory.GadgetId,
                Label = "Gadget",
                Matches = item => HasFlag(item, "g")
            },
            new DesktopInventoryCategory
            {
                Id = InventoryFilterCategory.TransferableId,
                Label = "Transferable",
                Matches = item => HasFlag(item, "t")
            },
            new DesktopInventoryCategory
            {
                Id = InventoryFilterCategory.DiscardableId,
                Label = "Discardable",
                Matches = item => HasFlag(item, "d")
            },
            new DesktopInventoryCategory
            {
                Id = InventoryFilterCategory.UncategorizedId,
                Label = "Other",
                Matches = item => item.AccessFlags.Count == 0
            }
        };
    }
}

/// <summary>One action on the selected item.</summary>
public sealed record ItemAction(string Id, string Label, string Description);
