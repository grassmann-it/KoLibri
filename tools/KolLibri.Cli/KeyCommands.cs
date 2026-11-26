using KoLibri;

internal static partial class KolibriCli
{
    private static void PrintKeysHelp(TextWriter w)
    {
        w.WriteLine("  keys status   --role R                                     parts, complete keys, possible sets (read-only)");
        w.WriteLine("  keys options                                               the 28 items a complete key can become (offline)");
        w.WriteLine("  keys assemble --role R --count N [--execute --approval-token TOKEN]");
        w.WriteLine("  keys redeem   --role R --item ID|NAME --count N [--execute --approval-token TOKEN]");
        w.WriteLine("  shop add      --role mallbot --item ID --qty N --price P [--limit L] [--execute --approval-token TOKEN]");
    }

    /// <summary>keys status|options|assemble|redeem — Crimbo 2023 pirate encryption keys.</summary>
    internal static async Task<int> KeysAsync(string[] args)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        var rest = args.Length > 0 ? args[1..] : args;
        switch (sub)
        {
            case "status": return await KeysStatusAsync(rest);
            case "options":
                StrictOptions.Only(rest, Array.Empty<string>(), Array.Empty<string>());
                WriteJson(new { command = "keys options", options = PirateKeys.RareOptions.Select(o => new { itemId = o.Option, name = o.Text }) });
                return 0;
            case "assemble": return await KeysAssembleAsync(rest);
            case "redeem": return await KeysRedeemAsync(rest);
            default:
                PrintKeysHelp(Console.Error);
                return 2;
        }
    }

    /// <summary>Crimbo 2023 pirate key item ids (complete key 11473, parts 11386-11389).</summary>
    internal const int PirateKeyAlpha = 11386, PirateKeyComplete = 11473;

    private static object CountsJson(PirateKeyCounts c) => new
    {
        parts = new { alpha = c.Alpha, bravo = c.Bravo, charlie = c.Charlie, delta = c.Delta },
        completeKeys = c.Keys,
        assemblableSets = c.Parts
    };

    /// <summary>The 0.4 rule: an id from the fixed list, or a name (exact, else a unique part of it).</summary>
    internal static ChoiceOption? ResolveRare(string idOrName, out string? problem)
    {
        var options = PirateKeys.RareOptions;
        problem = null;
        var text = (idOrName ?? "").Trim();
        if (int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id))
        {
            var byId = options.FirstOrDefault(o => o.Option == id);
            if (byId is null) problem = $"{id} is not one of the {options.Count} items the key offers.";
            return byId;
        }
        var exact = options.FirstOrDefault(o => string.Equals(o.Text, text, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        var partial = text.Length == 0 ? Array.Empty<ChoiceOption>() : options.Where(o => o.Text.Contains(text, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (partial.Length == 1) return partial[0];
        problem = partial.Length == 0
            ? $"'{text}' is not one of the {options.Count} items the key offers."
            : $"'{text}' matches several items: {string.Join(", ", partial.Select(o => $"{o.Text} ({o.Option})"))}.";
        return null;
    }

    private static async Task<int> KeysStatusAsync(string[] args)
    {
        StrictOptions.Only(args, new[] { "--role" }, Array.Empty<string>());
        var role = StrictOptions.Role(args);
        var (client, session) = await OpenAsync(role);
        await using var _ = client;
        await using var __ = session;
        var counts = await session.PirateKeys.GetCountsAsync();
        WriteJson(new { command = "keys status", readOnly = true, role = role.ToString(), IsStale = false, FetchedAt = DateTimeOffset.UtcNow, counts = CountsJson(counts) });
        return 0;
    }

    /// <summary>Checks --approval-token before anything is sent. Same rule as adventure run / choice submit.</summary>
    private static async Task RequireApprovalAsync(string[] args, string what)
    {
        var token = StrictOptions.Value(args, "--approval-token");
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException($"{what} requires --approval-token. Nothing was sent.");
        if (!await AgentGuard.ApprovalTokenValidAsync(token).ConfigureAwait(false))
            throw new ArgumentException("The supplied approval token does not match the stored approval token. Nothing was sent.");
    }

    private static bool Complete(PirateKeyResult r) => r.Done == r.Requested && r.Message is null && !r.Unclear;

    private static object RunJson(PirateKeyResult r) => new
    {
        requested = r.Requested,
        confirmed = r.Done,
        complete = Complete(r),
        unclear = r.Unclear,
        problem = r.Unclear && r.Message is { } m && m.StartsWith("Unclear: ", StringComparison.Ordinal) ? m["Unclear: ".Length..] : r.Message,
        before = CountsJson(r.Before),
        after = CountsJson(r.After),
        steps = r.Steps.Select(s => new { s.Number, s.Confirmed, s.Message, inventoryDelta = s.InventoryDelta.ToDictionary(d => d.Key.ToString(), d => d.Value) })
    };

    private static int RunExit(PirateKeyResult r) => r.Unclear ? 4 : Complete(r) ? 0 : 3;

    private static async Task<int> KeysAssembleAsync(string[] args)
    {
        StrictOptions.Only(args, new[] { "--role", "--count", "--approval-token" }, new[] { "--execute" });
        var role = StrictOptions.Role(args);
        var count = StrictOptions.RequiredInt(args, "--count", 1, 500, "keys to assemble");
        if (!HasFlag(args, "--execute"))
        {
            // Preview: no request at all. 'keys status' shows the counts.
            WriteJson(new { command = "keys assemble", previewOnly = true, executed = false, approvalRequired = true, role = role.ToString(), count,
                requests = $"{count} x GET inv_use.php?whichitem={PirateKeyAlpha}&ajax=1&pwd=…, each confirmed by the inventory (parts -1, complete key +1)",
                note = "Preview only; nothing was sent. 'keys status' shows parts and sets. Add --execute and --approval-token to assemble." });
            return 0;
        }
        await RequireApprovalAsync(args, "Assembling keys");
        var (client, session) = await OpenForActionAsync(role);
        await using var _ = client;
        await using var __ = session;
        var result = await session.PirateKeys.AssembleAsync(count);
        WriteJson(new { command = "keys assemble", executed = true, role = role.ToString(), result = RunJson(result) });
        return RunExit(result);
    }

    private static async Task<int> KeysRedeemAsync(string[] args)
    {
        StrictOptions.Only(args, new[] { "--role", "--item", "--count", "--approval-token" }, new[] { "--execute" });
        var role = StrictOptions.Role(args);
        var itemText = StrictOptions.Value(args, "--item") ?? throw new ArgumentException("--item ID|NAME is required (see 'keys options').");
        var count = StrictOptions.RequiredInt(args, "--count", 1, 500, "keys to redeem");
        // Resolved against the fixed list before any session is opened: an unknown item sends nothing.
        var rare = ResolveRare(itemText, out var problem)
            ?? throw new ArgumentException(problem + " Nothing was sent. See 'keys options'.");
        if (!HasFlag(args, "--execute"))
        {
            WriteJson(new { command = "keys redeem", previewOnly = true, executed = false, approvalRequired = true, role = role.ToString(),
                item = new { itemId = rare.Option, name = rare.Text }, count,
                requests = $"per key: read the key form (main.php, else GET inv_use.php?whichitem={PirateKeyComplete}&pwd=… and its one-time message page), then GET inv_use.php?whichitem={PirateKeyComplete}&qty=1&pwd=…&rare={rare.Option}, confirmed by the inventory (key -1, item +1)",
                note = "Preview only; nothing was sent. 'keys status' shows complete keys. Add --execute and --approval-token to redeem." });
            return 0;
        }
        await RequireApprovalAsync(args, "Redeeming keys");
        var (client, session) = await OpenForActionAsync(role);
        await using var _ = client;
        await using var __ = session;
        var result = await session.PirateKeys.RedeemAsync(rare.Option, count);
        WriteJson(new { command = "keys redeem", executed = true, role = role.ToString(), item = new { itemId = rare.Option, name = rare.Text }, result = RunJson(result) });
        return RunExit(result);
    }

    /// <summary>shop add --role R --item ID --qty N --price P [--limit L] [--execute --approval-token T]</summary>
    private static async Task<int> ShopAddAsync(string[] args)
    {
        StrictOptions.Only(args, new[] { "--role", "--item", "--qty", "--price", "--limit", "--approval-token" }, new[] { "--execute" });
        var role = StrictOptions.Value(args, "--role") is null ? AccountRole.Mallbot : StrictOptions.Role(args);
        var item = StrictOptions.RequiredInt(args, "--item", 1, int.MaxValue, "ITEM_ID");
        var qty = StrictOptions.RequiredInt(args, "--qty", 1, 100_000, "amount to list");
        var price = StrictOptions.RequiredInt(args, "--price", 1, 999_999_999, "meat per item");
        var limit = StrictOptions.Int(args, "--limit", 0, 0, 100_000);
        if (!HasFlag(args, "--execute"))
        {
            WriteJson(new { command = "shop add", previewOnly = true, executed = false, approvalRequired = true, role = role.ToString(),
                item, quantity = qty, price, limit,
                requests = "read backoffice.php?which=1, POST backoffice.php action=additem, read backoffice.php?which=1 again (listed quantity must rise by --qty at --price)",
                note = "Preview only; nothing was sent. 'shop --role mallbot' shows the current listings. An item already listed at another price is refused. Add --execute and --approval-token to list." });
            return 0;
        }
        await RequireApprovalAsync(args, "Listing in the store");
        var (client, session) = await OpenForActionAsync(role);
        await using var _ = client;
        await using var __ = session;
        var before = (await session.Shop.GetListingsAsync(refresh: true)).FirstOrDefault(l => l.Item.Id == item);
        var result = await session.Shop.AddAsync(item, qty, price, limit);
        var confirmed = result.Status == ActionStatus.Done;
        var unclear = result.Status == ActionStatus.Unclear;
        ShopListing? after = null;
        if (confirmed || unclear)
        {
            try { after = (await session.Shop.GetListingsAsync(refresh: true)).FirstOrDefault(l => l.Item.Id == item); }
            catch (KolibriException) { }
        }
        var outcome = new
        {
            sent = confirmed || unclear || (result.Rejection is { } r && !r.Contains("Nothing was sent", StringComparison.OrdinalIgnoreCase)
                                           && !r.Contains("already listed", StringComparison.OrdinalIgnoreCase)),
            confirmed,
            unclear,
            problem = confirmed ? null : result.Rejection,
            itemId = item,
            quantity = qty,
            price,
            quantityBefore = before?.Quantity ?? 0,
            quantityAfter = after?.Quantity,
            priceAfter = after?.Price,
            storeMessage = (string?)null
        };
        WriteJson(new { command = "shop add", executed = outcome.sent, role = role.ToString(), outcome });
        return unclear ? 4 : confirmed ? 0 : 3;
    }
}
