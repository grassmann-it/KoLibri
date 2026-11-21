using System.Net;
using Xunit;
using KolArbitrage.Core.Spiel;

namespace KolArbitrage.Core.Tests;

/// <summary>
/// Tests for the KoL account read client. Focus is the inventory parser:
/// legacy object/array shape plus the Mafia-mode flat dict that KoL
/// returns when the request sets `for=KoLibri`.
/// </summary>
public sealed class InventoryMafiaModeTests
{
    [Fact]
    public async Task ParseInventory_parses_Mafia_mode_flat_dict()
    {
        var http = new HttpClient(new StubHandler("{\"1\":\"8\",\"44\":\"655\",\"250\":\"1\"}"));
        var client = new KoLAccountReadClient(http);
        var inv = await client.FetchInventoryAsync(forceRefresh: true);
        Assert.Equal(3, inv.Items.Count);
        Assert.Contains(inv.Items, i => i.ItemId == "1" && i.Quantity == 8);
        Assert.Contains(inv.Items, i => i.ItemId == "44" && i.Quantity == 655);
        Assert.Contains(inv.Items, i => i.ItemId == "250" && i.Quantity == 1);
    }

    [Fact]
    public async Task ParseInventory_uses_itemNameResolver_for_display()
    {
        var http = new HttpClient(new StubHandler("{\"42\":\"7\"}"));
        var client = new KoLAccountReadClient(http,
            itemNameResolver: id => id == "42" ? "seal tooth" : null);
        var inv = await client.FetchInventoryAsync(forceRefresh: true);
        Assert.Single(inv.Items);
        Assert.Equal("seal tooth", inv.Items[0].Name);
        Assert.Equal("42", inv.Items[0].ItemId);
    }

    [Fact]
    public async Task ParseInventory_skips_zero_quantities_in_Mafia_mode()
    {
        var http = new HttpClient(new StubHandler("{\"1\":\"0\",\"44\":\"3\"}"));
        var client = new KoLAccountReadClient(http);
        var inv = await client.FetchInventoryAsync(forceRefresh: true);
        Assert.Single(inv.Items);
        Assert.Equal("44", inv.Items[0].ItemId);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _body;
        public StubHandler(string body) => _body = body;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body)
            });
        }
    }
}
