using KoLibri.Core.Spiel;

internal static partial class KolibriCli
{
    private static async Task<int> MafiaScriptAsync(string[] args)
    {
        if (args.Length == 0)
            return Fail("mafia-script FILE [--role main|mallbot]");

        var role = ParseRole(GetOption(args, "--role") ?? "main");
        var file = args[0];
        using var script = File.OpenText(file);
        HttpClient? http = null;
        KoLAccountReadClient? reader = null;
        try
        {
            await MafiaReadScript.ExecuteAsync(script, Console.Out,
                async token => await (await GetReaderAsync()).FetchStatusAsync(forceRefresh: true, token),
                async (storage, token) =>
                {
                    var account = await GetReaderAsync();
                    return storage ? await account.FetchStorageAsync(token)
                        : await account.FetchInventoryAsync(forceRefresh: true, token);
                });
            return 0;
        }
        finally
        {
            http?.Dispose();
        }

        async Task<KoLAccountReadClient> GetReaderAsync()
        {
            if (reader is not null) return reader;
            http = await ReadAuthenticatedHttpAsync(role);
            reader = new KoLAccountReadClient(http, itemNameResolver: BuildItemNameResolver()) { ReadCharsheetForOrganLimits = true };
            return reader;
        }
    }
}
