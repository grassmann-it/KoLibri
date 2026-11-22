using Xunit;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Tests;

public sealed class ItemClassifierTests
{
    private static readonly Lazy<(KoLmafiaCatalog Catalog, ItemClassifier Classifier)> Shared = new(() =>
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "external", "kolmafia-data");
        var catalog = new MafiaDataLoader().Load(dir);
        return (catalog, new ItemClassifier(catalog));
    });

    private static ItemClassifier C => Shared.Value.Classifier;

    [Fact]
    public void HotWing_is_food_with_cost_and_not_a_quest_item()
    {
        var wing = C.Classify(471);
        Assert.True(wing.Known);
        Assert.True(wing.IsFood);
        Assert.False(wing.IsQuest);
        Assert.False(wing.IsUsable);
        Assert.True(wing.IsConsumable);
        Assert.Equal(ItemCategory.Consumable, wing.Category);
        Assert.Equal(1, wing.OrganCost?.Size);
        Assert.Equal("crappy", wing.OrganCost?.Quality);
        Assert.Equal(1, wing.OrganCost?.AdventuresMin);
    }

    [Fact]
    public void QuestItem_is_quest_not_consumable()
    {
        var body = C.Classify("63"); // action figure body: primary none, flag q
        Assert.True(body.IsQuest);
        Assert.False(body.IsConsumable);
        Assert.False(body.IsUsable);
        Assert.Equal(ItemCategory.Quest, body.Category);
    }

    [Fact]
    public void PurePotion_is_usable_consumable()
    {
        var gum = C.Classify("297");
        Assert.True(gum.IsUsable);
        Assert.False(gum.IsReusable);
        Assert.Equal(ItemOrgan.None, gum.Organ);
        Assert.Equal(ItemCategory.Consumable, gum.Category);
    }

    [Fact]
    public void SealTooth_keeps_both_secondary_tokens_and_is_reusable_combat()
    {
        var record = Shared.Value.Catalog.Items.Single(i => i.Id == "2");
        Assert.Equal(new[] { "combat reusable", "smith" }, record.SecondaryUse);
        var tooth = C.Classify("2");
        Assert.True(tooth.IsCombatItem);
        Assert.True(tooth.IsReusable);
        Assert.False(tooth.IsConsumable);
        Assert.True(record.Usable);
    }

    [Fact]
    public void Hat_and_accessory_are_gear_with_slot_and_requirement()
    {
        var hat = C.Classify("3");
        Assert.Equal(ItemEquipSlot.Hat, hat.Slot);
        Assert.Equal(ItemCategory.Gear, hat.Category);
        Assert.True(hat.Requirement.Known);
        Assert.False(hat.Requirement.HasRequirement);
        Assert.Equal(10, hat.Requirement.Power);

        var club = C.Classify("1");
        Assert.Equal(ItemEquipSlot.Weapon, club.Slot);
        Assert.Equal("Mus", club.Requirement.Stat);

        var bling = C.Classify("96");
        Assert.Equal(ItemEquipSlot.Accessory, bling.Slot);
        Assert.True(bling.IsGear);
    }

    [Fact]
    public void FamiliarGear_is_gear_but_flagged_familiar()
    {
        var needle = C.Classify("848");
        Assert.True(needle.IsFamiliarEquipment);
        Assert.True(needle.IsGear);
        Assert.False(needle.IsConsumable);
    }

    [Fact]
    public void UnknownId_is_explicitly_unknown()
    {
        var unknown = C.Classify("99999999");
        Assert.False(unknown.Known);
        Assert.Equal(ItemCategory.Other, unknown.Category);
        Assert.False(unknown.Requirement.Known);
        Assert.Null(unknown.OrganCost);
    }

    [Fact]
    public void NoneUse_is_not_usable()
    {
        Assert.False(Shared.Value.Catalog.Items.Single(i => i.Id == "63").Usable);
    }

    [Fact]
    public void Consumables_are_not_limited_to_quest_flags()
    {
        var items = Shared.Value.Catalog.Items;
        var organCount = items.Count(i => i.PrimaryUse.Any(t => t is "food" or "drink" or "spleen"));
        Assert.Equal(2024, organCount);
        var consumables = C.All.Where(c => c.IsConsumable).ToArray();
        Assert.True(consumables.Count(c => c.Organ != ItemOrgan.None) == organCount);
        // The old filter (accessFlags contains "q") matched only quest items.
        Assert.DoesNotContain(consumables, c => c.IsQuest && c.Organ == ItemOrgan.None);
        var oldFilterOrgans = items.Count(i => i.AccessFlags.Contains("q")
            && i.PrimaryUse.Any(t => t is "food" or "drink" or "spleen"));
        Assert.True(oldFilterOrgans < organCount / 10);
    }

    [Fact]
    public void Outfits_and_organ_files_are_imported()
    {
        var catalog = Shared.Value.Catalog;
        Assert.True(catalog.Outfits.Count > 100);
        var bugbear = catalog.Outfits.Single(o => o.Name == "Bugbear Costume");
        Assert.Equal(2, bugbear.PartIds.Count(id => id is not null));
        Assert.Contains(catalog.Organs, o => o.Kind == "drink");
        Assert.Contains(catalog.Organs, o => o.Kind == "spleen");
    }
}
