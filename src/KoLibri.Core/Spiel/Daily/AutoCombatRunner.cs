using KoLibri.Core.Basis;
using System.Globalization;

namespace KoLibri.Core.Spiel;

/// <summary>
/// One round of an auto-combat run, as it happened.
/// </summary>
public sealed record AutoCombatRound
{
    public required int Round { get; init; }
    public required string Action { get; init; }
    public required string Kind { get; init; }
    public required string Outcome { get; init; }
    public required int PlayerHp { get; init; }
    public required int MonsterHp { get; init; }
    public string? BlockReason { get; init; }
}

/// <summary>What one account's auto-combat run did.</summary>
public sealed record AutoCombatRunResult
{
    public required string AccountId { get; init; }
    public required bool Started { get; init; }
    public required int Rounds { get; init; }
    public required string Outcome { get; init; }
    public required string Rationale { get; init; }
    public IReadOnlyList<AutoCombatRound> RoundLog { get; init; } = Array.Empty<AutoCombatRound>();
    public string? AuditPath { get; init; }
}

/// <summary>
/// Runs a whole fight with a deterministic policy.
///
/// The runner owns the loop that KoLmafia leaves to the player: read the
/// fight page, ask the policy what to do, submit that action, read the
/// next page. It stops on a terminal page, on the round budget, on the
/// abort rules or when the policy blocks.
///
/// Nothing here talks to the network — the caller supplies the gateway,
/// so the same runner serves the CLI, the MCP server and the desktop
/// window, and each account gets its own runner instance.
/// </summary>
public sealed class AutoCombatRunner
{
    private readonly ManualCombatGateway _gateway;

