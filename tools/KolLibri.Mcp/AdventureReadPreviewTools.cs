using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KolArbitrage.Core.Adventure;

namespace KolLibri.Mcp;

/// <summary>
/// Adventure/combat MCP additions.  The methods in this file are deliberately
/// preview-only: they consume snapshots or bounded policy documents and return
/// a decision/review object.  No method submits an adventure, fight, choice,
/// recovery or item request.
/// </summary>
public partial class KolibriMcpServer
{
    private static readonly Regex McpInputTag = new(
        "<input\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex McpOptionLink = new(
        "[?&]option=(\\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private void RegisterAdventurePreviewTools()
    {
        Register("kol_adventure_locations", "Read or filter the configured adventure location catalog; never starts an adventure.", AdventureLocationsSchema(), AdventureLocationsAsync);
        Register("kol_combat_inspect", "Inspect a redacted combat snapshot and legal actions; never submits a fight action.", CombatInspectSchema(), CombatInspectAsync);
        Register("kol_combat_decision", "Preview one bounded combat action against a snapshot; approval and commit remain separate.", CombatDecisionSchema(), CombatDecisionAsync);
        Register("kol_autocombat_preview", "Evaluate an ordered AutoCombat policy against one snapshot without executing it.", AutoCombatPreviewSchema(), AutoCombatPreviewAsync);
        Register("kol_autocombat_action_preview", "Bridge AutoCombat to one legal fight action without submitting it.", AutoCombatActionPreviewSchema(), AutoCombatActionPreviewAsync);
        Register("kol_recovery_plan", "Build a bounded HP/MP/effects/familiar/equipment recovery plan from snapshots only.", RecoveryPlanSchema(), RecoveryPlanAsync);
        Register("kol_choice_options", "Inspect choice id/options and preview an allowlisted option without posting it.", ChoiceOptionsSchema(), ChoiceOptionsAsync);
        Register("kol_adventure_bounded_preview", "Evaluate a bounded adventure run and its typed stop conditions without posting.", BoundedAdventurePreviewSchema(), BoundedAdventurePreviewAsync);

        // Command-shaped aliases make the additive surface convenient for
        // existing hosts while tools/list keeps the canonical kol_* names.
        Alias("adventure-locations", "kol_adventure_locations");
        Alias("adventure_locations", "kol_adventure_locations");
        Alias("locations", "kol_adventure_locations");
        Alias("combat-inspect", "kol_combat_inspect");
        Alias("combat_inspect", "kol_combat_inspect");
        Alias("combat-decision", "kol_combat_decision");
        Alias("combat_decision", "kol_combat_decision");
        Alias("combat-preview", "kol_combat_decision");
        Alias("autocombat-preview", "kol_autocombat_preview");
        Alias("autocombat_policy_preview", "kol_autocombat_preview");
        Alias("auto-combat-preview", "kol_autocombat_preview");
        Alias("autocombat-action-preview", "kol_autocombat_action_preview");
        Alias("autocombat_action_preview", "kol_autocombat_action_preview");
        Alias("recovery-plan", "kol_recovery_plan");
        Alias("recovery_plan", "kol_recovery_plan");
        Alias("choice-options", "kol_choice_options");
        Alias("choice_options", "kol_choice_options");
        Alias("choice-preview", "kol_choice_options");
        Alias("adventure-bounded-preview", "kol_adventure_bounded_preview");
        Alias("adventure_bounded_preview", "kol_adventure_bounded_preview");
    }

    private Task<object?> AdventureLocationsAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = NestedObject(arguments, "catalog")
            ?? NestedObject(arguments, "locations")
            ?? arguments;
        var requestedId = OptionalString(arguments, "locationId")
            ?? OptionalString(arguments, "id")
            ?? OptionalString(arguments, "name");
        var values = FindArray(source, "locations", "entries", "items", "catalog")
            ?? (source.ValueKind == JsonValueKind.Array ? source : (JsonElement?)null);
        var rows = values is { } array
            ? array.EnumerateArray().Select(ParseLocationPreview).ToArray()
            : ParseSingleLocation(source);

        if (!string.IsNullOrWhiteSpace(requestedId))
            rows = rows.Where(x => string.Equals(x.LocationId, requestedId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(x.Name, requestedId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(x.Zone, requestedId, StringComparison.OrdinalIgnoreCase)).ToArray();

        var stale = BoolProperty(source, "isStale") || BoolProperty(source, "stale");
        var selected = rows.Count == 1 ? rows[0] : null;
        return Task.FromResult<object?>(new
        {
            readOnly = true,
            execution = "catalog-read-only",
            fetchedAt = _clock.GetUtcNow(),
            isStale = stale,
            count = rows.Count,
            locations = rows,
            selectedLocation = selected
        });
    }

    private Task<object?> BoundedAdventurePreviewAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var conditionsElement = FindArray(arguments, "conditions", "stopWhenSatisfied");
        var conditions = conditionsElement is { } array
            ? array.Deserialize<AdventureCondition[]>(McpJson.Options) ?? Array.Empty<AdventureCondition>()
            : Array.Empty<AdventureCondition>();
        var snapshotElement = NestedObject(arguments, "snapshot");
        AdventureConditionSnapshot? snapshot = snapshotElement is { } snapshotValue
            ? Deserialize<AdventureConditionSnapshot>(snapshotValue, "snapshot")
            : null;
        var normalized = conditions.Select(condition => condition.Normalize()).ToArray();
        var results = snapshot is null
            ? normalized.Select(condition => new AdventureConditionResult
            {
                Condition = condition,
                IsSatisfied = false,
                Reason = "A fresh condition snapshot is required before an adventure run can start."
            }).ToArray()
            : AdventureConditionEvaluator.EvaluateAll(normalized, snapshot);
        var blocked = normalized.Length > 0 && snapshot is null;
        var satisfied = normalized.Length > 0 && !blocked && results.All(result => result.IsSatisfied);
        return Task.FromResult<object?>(new
        {
            readOnly = true,
            execution = "bounded-preview-only",
            mutation = "disabled",
            post = false,
            approvalRequired = true,
            location = OptionalString(arguments, "locationId") ?? OptionalString(arguments, "adventureId"),
            runId = OptionalString(arguments, "runId"),
            maxGatewaySteps = Math.Clamp(NullableIntProperty(arguments, "maxGatewaySteps") ?? 20, 1, 10_000),
            maxDurationSeconds = Math.Max(1, NullableIntProperty(arguments, "maxDurationSeconds") ?? 300),
            stopReason = blocked ? "ConditionsBlocked" : satisfied ? "ConditionsSatisfied" : "ReadyToRun",
            conditions = results,
            note = "Preview only; no adventure.php, fight.php or choice.php request was sent."
        });
    }

    private Task<object?> CombatInspectAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = NestedObject(arguments, "observation")
            ?? NestedObject(arguments, "combat")
            ?? arguments;
        var html = OptionalString(source, "html") ?? OptionalString(arguments, "html");
        var parsed = ParseCombatSnapshot(source, html);
        return Task.FromResult<object?>(new
        {
            readOnly = true,
            execution = "inspect-only",
            responseHash = parsed.ResponseHash,
            isStale = parsed.IsStale,
            canAct = parsed.CanAct,
            observation = parsed.Observation,
            legalActions = parsed.LegalActions,
            outcome = parsed.Outcome,
            advisories = parsed.Advisories
        });
    }

