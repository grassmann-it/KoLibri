using Xunit;
using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Tests;

/// <summary>
/// Snapshot tests for the Skeleton of Crimbos Past fixture set. The HTML
/// fixtures under <c>Fixtures/Html/SkeletonOfCrimbosPast/</c> are the
/// baseline the bounded runner and the daily routine use when exercising
/// the adventure path end-to-end without a live KoL server.
/// </summary>
public sealed class SkeletonOfCrimbosPastFixtureTests
{
    [Fact]
    public void Fight_round_1_fixture_parses_with_round_and_unknown_monster()
    {
        var parser = new KoLAdventureEncounterParser();
        var html = ReadFixture("fight-round1.html");
        var encounter = parser.Parse("skeleton-of-crimbos-past", new Uri("https://www.kingdomofloathing.com/fight.php"), html);

        Assert.Equal(AdventureEncounterKind.Fight, encounter.Kind);
        Assert.True(encounter.CanContinue);
        Assert.Equal(1, encounter.FightRound);
        Assert.False(string.IsNullOrWhiteSpace(encounter.ResponseHash));
    }

    [Fact]
    public void Fight_round_2_fixture_advances_round()
    {
        var parser = new KoLAdventureEncounterParser();
        var html = ReadFixture("fight-round2.html");
        var encounter = parser.Parse("skeleton-of-crimbos-past", new Uri("https://www.kingdomofloathing.com/fight.php"), html);
        Assert.Equal(2, encounter.FightRound);
        Assert.Equal(AdventureEncounterKind.Fight, encounter.Kind);
    }

    [Fact]
    public void Reward_fixture_parses_as_reward()
    {
        var parser = new KoLAdventureEncounterParser();
        var html = ReadFixture("reward.html");
        var encounter = parser.Parse("skeleton-of-crimbos-past", new Uri("https://www.kingdomofloathing.com/adventure.php"), html);
        Assert.Equal(AdventureEncounterKind.Reward, encounter.Kind);
        Assert.False(encounter.CanContinue);
    }

    [Fact]
    public void Choice_fixture_parses_with_options()
    {
        var parser = new KoLAdventureEncounterParser();
        var html = ReadFixture("choice-knucklebones.html");
        var encounter = parser.Parse("skeleton-of-crimbos-past", new Uri("https://www.kingdomofloathing.com/choice.php"), html);
        Assert.Equal(AdventureEncounterKind.Choice, encounter.Kind);
        Assert.Equal("1003", encounter.ChoiceId);
        Assert.Contains("1", encounter.AvailableChoices);
        Assert.Contains("2", encounter.AvailableChoices);
        Assert.True(encounter.CanContinue);
    }

