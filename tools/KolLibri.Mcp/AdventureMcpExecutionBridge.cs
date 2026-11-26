using KoLibri;

namespace KolLibri.Mcp;

/// <summary>An open choice that needs the user's decision (0.4: NeedsDecisionInfo).</summary>
public sealed record McpNeedsDecision(
    string RunId, string? AdventureId, string? ChoiceId, string? Title, IReadOnlyList<ChoiceOption> Options, string? Reason, string AccountRole);

/// <summary>What is open on the account: a choice, a fight, nothing, or unknown (0.4: OpenChoiceDetection).</summary>
public sealed record McpOpenChoice(string Kind, ChoiceEncounter? Choice, string? Reason = null)
{
    public bool IsOpen => Kind is "Choice" or "Fight";
    public bool IsUnavailable => Kind == "Unavailable";
    public string Message => Kind switch
    {
        "Unavailable" => $"The open choice could not be read: {Reason ?? "unknown error"}. Nothing is assumed to be free.",
        "Choice" => "A choice adventure is open. Continue there (or decide with `kolibri choice submit`).",
        "Fight" => "A fight is open. Continue there.",
        _ => "No choice or fight is open."
    };
}

public sealed record AdventureExecutionBridgeResult(
    bool Executed,
    string StopReason,
    IReadOnlyList<object> Steps,
    string? AuditPath,
    string? RequestId,
    McpNeedsDecision? NeedsDecision = null);

/// <summary>
/// Live adventure execution for the MCP tools, on KoLibri's Adventurer (0.5: replaces the gateway, checkpoint store and
/// bounded runners of KoLibri.Core). Previews send nothing; a live call needs an approval token (the gated HTTP host
/// supplies its own after the plan/execute step).
/// </summary>
public sealed class AdventureMcpExecutionBridge : IDisposable
{
    private readonly McpAccounts _accounts;