    private Task<object?> CombatDecisionAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = NestedObject(arguments, "observation")
            ?? NestedObject(arguments, "combat")
            ?? arguments;
        var html = OptionalString(source, "html") ?? OptionalString(arguments, "html");
        var parsed = ParseCombatSnapshot(source, html);
        var requested = ParseCombatAction(arguments, source);
        var expectedHash = OptionalString(arguments, "expectedResponseHash")
            ?? OptionalString(arguments, "responseHash");
        var expectedRound = NullableIntProperty(arguments, "expectedRound")
            ?? NullableIntProperty(arguments, "round");
        var checks = new List<string>();
        var allowed = true;
        if (parsed.IsStale)
        {
            allowed = false;
            checks.Add("Combat snapshot is stale; refresh before any action.");
        }
        if (parsed.Outcome is "death" or "dead" or "complete" or "victory")
        {
            allowed = false;
            checks.Add($"Combat outcome '{parsed.Outcome}' is terminal.");
        }
        if (!parsed.CanAct)
        {
            allowed = false;
            checks.Add("Combat observation is unknown or missing a round.");
        }
        if (!string.IsNullOrWhiteSpace(expectedHash)
            && !string.Equals(expectedHash, parsed.ResponseHash, StringComparison.OrdinalIgnoreCase))
        {
            allowed = false;
            checks.Add("Response hash does not match the inspected page.");
        }
        if (expectedRound is { } round && parsed.Round is { } observedRound && round != observedRound)
        {
            allowed = false;
            checks.Add($"Round does not match (expected {round}, observed {observedRound}).");
        }
        if (requested is null)
        {
            allowed = false;
            checks.Add("An explicit action is required for a one-step preview.");
        }
        else if (!ActionIsLegal(requested, parsed.LegalActions))
        {
            allowed = false;
            checks.Add("Requested action is not present in the legal action snapshot.");
        }

