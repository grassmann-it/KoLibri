using KoLibri;
using KoLibri.Safety;

// Consumer smoke test: uses only the packed KoLibri and KoLibri.Safety NuGets, offline against a fake KoL.
// Each check prints OK/FAIL; the exit code is the number of failed checks (capped at 100).
const int ExpectedRecipes = 4;

var failures = 0;
void Pass(string name, string detail = "") => Console.WriteLine($"OK    {name}{(detail.Length > 0 ? " - " + detail : "")}");
void Fail(string name, string why) { failures++; Console.WriteLine($"FAIL  {name} - {why}"); }
async Task Check(string name, Func<Task<string?>> body)
{
    try { Pass(name, await body() ?? ""); }
    catch (Exception e) { Fail(name, $"{e.GetType().Name}: {e.Message}"); }
}

Console.WriteLine("--- KoLibri consumer smoke test ---");
var temp = Directory.CreateTempSubdirectory("kolibri-consumer-smoke-").FullName;
var fake = new FakeKol();
KolibriClient NewClient(IActionGuard? guard = null, string? sub = null) => KolibriClient.Create(new KolibriOptions
{
    HttpHandler = fake,
    StateDirectory = Path.Combine(temp, sub ?? Guid.NewGuid().ToString("N")),
    ActionGuard = guard,
    Limiter = new LimiterOptions { ActionInterval = TimeSpan.Zero, ReadInterval = TimeSpan.Zero },
});

try
{
    await Check("1 login offline", async () =>
    {
        await using var client = NewClient();
        await using var session = await client.LoginAsync("Tester", "secret", new LoginOptions { AccountId = "main" });
        if (session.Account.PlayerId != 1001) throw new InvalidOperationException($"player id {session.Account.PlayerId}");
        return $"account {session.Account.Id.Value}";
    });

    await Check("2 game data from the package", async () =>
    {
        await using var client = NewClient();
        var item = client.Data.FindItem("seal tooth") ?? throw new InvalidOperationException("seal tooth not found (data not copied next to the app?)");
        return $"seal tooth = item {item.Item.Id}";
    });

    await Check("3 safety guard denies", async () =>
    {
        // No approval token was presented, so the guard refuses the kmail before any request is sent.
        var guard = new CompositeGuard(new ApprovalGuard(new HashSet<GuardedActionKind> { GuardedActionKind.SendMail }, TimeSpan.FromMinutes(5)));
        await using var client = NewClient(guard);
        await using var session = await client.LoginAsync("Tester", "secret", new LoginOptions { AccountId = "main" });
        var before = fake.Requests.Count;
        try
        {
            await session.Mail.SendAsync(new MailMessage("Someone", "hello"));
        }
        catch (ActionDeniedException e)
        {
            if (fake.Requests.Skip(before).Any(r => r.Contains("sendmessage"))) throw new InvalidOperationException("a request was sent although denied");
            return $"denied: {e.Reason}";
        }
        throw new InvalidOperationException("the kmail was not denied");
    });

    var recipes = new (string Name, Func<KolibriSession, TextWriter, Task<int>> Run)[]
    {
        ("Status", (s, o) => Cookbook.Status.Recipe.RunAsync(s, new Cookbook.Status.StatusOptions { ShowInventory = true }, o)),
        ("Events", (s, o) => Cookbook.Events.Recipe.RunAsync(s, new Cookbook.Events.EventsOptions(), o)),
        ("FarmLoop (dry run)", (s, o) => Cookbook.FarmLoop.Recipe.RunAsync(s, new Cookbook.FarmLoop.FarmLoopOptions(), o)),
        ("KmailBot (dry run)", (s, o) => Cookbook.KmailBot.Recipe.RunAsync(s, new Cookbook.KmailBot.KmailBotOptions(), o)),
    };
    if (recipes.Length < ExpectedRecipes) Fail("4 recipes", $"expected {ExpectedRecipes}, wired {recipes.Length}");
    foreach (var (name, run) in recipes)
    {
        await Check($"4 recipe {name}", async () =>
        {
            await using var client = NewClient();
            await using var session = await client.LoginAsync("Tester", "secret", new LoginOptions { AccountId = "main" });
            var output = new StringWriter();
            var code = await run(session, output);
            if (code != 0) throw new InvalidOperationException($"exit code {code}; output: {output}");
            return $"{output.ToString().Split('\n').Length - 1} lines";
        });
    }
}
finally
{
    try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
}

Console.WriteLine(failures == 0 ? "All consumer smoke checks passed." : $"{failures} check(s) failed.");
return Math.Min(failures, 100);
