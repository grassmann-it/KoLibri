using LibKolibri.Access;

namespace KolLibri.Mcp;

/// <summary>
/// Which tools a server registers. The default is the small core for playing by hand
/// (status, inventory, adventure/fight/choice, items, rest, mall search/buy, KMail, daily).
/// Everything else (arbitrage, bribes, CRM, reprice, analytics, two-account workflows,
/// planners) is only registered with <c>KOLIBRI_EXPERIMENTAL=1</c> or <c>--experimental</c>.
/// A tool that is not registered is not in <c>tools/list</c>, cannot be called, and its
/// rights are not offered on the consent page.
/// </summary>
public static class McpToolSurface
{
    /// <summary>Environment variable that turns on the experimental tools ("1" or "true").</summary>
    public const string ExperimentalVariable = "KOLIBRI_EXPERIMENTAL";

    /// <summary>Command line flag of <c>kolibri-mcp</c> that turns on the experimental tools.</summary>
    public const string ExperimentalFlag = "--experimental";

    /// <summary>The tools registered by default, with the rights each one can use.</summary>
    public static IReadOnlyDictionary<string, string[]> Core { get; } = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        // Status / charpane, inventory
        ["kol_status"] = [Rights.AccountRead],
        ["kol_inventory"] = [Rights.AccountRead],
        ["kol_login_status"] = [Rights.AccountRead],
        // Adventure start/continue, fight action, choice
        ["kol_adventure_locations"] = [Rights.AccountRead],
        ["kol_adventure_run_execute"] = [Rights.Adventure, Rights.AccountRead],
        ["kol_combat_inspect"] = [Rights.AccountRead],
        ["kol_combat_decision"] = [Rights.AccountRead],
        ["kol_choice_pending"] = [Rights.AccountRead],
        ["kol_choice_options"] = [Rights.AccountRead],
        ["kol_choice_chain_execute"] = [Rights.Adventure, Rights.AccountRead],
        // Use / eat / drink, rest
        ["kol_use_item"] = [Rights.ItemUse],
        ["kol_consume"] = [Rights.ItemUse],
        ["kol_rest"] = [Rights.ItemUse, Rights.Adventure],
        // Mall search and buy
        ["kol_price_summary"] = [Rights.MarketRead],
        ["kol_mall_buy"] = [Rights.MallBuy],
        // KMail
        ["kol_send_message"] = [Rights.MessageSend],
        // Daily audit / run
        ["kol_daily_audit"] = [Rights.AccountRead],
        ["kol_run_daily_bridge"] = [Rights.AccountRead],
        ["kol_meme_shop"] = [Rights.DailyRun, Rights.MallBuy],
    };

    /// <summary>True when the environment asks for the experimental tools.</summary>
    public static bool ExperimentalFromEnvironment()
        => Environment.GetEnvironmentVariable(ExperimentalVariable)?.Trim() is { } v
           && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase));

    /// <summary>Experimental tools on: <c>--experimental</c> or <c>KOLIBRI_EXPERIMENTAL=1</c>.</summary>
    public static bool ExperimentalRequested(IReadOnlyCollection<string> args)
        => args.Contains(ExperimentalFlag) || ExperimentalFromEnvironment();

    public static bool IsCore(string tool) => Core.ContainsKey(tool);

    /// <summary>
    /// Rights that the given tools can use, in catalog order. Experimental tools are not
    /// mapped here, so with them on the full catalog is offered.
    /// </summary>
    public static IReadOnlyList<string> RightsFor(IEnumerable<string> tools, bool experimental)
    {
        if (experimental) return Rights.All;
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in tools)
            if (Core.TryGetValue(tool, out var rights)) used.UnionWith(rights);
        return Rights.All.Where(used.Contains).ToArray();
    }
}
