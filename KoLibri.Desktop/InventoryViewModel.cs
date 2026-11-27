using System.Collections.ObjectModel;
using KoLibri.Desktop.ContextMenus;

namespace KoLibri.Desktop;

/// <summary>
/// The inventory browser: the list of what the account holds, a search box
/// and the category tabs, with a detail pane for the selected row.
///
/// The filtering lives here rather than in the view so the same logic can
/// be tested and reused. The categories mirror KoLmafia's access flags:
/// consumable, gadget, transferable, discardable and the uncategorised
/// remainder.
/// </summary>
public sealed class InventoryViewModel : DesktopViewModel
{
    /// <summary>Null in tests and at design time: loading then shows the signed-out state.</summary>
    private readonly DesktopLiveDataProvider? _provider;
    private readonly AccountRole _role;
    private readonly KoLmafiaCatalog? _catalog;

    private ObservableCollection<DesktopInventoryItem> _allItems = new();
    private DesktopInventoryItem? _selectedItem;
    private string _searchText = string.Empty;
    private string _selectedCategoryId = InventoryFilterCategory.AllId;
    private DateTimeOffset? _loadedAt;

    private readonly ItemClassifier? _classifier;
    private OrganState _organs = OrganState.Unknown;
    private string _actionResult = string.Empty;

    public InventoryViewModel(
        AccountRole role = AccountRole.Main,
        DesktopLiveDataProvider? provider = null,
        KoLmafiaCatalog? catalog = null,
        ItemClassifier? classifier = null)
    {
        _role = role;
        _provider = provider;
        _catalog = catalog;
        _classifier = classifier ?? (catalog is null ? null : new ItemClassifier(catalog));
        var categories = BuildCategories(this);
        Categories = new ObservableCollection<DesktopInventoryCategory>(categories);
        SelectedCategory = categories.First(c => c.Id == InventoryFilterCategory.AllId);
    }

    public AccountRole Role => _role;

    /// <summary>Classification of a row; unknown ids (or no catalog) classify as Misc.</summary>
    public ItemClassification Classify(DesktopInventoryItem item)
        => _classifier?.Classify(item.ItemId) ?? new ItemClassification { ItemId = item.ItemId, Name = item.Name };

    /// <summary>Last observed fullness, inebriety and spleen, for the overfill warning.</summary>
    public OrganState Organs
    {
        get => _organs;
        set
        {
            if (SetProperty(ref _organs, value)) OnPropertyChanged(nameof(OrganSummary));
        }
    }

    public string OrganSummary => _organs.Summary;

    /// <summary>The readable result (or error) of the last inventory action; empty before the first one.</summary>
    public string ActionResult
    {
        get => _actionResult;
        set => SetProperty(ref _actionResult, value);
    }

    /// <summary>The context-menu target: use kind and equip slots come from the item classification,
    /// so a row never offers an action it cannot do.</summary>
    public ItemMenuTarget ToMenuTarget(DesktopInventoryItem item)
    {
        var c = Classify(item);
        return new ItemMenuTarget(item.ItemId, item.Name, item.Quantity,
            InventoryActionPlanner.UseKindOf(c), InventoryActionPlanner.EquipSlotsOf(c));
    }

