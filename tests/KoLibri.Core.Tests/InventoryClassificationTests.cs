using KoLibri.Core.Spiel;
using KoLibri.Desktop;
using KoLibri.Desktop.ContextMenus;
using Xunit;

namespace KoLibri.Core.Tests;

internal static class InventoryTestCatalog
{
    private static readonly Lazy<(KoLmafiaCatalog Catalog, ItemClassifier Classifier)> Shared = new(() =>
    {
        var catalog = new MafiaDataLoader().Load(Path.Combine(AppContext.BaseDirectory, "external", "kolmafia-data"));
        return (catalog, new ItemClassifier(catalog));
    });

    public static ItemClassifier Classifier => Shared.Value.Classifier;

    public static DesktopInventoryItem Item(string id, string name, int quantity = 1)
        => new() { ItemId = id, Name = name, Quantity = quantity };

    public static InventoryViewModel Build(params DesktopInventoryItem[] items)
    {
        var viewModel = new InventoryViewModel(catalog: Shared.Value.Catalog, classifier: Shared.Value.Classifier);
        viewModel.ApplySnapshot(items, DateTimeOffset.UnixEpoch);
        return viewModel;
    }
}

public sealed class InventoryClassificationTests
{
    private static InventoryViewModel Sample() => InventoryTestCatalog.Build(
        InventoryTestCatalog.Item("471", "hot wing", 12),
        InventoryTestCatalog.Item("41", "ice-cold Sir Schlitz", 2),
        InventoryTestCatalog.Item("14", "moxie weed", 4),
        InventoryTestCatalog.Item("297", "tamarind-flavored chewing gum", 1),
        InventoryTestCatalog.Item("1", "seal-clubbing club"),
        InventoryTestCatalog.Item("306", "Knob Goblin harem veil"),
        InventoryTestCatalog.Item("63", "action figure body"),
        InventoryTestCatalog.Item("25", "meat paste", 340));

    private static string[] Names(InventoryViewModel vm, string categoryId)
    {
        vm.SelectedCategory = vm.Categories.First(c => c.Id == categoryId);
        return vm.Items.Select(i => i.Name).OrderBy(n => n).ToArray();
    }

    [Fact]
    public void Consumables_hold_food_booze_spleen_and_potions_but_not_quest_items()
    {
        var names = Names(Sample(), InventoryFilterCategory.ConsumableId);
        Assert.Contains("hot wing", names);
        Assert.Contains("ice-cold Sir Schlitz", names);
        Assert.Contains("moxie weed", names);
        Assert.Contains("tamarind-flavored chewing gum", names);
        Assert.DoesNotContain("action figure body", names);
        Assert.DoesNotContain("seal-clubbing club", names);
    }

    [Fact]
    public void Quest_flag_means_quest_not_consumable()
    {
        var vm = Sample();
        Assert.Equal(new[] { "action figure body" }, Names(vm, InventoryFilterCategory.QuestId));
        Assert.Equal(ItemUseKind.None, vm.ToMenuTarget(vm.Items.First(i => i.Name == "action figure body")).Use);
    }

    [Fact]
    public void Equipment_and_misc_are_separate()
    {
        var vm = Sample();
        Assert.Equal(new[] { "Knob Goblin harem veil", "seal-clubbing club" }, Names(vm, InventoryFilterCategory.EquipmentId));
        Assert.Equal(new[] { "meat paste" }, Names(vm, InventoryFilterCategory.UncategorizedId));
    }

    [Fact]
    public void Menu_target_offers_the_use_kind_of_the_organ_and_equip_slots_only_for_gear()
    {
        var vm = Sample();
        ItemMenuTarget T(string name) => vm.ToMenuTarget(vm.Items.First(i => i.Name == name));
        Assert.Equal(ItemUseKind.Eat, T("hot wing").Use);
        Assert.Equal(ItemUseKind.Drink, T("ice-cold Sir Schlitz").Use);
        Assert.Equal(ItemUseKind.Chew, T("moxie weed").Use);
        Assert.Equal(ItemUseKind.Use, T("tamarind-flavored chewing gum").Use);
        Assert.Equal(ItemUseKind.None, T("meat paste").Use);
        Assert.Equal(new[] { "Hat" }, T("Knob Goblin harem veil").EquipSlots);
        Assert.Empty(T("hot wing").EquipSlots!);
    }