        return Task.FromResult<object?>(new
        {
            readOnly = true,
            commitSupported = false,
            requiresApproval = true,
            actionSubmitted = false,
            allowed,
            action = requested,
            round = parsed.Round,
            responseHash = parsed.ResponseHash,
            checks,
            reason = allowed ? "Action is eligible for a future approved one-step commit." : checks.FirstOrDefault()
        });
    }

    private Task<object?> AutoCombatPreviewAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var policyElement = NestedObject(arguments, "policy");
        var policyJson = policyElement is { } nested
            ? nested.GetRawText()
            : arguments.GetRawText();
        AutoCombatPolicy policy;
        try
        {
            policy = AutoCombatPolicy.FromJson(policyJson);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException or InvalidOperationException)
        {
            throw new McpInvalidParamsException($"Invalid autocombat policy: {exception.Message}");
        }

        var source = NestedObject(arguments, "context")
            ?? NestedObject(arguments, "observation")
            ?? arguments;
        AutoCombatContext context;
        try
        {
            context = ParseAutoCombatContext(source);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException)
        {
            throw new McpInvalidParamsException($"Invalid autocombat context: {exception.Message}");
        }
        var usage = ParseAutoCombatUsage(arguments);
        AutoCombatDecision decision;
        try
        {
            decision = policy.Evaluate(context, usage);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new McpInvalidParamsException($"Autocombat policy cannot be evaluated: {exception.Message}");
        }

        return Task.FromResult<object?>(new
        {
            readOnly = true,
            execution = "preview-only",
            mutation = "disabled",
            policy,
            context,
            decision,
            fallback = decision.IsManual ? "manual-review" : null
        });
    }

    private Task<object?> AutoCombatActionPreviewAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var policyElement = NestedObject(arguments, "policy")
            ?? throw new McpInvalidParamsException("policy is required.");
        var observationElement = NestedObject(arguments, "observation")
            ?? throw new McpInvalidParamsException("observation is required.");
        AutoCombatPolicy policy;
        CombatObservation observation;
        try
        {
            policy = AutoCombatPolicy.FromJson(policyElement.GetRawText());
            observation = ParseAutoCombatObservation(observationElement);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException or InvalidOperationException)
        {
            throw new McpInvalidParamsException($"Invalid AutoCombat policy or observation: {exception.Message}");
        }

        var adventureId = OptionalString(arguments, "adventureId")
            ?? OptionalString(arguments, "locationId")
            ?? throw new McpInvalidParamsException("adventureId or locationId is required.");
        RecoveryPlan? recoveryPlan = null;
        var recoverySnapshotElement = NestedObject(arguments, "recoverySnapshot");
        if (recoverySnapshotElement is { } recoverySnapshotValue)
        {
            try
            {
                var snapshot = Deserialize<RecoverySnapshot>(recoverySnapshotValue, "recoverySnapshot").Normalize();
                var recoveryPolicyElement = NestedObject(arguments, "recoveryPolicy");
                var recoveryPolicy = Deserialize<RecoveryPolicy>(
                    recoveryPolicyElement ?? McpJson.EmptyObject, "recoveryPolicy").Normalize();
                recoveryPlan = BuildRecoveryPreview(snapshot, recoveryPolicy, _clock.GetUtcNow());
            }
            catch (McpInvalidParamsException)
            {
                throw;
            }
            catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException or InvalidOperationException)
            {
                throw new McpInvalidParamsException($"Invalid recovery snapshot or policy: {exception.Message}");
            }
        }
        var decision = AutoCombatAdventureAdapter.Evaluate(
            policy,
            observation,
            adventureId,
            OptionalString(arguments, "encounterId"),
            recoveryPlan: recoveryPlan);
        return Task.FromResult<object?>(new
        {
            readOnly = true,
            execution = "preview-only",
            mutation = "disabled",
            actionSubmitted = false,
            allowed = decision.Allowed,
            policyDecision = decision.PolicyDecision,
            action = decision.Action,
            blockReason = decision.BlockReason,
            recoveryPlan,
            responseHash = observation.ResponseHash,
            note = "No fight.php request was sent."
        });
    }

    private static CombatObservation ParseAutoCombatObservation(JsonElement source)
    {
        var actions = new List<CombatAction>();
        var actionArray = FindArray(source, "availableActions", "legalActions", "actions");
        if (actionArray is { } actionValues)
        {
            foreach (var value in actionValues.EnumerateArray())
            {
                var kindText = OptionalString(value, "kind") ?? OptionalString(value, "type");
                if (!Enum.TryParse<CombatActionKind>(kindText, true, out var kind)) kind = CombatActionKind.Unknown;
                actions.Add(new CombatAction(kind,
                    OptionalString(value, "id") ?? OptionalString(value, "actionId") ?? OptionalString(value, "value"),
                    OptionalString(value, "name") ?? OptionalString(value, "label"),
                    NullableIntProperty(value, "mpCost") ?? NullableIntProperty(value, "manaCost"))
                { IsEnabled = BoolProperty(value, "isEnabled", BoolProperty(value, "enabled", true)) });
            }
        }
        var effects = new List<CombatEffect>();
        var effectArray = FindArray(source, "effects", "activeEffects");
        if (effectArray is { } effectValues)
        {
            foreach (var value in effectValues.EnumerateArray())
            {
                if (value.ValueKind == JsonValueKind.String)
                    effects.Add(new CombatEffect(value.GetString() ?? string.Empty));
                else
                    effects.Add(new CombatEffect(
                        OptionalString(value, "name") ?? OptionalString(value, "id") ?? string.Empty,
                        NullableIntProperty(value, "duration") ?? NullableIntProperty(value, "turnsRemaining"),
                        OptionalString(value, "id"),
                        OptionalString(value, "description")));
            }
        }
        var outcomeText = OptionalString(source, "outcome");
        _ = Enum.TryParse<CombatOutcome>(outcomeText, true, out var outcome);
        return new CombatObservation
        {
            Monster = OptionalString(source, "monster") ?? OptionalString(source, "monsterName"),
            MonsterId = OptionalString(source, "monsterId"),
            Round = NullableIntProperty(source, "round") ?? 0,
            PlayerHp = NullableIntProperty(source, "playerHp") ?? NullableIntProperty(source, "hp"),
            PlayerMaxHp = NullableIntProperty(source, "playerMaxHp") ?? NullableIntProperty(source, "maxHp"),
            PlayerMp = NullableIntProperty(source, "playerMp") ?? NullableIntProperty(source, "mp"),
            PlayerMaxMp = NullableIntProperty(source, "playerMaxMp") ?? NullableIntProperty(source, "maxMp"),
            EnemyHp = NullableIntProperty(source, "enemyHp"),
            EnemyMaxHp = NullableIntProperty(source, "enemyMaxHp"),
            EnemyMp = NullableIntProperty(source, "enemyMp"),
            EnemyMaxMp = NullableIntProperty(source, "enemyMaxMp"),
            AvailableActions = actions,
            Effects = effects,
            Outcome = outcome,
            ResponseHash = OptionalString(source, "responseHash") ?? string.Empty
        }.Normalize();
    }

    private Task<object?> RecoveryPlanAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshotElement = NestedObject(arguments, "snapshot") ?? arguments;
        var policyElement = NestedObject(arguments, "policy");
        RecoverySnapshot snapshot;
        RecoveryPolicy policy;
        try
        {
            snapshot = Deserialize<RecoverySnapshot>(snapshotElement, "snapshot").Normalize();
            policy = Deserialize<RecoveryPolicy>(policyElement ?? McpJson.EmptyObject, "policy").Normalize();
        }
        catch (McpInvalidParamsException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException or InvalidOperationException)
        {
            throw new McpInvalidParamsException($"Invalid recovery snapshot or policy: {exception.Message}");
        }

        var plan = BuildRecoveryPreview(snapshot, policy, _clock.GetUtcNow());
        return Task.FromResult<object?>(new
        {
            readOnly = true,
            execution = "preview-only",
            mutation = "disabled",
            snapshot,
            policy,
            plan
        });
    }

    private Task<object?> ChoiceOptionsAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = NestedObject(arguments, "choice")
            ?? NestedObject(arguments, "observation")
            ?? arguments;
        var html = OptionalString(source, "html") ?? OptionalString(arguments, "html");
        var choiceId = OptionalString(source, "choiceId")
            ?? OptionalString(source, "whichchoice");
        var options = ParseChoiceOptions(source, html, out var parsedChoiceId);
        choiceId ??= parsedChoiceId;
        var requested = OptionalString(arguments, "optionId")
            ?? OptionalString(arguments, "option")
            ?? OptionalString(source, "selectedOption");
        var allow = ParseStringArray(arguments, "allowedOptions")
            ?? ParseStringArray(source, "allowedOptions");
        var selected = requested;
        if (string.IsNullOrWhiteSpace(selected) && allow is { Count: > 0 })
            selected = options.FirstOrDefault(x => allow.Contains(x.Id, StringComparer.Ordinal))?.Id;
        var allowed = selected is not null
            && options.Any(x => string.Equals(x.Id, selected, StringComparison.Ordinal));
        if (allow is { Count: > 0 } && selected is not null)
            allowed &= allow.Contains(selected, StringComparer.Ordinal);
        var advisories = new List<string>();
        if (string.IsNullOrWhiteSpace(choiceId)) advisories.Add("Choice id is unknown; manual review required.");
        if (options.Count == 0) advisories.Add("No recognised options were found; manual review required.");
        if (selected is not null && !allowed) advisories.Add("Selected option is not available or is not allowlisted.");
        return Task.FromResult<object?>(new
        {
            readOnly = true,
            commitSupported = false,
            choiceId,
            options,
            availableOptions = options.Select(x => x.Id).ToArray(),
            selectedOption = selected,
            allowed,
            optionSubmitted = false,
            requiresApproval = selected is not null,
            advisories
        });
    }

    private static IReadOnlyList<McpLocationPreview> ParseSingleLocation(JsonElement source)
    {
        if (source.ValueKind != JsonValueKind.Object || !HasAny(source, "locationId", "id", "snarfblat", "zone", "name"))
            return Array.Empty<McpLocationPreview>();
        return new[] { ParseLocationPreview(source) };
    }

    private static McpLocationPreview ParseLocationPreview(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new McpInvalidParamsException("Each adventure location must be an object.");
        var id = OptionalString(element, "locationId")
            ?? OptionalString(element, "id")
            ?? OptionalString(element, "name")
            ?? OptionalString(element, "snarfblat")
            ?? throw new McpInvalidParamsException("Adventure location requires locationId or id.");
        var snarfblat = IntProperty(element, "snarfblat", IntProperty(element, "adventureId", 0));
        var requirements = ParseRequirementStrings(element);
        var encounters = FindArray(element, "encounters", "encounterMetadata") is { } encounterArray
            ? encounterArray.EnumerateArray().Select(x => x.Clone()).ToArray()
            : Array.Empty<JsonElement>();
        return new McpLocationPreview(
            id,
            OptionalString(element, "name") ?? id,
            OptionalString(element, "zone") ?? OptionalString(element, "area"),
            snarfblat,
            Math.Max(0, IntProperty(element, "turnCost", IntProperty(element, "turns", 1))),
            requirements,
            BoolProperty(element, "unlocked", BoolProperty(element, "isUnlocked", true)),
            encounters,
            BoolProperty(element, "isStale") || BoolProperty(element, "stale"));
    }

    private static IReadOnlyList<string> ParseRequirementStrings(JsonElement element)
    {
        var array = FindArray(element, "requirements", "requirementsList", "prerequisites");
        if (array is null) return Array.Empty<string>();
        return array.Value.EnumerateArray()
            .Select(x => x.ValueKind == JsonValueKind.String
                ? x.GetString()
                : OptionalString(x, "id") ?? OptionalString(x, "name") ?? x.GetRawText())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .ToArray();
    }

    private static McpCombatSnapshot ParseCombatSnapshot(JsonElement source, string? html)
    {
        var responseHash = OptionalString(source, "responseHash")
            ?? OptionalString(source, "hash");
        if (string.IsNullOrWhiteSpace(responseHash) && !string.IsNullOrEmpty(html))
            responseHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(html))).ToLowerInvariant();
        var round = NullableIntProperty(source, "round")
            ?? NullableIntProperty(source, "fightRound")
            ?? (html is null ? null : ExtractHtmlInt(html, "onturn", "round", "combatround"));
        var monster = OptionalString(source, "monster")
            ?? OptionalString(source, "monsterId")
            ?? OptionalString(source, "monsterName")
            ?? (html is null ? null : ExtractHtmlMonster(html));
        var hp = NullableIntProperty(source, "hp") ?? NullableIntProperty(source, "currentHp") ?? (html is null ? null : ExtractHtmlInt(html, "hp", "health"));
        var maxHp = NullableIntProperty(source, "maxHp") ?? NullableIntProperty(source, "maximumHp");
        var mp = NullableIntProperty(source, "mp") ?? NullableIntProperty(source, "currentMp") ?? (html is null ? null : ExtractHtmlInt(html, "mp", "mana"));
        var maxMp = NullableIntProperty(source, "maxMp") ?? NullableIntProperty(source, "maximumMp");
        var legal = ParseLegalActions(source, html);
        var outcome = OptionalString(source, "outcome")?.ToLowerInvariant()
            ?? InferCombatOutcome(html);
        var stale = BoolProperty(source, "isStale") || BoolProperty(source, "stale");
        var canAct = !stale && round is > 0 && outcome is not ("death" or "dead" or "complete" or "victory");
        var observation = new McpCombatObservation(
            monster,
            round,
            hp,
            maxHp,
            mp,
            maxMp,
            OptionalString(source, "adventureId"),
            OptionalString(source, "encounterId"),
            ParseStringArray(source, "effects") ?? Array.Empty<string>(),
            OptionalString(source, "familiarId") ?? OptionalString(source, "familiar"),
            outcome,
            responseHash);
        var advisories = new List<string>();
        if (round is null) advisories.Add("Round is missing; manual review required.");
        if (string.IsNullOrWhiteSpace(monster)) advisories.Add("Monster is unknown.");
        if (legal.Count == 0) advisories.Add("No legal actions were supplied by the snapshot.");
        return new McpCombatSnapshot(observation, legal, outcome, responseHash, stale, canAct, advisories);
    }

    private static IReadOnlyList<McpCombatAction> ParseLegalActions(JsonElement source, string? html)
    {
        var array = FindArray(source, "legalActions", "actions", "availableActions");
        var values = new List<McpCombatAction>();
        if (array is { } elements)
        {
            foreach (var element in elements.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String)
                    values.Add(ParseCombatActionText(element.GetString() ?? string.Empty));
                else if (element.ValueKind == JsonValueKind.Object)
                    values.Add(new McpCombatAction(
                        OptionalString(element, "kind") ?? OptionalString(element, "type") ?? OptionalString(element, "action") ?? "attack",
                        OptionalString(element, "id") ?? OptionalString(element, "actionId") ?? OptionalString(element, "skillId") ?? OptionalString(element, "itemId"),
                        OptionalString(element, "label") ?? OptionalString(element, "name"),
                        BoolProperty(element, "legal", true)));
            }
        }
        if (values.Count == 0 && !string.IsNullOrWhiteSpace(html))
        {
            foreach (Match match in Regex.Matches(html!, "(?:fight\\.php[^>]*?whichskill=(\\d+)|whichskill[=\\\"']+(\\d+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                var id = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                values.Add(new McpCombatAction("skill", id, null, true));
            }
            if (Regex.IsMatch(html!, "(?:name=[\\\"']?attack|>attack<)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                values.Add(new McpCombatAction("attack", null, "attack", true));
            if (Regex.IsMatch(html!, "(?:name=[\\\"']?runaway|>run<|flee)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                values.Add(new McpCombatAction("run", null, "run", true));
        }
        return values
            .GroupBy(x => $"{x.Kind}:{x.Id}", StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToArray();
    }

    private static McpCombatAction? ParseCombatAction(JsonElement arguments, JsonElement source)
    {
        var action = NestedObject(arguments, "action") ?? NestedObject(source, "action");
        if (action is { } objectAction)
            return new McpCombatAction(
                OptionalString(objectAction, "kind") ?? OptionalString(objectAction, "type") ?? "attack",
                OptionalString(objectAction, "id") ?? OptionalString(objectAction, "actionId") ?? OptionalString(objectAction, "skillId") ?? OptionalString(objectAction, "itemId"),
                OptionalString(objectAction, "label"), true);
        var text = OptionalString(arguments, "action")
            ?? OptionalString(arguments, "actionId")
            ?? OptionalString(arguments, "kind");
        return string.IsNullOrWhiteSpace(text) ? null : ParseCombatActionText(text!);
    }

    private static McpCombatAction ParseCombatActionText(string value)
    {
        var text = value.Trim();
        var separator = text.IndexOfAny(new[] { ':', '=', ' ' });
        if (separator < 0) return new McpCombatAction(text.ToLowerInvariant(), null, text, true);
        var kind = text[..separator].Trim().ToLowerInvariant();
        var id = text[(separator + 1)..].Trim();
        return new McpCombatAction(kind, string.IsNullOrWhiteSpace(id) ? null : id, text, true);
    }

    private static bool ActionIsLegal(McpCombatAction requested, IReadOnlyList<McpCombatAction> legal)
        => legal.Any(candidate => candidate.Legal
            && string.Equals(candidate.Kind, requested.Kind, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(candidate.Id)
                || string.Equals(candidate.Id, requested.Id, StringComparison.OrdinalIgnoreCase)));

    private static AutoCombatContext ParseAutoCombatContext(JsonElement source)
    {
        var effects = ParseStringArray(source, "effects") ?? ParseStringArray(source, "activeEffects") ?? Array.Empty<string>();
        return new AutoCombatContext
        {
            Round = IntProperty(source, "round", IntProperty(source, "fightRound", 0)),
            Hp = IntProperty(source, "hp", IntProperty(source, "currentHp", IntProperty(source, "playerHp", 0))),
            MaxHp = IntProperty(source, "maxHp", IntProperty(source, "maximumHp", IntProperty(source, "playerMaxHp", 0))),
            Mp = IntProperty(source, "mp", IntProperty(source, "currentMp", IntProperty(source, "playerMp", 0))),
            MaxMp = IntProperty(source, "maxMp", IntProperty(source, "maximumMp", IntProperty(source, "playerMaxMp", 0))),
            Monster = OptionalString(source, "monster") ?? OptionalString(source, "monsterId") ?? OptionalString(source, "monsterName"),
            Effects = effects,
            ActionsTaken = IntProperty(source, "actionsTaken", IntProperty(source, "actionCount", 0)),
            SkillUses = IntProperty(source, "skillUses", 0),
            ItemUses = IntProperty(source, "itemUses", 0),
            RunAttempts = IntProperty(source, "runAttempts", 0),
            ManualDecisions = IntProperty(source, "manualDecisions", 0)
        };
    }

    private static AutoCombatUsage? ParseAutoCombatUsage(JsonElement arguments)
    {
        var source = NestedObject(arguments, "usage");
        if (source is null) return null;
        return new AutoCombatUsage
        {
            ActionsTaken = IntProperty(source.Value, "actionsTaken", 0),
            SkillUses = IntProperty(source.Value, "skillUses", 0),
            ItemUses = IntProperty(source.Value, "itemUses", 0),
            RunAttempts = IntProperty(source.Value, "runAttempts", 0),
            ManualDecisions = IntProperty(source.Value, "manualDecisions", 0)
        };
    }

    private static RecoveryPlan BuildRecoveryPreview(RecoverySnapshot snapshot, RecoveryPolicy policy, DateTimeOffset now)
    {
        var steps = new List<RecoveryPlanStep>();
        var reasons = new List<RecoveryStopReason>();
        var advisories = new List<string>();
        var value = snapshot.Normalize();
        var settings = policy.Normalize();
        if (settings.RequireFreshSnapshot && value.IsStale)
        {
            reasons.Add(RecoveryStopReason.StaleSnapshot);
            advisories.Add("Snapshot is stale; refresh before entering combat.");
        }

        var hpTarget = Math.Max(settings.MinimumHp, (int)Math.Ceiling(value.MaximumHp * settings.MinimumHpPercent));
        var mpTarget = Math.Max(settings.MinimumMp, (int)Math.Ceiling(value.MaximumMp * settings.MinimumMpPercent));
        var hpNeed = Math.Max(0, hpTarget - value.CurrentHp);
        var mpNeed = Math.Max(0, mpTarget - value.CurrentMp);
        var hpMissing = ApplyRecoveryItems(settings.HpRecoveryItems, value.ItemQuantities, hpNeed, true, steps, advisories, settings);
        var mpMissing = ApplyRecoveryItems(settings.MpRecoveryItems, value.ItemQuantities, mpNeed, false, steps, advisories, settings);
        if (hpMissing > 0) reasons.Add(RecoveryStopReason.MissingHpRecovery);
        if (mpMissing > 0) reasons.Add(RecoveryStopReason.MissingMpRecovery);

        var activeEffects = new HashSet<string>(value.Effects ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var effect in settings.RequiredEffects ?? Array.Empty<string>())
        {
            if (!activeEffects.Contains(effect))
            {
                reasons.Add(RecoveryStopReason.MissingEffect);
                advisories.Add($"Required effect '{effect}' is missing.");
            }
        }
        foreach (var requirement in settings.EffectRequirements ?? Array.Empty<RecoveryEffectRequirement>())
        {
            var turns = value.EffectTurns.TryGetValue(requirement.EffectId, out var remaining) ? remaining : 0;
            if (turns < requirement.MinimumTurns)
            {
                reasons.Add(turns == 0 ? RecoveryStopReason.MissingEffect : RecoveryStopReason.EffectTurnsTooLow);
                advisories.Add($"Effect '{requirement.EffectId}' has {turns} turn(s); {requirement.MinimumTurns} required.");
            }
        }
        if (!string.IsNullOrWhiteSpace(settings.RequiredFamiliarId)
            && !string.Equals(value.FamiliarId, settings.RequiredFamiliarId, StringComparison.OrdinalIgnoreCase))
        {
            if (steps.Count >= settings.MaxSteps) reasons.Add(RecoveryStopReason.PlanStepLimit);
            else steps.Add(new RecoveryPlanStep
            {
                Sequence = steps.Count + 1,
                Action = RecoveryActionKind.SelectFamiliar,
                FamiliarId = settings.RequiredFamiliarId,
                Reason = $"Select familiar '{settings.RequiredFamiliarId}'."
            });
            advisories.Add($"Required familiar '{settings.RequiredFamiliarId}' is not equipped.");
        }
        foreach (var equipment in settings.RequiredEquipment)
        {
            if (value.Equipment.TryGetValue(equipment.Key, out var equipped)
                && string.Equals(equipped, equipment.Value, StringComparison.OrdinalIgnoreCase)) continue;
            if (steps.Count >= settings.MaxSteps) reasons.Add(RecoveryStopReason.PlanStepLimit);
            else steps.Add(new RecoveryPlanStep
            {
                Sequence = steps.Count + 1,
                Action = RecoveryActionKind.Equip,
                Slot = equipment.Key,
                ItemId = equipment.Value,
                Reason = $"Equip '{equipment.Value}' in {equipment.Key}."
            });
            advisories.Add($"Required equipment '{equipment.Value}' is not equipped in {equipment.Key}.");
        }

        var distinctReasons = reasons.Distinct().ToArray();
        var blocked = distinctReasons.Length > 0;
        if (blocked && settings.EmitStopStep && steps.Count < settings.MaxSteps)
            steps.Add(new RecoveryPlanStep
            {
                Sequence = steps.Count + 1,
                Action = RecoveryActionKind.Stop,
                Reason = "Stop before combat until recovery requirements are satisfied."
            });
        var status = blocked
            ? RecoveryPlanStatus.Blocked
            : steps.Count == 0 ? RecoveryPlanStatus.Ready : RecoveryPlanStatus.RecoveryRequired;
        return new RecoveryPlan
        {
            GeneratedAt = now,
            Status = status,
            CanEnterCombat = !blocked,
            ProjectedCanEnterCombat = !blocked,
            Steps = steps.Take(Math.Max(0, settings.MaxSteps)).ToArray(),
            StopReasons = distinctReasons,
            Advisories = advisories.ToArray()
        }.Normalize();
    }

    private static int ApplyRecoveryItems(
        IReadOnlyList<RecoveryConsumable> items,
        IReadOnlyDictionary<string, int> inventory,
        int need,
        bool hp,
        ICollection<RecoveryPlanStep> steps,
        ICollection<string> advisories,
        RecoveryPolicy policy)
    {
        if (need <= 0) return 0;
        var remaining = need;
        var used = 0;
        foreach (var item in (items ?? Array.Empty<RecoveryConsumable>())
            .Where(x => x.IsAllowed && (hp ? x.HpRestore : x.MpRestore) > 0)
            .OrderByDescending(x => hp ? x.HpRestore : x.MpRestore)
            .ThenBy(x => x.ItemId, StringComparer.OrdinalIgnoreCase))
        {
            var available = inventory.TryGetValue(item.ItemId, out var quantity)
                ? Math.Max(0, quantity)
                : Math.Max(0, item.AvailableQuantity);
            while (available-- > 0 && remaining > 0)
            {
                if (used >= policy.MaxRecoveryActions || steps.Count >= policy.MaxSteps)
                {
                    advisories.Add("Recovery action/step budget reached.");
                    return remaining;
                }
                var delta = hp ? item.HpRestore : item.MpRestore;
                steps.Add(new RecoveryPlanStep
                {
                    Sequence = steps.Count + 1,
                    Action = RecoveryActionKind.UseItem,
                    ItemId = item.ItemId,
                    Quantity = 1,
                    ExpectedHpDelta = hp ? delta : 0,
                    ExpectedMpDelta = hp ? 0 : delta,
                    Reason = $"Use {item.ItemId} for {(hp ? "HP" : "MP")} recovery."
                });
                remaining = Math.Max(0, remaining - delta);
                used++;
            }
        }
        return remaining;
    }

    private static IReadOnlyList<McpChoiceOption> ParseChoiceOptions(JsonElement source, string? html, out string? parsedChoiceId)
    {
        parsedChoiceId = OptionalString(source, "choiceId") ?? OptionalString(source, "whichchoice");
        var options = new List<McpChoiceOption>();
        var array = FindArray(source, "options", "availableOptions", "choices");
        if (array is { } values)
        {
            foreach (var value in values.EnumerateArray())
            {
                if (value.ValueKind == JsonValueKind.String)
                    options.Add(new McpChoiceOption(value.GetString() ?? string.Empty, null, true));
                else if (value.ValueKind == JsonValueKind.Object)
                    options.Add(new McpChoiceOption(
                        OptionalString(value, "id") ?? OptionalString(value, "optionId") ?? OptionalString(value, "option") ?? string.Empty,
                        OptionalString(value, "label") ?? OptionalString(value, "name"),
                        BoolProperty(value, "available", true)));
            }
        }
        if (!string.IsNullOrWhiteSpace(html))
        {
            foreach (Match tag in McpInputTag.Matches(html!))
            {
                var name = HtmlAttribute(tag.Value, "name");
                if (!string.Equals(name, "whichchoice", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, "option", StringComparison.OrdinalIgnoreCase)) continue;
                var value = HtmlAttribute(tag.Value, "value");
                if (string.Equals(name, "whichchoice", StringComparison.OrdinalIgnoreCase)) parsedChoiceId ??= value;
                else if (!string.IsNullOrWhiteSpace(value) && options.All(x => !string.Equals(x.Id, value, StringComparison.Ordinal)))
                    options.Add(new McpChoiceOption(value!, null, true));
            }
            foreach (Match match in McpOptionLink.Matches(html!))
                if (options.All(x => !string.Equals(x.Id, match.Groups[1].Value, StringComparison.Ordinal)))
                    options.Add(new McpChoiceOption(match.Groups[1].Value, null, true));
        }
        return options.Where(x => !string.IsNullOrWhiteSpace(x.Id)).DistinctBy(x => x.Id, StringComparer.Ordinal).ToArray();
    }

    private static string? HtmlAttribute(string tag, string name)
    {
        var match = Regex.Match(tag,
            $"\\b{Regex.Escape(name)}\\s*=\\s*(?:\"(?<d>[^\"]*)\"|'(?<s>[^']*)'|(?<b>[^\\s>]+))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success
            ? match.Groups["d"].Success ? match.Groups["d"].Value
            : match.Groups["s"].Success ? match.Groups["s"].Value
            : match.Groups["b"].Value
            : null;
    }

    private static int? ExtractHtmlInt(string html, params string[] names)
    {
        foreach (var name in names)
        {
            var pattern = $"(?:name|id|data-{Regex.Escape(name)})\\s*=\\s*[\\\"']?{Regex.Escape(name)}[\\\"']?[^>]*?value\\s*=\\s*[\\\"']?(\\d+)";
            var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return value;
            match = Regex.Match(html, $"\\b{Regex.Escape(name)}\\s*[=:]\\s*(\\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return value;
        }
        return null;
    }

    private static string? ExtractHtmlMonster(string html)
    {
        var match = Regex.Match(html, "(?:monster|encounter)[^>]{0,100}>([^<]{1,100})<", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups[1].Value).Trim() : null;
    }

    private static string? InferCombatOutcome(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        if (Regex.IsMatch(html, "(?:you are dead|have been defeated|combat death)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "death";
        if (Regex.IsMatch(html, "(?:combat is over|you win|victory|you acquire)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "victory";
        return null;
    }

    private static JsonElement? FindArray(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return element.ValueKind == JsonValueKind.Array ? element : null;
        foreach (var name in names)
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array) return value;
        return null;
    }

    private static JsonElement? NestedObject(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static bool HasAny(JsonElement element, params string[] names)
        => names.Any(name => element.TryGetProperty(name, out _));

    private static IReadOnlyList<string>? ParseStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.String) return new[] { value.GetString() ?? string.Empty };
        if (value.ValueKind != JsonValueKind.Array) return null;
        return value.EnumerateArray()
            .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : OptionalString(x, "id") ?? OptionalString(x, "name"))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .ToArray();
    }

    private sealed record McpLocationPreview(
        string LocationId,
        string Name,
        string? Zone,
        int Snarfblat,
        int TurnCost,
        IReadOnlyList<string> Requirements,
        bool Unlocked,
        IReadOnlyList<JsonElement> Encounters,
        bool IsStale);

    private sealed record McpCombatAction(string Kind, string? Id, string? Label, bool Legal);

    private sealed record McpCombatObservation(
        string? Monster,
        int? Round,
        int? Hp,
        int? MaxHp,
        int? Mp,
        int? MaxMp,
        string? AdventureId,
        string? EncounterId,
        IReadOnlyList<string> Effects,
        string? Familiar,
        string? Outcome,
        string? ResponseHash);

    private sealed record McpCombatSnapshot(
        McpCombatObservation Observation,
        IReadOnlyList<McpCombatAction> LegalActions,
        string? Outcome,
        string? ResponseHash,
        bool IsStale,
        bool CanAct,
        IReadOnlyList<string> Advisories)
    {
        public int? Round => Observation.Round;
    }

    private sealed record McpChoiceOption(string Id, string? Label, bool Available);

    private static JsonElement AdventureLocationsSchema()
        => Schema("{\"type\":\"object\",\"properties\":{\"role\":{\"enum\":[\"Main\",\"Mallbot\"]},\"locationId\":{\"type\":\"string\"},\"catalog\":{\"type\":\"object\"},\"locations\":{\"type\":\"array\",\"items\":{\"type\":\"object\"}},\"forceRefresh\":{\"type\":\"boolean\"}},\"additionalProperties\":true}");

    private static JsonElement CombatInspectSchema()
        => Schema("{\"type\":\"object\",\"properties\":{\"role\":{\"enum\":[\"Main\",\"Mallbot\"]},\"observation\":{\"type\":\"object\"},\"combat\":{\"type\":\"object\"},\"html\":{\"type\":\"string\"}},\"additionalProperties\":true}");

    private static JsonElement CombatDecisionSchema()
        => Schema("{\"type\":\"object\",\"required\":[\"action\"],\"properties\":{\"role\":{\"enum\":[\"Main\",\"Mallbot\"]},\"observation\":{\"type\":\"object\"},\"action\":{},\"expectedResponseHash\":{\"type\":\"string\"},\"expectedRound\":{\"type\":\"integer\"}},\"additionalProperties\":true}");

    private static JsonElement AutoCombatPreviewSchema()
        => Schema("{\"type\":\"object\",\"required\":[\"policy\"],\"properties\":{\"policy\":{},\"context\":{\"type\":\"object\"},\"observation\":{\"type\":\"object\"},\"usage\":{\"type\":\"object\"}},\"additionalProperties\":true}");

    private static JsonElement AutoCombatActionPreviewSchema()
        => Schema("{\"type\":\"object\",\"required\":[\"policy\",\"observation\"],\"properties\":{\"policy\":{\"type\":\"object\"},\"observation\":{\"type\":\"object\"},\"adventureId\":{\"type\":\"string\"},\"locationId\":{\"type\":\"string\"},\"encounterId\":{\"type\":\"string\"},\"recoverySnapshot\":{\"type\":\"object\"},\"recoveryPolicy\":{\"type\":\"object\"}},\"additionalProperties\":true}");

    private static JsonElement RecoveryPlanSchema()
        => Schema("{\"type\":\"object\",\"required\":[\"snapshot\"],\"properties\":{\"snapshot\":{\"type\":\"object\"},\"policy\":{\"type\":\"object\"},\"role\":{\"enum\":[\"Main\",\"Mallbot\"]}},\"additionalProperties\":true}");

    private static JsonElement ChoiceOptionsSchema()
        => Schema("{\"type\":\"object\",\"properties\":{\"choice\":{\"type\":\"object\"},\"observation\":{\"type\":\"object\"},\"choiceId\":{\"type\":\"string\"},\"optionId\":{\"type\":\"string\"},\"html\":{\"type\":\"string\"},\"allowedOptions\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}},\"additionalProperties\":true}");

    private static JsonElement BoundedAdventurePreviewSchema()
        => Schema("{\"type\":\"object\",\"properties\":{\"locationId\":{\"type\":\"string\"},\"adventureId\":{\"type\":\"string\"},\"runId\":{\"type\":\"string\"},\"conditions\":{\"type\":\"array\",\"items\":{\"type\":\"object\"}},\"snapshot\":{\"type\":\"object\"},\"maxGatewaySteps\":{\"type\":\"integer\"},\"maxDurationSeconds\":{\"type\":\"integer\"}},\"additionalProperties\":true}");
}
