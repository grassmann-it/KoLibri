using System.Collections.ObjectModel;
using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;

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
    private readonly DesktopLiveDataProvider _provider;
    private readonly AccountRole _role;
    private readonly KoLmafiaCatalog? _catalog;

    private ObservableCollection<DesktopInventoryItem> _allItems = new();
    private DesktopInventoryItem? _selectedItem;
    private string _searchText = string.Empty;
    private string _selectedCategoryId = InventoryFilterCategory.AllId;
    private DateTimeOffset? _loadedAt;

    public InventoryViewModel(
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

    public AccountRole Role => _role;

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
            if (!SetProperty(ref _selectedItem, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(Detail));
        }
    }

    /// <summary>The filtered, sorted rows.</summary>
    public ObservableCollection<DesktopInventoryItem> Items { get; } = new();

    public ObservableCollection<DesktopInventoryCategory> Categories { get; }

    public bool HasSelection => SelectedItem is not null;

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
        _allItems = new ObservableCollection<DesktopInventoryItem>(items);
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
            var snapshot = await _provider
                .LoadInventoryAsync(_role, forceRefresh, _catalog, cancellationToken)
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
                Label = "Alle",
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

/// <summary>Stable ids for the inventory categories.</summary>
public static class InventoryFilterCategory
{
    public const string AllId = "all";
    public const string ConsumableId = "consumable";
    public const string GadgetId = "gadget";
    public const string TransferableId = "transferable";
    public const string DiscardableId = "discardable";
    public const string UncategorizedId = "uncategorized";
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
        : "not listed";
}
