using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;
using KoLibri;

namespace KolLibri.Mcp;

/// <summary>
/// Live-mutation MCP surface for the bounded adventure runners. Every entry
/// point defaults to preview; live execution requires <c>execute=true</c> and
/// an explicit <c>approvalToken</c> that matches the host-supplied one.
/// </summary>
public partial class KolibriMcpServer
{
    private void RegisterAdventureExecutionTools()
    {
        Register("kol_adventure_run_execute",
            "Execute (or preview) a bounded adventure run via the runner; live path needs execute=true; with rights, call once to get a planId (preview), then again with the same arguments plus planId to run (approvalToken only for an ungated host). If the result has needsDecision, ask the user which option to take; do not guess.",
            AdventureRunExecuteSchema(),
            AdventureRunExecuteAsync);
        Register("kol_choice_chain_execute",
            "Execute (or preview) a bounded choice chain against an existing checkpoint; live path needs execute=true; with rights, call once to get a planId (preview), then again with the same arguments plus planId to run (approvalToken only for an ungated host). If the result has needsDecision, ask the user which option to take; do not guess.",
            ChoiceChainExecuteSchema(),
            ChoiceChainExecuteAsync);

        Register("kol_choice_pending",
            "Read only: is a choice adventure (or fight) open on the account? Returns the choice id, title and visible options. If one is open, ask the user which option to take; never guess.",
            ChoicePendingSchema(),
            ChoicePendingAsync);
        Alias("choice-pending", "kol_choice_pending");
        Alias("choice_pending", "kol_choice_pending");

        Alias("adventure-run-execute", "kol_adventure_run_execute");
        Alias("adventure_run_execute", "kol_adventure_run_execute");
        Alias("adventure-run", "kol_adventure_run_execute");
        Alias("adventure_run", "kol_adventure_run_execute");
        Alias("choice-chain-execute", "kol_choice_chain_execute");
        Alias("choice_chain_execute", "kol_choice_chain_execute");
        Alias("choice-chain", "kol_choice_chain_execute");
        Alias("choice_chain", "kol_choice_chain_execute");
    }

