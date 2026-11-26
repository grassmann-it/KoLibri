using KoLibri;

internal static partial class KolibriCli
{
    /// <summary>MCP's HTTP host defaults to 8765, so the relay uses a different default.</summary>
    internal const int RelayDefaultPort = 8766;

    /// <summary>
    /// relay --role R [--port 8766] [--log FILE] [--browser-ua]
    /// Local reverse proxy: opens the stored KoLibri session in your own browser at http://127.0.0.1:PORT/
    /// and logs every request (names and a few safe values only; never cookies, hashes or free values).
    /// The first URL printed contains a per-run secret; only the browser that opened it can use the relay.
    /// </summary>
    private static async Task<int> RelayAsync(string[] args)
    {
        var role = ParseRole(GetOption(args, "--role") ?? "main");
        var port = Math.Clamp(ReadInt(args, "--port", RelayDefaultPort), 1024, 65535);
        var browserUa = HasFlag(args, "--browser-ua");
        var logPath = Path.GetFullPath(GetOption(args, "--log") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KoLibri", "relay-log.jsonl"));
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        var (client, session) = await OpenAsync(role);
        await using var _client = client;
        await using var _session = session;
        RelayServer host;
        try
        {
            host = await session.StartRelayAsync(new RelayOptions
            {
                Port = port, LogPath = logPath, UseBrowserUserAgent = browserUa, Output = Console.WriteLine
            });
        }
        catch (InvalidOperationException ex) { Console.Error.WriteLine(ex.Message); return 1; }
        await using var _ = host;
        Console.WriteLine($"KoLibri relay for {role} ({session.Account.PlayerName}) on {host.BaseUri.GetLeftPart(UriPartial.Authority)}/");
        Console.WriteLine($"Open this URL in your own browser (it carries the secret for this run): {host.EntryUri("/main.php")}");
        Console.WriteLine($"User-Agent sent to KoL: {(browserUa ? "the browser's own" : "the stored session's")}");
        Console.WriteLine($"Log: {logPath}   (Ctrl-C to stop)");
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (OperationCanceledException) { }
        return 0;
    }
}
