using System.Globalization;
using System.Text.Json;
using KolArbitrage.Core.Accounts;
using KolArbitrage.Core.Adventure;

// These commands intentionally stop at the read/preview boundary.  Keeping
// them in a separate partial keeps the existing pricing CLI stable while the
// adventure executor is still being integrated behind an approved transport.
internal static partial class KolibriCli
{
    private sealed record CliAdventureLocation(
        string Id,
        string Name,
        string? Description,
        bool Enabled,
        bool PreviewOnly);

    private static readonly IReadOnlyList<CliAdventureLocation> AdventureLocations =
    [
        new("skeleton-of-crimbos-past", "Skeleton of Crimbos Past",
            "Daily skeleton adventure used by the adventure planner.", true, true),
        new("knucklebones", "Knucklebones target",
            "Generic allowlisted location for a bounded knucklebones preview.", false, true),
        new("lucky-surprise-egg", "Lucky Surprise Egg",
            "Egg consumption is planned separately and never sent by this CLI.", false, true),
        new("elf-guard", "Elf Guard",
            "Read-only placeholder for an explicitly configured Elf Guard run.", false, true),
        new("crimbuccaneer", "Crimbuccaneer",
            "Read-only placeholder for an explicitly configured Crimbuccaneer run.", false, true)
    ];

    private static async Task<int> AdventureCommandAsync(string[] args)
    {
        if (args.Length == 0 || HasFlag(args, "--help") || IsHelp(args[0]))
        {
            PrintAdventureHelp();
            return 0;
        }

        return args[0].ToLowerInvariant() switch
        {
            "locations" or "location" or "list" => await AdventureLocationsAsync(args[1..]),
            "start" or "begin" => await AdventureStartAsync(args[1..]),
            "inspect" or "status" or "resume" => await AdventureInspectAsync(args[1..]),
            "step" or "advance" => await AdventureStepAsync(args[1..]),
            "bounded-preview" or "run-preview" => await AdventureBoundedPreviewAsync(args[1..]),
            "run" or "execute" => await AdventureRunAsync(args[1..]),
            "stop" or "abort" => await AdventureStopAsync(args[1..]),
            _ => Fail($"Unbekannter adventure-Befehl: {args[0]}")
        };
    }

