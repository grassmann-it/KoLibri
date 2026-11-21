using KoLibri.Core.Basis;

namespace KoLibri.Core.Spiel;

/// <summary>What one grind step did.</summary>
public sealed record GrindStep
{
    public required int Snarfblat { get; init; }
    public required string LocationName { get; init; }
    public required string Outcome { get; init; }
    public required int Rounds { get; init; }
    public IReadOnlyList<QuestDrop> Drops { get; init; } = Array.Empty<QuestDrop>();
    public string? MonsterName { get; init; }
    public string? Detail { get; init; }
}

/// <summary>What a whole grind run did.</summary>
public sealed record GrindRunResult
{
    public required bool Completed { get; init; }
    public required int Fights { get; init; }
    public required int Steps { get; init; }
    public required string Outcome { get; init; }
    public required string Rationale { get; init; }
    public IReadOnlyList<GrindStep> StepLog { get; init; } = Array.Empty<GrindStep>();
    /// <summary>How many of the target item the run collected.</summary>
    public int Collected { get; init; }
    /// <summary>How many the account holds in total.</summary>
    public int TotalHeld { get; init; }
    public string? AuditPath { get; init; }
}

/// <summary>
/// Grinds an adventure until the goal is met.
///
/// The loop is the one KoLmafia leaves to the player: start the adventure,
/// pick an encounter, fight it, count the loot, repeat. It stops when the
/// goal is reached, when the account runs out of adventures, when the
/// location has nothing left, or when the fight budget runs out.
///
/// The runner owns no policy — it always attacks, which is the right
/// default for a grind. A fight that cannot be won by attacking stops the
/// run rather than burning adventures.
/// </summary>
public sealed class QuestRunner
{
    private readonly LiveManualCombatTransport _transport;
    private readonly ICombatObservationParser _parser;
    private readonly Func<CancellationToken, Task<int>>? _countHeld;

    public QuestRunner(
        LiveManualCombatTransport transport,
        ICombatObservationParser? parser = null,
        Func<CancellationToken, Task<int>>? countHeld = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _parser = parser ?? new KoLCombatObservationParser();
        _countHeld = countHeld;
    }

    /// <summary>
    /// Grinds one location until the target amount of the wanted item is
    /// collected.
    /// </summary>
    public async Task<GrindRunResult> GrindAsync(
        int snarfblat,
        string locationName,
        string wantedItem,
        int targetAmount,
        int alreadyHeld,
        int maxFights = 50,
        int maxRoundsPerFight = 30,
        BridgeAuditLog? audit = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wantedItem);
        var steps = new List<GrindStep>();
        var collected = 0;
        var fights = 0;
        var operationId = Guid.NewGuid().ToString("N");

