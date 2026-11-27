using System.Text.Json;
using KoLibri.Extras;

namespace KolLibri.Mcp;

public partial class KolibriMcpServer
{

    private void RegisterArbitrageTool()
        => Register("kol_arbitrage_plan",
            "Plan buying an item below its resale price from live Mall offers (per-shop quantity, daily limits, budget, meat reserve, reasons for skipped offers). Read-only: buys nothing. Run the plan step by step with kol_mall_buy.",
            Schema("{\"type\":\"object\",\"required\":[\"itemId\"],\"properties\":{"
                + "\"itemId\":{\"type\":\"integer\",\"minimum\":1},"
                + "\"role\":{\"enum\":[\"Main\",\"Mallbot\"]},"
                + "\"forceRefresh\":{\"type\":\"boolean\",\"default\":false},"
                + "\"maxPrice\":{\"type\":\"integer\",\"minimum\":1},"
                + "\"referencePrice\":{\"type\":\"integer\",\"minimum\":1},"
                + "\"marginPercent\":{\"type\":\"number\",\"minimum\":0,\"maximum\":90,\"default\":10},"
                + "\"mallTaxPercent\":{\"type\":\"number\",\"minimum\":0,\"maximum\":50,\"default\":0},"
                + "\"budget\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000000000,\"default\":2000000},"
                + "\"meatReserve\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":1000000000,\"default\":0},"
                + "\"availableMeat\":{\"type\":\"integer\",\"minimum\":0},"
                + "\"unitsPerRun\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":10000,\"default\":200}"
                + "},\"additionalProperties\":false}"),
            ArbitragePlanAsync);

    private async Task<object?> ArbitragePlanAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var itemId = IntProperty(arguments, "itemId");
        if (itemId < 1) throw new McpInvalidParamsException("itemId is required.");
        var role = RoleFor(arguments, _options.DefaultRole);

        var settings = new ItemArbitrageSettings
        {
            MaxPrice = NullableLongProperty(arguments, "maxPrice"),
            ReferencePrice = NullableLongProperty(arguments, "referencePrice"),
            MarginPercent = (double?)DecimalProperty(arguments, "marginPercent") ?? ArbitrageLimits.DefaultMarginPercent,
            MallTaxPercent = (double?)DecimalProperty(arguments, "mallTaxPercent") ?? ArbitrageLimits.DefaultMallTaxPercent,
            Budget = LongProperty(arguments, "budget", ArbitrageLimits.DefaultBudget),
            MeatReserve = LongProperty(arguments, "meatReserve", ArbitrageLimits.DefaultMeatReserve),
            UnitsPerRun = IntProperty(arguments, "unitsPerRun", ArbitrageLimits.DefaultUnitsPerRun)
        };
        try { settings.Validate(); }
        catch (ArgumentOutOfRangeException e) { throw new McpInvalidParamsException(e.Message); }

        // Only market data is read. The account's meat is private account data (right account.status), so
        // it is not read here: the caller may pass availableMeat, and kol_mall_buy checks the real meat.
        await using var session = await _accounts.OpenAsync(role, cancellationToken).ConfigureAwait(false);
        var plan = await ItemArbitrageService.PlanAsync(
            session, itemId, settings, NullableLongProperty(arguments, "availableMeat"),
            refresh: BoolProperty(arguments, "forceRefresh"), cancellationToken: cancellationToken).ConfigureAwait(false);
        return new
        {
            readOnly = true,
            role = role.ToString(),
            plan,
            howToRun = "Buy each step with kol_mall_buy (itemId, itemName, quantity <= step.quantity, maxPricePerUnit = step.unitPrice, "
                + "preview first, then confirm). Re-plan after every purchase; never repeat a step whose result was unclear."
        };
    }
}
