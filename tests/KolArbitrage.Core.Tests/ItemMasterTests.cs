using KolArbitrage.Core.Integration;
using Xunit;

namespace KolArbitrage.Core.Tests;

public sealed class ItemMasterTests
{
    private const string Fixture = """
        # item number name descid image use access autosell plural
        1	seal tooth	617818041	tooth.gif	message, combat reusable, smith	t,d	1	seal teeth
        10323	packaged Pocket Professor	617818043	professor.gif	usable, package	t,g	1000	packaged Pocket Professors
        10324	Pocket Professor memory chip	617818044	chip.gif	usable		0	Pocket Professor memory chips
        10325
        """;

    [Fact]
    public void ParsesAccessAutosellAndHtmlNamesOffline()
    {
        var catalog = KoLmafiaItemsTxtParser.Parse(Fixture);

        var tooth = catalog.Get("1");
        Assert.Equal("seal tooth", tooth.Name);
        Assert.True(tooth.IsTradeable);
        Assert.False(tooth.IsGiftable);
        Assert.True(tooth.IsDiscardable);
        Assert.Equal(1, tooth.NpcPrice);
        Assert.True(tooth.IsNpcSellable);
    }

    [Fact]
    public void InfersPackagedAndOpenedPairFromAdjacentCatalogRows()
    {
        var catalog = KoLmafiaItemsTxtParser.Parse(Fixture);

        var packaged = catalog.Get("10323");
        Assert.True(packaged.IsPackaged);
        Assert.Equal("10324", packaged.OpenedVariantId);
        Assert.Equal("10323", catalog.Get("10324").PackagedVariantId);
        Assert.Same(catalog.Get("10324"), catalog.GetOpenedVariant("10323"));
    }

    [Fact]
    public void StrictModeReportsMalformedRowsButSkipsNumericPlaceholders()
    {
        var result = KoLmafiaItemsTxtParser.ParseWithDiagnostics(
            "1\nnot-an-item\tbad\n", new KoLmafiaItemsTxtParserOptions { IgnoreMalformedRows = true });

        Assert.Empty(result.Items);
        Assert.Single(result.Diagnostics);
        Assert.Throws<FormatException>(() => KoLmafiaItemsTxtParser.Parse(
            "not-an-item\tbad\n", new KoLmafiaItemsTxtParserOptions { IgnoreMalformedRows = false }));
    }
}
