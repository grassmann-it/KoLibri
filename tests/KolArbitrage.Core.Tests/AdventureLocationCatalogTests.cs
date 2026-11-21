using Xunit;
using KolArbitrage.Core.Spiel;

namespace KolArbitrage.Core.Tests;

public sealed class AdventureLocationCatalogTests
{
    private static readonly string FixtureRoot = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "Adventure");

    [Fact]
    public void FixtureKeepsStableIdSnarfblatZoneRequirementsAndTurnCostSeparate()
    {
        var path = Path.Combine(FixtureRoot, "location-catalog.json");
        var catalog = AdventureLocationCatalog.LoadJson(path);

        Assert.Equal(2, catalog.Count);
        var woods = catalog.GetByLocationId("HAUNTED-WOODS");
        Assert.Equal("haunted-woods", woods.LocationId);
        Assert.Equal(56, woods.Snarfblat);
        Assert.Equal("Haunted Woods", woods.Zone);
        Assert.Equal(1, woods.TurnCost);
        Assert.Contains("level>=5", woods.Requirements);
        Assert.Contains("quest=woods", woods.Requirements);
        Assert.Single(woods.RequirementContracts);
        Assert.Equal(AdventureLocationRequirementKind.Quest, woods.RequirementContracts[0].Kind);
        Assert.Equal(AdventureLocationUnlockState.Unlocked, woods.UnlockState);
        Assert.True(woods.IsUnlocked);
        Assert.Single(woods.EncounterMetadata);
        Assert.Equal("skeleton", woods.EncounterMetadata[0].MonsterId);

        var bySnarfblat = catalog.GetBySnarfblat(80);
        Assert.Equal("hidden-city", bySnarfblat.LocationId);
        Assert.Equal(2, bySnarfblat.Turns);
    }

    [Fact]
    public void CatalogRejectsDuplicateSnarfblatAndSupportsCaseInsensitiveStableIds()
    {
        var catalog = new AdventureLocationCatalog()
            .Add(new AdventureLocation("one", 101, "Zone One"));

        Assert.True(catalog.TryGet("ONE", out var found));
        Assert.Equal("one", found.LocationId);
        Assert.Throws<ArgumentException>(() => catalog.Add(
            new AdventureLocation("two", 101, "Zone Two")));
    }

    [Fact]
    public void TypedLocationRequestNormalizesIntoExistingRequestShape()
    {
        var request = new AdventureLocationRunRequest
        {
            RunId = "  location-run ",
            Location = new AdventureLocation
            {
                LocationId = "  hidden-city ",
                Snarfblat = 80,
                Zone = " The Hidden City ",
                TurnCost = 2
            }
        }.Normalize();

        var existing = request.ToAdventureRunRequest().Normalize();
        Assert.Equal("location-run", existing.RunId);
        Assert.Equal("hidden-city", existing.AdventureId);
        Assert.Equal(80, request.Location.Snarfblat);
        Assert.Equal("The Hidden City", request.Location.Zone);
    }

    [Fact]
    public void ExistingRunRequestCanCarryOptionalLocationWithoutChangingAdventureIdCallers()
    {
        var request = new AdventureRunRequest
        {
            RunId = "legacy",
            AdventureId = "legacy-adventure",
            Location = new AdventureLocation
            {
                LocationId = "catalog-key",
                Snarfblat = 123,
                Zone = "Zone"
            }
        }.Normalize();

        Assert.Equal("legacy-adventure", request.AdventureId);
        Assert.Equal(123, request.TargetLocation!.Snarfblat);
    }

    [Fact]
    public void LocationRouteUsesSnarfblatInsteadOfStableLocationKey()
    {
        var location = new AdventureLocation
        {
            LocationId = "hidden-city",
            Snarfblat = 80,
            Zone = "The Hidden City"
        };

        Assert.Equal("adventure.php?snarfblat=80", KoLRoutes.AdventureAtLocation(location));
    }

    [Fact]
    public void CatalogJsonRoundTripsTypedLocationFields()
    {
        var original = new AdventureLocationCatalog().Add(new AdventureLocation
        {
            LocationId = "roundtrip",
            Snarfblat = 321,
            Zone = "Zone",
            Requirements = new[] { "level>=10" },
            RequirementContracts = new[]
            {
                new AdventureLocationRequirement(
                    "level", "10", AdventureLocationRequirementKind.Level, minimum: 10)
            },
            TurnCost = 3,
            UnlockState = AdventureLocationUnlockState.RequirementsMissing
        });

        var copy = AdventureLocationCatalog.FromJson(original.ToJson());
        var location = copy.Get("roundtrip");
        Assert.Equal(321, location.Snarfblat);
        Assert.Equal(3, location.TurnCost);
        Assert.Equal(AdventureLocationUnlockState.RequirementsMissing, location.UnlockState);
        Assert.Contains(location.RequirementContracts,
            requirement => requirement.Kind == AdventureLocationRequirementKind.Level
                && requirement.Minimum == 10);
    }
}
