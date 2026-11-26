using KolLibri.Mcp;
using KoLibri.Core.Spiel;
using KoLibri.Safety.Access;

// kolibri-mcp                       HTTP on 127.0.0.1:8765 (default; AI access needs approval in the browser)
// kolibri-mcp --port 9000 [--no-code]
// kolibri-mcp --stdio               read-only tools over stdio, no approvals needed
// kolibri-mcp --experimental        also offer arbitrage, bribes, CRM, reprice, analytics (or KOLIBRI_EXPERIMENTAL=1)
// kolibri-mcp grants list|revoke <id>
if (args.Length > 0 && args[0] == "grants") return GrantsCommand(args);

var options = new KolibriMcpServerOptions { ExperimentalTools = McpToolSurface.ExperimentalRequested(args) };

if (args.Contains("--stdio"))
{
    var stdioRuntime = await McpRuntimeBootstrap.CreateConfiguredAsync();
    return await KolibriMcpServer.RunStdioAsync(stdioRuntime, options);
}

var port = 8765;
var portIndex = Array.IndexOf(args, "--port");
if (portIndex >= 0 && (portIndex + 1 >= args.Length || !int.TryParse(args[portIndex + 1], out port) || port is < 1 or > 65535))
{
    Console.Error.WriteLine("--port needs a number between 1 and 65535.");
    return 2;
}

using var binder = await McpRuntimeBootstrap.CreateBinderAsync();
var runtime = binder.Runtime;
var grants = new GrantStore();
var gate = new AccessGate(grants, new UsageLedger());
await using var adventures = new KoLAdventureGatewayFactory();
using var bridge = new AdventureMcpExecutionBridge(adventures, new InMemoryAdventureRunCheckpointStore());
var server = new KolibriMcpServer(runtime, options, executionBridge: bridge) { BeforeToolCall = binder.RefreshAsync };
server.Tools.Remove("kol_plan_daily_adventures_execute");
server.Tools.Remove("kol_login_clear");     // session management stays with the user
server.Tools.Remove("kol_login_refresh");   // runs on a mock transport, not live
server.EnableWriteTools(gate, new KolibriClientActions());
using var host = new McpHttpHost(server, grants, gate, new McpOAuthServer(grants), port, requireCode: !args.Contains("--no-code"));
host.Start();
Console.Error.WriteLine($"KoLibri MCP: {host.BaseUrl}/mcp  (nur lokal erreichbar)");
Console.Error.WriteLine(options.ExperimentalTools
    ? $"Experimental tools are ON ({server.Tools.Tools.Count} tools)."
    : $"{server.Tools.Tools.Count} core tools. Arbitrage, CRM, reprice and analytics: --experimental or {McpToolSurface.ExperimentalVariable}=1.");
Console.Error.WriteLine("On first connect the client opens a browser page where you grant rights and limits.");
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
await host.RunAsync(cts.Token);
return 0;

static int GrantsCommand(string[] args)
{
    var store = new GrantStore();
    var ledger = new UsageLedger();
    if (args.Length >= 3 && args[1] == "revoke")
    {
        Console.WriteLine(store.Revoke(args[2]) ? "revoked" : "not found or already revoked");
        return 0;
    }
    foreach (var g in store.List())
    {
        var state = g.RevokedAt is not null ? "revoked" : g.ExpiresAt <= DateTimeOffset.UtcNow ? "expired" : "active";
        Console.WriteLine($"{g.Id}  {state,-8} {g.ClientName}  accounts={string.Join(',', g.Accounts)}  until {g.ExpiresAt:u}");
        foreach (var (right, limit) in g.Rights)
        {
            var used = ledger.UsedToday(g.Id, right);
            Console.WriteLine($"    {right,-15} {(limit.DailyBudget is { } b ? $"{used}/{b} today" : "read")}"
                + (limit.MaxUnitPrice is { } m ? $", max price {m}" : "") + (limit.MeatReserve is { } r ? $", reserve {r}" : ""));
        }
    }
    return 0;
}
