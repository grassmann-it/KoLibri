internal static partial class KolibriCli
{
    /// <summary>Stub: P3-W2 ports reprice/compare onto KoLibri.Extras. Until then it sends nothing and exits 3.</summary>
    internal static Task<int> RepriceCommandAsync(string[] args, bool compareMode)
        => Task.FromResult(NotYet(compareMode ? "compare" : "reprice"));
}
