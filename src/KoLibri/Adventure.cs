using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using KoLibri.Internal;
using CoreSpiel = KoLibri.Core.Spiel;

namespace KoLibri
{
    public sealed class Adventurer
    {
        private readonly KolibriSession _session;
        private FightEncounter? _fight;
        private CoreSpiel.CombatObservation? _lastObservation;

        internal Adventurer(KolibriSession session) => _session = session;

        /// <summary>Ein Zug in <paramref name="location"/>. Liefert die Begegnung, ohne sie aufzulösen.</summary>
        public Task<Encounter> AdventureAsync(Location location, CancellationToken cancellationToken = default)
        {
            var action = new GuardedAction(_session.Account.Id, GuardedActionKind.Adventure, $"adventure at {location.Snarfblat}")
            {
                Quantity = 1,
                Path = "adventure.php"
            };
            return _session.GuardAsync(action, () => AdventureCoreAsync(location, cancellationToken), e => e is not BlockedEncounter, cancellationToken);
        }

        /// <summary>Offene Begegnung (Kampf oder Choice), sonst null. Für Wiederaufnahme nach Browser-Klicks.</summary>
        public async Task<Encounter?> GetOpenEncounterAsync(CancellationToken cancellationToken = default)
        {
            var detection = await _session.ActAsync(() => new CoreSpiel.OpenChoiceClient(_session.Http, _session.Client.Options.BaseUri)
                .DetectAsync(cancellationToken)).ConfigureAwait(false);
            switch (detection.Kind)
            {
                case CoreSpiel.OpenChoiceKind.Fight:
                {
                    var page = await Get("fight.php", cancellationToken).ConfigureAwait(false);
                    return page.Kind == PageKind.NotInFight ? null : Remember(ToEncounter(page, _fight?.Location));
                }
                case CoreSpiel.OpenChoiceKind.Choice:
                {
                    var page = await Get("choice.php", cancellationToken).ConfigureAwait(false);
                    var encounter = ToEncounter(page, null);
                    return encounter is ChoiceEncounter or FightEncounter ? Remember(encounter) : null;
                }
                case CoreSpiel.OpenChoiceKind.Unavailable:
                    if (detection.Reason?.Contains("expired", StringComparison.OrdinalIgnoreCase) == true)
                        throw new SessionExpiredException(_session.Account.Id);
                    throw new KolibriException($"Could not check for an open encounter: {detection.Reason}");
                default:
                    _fight = null;
                    return null;
            }
        }

        /// <summary>Eine Kampfrunde.</summary>
        public Task<FightRound> FightAsync(CombatAction action, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(action);
            var guarded = new GuardedAction(_session.Account.Id, GuardedActionKind.Fight, $"fight: {Describe(action)}")
            {
                Item = action.Kind == CombatActionKind.Item && action.Id is { } item ? new ItemRef(item) : null,
                Quantity = 1,
                Path = "fight.php"
            };
            ValidateAction(action);
            return _session.GuardAsync(guarded, () => FightCoreAsync(action, cancellationToken), r => r.Outcome != FightOutcome.Unknown, cancellationToken);
        }

        /// <summary>Kämpft mit der Strategie bis Sieg, Niederlage, Flucht oder CombatAction.Stop.</summary>
        public Task<FightResult> FightToEndAsync(ICombatStrategy strategy, CancellationToken cancellationToken = default)
            => FightToEndAsync(strategy, 30, cancellationToken);

        public Task<Encounter> ChooseAsync(int choiceId, int option, CancellationToken cancellationToken = default)
        {
            var action = new GuardedAction(_session.Account.Id, GuardedActionKind.Choice, $"choice {choiceId} option {option}")
            {
                Quantity = 1,
                Path = "choice.php"
            };
            return _session.GuardAsync(action, () => ChooseCoreAsync(choiceId, option, cancellationToken), _ => true, cancellationToken);
        }

