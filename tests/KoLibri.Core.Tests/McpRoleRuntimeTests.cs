using LibKolibri;
using Xunit;
using KoLibri.Core.Basis;
using KoLibri.Core.Markt;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Tests;

/// <summary>LibKolibri runtime registration (the MCP role tests moved to KoLibri.Tests/McpReadToolsTests).</summary>
public sealed class McpRoleRuntimeTests
{
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
}
