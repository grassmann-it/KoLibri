using KoLibri.Desktop;
using Xunit;

namespace KoLibri.Tests.Desktop;

/// <summary>The location picker of the Adventure tab: search, zones, favorites, recent, keyboard, access. No UI, no requests.</summary>
public sealed class LocationPickerViewModelTests
{
    private static readonly EncounterBrowserLocationRow Skeleton = Row("adventure=439:The Skeleton Store", "The Skeleton Store", "Town", 439);
    private static readonly EncounterBrowserLocationRow Nook = Row("adventure=264:The Defiled Nook", "The Defiled Nook", "Cyrpt", 264);
    private static readonly EncounterBrowserLocationRow Cranny = Row("adventure=262:The Defiled Cranny", "The Defiled Cranny", "Cyrpt", 262);
    private static readonly EncounterBrowserLocationRow Spooky = Row("adventure=15:The Spooky Forest", "The Spooky Forest", "Woods", 15);
    private static readonly EncounterBrowserLocationRow Special = Row("special:Daily Dungeon", "The Daily Dungeon", "Town", 0);

    private static EncounterBrowserLocationRow Row(string id, string name, string zone, int snarfblat)
        => new(id, name, zone, snarfblat, 1, 0, 0, "skeletons everywhere");

    private static LocationPickerViewModel Picker(IEnumerable<string>? favorites = null, IEnumerable<string>? recent = null)
        => new(new[] { Skeleton, Nook, Cranny, Spooky, Special }, favorites, recent);

    private static IEnumerable<EncounterBrowserLocationRow> Locations(LocationPickerViewModel p)
        => p.Entries.Where(e => e.IsLocation).Select(e => e.Location!);

    [Fact]
    public void StartsWithClosedZonesInAlphabeticalOrder()
    {
        var p = Picker();
        Assert.All(p.Entries, e => Assert.True(e.IsHeader));
        Assert.Equal(new[] { "Cyrpt", "Town", "Woods" }, p.Entries.Select(e => e.Label));
        Assert.Equal(2, p.Entries.First(e => e.Label == "Town").Count);
        Assert.Null(p.Selected);
    }

    [Fact]
    public void PartialCaseInsensitiveSearchFindsSkeletonStore()
    {
        var p = Picker();
        p.SearchText = "SKEL";
        Assert.Equal(new[] { Skeleton }, Locations(p));
        // The description is not searched: "skeletons everywhere" is on every fixture row.
        Assert.Equal(1, p.MatchCount);
        Assert.Equal("Town", p.Entries[0].Label);
        Assert.True(p.Entries[0].IsExpanded);
        Assert.Same(Skeleton, p.SelectedLocation); // typing highlights the first hit
        Assert.Equal("· 439", p.Entries[1].Detail);
        Assert.Equal("The Skeleton Store · 439", p.Entries[1].AccessibleName);
    }

    [Theory]
    [InlineData("439", "The Skeleton Store")]
    [InlineData("#264", "The Defiled Nook")]
    [InlineData("cyrpt cran", "The Defiled Cranny")]
    [InlineData("  spooky   for ", "The Spooky Forest")]
    public void EveryWordMatchesNameZoneOrNumber(string search, string expected)
    {
        var p = Picker();
        p.SearchText = search;
        Assert.Equal(expected, Assert.Single(Locations(p)).DisplayName);
    }

    [Fact]
    public void NoMatchShowsMessageAndClearingRestoresZones()
    {
        var p = Picker();
        p.SearchText = "zzz";
        Assert.Empty(p.Entries);
        Assert.Equal("No location matches \"zzz\".", p.EmptyMessage);
        p.SearchText = "";
        Assert.Null(p.EmptyMessage);
        Assert.Equal(3, p.Entries.Count);
    }

    [Fact]
    public void ZonesOpenAndCloseOutsideASearch()
    {
        var p = Picker();
        p.ToggleZone("Cyrpt");
        Assert.Equal(new[] { Cranny, Nook }, Locations(p)); // sorted without "The"
        Assert.Equal("▾", p.Entries[0].Chevron);
        p.ToggleZone("Cyrpt");
        Assert.Empty(Locations(p));
    }

    [Fact]
    public void FavoritesAndRecentComeFirstAndFollowTheSearch()
    {
        var p = Picker(favorites: new[] { Spooky.LocationId }, recent: new[] { Skeleton.LocationId, "adventure=1:Gone" });
        Assert.Equal(new[] { LocationPickerViewModel.FavoritesSection, LocationPickerViewModel.RecentSection, "Cyrpt", "Town", "Woods" },
            p.Entries.Where(e => e.IsHeader).Select(e => e.Label));
        var fav = p.Entries[1];
        Assert.Same(Spooky, fav.Location);
        Assert.True(fav.IsFavorite);
        Assert.Equal("★", fav.Star);
        Assert.Equal("· 15 · Woods", fav.Detail); // zone shown in the top sections
        Assert.Equal(new[] { Skeleton.LocationId }, p.Recent); // unknown ids are dropped

        p.SearchText = "skel";
        Assert.Equal(new[] { LocationPickerViewModel.RecentSection, "Town" }, p.Entries.Where(e => e.IsHeader).Select(e => e.Label));
    }

    [Fact]
    public void ToggleFavoriteRaisesListsChangedAndStarsEveryCopy()
    {
        var p = Picker();
        var changes = 0;
        p.ListsChanged += (_, _) => changes++;
        p.SearchText = "skel";
        p.ToggleFavorite(Skeleton.LocationId);
        Assert.Equal(new[] { Skeleton.LocationId }, p.Favorites);
        Assert.All(p.Entries.Where(e => e.IsLocation), e => Assert.True(e.IsFavorite));
        Assert.Equal(LocationPickerViewModel.FavoritesSection, p.Entries[0].Label);
        p.ToggleFavorite(Skeleton.LocationId);
        Assert.Empty(p.Favorites);
        p.ToggleFavorite("not-a-location");
        Assert.Equal(2, changes);
    }