    private async Task<object?> AdventureRunExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bridge = _executionBridge
            ?? throw new McpInvalidParamsException("Live adventure execution is not configured for this MCP host.");
        var role = RoleFor(arguments, _options.DefaultRole);
        var location = RequiredString(arguments, "locationId");
        var runId = OptionalString(arguments, "runId") ?? $"mcp-{Guid.NewGuid():N}";
        var execute = McpArgs.Execute(arguments);
        var approvalToken = ApprovalTokenFor(arguments);
        var maxSteps = Math.Clamp(NullableIntProperty(arguments, "maxGatewaySteps") ?? 20, 1, 10_000);
        var maxDurationSeconds = Math.Max(1, NullableIntProperty(arguments, "maxDurationSeconds") ?? 300);
        // Allowlist of 0.4: the first configured fight action is used; without one a fight stops the run.
        var fightActions = ParseCsv(arguments, "fightActions").Select(ParseFightAction).ToArray();
        var table = ParseChoiceTable(arguments);
        // A catalog id ("adventure=543:Name") must reach KoL as the numeric snarfblat, not as text.
        var snarfblat = NullableIntProperty(arguments, "snarfblat");
        if (snarfblat is null && TryGetSnarfblat(location, out var resolved)) snarfblat = resolved;
        var conditionsElement = FindArray(arguments, "conditions");
        var stopWhen = conditionsElement is { } array ? StopConditions(array) : Array.Empty<StopCondition>();
        if (execute && snarfblat is not > 0)
            throw new McpInvalidParamsException($"No location number (snarfblat) for '{location}'. Pass snarfblat. Nothing was sent.");
        var result = await bridge.RunBoundedAsync(
            role, runId, location, snarfblat ?? 0, fightActions.FirstOrDefault(), CreateChoiceResolver(table), stopWhen,
            maxSteps, TimeSpan.FromSeconds(maxDurationSeconds), execute, approvalToken, cancellationToken).ConfigureAwait(false);
        if (execute && result.Steps.Count == 0) BookUnits(0); // nothing was sent: nothing is used
        return new
        {
            executed = result.Executed,
            approvalRequired = true,
            runId,
            role = role.ToString(),
            location,
            stopReason = result.StopReason,
            requestId = result.RequestId,
            needsDecision = NeedsDecisionPayload(result.NeedsDecision),
            steps = result.Steps,
            auditPath = result.AuditPath,
            note = execute
                ? "One POST per step; the runner stopped at the bounded limits."
                : "Preview only; no adventure.php/fight.php/choice.php POST was sent."
        };
    }

    private async Task<object?> ChoiceChainExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bridge = _executionBridge
            ?? throw new McpInvalidParamsException("Live choice-chain execution is not configured for this MCP host.");
        var role = RoleFor(arguments, _options.DefaultRole);
        var runId = RequiredString(arguments, "runId");
        var execute = McpArgs.Execute(arguments);
        var approvalToken = ApprovalTokenFor(arguments);
        var maxChoices = Math.Clamp(NullableIntProperty(arguments, "maxChoices") ?? 10, 1, 1_000);
        var maxDurationSeconds = Math.Max(1, NullableIntProperty(arguments, "maxDurationSeconds") ?? 120);
        var location = OptionalString(arguments, "locationId");
        var result = await bridge.RunChoiceChainAsync(
            role, runId, CreateChoiceResolver(ParseChoiceTable(arguments)), maxChoices, TimeSpan.FromSeconds(maxDurationSeconds),
            execute, approvalToken, cancellationToken).ConfigureAwait(false);
        if (execute && result.Steps.Count == 0) BookUnits(0); // nothing was sent: nothing is used
        return new
        {
            executed = result.Executed,
            approvalRequired = true,
            runId,
            role = role.ToString(),
            location,
            stopReason = result.StopReason,
            needsDecision = NeedsDecisionPayload(result.NeedsDecision),
            steps = result.Steps,
            auditPath = result.AuditPath,
            note = execute
                ? "One POST per choice; the runner stopped at the bounded limits."
                : "Preview only; no choice.php POST was sent."
        };
    }

    private async Task<object?> ChoicePendingAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bridge = _executionBridge
            ?? throw new McpInvalidParamsException("Live account access is not configured for this MCP host.");
        var role = RoleFor(arguments, _options.DefaultRole);
        var detection = await bridge.DetectOpenChoiceAsync(role, cancellationToken).ConfigureAwait(false);
        if (detection.IsUnavailable)
            throw new McpInvalidParamsException(detection.Message); // "could not ask" is an error, never "free"
        return new
        {
            readOnly = true,
            role = role.ToString(),
            open = detection.IsOpen,
            kind = detection.Kind,
            message = detection.Message,
            choiceId = detection.Choice?.ChoiceId.ToString(CultureInfo.InvariantCulture),
            title = detection.Choice?.Title,
            options = detection.Choice?.Options.Select(o => new { number = o.Option, text = o.Text }).ToArray(),
            instruction = detection.Kind == "Choice" ? AskUserInstruction : null
        };
    }

    private static readonly Regex SnarfblatInId = new(@"(?:^|[=:])(\d+)(?:[:]|$)", RegexOptions.Compiled);

    /// <summary>"adventure=543:Name" → 543, "113" → 113 (as AdventureLocationIds in 0.4).</summary>
    internal static bool TryGetSnarfblat(string? locationId, out int snarfblat)
    {
        snarfblat = 0;
        if (string.IsNullOrWhiteSpace(locationId)) return false;
        var match = SnarfblatInId.Match(locationId.Trim());
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out snarfblat) && snarfblat > 0;
    }

    /// <summary>fightActions entries: attack, run, skill:ID, item:ID.</summary>
    internal static CombatAction ParseFightAction(string text)
    {
        var value = text.Trim().ToLowerInvariant();
        if (value is "attack") return CombatAction.Attack;
        if (value is "run" or "runaway") return CombatAction.RunAway;
        if (value.StartsWith("skill:", StringComparison.Ordinal) && int.TryParse(value[6..], out var skill) && skill > 0) return CombatAction.Skill(skill);
        if (value.StartsWith("item:", StringComparison.Ordinal) && int.TryParse(value[5..], out var item) && item > 0) return CombatAction.Item(item);
        throw new McpInvalidParamsException($"fightActions entry '{text}' is not one of attack, run, skill:ID, item:ID.");
    }

    private static Dictionary<int, int> ParseChoiceTable(JsonElement arguments)
    {
        var table = new Dictionary<int, int>();
        foreach (var choiceAction in ParseCsv(arguments, "choiceActions"))
        {
            var separator = choiceAction.IndexOf(':');
            if (separator <= 0 || separator == choiceAction.Length - 1
                || !int.TryParse(choiceAction[..separator], out var choiceId) || !int.TryParse(choiceAction[(separator + 1)..], out var optionId))
                throw new McpInvalidParamsException("choiceActions entries must be CHOICE_ID:OPTION_ID.");
            table[choiceId] = optionId;
        }
        return table;
    }

    /// <summary>The choice policy file of 0.4 (defaults, ask|auto-safe) plus the call's choiceActions.</summary>
    private static IChoiceResolver CreateChoiceResolver(IReadOnlyDictionary<int, int> extra)
    {
        var config = new ChoiceResolutionConfigStore(Environment.GetEnvironmentVariable("KOLIBRI_CHOICE_POLICY")).Load();
        var table = new Dictionary<int, int>();
        foreach (var (id, entry) in config.Choices)
            if (int.TryParse(id, out var choiceId)) table[choiceId] = entry.Option;
        foreach (var (id, option) in extra) table[id] = option;
        return config.Mode == ChoiceResolutionMode.AutoSafe ? ChoiceResolvers.Safe(table) : ChoiceResolvers.FromTable(table);
    }

    /// <summary>0.4 conditions as stop conditions; unsupported kinds are refused before anything is sent.</summary>
    private static IReadOnlyList<StopCondition> StopConditions(JsonElement conditions)
    {
        var result = new List<StopCondition>();
        foreach (var c in conditions.EnumerateArray())
        {
            string Text(string name)
            {
                if (c.ValueKind != JsonValueKind.Object) return "";
                foreach (var p in c.EnumerateObject())
                    if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                        return p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetRawText() : p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : "";
                return "";
            }
            var kind = Text("kind").ToLowerInvariant();
            var op = Text("operator").ToLowerInvariant();
            var target = int.TryParse(Text("target"), out var t) ? Math.Max(0, t) : 0;
            if (kind is "itemcount" && op is "" or "atleast" && int.TryParse(Text("key"), out var item) && item > 0)
                result.Add(StopCondition.ItemCountAtLeast(item, target));
            else if (kind is "adventuresremaining" or "turnsremaining" && op is "atmost")
                result.Add(StopCondition.AdventuresAtMost(target));
            else
                throw new McpInvalidParamsException($"Condition {c.GetRawText()} is not supported in KoLibri 0.5 (supported: ItemCount AtLeast with an item id, AdventuresRemaining AtMost). Nothing was sent.");
        }
        return result;
    }

    internal const string AskUserInstruction =
        "A choice adventure is open and needs the user's decision. Show the options to the user and ask which one to take; do not guess. "
        + "After the user decided, the choice is submitted with the CLI (kolibri choice submit) or by resuming the run.";

    private static object? NeedsDecisionPayload(McpNeedsDecision? info)
        => info is null ? null : new
        {
            status = "NeedsDecision",
            runId = info.RunId,
            role = info.AccountRole,
            adventureId = info.AdventureId,
            choiceId = info.ChoiceId,
            title = info.Title,
            options = info.Options.Select(o => new { number = o.Option, text = o.Text }).ToArray(),
            reason = info.Reason,
            instruction = AskUserInstruction
        };

    private static JsonElement ChoicePendingSchema()
        => Schema("{\"type\":\"object\",\"properties\":{\"role\":{\"enum\":[\"Main\",\"Mallbot\"]}},\"additionalProperties\":false}");

    private static IReadOnlyList<string> ParseCsv(JsonElement element, string property)
    {
        var text = OptionalString(element, property);
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        return text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static JsonElement AdventureRunExecuteSchema()
        => Schema("{\"type\":\"object\",\"properties\":{"
            + "\"role\":{\"enum\":[\"Main\",\"Mallbot\"]},"
            + "\"locationId\":{\"type\":\"string\"},"
            + "\"runId\":{\"type\":\"string\"},"
            + "\"execute\":{\"type\":\"boolean\",\"default\":false},"
            + "\"approvalToken\":{\"type\":\"string\"},"
            + "\"planId\":{\"type\":\"string\"},"
            + "\"maxGatewaySteps\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":10000},"
            + "\"maxDurationSeconds\":{\"type\":\"integer\",\"minimum\":1},"
            + "\"maxTurns\":{\"type\":\"integer\"},"
            + "\"turnsUsed\":{\"type\":\"integer\"},"
            + "\"maxRuns\":{\"type\":\"integer\"},"
            + "\"runsUsed\":{\"type\":\"integer\"},"
            + "\"fightActions\":{\"type\":\"string\"},"
            + "\"choiceActions\":{\"type\":\"string\"},"
            + "\"conditions\":{\"type\":\"array\",\"items\":{\"type\":\"object\"}},"
            + "\"snapshot\":{\"type\":\"object\"},"
            + "\"forceRefresh\":{\"type\":\"boolean\"},"
            + "\"snarfblat\":{\"type\":\"integer\",\"minimum\":1}"
            + "},\"required\":[\"locationId\"],\"additionalProperties\":true}");

    private static JsonElement ChoiceChainExecuteSchema()
        => Schema("{\"type\":\"object\",\"properties\":{"
            + "\"role\":{\"enum\":[\"Main\",\"Mallbot\"]},"
            + "\"runId\":{\"type\":\"string\"},"
            + "\"locationId\":{\"type\":\"string\"},"
            + "\"choiceActions\":{\"type\":\"string\"},"
            + "\"maxChoices\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000},"
            + "\"maxDurationSeconds\":{\"type\":\"integer\",\"minimum\":1},"
            + "\"execute\":{\"type\":\"boolean\",\"default\":false},"
            + "\"approvalToken\":{\"type\":\"string\"},"
            + "\"planId\":{\"type\":\"string\"}"
            + "},\"required\":[\"runId\"],\"additionalProperties\":true}");
}

public partial class KolibriMcpServer
{
    /// <summary>
    /// Under an access gate (HTTP) the grant is the approval, so the client need not invent a token;
    /// without a gate the caller must still pass the host's approval token.
    /// </summary>
    private string? ApprovalTokenFor(JsonElement arguments)
        => OptionalString(arguments, "approvalToken") ?? (Authorizer is null ? null : "kolibri-mcp-gated");
}
