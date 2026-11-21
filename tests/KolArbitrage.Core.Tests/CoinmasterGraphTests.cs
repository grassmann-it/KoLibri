using Xunit;
using KolArbitrage.Core.Markt;

namespace KolArbitrage.Core.Tests;

public sealed class CoinmasterGraphTests
{
    [Fact]
    public void DerivesCurrencyValueFromBestTradeableAlternative()
    {
        var catalog = new CoinmasterCatalog(new[]
        {
            new CoinmasterCatalogEntry("Buck", "cheap", "Cheap", 10),
            new CoinmasterCatalogEntry("Buck", "best", "Best", 20),
            new CoinmasterCatalogEntry("Buck", "bound", "Bound", 1, IsTradeable: false)
        });
        var graph = new CoinmasterConversionGraph(catalog);

        var values = graph.DeriveCurrencyOpportunityCosts(new Dictionary<string, decimal>
        {
            ["cheap"] = 100m,
            ["best"] = 300m,
            ["bound"] = 10000m
        });

        Assert.Equal(15m, values["Buck"]);
    }

    [Fact]
    public void PlannerSubtractsCurrencyAndRequiredTicketCosts()
    {
        var catalog = new CoinmasterCatalog(
            new[]
            {
                new CoinmasterCatalogEntry("Buck", "book", "Book", 10,
                    RequiresTicket: true),
                new CoinmasterCatalogEntry("Buck", "ticket", "Ticket", 5,
                    IsOneDayTicket: true)
            },
            new Dictionary<string, int> { ["Buck"] = 5 });
        var planner = new CoinmasterRoiPlanner(catalog,
            new Dictionary<string, decimal> { ["Buck"] = 2m });

        var rows = planner.Plan(new Dictionary<string, decimal>
        {
            ["book"] = 100m,
            ["ticket"] = 20m
        });
        var book = Assert.Single(rows, x => x.ItemId == "book");
        var ticket = Assert.Single(rows, x => x.ItemId == "ticket");

        Assert.Equal(20m, book.CurrencyCostInMeat);
        Assert.Equal(10m, book.TicketCostInMeat);
        Assert.Equal(70m, book.NetResale);
        Assert.True(book.IsCandidate);
        Assert.False(ticket.IsCandidate);
    }

    [Fact]
    public void NonTradeableOutputIsNeverCandidate()
    {
        var catalog = new CoinmasterCatalog(new[]
        {
            new CoinmasterCatalogEntry("Coin", "bound", "Bound", 1, IsTradeable: false)
        });
        var planner = new CoinmasterRoiPlanner(catalog,
            new Dictionary<string, decimal> { ["Coin"] = 100m });

        var row = Assert.Single(planner.Plan(new Dictionary<string, decimal>
        {
            ["bound"] = 100000m
        }));

        Assert.False(row.IsCandidate);
        Assert.Equal("Output is not tradeable.", row.Rationale);
    }
}
