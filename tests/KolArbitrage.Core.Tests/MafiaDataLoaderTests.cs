using KolArbitrage.Core.Data;
using Xunit;

namespace KolArbitrage.Core.Tests;

public sealed class MafiaDataLoaderTests
{
    [Fact]
    public void Loader_reads_vendor_snapshot()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "external", "kolmafia-data");
        Assert.True(Directory.Exists(dir), $"Vendor directory '{dir}' missing.");

        var loader = new MafiaDataLoader();
        var catalog = loader.Load(dir);

        // The Mafia dump is large; assert that we loaded real-world quantities
        // and that the records carry the typed fields the data layer needs.
        Assert.True(catalog.Monsters.Count > 1000, $"Loaded only {catalog.Monsters.Count} monsters");
        Assert.True(catalog.Items.Count > 1000, $"Loaded only {catalog.Items.Count} items");
        Assert.True(catalog.Skills.Count > 100, $"Loaded only {catalog.Skills.Count} skills");
        Assert.True(catalog.Adventures.Count > 100, $"Loaded only {catalog.Adventures.Count} adventures");
        Assert.True(catalog.Zones.Count > 50, $"Loaded only {catalog.Zones.Count} zones");
        Assert.True(catalog.Effects.Count > 100, $"Loaded only {catalog.Effects.Count} effects");
        Assert.True(catalog.Modifiers.Count > 100, $"Loaded only {catalog.Modifiers.Count} modifiers");
        Assert.True(catalog.Concoctions.Count > 100, $"Loaded only {catalog.Concoctions.Count} concoctions");

        // Spot-check: the seal tooth item from the start of items.txt.
        var sealTooth = catalog.Items.FirstOrDefault(i => i.Name == "seal tooth");
        Assert.NotNull(sealTooth);
        Assert.Equal("2", sealTooth!.Id);
        Assert.Equal("seal teeth", sealTooth.Plural);
        Assert.True(sealTooth.Tradeable);

        // Spot-check: a well-known monster (haXx0r in legacy form).
        var haxxor = catalog.Monsters.FirstOrDefault(m => m.Name.Contains("HaXx0r"));
        Assert.NotNull(haxxor);
        Assert.Equal("141", haxxor!.Id);

        // Spot-check: the first skill in the canonical Mafia dump is
        // Liver of Steel, marked as non-permable.
        var liver = catalog.Skills.FirstOrDefault(s => s.Name == "Liver of Steel");
        Assert.NotNull(liver);
        Assert.Equal(1, liver!.Id);
        Assert.False(liver.Permable);

        // Spot-check: the campground adventure has a sniff property.
        var campground = catalog.Adventures.FirstOrDefault(a => a.Name == "Campground");
        Assert.NotNull(campground);
        Assert.True(campground!.Snarfblat > 0);

        // Spot-check: the first zone in the canonical Mafia dump is "Town"
        // with display name "Seaside Town".
        var town = catalog.Zones.FirstOrDefault(z => z.Id == "Town");
        Assert.NotNull(town);
        Assert.Equal("Seaside Town", town!.Name);
        Assert.Equal("Town", town.Type);
    }

    [Fact]
    public void Loader_records_warnings_for_unparseable_lines()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kolibri-loader-bad-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "items.jsonl"),
                "{\"id\": \"1\", \"name\": \"valid item\"}\n" +
                "{this is not valid json\n" +
                "{\"id\": \"2\", \"name\": \"another valid item\"}\n");
            File.WriteAllText(Path.Combine(dir, "monsters.jsonl"), "{\"id\": \"1\", \"name\": \"skeleton\"}\n");
            File.WriteAllText(Path.Combine(dir, "skills.jsonl"), "{\"id\": 1, \"name\": \"test\"}\n");
            File.WriteAllText(Path.Combine(dir, "adventures.jsonl"), "{\"name\": \"test\"}\n");
            File.WriteAllText(Path.Combine(dir, "zones.jsonl"), "{\"zoneId\": \"Town\", \"displayName\": \"Town\"}\n");
            File.WriteAllText(Path.Combine(dir, "effects.jsonl"), "{\"id\": 1, \"name\": \"test\"}\n");
            File.WriteAllText(Path.Combine(dir, "modifiers.jsonl"), "{\"header\": \"x\", \"description\": \"y\"}\n");
            File.WriteAllText(Path.Combine(dir, "concoctions.jsonl"), "{\"name\": \"x\", \"mixMethod\": \"y\", \"description\": \"z\"}\n");
            File.WriteAllText(Path.Combine(dir, "monsterparts.jsonl"), "{\"monster\": \"a\", \"part\": \"b\"}\n");
            File.WriteAllText(Path.Combine(dir, "restores.jsonl"), "{\"name\": \"x\", \"effect\": \"y\", \"stat\": \"hp\", \"delta\": \"5\"}\n");

            var loader = new MafiaDataLoader();
            loader.Load(dir, out var diagnostics);

            Assert.Equal(1, diagnostics.WarningCount);
            Assert.Single(diagnostics.Warnings, w => w.Contains("items.jsonl"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Default_directory_lives_inside_user_local_app_data()
    {
        var path = MafiaDataLoader.DefaultDirectory();
        Assert.Contains("KoLibri", path);
        Assert.Contains("kolmafia-data", path);
    }

    [Fact]
    public void Missing_directory_throws()
    {
        var loader = new MafiaDataLoader();
        Assert.Throws<DirectoryNotFoundException>(() => loader.Load("does/not/exist"));
    }
}
