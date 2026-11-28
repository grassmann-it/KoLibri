using System.Net;
using System.Text;

namespace KoLibri.Tests;

/// <summary>An offline KoL: answers by "METHOD page" (api.php by "what"), records every request.</summary>
internal sealed class FakeKol : HttpMessageHandler
{
    private readonly Dictionary<string, Queue<Func<HttpRequestMessage, string, HttpResponseMessage>>> _routes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<HttpRequestMessage, string, HttpResponseMessage>> _last = new(StringComparer.OrdinalIgnoreCase);

    public List<(string Method, string Path, string Form)> Requests { get; } = new();

    public int StatusAdventures { get; set; } = 50;
    public int StatusHp { get; set; } = 100;
    public int StatusMaxHp { get; set; } = 100;
    public int Fullness { get; set; }
    public int FullnessLimit { get; set; } = 15;
    public long Meat { get; set; } = 10_000;

    private readonly HashSet<string> _defaults = new(StringComparer.OrdinalIgnoreCase);

    public FakeKol()
    {
        On("GET api.php?what=status", (_, _) => Html(StatusJson()));
        On("GET api.php?what=inventory", (_, _) => Html("{}"));
        _defaults.UnionWith(_routes.Keys);
    }

    public string StatusJson() =>
        $$"""{"name":"Tester","playerid":"1001","level":7,"meat":{{Meat}},"adventures":{{StatusAdventures}},"hp":{{StatusHp}},"maxhp":{{StatusMaxHp}},"mp":40,"maxmp":50,"full":{{Fullness}},"fullnessmax":{{FullnessLimit}},"drunk":0,"drunkmax":14,"spleen":0,"spleenmax":15,"pwd":"0123456789abcdef","familiar":"0","equipment":{"hat":"3","weapon":"0"},"effects":{} }""";

    /// <summary>Adds a response for a route; several calls queue answers, the last one repeats.</summary>
    public FakeKol On(string route, Func<HttpRequestMessage, string, HttpResponseMessage> respond)
    {
        if (_defaults.Remove(route)) _routes.Remove(route);
        if (!_routes.TryGetValue(route, out var queue)) _routes[route] = queue = new();
        queue.Enqueue(respond);
        _last[route] = respond;
        return this;
    }

    public FakeKol On(string route, string html) => On(route, (_, _) => Html(html));

    public static HttpResponseMessage Html(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    public static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found) { Content = new StringContent("") };
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    public int Count(string method, string page)
        => Requests.Count(r => r.Method == method && r.Path.Split('?')[0].TrimStart('/').Equals(page, StringComparison.OrdinalIgnoreCase));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var form = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var uri = request.RequestUri!;
        Requests.Add((request.Method.Method, uri.PathAndQuery, form));
        var page = uri.AbsolutePath.TrimStart('/');
        var key = $"{request.Method.Method} {page}";
        if (page == "api.php")
        {
            var what = System.Web.HttpUtility.ParseQueryString(uri.Query)["what"];
            key += "?what=" + what;
        }
        Func<HttpRequestMessage, string, HttpResponseMessage>? respond = null;
        if (_routes.TryGetValue(key, out var queue))
            respond = queue.Count > 0 ? queue.Dequeue() : _last[key];
        var response = respond?.Invoke(request, form) ?? Html("<html><body>nothing here</body></html>");
        response.RequestMessage = request;
        return response;
    }
}

/// <summary>Keeps the session action lane occupied until the test releases the hold request.</summary>
internal sealed class HeldActionLane(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.AbsolutePath != "/hold.php")
            return await base.SendAsync(request, cancellationToken);

        Started.TrySetResult();
        await Release.Task.WaitAsync(cancellationToken);
        return FakeKol.Html("ok");
    }
}

internal sealed class MemoryAccountStore : IAccountStore
{
    private readonly Dictionary<string, AccountRecord> _records = new(StringComparer.OrdinalIgnoreCase);

    public static MemoryAccountStore WithMain(string? password = null)
    {
        var store = new MemoryAccountStore();
        store._records["main"] = new AccountRecord(
            new Account(new AccountId("main"), "Tester", 1001, null, Array.Empty<string>()), "PHPSESSID=abc", password, null);
        return store;
    }

    public IReadOnlyDictionary<string, AccountRecord> Records => _records;

    public Task<IReadOnlyList<AccountRecord>> LoadAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<AccountRecord>>(_records.Values.ToArray());

    public Task SaveAsync(AccountRecord record, CancellationToken cancellationToken)
    {
        _records[record.Account.Id.Value] = record;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(AccountId id, CancellationToken cancellationToken)
    {
        _records.Remove(id.Value);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingGuard : IActionGuard
{
    public Func<GuardedAction, GuardDecision> Decide { get; set; } = _ => GuardDecision.Allow;
    public List<GuardedAction> Checked { get; } = new();
    public List<(GuardedAction Action, bool Succeeded)> Completed { get; } = new();

    public ValueTask<GuardDecision> CheckAsync(GuardedAction action, CancellationToken cancellationToken)
    {
        Checked.Add(action);
        return ValueTask.FromResult(Decide(action));
    }

    public ValueTask CompletedAsync(GuardedAction action, bool succeeded, CancellationToken cancellationToken)
    {
        Completed.Add((action, succeeded));
        return ValueTask.CompletedTask;
    }
}

internal static class TestClient
{
    public static KolibriClient Create(HttpMessageHandler kol, IAccountStore? store = null, IActionGuard? guard = null, string? state = null)
        => KolibriClient.Create(new KolibriOptions
        {
            HttpHandler = kol,
            AccountStore = store ?? MemoryAccountStore.WithMain(),
            ActionGuard = guard,
            StateDirectory = state ?? Directory.CreateTempSubdirectory("kolibri-tests-").FullName,
            DisableLimiterForTests = true
        });
}
