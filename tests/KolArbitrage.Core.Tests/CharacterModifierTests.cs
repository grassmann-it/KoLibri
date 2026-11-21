using KolArbitrage.Core.Data;
using KolArbitrage.Core.Integration;
using Xunit;

namespace KolArbitrage.Core.Tests;

public sealed class CharacterModifierTests
{
    [Fact]
    public void Combines_supported_equipment_and_effect_modifiers_from_the_catalog()
    {
        var catalog = new MafiaDataLoader().Load(Path.Combine(AppContext.BaseDirectory, "external", "kolmafia-data"));
        var status = new KoLStatusSnapshot("Fixture", 10, 0, 4, DateTimeOffset.UnixEpoch)
        {
            Equipment = new Dictionary<string, string> { ["acc1"] = "556" },
            Effects = new[] { "Empathy" },
            TimedEffects = new[] { new KoLTimedEffect("Empathy", 7) }
        };
        var values = CharacterModifierCalculator.Calculate(catalog, status);

        Assert.Equal(1m, values.Values["Stench Resistance"]);
        Assert.Equal(5m, values.Values["Familiar Weight"]);
    }

    [Fact]
    public void Slot_preview_compares_owned_candidates_against_the_equipped_item()
    {
        var catalog = new MafiaDataLoader().Load(Path.Combine(AppContext.BaseDirectory, "external", "kolmafia-data"));
        var status = new KoLStatusSnapshot("Fixture", 10, 0, 4, DateTimeOffset.UnixEpoch)
        {
            Equipment = new Dictionary<string, string> { ["acc1"] = "0" }
        };
        var snapshot = new CharacterActionSnapshot(status, new KoLInventorySnapshot(
            new[] { new KoLInventoryItem("556", "Pine-Fresh air freshener", 1) }, DateTimeOffset.UnixEpoch));
        var suggestion = Assert.Single(CharacterModifierCalculator.CompareSlot(
            catalog, snapshot, CharacterEquipmentSlot.Accessory1, "Stench Resistance"));
        Assert.Equal("556", suggestion.ItemId);
        Assert.Equal(1m, suggestion.Improvement);

        var alreadyEquipped = snapshot with { Status = status with
        {
            Equipment = new Dictionary<string, string> { ["acc1"] = "556" }
        }};
        Assert.Empty(CharacterModifierCalculator.CompareSlot(
            catalog, alreadyEquipped, CharacterEquipmentSlot.Accessory1, "Stench Resistance"));
        Assert.Empty(CharacterModifierCalculator.CompareSlot(
            catalog, snapshot, CharacterEquipmentSlot.Hat, "Stench Resistance"));
    }

    [Fact]
    public void Dynamic_and_quoted_text_do_not_contribute_fictitious_flat_bonuses()
    {
        var catalog = new MafiaDataLoader().Load(Path.Combine(AppContext.BaseDirectory, "external", "kolmafia-data"));
        var observed = new KoLStatusSnapshot("Fixture", 10, 0, 4, DateTimeOffset.UnixEpoch)
        {
            Effects = new[] { "Fixture Effect" },
            FamiliarId = "999999"
        };
        catalog = catalog with
        {
            Modifiers = new[] { new MafiaModifierRecord
            {
                Header = "Effect", Description = "Fixture Effect",
                Extras = new[] { """Familiar Effect: "foo, Item Drop: +99", Meat Drop: [if(1,40,20)], Initiative: +15""" }
            } }
        };

        var result = CharacterModifierCalculator.Calculate(catalog, observed);
        Assert.False(result.Values.ContainsKey("Item Drop"));
        Assert.False(result.Values.ContainsKey("Meat Drop"));
        Assert.Equal(15m, result.Values["Initiative"]);
        Assert.Contains("Fixture Effect: Meat Drop", result.Unresolved);
        Assert.Contains("Familiar species for 999999", result.Unresolved);
    }
}
