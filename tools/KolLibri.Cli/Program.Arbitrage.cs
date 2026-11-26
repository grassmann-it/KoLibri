using System.Globalization;
using KoLibri.Core.Basis;
using KoLibri.Core.Markt;
using KoLibri.Core.Spiel;
using LibKolibri;

internal static partial class KolibriCli
{
    /// <summary>Live Meat or an error: a cached value must never decide a purchase.</summary>
    private static Task<long> FreshMeatAsync(KoLAccountReadClient api, CancellationToken ct)
        => new LiveArbitrageAccountReads(api).MeatAsync(ct);

    private static async Task<int> ArbitrageAsync(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0])) { PrintArbitrageHelp(); return args.Length == 0 ? 2 : 0; }
        return args[0].ToLowerInvariant() switch
        {
            "plan" => await ArbitragePlanOrBuyAsync(args[1..], buy: false),
            "buy" => await ArbitragePlanOrBuyAsync(args[1..], buy: true),
            _ => Fail($"Unknown arbitrage command: {args[0]}")
        };
    }

    private static void PrintArbitrageHelp() => PrintArbitrageHelp(Console.Out);

    private static void PrintArbitrageHelp(TextWriter w)
    {
        w.WriteLine("  arbitrage plan --role mallbot --item ITEM_ID [--max-price N] [--budget N] [--margin PERCENT]");
        w.WriteLine("                 [--tax PERCENT] [--reserve N] [--units N] [--reference-price N] [--refresh]   (read-only)");
        w.WriteLine("  arbitrage buy  ...same options... [--execute --approval-token TOKEN] [--then list --price N | --then send-to main]");
        w.WriteLine("                 without --execute: plan plus a preview of every purchase; nothing is sent");
    }

    private static T ArbitrageNumber<T>(string[] args, string name, T fallback) where T : struct, IParsable<T>
    {
        var text = GetOption(args, name);
        if (text is null) return fallback;
        return T.TryParse(text.Replace("_", ""), CultureInfo.InvariantCulture, out var value)
            ? value : throw new ArgumentException($"{name} needs a number.");
    }

    private static async Task<int> ArbitragePlanOrBuyAsync(string[] args, bool buy)
    {
        var itemId = ArbitrageNumber(args, "--item", 0);
        if (itemId <= 0) throw new ArgumentException("--item ITEM_ID is missing.");
        var role = ParseRole(GetOption(args, "--role") ?? "mallbot");
        var settings = new ItemArbitrageSettings
        {
            MaxPrice = ArbitrageNumber<long>(args, "--max-price", 0) is > 0 and var mp ? mp : null,
            ReferencePrice = ArbitrageNumber<long>(args, "--reference-price", 0) is > 0 and var rp ? rp : null,
            MarginPercent = ArbitrageNumber(args, "--margin", ArbitrageLimits.DefaultMarginPercent),
            MallTaxPercent = ArbitrageNumber(args, "--tax", ArbitrageLimits.DefaultMallTaxPercent),
            Budget = ArbitrageNumber(args, "--budget", ArbitrageLimits.DefaultBudget),
            MeatReserve = ArbitrageNumber(args, "--reserve", ArbitrageLimits.DefaultMeatReserve),
            UnitsPerRun = ArbitrageNumber(args, "--units", ArbitrageLimits.DefaultUnitsPerRun)
        };
        settings.Validate();

        var execute = buy && HasFlag(args, "--execute");
        var then = GetOption(args, "--then");
        long listPrice = 0;
        AccountRole? sendTo = null;
        if (then is not null)
        {
            if (!buy) throw new ArgumentException("--then belongs to 'arbitrage buy'.");
            var thenIndex = Array.FindIndex(args, a => a.Equals("--then", StringComparison.OrdinalIgnoreCase));
            switch (then.ToLowerInvariant())
            {
                case "list":
                    listPrice = ArbitrageNumber<long>(args, "--price", 0);
                    if (listPrice < 1) throw new ArgumentException("--then list needs --price N.");
                    if (listPrice > int.MaxValue)
                        throw new ArgumentException($"--price {listPrice:N0} is too high: the shop API takes at most {int.MaxValue:N0}. Nothing was bought.");
                    break;
                case "send-to":
                    var target = thenIndex + 2 < args.Length ? args[thenIndex + 2] : null;
                    sendTo = ParseRole(target ?? throw new ArgumentException("--then send-to needs main or mallbot."));
                    if (sendTo == role) throw new ArgumentException("--then send-to must name the other account; transfers go only between your own two accounts.");
                    break;
                default:
                    throw new ArgumentException("--then must be 'list --price N' or 'send-to main|mallbot'.");
            }
        }
        string? token = GetOption(args, "--approval-token");
        if (execute && !await AgentGuard.ApprovalTokenValidAsync(token).ConfigureAwait(false))
            throw new ArgumentException("The supplied approval token is missing or does not match the stored approval token. Nothing was sent.");

        var refresh = HasFlag(args, "--refresh");
        var http = await ReadAuthenticatedHttpAsync(role);
        var api = new KoLAccountReadClient(http, itemNameResolver: BuildItemNameResolver()) { ReadCharsheetForOrganLimits = true };
        var mall = new KoLMallReadClient(http, itemNameResolver: BuildItemNameResolver());
        using var pricegunHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var meat = await FreshMeatAsync(api, CancellationToken.None);
        var plan = await ItemArbitrageService.PlanAsync(mall, new PricegunReadClient(pricegunHttp), itemId, settings, meat,
            forceRefresh: refresh);
        if (!buy)
        {
            WriteJson(new { command = "arbitrage plan", readOnly = true, role = role.ToString(), meat, settings, plan });
            return 0;
        }

        var credentials = (await AccountSessionManager.CreateDefault().GetSessionAsync(role))?.Credentials
            ?? throw new InvalidOperationException($"{role} is not logged in.");
        var ledger = new ArbitrageSpendLedger(ArbitrageSpendLedger.DefaultPath());
        var executor = ItemArbitrageExecutor.Create(http, credentials, mall, new LiveArbitrageAccountReads(api), ledger,
            role.ToString(), plan, settings);
        if (!execute)
        {
            var preview = await executor.PreviewAsync(plan);
            WriteJson(new { command = "arbitrage buy", readOnly = true, executed = false, role = role.ToString(), meat, plan, preview });
            return 0;
        }

        var run = await executor.ExecuteAsync(plan);
        object? followUp = null;
        if (run.UnitsBought > 0 && run.Unclear)
            followUp = new { skipped = true, reason = "A purchase is unclear; check the account before listing or sending." };
        else if (run.UnitsBought > 0 && listPrice > 0)
        {
            var lister = new StoreLister(http);
            var listed = (await lister.ReadOwnListingsAsync(credentials)).FirstOrDefault(x => x.ItemId == itemId);
            if (listed is not null)
                followUp = new { list = "skipped", reason = $"Already listed at {listed.Price:N0}; KoL only lists items that are not listed yet." };
            else
            {
                var result = await lister.ListAsync(credentials, itemId, (int)listPrice, run.UnitsBought);
                followUp = new { list = result.Accepted ? "listed" : "not listed", quantity = run.UnitsBought, price = (int)listPrice, result.Message };
            }
        }
        else if (run.UnitsBought > 0 && sendTo is { } to)
        {
            using var client = await KolibriClient.OpenAsync(role);
            var recipient = await client.OtherAccountNameAsync()
                ?? throw new InvalidOperationException("The other account is not stored; nothing was sent.");
            var outcome = await client.SendKMailOutcomeAsync(recipient, "Sent with KoLibri.",
                [new KoLMailAttachment(itemId, run.UnitsBought)], 0);
            // Unconfirmed: the mail may have arrived; never report it as not sent.
            followUp = new { sendTo = to.ToString(), quantity = run.UnitsBought, sent = outcome == KoLMailSendOutcome.Sent,
                unconfirmed = outcome == KoLMailSendOutcome.Unconfirmed };
        }
        WriteJson(new { command = "arbitrage buy", executed = true, role = role.ToString(), unitsBought = run.UnitsBought,
            meatSpent = run.MeatSpent, unclear = run.Unclear, steps = run.Steps, followUp });
        return run.Unclear ? 4 : 0;
    }
}
