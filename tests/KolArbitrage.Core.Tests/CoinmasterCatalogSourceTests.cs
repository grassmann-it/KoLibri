using Xunit;
using KolArbitrage.Core.Markt;

namespace KolArbitrage.Core.Tests;

public sealed class CoinmasterCatalogSourceTests
{
    [Fact]
    public async Task SourcePreservesManifestAndTicketCosts()
    {
        var fetchedAt = new DateTimeOffset(2026, 11, 17, 1, 2, 3, TimeSpan.Zero);
        var manifest = new CoinmasterCatalogManifest(
            new Uri("https://catalog.example.test/coinmasters.json"),
            version: "2026-11-17",
            fetchedAt: fetchedAt);
        var source = new CoinmasterCatalogSource(_ => Task.FromResult(
            new CoinmasterCatalogSnapshot(
                new[] { new CoinmasterCatalogEntry("Buck", "123", "future output", 10) },
                manifest,
                new Dictionary<string, int> { ["Buck"] = 5 })));

        var snapshot = await source.ReadAsync();

        Assert.Equal("2026-11-17", snapshot.Version);
        Assert.Equal(fetchedAt, snapshot.FetchedAt);
        Assert.Equal(manifest.SourceUri, snapshot.SourceUri);
        Assert.Equal(5, snapshot.TicketCosts["Buck"]);
        Assert.True(snapshot.IsValid(out var error), error);
        Assert.Equal(10, snapshot.ToCatalog().Entries.Single().CurrencyCost);
    }

    [Fact]
    public void JsonImporterAcceptsRowsAndManifest()
    {
        const string json = """
        {
          "manifest": {
            "sourceUri": "https://catalog.example.test/coinmasters.json",
            "version": "v2"
          },
          "entries": [
            { "currency": " Buck ", "itemId": "123", "itemName": "output", "currencyCost": 10 }
          ],
          "ticketCosts": { "Buck": 5 }
        }
        """;

        var snapshot = CoinmasterCatalogImporter.FromJson(json);

        Assert.Equal("v2", snapshot.Version);
        Assert.Equal("Buck", snapshot.Entries.Single().Currency);
        Assert.Equal(5, snapshot.TicketCosts["buck"]);
        Assert.True(snapshot.IsValid(out var error), error);
    }

    [Fact]
    public void PlannerSubtractsConfiguredFeesTransferAndHoldingCosts()
    {
        var planner = new CoinmasterRoiPlanner(
            new CoinmasterCatalog(new[]
            {
                new CoinmasterCatalogEntry("Buck", "123", "output", 10)
            }),
            new Dictionary<string, decimal> { ["Buck"] = 2m },
            new CoinmasterRoiOptions
            {
                SaleFeeRate = 0.10m,
                ListingFee = 3m,
                TransferCost = 4m,
                HoldingCost = 5m,
                BagCostAllocation = 6m,
                SafetyBuffer = 2m
            });

        var row = Assert.Single(planner.Plan(new Dictionary<string, decimal> { ["123"] = 100m }));

        Assert.Equal(10m, row.SaleFeeInMeat);
        Assert.Equal(20m, row.CurrencyCostInMeat);
        Assert.Equal(50m, row.NetResale);
        Assert.Equal(50m, row.TotalCostsInMeat);
        Assert.True(row.IsCandidate);
    }

    [Fact]
    public void UnknownCurrencyIsReviewOnlyEvenWhenPositiveNetIsNotRequired()
    {
        var planner = new CoinmasterRoiPlanner(
            new CoinmasterCatalog(new[]
            {
                new CoinmasterCatalogEntry("Unknown currency", "123", "output", 10)
            }),
            options: new CoinmasterRoiOptions { RequirePositiveNet = false });

        var row = Assert.Single(planner.Plan(new Dictionary<string, decimal> { ["123"] = 100_000m }));

        Assert.False(row.IsCandidate);
        Assert.Contains("Currency opportunity cost", row.Rationale, StringComparison.OrdinalIgnoreCase);
    }
}