        /// <summary>Der eine Adventure-Läufer: N Züge, Strategie, Resolver, Stopp-Regeln.</summary>
        public async Task<RunResult> RunAsync(RunRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentOutOfRangeException.ThrowIfLessThan(request.MaxTurns, 1);
            var encounters = new List<Encounter>();
            var items = new List<InventoryItem>();
            long meat = 0;
            Status? start = null;
            Status? last = null;
            var recoverySteps = new List<RecoveryStep>();
            var recoveryAdventures = 0;
            Inventory? baseline = null;

            RunResult Finish(RunStopReason reason, string? detail, Encounter? open = null)
            {
                // Costly rests spend adventures that are not turns of the run.
                var spent = start is not null && last is not null ? Math.Max(0, start.Adventures - last.Adventures - recoveryAdventures) : 0;
                return new RunResult(reason, spent, encounters.ToArray(), detail)
                {
                    Open = open,
                    RecoverySteps = recoverySteps.ToArray(),
                    Items = Merge(items),
                    MeatGained = meat
                };
            }

            try
            {
                start = last = await _session.StatusAsync(cancellationToken).ConfigureAwait(false);
                // One inventory read per run: the starting stock; loot of the run is added from the encounters.
                if (request.StopWhen.Any(c => c.Kind == StopConditionKind.ItemCountAtLeast))
                    baseline = await _session.Inventory.GetAsync(true, cancellationToken).ConfigureAwait(false);
                // An encounter left open (browser click, earlier stop) is finished before a new turn starts.
                var pending = await GetOpenEncounterAsync(cancellationToken).ConfigureAwait(false);
                var turnsStarted = 0;
                while (true)
                {
                    Encounter encounter;
                    var chainStart = encounters.Count;
                    if (pending is not null)
                    {
                        encounter = pending;
                        pending = null;
                    }
                    else
                    {
                        if (turnsStarted >= request.MaxTurns)
                            return Finish(RunStopReason.TurnsDone, null);
                        // Recovery and upkeep run between turns only, never in the middle of a fight/choice chain.
                        var recovered = await RecoverAsync(request.Recovery, request.MaxRecoveryStepsPerTurn, last, recoverySteps, cancellationToken).ConfigureAwait(false);
                        last = recovered.Status;
                        recoveryAdventures += recovered.AdventuresSpent;
                        if (recovered.StopDetail is { } recoveryStop)
                            return Finish(RunStopReason.Recovery, recoveryStop);
                        if (StopCondition(request.StopWhen, last, baseline, items) is { } why)
                            return Finish(RunStopReason.Condition, why);
                        if (last.Adventures <= 0)
                            return Finish(RunStopReason.Blocked, "No adventures left.");
                        var before = last;
                        encounter = await AdventureAsync(request.Location, cancellationToken).ConfigureAwait(false);
                        turnsStarted++;
                        if (encounter is BlockedEncounter { Reason: BlockReason.Other } empty
                            && empty.Title is InterstitialTitle or NothingOpenTitle)
                        {
                            // No encounter and none open (0.4 #73): the counter decides, a start is never repeated blindly.
                            last = await _session.StatusAsync(cancellationToken).ConfigureAwait(false);
                            if (last.Adventures < before.Adventures)
                            {
                                // The turn was spent: it counts as done.
                                encounters.Add(new NonCombatEncounter(empty.Page, request.Location, empty.Title, Array.Empty<InventoryItem>(), 0));
                                continue;
                            }
                            if (empty.Title == InterstitialTitle && turnsStarted == 1)
                            {
                                // KoL's one-time interstitial after a login: one retry, only on the first start and only
                                // when no turn was spent.
                                encounter = await AdventureAsync(request.Location, cancellationToken).ConfigureAwait(false);
                            }
                            else
                            {
                                encounters.Add(empty);
                                return Finish(RunStopReason.Error,
                                    "KoL answered adventure.php without an encounter, none is open and the adventure counter did not change. No second start was sent.");
                            }
                        }
                    }

                    // Resolve the encounter chain (fight → choice → …) of this turn.
                    while (true)
                    {
                        encounters.Add(encounter);
                        if (encounter is FightEncounter)
                        {
                            var fight = await FightToEndAsync(request.Combat, request.MaxRoundsPerFight, cancellationToken).ConfigureAwait(false);
                            items.AddRange(fight.Items);
                            meat += fight.Meat;
                            switch (fight.Outcome)
                            {
                                case FightOutcome.Lost:
                                    last = await _session.StatusAsync(cancellationToken).ConfigureAwait(false);
                                    return Finish(RunStopReason.Defeat, $"Lost the fight against {fight.MonsterName}.");
                                case FightOutcome.Stopped:
                                    last = await _session.StatusAsync(cancellationToken).ConfigureAwait(false);
                                    return Finish(RunStopReason.NeedsCombat,
                                        fight.Rounds.Count >= request.MaxRoundsPerFight
                                            ? $"The fight round limit ({request.MaxRoundsPerFight}) was reached; the fight is still open."
                                            : "The combat strategy stopped; the fight is still open.", _fight);
                                case FightOutcome.Unknown:
                                    if (fight.Rounds.Count > 0)
                                        encounters.Add(new BlockedEncounter(fight.Rounds[^1].Page, request.Location, "Unreadable fight page", BlockReason.UnknownPage));
                                    last = await _session.StatusAsync(cancellationToken).ConfigureAwait(false);
                                    return Finish(RunStopReason.Error, "The fight ended in a state KoLibri could not read; check the account.");
                            }
                            var next = fight.Rounds.Count > 0 ? fight.Rounds[^1].Page : null;
                            var follow = next is null ? null : ToEncounter(next, request.Location);
                            if (follow is ChoiceEncounter or FightEncounter) { encounter = Remember(follow); continue; }
                            break;
                        }
                        if (encounter is ChoiceEncounter choice)
                        {
                            var decision = request.Choices.Resolve(new ChoiceContext(choice.Location, choice.ChoiceId, choice.Title, choice.Options));
                            if (decision.Option is not { } option)
                            {
                                last = await _session.StatusAsync(cancellationToken).ConfigureAwait(false);
                                return Finish(RunStopReason.NeedsChoice, decision.Reason, choice);
                            }
                            encounter = await ChooseAsync(choice.ChoiceId, option, cancellationToken).ConfigureAwait(false);
                            if (encounter is NonCombatEncounter reward)
                            {
                                items.AddRange(reward.Items);
                                meat += reward.Meat;
                                encounters.Add(reward);
                                break;
                            }
                            continue;
                        }
                        if (encounter is NonCombatEncounter nonCombat)
                        {
                            items.AddRange(nonCombat.Items);
                            meat += nonCombat.Meat;
                            break;
                        }
                        if (encounter is BlockedEncounter blocked)
                        {
                            last = await _session.StatusAsync(cancellationToken).ConfigureAwait(false);
                            return Finish(RunStopReason.Blocked, $"{blocked.Reason}: {blocked.Title}");
                        }
                        break;
                    }

                    var previous = last;
                    last = await _session.StatusAsync(cancellationToken).ConfigureAwait(false);
                    if (TurnStop(request.StopWhen, encounters, chainStart) is { } seen)
                        return Finish(RunStopReason.Condition, seen);
                    if (turnsStarted > 0 && last.Adventures == previous.Adventures && encounters.Count > 0
                        && encounters[^1] is not ChoiceEncounter && !IsFreeEncounter(encounters[^1]))
                    {
                        // Same rule as the 0.4 CLI loop: an adventure that did not move the counter is not guessed at.
                        if (turnsStarted < request.MaxTurns)
                            return Finish(RunStopReason.Error, "The adventure counter did not change after a completed adventure; stopping instead of guessing.");
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Finish(RunStopReason.Cancelled, "Cancelled.");
            }
            catch (KolibriException exception)
            {
                return Finish(RunStopReason.Error, exception.Message);
            }
        }

        public async Task<LocationAccess> GetLocationAccessAsync(Location location, CancellationToken cancellationToken = default)
        {
            int? snarfblat = location.Snarfblat;
            if (!CoreSpiel.KoLQuestAccess.HasRule(snarfblat))
                return new LocationAccess(location, true, "No access rule is known for this location; KoL decides when you adventure.");
            var status = await _session.ReadAsync(r => r.FetchStatusAsync(true, cancellationToken), cancellationToken).ConfigureAwait(false);
            CoreSpiel.KoLLocationAccess access;
            if (CoreSpiel.KoLQuestAccess.IsBatHole(snarfblat))
            {
                var html = await _session.ReadAsync(r => r.FetchBatHoleMapAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
                access = CoreSpiel.KoLQuestAccess.EvaluateBatHole(snarfblat, status, html);
            }
            else
            {
                var quests = CoreSpiel.KoLQuestAccess.RequiresQuestLog(snarfblat)
                    ? await _session.ReadAsync(r => r.FetchQuestLogAsync(cancellationToken), cancellationToken).ConfigureAwait(false)
                    : null;
                var inventory = CoreSpiel.KoLQuestAccess.RequiresInventory(snarfblat)
                    ? await _session.ReadAsync(r => r.FetchInventoryAsync(true, cancellationToken), cancellationToken).ConfigureAwait(false)
                    : null;
                access = CoreSpiel.KoLQuestAccess.Evaluate(snarfblat, status, quests, inventory);
            }
            return new LocationAccess(location, access.Allowed != false, access.Reason);
        }

        // ------------------------------------------------------------------ core steps

        internal const string InterstitialTitle = "KoL interstitial (Daily Reminders / New Events)";
        internal const string NothingOpenTitle = "KoL answered without an encounter and none is open.";

        private async Task<Encounter> AdventureCoreAsync(Location location, CancellationToken cancellationToken)
        {
            var form = new Dictionary<string, string> { ["snarfblat"] = location.Snarfblat.ToString(CultureInfo.InvariantCulture) };
            var page = await _session.SendAsync(HttpMethod.Post, _session.BuildUri("adventure.php", null), form, RequestLane.Action, cancellationToken)
                .ConfigureAwait(false);
            Encounter encounter;
            if (IsMainPage(page))
            {
                // adventure.php → main.php: KoL's interstitial, or an encounter is open elsewhere.
                var open = await GetOpenEncounterAsync(cancellationToken).ConfigureAwait(false);
                encounter = open ?? new BlockedEncounter(page, location,
                    IsInterstitial(page.Html) ? InterstitialTitle : NothingOpenTitle,
                    BlockReason.Other);
            }
            else if (page.Kind == PageKind.NotInFight)
            {
                // adventure.php → fight.php "Not in a Fight" (0.4 #73): read choice.php once (GET, no second start).
                var choice = await Get("choice.php", cancellationToken).ConfigureAwait(false);
                encounter = ToEncounter(choice, location) is ChoiceEncounter open
                    ? open
                    : new BlockedEncounter(page, location, NothingOpenTitle, BlockReason.Other);
            }
            else
            {
                encounter = ToEncounter(page, location);
            }
            return Remember(encounter);
        }

        private async Task<FightRound> FightCoreAsync(CombatAction action, CancellationToken cancellationToken)
        {
            var form = new Dictionary<string, string>(StringComparer.Ordinal);
            switch (action.Kind)
            {
                case CombatActionKind.Attack: form["action"] = "attack"; break;
                case CombatActionKind.Skill: form["action"] = "skill"; form["whichskill"] = Id(action.Id); break;
                case CombatActionKind.Item:
                    form["action"] = "useitem";
                    form["whichitem"] = Id(action.Id);
                    if (action.SecondId is { } second) form["whichitem2"] = second.ToString(CultureInfo.InvariantCulture);
                    break;
                case CombatActionKind.RunAway: form["action"] = "runaway"; break;
                default: throw new NotSupportedException($"Combat action {action.Kind} cannot be sent.");
            }
            var previous = _lastObservation;
            var page = await _session.SendAsync(HttpMethod.Post, _session.BuildUri("fight.php", null), form, RequestLane.Action, cancellationToken)
                .ConfigureAwait(false);
            if (page.Kind == PageKind.NotInFight)
            {
                _fight = null;
                return new FightRound(previous?.Round + 1 ?? 1, action, FightOutcome.Unknown, previous?.PlayerHp ?? 0, previous?.PlayerMp ?? 0, page);
            }
            var observation = new CoreSpiel.KoLCombatObservationParser().Parse(page.Html, page.Url);
            _lastObservation = observation;
            var outcome = observation.Outcome switch
            {
                CoreSpiel.CombatOutcome.Ongoing => FightOutcome.Ongoing,
                CoreSpiel.CombatOutcome.Victory => FightOutcome.Won,
                CoreSpiel.CombatOutcome.Defeat => FightOutcome.Lost,
                CoreSpiel.CombatOutcome.Fled => FightOutcome.RanAway,
                _ when page.Kind == PageKind.Fight && OpenFightForm(page.Html) => FightOutcome.Ongoing,
                _ => FightOutcome.Unknown
            };
            var (gained, meat) = outcome == FightOutcome.Ongoing ? (Array.Empty<InventoryItem>(), 0L) : Loot(page.Html);
            var round = new FightRound(observation.Round > 0 ? observation.Round : (previous?.Round ?? 0) + 1, action, outcome,
                observation.PlayerHp ?? previous?.PlayerHp ?? 0, observation.PlayerMp ?? previous?.PlayerMp ?? 0, page)
            {
                DamageDealt = Delta(previous?.EnemyHp, observation.EnemyHp),
                DamageTaken = Delta(previous?.PlayerHp, observation.PlayerHp),
                Items = gained,
                Meat = meat
            };
            if (outcome == FightOutcome.Ongoing && _fight is { } open)
                _fight = open with { Page = page, Round = round.Round };
            else if (outcome != FightOutcome.Ongoing)
                _fight = null;
            if (gained.Length > 0) _session.RaiseItems(gained, ItemSource.Fight);
            return round;
        }

        internal async Task<FightResult> FightToEndAsync(ICombatStrategy strategy, int maxRounds, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(strategy);
            var fight = _fight ?? await GetOpenEncounterAsync(cancellationToken).ConfigureAwait(false) as FightEncounter
                ?? throw new KolibriException("No fight is open.");
            // KoL's fight.php usually shows no player HP: then the HP come from a fresh status (0.4 #74: HitPointsProvider).
            var status = await _session.StatusAsync(cancellationToken).ConfigureAwait(false);
            var rounds = new List<FightRound>();
            var pageHp = HasHp(_lastObservation);
            var hp = pageHp ? _lastObservation!.PlayerHp!.Value : status.Hp;
            var mp = pageHp && _lastObservation!.PlayerMp is { } pageMp ? pageMp : status.Mp;
            var outcome = FightOutcome.Ongoing;
            while (outcome == FightOutcome.Ongoing)
            {
                if (rounds.Count >= maxRounds) { outcome = FightOutcome.Stopped; break; }
                var context = new CombatContext(fight, fight.Round + rounds.Count, hp, status.MaxHp, mp, status.MaxMp, rounds.ToArray())
                {
                    Effects = status.Effects
                };
                var action = strategy.Next(context);
                if (action.Kind == CombatActionKind.Stop) { outcome = FightOutcome.Stopped; break; }
                var round = await FightAsync(action, cancellationToken).ConfigureAwait(false);
                if (round.Outcome == FightOutcome.Ongoing && !HasHp(_lastObservation))
                {
                    // Only while the fight goes on, and only when the page has no HP: one status read, never a guess.
                    status = await _session.StatusAsync(cancellationToken).ConfigureAwait(false);
                    round = round with { Hp = status.Hp, Mp = status.Mp };
                }
                rounds.Add(round);
                hp = round.Hp;
                mp = round.Mp;
                outcome = round.Outcome;
            }
            var result = new FightResult(outcome, fight.MonsterName, rounds.ToArray())
            {
                Items = Merge(rounds.SelectMany(r => r.Items)),
                Meat = rounds.Sum(r => r.Meat)
            };
            if (outcome is FightOutcome.Won or FightOutcome.Lost or FightOutcome.RanAway)
                _session.RaiseFightEnded(result);
            return result;
        }

        private async Task<Encounter> ChooseCoreAsync(int choiceId, int option, CancellationToken cancellationToken)
        {
            var current = await Get("choice.php", cancellationToken).ConfigureAwait(false);
            var parsed = CoreSpiel.OpenChoiceClient.Parse(current.Html);
            if (parsed.Choice is not { } state)
                throw new KolibriException($"No choice is open. Nothing was sent.");
            if (state.ChoiceId != choiceId.ToString(CultureInfo.InvariantCulture))
                throw new KolibriException($"The open choice is {state.ChoiceId}, not {choiceId}. Nothing was sent.");
            if (state.Options.All(o => o.Number != option))
                throw new KolibriException($"Option {option} is not visible on the open choice. Nothing was sent.");
            var form = new Dictionary<string, string>(state.Fields, StringComparer.Ordinal)
            {
                ["whichchoice"] = state.ChoiceId,
                ["option"] = option.ToString(CultureInfo.InvariantCulture)
            };
            var page = await _session.SendAsync(HttpMethod.Post, _session.BuildUri("choice.php", null), form, RequestLane.Action, cancellationToken)
                .ConfigureAwait(false);
            Encounter next = IsMainPage(page)
                ? await GetOpenEncounterAsync(cancellationToken).ConfigureAwait(false) ?? new NonCombatEncounter(page, null, state.Title, Array.Empty<InventoryItem>(), 0)
                : ToEncounter(page, null);
            if (next is ChoiceEncounter again && again.ChoiceId == choiceId
                && again.Options.Select(o => o.Option).SequenceEqual(state.Options.Select(o => o.Number)))
                throw new KolibriException("KoL did not accept the option: the same choice is still open.");
            if (next is NonCombatEncounter reward && reward.Items.Count > 0) _session.RaiseItems(reward.Items, ItemSource.Choice);
            return Remember(next);
        }

        // ------------------------------------------------------------------ parsing

        private static readonly Regex AcquireItem = new(
            @"You acquire (?:an item|(?<n>[\d,]+) items?)?:?\s*(?:<[^>]+>\s*)*<b>(?<name>[^<]+)</b>(?:\s*\((?<count>[\d,]+)\))?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex GainMeat = new(@"You gain (?<n>[\d,]+) Meat", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Title = new(@"<b>(?<t>[^<]{2,120})</b>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private (InventoryItem[] Items, long Meat) Loot(string html)
        {
            var items = new List<InventoryItem>();
            foreach (Match match in AcquireItem.Matches(html))
            {
                var name = WebUtility.HtmlDecode(match.Groups["name"].Value).Trim();
                var count = ParseInt(match.Groups["count"].Value) ?? ParseInt(match.Groups["n"].Value) ?? 1;
                var id = _session.Client.Data.FindItem(name)?.Item ?? new ItemRef(0);
                items.Add(new InventoryItem(id, name, count));
            }
            long meat = GainMeat.Matches(html).Sum(m => (long)(ParseInt(m.Groups["n"].Value) ?? 0));
            return (items.ToArray(), meat);
        }

        internal Encounter ToEncounter(Page page, Location? location)
        {
            if (page.Kind == PageKind.Fight || page.Url.AbsolutePath.EndsWith("fight.php", StringComparison.OrdinalIgnoreCase) && OpenFightForm(page.Html))
            {
                var observation = new CoreSpiel.KoLCombatObservationParser().Parse(page.Html, page.Url);
                if (observation.Outcome is CoreSpiel.CombatOutcome.Ongoing || OpenFightForm(page.Html))
                {
                    _lastObservation = observation;
                    var name = observation.MonsterName ?? observation.Monster ?? "unknown monster";
                    return new FightEncounter(page, location, name, name,
                        int.TryParse(observation.MonsterId, NumberStyles.None, CultureInfo.InvariantCulture, out var mid) ? mid : null,
                        Math.Max(1, observation.Round));
                }
            }
            if (page.Kind == PageKind.Choice)
            {
                var parsed = CoreSpiel.OpenChoiceClient.Parse(page.Html);
                if (parsed.Choice is { } state && int.TryParse(state.ChoiceId, NumberStyles.None, CultureInfo.InvariantCulture, out var cid))
                    return new ChoiceEncounter(page, location, state.Title, cid,
                        state.Options.Select(o => new ChoiceOption(o.Number, o.Text)).ToArray());
            }
            var text = Plain(page.Html);
            if (Blocked(text) is { } reason)
                return new BlockedEncounter(page, location, FirstTitle(page.Html) ?? reason.ToString(), reason);
            var (items, meat) = Loot(page.Html);
            if (page.Kind == PageKind.NotInFight || (items.Length == 0 && meat == 0 && LooksUnknown(page)))
                return new BlockedEncounter(page, location, FirstTitle(page.Html), BlockReason.UnknownPage);
            return new NonCombatEncounter(page, location, FirstTitle(page.Html), items, meat);
        }

        private static BlockReason? Blocked(string text)
        {
            if (Regex.IsMatch(text, @"(?i)you('re| are) (all )?out of adventures|you don't have any adventures|you have no adventures"))
                return BlockReason.NoAdventures;
            if (Regex.IsMatch(text, @"(?i)too beaten up|you're way too beaten|you are too beaten"))
                return BlockReason.Beaten;
            if (Regex.IsMatch(text, @"(?i)you can't get (to|there)|you shouldn't be here|that isn't a place you can|this area is closed|not yet unlocked|you can't adventure there"))
                return BlockReason.LocationLocked;
            if (Regex.IsMatch(text, @"(?i)you are too drunk to"))
                return BlockReason.Other;
            return null;
        }

        private static bool LooksUnknown(Page page)
            => !page.Url.AbsolutePath.EndsWith("adventure.php", StringComparison.OrdinalIgnoreCase)
               && !page.Url.AbsolutePath.EndsWith("choice.php", StringComparison.OrdinalIgnoreCase)
               && !page.Url.AbsolutePath.EndsWith("fight.php", StringComparison.OrdinalIgnoreCase);

        /// <summary>The fight page itself shows the player's HP (current and maximum).</summary>
        private static bool HasHp(CoreSpiel.CombatObservation? observation)
            => observation is { PlayerHp: not null, PlayerMaxHp: > 0 };

        private static bool OpenFightForm(string html)
            => html.Contains("name=attack", StringComparison.OrdinalIgnoreCase)
               || html.Contains("value=\"attack\"", StringComparison.OrdinalIgnoreCase)
               || html.Contains("name=\"action\" value=\"attack\"", StringComparison.OrdinalIgnoreCase)
               || html.Contains("id=\"tack\"", StringComparison.OrdinalIgnoreCase);

        private static bool IsMainPage(Page page) => page.Url.AbsolutePath.EndsWith("main.php", StringComparison.OrdinalIgnoreCase);

        private static bool IsInterstitial(string html)
            => html.Contains("Daily Reminders", StringComparison.OrdinalIgnoreCase) || html.Contains("New Events", StringComparison.OrdinalIgnoreCase);

        private static bool IsFreeEncounter(Encounter encounter) => encounter is BlockedEncounter;

        private static string? FirstTitle(string html)
        {
            var match = Title.Match(html);
            return match.Success ? WebUtility.HtmlDecode(match.Groups["t"].Value).Trim() : null;
        }

        private static string Plain(string html)
            => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")), @"\s+", " ");

        private async Task<Page> Get(string path, CancellationToken cancellationToken)
            => await _session.SendAsync(HttpMethod.Get, _session.BuildUri(path, null), null, RequestLane.Auto, cancellationToken).ConfigureAwait(false);

        private Encounter Remember(Encounter encounter)
        {
            _fight = encounter as FightEncounter;
            _session.RaiseEncounter(encounter);
            return encounter;
        }

        private const int BeatenUpEffectId = 7;

        private readonly record struct RecoveryOutcome(Status Status, int AdventuresSpent, string? StopDetail);

        /// <summary>
        /// Asks the policy for steps before a new turn, runs each through <see cref="CharacterActions"/>, re-reads the status
        /// and repeats until the policy says None or the limit is reached. A step without progress ends the run: no guessing, no endless loop.
        /// </summary>
        private async Task<RecoveryOutcome> RecoverAsync(IRecoveryPolicy policy, int limit, Status status, List<RecoveryStep> log, CancellationToken cancellationToken)
        {
            if (ReferenceEquals(policy, RecoveryPolicies.None) || limit <= 0) return new(status, 0, null);
            if (status.Effects.Any(e => e.Id == BeatenUpEffectId || e.Name.Equals("Beaten Up", StringComparison.OrdinalIgnoreCase)))
                return new(status, 0, "You are Beaten Up; KoLibri does not guess a way to heal that. Heal by hand and start again.");
            var done = new List<RecoveryStep>();
            Inventory? inventory = null;
            var spent = 0;
            while (done.Count < limit)
            {
                var step = policy.Next(new RecoveryContext(status, inventory, done.ToArray()));
                if (step.Kind == RecoveryStepKind.NeedInventory)
                {
                    if (inventory is not null) break;
                    inventory = await _session.Inventory.GetAsync(true, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (step.Kind == RecoveryStepKind.None) break;
                if (step.Kind == RecoveryStepKind.Stop)
                    return new(status, spent, step.Reason ?? "The recovery policy asked to stop.");
                var result = step.Kind switch
                {
                    RecoveryStepKind.Rest => await _session.Character.RestAsync(1, cancellationToken).ConfigureAwait(false),
                    RecoveryStepKind.UseItem => await _session.Character.UseAsync(new ItemRef(step.Id), 1, cancellationToken).ConfigureAwait(false),
                    RecoveryStepKind.Cast => await _session.Character.CastAsync(new SkillRef(step.Id), step.Times, cancellationToken).ConfigureAwait(false),
                    _ => throw new InvalidOperationException($"Unsupported recovery step {step.Kind}.")
                };
                done.Add(step);
                log.Add(step);
                if (step.Kind == RecoveryStepKind.UseItem) inventory = null;
                var after = await _session.StatusAsync(cancellationToken).ConfigureAwait(false);
                spent += Math.Max(0, status.Adventures - after.Adventures);
                if (result.Status is ActionStatus.Rejected or ActionStatus.Unclear || result.Completed < 1)
                    return new(after, spent, $"Recovery step {Describe(step)} failed: {result.Rejection ?? result.Status.ToString()}");
                if (!MadeProgress(status, after))
                    return new(after, spent, $"Recovery step {Describe(step)} brought no progress (HP, MP and effects unchanged); stopping instead of repeating it.");
                status = after;
            }
            return new(status, spent, null);
        }

        private static bool MadeProgress(Status before, Status after)
        {
            if (after.Hp > before.Hp || after.Mp > before.Mp) return true;
            foreach (var effect in after.Effects)
            {
                var old = before.Effects.Where(e => e.Id == effect.Id && e.Name == effect.Name).Select(e => e.Turns).DefaultIfEmpty(-1).Max();
                if (effect.Turns > old) return true;
            }
            return false;
        }

        private static string Describe(RecoveryStep step) => step.Kind switch
        {
            RecoveryStepKind.UseItem => $"use item {step.Id}",
            RecoveryStepKind.Cast => $"cast skill {step.Id}",
            _ => step.Kind.ToString().ToLowerInvariant()
        };

        /// <summary>Conditions checked before a turn (after recovery). Item counts come from the starting inventory plus the run's loot.</summary>
        private static string? StopCondition(IReadOnlyList<StopCondition> conditions, Status status, Inventory? baseline, IReadOnlyList<InventoryItem> loot)
        {
            foreach (var condition in conditions)
            {
                switch (condition.Kind)
                {
                    case StopConditionKind.HpBelowPercent:
                        if (status.MaxHp <= 0 || 100L * status.Hp < (long)condition.Value * status.MaxHp)
                            return $"HP is below {condition.Value}% (or unknown).";
                        break;
                    case StopConditionKind.MpBelowPercent:
                        if (status.MaxMp <= 0 || 100L * status.Mp < (long)condition.Value * status.MaxMp)
                            return $"MP is below {condition.Value}% (or unknown).";
                        break;
                    case StopConditionKind.AdventuresAtMost:
                        if (status.Adventures <= condition.Value) return $"{status.Adventures} adventures left (limit {condition.Value}).";
                        break;
                    case StopConditionKind.MeatAtMost:
                        if (status.Meat <= condition.Value) return $"Meat is at {status.Meat} (limit {condition.Value}).";
                        break;
                    case StopConditionKind.EffectMissing:
                        if (status.Effects.All(e => e.Id != condition.Id)) return $"Effect {condition.Id} is missing.";
                        break;
                    case StopConditionKind.ItemCountAtLeast:
                        var item = new ItemRef(condition.Id ?? 0);
                        var count = (baseline?.Count(item) ?? 0) + loot.Where(i => i.Item == item).Sum(i => i.Quantity);
                        if (count >= condition.Value) return $"Item {condition.Id}: {count} (goal {condition.Value}).";
                        break;
                }
            }
            return null;
        }

        /// <summary>Conditions on what the turn just showed (monster, title, turtle). Evaluated after the whole chain of the turn.</summary>
        private static string? TurnStop(IReadOnlyList<StopCondition> conditions, IReadOnlyList<Encounter> encounters, int from)
        {
            foreach (var condition in conditions)
            {
                if (condition.Kind is not (StopConditionKind.MonsterSeen or StopConditionKind.EncounterTitle or StopConditionKind.TurtleEncounter))
                    continue;
                for (var i = from; i < encounters.Count; i++)
                {
                    var encounter = encounters[i];
                    switch (condition.Kind)
                    {
                        case StopConditionKind.MonsterSeen:
                            if (encounter is FightEncounter fight && string.Equals(fight.MonsterName.Trim(), condition.Pattern?.Trim(), StringComparison.OrdinalIgnoreCase))
                                return $"Monster seen: {fight.MonsterName}.";
                            break;
                        case StopConditionKind.EncounterTitle:
                            var title = encounter.Title ?? (encounter as FightEncounter)?.MonsterName;
                            if (title is not null && condition.Pattern is { } pattern
                                && System.Text.RegularExpressions.Regex.IsMatch(title, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                                return $"Encounter '{title}' matches /{pattern}/.";
                            break;
                        case StopConditionKind.TurtleEncounter:
                            if (encounter is FightEncounter turtleFight
                                    ? CoreSpiel.TurtleTamerEncounters.IsTurtleMonster(turtleFight.MonsterName) || CoreSpiel.TurtleTamerEncounters.IsTurtleEncounter(turtleFight.Title)
                                    : CoreSpiel.TurtleTamerEncounters.IsTurtleEncounter(encounter.Title))
                                return $"Turtle Tamer encounter: {(encounter as FightEncounter)?.MonsterName ?? encounter.Title}"
                                    + (encounter is ChoiceEncounter turtleChoice ? $" (choice {turtleChoice.ChoiceId})." : ".");
                            break;
                    }
                }
            }
            return null;
        }

        private static void ValidateAction(CombatAction action)
        {
#pragma warning disable KOLIBRI0001
            if (action.Kind == CombatActionKind.Macro)
                throw new NotSupportedException("Combat macros are not enabled in KoLibri 0.5 (KOLIBRI0001); they need a recorded live test first.");
#pragma warning restore KOLIBRI0001
            if (action.Kind == CombatActionKind.Stop)
                throw new ArgumentException("CombatAction.Stop is not sent to KoL; it hands the fight back to a human.", nameof(action));
            if (action.Kind is CombatActionKind.Skill or CombatActionKind.Item && action.Id is not > 0)
                throw new ArgumentException($"A {action.Kind} action needs an id.", nameof(action));
        }

        private static string Describe(CombatAction action) => action.Kind switch
        {
            CombatActionKind.Skill => $"skill {action.Id}",
            CombatActionKind.Item => action.SecondId is { } second ? $"items {action.Id}+{second}" : $"item {action.Id}",
            _ => action.Kind.ToString().ToLowerInvariant()
        };

        private static string Id(int? id) => (id ?? 0).ToString(CultureInfo.InvariantCulture);
        private static int Delta(int? before, int? after) => before is { } b && after is { } a && b > a ? b - a : 0;
        private static int? ParseInt(string text)
            => int.TryParse(text.Replace(",", ""), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

        private static IReadOnlyList<InventoryItem> Merge(IEnumerable<InventoryItem> items)
            => items.GroupBy(i => (i.Item, i.Name)).Select(g => new InventoryItem(g.Key.Item, g.Key.Name, g.Sum(i => i.Quantity))).ToArray();
    }

    public abstract record Encounter(Page Page, Location? Location, string? Title);
    public sealed record FightEncounter(Page Page, Location? Location, string? Title, string MonsterName, int? MonsterId, int Round) : Encounter(Page, Location, Title);
    public sealed record ChoiceEncounter(Page Page, Location? Location, string? Title, int ChoiceId, IReadOnlyList<ChoiceOption> Options) : Encounter(Page, Location, Title);
    public sealed record NonCombatEncounter(Page Page, Location? Location, string? Title, IReadOnlyList<InventoryItem> Items, long Meat) : Encounter(Page, Location, Title);
    /// <summary>Kein Zug möglich: keine Adventures, Ort gesperrt, Organ zu voll, unbekannte Seite.</summary>
    public sealed record BlockedEncounter(Page Page, Location? Location, string? Title, BlockReason Reason) : Encounter(Page, Location, Title);

    public enum BlockReason { NoAdventures, LocationLocked, Beaten, UnknownPage, Other }

    public sealed record ChoiceOption(int Option, string Text);

    public sealed record LocationAccess(Location Location, bool Accessible, string? Reason);

    public enum FightOutcome { Ongoing, Won, Lost, RanAway, Stopped, Unknown }

    public sealed record FightRound(int Round, CombatAction Action, FightOutcome Outcome, int Hp, int Mp, Page Page)
    {
        public int DamageDealt { get; init; }
        public int DamageTaken { get; init; }
        public IReadOnlyList<InventoryItem> Items { get; init; } = Array.Empty<InventoryItem>();
        public long Meat { get; init; }
    }

    public sealed record FightResult(FightOutcome Outcome, string MonsterName, IReadOnlyList<FightRound> Rounds)
    {
        public IReadOnlyList<InventoryItem> Items { get; init; } = Array.Empty<InventoryItem>();
        public long Meat { get; init; }
    }

    public sealed record RunRequest(Location Location, int MaxTurns)
    {
        public ICombatStrategy Combat { get; init; } = CombatStrategies.AttackOnly;
        public IChoiceResolver Choices { get; init; } = ChoiceResolvers.AlwaysAsk;
        public IReadOnlyList<StopCondition> StopWhen { get; init; } = Array.Empty<StopCondition>();
        public int MaxRoundsPerFight { get; init; } = 30;
        /// <summary>Erholung und Upkeep vor jedem neuen Zug (nicht mitten in einer Kette). Standard: keine.</summary>
        public IRecoveryPolicy Recovery { get; init; } = RecoveryPolicies.None;
        /// <summary>Obergrenze der Erholungsschritte vor einem Zug.</summary>
        public int MaxRecoveryStepsPerTurn { get; init; } = 5;
    }

    public enum StopConditionKind { HpBelowPercent, MpBelowPercent, AdventuresAtMost, ItemCountAtLeast, EffectMissing, MeatAtMost, MonsterSeen, EncounterTitle, TurtleEncounter }

    public sealed record StopCondition(StopConditionKind Kind, int Value, int? Id = null)
    {
        /// <summary>Monstername (MonsterSeen) bzw. regulärer Ausdruck (EncounterTitle); sonst null.</summary>
        public string? Pattern { get; init; }

        public static StopCondition HpBelowPercent(int percent) => new(StopConditionKind.HpBelowPercent, percent);
        public static StopCondition MpBelowPercent(int percent) => new(StopConditionKind.MpBelowPercent, percent);
        public static StopCondition AdventuresAtMost(int adventures) => new(StopConditionKind.AdventuresAtMost, adventures);
        public static StopCondition ItemCountAtLeast(ItemRef item, int count) => new(StopConditionKind.ItemCountAtLeast, count, item.Id);
        public static StopCondition EffectMissing(int effectId) => new(StopConditionKind.EffectMissing, 0, effectId);
        /// <summary>Stoppt nach dem Zug, in dem ein Kampf gegen dieses Monster (Name, ohne Groß-/Kleinschreibung) vorkam.</summary>
        public static StopCondition MonsterSeen(string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            return new(StopConditionKind.MonsterSeen, 0) { Pattern = name };
        }
        /// <summary>Stoppt nach dem Zug, dessen Begegnungstitel auf den regulären Ausdruck passt (ohne Groß-/Kleinschreibung).</summary>
        public static StopCondition EncounterTitle(string regex)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(regex);
            _ = new Regex(regex); // fail early on a bad pattern
            return new(StopConditionKind.EncounterTitle, 0) { Pattern = regex };
        }
        /// <summary>Stoppt nach dem Zug mit einer Turtle-Tamer-Begegnung (Titel oder Monster, erkannt über den Core).</summary>
        public static StopCondition TurtleEncounter() => new(StopConditionKind.TurtleEncounter, 0);
    }

    public enum RunStopReason { TurnsDone, Condition, NeedsChoice, NeedsCombat, Defeat, Blocked, Cancelled, Error, Recovery }

    public sealed record RunResult(RunStopReason StopReason, int TurnsSpent, IReadOnlyList<Encounter> Encounters, string? Detail)
    {
        /// <summary>Offene Begegnung bei NeedsChoice/NeedsCombat: dort kann ein Mensch weitermachen.</summary>
        public Encounter? Open { get; init; }
        public IReadOnlyList<InventoryItem> Items { get; init; } = Array.Empty<InventoryItem>();
        public long MeatGained { get; init; }
        /// <summary>Die Erholungs- und Upkeep-Schritte, die der Lauf ausgeführt hat.</summary>
        public IReadOnlyList<RecoveryStep> RecoverySteps { get; init; } = Array.Empty<RecoveryStep>();
    }
}
