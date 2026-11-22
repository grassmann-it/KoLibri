using System.Text.Json;
using LibKolibri;
using KoLibri.Core.Spiel;

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
            "Execute (or preview) a bounded adventure run via the runner; live path requires execute=true and an approvalToken.",
            AdventureRunExecuteSchema(),
            AdventureRunExecuteAsync);
        Register("kol_choice_chain_execute",
            "Execute (or preview) a bounded choice chain against an existing checkpoint; live path requires execute=true and an approvalToken.",
            ChoiceChainExecuteSchema(),
            ChoiceChainExecuteAsync);

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
            ?? throw new McpInvalidParamsException(
                "Live adventure execution is not configured for this MCP host.");
        var role = RoleFor(arguments, _options.DefaultRole);
        var location = RequiredString(arguments, "locationId");
        var runId = OptionalString(arguments, "runId") ?? $"mcp-{Guid.NewGuid():N}";
        var execute = McpArgs.Execute(arguments);
        var approvalToken = ApprovalTokenFor(arguments);
        var maxSteps = Math.Clamp(NullableIntProperty(arguments, "maxGatewaySteps") ?? 20, 1, 10_000);
        var maxDurationSeconds = Math.Max(1, NullableIntProperty(arguments, "maxDurationSeconds") ?? 300);

        var allowlist = new AdventureActionAllowlist().AddAdventure(location);
        foreach (var fightAction in ParseCsv(arguments, "fightActions"))
            allowlist.AddFightAction(location, fightAction);
        foreach (var choiceAction in ParseCsv(arguments, "choiceActions"))
        {
            var separator = choiceAction.IndexOf(':');
            if (separator <= 0 || separator == choiceAction.Length - 1)
                throw new McpInvalidParamsException("choiceActions entries must be CHOICE_ID:OPTION_ID.");
            allowlist.AddChoiceOption(location, choiceAction[..separator], choiceAction[(separator + 1)..]);
        }

        var request = new AdventureRunRequest
        {
            RunId = runId,
            AdventureId = location,
            Role = role,
            DailyLimit = new AdventureDailyLimitContract
            {
                Day = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime),
                MaxTurns = Math.Max(0, NullableIntProperty(arguments, "maxTurns") ?? int.MaxValue),
                TurnsUsed = Math.Max(0, NullableIntProperty(arguments, "turnsUsed") ?? 0),
                MaxRuns = Math.Max(0, NullableIntProperty(arguments, "maxRuns") ?? int.MaxValue),
                RunsUsed = Math.Max(0, NullableIntProperty(arguments, "runsUsed") ?? 0)
            }
        };

        var conditionsElement = FindArray(arguments, "conditions");
        var conditions = conditionsElement is { } array
            ? array.Deserialize<AdventureCondition[]>(McpJson.Options) ?? Array.Empty<AdventureCondition>()
            : Array.Empty<AdventureCondition>();

        var snapshotElement = NestedObject(arguments, "snapshot");
        IAdventureConditionSnapshotProvider? snapshotProvider = null;
        if (snapshotElement is { } snapshotValue)
        {
            var snapshot = Deserialize<AdventureConditionSnapshot>(snapshotValue, "snapshot");
            snapshotProvider = new StaticAdventureConditionSnapshotProvider(snapshot with { IsStale = false });
        }
        else if (_roleRuntime.TryGet<IAccountReadClient>(role, out var account))
        {
            snapshotProvider = new AccountAdventureConditionSnapshotProvider(
                account,
                conditions,
                forceRefresh: BoolProperty(arguments, "forceRefresh"));
        }

        var result = await bridge.RunBoundedAsync(
            role,
            request,
            allowlist,
            conditions,
            snapshotProvider ?? new NullConditionSnapshotProvider(),
            new BoundedAdventureRunnerOptions
            {
                MaxGatewaySteps = maxSteps,
                MaxDuration = TimeSpan.FromSeconds(maxDurationSeconds)
            },
            execute,
            approvalToken,
            cancellationToken).ConfigureAwait(false);

        return new
        {
            executed = result.Executed,
            approvalRequired = true,
            runId,
            role = role.ToString(),
            location,
            stopReason = result.StopReason,
            requestId = result.RequestId,
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
            ?? throw new McpInvalidParamsException(
                "Live choice-chain execution is not configured for this MCP host.");
        var role = RoleFor(arguments, _options.DefaultRole);
        var runId = RequiredString(arguments, "runId");
        var execute = McpArgs.Execute(arguments);
        var approvalToken = ApprovalTokenFor(arguments);
        var maxChoices = Math.Clamp(NullableIntProperty(arguments, "maxChoices") ?? 10, 1, 1_000);
        var maxDurationSeconds = Math.Max(1, NullableIntProperty(arguments, "maxDurationSeconds") ?? 120);

        var location = OptionalString(arguments, "locationId");
        var allowlist = location is null
            ? new AdventureActionAllowlist()
            : new AdventureActionAllowlist().AddAdventure(location);
        foreach (var choiceAction in ParseCsv(arguments, "choiceActions"))
        {
            var separator = choiceAction.IndexOf(':');
            if (separator <= 0 || separator == choiceAction.Length - 1)
                throw new McpInvalidParamsException("choiceActions entries must be CHOICE_ID:OPTION_ID.");
            var choiceLocation = location ?? choiceAction[..separator];
            allowlist.AddChoiceOption(choiceLocation, choiceAction[..separator], choiceAction[(separator + 1)..]);
        }

        var result = await bridge.RunChoiceChainAsync(
            role,
            runId,
            allowlist,
            new BoundedChoiceChainOptions
            {
                MaxChoices = maxChoices,
                MaxDuration = TimeSpan.FromSeconds(maxDurationSeconds)
            },
            execute,
            approvalToken,
            cancellationToken).ConfigureAwait(false);

        return new
        {
            executed = result.Executed,
            approvalRequired = true,
            runId,
            role = role.ToString(),
            location,
            stopReason = result.StopReason,
            steps = result.Steps,
            auditPath = result.AuditPath,
            note = execute
                ? "One POST per choice; the runner stopped at the bounded limits."
                : "Preview only; no choice.php POST was sent."
        };
    }

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
            + "\"forceRefresh\":{\"type\":\"boolean\"}"
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

/// <summary>Returns a fixed snapshot without touching an account gateway.</summary>
internal sealed class StaticAdventureConditionSnapshotProvider : IAdventureConditionSnapshotProvider
{
    private readonly AdventureConditionSnapshot _snapshot;

    public StaticAdventureConditionSnapshotProvider(AdventureConditionSnapshot snapshot)
        => _snapshot = snapshot ?? new AdventureConditionSnapshot();

    public ValueTask<AdventureConditionSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_snapshot);
}

/// <summary>Default no-snapshot provider; treated by the runner as conditions-blocked.</summary>
internal sealed class NullConditionSnapshotProvider : IAdventureConditionSnapshotProvider
{
    public ValueTask<AdventureConditionSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new AdventureConditionSnapshot { IsStale = true, UnavailableReason = "No condition snapshot was supplied." });
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
