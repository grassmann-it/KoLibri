using KoLibri.Core.Basis;
using KoLibri.Core.Markt;

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
        var ledger = new SharedMallSpendLedger();
        var account = role.ToString();
        var amount = ledger.UnclearAmount(account);
        if (amount < 0) return Fail($"The spend ledger cannot be read: {ledger.Problem}");

        long? meat = null;
        try
        {
            var client = await ReadClientAsync(role);
            meat = (await client.Api.FetchStatusAsync(forceRefresh: true)).Meat;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.Error.WriteLine($"Fresh Meat could not be read: {exception.Message}");
        }

        WriteJson(new { account, unclearMeat = amount, meatNow = meat, ledger = SharedMallSpendLedger.DefaultPath() });
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
        if (!ledger.ResolveUnclear(account, amount, settle))
            return Fail("The unclear amount changed meanwhile. Nothing was changed; run the command again.");
        Console.Error.WriteLine(settle ? $"{amount:N0} Meat counted as spent." : $"{amount:N0} Meat released.");
        return 0;
    }
}
