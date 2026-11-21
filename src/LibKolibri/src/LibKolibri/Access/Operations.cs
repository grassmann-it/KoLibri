namespace LibKolibri.Access;

/// <summary>Whether an operation only looks or changes something in the game.</summary>
public enum OperationKind { Read, Write }

/// <summary>
/// One entry of the single operation list. CLI commands, MCP tools, the consent page
/// and the reference docs are all derived from these entries; the right a token needs
/// is part of the definition, not of each front end.
/// </summary>
public sealed record OperationInfo(
    string Name,
    string Right,
    OperationKind Kind,
    string Description,
    string BudgetUnit = "");

/// <summary>Names of the rights an AI client can be granted.</summary>
public static class Rights
{
    public const string AccountRead = "account.read";
    public const string MarketRead = "market.read";
    public const string ShopRead = "shop.read";
    public const string Adventure = "adventure";
    public const string MallBuy = "mall.buy";
    public const string DailyRun = "daily.run";
    public const string TransferMeat = "transfer.meat";
    public const string TransferItems = "transfer.items";
    public const string MessageSend = "message.send";
    public const string ShopReprice = "shop.reprice";
    public const string ItemUse = "item.use";

    public static IReadOnlyList<string> All { get; } =
    [
        AccountRead, MarketRead, ShopRead, Adventure, MallBuy, DailyRun,
        TransferMeat, TransferItems, MessageSend, ShopReprice, ItemUse,
    ];
}

/// <summary>The fixed list of operations and the right each one needs.</summary>
public static class OperationCatalog
{
    public static IReadOnlyList<OperationInfo> All { get; } =
    [
        new("account.status", Rights.AccountRead, OperationKind.Read, "Character status, meat, adventures."),
        new("account.inventory", Rights.AccountRead, OperationKind.Read, "Inventory contents."),
        new("market.search", Rights.MarketRead, OperationKind.Read, "Mall listings and price history for an item."),
        new("shop.read", Rights.ShopRead, OperationKind.Read, "Own store sales, buyer statistics and price suggestions."),
        new("daily.preview", Rights.AccountRead, OperationKind.Read, "Plan of the daily routine without executing it."),
        new("adventure.run", Rights.Adventure, OperationKind.Write, "Spend adventures at a location.", "adventures"),
        new("mall.buy", Rights.MallBuy, OperationKind.Write, "Buy items from mall stores.", "meat"),
        new("daily.run", Rights.DailyRun, OperationKind.Write, "Run the daily routine / meme shop.", "runs"),
        new("transfer.meat", Rights.TransferMeat, OperationKind.Write, "Send meat between own accounts.", "meat"),
        new("transfer.items", Rights.TransferItems, OperationKind.Write, "Send items between own accounts.", "items"),
        new("message.send", Rights.MessageSend, OperationKind.Write, "Send a KMail or chat message.", "messages"),
        new("shop.reprice", Rights.ShopReprice, OperationKind.Write, "Change prices in the own mall store.", "changes"),
        new("item.use", Rights.ItemUse, OperationKind.Write, "Use or consume items.", "uses"),
    ];

    public static OperationInfo? Find(string name)
        => All.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.Ordinal));
}
