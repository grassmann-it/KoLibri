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
        /// <summary>Last trustworthy player HP/MP (api status, or explicit HP markup on the fight page).</summary>
        private (int Hp, int Mp)? _knownVitals;

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
                    if (page.Kind == PageKind.NotInFight) return null;
                    // A finished fight page is nothing open (and its loot was counted when it was shown).
                    return ToEncounter(page, _fight?.Location) is FightEncounter open ? Remember(open) : null;
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
            ArgumentOutOfRangeException.ThrowIfNegative(request.MaxRecoveryAdventures);
            var encounters = new List<Encounter>();
            var items = new List<InventoryItem>();
            long meat = 0;
            Status? start = null;
            Status? last = null;
            var recoverySteps = new List<RecoveryStep>();
            var recoveryAdventures = 0;
            var restsArePaid = false;
            Inventory? baseline = null;

            RunResult Finish(RunStopReason reason, string? detail, Encounter? open = null)
            {
                // Costly rests spend adventures that are not turns of the run; they are reported on their own.
                var spent = start is not null && last is not null ? Math.Max(0, start.Adventures - last.Adventures - recoveryAdventures) : 0;
                return new RunResult(reason, spent, encounters.ToArray(), detail)
                {
                    Open = open,
                    RecoverySteps = recoverySteps.ToArray(),
                    RecoveryAdventuresSpent = recoveryAdventures,
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
                        var recovered = await RecoverAsync(request.Recovery, request.MaxRecoveryStepsPerTurn, last, recoverySteps,
                            request.MaxRecoveryAdventures - recoveryAdventures, restsArePaid, cancellationToken).ConfigureAwait(false);
                        last = recovered.Status;
                        recoveryAdventures += recovered.AdventuresSpent;
                        restsArePaid = recovered.RestsArePaid;
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

                    // Resolve the encounter chain (fight → choice → …) of this turn, with a bound on the choices.
                    var choicesThisTurn = 0;
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
                        if (encounter is FinishedFightEncounter finished)
                        {
                            // KoL ended the fight on the page that started it (auto-attack): nothing to send.
                            items.AddRange(finished.Result.Items);
                            meat += finished.Result.Meat;
                            if (finished.Result.Outcome == FightOutcome.Lost)
                            {
                                last = await _session.StatusAsync(cancellationToken).ConfigureAwait(false);
                                return Finish(RunStopReason.Defeat, $"Lost the fight against {finished.MonsterName}.");
                            }
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
                            if (++choicesThisTurn > MaxChoicesPerTurn)
                            {
                                last = await _session.StatusAsync(cancellationToken).ConfigureAwait(false);
                                return Finish(RunStopReason.Error,
                                    $"More than {MaxChoicesPerTurn} choices in one turn (last: choice {choice.ChoiceId}); stopping instead of looping. The choice is still open.", choice);
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
            KeyValuePair<string, string>[] form = [new("snarfblat", location.Snarfblat.ToString(CultureInfo.InvariantCulture))];
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
#pragma warning disable KOLIBRI0001
                case CombatActionKind.Macro:
#pragma warning restore KOLIBRI0001
                    // As KoLmafia sends it (FightRequest.handleMacroAction + GenericRequest.getDataString): action=macro, macrotext=…, pwd.
                    form["action"] = "macro";
                    form["macrotext"] = CombatMacroText.Normalize(action.Macro ?? "");
                    form["pwd"] = await _session.GetPasswordHashAsync(cancellationToken).ConfigureAwait(false);
                    break;
                default: throw new NotSupportedException($"Combat action {action.Kind} cannot be sent.");
            }
            var previous = _lastObservation;
            var page = await _session.SendAsync(HttpMethod.Post, _session.BuildUri("fight.php", null), form.ToArray(), RequestLane.Action, cancellationToken)
                .ConfigureAwait(false);
            if (page.Kind == PageKind.NotInFight)
            {
                _fight = null;
                return new FightRound(previous?.Round + 1 ?? 1, action, FightOutcome.Unknown, _knownVitals?.Hp ?? 0, _knownVitals?.Mp ?? 0, page);
            }
            var observation = Observe(page);
            _lastObservation = observation;
#pragma warning disable KOLIBRI0001
            var isMacro = action.Kind == CombatActionKind.Macro;
#pragma warning restore KOLIBRI0001
            var result = isMacro ? MacroRound(action, page, observation, previous) : PlainRound(action, page, observation, previous);
            return await WithPlayerVitalsAsync(result, isMacro, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// The player's HP/MP for a round. KoL's fight.php normally shows none (0.4 #74: HitPointsProvider): then one status read
        /// while the fight goes on or after a macro (which may hold many rounds), else the last known value. Never a number from
        /// the combat text.
        /// </summary>
        private async Task<FightRound> WithPlayerVitalsAsync(FightRound round, bool isMacro, CancellationToken cancellationToken)
        {
            if (HasHp(_lastObservation))
            {
                _knownVitals = (round.Hp, round.Mp);
                return round;
            }
            if (round.Outcome == FightOutcome.Ongoing || isMacro && round.Outcome != FightOutcome.Unknown)
            {
                var before = _knownVitals?.Hp;
                var status = await _session.StatusAsync(cancellationToken).ConfigureAwait(false);
                _knownVitals = (status.Hp, status.Mp);
                return round with { Hp = status.Hp, Mp = status.Mp, DamageTaken = before is { } b ? Math.Max(0, b - status.Hp) : round.DamageTaken };
            }
            return _knownVitals is { } known ? round with { Hp = known.Hp, Mp = known.Mp } : round;
        }

        private FightRound PlainRound(CombatAction action, Page page, CoreSpiel.CombatObservation observation, CoreSpiel.CombatObservation? previous)
        {
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

        private static readonly Regex MacroActionMarker = new(@"<!--\s*macroaction:\s*(?<action>\w+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex MacroAbortText = new(@"macro abort|could not match item\(s\) for use", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex MacroInvalidText = new(@"invalid macro", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>
        /// One macro answer can hold several rounds (KoL puts a "&lt;!-- macroaction: … --&gt;" comment before each, see KoLmafia
        /// FightRequest.MACRO_PATTERN). Outcome from the page's end markers (&lt;!--WINWINWIN--&gt;, &lt;!--LOSELOSELOSE--&gt;) or the
        /// observation parser; abort/invalid texts as in KoLmafia FightRequest.containsMacroError.
        /// </summary>
        private FightRound MacroRound(CombatAction action, Page page, CoreSpiel.CombatObservation observation, CoreSpiel.CombatObservation? previous)
        {
            var html = page.Html;
            var steps = MacroActionMarker.Matches(html).Count;
            var invalid = MacroInvalidText.IsMatch(html);
            var aborted = !invalid && MacroAbortText.IsMatch(html);
            var outcome = html.Contains("<!--WINWINWIN-->", StringComparison.OrdinalIgnoreCase) ? FightOutcome.Won
                : html.Contains("<!--LOSELOSELOSE-->", StringComparison.OrdinalIgnoreCase) ? FightOutcome.Lost
                // KoL: "You run away, like a little coward." (KoLmafia test_fight_battle_end_both_combat_bars_runaway_macro.html)
                : !OpenFightForm(html) && MacroRanAway.IsMatch(Plain(html)) ? FightOutcome.RanAway
                : observation.Outcome switch
                {
                    CoreSpiel.CombatOutcome.Victory => FightOutcome.Won,
                    CoreSpiel.CombatOutcome.Defeat => FightOutcome.Lost,
                    CoreSpiel.CombatOutcome.Fled => FightOutcome.RanAway,
                    CoreSpiel.CombatOutcome.Ongoing => FightOutcome.Ongoing,
                    _ when OpenFightForm(html) => FightOutcome.Ongoing,
                    _ => FightOutcome.Unknown
                };
            string? error = null;
            if (invalid && outcome is not (FightOutcome.Won or FightOutcome.Lost or FightOutcome.RanAway))
            {
                // KoL refused the macro text: nothing ran. Unknown ends FightToEndAsync without a second request.
                outcome = FightOutcome.Unknown;
                error = "KoL rejected the macro as invalid: " + MacroMessage(html, MacroInvalidText);
            }
            else if (aborted && outcome == FightOutcome.Ongoing)
                error = "KoL aborted the macro; the fight is still open: " + MacroMessage(html, MacroAbortText);
            var rounds = Math.Max(steps, error is not null && outcome == FightOutcome.Unknown ? 0 : 1);
            var (gained, meat) = outcome == FightOutcome.Ongoing || outcome == FightOutcome.Unknown ? (Array.Empty<InventoryItem>(), 0L) : Loot(html);
            // "var onturn = N" names the round the macro started in (KoLmafia test_fight_lose.html: onturn = 1 after 19 actions).
            var start = observation.Round > 0 ? observation.Round : (previous?.Round ?? _fight?.Round ?? 0) + 1;
            var number = start + Math.Max(1, rounds) - 1;
            var round = new FightRound(number, action, outcome,
                observation.PlayerHp ?? previous?.PlayerHp ?? 0, observation.PlayerMp ?? previous?.PlayerMp ?? 0, page)
            {
                DamageDealt = Delta(previous?.EnemyHp, observation.EnemyHp),
                DamageTaken = Delta(previous?.PlayerHp, observation.PlayerHp),
                Items = gained,
                Meat = meat,
#pragma warning disable KOLIBRI0001
                RoundsInResponse = rounds,
                MacroError = error
#pragma warning restore KOLIBRI0001
            };
            if (outcome == FightOutcome.Ongoing && _fight is { } open)
                _fight = open with { Page = page, Round = round.Round };
            else if (outcome != FightOutcome.Ongoing)
                _fight = null;
            if (gained.Length > 0) _session.RaiseItems(gained, ItemSource.Fight);
            return round;
        }

        private static readonly Regex MacroRanAway = new(@"\byou run away\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>KoL's text from the marker to the end of its sentence, e.g. "Macro Aborted: 37 instructions (In a row?!) executed without any actions being taken."</summary>
        private static string MacroMessage(string html, Regex marker)
        {
            // KoL puts the message in its own block, e.g. live 2026-11-27: Invalid macro command encountered:<br/>"frobnicate"<table>…
            var raw = marker.Match(html);
            if (raw.Success)
            {
                var blockEnd = Regex.Match(html[raw.Index..], @"</?(?:table|p|form|center|div|td)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (blockEnd.Success && blockEnd.Index is > 0 and < 400)
                {
                    var block = Regex.Replace(Plain(html.Substring(raw.Index, blockEnd.Index)), @"\s*:\s*", ": ").Trim();
                    if (block.Length > raw.Length) return block;
                }
            }
            var text = Plain(html);
            var match = marker.Match(text);
            if (!match.Success) return "(no text)";
            var end = Regex.Match(text[match.Index..], @"\.(?=\s|$)");
            var sentence = (end.Success && end.Index < 160 ? text.Substring(match.Index, end.Index + 1) : text.Substring(match.Index, Math.Min(match.Length, text.Length - match.Index))).Trim();
            return sentence;
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
            _knownVitals = (hp, mp);
            var outcome = FightOutcome.Ongoing;
            while (outcome == FightOutcome.Ongoing)
            {
                if (rounds.Count >= maxRounds) { outcome = FightOutcome.Stopped; break; }
                var context = new CombatContext(fight, fight.Round + rounds.Count, hp, status.MaxHp, mp, status.MaxMp, rounds.ToArray())
                {
                    Effects = status.Effects,
                    AvailableSkills = CombatOptions(_lastObservation, CoreSpiel.CombatActionKind.Skill),
                    AvailableItems = CombatOptions(_lastObservation, CoreSpiel.CombatActionKind.Item)
                };
                var action = strategy.Next(context);
                if (action.Kind == CombatActionKind.Stop) { outcome = FightOutcome.Stopped; break; }
#pragma warning disable KOLIBRI0001
                var isMacro = action.Kind == CombatActionKind.Macro;
                // KoL aborted this very macro last time and the fight is still open: sending it again would abort again.
                if (isMacro && rounds.Count > 0 && rounds[^1] is { MacroError: not null, Action: var last } && last == action)
                { outcome = FightOutcome.Stopped; break; }
#pragma warning restore KOLIBRI0001
                // FightAsync already carries the player's HP/MP (page markup or one status read, see WithPlayerVitalsAsync).
                var round = await FightAsync(action, cancellationToken).ConfigureAwait(false);
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

        private static readonly Regex MpSuffix = new(@"\s*\((?<mp>\d+)\s*mp\)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex CountSuffix = new(@"\s*\(\d+\)\s*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>Skill/item dropdown of the current fight page as options; MP cost from the "(N mp)" label, else from the game data.</summary>
        private IReadOnlyList<CombatOption> CombatOptions(CoreSpiel.CombatObservation? observation, CoreSpiel.CombatActionKind kind)
        {
            if (observation is null) return Array.Empty<CombatOption>();
            var options = new List<CombatOption>();
            foreach (var action in observation.AvailableActions)
            {
                if (action.Kind != kind || !action.IsEnabled
                    || !int.TryParse(action.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) continue;
                var name = action.Name ?? "";
                int? mp = null;
                if (kind == CoreSpiel.CombatActionKind.Skill)
                {
                    if (MpSuffix.Match(name) is { Success: true } m)
                    {
                        mp = int.Parse(m.Groups["mp"].Value, CultureInfo.InvariantCulture);
                        name = name[..m.Index];
                    }
                    mp ??= _session.Client.Data.Skill(new SkillRef(id))?.MpCost;
                }
                else name = CountSuffix.Replace(name, "");
                options.Add(new CombatOption(id, name.Trim(), mp));
            }
            return options;
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
            var page = await _session.SendAsync(HttpMethod.Post, _session.BuildUri("choice.php", null), form.ToArray(), RequestLane.Action, cancellationToken)
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
            if (page.Kind == PageKind.Fight || page.Url.AbsolutePath.EndsWith("fight.php", StringComparison.OrdinalIgnoreCase) && OpenFightForm(page.Html)
                || FinishedFightMarker(page.Html))
            {
                var observation = Observe(page);
                var name = observation.MonsterName ?? observation.Monster ?? "unknown monster";
                var monsterId = int.TryParse(observation.MonsterId, NumberStyles.None, CultureInfo.InvariantCulture, out var mid) ? mid : (int?)null;
                // KoL's end markers without a fight form beat the parser's "Ongoing" (it says so for any page with "onturn").
                var ended = FinishedOutcome(page.Html, observation);
                if (ended is null && (observation.Outcome is CoreSpiel.CombatOutcome.Ongoing || OpenFightForm(page.Html)))
                {
                    _lastObservation = observation;
                    return new FightEncounter(page, location, name, name, monsterId, Math.Max(1, observation.Round));
                }
                // KoL already ended the fight on this page (auto-attack, live 2026-11-27): a fight, not a NonCombat
                // titled with combat text such as "CRITICAL HIT!".
                if (ended is { } && (observation.MonsterName ?? observation.Monster) is not null)
                {
                    var (loot, gained) = Loot(page.Html);
                    var result = new FightResult(ended.Value, name, Array.Empty<FightRound>()) { Items = loot, Meat = gained };
                    return new FinishedFightEncounter(page, location, name, name, monsterId, result);
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

        private static bool FinishedFightMarker(string html)
            => html.Contains("<!--WINWINWIN-->", StringComparison.OrdinalIgnoreCase)
               || html.Contains("<!--LOSELOSELOSE-->", StringComparison.OrdinalIgnoreCase);

        /// <summary>Outcome of a fight page without a fight form: KoL's end markers first, then the observation parser.</summary>
        private static FightOutcome? FinishedOutcome(string html, CoreSpiel.CombatObservation observation)
        {
            if (OpenFightForm(html)) return null;
            if (html.Contains("<!--WINWINWIN-->", StringComparison.OrdinalIgnoreCase)) return FightOutcome.Won;
            if (html.Contains("<!--LOSELOSELOSE-->", StringComparison.OrdinalIgnoreCase)) return FightOutcome.Lost;
            return observation.Outcome switch
            {
                CoreSpiel.CombatOutcome.Victory => FightOutcome.Won,
                CoreSpiel.CombatOutcome.Defeat => FightOutcome.Lost,
                CoreSpiel.CombatOutcome.Fled => FightOutcome.RanAway,
                _ => null
            };
        }

        private static readonly Regex PlayerHpMarkup = new(
            @"\b(?:id|name|class)\s*=\s*['""]?(?:playerhp|player-hp|myhp|currenthp|current-hp|hitpoints)\b|\bdata-player-hp\b|\b(?:playerhp|myhp|currenthp)\s*=\s*\d",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex PlayerMpMarkup = new(
            @"\b(?:id|name|class)\s*=\s*['""]?(?:playermp|player-mp|mymp|currentmp|current-mp|mojopoints)\b|\bdata-player-mp\b|\b(?:playermp|mymp|currentmp)\s*=\s*\d",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>
        /// Parses a fight page. The player's HP/MP are kept only when the page marks them explicitly: KoL's fight.php has none, and
        /// the parser's text fallback picks numbers out of the combat text (live 2026-11-27: pageHp 8 and 1 at 504/504 HP).
        /// </summary>
        private static CoreSpiel.CombatObservation Observe(Page page)
        {
            var observation = new CoreSpiel.KoLCombatObservationParser().Parse(page.Html, page.Url);
            if (!PlayerHpMarkup.IsMatch(page.Html)) observation = observation with { PlayerHp = null, PlayerMaxHp = null };
            if (!PlayerMpMarkup.IsMatch(page.Html)) observation = observation with { PlayerMp = null, PlayerMaxMp = null };
            return observation;
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
            if (encounter is FinishedFightEncounter finished)
            {
                if (finished.Result.Items.Count > 0) _session.RaiseItems(finished.Result.Items, ItemSource.Fight);
                _session.RaiseFightEnded(finished.Result);
            }
            return encounter;
        }

        private const int BeatenUpEffectId = 7;

        /// <summary>Choice transitions allowed within one turn before the run stops (a resolver loop, or KoL chaining choices).</summary>
        internal const int MaxChoicesPerTurn = 10;

        private readonly record struct RecoveryOutcome(Status Status, int AdventuresSpent, string? StopDetail, bool RestsArePaid);

        /// <summary>
        /// Asks the policy for steps before a new turn, runs each through <see cref="CharacterActions"/>, re-reads the status
        /// and repeats until the policy says None or the limit is reached. A step without progress ends the run: no guessing, no endless loop.
        /// Steps that cost adventures (a rest once the free rests are used up) are bounded by <paramref name="adventureBudget"/>:
        /// KoL only shows that a rest was paid in its answer, so the first paid rest is seen afterwards; from then on every
        /// further rest is known to cost one and is refused before it is sent when the budget is used up.
        /// </summary>
        private async Task<RecoveryOutcome> RecoverAsync(IRecoveryPolicy policy, int limit, Status status, List<RecoveryStep> log,
            int adventureBudget, bool restsArePaid, CancellationToken cancellationToken)
        {
            if (ReferenceEquals(policy, RecoveryPolicies.None) || limit <= 0) return new(status, 0, null, restsArePaid);
            if (status.Effects.Any(e => e.Id == BeatenUpEffectId || e.Name.Equals("Beaten Up", StringComparison.OrdinalIgnoreCase)))
                return new(status, 0, "You are Beaten Up; KoLibri does not guess a way to heal that. Heal by hand and start again.", restsArePaid);
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
                    return new(status, spent, step.Reason ?? "The recovery policy asked to stop.", restsArePaid);
                if (step.Kind == RecoveryStepKind.Rest && restsArePaid && spent + 1 > adventureBudget)
                    return new(status, spent, PaidRestStop(adventureBudget, spent), restsArePaid);
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
                var cost = Math.Max(0, status.Adventures - after.Adventures);
                spent += cost;
                if (cost > 0 && step.Kind == RecoveryStepKind.Rest) restsArePaid = true;
                if (result.Status is ActionStatus.Rejected or ActionStatus.Unclear || result.Completed < 1)
                    return new(after, spent, $"Recovery step {Describe(step)} failed: {result.Rejection ?? result.Status.ToString()}", restsArePaid);
                if (!MadeProgress(status, after))
                    return new(after, spent, $"Recovery step {Describe(step)} brought no progress (HP, MP and effects unchanged); stopping instead of repeating it.", restsArePaid);
                if (spent > adventureBudget)
                    return new(after, spent, PaidRestStop(adventureBudget, spent), restsArePaid);
                status = after;
            }
            return new(status, spent, null, restsArePaid);
        }

        private static string PaidRestStop(int budget, int spent)
            => spent > budget
                ? "A rest cost an adventure (the free rests are used up), more than MaxRecoveryAdventures allows; no further paid rest is sent."
                : "Rests are no longer free and the MaxRecoveryAdventures budget is used up; stopping before a paid rest.";

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
                            if (MonsterOf(encounter) is { } seen && string.Equals(seen.Trim(), condition.Pattern?.Trim(), StringComparison.OrdinalIgnoreCase))
                                return $"Monster seen: {seen}.";
                            break;
                        case StopConditionKind.EncounterTitle:
                            var title = encounter.Title ?? MonsterOf(encounter);
                            if (title is not null && condition.Pattern is { } pattern
                                && System.Text.RegularExpressions.Regex.IsMatch(title, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                                return $"Encounter '{title}' matches /{pattern}/.";
                            break;
                        case StopConditionKind.TurtleEncounter:
                            if (MonsterOf(encounter) is { } turtleMonster
                                    ? CoreSpiel.TurtleTamerEncounters.IsTurtleMonster(turtleMonster) || CoreSpiel.TurtleTamerEncounters.IsTurtleEncounter(encounter.Title)
                                    : CoreSpiel.TurtleTamerEncounters.IsTurtleEncounter(encounter.Title))
                                return $"Turtle Tamer encounter: {MonsterOf(encounter) ?? encounter.Title}"
                                    + (encounter is ChoiceEncounter turtleChoice ? $" (choice {turtleChoice.ChoiceId})." : ".");
                            break;
                    }
                }
            }
            return null;
        }

        /// <summary>The monster of an open or an already finished fight; null for other encounters.</summary>
        private static string? MonsterOf(Encounter encounter) => encounter switch
        {
            FightEncounter fight => fight.MonsterName,
            FinishedFightEncounter finished => finished.MonsterName,
            _ => null
        };

        private void ValidateAction(CombatAction action)
        {
#pragma warning disable KOLIBRI0001
            if (action.Kind == CombatActionKind.Macro)
            {
                if (!_session.Client.Options.EnableExperimentalMacros)
                    throw new NotSupportedException("Combat macros are not enabled in KoLibri 0.5 (KOLIBRI0001); they need a recorded live test first. "
                        + "To try them anyway, set KolibriOptions.EnableExperimentalMacros = true.");
                if (string.IsNullOrWhiteSpace(action.Macro))
                    throw new ArgumentException("A macro action needs macro text.", nameof(action));
                _ = CombatMacroText.Normalize(action.Macro); // empty after comments or too long: refuse before sending
            }
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
#pragma warning disable KOLIBRI0001
            CombatActionKind.Macro => CombatMacroText.Summary(action.Macro),
#pragma warning restore KOLIBRI0001
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
    /// <summary>An open fight: send actions with FightAsync/FightToEndAsync.</summary>
    public sealed record FightEncounter(Page Page, Location? Location, string? Title, string MonsterName, int? MonsterId, int Round) : Encounter(Page, Location, Title);
    /// <summary>
    /// A fight KoL already ended on the page that started it (auto-attack, or a fight decided before any action of ours): outcome,
    /// loot and meat of that page in <paramref name="Result"/>. Nothing is open and nothing more is sent; the run counts it like
    /// a fight (MonsterSeen, FightEnded). <see cref="Encounter.Title"/> is the monster's name, never combat text.
    /// </summary>
    public sealed record FinishedFightEncounter(Page Page, Location? Location, string? Title, string MonsterName, int? MonsterId, FightResult Result) : Encounter(Page, Location, Title);
    public sealed record ChoiceEncounter(Page Page, Location? Location, string? Title, int ChoiceId, IReadOnlyList<ChoiceOption> Options) : Encounter(Page, Location, Title);
    public sealed record NonCombatEncounter(Page Page, Location? Location, string? Title, IReadOnlyList<InventoryItem> Items, long Meat) : Encounter(Page, Location, Title);
    /// <summary>Kein Zug möglich: keine Adventures, Ort gesperrt, Organ zu voll, unbekannte Seite.</summary>
    public sealed record BlockedEncounter(Page Page, Location? Location, string? Title, BlockReason Reason) : Encounter(Page, Location, Title);

    public enum BlockReason { NoAdventures, LocationLocked, Beaten, UnknownPage, Other }

    public sealed record ChoiceOption(int Option, string Text);

    public sealed record LocationAccess(Location Location, bool Accessible, string? Reason);

    public enum FightOutcome { Ongoing, Won, Lost, RanAway, Stopped, Unknown }

    /// <summary>One answer of fight.php.</summary>
    /// <param name="Hp">The player's HP after this round. KoL's fight page normally shows none: then they come from
    /// <c>api.php?what=status</c> (read once after the round while the fight goes on, or after a macro); otherwise the last known value.
    /// Never a number guessed from the combat text or the monster's HP.</param>
    /// <param name="Mp">The player's MP, from the same source as <paramref name="Hp"/>.</param>
    public sealed record FightRound(int Round, CombatAction Action, FightOutcome Outcome, int Hp, int Mp, Page Page)
    {
        public int DamageDealt { get; init; }
        public int DamageTaken { get; init; }
        public IReadOnlyList<InventoryItem> Items { get; init; } = Array.Empty<InventoryItem>();
        public long Meat { get; init; }

        /// <summary>Wie viele Kampfrunden diese eine Antwort enthält: 1 für normale Aktionen, bei Makros die Zahl der ausgeführten Makro-Aktionen (0 bei ungültigem Makro).</summary>
        [System.Diagnostics.CodeAnalysis.Experimental(KolibriDiagnostics.CombatMacros)]
        public int RoundsInResponse { get; init; } = 1;

        /// <summary>Nur bei Makros: KoL hat das Makro abgebrochen (Kampf offen, Outcome Ongoing) oder als ungültig abgelehnt (Outcome Unknown). Sonst null.</summary>
        [System.Diagnostics.CodeAnalysis.Experimental(KolibriDiagnostics.CombatMacros)]
        public string? MacroError { get; init; }
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
        /// <summary>
        /// Adventures, die die Erholung über den ganzen Lauf höchstens kosten darf (bezahlte Rasten, sobald die freien
        /// verbraucht sind); sie zählen nicht zu MaxTurns. Standard 0: keine bezahlten Rasten. KoL zeigt erst an der
        /// Antwort, dass eine Rast bezahlt war; diese eine wird gemeldet (RecoveryAdventuresSpent), danach stoppt der Lauf.
        /// </summary>
        public int MaxRecoveryAdventures { get; init; }
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
        /// <summary>Adventures, die die Erholung gekostet hat (bezahlte Rasten). Nicht in <see cref="TurnsSpent"/> enthalten.</summary>
        public int RecoveryAdventuresSpent { get; init; }
    }
}