    public AutoCombatRunner(ManualCombatGateway gateway)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
    }

    /// <summary>Reads the current fight page.</summary>
    public Task<ManualCombatObservation?> InspectAsync(
        string combatId,
        CancellationToken cancellationToken = default)
        => _gateway.InspectObservationAsync(combatId, cancellationToken);

    /// <summary>
    /// Fights until the fight ends. The policy decides every action; the
    /// runner only enforces the budget and the abort rules.
    /// </summary>
    public async Task<AutoCombatRunResult> RunAsync(
        AutoCombatPolicy policy,
        string combatId,
        string? adventureId = null,
        string accountId = "default",
        int maxRounds = 20,
        int abortBelowHpPercent = 30,
        string? approvalToken = null,
        BridgeAuditLog? audit = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(combatId);

        var rounds = new List<AutoCombatRound>();
        var operationId = Guid.NewGuid().ToString("N");
        var usage = new AutoCombatUsage();

        for (var round = 0; round < maxRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = await _gateway
                .InspectObservationAsync(combatId, cancellationToken)
                .ConfigureAwait(false);
            if (observation is null)
            {
                return new AutoCombatRunResult
                {
                    AccountId = accountId,
                    Started = false,
                    Rounds = rounds.Count,
                    Outcome = "no-observation",
                    Rationale = "The fight page could not be read.",
                    RoundLog = rounds,
                    AuditPath = audit?.FilePath
                };
            }
            var value = observation.Normalize();
            if (value.IsTerminal)
            {
                return new AutoCombatRunResult
                {
                    AccountId = accountId,
                    Started = rounds.Count > 0,
                    Rounds = rounds.Count,
                    Outcome = "fight-ended",
                    Rationale = $"The fight ended after {rounds.Count} round(s).",
                    RoundLog = rounds,
                    AuditPath = audit?.FilePath
                };
            }

            // The abort rule is the operator's safety net: below the
            // threshold we stop instead of dying.
            if (value.PlayerMaxHp > 0 && value.PlayerHp > 0)
            {
                var hpPercent = value.PlayerHp * 100 / value.PlayerMaxHp;
                if (hpPercent < abortBelowHpPercent)
                {
                    return new AutoCombatRunResult
                    {
                        AccountId = accountId,
                        Started = rounds.Count > 0,
                        Rounds = rounds.Count,
                        Outcome = "aborted-low-hp",
                        Rationale = $"Stopped at {hpPercent} % HP — below the {abortBelowHpPercent} % threshold.",
                        RoundLog = rounds,
                        AuditPath = audit?.FilePath
                    };
                }
            }

            var policyObservation = ToPolicyObservation(value);
            var decision = AutoCombatAdventureAdapter.Evaluate(
                policy, policyObservation, adventureId ?? string.Empty, usage: usage);
            if (!decision.Allowed || decision.Action is null)
            {
                return new AutoCombatRunResult
                {
                    AccountId = accountId,
                    Started = rounds.Count > 0,
                    Rounds = rounds.Count,
                    Outcome = "policy-blocked",
                    Rationale = decision.BlockReason ?? "The policy blocked the fight.",
                    RoundLog = rounds,
                    AuditPath = audit?.FilePath
                };
            }

            var action = ToManualAction(decision.PolicyDecision.Action);
            var preview = await _gateway.PreviewAsync(
                new ManualCombatRequest
                {
                    CombatId = combatId,
                    AdventureId = adventureId,
                    Action = action,
                    ExpectedRound = value.Round,
                    ExpectedResponseHash = value.ResponseHash ?? string.Empty,
                    AccountId = accountId,
                    OperationId = operationId
                }, cancellationToken).ConfigureAwait(false);
            var execution = await _gateway
                .ExecuteAsync(preview, approvalToken, cancellationToken)
                .ConfigureAwait(false);

            var policyAction = decision.PolicyDecision.Action;
            rounds.Add(new AutoCombatRound
            {
                Round = value.Round,
                Action = policyAction.Id ?? policyAction.Kind.ToString(),
                Kind = policyAction.Kind.ToString(),
                Outcome = execution.Status.ToString(),
                PlayerHp = value.PlayerHp,
                MonsterHp = value.EnemyHp
            });

            if (audit is not null)
            {
                await audit.AppendAsync(new AuditEvent(
                    AuditEventPhase.Commit, operationId, "AutoCombatRunner",
                    requestId: $"combat-{combatId}-{value.Round}",
                    action: $"combat.round.{value.Round}",
                    status: execution.Status.ToString(),
                    message: $"{policyAction.Kind} {policyAction.Id}",
                    accountId: accountId), cancellationToken).ConfigureAwait(false);
            }
        }

        return new AutoCombatRunResult
        {
            AccountId = accountId,
            Started = rounds.Count > 0,
            Rounds = rounds.Count,
            Outcome = "round-budget-reached",
            Rationale = string.Format(
                CultureInfo.InvariantCulture,
                "The fight ran {0} round(s) without ending.", rounds.Count),
            RoundLog = rounds,
            AuditPath = audit?.FilePath
        };
    }

    /// <summary>Converts the policy's action into the gateway's action.</summary>
    private static ManualCombatAction ToManualAction(AutoCombatAction action)
        => action.Kind switch
        {
            AutoCombatActionKind.Skill => ManualCombatAction.Skill(action.Id ?? string.Empty),
            AutoCombatActionKind.Item => ManualCombatAction.Item(
                action.Id ?? string.Empty, Math.Max(1, action.Quantity)),
            AutoCombatActionKind.Run => ManualCombatAction.Run(),
            _ => ManualCombatAction.Attack()
        };

    /// <summary>
    /// Converts the gateway's observation into the shape the policy
    /// evaluates. The gateway keeps the redacted page data; the policy
    /// works on the abstract combat model.
    /// </summary>
    private static CombatObservation ToPolicyObservation(ManualCombatObservation value)
        => new()
        {
            Monster = value.MonsterId ?? value.MonsterName,
            MonsterName = value.MonsterName,
            MonsterId = value.MonsterId,
            MonsterDetails = new CombatMonster(
                value.MonsterName, value.MonsterId, value.EnemyHp, value.EnemyMaxHp,
                value.EnemyMp, value.EnemyMaxMp),
            Round = value.Round,
            PlayerHp = value.PlayerHp,
            PlayerMaxHp = value.PlayerMaxHp,
            PlayerMp = value.PlayerMp,
            PlayerMaxMp = value.PlayerMaxMp,
            EnemyHp = value.EnemyHp,
            EnemyMaxHp = value.EnemyMaxHp,
            EnemyMp = value.EnemyMp,
            EnemyMaxMp = value.EnemyMaxMp,
            AvailableActions = value.AvailableActions
                .Select(action => new CombatAction(
                    action.Kind switch
                    {
                        ManualCombatActionKind.Skill => CombatActionKind.Skill,
                        ManualCombatActionKind.Item => CombatActionKind.Item,
                        ManualCombatActionKind.Run => CombatActionKind.Run,
                        _ => CombatActionKind.Attack
                    },
                    action.Id, action.Id, null))
                .ToArray(),
            Effects = value.Effects
                .Select(effect => new CombatEffect(effect, null, null, null))
                .ToArray(),
            Familiar = value.Familiar,
            Outcome = value.Outcome switch
            {
                ManualCombatOutcome.Active => CombatOutcome.Ongoing,
                ManualCombatOutcome.Won => CombatOutcome.Won,
                ManualCombatOutcome.Lost => CombatOutcome.Lost,
                ManualCombatOutcome.Fled => CombatOutcome.Fled,
                ManualCombatOutcome.Aborted => CombatOutcome.Fled,
                _ => CombatOutcome.Unknown
            },
            ResponseHash = value.ResponseHash ?? string.Empty
        };
}
