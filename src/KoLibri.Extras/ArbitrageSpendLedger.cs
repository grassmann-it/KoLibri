// Moved from KoLibri.Core (0.5 step 3, docs/lib-api-0.5.md §6.2). The Core copy stays until MCP and Desktop move over.
namespace KoLibri.Extras;

/// <summary>
/// Meat spent through arbitrage purchases per account and KoL day. See <see cref="FileSpendLedger"/> for the
/// cross-process transaction, fail-closed and reservation rules.
/// </summary>
public sealed class ArbitrageSpendLedger(string? path = null, TimeProvider? clock = null, int lockTimeoutMs = FileSpendLedger.DefaultLockTimeoutMs)
    : FileSpendLedger(path, clock, lockTimeoutMs)
{
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KoLibri", "arbitrage-ledger.json");
}