    [Fact]
    public void RecentKeepsTheLastFiveNewestFirstWithoutDuplicates()
    {
        var rows = Enumerable.Range(1, 8).Select(i => Row($"adventure={i}:Zone {i}", $"Zone {i}", "Test", i)).ToArray();
        var p = new LocationPickerViewModel(rows);
        foreach (var r in rows) p.MarkUsed(r.LocationId);
        p.MarkUsed(rows[5].LocationId);
        Assert.Equal(new[] { 6, 8, 7, 5, 4 }.Select(i => $"adventure={i}:Zone {i}"), p.Recent);
        Assert.Equal(LocationPickerViewModel.RecentLimit, p.Recent.Count);
    }

    [Fact]
    public void ArrowsSkipHeadersAndEnterChooses()
    {
        var p = Picker(favorites: new[] { Nook.LocationId });
        p.SearchText = "defiled";
        // Favorites: Nook; Cyrpt: Cranny, Nook.
        Assert.Same(Nook, p.SelectedLocation);
        p.Move(+1);
        Assert.Same(Cranny, p.SelectedLocation);
        Assert.Equal("Cyrpt", p.Selected!.Section);
        p.Move(+1);
        p.Move(+1); // stays on the last row
        Assert.Same(Nook, p.SelectedLocation);
        Assert.Equal("Cyrpt", p.Selected!.Section);
        p.Move(-1);
        Assert.Same(Cranny, p.SelectedLocation);

        EncounterBrowserLocationRow? chosen = null;
        p.LocationChosen += (_, r) => chosen = r;
        Assert.Same(Cranny, p.Choose());
        Assert.Same(Cranny, chosen);
    }

    [Fact]
    public void HeadersCannotBeSelectedAndChooseWithoutSelectionDoesNothing()
    {
        var p = Picker();
        p.Selected = p.Entries[0];
        Assert.Null(p.Selected);
        Assert.Null(p.Choose());
        p.Move(+1); // all zones closed: no location rows
        Assert.Null(p.Selected);
    }

    [Fact]
    public void SelectByIdOpensTheZoneAndClearsAHidingSearch()
    {
        var p = Picker();
        p.SearchText = "spooky";
        Assert.True(p.Select(Nook.LocationId));
        Assert.Equal(string.Empty, p.SearchText);
        Assert.Same(Nook, p.SelectedLocation);
        Assert.True(p.IsZoneExpanded("Cyrpt"));
        Assert.False(p.Select("missing"));
    }

    [Fact]
    public void KnownLockedAccessGraysOutButKeepsTheLocation()
    {
        var p = Picker();
        p.SearchText = "skel";
        Assert.False(p.Entries[1].IsLocked);
        p.SetAccess(439, accessible: false, "You need the Skeleton Store office key.");
        var entry = p.Entries[1];
        Assert.Same(Skeleton, entry.Location);
        Assert.True(entry.IsLocked);
        Assert.Equal(0.45, entry.RowOpacity);
        Assert.Equal("You need the Skeleton Store office key.", entry.Tip);
        Assert.Same(Skeleton, p.SelectedLocation); // selection survives the rebuild
        p.ClearAccess();
        Assert.False(p.Entries[1].IsLocked);
    }

    [Fact]
    public void SpecialRouteLocationsHaveNoNumber()
    {
        var p = Picker();
        p.SearchText = "daily";
        Assert.Equal("· special route", p.Entries[1].Detail);
        Assert.Equal("The Daily Dungeon", p.Entries[1].AccessibleName);
    }

    [Fact]
    public void FavoritesAndRecentRoundTripThroughTheSettingsFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kolibri-picker-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DesktopSettingsStore(Path.Combine(dir, "s.json"));
            var s = new DesktopSettings();
            s.FavoriteLocations.Add(Skeleton.LocationId);
            s.RecentLocations.AddRange(new[] { Nook.LocationId, Skeleton.LocationId });
            store.Save(s);
            var back = store.Load();
            Assert.Equal(new[] { Skeleton.LocationId }, back.FavoriteLocations);
            Assert.Equal(new[] { Nook.LocationId, Skeleton.LocationId }, back.RecentLocations);

            File.WriteAllText(store.Path, "{\"favoriteLocations\":[\"a\",\"a\",3,\"\"],\"recentLocations\":[\"1\",\"2\",\"3\",\"4\",\"5\",\"6\",\"7\"]}");
            back = store.Load();
            Assert.Equal(new[] { "a" }, back.FavoriteLocations);
            Assert.Equal(LocationPickerViewModel.RecentLimit, back.RecentLocations.Count);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void RealCatalogHighlightsTheSkeletonStoreForSkel()
    {
        var p = new LocationPickerViewModel(AdventureKit.Browser().Locations);
        p.SearchText = "skel";
        var hit = Assert.Single(p.Entries, e => e.IsLocation && e.Location!.Snarfblat == 439);
        Assert.Equal("The Skeleton Store · 439", hit.AccessibleName);
        Assert.Equal("Seaside Town", hit.Section);
        Assert.Same(hit, p.Selected); // ahead of "The Tower of Procedurally-Generated Skeletons"
        Assert.Contains(p.Entries, e => e.IsLocation && e.Location!.Snarfblat == 317);
    }
}
