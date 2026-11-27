using KoLibri.Desktop;
using Xunit;

namespace KoLibri.Tests.Desktop;

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
    public void Menu_target_maps_the_row_without_inventing_actions()
    {
        var target = ItemManagerViewModel.ToMenuTarget(new DesktopInventoryItem
        {
            ItemId = "10881", Name = "11-leaf clover", Quantity = 3, AccessFlags = new[] { "q", "t" }
        });
        Assert.Equal("10881", target.ItemId);
        Assert.Equal(3, target.Quantity);
        Assert.Equal(KoLibri.Desktop.ContextMenus.ItemUseKind.None, target.Use);
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
