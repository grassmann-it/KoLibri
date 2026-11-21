using LibKolibri;
using KolArbitrage.Core.Basis;

// A minimal consumer of LibKolibri: open the stored session, read the account, look at the mall.
//   dotnet run --project sample/KolLibri.Sample -- main
//   dotnet run --project sample/KolLibri.Sample -- main --meme-shop      (writes: runs today's meme shop)
//   dotnet run --project sample/KolLibri.Sample -- main --login USER     (asks for the password)

var role = args.Contains("mallbot") ? AccountRole.Mallbot : AccountRole.Main;

KolibriClient client;
var loginIndex = Array.IndexOf(args, "--login");
if (loginIndex >= 0 && loginIndex + 1 < args.Length)
{
    Console.Error.Write("Password: ");
    var password = Console.ReadLine() ?? string.Empty;
    client = await KolibriClient.LoginAsync(role, args[loginIndex + 1], password);
}
else
{
    try { client = await KolibriClient.OpenAsync(role); }
    catch (KolibriLoginRequiredException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }
}

using (client)
{
    var status = await client.GetStatusAsync();
    var inventory = await client.GetInventoryAsync();
    Console.WriteLine($"{status.PlayerName}: level {status.Level}, {status.Meat} meat, "
        + $"{status.Adventures} adventures, {inventory.Items.Count} inventory entries.");

    var mall = await client.SearchMallAsync("11-leaf clover");
    var cheapest = mall.Listings.OrderBy(l => l.Price).FirstOrDefault();
    Console.WriteLine(cheapest is null
        ? "No clover listing in the mall."
        : $"Cheapest 11-leaf clover: {cheapest.Price} meat ({cheapest.Quantity} offered).");

    if (args.Contains("--meme-shop"))
    {
        var run = await client.RunMemeShopAsync(topUpBacon: false);
        Console.WriteLine($"Meme shop: {run.Verdict}");
        foreach (var step in run.Steps)
            Console.WriteLine($"  {(step.Accepted ? "ok  " : "FAIL")} {step.Description}");
    }
}
return 0;