        while (fights < maxFights && collected + alreadyHeld < targetAmount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fights++;

            // The inventory is the ground truth for how much the account
            // holds. The loot in the narration is a bonus on top of it.
            if (_countHeld is not null)
            {
                var held = await _countHeld(cancellationToken).ConfigureAwait(false);
                if (held > alreadyHeld + collected)
                {
                    collected = held - alreadyHeld;
                }
                if (collected + alreadyHeld >= targetAmount) break;
            }

            // 1. Start the adventure. KoL redirects to the encounter page,
            //    or straight to the fight when one is already running.
            var page = await _transport.StartAdventureAsync(snarfblat, cancellationToken)
                .ConfigureAwait(false);
            if (page.IsChoice)
            {
                // 2. Pick an encounter. The first option is often a
                //    non-combat ("check out the cash register"), so the
                //    runner walks the options until one leads to a fight.
                var choices = EncounterChoiceReader.ReadChoices(page.Html);
                if (choices.Count == 0)
                {
                    steps.Add(new GrindStep
                    {
                        Snarfblat = snarfblat,
                        LocationName = locationName,
                        Outcome = "no-encounter",
                        Rounds = 0,
                        Detail = "The location offered no encounter."
                    });
                    break;
                }
                var picked = false;
                foreach (var choice in choices)
                {
                    var candidate = await _transport
                        .ChooseEncounterAsync(choice.WhichChoice, choice.Option, choice.Password, cancellationToken)
                        .ConfigureAwait(false);
                    if (candidate.IsFight)
                    {
                        page = candidate;
                        picked = true;
                        break;
                    }
                    // A non-combat brings the encounter page back; the
                    // next option is tried.
                    if (candidate.IsChoice) page = candidate;
                }
                if (!picked)
                {
                    steps.Add(new GrindStep
                    {
                        Snarfblat = snarfblat,
                        LocationName = locationName,
                        Outcome = "no-fight",
                        Rounds = 0,
                        Detail = "No option of the encounter led to a fight."
                    });
                    break;
                }
            }
            else if (!page.IsFight)
            {
                steps.Add(new GrindStep
                {
                    Snarfblat = snarfblat,
                    LocationName = locationName,
                    Outcome = "no-fight",
                    Rounds = 0,
                    Detail = "The adventure did not lead to a fight."
                });
                break;
            }

            // 3. Fight it round by round. The encounter request answers
            //    with the fight page — it already carries the action form
            //    and the confirmation token in its URL.
            var rounds = 0;
            var monster = string.Empty;
            var fightOver = false;
            var fightHtml = page.Html;
            var lootHtml = page.Html;
            while (rounds < maxRoundsPerFight)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var observation = _parser.Parse(fightHtml, page.Url);
                if (observation.Round > 0) monster = observation.MonsterName ?? monster;
                // The action form decides whether the fight is still
                // running: KoL's fight page always carries it, and the
                // result page never does. The narration alone is not a
                // reliable sign — the enemy's description can contain the
                // words a victory check looks for.
                var action = observation.AvailableActions
                    .FirstOrDefault(a => a.Kind == CombatActionKind.Attack);
                if (action is null)
                {
                    fightOver = true;
                    lootHtml = fightHtml;
                    break;
                }
                var next = await _transport
                    .SubmitFightActionAsync("attack", null, 1,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                fightHtml = next.Html;
                page = next;
                // KoL announces the loot on the page the fight ends on. The
                // fight can also end inside the adventure request, so every
                // page that carries loot is kept.
                if (DropTracker.ReadDrops(next.Html).Count > 0) lootHtml = next.Html;
                rounds++;
            }

            // 4. Count the loot. KoL announces the drops on the page the
            //    fight ended on.
            var drops = DropTracker.ReadDrops(lootHtml);
            var gained = DropTracker.CountOf(drops, wantedItem);
            collected += gained;
            steps.Add(new GrindStep
            {
                Snarfblat = snarfblat,
                LocationName = locationName,
                Outcome = fightOver ? "fought" : "round-limit",
                Rounds = rounds,
                MonsterName = monster,
                Drops = drops,
                Detail = gained > 0
                    ? $"+{gained} {wantedItem}"
                    : "no drop"
            });

            if (audit is not null)
            {
                await audit.AppendAsync(new AuditEvent(
                    AuditEventPhase.Commit, operationId, "QuestRunner",
                    requestId: $"grind-{snarfblat}-{fights}",
                    action: $"quest.grind.{snarfblat}",
                    status: fightOver ? "fought" : "round-limit",
                    message: $"{monster} · {rounds} rounds · +{gained} {wantedItem}",
                    accountId: null), cancellationToken).ConfigureAwait(false);
            }
        }

        var total = alreadyHeld + collected;
        var completed = total >= targetAmount;
        return new GrindRunResult
        {
            Completed = completed,
            Fights = fights,
            Steps = steps.Count,
            Outcome = completed ? "target-reached"
                : fights >= maxFights ? "fight-budget-reached"
                : steps.Count > 0 && steps[^1].Outcome != "fought" ? steps[^1].Outcome
                : "stopped",
            Rationale = completed
                ? $"Collected {total} {wantedItem} (target {targetAmount})."
                : $"Collected {collected} more {wantedItem}; the account holds {total}.",
            StepLog = steps,
            Collected = collected,
            TotalHeld = total,
            AuditPath = audit?.FilePath
        };
    }
}
