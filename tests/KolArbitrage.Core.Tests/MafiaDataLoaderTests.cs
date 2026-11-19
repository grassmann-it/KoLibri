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

        Assert.NotEmpty(catalog.Monsters);
        Assert.NotEmpty(catalog.Items);
        Assert.NotEmpty(catalog.Skills);
        Assert.NotEmpty(catalog.Adventures);
        Assert.NotEmpty(catalog.Zones);

        Assert.Contains(catalog.Monsters, m => m.Id == "skeleton-of-crimbos-past");
        Assert.Contains(catalog.Skills, s => s.Id == "sauceror-paste");
        Assert.Contains(catalog.Adventures, a => a.Id == "skeleton-of-crimbos-past");
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
