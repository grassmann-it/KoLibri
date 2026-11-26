using System.Net;
using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;
using KoLibri.Desktop;
using LibKolibri;
using Xunit;

namespace KoLibri.Core.Tests;

/// <summary>
/// Desktop adventure batch with the configured AutoCombat policy, all against recorded pages
/// (no live requests): the policy action is sent, a choice without an answer pauses, a defeat stops.
/// </summary>
public sealed class DesktopBatchAutoCombatTests
{
    private const string Status = """{"name":"Fixture","adventures":"9","hp":"80","maxhp":"100"}""";

    private static readonly KoLibri.Desktop.AdventureLocation Zone =
        new("fixture-zone", "Fixture", "Fixture", 543, 1, Array.Empty<AdventureEncounterMetadata>());

    private static string Fixture(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { AppContext.BaseDirectory, "Fixtures" }.Concat(parts).ToArray()));

    private static AutoCombatPolicy SkillThenAttack(string skillId) => new()
    {
        Rules = new[]
        {
            new AutoCombatRule
            {
                When = new AutoCombatPredicate(),
                Then = new AutoCombatAction { Kind = AutoCombatActionKind.Skill, Id = skillId, Quantity = 1 },
                MaxUses = 1
            }
        },
        DefaultAction = new AutoCombatAction { Kind = AutoCombatActionKind.Attack, Quantity = 1 }
    };

    /// <summary>Answers by path: adventure.php, fight.php, choice.php each from their own queue; api.php with status.</summary>
    private sealed class Kol(string adventure, params string[] fights) : HttpMessageHandler
    {
        private readonly Queue<string> _fights = new(fights);
        public List<(string Path, HttpMethod Method, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (Requests)
                Requests.Add((path, request.Method, request.Content is null ? "" : request.Content.ReadAsStringAsync(ct).Result));
            await Task.Yield();
            var body = path switch
            {
                _ when path.EndsWith("adventure.php") => adventure,
                _ when path.EndsWith("fight.php") && _fights.Count > 0 => _fights.Dequeue(),
                _ when path.EndsWith("api.php") => Status,
                _ => "{}"
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body), RequestMessage = request };
        }

        public IEnumerable<(string Path, string Body)> Posts
            => Requests.Where(r => r.Method == HttpMethod.Post).Select(r => (r.Path, r.Body));
    }

    private static EncounterBrowserSessionViewModel Session(Kol kol, string root, out EncounterBrowserViewModel browser)
    {
        var endpoint = new Uri("https://adventure.test/");
        var store = new InMemoryAdventureRunCheckpointStore();
        var audit = new AuditEventJournal(Path.Combine(root, "audit.jsonl"));
        var viewModel = new EncounterBrowserSessionViewModel((role, approval, verification, _) =>
        {
            var account = new AccountSession(new AccountProfile { Id = "fixture", DisplayName = "Fixture", Role = role }, new AccountCredentials());
            var http = new HttpClient(kol, disposeHandler: false) { BaseAddress = endpoint };
            return Task.FromResult(new ManualAdventureSession(KoLAdventureSessionFactory.Create(
                account, http, approval, verification, store, audit,
                new AdventureHttpTransportOptions { BaseUri = endpoint, AllowedHosts = new[] { endpoint.Host } })));
        }, new ApprovalTokenStore(root), AccountRole.Main)
        {
            ApprovalToken = "fixture",
            // Never the user's real choice file: an empty policy has no saved answers.
            ChoiceResolverFactory = () => new ChoiceResolver(new ChoiceResolutionConfig())
        };
        browser = new EncounterBrowserViewModel(new MafiaDataLoader().Load(
            Path.Combine(AppContext.BaseDirectory, "external", "kolmafia-data")));
        return viewModel;
    }

    [Fact]
    public void Allowlist_actions_come_from_the_policy_and_default_to_attack()
    {
        Assert.Equal(new[] { "attack" }, AutoCombatAdventureAdapter.AllowedFightActionIds(null));
        Assert.Equal(new[] { "skill:1001", "attack" }, AutoCombatAdventureAdapter.AllowedFightActionIds(SkillThenAttack("1001")));
        var flee = new AutoCombatPolicy
        {
            Rules = new[]
            {
                new AutoCombatRule
                {
                    When = new AutoCombatPredicate(),
                    Then = new AutoCombatAction { Kind = AutoCombatActionKind.Item, Id = "2002", Quantity = 1 }
                }
            },
            DefaultAction = new AutoCombatAction { Kind = AutoCombatActionKind.Run }
        };
        Assert.Equal(new[] { "item:2002", "run" }, AutoCombatAdventureAdapter.AllowedFightActionIds(flee));
    }

    [Fact]
    public async Task Batch_sends_the_policy_skill_in_a_fight()
    {
        var root = Directory.CreateTempSubdirectory("kolibri-wv-skill-");
        try
        {
            var kol = new Kol(Fixture("Adventure", "combat-round-1.html"), Fixture("Adventure", "combat-reward.html"));
            var viewModel = Session(kol, root.FullName, out var browser);

            await viewModel.RunTurnsAsync(browser, Zone, 1, null, combatPolicy: SkillThenAttack("1001"));

            var fight = Assert.Single(kol.Posts, post => post.Path.EndsWith("fight.php"));
            Assert.Contains("whichskill=1001", fight.Body);
            Assert.DoesNotContain("action=attack", fight.Body);
            Assert.Equal(1, viewModel.LastReport?.TurnsUsed);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task Batch_without_a_policy_still_attacks()
    {
        var root = Directory.CreateTempSubdirectory("kolibri-wv-attack-");
        try
        {
            var kol = new Kol(Fixture("Adventure", "combat-round-1.html"), Fixture("Adventure", "combat-reward.html"));
            var viewModel = Session(kol, root.FullName, out var browser);

            await viewModel.RunTurnsAsync(browser, Zone, 1, null);

            var fight = Assert.Single(kol.Posts, post => post.Path.EndsWith("fight.php"));
            Assert.Contains("action=attack", fight.Body);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task Policy_without_a_legal_action_leaves_the_fight_open_and_shown()
    {
        var root = Directory.CreateTempSubdirectory("kolibri-wv-blocked-");
        try
        {
            var kol = new Kol(Fixture("Adventure", "combat-round-1.html"));
            var viewModel = Session(kol, root.FullName, out var browser);
            var onlyMissingSkill = new AutoCombatPolicy
            {
                DefaultAction = new AutoCombatAction { Kind = AutoCombatActionKind.Skill, Id = "9999" }
            };

            await viewModel.RunTurnsAsync(browser, Zone, 5, null, combatPolicy: onlyMissingSkill);

            Assert.DoesNotContain(kol.Posts, post => post.Path.EndsWith("fight.php"));
            Assert.Equal(nameof(BoundedAdventureRunStopReason.CombatPolicyStopped), viewModel.LastReport?.StopReason);
            Assert.True(viewModel.LastReport?.NeedsUser);
            Assert.True(viewModel.IsSessionActive); // the fight stays open for the user
            Assert.Contains("fight is still open", viewModel.LastMessage);
            Assert.Contains(browser.Actions, action => action.Kind == "attack"); // the encounter view shows the fight
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task Batch_pauses_at_a_choice_without_a_saved_answer()
    {
        var root = Directory.CreateTempSubdirectory("kolibri-wv-choice-");
        try
        {
            var kol = new Kol(Fixture("Adventure", "choice-success.html"));
            var viewModel = Session(kol, root.FullName, out var browser);

            await viewModel.RunTurnsAsync(browser, Zone, 5, null, combatPolicy: SkillThenAttack("1001"));

            Assert.Equal(new[] { "/adventure.php" }, kol.Posts.Select(post => post.Path));
            Assert.Equal(nameof(BoundedAdventureRunStopReason.NeedsDecision), viewModel.LastReport?.StopReason);
            Assert.True(viewModel.IsSessionActive);
            Assert.NotNull(viewModel.Decisions.ChoiceId);
            Assert.Contains("4 left", viewModel.LastMessage);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task Batch_stops_after_a_defeat_and_starts_no_further_turn()
    {
        var root = Directory.CreateTempSubdirectory("kolibri-wv-defeat-");
        try
        {
            var kol = new Kol(Fixture("Adventure", "combat-round-1.html"), Fixture("Adventure", "combat-death.html"));
            var viewModel = Session(kol, root.FullName, out var browser);

            await viewModel.RunTurnsAsync(browser, Zone, 5, null, combatPolicy: SkillThenAttack("1001"));

            Assert.Single(kol.Posts, post => post.Path.EndsWith("adventure.php"));
            Assert.Single(kol.Posts, post => post.Path.EndsWith("fight.php"));
            Assert.Equal(1, viewModel.LastReport?.TurnsUsed);
            Assert.Equal(4, viewModel.LastReport?.TurnsLeft);
        }
        finally { root.Delete(recursive: true); }
    }
}