    /// <summary>The search text. Setting it re-filters the list.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            ApplyFilter();
        }
    }

    /// <summary>Column the list is sorted by (name, type or count).</summary>
    public InventorySortKey SortKey { get; private set; } = InventorySortKey.Name;

    public bool SortDescending { get; private set; }

    /// <summary>Sorts by a column; choosing the active column again flips the direction.</summary>
    public void SortBy(InventorySortKey key)
    {
        if (key == SortKey) SortDescending = !SortDescending;
        else { SortKey = key; SortDescending = key == InventorySortKey.Count; }
        ApplyFilter();
        OnPropertyChanged(nameof(SortKey));
    }

    /// <summary>The selected category tab.</summary>
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

    /// <summary>The row whose detail is shown.</summary>
    public DesktopInventoryItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            var previousId = _selectedItem?.ItemId;
            if (!SetProperty(ref _selectedItem, value)) return;
            // A result describes the item it was produced for; a different (or no) selection starts clean.
            if (previousId != value?.ItemId && _actionResult.Length > 0) ActionResult = string.Empty;
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(Detail));
        }
    }

    /// <summary>The filtered, sorted rows.</summary>
    public ObservableCollection<DesktopInventoryItem> Items { get; } = new();

    public ObservableCollection<DesktopInventoryCategory> Categories { get; }

    public bool HasSelection => SelectedItem is not null;

    /// <summary>True when there is nothing to list (not signed in, not loaded, or an empty inventory).</summary>
    public bool IsEmptyState => _signedOut || _loadedAt is null || TotalCount == 0;

    public string EmptyTitle => _signedOut ? "Not signed in"
        : LoadState == DesktopLoadState.Loading ? "Loading…"
        : LoadState == DesktopLoadState.Error ? "Inventory could not be loaded"
        : _loadedAt is null ? "Inventory not loaded" : "Inventory is empty";

    /// <summary>Everything the detail pane shows about the selected row.</summary>
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

    /// <summary>Total number of distinct rows, before filtering.</summary>
    public int TotalCount => _allItems.Count;

    public string SummaryText => LoadState switch
    {
        DesktopLoadState.Error => ErrorMessage ?? "Inventory could not be loaded.",
        DesktopLoadState.Loading => "Loading inventory…",
        _ when _signedOut => $"Sign in as {_role} under Accounts to see inventory.",
        _ when _loadedAt is null => "Inventory has not been loaded.",
        _ => $"{Items.Count:N0} of {TotalCount:N0} item types" + (IsStale ? " · stale" : "")
    };

    private bool _signedOut;

    /// <summary>Replaces the item set and re-applies the filter. The live
    /// loader calls this; tests use it to drive the view model without a
    /// network.</summary>
    public void ApplySnapshot(IReadOnlyList<DesktopInventoryItem> items, DateTimeOffset? observedAt = null)
    {
        _allItems = new ObservableCollection<DesktopInventoryItem>(items.Select(item =>
            item with { KindLabel = InventoryActionPlanner.Label(Classify(item)) }));
        if (observedAt is { } at) LoadedAt = at;
        ApplyFilter();
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(SummaryText));
    }

    internal void ApplyLiveSnapshot(DesktopInventorySnapshot snapshot)
    {
        _signedOut = !snapshot.HasSession;
        ApplySnapshot(snapshot.Items, snapshot.HasSession ? snapshot.ObservedAt : null);
        LoadState = snapshot.Items.Count == 0
            ? DesktopLoadState.Empty
            : snapshot.IsStale ? DesktopLoadState.Stale : DesktopLoadState.Ready;
        IsStale = snapshot.IsStale;
    }

    /// <summary>Loads the live inventory.</summary>
    public async Task LoadAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        LoadState = DesktopLoadState.Loading;
        try
        {
            var snapshot = _provider is null
                ? new DesktopInventorySnapshot { Role = _role, Items = Array.Empty<DesktopInventoryItem>(), HasSession = false, ObservedAt = DateTimeOffset.UtcNow }
                : await _provider.LoadInventoryAsync(_role, forceRefresh, _catalog, cancellationToken)
                ;
            ApplyLiveSnapshot(snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LoadState = DesktopLoadState.Empty;
            throw;
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
        });
        IOrderedEnumerable<DesktopInventoryItem> ordered = SortKey switch
        {
            InventorySortKey.Count => SortDescending ? filtered.OrderByDescending(i => i.Quantity) : filtered.OrderBy(i => i.Quantity),
            InventorySortKey.Type => SortDescending
                ? filtered.OrderByDescending(i => i.KindLabel ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                : filtered.OrderBy(i => i.KindLabel ?? string.Empty, StringComparer.OrdinalIgnoreCase),
            _ => SortDescending ? filtered.OrderByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase) : filtered.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
        };
        var rows = (SortKey == InventorySortKey.Name ? ordered : ordered.ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)).ToArray();

        var selectedId = SelectedItem?.ItemId;
        Items.Clear();
        foreach (var item in rows) Items.Add(item);
        // A refresh or filter change rebuilds the rows; keep the same item selected (with its new
        // quantity) so the detail pane and its buttons never describe a stale row.
        SelectedItem = selectedId is null ? null : Items.FirstOrDefault(row => row.ItemId == selectedId);
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(TotalCount));
    }

    private static IReadOnlyList<DesktopInventoryCategory> BuildCategories(InventoryViewModel owner)
    {
        DesktopInventoryCategory Make(string id, string label, ItemCategory? category) => new()
        {
            Id = id,
            Label = label,
            Matches = category is null ? _ => true : item => owner.Classify(item).Matches(category.Value)
        };

        return new[]
        {
            Make(InventoryFilterCategory.AllId, "All", null),
            Make(InventoryFilterCategory.ConsumableId, "Consumables", ItemCategory.Consumable),
            Make(InventoryFilterCategory.EquipmentId, "Equipment", ItemCategory.Gear),
            Make(InventoryFilterCategory.QuestId, "Quest", ItemCategory.Quest),
            Make(InventoryFilterCategory.UncategorizedId, "Misc", ItemCategory.Other)
        };
    }
}

public enum InventorySortKey { Name, Type, Count }

/// <summary>Stable ids for the inventory categories.</summary>
public static class InventoryFilterCategory
{
    public const string AllId = "all";
    public const string ConsumableId = "consumable";
    public const string GadgetId = "gadget";
    public const string TransferableId = "transferable";
    public const string DiscardableId = "discardable";
    public const string UncategorizedId = "uncategorized";
    public const string EquipmentId = "equipment";
    public const string QuestId = "quest";
}

/// <summary>The detail pane content for the selected inventory row.</summary>
public sealed record DesktopItemDetail
{
    public required string ItemId { get; init; }
    public required string Name { get; init; }
    public required int Quantity { get; init; }
    public string? ImageUrl { get; init; }
    public required IReadOnlyList<string> Uses { get; init; }
    public long? CheapestMallPrice { get; init; }

    public string UsesText => Uses.Count == 0 ? "—" : string.Join(", ", Uses);
    public string MallPriceText => CheapestMallPrice is { } price
        ? $"{price:N0} Meat"
        : "not looked up (use Mallsearch)";
}