    public AdventureMcpExecutionBridge(McpAccounts accounts) => _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));

    public string AuditPath => "<memory>";

    /// <summary>One bounded adventure (fight with the first allowed action, choices by the resolver, stop conditions).</summary>
    public async ValueTask<AdventureExecutionBridgeResult> RunBoundedAsync(
        AccountRole role, string runId, string locationId, int snarfblat, CombatAction? fightAction, IChoiceResolver choices,
        IReadOnlyList<StopCondition> stopWhen, int maxSteps, TimeSpan maxDuration, bool execute, string? approvalToken,
        CancellationToken cancellationToken)
    {
        if (!execute) return new(false, "ReadyToRun", Array.Empty<object>(), AuditPath, null);
        RequireToken(approvalToken);
        await using var session = await _accounts.OpenAsync(role, cancellationToken).ConfigureAwait(false);
        var strategy = new FirstActionStrategy(fightAction);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(maxDuration);
        var result = await session.Adventure.RunAsync(new RunRequest(snarfblat, 1)
        {
            Combat = strategy,
            Choices = choices,
            StopWhen = stopWhen,
            MaxRoundsPerFight = Math.Max(1, maxSteps)
        }, deadline.Token).ConfigureAwait(false);
        var stopReason = result.StopReason switch
        {
            RunStopReason.TurnsDone => "Complete",
            RunStopReason.Condition => "ConditionsSatisfied",
            RunStopReason.NeedsChoice => "NeedsDecision",
            RunStopReason.NeedsCombat when strategy.HadNoAction => "GatewayStopped",
            RunStopReason.NeedsCombat => "StepLimitReached",
            RunStopReason.Cancelled => "DeadlineReached",
            _ => "GatewayStopped"
        };
        var needs = result.StopReason == RunStopReason.NeedsChoice && result.Open is ChoiceEncounter open
            ? new McpNeedsDecision(runId, locationId, open.ChoiceId.ToString(System.Globalization.CultureInfo.InvariantCulture), open.Title,
                open.Options, result.Detail, role.ToString())
            : null;
        return new(result.Encounters.Count > 0, stopReason, result.Encounters.Select(e => Step(e, result.Detail)).ToArray(), AuditPath,
            result.Encounters.Count > 0 ? runId : null, needs);
    }

    public async ValueTask<McpOpenChoice> DetectOpenChoiceAsync(AccountRole role, CancellationToken cancellationToken)
    {
        try
        {
            await using var session = await _accounts.OpenAsync(role, cancellationToken).ConfigureAwait(false);
            return Open(await session.Adventure.GetOpenEncounterAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (SessionExpiredException) { return new("Unavailable", null, "the session expired (login page)"); }
        catch (Exception e) when (e is KolibriException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new("Unavailable", null, e.Message);
        }
    }

    /// <summary>Answers the open choice(s) of a stopped run with the resolver, at most <paramref name="maxChoices"/>.</summary>
    public async ValueTask<AdventureExecutionBridgeResult> RunChoiceChainAsync(
        AccountRole role, string runId, IChoiceResolver choices, int maxChoices, TimeSpan maxDuration, bool execute,
        string? approvalToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (!execute) return new(false, "Complete", Array.Empty<object>(), AuditPath, null);
        RequireToken(approvalToken);
        await using var session = await _accounts.OpenAsync(role, cancellationToken).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(maxDuration);
        var steps = new List<object>();
        var stop = "ChoiceLimitReached";
        McpNeedsDecision? needs = null;
        try
        {
            for (var i = 0; i < maxChoices; i++)
            {
                var open = await session.Adventure.GetOpenEncounterAsync(deadline.Token).ConfigureAwait(false);
                if (open is not ChoiceEncounter choice) { stop = open is FightEncounter ? "FightOpen" : "Complete"; break; }
                var decision = choices.Resolve(new ChoiceContext(choice.Location, choice.ChoiceId, choice.Title, choice.Options));
                if (decision.Option is not { } option)
                {
                    stop = "NeedsDecision";
                    needs = new McpNeedsDecision(runId.Trim(), null, choice.ChoiceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        choice.Title, choice.Options, decision.Reason, role.ToString());
                    break;
                }
                var next = await session.Adventure.ChooseAsync(choice.ChoiceId, option, deadline.Token).ConfigureAwait(false);
                steps.Add(new { status = "Complete", choiceId = choice.ChoiceId, option, title = choice.Title, next = Kind(next) });
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { stop = "DeadlineReached"; }
        catch (KolibriException exception) when (exception is not SessionExpiredException)
        {
            stop = "GatewayStopped";
            steps.Add(new { status = "Aborted", abortReason = exception.Message });
        }
        return new(steps.Count > 0, stop, steps, AuditPath, steps.Count > 0 ? runId : null, needs);
    }

    public void Dispose() { }

    private static void RequireToken(string? approvalToken)
    {
        if (string.IsNullOrWhiteSpace(approvalToken))
            throw new McpInvalidParamsException("execute=true requires an approvalToken.");
    }

    private static McpOpenChoice Open(Encounter? encounter) => encounter switch
    {
        ChoiceEncounter c => new("Choice", c),
        FightEncounter => new("Fight", null),
        _ => new("None", null)
    };

    private static string Kind(Encounter encounter) => encounter switch
    {
        FightEncounter => "Fight",
        ChoiceEncounter => "Choice",
        NonCombatEncounter => "NonCombat",
        BlockedEncounter b => "Blocked:" + b.Reason,
        _ => encounter.GetType().Name
    };

    private static object Step(Encounter encounter, string? detail) => new
    {
        status = Kind(encounter),
        requestId = (string?)null,
        responseHash = (string?)null,
        abortReason = encounter is BlockedEncounter blocked ? blocked.Reason.ToString() : null,
        requiresRefresh = false,
        complete = encounter is NonCombatEncounter,
        monster = (encounter as FightEncounter)?.MonsterName,
        title = encounter.Title
    };

    /// <summary>0.4 allowlist rule: the first allowed fight action every round; no allowed action stops the run.</summary>
    private sealed class FirstActionStrategy(CombatAction? action) : ICombatStrategy
    {
        public bool HadNoAction { get; private set; }

        public CombatAction Next(CombatContext context)
        {
            if (action is not null) return action;
            HadNoAction = true;
            return CombatAction.Stop;
        }
    }
}
