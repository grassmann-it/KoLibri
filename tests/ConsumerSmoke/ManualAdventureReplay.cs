using System.Net;
using LibKolibri;
using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;

internal static class ManualAdventureReplay
{
    public static async Task RunAsync()
    {
        var root = Directory.CreateTempSubdirectory("kolibri-consumer-replay-");
        try
        {
            var baseUri = new Uri("https://adventure.test/");
            using var audit = new AuditEventJournal(Path.Combine(root.FullName, "audit.jsonl"));
            var account = new AccountSession(
                new AccountProfile { Id = "fixture", DisplayName = "Fixture", Role = AccountRole.Mallbot },
                new AccountCredentials());
            using var handler = new ReplayHandler();
            await using var session = new ManualAdventureSession(KoLAdventureSessionFactory.Create(
                account, new HttpClient(handler, disposeHandler: false) { BaseAddress = baseUri },
                new DelegateAdventureApprovalHook((_, _) =>
                    ValueTask.FromResult(AdventureApprovalDecision.Allow("fixture-approval"))),
                new DelegateAdventureVerificationHook((_, _) =>
                    ValueTask.FromResult(AdventureVerificationResult.Success())),
                new JsonFileAdventureRunCheckpointStore(Path.Combine(root.FullName, "runs")), audit,
                new AdventureHttpTransportOptions { BaseUri = baseUri, AllowedHosts = new[] { baseUri.Host } }));
            var policy = new AdventureActionAllowlist { WaitForManualChoice = true }
                .AddAdventure("fixture-zone").AddFightAction("fixture-zone", "attack");
            const string runId = "fixture-run";
            var start = await session.StartAsync(
                new AdventureRunRequest { RunId = runId, AdventureId = "fixture-zone", Snarfblat = 543 }, policy);
            Require(start.Encounter?.Kind == AdventureEncounterKind.Fight, "Expected a fight.");
            var available = start.Encounter!.Combat!.AvailableActions.Where(action => action.IsEnabled)
                .Select(action => action.Kind is CombatActionKind.Skill or CombatActionKind.Item
                    ? $"{action.Kind}:{action.Id}" : action.Kind.ToString())
                .Distinct(StringComparer.Ordinal);
            Require(available.SequenceEqual(new[] { "Attack", "Skill:1001", "Item:2002" }),
                "Unexpected fight actions.");
            var fight = await session.ExecuteAsync(runId, policy, new SkillStepSpec("1001", Round: 1));
            Require(fight.Encounter?.Kind == AdventureEncounterKind.Choice && fight.Encounter.ChoiceId == "1234"
                && fight.Encounter.AvailableChoices.SequenceEqual(new[] { "1", "2" }), "Expected choice 1234 and two options.");
            var finish = await session.ExecuteAsync(runId, policy, new ChoiceStepSpec("1234", "2"));
            Require(finish.IsComplete && finish.Drops.Count == 1 &&
                finish.Drops[0] is { ItemName: "knucklebone", Quantity: 1 }, "Expected one knucklebone.");
            var status = await session.ReadStatusAsync();
            Require(status.Adventures == 9 && status.CurrentHp == 80 && status.CurrentMp == 40,
                "Unexpected character status after adventure.");
            Require(handler.Count == 4, "Unexpected request count.");
            Console.WriteLine("NuGet adventure replay passed: fight, choice, loot, 9 adventures left.");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ReplayHandler : HttpMessageHandler
    {
        private static readonly (HttpMethod Method, string Path, string Field, string? Fixture)[] Sequence =
        {
            (HttpMethod.Post, "/adventure.php", "snarfblat=543", "combat-round-1.html"),
            (HttpMethod.Post, "/fight.php", "whichskill=1001", "choice-success.html"),
            (HttpMethod.Post, "/choice.php", "option=2", "reward.html"),
            (HttpMethod.Get, "/api.php", "", null)
        };

        public int Count { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Require(Count < Sequence.Length, "Extra request during replay.");
            var expected = Sequence[Count++];
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Require(request.Method == expected.Method && request.RequestUri?.AbsolutePath == expected.Path
                && body.Contains(expected.Field, StringComparison.Ordinal), $"Unexpected request for {expected.Path}.");
            var response = expected.Fixture is { } file
                ? await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Replay", file), cancellationToken)
                : """{"name":"Fixture","adventures":"9","hp":"80","maxhp":"100","mp":"40","maxmp":"50"}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(response)
            };
        }
    }
}
