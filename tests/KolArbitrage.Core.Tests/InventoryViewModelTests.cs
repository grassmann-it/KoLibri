using KoLibri.Desktop;
using Xunit;

namespace KolArbitrage.Core.Tests;

/// <summary>
/// Tests for the inventory browser. The search, the category filter and
/// the detail pane are what turn the raw item list into something you can
/// work with — KoLmafia-style navigation.
/// </summary>
public sealed class InventoryViewModelTests
{
    private static InventoryViewModel Build(params DesktopInventoryItem[] items)
    {
        var viewModel = new InventoryViewModel();
        viewModel.ApplySnapshot(items, DateTimeOffset.UnixEpoch);
        return viewModel;
    }

    private static DesktopInventoryItem Item(
        string id, string name, int quantity = 1, params string[] flags)
        => new()
        {
            ItemId = id,
            Name = name,
            Quantity = quantity,
            AccessFlags = flags
        };

    [Fact]
    public void Search_filters_by_name()
    {
        var viewModel = Build(
            Item("10881", "11-leaf clover"),
            Item("8763", "BACON"),
            Item("42", "hermit permit"));
        viewModel.SearchText = "clover";
        Assert.Single(viewModel.Items);
        Assert.Equal("11-leaf clover", viewModel.Items[0].Name);
    }

    [Fact]
    public void Search_is_case_insensitive()
    {
        var viewModel = Build(Item("8763", "BACON"));
        viewModel.SearchText = "bacon";
        Assert.Single(viewModel.Items);
    }

    [Fact]
    public void Search_filters_by_item_id()
    {
        var viewModel = Build(
            Item("10881", "11-leaf clover"),
            Item("8763", "BACON"));
        viewModel.SearchText = "10881";
        Assert.Single(viewModel.Items);
        Assert.Equal("11-leaf clover", viewModel.Items[0].Name);
    }

    [Fact]
    public void Search_combines_with_the_category_filter()
    {
        var viewModel = Build(
            Item("1", "consumable thing", 1, "q"),
            Item("2", "gadget thing", 1, "g"),
            Item("3", "consumable other", 1, "q"));
        viewModel.SelectedCategory = viewModel.Categories
            .First(c => c.Id == InventoryFilterCategory.ConsumableId);
        viewModel.SearchText = "other";
        Assert.Single(viewModel.Items);
        Assert.Equal("consumable other", viewModel.Items[0].Name);
    }

    [Fact]
    public void Category_consumable_keeps_only_flagged_items()
    {
        var viewModel = Build(
            Item("1", "a", 1, "q"),
            Item("2", "b", 1, "g"),
            Item("3", "c", 1, "t", "d"));
        viewModel.SelectedCategory = viewModel.Categories
            .First(c => c.Id == InventoryFilterCategory.ConsumableId);
        Assert.Single(viewModel.Items);
        Assert.Equal("a", viewModel.Items[0].Name);
    }

    [Fact]
    public void Category_uncategorized_keeps_items_without_flags()
    {
        var viewModel = Build(
            Item("1", "flagged", 1, "q"),
            Item("2", "unflagged"));
        viewModel.SelectedCategory = viewModel.Categories
            .First(c => c.Id == InventoryFilterCategory.UncategorizedId);
        Assert.Single(viewModel.Items);
        Assert.Equal("unflagged", viewModel.Items[0].Name);
    }

    [Fact]
    public void Items_are_sorted_by_name()
    {
        var viewModel = Build(
            Item("3", "zebra"),
            Item("1", "apple"),
            Item("2", "mango"));
        Assert.Equal(new[] { "apple", "mango", "zebra" },
            viewModel.Items.Select(i => i.Name).ToArray());
    }

    [Fact]
    public void Selecting_a_row_fills_the_detail_pane()
    {
        var viewModel = Build(Item("10881", "11-leaf clover", 21, "q"));
        viewModel.SelectedItem = viewModel.Items[0];
        Assert.True(viewModel.HasSelection);
        Assert.NotNull(viewModel.Detail);
        Assert.Equal("11-leaf clover", viewModel.Detail!.Name);
        Assert.Equal(21, viewModel.Detail.Quantity);
    }

    [Fact]
    public void Clearing_the_selection_empties_the_detail_pane()
    {
        var viewModel = Build(Item("10881", "11-leaf clover"));
        viewModel.SelectedItem = viewModel.Items[0];
        viewModel.SelectedItem = null;
        Assert.False(viewModel.HasSelection);
        Assert.Null(viewModel.Detail);
    }

    [Fact]
    public void Summary_counts_filtered_and_total()
    {
        var viewModel = Build(
            Item("1", "apple"),
            Item("2", "mango"),
            Item("3", "zebra"));
        viewModel.SearchText = "a";
        Assert.Contains("of 3", viewModel.SummaryText);
    }
}
