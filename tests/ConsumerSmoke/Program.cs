using System.Text.Json;
using KolArbitrage.Core.Data;
using KolArbitrage.Core.Security;
using LibKolibri;

Console.WriteLine("--- KoLibri consumer smoke test ---");

// 1. Load KoLmafia data from the vendor snapshot.
var loader = new MafiaDataLoader();
var dataDir = Path.Combine(AppContext.BaseDirectory, "external", "kolmafia-data");
if (!Directory.Exists(dataDir))
{
    Console.Error.WriteLine($"Vendor directory '{dataDir}' is missing.");
    return 1;
}
loader.Load(dataDir, out var diagnostics);
Console.WriteLine($"Loaded catalog with {diagnostics.WarningCount} parse warnings.");

// 2. Token round-trip via ApprovalTokenStore.
var tokenRoot = Path.Combine(Path.GetTempPath(), "kolibri-consumer-" + Guid.NewGuid().ToString("N"));
try
{
    using var store = new ApprovalTokenStore(tokenRoot);
    await store.SaveAsync(new ApprovalToken("demo-token", DateTimeOffset.UtcNow, "smoke"));
    var loaded = await store.LoadAsync();
    if (loaded?.Token != "demo-token")
    {
        Console.Error.WriteLine("Token round-trip failed.");
        return 2;
    }
    Console.WriteLine("Approval token round-trip succeeded.");
}
finally
{
    if (Directory.Exists(tokenRoot)) Directory.Delete(tokenRoot, recursive: true);
}

// 3. Reach into LibKolibri's public API to make sure the surface is real.
var runtime = new LibKolibriRuntime();
Console.WriteLine($"LibKolibri runtime instance: {runtime.GetType().FullName}");

Console.WriteLine("All consumer smoke checks passed.");
return 0;
