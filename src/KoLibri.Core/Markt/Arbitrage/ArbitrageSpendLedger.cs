namespace KoLibri.Core.Markt;

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
