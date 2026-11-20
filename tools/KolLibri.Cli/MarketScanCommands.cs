using System.Globalization;
using System.Text.Json;
using KolArbitrage.Core.Accounts;
using KolArbitrage.Core.Daily;

/// <summary>
/// The market scan: what the arbitrage items cost right now, what they
/// fetch again, and which listings are worth buying.
///
/// The scan is read-only. The plan is printed; buying needs an explicit
/// flag, so the meat is only spent when the operator asks for it.
/// </summary>
internal static class MarketScanCommands
{
    public static async Task<int> RunAsync(
        string[] args,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken = default)
    {
        var execute = args.Contains("--execute", StringComparer.OrdinalIgnoreCase);
        var sell = args.Contains("--sell", StringComparer.OrdinalIgnoreCase);
        var roleValue = GetOption(args, "--role") ?? "mallbot";
        var role = Enum.TryParse<AccountRole>(roleValue, ignoreCase: true, out var parsed)
            ? parsed
            : AccountRole.Mallbot;

        var accounts = AccountSessionManager.CreateDefault();
        await accounts.InitializeAsync().ConfigureAwait(false);
        var session = await accounts.GetSessionAsync(role).ConfigureAwait(false);
        if (session is null)
        {
            Console.Error.WriteLine(
                $"No session for {role}. Log in first: kolibri login refresh --role {roleValue.ToLowerInvariant()}");
            return 1;
        }

        using var http = KoLLoginClient.CreateAuthenticatedClient(
            session.Credentials,
            new Uri("https://www.kingdomofloathing.com/"));
        var scanner = new MarketScanner(http);
        var planner = new BuyPlanner();
        var meat = await ReadMeatAsync(http, cancellationToken).ConfigureAwait(false);

        var results = new List<object>();
        foreach (var rule in BuyPlanner.DefaultRules)
        {
            var snapshot = await scanner
                .ScanAsync(rule.ItemId, rule.ItemName, rule.ItemName, cancellationToken)
                .ConfigureAwait(false);
            var plan = planner.Plan(rule, snapshot.MallListings, meat);
            results.Add(new
            {
                itemId = rule.ItemId,
                item = rule.ItemName,
                cheapestMallPrice = snapshot.CheapestMallPrice,
                medianPrice = snapshot.MedianPrice,
                lastSalePrice = snapshot.LastSalePrice,
                mallListings = snapshot.MallListings.Count,
                rule = new
                {
                    maximumPrice = rule.MaximumPrice,
                    resalePrice = rule.ResalePrice,
                    buyDespiteDailyLimit = rule.BuyDespiteDailyLimit
                },
                plan = new
                {
                    candidates = plan.Candidates.Select(c => new
                    {
                        storeId = c.StoreId,
                        store = c.StoreName,
                        unitPrice = c.UnitPrice,
                        quantity = c.Quantity,
                        dailyLimit = c.DailyLimit,
                        profitPerUnit = c.ProfitPerUnit,
                        totalProfit = c.TotalProfit,
                        reason = c.Reason
                    }),
                    totalCost = plan.TotalCost,
                    totalProfit = plan.TotalProfit,
                    rationale = plan.Rationale
                }
            });

            if (execute && plan.Candidates.Count > 0)
            {
                var buyer = new MallBuyClient(http, MallBuyClient.DefaultBaseUri);
                foreach (var candidate in plan.Candidates)
                {
                    var pwd = await scanner
                        .ReadPasswordHashAsync(session.Credentials, candidate.StoreId, cancellationToken)
                        .ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(pwd))
                    {
                        Console.Error.WriteLine(
                            $"  store {candidate.StoreId}: no password hash — skipped");
                        continue;
                    }
                    var result = await buyer.BuyAsync(
                        session.Credentials,
                        candidate.StoreId,
                        candidate.ItemId,
                        candidate.UnitPrice,
                        candidate.Quantity,
                        pwd,
                        cancellationToken).ConfigureAwait(false);
                    var bought = result.Message.Contains("You acquire an item", StringComparison.OrdinalIgnoreCase);
                    if (bought)
                    {
                        Console.Error.WriteLine(
                            $"  bought {candidate.Quantity} x {candidate.ItemName} "
                            + $"for {candidate.UnitPrice:N0} from store {candidate.StoreId}");
                    }
                    else if (result.Message.Contains(MallBuyClient.DailyLimitRefusal, StringComparison.OrdinalIgnoreCase))
                    {
                        // The seller limits how much they sell per day and
                        // today's share is used up.
                        Console.Error.WriteLine(
                            $"  skipped {candidate.ItemName} from store {candidate.StoreId}: "
                            + "the seller's daily limit is used up");
                    }
                    else
                    {
                        Console.Error.WriteLine(
                            $"  not bought {candidate.ItemName} from store {candidate.StoreId}: "
                            + FirstLine(result.Message));
                    }
                }
            }
        }

