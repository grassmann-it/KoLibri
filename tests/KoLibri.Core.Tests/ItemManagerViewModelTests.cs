using KoLibri.Desktop;
using Xunit;

namespace KoLibri.Core.Tests;

/// <summary>
/// The item manager is the screen a KoL player lives in, so the live path
/// matters: the provider has to produce rows the view can bind to, with
/// the catalog data attached.
/// </summary>
public sealed class ItemManagerViewModelTests
{
    [Fact]
    public void Search_filters_by_name_and_id()
    {
        var viewModel = new ItemManagerViewModel();
        viewModel.ApplySnapshot(new[]
        {
            new DesktopInventoryItem { ItemId = "10881", Name = "11-leaf clover", Quantity = 3 },
            new DesktopInventoryItem { ItemId = "8763", Name = "BACON", Quantity = 455 }
        });
        viewModel.SearchText = "clover";
        Assert.Single(viewModel.Items);
        Assert.Equal("11-leaf clover", viewModel.Items[0].Name);
    }

    [Fact]
    public void Category_filter_uses_the_access_flags()
    {
        var viewModel = new ItemManagerViewModel();
        viewModel.ApplySnapshot(new[]
        {
            new DesktopInventoryItem { ItemId = "1", Name = "a", Quantity = 1, AccessFlags = new[] { "q" } },
            new DesktopInventoryItem { ItemId = "2", Name = "b", Quantity = 1, AccessFlags = new[] { "g" } }
        });
        viewModel.SelectedCategory = viewModel.Categories
            .First(c => c.Id == InventoryFilterCategory.ConsumableId);
        Assert.Single(viewModel.Items);
        Assert.Equal("a", viewModel.Items[0].Name);
    }

    [Fact]
    public void Actions_follow_the_selected_item()
    {
        var viewModel = new ItemManagerViewModel();
        viewModel.ApplySnapshot(new[]
        {
            new DesktopInventoryItem
            {
                ItemId = "10881", Name = "11-leaf clover", Quantity = 3,
                AccessFlags = new[] { "q", "t" }
            }
        });
        viewModel.SelectedItem = viewModel.Items[0];
        viewModel.RefreshActions();
        Assert.Contains(viewModel.Actions, a => a.Id == "use");
        Assert.Contains(viewModel.Actions, a => a.Id == "transfer");
        Assert.Contains(viewModel.Actions, a => a.Id == "sell");
    }

    [Fact]
    public void No_selection_means_no_actions()
    {
        var viewModel = new ItemManagerViewModel();
        viewModel.RefreshActions();
        Assert.Empty(viewModel.Actions);
    }

    [Fact]
    public void Detail_shows_the_item_data()
    {
        var viewModel = new ItemManagerViewModel();
        viewModel.ApplySnapshot(new[]
        {
            new DesktopInventoryItem
            {
                ItemId = "10881", Name = "11-leaf clover", Quantity = 3,
                Uses = new[] { "cook" }, CheapestMallPrice = 25_000
            }
        });
        viewModel.SelectedItem = viewModel.Items[0];
        Assert.NotNull(viewModel.Detail);
        Assert.Equal("11-leaf clover", viewModel.Detail!.Name);
        Assert.Equal("cook", viewModel.Detail.UsesText);
        Assert.Contains("25,000", viewModel.Detail.MallPriceText);
    }
}
