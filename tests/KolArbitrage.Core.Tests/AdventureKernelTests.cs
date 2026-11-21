using Xunit;
using KolArbitrage.Core.Spiel;

namespace KolArbitrage.Core.Tests;

public sealed class AdventureKernelTests
{
    private static readonly string FixtureRoot = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "Adventure");

    [Fact]
    public void FixtureFightProducesOnlyAllowlistedAction()
    {
        var parser = new KoLAdventureEncounterParser();
        var encounter = parser.Parse(
            "skeleton",
            new Uri("https://example.invalid/fight.php"),
            File.ReadAllText(Path.Combine(FixtureRoot, "fight.php.html")));
        var observation = AdventureKernelObservation.From(encounter) with
        {
            EncounterId = "skeleton",
            MonsterId = "skeleton"
        };
        var allowlist = new AdventureActionAllowlist()
            .AddAdventure("skeleton")
            .AddFightAction("skeleton", "attack", "skeleton", round: 1, monsterId: "skeleton");
        var kernel = new AdventureRunKernel();

        var started = kernel.Begin(new AdventureRunRequest
        {
            RunId = "fixture-fight",
            AdventureId = "skeleton",
            DailyLimit = new AdventureDailyLimitContract
            {
                MaxTurns = 1,
                MaxRuns = 1
            }
        }, allowlist);
        var next = kernel.Observe(started.State, observation, allowlist);

        var action = Assert.IsType<AdventureFightAction>(Assert.Single(next.Actions));
        Assert.Equal("attack", action.ActionId);
        Assert.Equal(AdventureRunPhase.Fight, next.State.Phase);
        Assert.Equal(encounter.ResponseHash, next.Checkpoint.LastResponseHash);
    }

    [Fact]
    public void FixtureChoiceWithUnapprovedOptionAbortsWithoutAction()
    {
        var parser = new KoLAdventureEncounterParser();
        var encounter = parser.Parse(
            "skeleton",
            new Uri("https://example.invalid/choice.php"),
            File.ReadAllText(Path.Combine(FixtureRoot, "choice.php.html")));
        var observation = AdventureKernelObservation.From(encounter) with
        {
            EncounterId = "skeleton"
        };
        var allowlist = new AdventureActionAllowlist()
            .AddAdventure("skeleton")
            .AddChoiceOption("skeleton", "1234", "9", "skeleton");
        var kernel = new AdventureRunKernel();
        var started = kernel.Begin(new AdventureRunRequest
        {
            RunId = "fixture-choice",
            AdventureId = "skeleton"
        }, allowlist);

        var result = kernel.Observe(started.State, observation, allowlist);

        Assert.True(result.IsAborted);
        Assert.Equal(AdventureAbortReason.ChoiceOptionNotAvailable, result.AbortReason);
        Assert.Empty(result.Actions);
    }

    [Fact]
    public async Task UnknownFixtureAbortsAndCheckpointCanResumeForRefresh()
    {
        var parser = new KoLAdventureEncounterParser();
        var encounter = parser.Parse(
            "skeleton",
            new Uri("https://example.invalid/relay.php"),
            File.ReadAllText(Path.Combine(FixtureRoot, "unknown.html")));
        var allowlist = new AdventureActionAllowlist().AddAdventure("skeleton");
        var kernel = new AdventureRunKernel();
        var started = kernel.Begin(new AdventureRunRequest
        {
            RunId = "fixture-unknown",
            AdventureId = "skeleton"
        }, allowlist);

        var result = kernel.Observe(started.State, encounter, allowlist);
        Assert.True(result.IsAborted);
        Assert.Equal(AdventureAbortReason.UnknownEncounter, result.AbortReason);
        Assert.Equal(AdventureRunPhase.Aborted, result.Checkpoint.Phase);

        var store = new InMemoryAdventureRunCheckpointStore();
        await store.SaveAsync(result.Checkpoint);
        var loaded = await store.LoadAsync("fixture-unknown");
        Assert.NotNull(loaded);
        var resumed = kernel.Resume(loaded!, allowlist);
        Assert.True(resumed.WasResumed);
        Assert.Empty(resumed.Actions);
        Assert.True(resumed.IsAborted);
    }

    [Fact]
    public void DailyLimitStopsStartBeforeActionIsEmitted()
    {
        var allowlist = new AdventureActionAllowlist().AddAdventure("skeleton");
        var result = new AdventureRunKernel().Begin(new AdventureRunRequest
        {
            RunId = "limit",
            AdventureId = "skeleton",
            DailyLimit = new AdventureDailyLimitContract
            {
                MaxTurns = 1,
                TurnsUsed = 1,
                MaxRuns = 1,
                RunsUsed = 1
            }
        }, allowlist);

        Assert.True(result.IsAborted);
        Assert.Equal(AdventureAbortReason.DailyLimitReached, result.AbortReason);
        Assert.Empty(result.Actions);
    }
}