        // The sell side: the bought items go back on the market at the
        // price they fetch again — that is what turns the purchase into
        // an arbitrage instead of a pile of items.
        if (sell)
        {
            var lister = new StoreLister(http);
            var storeItems = await lister
                .ReadItemsAsync(session.Credentials, cancellationToken)
                .ConfigureAwait(false);
            var ownListings = await lister
                .ReadOwnListingsAsync(session.Credentials, cancellationToken)
                .ConfigureAwait(false);
            foreach (var sellRule in BuyPlanner.DefaultRules)
            {
                var held = storeItems.FirstOrDefault(x => x.ItemId == sellRule.ItemId);
                var listed = ownListings.FirstOrDefault(x => x.ItemId == sellRule.ItemId);
                if (listed is not null)
                {
                    // The item is already on the market. KoL only lists the
                    // items that are not yet listed, so an existing
                    // listing is left alone — it is already at a price.
                    Console.Error.WriteLine(
                        $"  {sellRule.ItemName} is already listed at {listed.Price:N0} "
                        + $"({listed.DailyLimit}/day)");
                    continue;
                }
                if (held is null || held.InStock <= 0)
                {
                    Console.Error.WriteLine(
                        $"  nothing to sell: no {sellRule.ItemName} in the store");
                    continue;
                }
                var result = await lister.ListAsync(
                    session.Credentials,
                    sellRule.ItemId,
                    sellRule.ResalePrice,
                    held.InStock,
                    3,
                    cancellationToken).ConfigureAwait(false);
                Console.Error.WriteLine(
                    $"  {(result.Accepted ? "listed" : "not listed")} {held.InStock} x "
                    + $"{sellRule.ItemName} for {sellRule.ResalePrice:N0}: "
                    + FirstLine(result.Message));
            }
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            command = "market-scan",
            role = role.ToString(),
            meat,
            executed = execute,
            sold = sell,
            items = results
        }, jsonOptions));
        return 0;
    }

    /// <summary>
    /// Reads the account's meat. The status endpoint carries it; the meat
    /// endpoint does not exist and answers the API's help page.
    /// </summary>
    private static async Task<long> ReadMeatAsync(HttpClient http, CancellationToken cancellationToken)
    {
        try
        {
            var body = await http.GetStringAsync(
                new Uri("https://www.kingdomofloathing.com/api.php?what=status&for=KoLibri"),
                cancellationToken).ConfigureAwait(false);
            return ParseMeat(body);
        }
        catch (HttpRequestException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Reads the meat out of the status payload. KoL writes the numbers as
    /// strings, so both forms are read.
    /// </summary>
    internal static long ParseMeat(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return 0;
        var trimmed = body.Trim();
        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var plain))
            return plain;
        if (!trimmed.StartsWith('{')) return 0;
        using var document = JsonDocument.Parse(trimmed);
        foreach (var name in new[] { "meat", "closetmeat", "storagemeat" })
        {
            if (!document.RootElement.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String
                && long.TryParse(value.GetString(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var text)) return text;
        }
        return 0;
    }

    /// <summary>
    /// The first line of a message, for the short report. KoL's answers
    /// are HTML, and the whole page is too much to print.
    /// </summary>
    private static string FirstLine(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return string.Empty;
        var text = System.Text.RegularExpressions.Regex.Replace(message, "<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        var cleaned = string.Join(' ',
            text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 3));
        return cleaned.Length > 160 ? cleaned[..160] : cleaned;
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }
}
