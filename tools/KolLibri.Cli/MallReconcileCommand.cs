using KoLibri.Extras;

internal static partial class KolibriCli
{
    /// <summary>
    /// <c>mall-reconcile [--role R] [--settle|--release]</c>: shows the unclear Meat of a Mall purchase counted against
    /// today's budget with fresh Meat. With <c>--settle</c> (it was bought) or <c>--release</c> (it was not) and a
    /// confirmation showing the amount, it resolves the entry. Never resolves anything without that confirmation.
    /// </summary>
    private static async Task<int> MallReconcileAsync(string[] args)
    {
        var role = ParseRole(GetOption(args, "--role") ?? "mallbot");
        var settle = HasFlag(args, "--settle");
        var release = HasFlag(args, "--release");
        if (settle && release) return Fail("Use either --settle or --release, not both.");
        var ledgerPath = ConfiguredStateDirectory is { } state
            ? Path.Combine(state, "mall-spend-ledger.json") : SharedMallSpendLedger.DefaultPath();
        var ledger = new SharedMallSpendLedger(ledgerPath);
        var account = role.ToString();
        // One snapshot (KoL day, revision, unclear meat and reservations of other or crashed processes): the
        // confirmation below applies to exactly that state.
        if (!ledger.TryReadUnclear(account, out var snapshot)) return Fail($"The spend ledger cannot be read: {ledger.Problem}");
        var amount = snapshot.Total;

        long? meat = null;
        try
        {
            var (client, session) = await OpenAsync(role);
            await using var _ = client;
            await using var __ = session;
            meat = (await session.Character.GetStatusAsync(refresh: true)).Meat;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.Error.WriteLine($"Fresh Meat could not be read: {exception.Message}");
        }

        WriteJson(new { account, unclearMeat = amount, meatNow = meat, ledger = ledgerPath });
        if (amount == 0) { Console.Error.WriteLine("No unclear Mall purchases today."); return 0; }
        if (!settle && !release)
        {
            Console.Error.WriteLine("Check your inventory, then run with --settle (it was bought) or --release (it was not).");
            return 0;
        }

        var question = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{account}: {amount:N0} Meat unclear. {(settle ? "Count as SPENT" : "RELEASE as not bought")}? (yes/no): ");
        var answer = AgentGuard.ConfirmOnTerminal(question);
        if (answer is null)
        {
            if (Console.IsInputRedirected) return Fail("No terminal reachable for the confirmation. Nothing was changed.");
            Console.Error.Write(question);
            var line = Console.ReadLine()?.Trim().ToLowerInvariant();
            answer = line is "y" or "yes" or "j" or "ja";
        }
        if (answer is not true) { Console.Error.WriteLine("Cancelled. Nothing was changed."); return 3; }
        if (!ledger.Reconcile(account, snapshot, settle ? amount : 0))
            return Fail("The unclear amount changed meanwhile. Nothing was changed; run the command again.");
        Console.Error.WriteLine(settle ? $"{amount:N0} Meat counted as spent." : $"{amount:N0} Meat released.");
        return 0;
    }
}