    private static Task<int> AdventureLocationsAsync(string[] args)
    {
        var catalogPath = GetOption(args, "--catalog");
        IReadOnlyList<object> catalogLocations = Array.Empty<object>();
        if (catalogPath is not null)
        {
            var catalog = AdventureLocationCatalog.LoadJson(catalogPath, JsonOptions);
            catalogLocations = catalog.Locations.Cast<object>().ToArray();
        }
        var requested = GetOption(args, "--location")
            ?? GetOption(args, "--adventure");
        var locations = string.IsNullOrWhiteSpace(requested)
            ? AdventureLocations
            : AdventureLocations.Where(x => string.Equals(x.Id, requested.Trim(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(x.Name, requested.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        var locationDocuments = catalogLocations.Count == 0
            ? locations.Cast<object>().ToArray()
            : catalogLocations;

        WriteJson(new
        {
            command = "adventure locations",
            readOnly = true,
            mutating = false,
            post = false,
            previewOnly = true,
            count = locationDocuments.Count,
            locations = locationDocuments,
            catalogPath,
            catalog = catalogLocations
        });
        return Task.FromResult(0);
    }

    private static Task<int> AdventureStartAsync(string[] args)
    {
        var location = ReadAdventureId(args);
        var runId = GetOption(args, "--run-id")
            ?? GetOption(args, "--id")
            ?? $"preview-{Guid.NewGuid():N}";
        var role = ParseRole(GetOption(args, "--role") ?? "main");
        var maxTurns = ReadInt(args, "--max-turns", ReadInt(args, "--turns", int.MaxValue));
        var turnsUsed = ReadInt(args, "--turns-used", 0);
        var maxRuns = ReadInt(args, "--max-runs", int.MaxValue);
        var runsUsed = ReadInt(args, "--runs-used", 0);
        var limit = new AdventureDailyLimitContract
        {
            Day = DateOnly.FromDateTime(DateTime.UtcNow),
            MaxTurns = Math.Max(0, maxTurns),
            TurnsUsed = Math.Max(0, turnsUsed),
            MaxRuns = Math.Max(0, maxRuns),
            RunsUsed = Math.Max(0, runsUsed)
        };
        var request = new AdventureRunRequest
        {
            RunId = runId,
            AdventureId = location,
            Role = role,
            DailyLimit = limit
        };
        var allowlist = new AdventureActionAllowlist().AddAdventure(location);
        var decision = new AdventureRunKernel().Begin(request, allowlist);
        var checkpointPath = CheckpointPath(args);
        if (checkpointPath is not null)
            SaveCheckpoint(checkpointPath, decision.Checkpoint);

        WriteJson(new
        {
            command = "adventure start",
            readOnly = true,
            previewOnly = true,
            mutating = false,
            post = false,
            executed = false,
            approvalRequired = true,
            runId,
            role = role.ToString(),
            location,
            decision = DecisionDocument(decision),
            checkpoint = decision.Checkpoint,
            checkpointPath
        });
        return Task.FromResult(decision.IsAborted ? 3 : 0);
    }

    private static Task<int> AdventureInspectAsync(string[] args)
    {
        var checkpointPath = CheckpointPath(args);
        var checkpoint = checkpointPath is null ? null : ReadCheckpoint(checkpointPath);
        var location = checkpoint?.AdventureId ?? ReadOptionalAdventureId(args);
        AdventureEncounterState? encounter = null;
        var fixturePath = GetOption(args, "--fixture");
        if (fixturePath is not null)
        {
            if (location is null)
                throw new ArgumentException("--location is required when --fixture is used.");
            var html = File.ReadAllText(fixturePath);
            encounter = new KoLAdventureEncounterParser().Parse(
                location,
                Uri.TryCreate(GetOption(args, "--uri"), UriKind.Absolute, out var uri) ? uri : null,
                html,
                ReadInt(args, "--retry", 0));
        }

        WriteJson(new
        {
            command = "adventure inspect",
            readOnly = true,
            previewOnly = true,
            mutating = false,
            post = false,
            executed = false,
            requiresRefresh = checkpoint is not null && checkpoint.InFlightRequestId is not null,
            runId = checkpoint?.RunId ?? GetOption(args, "--run-id"),
            location,
            checkpoint,
            encounter = encounter is null ? null : EncounterDocument(encounter)
        });
        return Task.FromResult(0);
    }

    private static Task<int> AdventureStepAsync(string[] args)
    {
        var checkpointPath = CheckpointPath(args);
        var checkpoint = checkpointPath is null ? null : ReadCheckpoint(checkpointPath);
        var location = checkpoint?.AdventureId ?? ReadAdventureId(args);
        var runId = checkpoint?.RunId
            ?? GetOption(args, "--run-id")
            ?? $"preview-{Guid.NewGuid():N}";
        var role = checkpoint?.Role ?? ParseRole(GetOption(args, "--role") ?? "main");
        var actionId = GetOption(args, "--action-id")
            ?? GetOption(args, "--action")
            ?? "attack";
        var encounterId = GetOption(args, "--encounter");
        var monster = GetOption(args, "--monster");
        var round = ReadInt(args, "--round", checkpoint?.FightRound is { } previous ? previous + 1 : 1);
        var choiceId = GetOption(args, "--choice-id") ?? GetOption(args, "--choice");
        var optionId = GetOption(args, "--option");
        var available = ParseCsv(GetOption(args, "--available"));
        var fixturePath = GetOption(args, "--fixture");
        AdventureEncounterState? fixture = null;
        if (fixturePath is not null)
        {
            var html = File.ReadAllText(fixturePath);
            fixture = new KoLAdventureEncounterParser().Parse(
                location,
                Uri.TryCreate(GetOption(args, "--uri"), UriKind.Absolute, out var uri) ? uri : null,
                html);
            encounterId ??= fixture.Location;
            round = fixture.FightRound ?? round;
            choiceId ??= fixture.ChoiceId;
            available = fixture.AvailableChoices.Count == 0 ? available : fixture.AvailableChoices;
        }

        var observedResponseHash = fixture?.ResponseHash ?? PreviewHash(args);
        var allowlist = new AdventureActionAllowlist().AddAdventure(location);
        AdventureKernelObservation observation;
        if (!string.IsNullOrWhiteSpace(choiceId))
        {
            optionId ??= available.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(optionId))
                throw new ArgumentException("A choice step needs --option or --available.");
            allowlist.AddChoiceOption(location, choiceId, optionId, encounterId);
            observation = new AdventureKernelObservation
            {
                AdventureId = location,
                Kind = AdventureEncounterKind.Choice,
                EncounterId = encounterId,
                ChoiceId = choiceId,
                AvailableChoices = available.Count == 0 ? new[] { optionId } : available,
                ResponseHash = observedResponseHash
            };
        }
        else
        {
            allowlist.AddFightAction(location, actionId, encounterId, round, monster);
            observation = new AdventureKernelObservation
            {
                AdventureId = location,
                Kind = AdventureEncounterKind.Fight,
                EncounterId = encounterId,
                MonsterId = monster,
                FightRound = round,
                ResponseHash = observedResponseHash
            };
        }

        AdventureKernelDecision decision;
        if (checkpoint is null)
        {
            var started = new AdventureRunKernel().Begin(
                new AdventureRunRequest
                {
                    RunId = runId,
                    AdventureId = location,
                    Role = role,
                    DailyLimit = new AdventureDailyLimitContract
                    {
                        Day = DateOnly.FromDateTime(DateTime.UtcNow),
                        MaxTurns = ReadInt(args, "--max-turns", int.MaxValue),
                        MaxRuns = ReadInt(args, "--max-runs", int.MaxValue)
                    }
                },
                allowlist);
            decision = started.IsAborted
                ? started
                : new AdventureRunKernel().Observe(started.State, observation, allowlist);
        }
        else
        {
            var state = checkpoint.ToState();
            decision = state.IsTerminal
                ? new AdventureKernelDecision
                {
                    State = state,
                    Checkpoint = checkpoint,
                    AbortReason = state is AdventureAbortedRunState aborted ? aborted.Reason : AdventureAbortReason.TerminalState
                }
                : new AdventureRunKernel().Observe(state, observation, allowlist);
        }

        if (checkpointPath is not null && !decision.IsAborted)
            SaveCheckpoint(checkpointPath, decision.Checkpoint);

        WriteJson(new
        {
            command = "adventure step",
            readOnly = true,
            previewOnly = true,
            mutating = false,
            post = false,
            executed = false,
            approvalRequired = true,
            wouldPost = decision.NextAction is not null,
            runId,
            location,
            fixture = fixture is not null,
            observation,
            decision = DecisionDocument(decision),
            checkpoint = decision.Checkpoint,
            checkpointPath
        });
        return Task.FromResult(decision.IsAborted ? 3 : 0);
    }

    private static Task<int> AdventureStopAsync(string[] args)
    {
        var checkpointPath = CheckpointPath(args);
        var checkpoint = checkpointPath is null ? null : ReadCheckpoint(checkpointPath);
        var runId = checkpoint?.RunId ?? GetOption(args, "--run-id") ?? ReadOptionalAdventureId(args);
        if (string.IsNullOrWhiteSpace(runId))
            throw new ArgumentException("--run-id or --checkpoint is required.");
        var reason = GetOption(args, "--reason") ?? "Stopped by operator preview.";
        WriteJson(new
        {
            command = "adventure stop",
            readOnly = true,
            previewOnly = true,
            mutating = false,
            post = false,
            executed = false,
            stopped = true,
            aborted = true,
            runId,
            location = checkpoint?.AdventureId,
            reason,
            checkpoint,
            checkpointPath,
            note = "The local preview was stopped; no KoL request was sent."
        });
        return Task.FromResult(0);
    }

    private static Task<int> AdventureBoundedPreviewAsync(string[] args)
    {
        var location = ReadAdventureId(args);
        var runId = GetOption(args, "--run-id") ?? $"preview-{Guid.NewGuid():N}";
        var conditionsPath = GetOption(args, "--conditions-file");
        var snapshotPath = GetOption(args, "--snapshot-file");
        var conditions = conditionsPath is null
            ? Array.Empty<AdventureCondition>()
            : ReadJsonArray<AdventureCondition>(conditionsPath, "conditions");
        AdventureConditionSnapshot? snapshot = snapshotPath is null
            ? null
            : ReadJsonObject<AdventureConditionSnapshot>(snapshotPath, "snapshot");
        var results = snapshot is null
            ? conditions.Select(condition => new AdventureConditionResult
            {
                Condition = condition.Normalize(),
                IsSatisfied = false,
                Reason = "A fresh condition snapshot is required before an adventure run can start."
            }).ToArray()
            : AdventureConditionEvaluator.EvaluateAll(conditions, snapshot);
        var stopReason = conditions.Length == 0
            ? "No conditions supplied; preview is ready for an explicit bounded run."
            : snapshot is null
                ? "ConditionsBlocked"
                : results.All(result => result.IsSatisfied) ? "ConditionsSatisfied" : "ReadyToRun";

        WriteJson(new
        {
            command = "adventure bounded-preview",
            readOnly = true,
            previewOnly = true,
            mutating = false,
            post = false,
            executed = false,
            approvalRequired = true,
            runId,
            location,
            maxGatewaySteps = Math.Clamp(ReadInt(args, "--max-steps", 20), 1, 10_000),
            maxDurationSeconds = Math.Max(1, ReadInt(args, "--max-duration-seconds", 300)),
            stopReason,
            conditions = results,
            conditionSource = conditionsPath,
            snapshotSource = snapshotPath,
            note = "Preview only; no adventure.php, fight.php or choice.php request was sent."
        });
        return Task.FromResult(stopReason == "ConditionsBlocked" ? 3 : 0);
    }

    private static async Task<int> AdventureRunAsync(string[] args)
    {
        var location = ReadAdventureId(args);
        var runId = GetOption(args, "--run-id") ?? $"cli-{Guid.NewGuid():N}";
        var role = ParseRole(GetOption(args, "--role") ?? "main");
        var conditionsPath = GetOption(args, "--conditions-file");
        var conditions = conditionsPath is null
            ? Array.Empty<AdventureCondition>()
            : ReadJsonArray<AdventureCondition>(conditionsPath, "conditions");
        if (!HasFlag(args, "--execute"))
        {
            WriteJson(new
            {
                command = "adventure run",
                readOnly = true,
                previewOnly = true,
                executed = false,
                approvalRequired = true,
                runId,
                location,
                role = role.ToString(),
                conditions,
                note = "Preview only. Add --execute and --approval-token to submit allowlisted requests."
            });
            return 0;
        }

        var approvalToken = GetOption(args, "--approval-token");
        if (string.IsNullOrWhiteSpace(approvalToken))
            throw new ArgumentException("Live adventure execution requires --approval-token.");

        var approval = new DelegateAdventureApprovalHook((context, _) =>
            ValueTask.FromResult(string.Equals(approvalToken, GetOption(args, "--approval-token"), StringComparison.Ordinal)
                ? AdventureApprovalDecision.Allow(approvalToken)
                : AdventureApprovalDecision.Deny("The supplied approval token does not match the command approval.")));
        var verification = new DelegateAdventureVerificationHook((context, _) =>
            ValueTask.FromResult(context.Response.Accepted == true && context.Observation.Kind != AdventureEncounterKind.Unknown
                ? AdventureVerificationResult.Success()
                : AdventureVerificationResult.Failed("The live response was not a known, accepted adventure encounter.")));

        await using var session = await KoLAdventureSessionFactory.CreateAsync(
            role,
            approval,
            verification,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        var allowlist = new AdventureActionAllowlist().AddAdventure(location);
        foreach (var actionId in GetOptions(args, "--fight-action"))
            allowlist.AddFightAction(location, actionId);
        foreach (var choice in GetOptions(args, "--choice-action"))
        {
            var separator = choice.IndexOf(':');
            if (separator <= 0 || separator == choice.Length - 1)
                throw new ArgumentException("--choice-action expects CHOICE_ID:OPTION_ID.");
            allowlist.AddChoiceOption(location, choice[..separator], choice[(separator + 1)..]);
        }

        var request = new AdventureRunRequest
        {
            RunId = runId,
            AdventureId = location,
            Role = role,
            DailyLimit = new AdventureDailyLimitContract
            {
                Day = DateOnly.FromDateTime(DateTime.UtcNow),
                MaxTurns = Math.Max(0, ReadInt(args, "--max-turns", int.MaxValue)),
                MaxRuns = Math.Max(0, ReadInt(args, "--max-runs", int.MaxValue))
            }
        };
        var provider = new AccountAdventureConditionSnapshotProvider(session.AccountReader, conditions, forceRefresh: true);
        var runner = new BoundedAdventureRunner(session.Gateway);
        var result = await runner.RunAsync(
            request,
            allowlist,
            conditions,
            provider,
            new BoundedAdventureRunnerOptions
            {
                MaxGatewaySteps = ReadInt(args, "--max-steps", 20),
                MaxDuration = TimeSpan.FromSeconds(Math.Max(1, ReadInt(args, "--max-duration-seconds", 300)))
            }).ConfigureAwait(false);

        WriteJson(new
        {
            command = "adventure run",
            readOnly = false,
            previewOnly = false,
            executed = true,
            approvalRequired = true,
            runId,
            location,
            role = role.ToString(),
            stopReason = result.StopReason.ToString(),
            conditions = result.Conditions,
            steps = result.Steps.Select(step => new
            {
                status = step.Status.ToString(),
                requestId = step.RequestId,
                responseHash = step.Response?.ResponseHash,
                abortReason = step.AbortReason?.ToString(),
                requiresRefresh = step.RequiresRefresh,
                complete = step.IsComplete
            }).ToArray(),
            auditPath = KoLAdventureSessionFactory.DefaultAuditPath
        });
        return result.StopReason == BoundedAdventureRunStopReason.GatewayStopped ? 3 : 0;
    }

    private static async Task<int> CombatCommandAsync(string[] args)
    {
        if (args.Length == 0 || HasFlag(args, "--help") || IsHelp(args[0]))
        {
            PrintCombatHelp();
            return 0;
        }

        return args[0].ToLowerInvariant() switch
        {
            "preview" or "inspect" => await CombatPreviewAsync(args[1..], manual: false),
            "manual" or "step" => await CombatPreviewAsync(args[1..], manual: true),
            "auto" or "autocombat" => await AutoCombatCommandAsync(args[1..]),
            _ => Fail($"Unbekannter combat-Befehl: {args[0]}")
        };
    }

    private static Task<int> CombatPreviewAsync(string[] args, bool manual)
    {
        var location = ReadOptionalAdventureId(args) ?? "preview";
        var fixturePath = GetOption(args, "--fixture");
        var fixture = fixturePath is null
            ? null
            : new KoLCombatObservationParser().Parse(
                File.ReadAllText(fixturePath),
                Uri.TryCreate(GetOption(args, "--uri"), UriKind.Absolute, out var fixtureUri) ? fixtureUri : null);
        var round = Math.Max(1, ReadInt(args, "--round", fixture?.Round is > 0 ? fixture.Round : 1));
        var actionId = GetOption(args, "--action-id")
            ?? GetOption(args, "--action")
            ?? "attack";
        var kindOption = GetOption(args, "--kind");
        var selectedId = GetOption(args, "--skill") ?? GetOption(args, "--item");
        if (!string.IsNullOrWhiteSpace(selectedId)
            && !actionId.Contains(':', StringComparison.Ordinal))
            actionId = $"{kindOption ?? (GetOption(args, "--skill") is not null ? "skill" : "item")}:{selectedId}";
        var encounter = GetOption(args, "--encounter");
        var monster = GetOption(args, "--monster") ?? fixture?.MonsterName;
        var allowlist = new AdventureActionAllowlist()
            .AddAdventure(location)
            .AddFightAction(location, actionId, encounter, round, monster);
        var allowed = allowlist.TryGetFightAction(location, encounter, monster, round, out var action);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["action"] = actionId,
            ["round"] = round.ToString(CultureInfo.InvariantCulture)
        };
        if (!string.IsNullOrWhiteSpace(encounter)) fields["encounter"] = encounter;
        if (!string.IsNullOrWhiteSpace(monster)) fields["monster"] = monster;

        var combatId = GetOption(args, "--combat-id")
            ?? GetOption(args, "--id")
            ?? "preview";
        var expectedHash = fixture?.ResponseHash ?? PreviewHash(args);
        var selected = ParseManualCombatAction(actionId, kindOption, GetOption(args, "--quantity"));
        if (fixture is not null && fixture.AvailableActions.Count > 0)
            allowed &= fixture.AvailableActions.Any(candidate =>
                candidate.Kind switch
                {
                    CombatActionKind.Attack => selected.Kind == ManualCombatActionKind.Attack,
                    CombatActionKind.Skill => selected.Kind == ManualCombatActionKind.Skill
                        && (string.IsNullOrWhiteSpace(candidate.Id)
                            || string.Equals(candidate.Id, selected.Id, StringComparison.OrdinalIgnoreCase)),
                    CombatActionKind.Item => selected.Kind == ManualCombatActionKind.Item
                        && (string.IsNullOrWhiteSpace(candidate.Id)
                            || string.Equals(candidate.Id, selected.Id, StringComparison.OrdinalIgnoreCase)),
                    CombatActionKind.Run => selected.Kind == ManualCombatActionKind.Run,
                    _ => false
                });
        var previewAbortReasons = new List<ManualCombatAbortReason>();
        if (!allowed) previewAbortReasons.Add(ManualCombatAbortReason.ActionNotLegal);
        if (fixture is not null && fixture.IsTerminal)
            previewAbortReasons.Add(ManualCombatAbortReason.TerminalCombat);
        var observation = new ManualCombatObservation
        {
            CombatId = combatId,
            AdventureId = location,
            MonsterName = monster,
            MonsterId = fixture?.MonsterId,
            Round = round,
            PlayerHp = fixture?.PlayerHp ?? ReadInt(args, "--hp", 0),
            PlayerMaxHp = fixture?.PlayerMaxHp ?? ReadInt(args, "--max-hp", 0),
            PlayerMp = fixture?.PlayerMp ?? ReadInt(args, "--mp", 0),
            PlayerMaxMp = fixture?.PlayerMaxMp ?? ReadInt(args, "--max-mp", 0),
            EnemyHp = fixture?.EnemyHp ?? 0,
            EnemyMaxHp = fixture?.EnemyMaxHp ?? 0,
            EnemyMp = fixture?.EnemyMp ?? 0,
            EnemyMaxMp = fixture?.EnemyMaxMp ?? 0,
            ResponseHash = expectedHash,
            Outcome = fixture is null ? ManualCombatOutcome.Active : MapOutcome(fixture.Outcome),
            Message = fixture is null ? "Synthetic read-only combat observation." : null
        }.Normalize();
        var request = new ManualCombatRequest
        {
            CombatId = combatId,
            AdventureId = location,
            Action = selected,
            ExpectedRound = round,
            ExpectedResponseHash = expectedHash,
            AccountId = GetOption(args, "--account") ?? "default"
        }.Normalize();
        request.Validate();
        var preview = new ManualCombatPreview
        {
            OperationId = GetOption(args, "--operation-id") ?? $"preview-{Guid.NewGuid():N}",
            Request = request,
            Observation = observation,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            RequiresApproval = manual,
            AbortReasons = previewAbortReasons,
            Messages = new[] { "Preview only; no transport was invoked." }
        };

        WriteJson(new
        {
            command = manual ? "combat manual" : "combat preview",
            mode = manual ? "manual" : "preview",
            readOnly = true,
            previewOnly = true,
            mutating = false,
            post = false,
            executed = false,
            approvalRequired = manual,
            wouldPost = allowed && previewAbortReasons.Count == 0,
            action = allowed && previewAbortReasons.Count == 0 ? CombatActionDocument(action) : null,
            manualAction = ManualCombatActionDocument(selected),
            preview = ManualPreviewDocument(preview),
            observation = ManualObservationDocument(observation),
            requestFields = fields,
            allowlisted = allowed,
            location,
            encounter,
            monster,
            round,
            safety = "No fight.php request is sent by this command."
        });
        return Task.FromResult(previewAbortReasons.Count == 0 ? 0 : 3);
    }

    private static ManualCombatAction ParseManualCombatAction(
        string actionText,
        string? kindText,
        string? quantityText)
    {
        var quantity = int.TryParse(quantityText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Max(1, parsed)
            : 1;
        var value = actionText.Trim();
        var separator = value.IndexOf(':');
        var inferredKind = separator > 0 ? value[..separator] : value;
        var id = separator > 0
            ? value[(separator + 1)..]
            : kindText is "skill" or "cast" or "item" or "use" or "useitem"
                ? value
                : null;
        var kind = (kindText ?? inferredKind).Trim().ToLowerInvariant();
        return kind switch
        {
            "attack" or "hit" => ManualCombatAction.Attack(),
            "skill" or "cast" => ManualCombatAction.Skill(
                string.IsNullOrWhiteSpace(id) ? throw new ArgumentException("Skill action needs an id (use --skill ID).") : id),
            "item" or "use" or "useitem" => ManualCombatAction.Item(
                string.IsNullOrWhiteSpace(id) ? throw new ArgumentException("Item action needs an id (use --item ID).") : id, quantity),
            "run" or "flee" or "runaway" => ManualCombatAction.Run(),
            _ => ManualCombatAction.Attack()
        };
    }

    private static ManualCombatOutcome MapOutcome(CombatOutcome outcome)
        => outcome switch
        {
            CombatOutcome.Victory => ManualCombatOutcome.Won,
            CombatOutcome.Defeat => ManualCombatOutcome.Lost,
            CombatOutcome.Fled => ManualCombatOutcome.Fled,
            CombatOutcome.Ongoing => ManualCombatOutcome.Active,
            _ => ManualCombatOutcome.Unknown
        };

    private static async Task<int> AutoCombatCommandAsync(string[] args)
    {
        if (args.Length > 0 && (IsHelp(args[0]) || HasFlag(args, "--help")))
        {
            PrintCombatHelp();
            return 0;
        }

        if (args.Length > 0
            && args[0] is "preview" or "run" or "inspect" or "step")
            args = args[1..];

        var policy = BuildAutoCombatPolicy(args);
        var maxRounds = Math.Clamp(ReadInt(args, "--max-rounds", policy.Budget.MaxRounds), 1, 100);
        var context = new AutoCombatContext
        {
            Round = 1,
            Hp = Math.Max(0, ReadInt(args, "--hp", 100)),
            MaxHp = Math.Max(0, ReadInt(args, "--max-hp", 100)),
            Mp = Math.Max(0, ReadInt(args, "--mp", 0)),
            MaxMp = Math.Max(0, ReadInt(args, "--max-mp", 0)),
            Monster = GetOption(args, "--monster"),
            Effects = ParseCsv(GetOption(args, "--effects")),
            ActionsTaken = 0
        };
        var usage = new AutoCombatUsage();
        var recoveryPlan = ReadRecoveryPlan(args);
        var decisions = new List<object>();
        for (var round = 1; round <= maxRounds; round++)
        {
            if (recoveryPlan is not null && !recoveryPlan.CanEnterCombat)
            {
                decisions.Add(new
                {
                    round,
                    decision = (object?)null,
                    wouldPost = false,
                    stopReason = "RecoveryBlocked",
                    recovery = recoveryPlan
                });
                break;
            }
            var snapshot = context with
            {
                Round = round,
                ActionsTaken = usage.ActionsTaken,
                SkillUses = usage.SkillUses,
                ItemUses = usage.ItemUses,
                RunAttempts = usage.RunAttempts,
                ManualDecisions = usage.ManualDecisions,
                RuleUses = usage.RuleUses
            };
            var decision = policy.Evaluate(snapshot, usage);
            decisions.Add(new
            {
                round,
                decision,
                wouldPost = decision.IsAction && decision.Action.Kind is not AutoCombatActionKind.Manual and not AutoCombatActionKind.Abort
            });
            if (!decision.IsAction || decision.Action.Kind is AutoCombatActionKind.Run or AutoCombatActionKind.Abort)
                break;
            usage = usage with
            {
                ActionsTaken = usage.ActionsTaken + 1,
                SkillUses = usage.SkillUses + (decision.Action.Kind == AutoCombatActionKind.Skill ? 1 : 0),
                ItemUses = usage.ItemUses + (decision.Action.Kind == AutoCombatActionKind.Item ? 1 : 0),
                RunAttempts = usage.RunAttempts + (decision.Action.Kind == AutoCombatActionKind.Run ? 1 : 0),
                ManualDecisions = usage.ManualDecisions + (decision.Action.Kind == AutoCombatActionKind.Manual ? 1 : 0)
            };
        }

        WriteJson(new
        {
            command = "autocombat",
            mode = "dry-run",
            readOnly = true,
            previewOnly = true,
            mutating = false,
            post = false,
            executed = false,
            approvalRequired = true,
            location = ReadOptionalAdventureId(args),
            monster = context.Monster,
            maxRounds,
            policy,
            context,
            recovery = recoveryPlan,
            decisions,
            stopReason = decisions.Count == 0
                ? "No decision was evaluated."
                : recoveryPlan is not null && !recoveryPlan.CanEnterCombat
                    ? "RecoveryBlocked"
                    : null,
            safety = "Combat execution is disabled; no fight.php request is sent."
        });
        await Task.CompletedTask;
        return 0;
    }

    private static RecoveryPlan? ReadRecoveryPlan(string[] args)
    {
        var snapshotPath = GetOption(args, "--recovery-snapshot");
        if (snapshotPath is null)
            return null;
        if (!File.Exists(snapshotPath))
            throw new ArgumentException($"Recovery-Snapshot '{snapshotPath}' wurde nicht gefunden.");
        try
        {
            var snapshot = JsonSerializer.Deserialize<RecoverySnapshot>(
                File.ReadAllText(snapshotPath), JsonOptions)
                ?? throw new ArgumentException("Recovery-Snapshot ist leer.");
            var policyPath = GetOption(args, "--recovery-policy");
            var policy = policyPath is null
                ? new RecoveryPolicy()
                : JsonSerializer.Deserialize<RecoveryPolicy>(File.ReadAllText(policyPath), JsonOptions)
                    ?? throw new ArgumentException("Recovery-Policy ist leer.");
            return new RecoveryPlanner(policy).Plan(snapshot);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Recovery-Snapshot oder -Policy ist kein gültiges JSON.", exception);
        }
    }

    private static AutoCombatPolicy BuildAutoCombatPolicy(string[] args)
    {
        var policyText = GetOption(args, "--policy");
        var policyPath = GetOption(args, "--policy-file");
        if (policyPath is not null)
            policyText = File.ReadAllText(policyPath);
        if (!string.IsNullOrWhiteSpace(policyText))
            return policyText.TrimStart().StartsWith("{", StringComparison.Ordinal)
                ? AutoCombatPolicy.FromJson(policyText)
                : AutoCombatPolicy.Parse(policyText);

        var rules = new List<AutoCombatRule>();
        foreach (var skill in GetOptions(args, "--skill"))
            rules.Add(new AutoCombatRule
            {
                Id = $"skill:{skill}",
                When = new AutoCombatPredicate { MinMp = 1 },
                Action = AutoCombatAction.Skill(skill, "CLI skill priority")
            });
        foreach (var item in GetOptions(args, "--item"))
            rules.Add(new AutoCombatRule
            {
                Id = $"item:{item}",
                When = AutoCombatPredicate.Any,
                Action = AutoCombatAction.Item(item, "CLI item priority")
            });
        var fallback = HasFlag(args, "--manual-on-fallback")
            ? AutoCombatAction.Manual("Manual review requested by CLI.")
            : AutoCombatAction.Attack("Default bounded attack.");
        return new AutoCombatPolicy
        {
            Rules = rules,
            DefaultAction = fallback,
            Budget = new AutoCombatBudget
            {
                MaxRounds = Math.Clamp(ReadInt(args, "--max-rounds", 10), 1, 100),
                MaxActions = Math.Clamp(ReadInt(args, "--max-actions", 10), 1, 100),
                MaxSkillUses = Math.Max(0, ReadInt(args, "--max-skills", int.MaxValue)),
                MaxItemUses = Math.Max(0, ReadInt(args, "--max-items", int.MaxValue))
            }
        };
    }

    private static string ReadAdventureId(string[] args)
        => ReadOptionalAdventureId(args)
            ?? throw new ArgumentException("--location LOCATION is required.");

    private static string? ReadOptionalAdventureId(string[] args)
        => GetOption(args, "--location")
            ?? GetOption(args, "--adventure")
            ?? Positionals(args).FirstOrDefault();

    private static string? CheckpointPath(string[] args)
        => GetOption(args, "--checkpoint") ?? GetOption(args, "--state");

    private static AdventureCheckpoint ReadCheckpoint(string path)
    {
        if (!File.Exists(path))
            throw new ArgumentException($"Checkpoint-Datei '{path}' wurde nicht gefunden.");
        try
        {
            return JsonSerializer.Deserialize<AdventureCheckpoint>(File.ReadAllText(path), JsonOptions)
                ?? throw new ArgumentException($"Checkpoint-Datei '{path}' ist leer.");
        }
        catch (JsonException exception)
        {
            throw new ArgumentException($"Checkpoint-Datei '{path}' ist kein gültiges JSON.", exception);
        }
    }

    private static T[] ReadJsonArray<T>(string path, string propertyName)
    {
        using var document = ReadJsonDocument(path);
        var element = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement
            : document.RootElement.TryGetProperty(propertyName, out var nested) ? nested : default;
        if (element.ValueKind != JsonValueKind.Array)
            throw new ArgumentException($"{path} muss ein JSON-Array oder ein Objekt mit '{propertyName}' sein.");
        return element.Deserialize<T[]>(JsonOptions) ?? Array.Empty<T>();
    }

    private static T ReadJsonObject<T>(string path, string propertyName)
    {
        using var document = ReadJsonDocument(path);
        var element = document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty(propertyName, out var nested) ? nested : document.RootElement;
        return element.Deserialize<T>(JsonOptions)
            ?? throw new ArgumentException($"{path} enthält kein gültiges {typeof(T).Name}-Objekt.");
    }

    private static JsonDocument ReadJsonDocument(string path)
    {
        if (!File.Exists(path))
            throw new ArgumentException($"JSON-Datei '{path}' wurde nicht gefunden.");
        try { return JsonDocument.Parse(File.ReadAllText(path)); }
        catch (JsonException exception) { throw new ArgumentException($"JSON-Datei '{path}' ist ungültig.", exception); }
    }

    private static void SaveCheckpoint(string path, AdventureCheckpoint checkpoint)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(checkpoint, JsonOptions));
    }

    private static int ReadInt(string[] args, string name, int fallback)
        => int.TryParse(GetOption(args, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static IReadOnlyList<string> ParseCsv(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IReadOnlyList<string> Positionals(string[] args)
    {
        var values = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            var value = args[index];
            if (value.StartsWith("-", StringComparison.Ordinal))
            {
                if (index + 1 < args.Length && !args[index + 1].StartsWith("-", StringComparison.Ordinal)) index++;
                continue;
            }
            values.Add(value);
        }
        return values;
    }

    private static string PreviewHash(string[] args)
        => $"cli-preview:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join('\u001f', args)))).ToLowerInvariant()}";

    private static object DecisionDocument(AdventureKernelDecision decision)
        => new
        {
            phase = decision.State.Phase.ToString(),
            stateKind = decision.State.StateKind.ToString(),
            aborted = decision.IsAborted,
            complete = decision.IsComplete,
            requiresRefresh = decision.RequiresRefresh,
            abortReason = decision.AbortReason?.ToString(),
            action = decision.NextAction is null ? null : KernelActionDocument(decision.NextAction)
        };

    private static object KernelActionDocument(AdventureKernelAction action)
        => action switch
        {
            AdventureFightAction fight => new
            {
                kind = action.Kind.ToString(),
                adventureId = action.AdventureId,
                actionId = fight.ActionId,
                round = fight.Round,
                encounterId = fight.EncounterId,
                monsterId = fight.MonsterId
            },
            AdventureChoiceAction choice => new
            {
                kind = action.Kind.ToString(),
                adventureId = action.AdventureId,
                choiceId = choice.ChoiceId,
                optionId = choice.OptionId,
                encounterId = choice.EncounterId,
                extraFields = choice.ExtraFields
            },
            _ => new { kind = action.Kind.ToString(), adventureId = action.AdventureId }
        };

    private static object CombatActionDocument(AdventureFightAction action)
        => new
        {
            kind = "Fight",
            actionId = action.ActionId,
            round = action.Round,
            adventureId = action.AdventureId,
            encounterId = action.EncounterId,
            monsterId = action.MonsterId
        };

    private static object ManualCombatActionDocument(ManualCombatAction action)
        => new
        {
            kind = action.Kind.ToString(),
            actionId = action.ActionId,
            quantity = action.Quantity,
            type = action.Type.ToString()
        };

    private static object ManualObservationDocument(ManualCombatObservation observation)
        => new
        {
            combatId = observation.CombatId,
            adventureId = observation.AdventureId,
            monsterId = observation.MonsterId,
            monsterName = observation.MonsterName,
            round = observation.Round,
            playerHp = observation.PlayerHp,
            playerMaxHp = observation.PlayerMaxHp,
            playerMp = observation.PlayerMp,
            playerMaxMp = observation.PlayerMaxMp,
            enemyHp = observation.EnemyHp,
            enemyMaxHp = observation.EnemyMaxHp,
            outcome = observation.Outcome.ToString(),
            responseHash = observation.ResponseHash,
            effects = observation.Effects,
            availableActions = observation.AvailableActions.Select(ManualCombatActionDocument).ToArray()
        };

    private static object ManualPreviewDocument(ManualCombatPreview preview)
        => new
        {
            operationId = preview.OperationId,
            fingerprint = preview.Fingerprint,
            createdAt = preview.CreatedAt,
            expiresAt = preview.ExpiresAt,
            requiresApproval = preview.RequiresApproval,
            ready = preview.IsReady,
            request = new
            {
                preview.Request.CombatId,
                preview.Request.AdventureId,
                action = ManualCombatActionDocument(preview.Request.Action),
                preview.Request.ExpectedRound,
                preview.Request.ExpectedResponseHash,
                preview.Request.AccountId
            },
            abortReasons = preview.AbortReasons,
            messages = preview.Messages
        };

    private static object EncounterDocument(AdventureEncounterState encounter)
        => new
        {
            adventureId = encounter.AdventureId,
            kind = encounter.Kind.ToString(),
            location = encounter.Location,
            choiceId = encounter.ChoiceId,
            availableChoices = encounter.AvailableChoices,
            canContinue = encounter.CanContinue,
            fightRound = encounter.FightRound,
            responseHash = encounter.ResponseHash,
            retryCount = encounter.RetryCount,
            advisories = encounter.Advisories,
            extraFields = encounter.ExtraFields
        };

    private static void PrintAdventureHelp()
    {
        Console.WriteLine("  adventure locations [--location ID]                 read-only location catalog");
        Console.WriteLine("  adventure start --location ID [--role main] [--run-id ID] [--checkpoint PATH]");
        Console.WriteLine("  adventure inspect [--checkpoint PATH] [--location ID] [--fixture HTML]");
        Console.WriteLine("  adventure step --location ID [--action attack] [--round N] [--checkpoint PATH]");
        Console.WriteLine("  adventure bounded-preview --location ID [--conditions-file PATH] [--snapshot-file PATH]");
        Console.WriteLine("  adventure run --location ID [--execute --approval-token TOKEN] [--fight-action attack]");
        Console.WriteLine("  adventure stop --run-id ID [--checkpoint PATH] [--reason TEXT]");
        Console.WriteLine("  All adventure commands are previews; no adventure.php/fight.php/choice.php POST is sent.");
    }

    private static void PrintCombatHelp()
    {
        Console.WriteLine("  combat preview [--location ID] [--action attack] [--round N] [--monster NAME]");
        Console.WriteLine("  combat inspect [--location ID] [--round N]                     preview encounter metadata");
        Console.WriteLine("  combat manual [--location ID] --action ACTION [--round N]       one-step dry-run");
        Console.WriteLine("  autocombat --dry-run [--location ID] [--max-rounds N] [--skill ID] [--item ID] [--recovery-snapshot FILE]");
        Console.WriteLine("  autocombat --policy 'skill(123) -> attack' [--dry-run]");
        Console.WriteLine("  Combat commands are preview-only; no fight.php POST is sent.");
    }
}