    [Fact]
    public void Rows_carry_a_readable_kind_label()
    {
        var vm = Sample();
        Assert.Equal("Food", vm.Items.First(i => i.Name == "hot wing").KindLabel);
        Assert.Equal("Booze", vm.Items.First(i => i.Name == "ice-cold Sir Schlitz").KindLabel);
        Assert.Equal("Quest", vm.Items.First(i => i.Name == "action figure body").KindLabel);
        Assert.Equal("Equipment", vm.Items.First(i => i.Name == "seal-clubbing club").KindLabel);
    }

    [Fact]
    public void Item_menu_without_actions_keeps_placeholders_and_with_actions_calls_them()
    {
        var calls = new List<string>();
        var actions = new Recorder(calls);
        var target = new ItemMenuTarget("471", "hot wing", 12, ItemUseKind.Eat);
        var items = new ItemContextMenu(null!, actions).Build(target);
        var eat = items.First(i => i.Label == "Eat");
        Assert.Equal(new[] { "Eat 1", "Eat 5", "Eat all (12)" }, eat.Submenu!.Select(i => i.Label).ToArray());
        eat.Submenu![1].Invoke();
        eat.Submenu![2].Invoke();
        Assert.Equal(new[] { "Eat:5", "Eat:12" }, calls);
    }

    private sealed class Recorder(List<string> calls) : IItemMenuActions
    {
        public void Consume(ItemMenuTarget item, ItemUseKind kind, int quantity) => calls.Add($"{kind}:{quantity}");
        public void Equip(ItemMenuTarget item, string slot) => calls.Add($"Equip:{slot}");
    }
}

public sealed class InventoryOverfillTests
{
    private static ItemClassification Wing => InventoryTestCatalog.Classifier.Classify("471");
    private static ItemClassification Beer => InventoryTestCatalog.Classifier.Classify("41");

    [Fact]
    public void Fits_when_enough_room_remains()
    {
        var check = InventoryActionPlanner.CheckOverfill(Wing, 3, new OrganState(10, 0, 0, 15, 14, 15));
        Assert.True(check.Known);
        Assert.False(check.Overfills);
        Assert.Equal(5, check.Remaining);
        Assert.Contains("would remain", check.Message);
    }

    [Fact]
    public void Warns_with_remaining_room_when_over_the_limit()
    {
        var check = InventoryActionPlanner.CheckOverfill(Wing, 6, new OrganState(12, 0, 0, 15, 14, 15));
        Assert.True(check.Overfills);
        Assert.Equal(3, check.Remaining);
        Assert.Contains("only 3 of 15 fullness remain", check.Message);
    }

    [Fact]
    public void Drink_uses_inebriety_limit()
    {
        var size = Beer.OrganCost!.Size!.Value;
        var check = InventoryActionPlanner.CheckOverfill(Beer, 1, new OrganState(0, 14 - size + 1, 0, 15, 14, 15));
        Assert.True(check.Overfills);
        Assert.Contains("inebriety", check.Message);
    }

    [Fact]
    public void Unknown_current_fill_never_blocks_but_says_so()
    {
        var check = InventoryActionPlanner.CheckOverfill(Wing, 99, OrganState.Unknown);
        Assert.False(check.Known);
        Assert.False(check.Overfills);
        Assert.Contains("unknown", check.Message);
    }

    [Fact]
    public void Non_organ_items_have_no_check_and_max_fitting_counts_whole_units()
    {
        var paste = InventoryTestCatalog.Classifier.Classify("25");
        Assert.False(InventoryActionPlanner.CheckOverfill(paste, 5, OrganState.Unknown).Overfills);
        Assert.Equal(5, InventoryActionPlanner.MaxFitting(Wing, new OrganState(10, 0, 0, 15, 14, 15)));
    }

    [Fact]
    public void Organ_summary_shows_remaining_limits()
        => Assert.Equal("Fullness 3/15 · Inebriety ?/14 · Spleen 0/15", new OrganState(3, null, 0, 15, 14, 15).Summary);
}
