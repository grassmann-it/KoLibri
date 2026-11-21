using System.Text.Json;
using KolLibri.Mcp;
using LibKolibri;
using Xunit;
using KoLibri.Core.Basis;
using KoLibri.Core.Markt;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Tests;

public sealed class McpRoleRuntimeTests
{
    [Fact]
    public async Task StatusUsesTheRoleScopedGatewayWithoutCredentialsInArguments()
    {
        var roleRuntime = new RoleScopedRuntime()
            .Register(AccountRole.Main, new FixtureAccountGateway("main-player", 11))
            .Register(AccountRole.Mallbot, new FixtureAccountGateway("mallbot-player", 22));
        var server = new KolibriMcpServer(roleRuntime);

        var response = await server.HandleAsync(Parse("""
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"kol_status","arguments":{"role":"Mallbot"}}}
            """));

        Assert.NotNull(response);
        var structured = response!.Value.GetProperty("result").GetProperty("structuredContent");
        Assert.Equal("mallbot-player", structured.GetProperty("playerName").GetString());
        Assert.Equal(22, structured.GetProperty("adventures").GetInt32());
    }

    [Fact]
    public async Task InventoryRoleSelectionDoesNotRequireCookieOrTokenFields()
    {
        var roleRuntime = new RoleScopedRuntime()
            .Register(AccountRole.Main, new FixtureAccountGateway("main-player", 1))
            .Register(AccountRole.Mallbot, new FixtureAccountGateway("mallbot-player", 2));
        var server = new KolibriMcpServer(roleRuntime);

        var response = await server.HandleAsync(Parse("""
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"kol_inventory","arguments":{"role":"main"}}}
            """));

        Assert.NotNull(response);
        var structured = response!.Value.GetProperty("result").GetProperty("structuredContent");
        Assert.Equal("main-item", structured.GetProperty("items")[0].GetProperty("itemId").GetString());
    }

    [Fact]
    public async Task PriceReadUsesTheSelectedRoleMallGateway()
    {
        var roleRuntime = new RoleScopedRuntime()
            .Register(AccountRole.Main, new FixtureMallGateway(10))
            .Register(AccountRole.Mallbot, new FixtureMallGateway(20));
        var server = new KolibriMcpServer(roleRuntime);

        var response = await server.HandleAsync(Parse("""
            {"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"kol_price_summary","arguments":{"itemId":"123","role":"Mallbot"}}}
            """));

        Assert.NotNull(response);
        var structured = response!.Value.GetProperty("result").GetProperty("structuredContent");
        Assert.Equal(20, structured.GetProperty("lowestSellPrice").GetDecimal());
    }

    [Fact]
    public void CombatGatewayRegistrationIsExplicitAndDiscoverable()
    {
        using var transport = new ManualCombatFixtureTransport();
        using var gateway = new ManualCombatGateway(transport);
        var runtime = new LibKolibriRuntime().RegisterCombatGateway(gateway);

        var combat = runtime.Get<ICombatGateway>();
        Assert.IsType<CoreCombatGateway>(combat);
        Assert.Contains(KolibriCapability.Combat,
            runtime.Describe().Modules.Single().Capabilities);
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class FixtureAccountGateway(string playerName, int marker) : IAccountGateway
    {
        public Task<KoLStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new KoLStatusSnapshot(playerName, marker, marker * 100L, 1, DateTimeOffset.UtcNow));

        public Task<KoLInventorySnapshot> GetInventoryAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new KoLInventorySnapshot(
                [new KoLInventoryItem($"{(marker == 1 ? "main" : "mallbot")}-item", "Fixture item", marker)],
                DateTimeOffset.UtcNow));
    }

    private sealed class FixtureMallGateway(long price) : IMallGateway
    {
        public Task<MallSnapshot> SearchAsync(string item, CancellationToken cancellationToken = default)
            => Task.FromResult(new MallSnapshot(
                item,
                [new MallListing(item, "fixture-store", "Fixture", price, 1, null, DateTimeOffset.UtcNow,
                    new Uri("https://fixture.invalid/mall.php"))],
                DateTimeOffset.UtcNow,
                new Uri("https://fixture.invalid/mall.php")));

        public Task<IReadOnlyList<OwnShopListing>> GetOwnShopAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OwnShopListing>>([]);
    }
}