    [Fact]
    public async Task Bounded_runner_walks_full_skeleton_chain_to_reward()
    {
        // The fixture transport queues typed observations matching the
        // Skeleton of Crimbos Past allowlist. We deliberately use the
        // observation shortcut (instead of HTML) so the chain exercises
        // the bounded runner end-to-end without relying on the parser's
        // encounter-id extraction for this minimal fixture.
        var transport = new AdventureExecutionFixtureTransport();
        var store = new InMemoryAdventureRunCheckpointStore();
        transport.EnqueueObservation(AdventureTransportOperation.Adventure,
            new AdventureKernelObservation
            {
                AdventureId = "skeleton-of-crimbos-past",
                Kind = AdventureEncounterKind.Fight,
                EncounterId = "skeleton",
                MonsterId = "skeleton",
                FightRound = 1,
                CanContinue = true,
                ResponseHash = "round-1"
            });
        transport.EnqueueObservation(AdventureTransportOperation.Fight,
            new AdventureKernelObservation
            {
                AdventureId = "skeleton-of-crimbos-past",
                Kind = AdventureEncounterKind.Fight,
                EncounterId = "skeleton",
                MonsterId = "skeleton",
                FightRound = 2,
                CanContinue = true,
                ResponseHash = "round-2"
            });
        transport.EnqueueObservation(AdventureTransportOperation.Fight,
            new AdventureKernelObservation
            {
                AdventureId = "skeleton-of-crimbos-past",
                Kind = AdventureEncounterKind.Reward,
                CanContinue = false,
                ResponseHash = "reward"
            });

        var allowlist = new AdventureActionAllowlist()
            .AddAdventure("skeleton-of-crimbos-past")
            .AddEncounter("skeleton-of-crimbos-past", "skeleton")
            .AddFightAction("skeleton-of-crimbos-past", "attack", "skeleton");
        await using var gateway = new AdventureExecutionGateway(transport, store);

        var request = new AdventureRunRequest
        {
            RunId = "skeleton-fixture",
            AdventureId = "skeleton-of-crimbos-past",
            Role = AccountRole.Main,
            DailyLimit = new AdventureDailyLimitContract { MaxTurns = 1, MaxRuns = 1 }
        };

        var runner = new BoundedAdventureRunner(gateway);
        var result = await runner.RunAsync(
            request,
            allowlist,
            stopWhenSatisfied: Array.Empty<AdventureCondition>(),
            snapshotProvider: new StaticConditionSnapshotProvider(new AdventureConditionSnapshot { IsStale = false, TurnsRemaining = 1 }),
            options: new BoundedAdventureRunnerOptions { MaxGatewaySteps = 5, MaxDuration = TimeSpan.FromSeconds(30) });

        if (result.StopReason != BoundedAdventureRunStopReason.Complete)
        {
            var details = string.Join(" | ", result.Steps.Select(s =>
                $"{s.Status}/{(s.AbortReason?.ToString() ?? "-")}/req={s.RequestId}"));
            Assert.Fail($"Expected Complete but got {result.StopReason}. Steps: {details}");
        }
        Assert.Equal(BoundedAdventureRunStopReason.Complete, result.StopReason);
        Assert.Equal(3, result.Steps.Count);
        Assert.Equal(2, transport.Count(AdventureTransportOperation.Fight));
    }

    [Fact]
    public void Choice_fixture_round_trip_through_parser_uses_allowlisted_option()
    {
        // Round-trip test: parser reads the choice-knucklebones fixture,
        // we feed the resulting options through the allowlist, and verify
        // that an allowlisted option survives the chain.
        var parser = new KoLAdventureEncounterParser();
        var html = ReadFixture("choice-knucklebones.html");
        var encounter = parser.Parse("skeleton-of-crimbos-past", new Uri("https://www.kingdomofloathing.com/choice.php"), html);
        Assert.Equal(AdventureEncounterKind.Choice, encounter.Kind);
        Assert.Equal("1003", encounter.ChoiceId);

        var allowlist = new AdventureActionAllowlist()
            .AddAdventure("skeleton-of-crimbos-past")
            .AddEncounter("skeleton-of-crimbos-past", "/choice.php")
            .AddChoiceOption("skeleton-of-crimbos-past", "1003", "2");
        var allowed = allowlist.TryGetChoiceAction(
            "skeleton-of-crimbos-past",
            encounter.ChoiceId!,
            encounter.AvailableChoices,
            encounter.Location,
            out var action);
        Assert.True(allowed);
        Assert.Equal("2", action.OptionId);
        Assert.Equal("1003", action.ChoiceId);
    }

    private static string ReadFixture(string name)
    {
        var path = Path.Combine("Fixtures", "Html", "SkeletonOfCrimbosPast", name);
        Assert.True(File.Exists(path), $"Fixture file '{path}' is missing. Content is set to CopyToOutputDirectory.");
        return File.ReadAllText(path);
    }

    private sealed class StaticConditionSnapshotProvider : IAdventureConditionSnapshotProvider
    {
        private readonly AdventureConditionSnapshot _snapshot;
        public StaticConditionSnapshotProvider(AdventureConditionSnapshot snapshot)
            => _snapshot = snapshot;
        public ValueTask<AdventureConditionSnapshot> ReadAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_snapshot);
    }
}
