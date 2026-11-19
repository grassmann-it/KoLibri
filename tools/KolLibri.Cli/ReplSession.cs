using System.Text;
using KolArbitrage.Core.Accounts;
using KolArbitrage.Core.Adventure;
using KolArbitrage.Core.Analytics;
using KolArbitrage.Core.Daily;
using KolArbitrage.Core.Data;
using KolArbitrage.Core.Integration;
using KolArbitrage.Core.Pricing;

namespace KoLibri.Cli.Repl;

/// <summary>
/// Default <see cref="IReplContext"/> backed by a real KoLmafia catalog plus
/// the existing one-shot CLI helpers. Read-only; live POSTs stay in the
/// dedicated commands.
/// </summary>
public sealed class KoLibriReplContext : IReplContext
{
    private readonly KoLmafiaCatalog _catalog;
    private readonly Func<AccountRole, IAccountReadClient> _readClientFactory;
    private readonly Func<AccountRole, IMallReadClient> _mallClientFactory;

    public KoLibriReplContext(
        AccountRole role,
        KoLmafiaCatalog catalog,
        Func<AccountRole, IAccountReadClient> readClientFactory,
        Func<AccountRole, IMallReadClient> mallClientFactory)
    {
        Role = role;
        _catalog = catalog;
        _readClientFactory = readClientFactory;
        _mallClientFactory = mallClientFactory;
    }

    public AccountRole Role { get; }

    public Task<KoLStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        var client = _readClientFactory(Role);
        return client.FetchStatusAsync(forceRefresh: false, cancellationToken);
    }

    public Task<KoLInventorySnapshot> GetInventoryAsync(CancellationToken cancellationToken)
    {
        var client = _readClientFactory(Role);
        return client.FetchInventoryAsync(forceRefresh: false, cancellationToken);
    }

    public Task<MafiaItemRecord?> LookupItemAsync(string query, CancellationToken _)
        => Task.FromResult(FindItem(query));

    public Task<MafiaMonsterRecord?> LookupMonsterAsync(string query, CancellationToken _)
        => Task.FromResult(FindMonster(query));

    public DailyRoutineReport RunDailyRoutine(DailyRoutineSnapshot snapshot)
        => new DailyRoutineRunner().Run(snapshot);

    public async Task<IReadOnlyList<object>> RepricePreviewAsync(int limit, CancellationToken cancellationToken)
    {
        // Delegate to the dedicated preview helper used by the CLI. We
        // surface the same RepricePreviewResult list so callers get a
        // consistent shape, but the REPL only displays the count to keep
        // the prompt responsive.
        var mall = _mallClientFactory(Role);
        var ownShop = await mall.FetchOwnShopAsync(forceRefresh: false, cancellationToken);
        var work = ownShop.Take(limit).Select(async listing =>
        {
            var market = await mall.SearchAsync(listing.DisplayName ?? listing.ItemId, forceRefresh: false, cancellationToken: cancellationToken);
            var lowest = market.Listings.Count == 0 ? (decimal?)null : market.Listings.Min(x => (decimal)x.Price);
            return (object)new
            {
                listing.ItemId,
                listing.DisplayName,
                listing.Price,
                marketLowest = lowest,
                marketStale = market.IsStale
            };
        });
        return await Task.WhenAll(work);
    }

    public MafiaConcoctionRecord? LookupConcoction(string query)
        => _catalog.Concoctions.FirstOrDefault(c =>
            c.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

    private MafiaItemRecord? FindItem(string query)
    {
        if (int.TryParse(query, out var id))
        {
            var byId = _catalog.Items.FirstOrDefault(i => i.Id == id.ToString());
            if (byId is not null) return byId;
        }
        return _catalog.Items.FirstOrDefault(i => i.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private MafiaMonsterRecord? FindMonster(string query)
    {
        if (int.TryParse(query, out var id))
        {
            var byId = _catalog.Monsters.FirstOrDefault(m => m.Id == id.ToString());
            if (byId is not null) return byId;
        }
        return _catalog.Monsters.FirstOrDefault(m => m.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Interactive REPL host. Reads commands line-by-line from stdin and
/// dispatches them through <see cref="ReplDispatcher"/>. Designed so
/// other hosts (an Avalonia debug shell, a CI smoke run, a TUI) can call
/// <see cref="DispatchLineAsync"/> directly.
/// </summary>
public sealed class ReplSession
{
    private readonly ReplDispatcher _dispatcher = new();
    private readonly IReplContext _context;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly PromptFormatter _prompt;
    private bool _exitRequested;

    public ReplSession(IReplContext context, TextReader? input = null, TextWriter? output = null)
    {
        _context = context;
        _input = input ?? Console.In;
        _output = output ?? Console.Out;
        _prompt = new PromptFormatter("kolibri", context.Role.ToString().ToLowerInvariant());
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        _output.WriteLine("Kolibri REPL — type 'help' for commands, 'exit' to leave.");
        while (!_exitRequested && !cancellationToken.IsCancellationRequested)
        {
            _output.Write(_prompt.Render());
            var line = await _input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) break;
            var result = await DispatchLineAsync(line, cancellationToken).ConfigureAwait(false);
            _output.WriteLine(result.Text);
        }
        return _exitRequested ? 0 : 130;
    }

    public async ValueTask<ReplResult> DispatchLineAsync(string line, CancellationToken cancellationToken)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return ReplResult.Plain(string.Empty);
        var args = ParseArgs(trimmed);
        var head = args[0].ToLowerInvariant();
        if (head is "exit" or "quit" or ":q")
        {
            _exitRequested = true;
            return ReplResult.Plain("(exiting)");
        }
        return await _dispatcher.DispatchAsync(args, _context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Splits a REPL line into argv-style tokens with quote handling.</summary>
    public static string[] ParseArgs(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result.ToArray();
    }

    private sealed class PromptFormatter
    {
        private readonly string _prefix;
        private readonly string _role;
        public PromptFormatter(string prefix, string role) { _prefix = prefix; _role = role; }
        public string Render() => $"{_prefix} ({_role})> ";
    }
}
