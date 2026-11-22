using System.Security.Cryptography;
using System.Text;
using KoLibri.Core.Basis;
using KoLibri.Core.Spiel;

namespace LibKolibri;

/// <summary>Plays one manually chosen action at a time on an authenticated account.</summary>
public sealed class ManualAdventureSession : IAsyncDisposable
{
    private readonly KoLAdventureSession _session;
    private readonly AdventureManualDriver _driver;

    public ManualAdventureSession(KoLAdventureSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        // The session's approval hook makes the approval decision for each request.
        _driver = new AdventureManualDriver(session.Role, session.Gateway);
    }

    public AccountRole Role => _session.Role;
    public string AccountId => _session.AccountId;

    public static async Task<ManualAdventureSession> OpenAsync(
        AccountRole role,
        IAdventureApprovalHook approval,
        IAdventureVerificationHook verification,
        AccountSessionManager? accounts = null,
        CancellationToken cancellationToken = default)
    {
        var manager = accounts ?? AccountSessionManager.CreateDefault();
        var account = await manager.GetRequiredSessionAsync(role, cancellationToken).ConfigureAwait(false);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account.Profile.Id)));
        var store = new JsonFileAdventureRunCheckpointStore(
            Path.Combine(KoLAdventureSessionFactory.DefaultCheckpointDirectory, role.ToString(), key));
        var session = await KoLAdventureSessionFactory.CreateAsync(
            role, approval, verification, manager, checkpoints: store, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(session.AccountId, account.Profile.Id, StringComparison.Ordinal))
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("The account changed while opening the adventure session.");
        }
        return new ManualAdventureSession(session);
    }

    public ValueTask<ManualStepOutcome> StartAsync(
        AdventureRunRequest request,
        AdventureActionAllowlist allowlist,
        CancellationToken cancellationToken = default)
        => _driver.StartAsync(request, allowlist, cancellationToken);

    public ValueTask<ManualStepOutcome> ExecuteAsync(
        string runId,
        AdventureActionAllowlist allowlist,
        AdventureStepSpec step,
        CancellationToken cancellationToken = default)
        => _driver.ExecuteAsync(runId, allowlist, step, cancellationToken);

    public Task<AdventureExecutionResult> ResumeAsync(
        string runId,
        AdventureActionAllowlist allowlist,
        CancellationToken cancellationToken = default)
        => _session.Gateway.ResumeAsync(runId, allowlist, cancellationToken);

    public Task<AdventureExecutionResult> RefreshAsync(
        string runId,
        AdventureActionAllowlist allowlist,
        CancellationToken cancellationToken = default)
        => _session.Gateway.RefreshAsync(runId, allowlist, cancellationToken);

    public Task<BoundedAdventureRunResult> RunUntilChoiceAsync(
        AdventureRunRequest request, AdventureActionAllowlist allowlist,
        IReadOnlyList<AdventureCondition>? conditions = null,
        BoundedAdventureRunnerOptions? settings = null,
        CancellationToken cancellationToken = default)
    {
        var policy = new AdventureActionAllowlist(allowlist.Entries)
        {
            // Keep the configured resolver: it answers by rule or saved answer and pauses only without one.
            WaitForManualChoice = true,
            ChoiceResolver = allowlist.ChoiceResolver
        };
        var scopedRequest = request with { Role = Role };
        var provider = conditions is { Count: > 0 }
            ? new AccountAdventureConditionSnapshotProvider(_session.AccountReader, conditions) : null;
        return new BoundedAdventureRunner(_session.Gateway).RunAsync(
            scopedRequest, policy, conditions, provider, settings, cancellationToken);
    }

    /// <summary>Starts at most the supplied number of turns and stops at a manual choice.</summary>
    public async Task<ManualAdventureBatchResult> RunTurnsAsync(
        AdventureRunRequest template, AdventureActionAllowlist allowlist, int remainingTurns,
        IReadOnlyList<AdventureCondition>? conditions = null,
        BoundedAdventureRunnerOptions? settings = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(remainingTurns);
        BoundedAdventureRunResult? last = null;
        var started = 0;
        var drops = new List<QuestDrop>();
        while (started < remainingTurns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = template with { RunId = Guid.NewGuid().ToString("N") };
            last = await RunUntilChoiceAsync(request, allowlist, conditions, settings, cancellationToken)
                .ConfigureAwait(false);
            if (last.Steps.Count > 0) started++;
            foreach (var step in last.Steps)
                if (step.Response?.Html is { } html) drops.AddRange(DropTracker.ReadDrops(html));
            // A lost fight ends the batch: never start the next adventure after a defeat.
            if (last.Steps.Count > 0 && last.Steps[^1].Response?.Html is { } finalHtml
                && new KoLCombatObservationParser().Parse(finalHtml, new Uri("https://www.kingdomofloathing.com/fight.php"))
                    .Outcome == CombatOutcome.Defeat)
                break;
            if (last.StopReason != BoundedAdventureRunStopReason.Complete
                || last.Conditions.Count > 0 && last.Conditions.All(condition => condition.IsSatisfied))
                break;
        }
        return new ManualAdventureBatchResult(started, remainingTurns - started, last!) { Drops = drops };
    }

    public async Task<KoLLocationAccess> ReadLocationAccessAsync(
        int? snarfblat, CancellationToken cancellationToken = default)
    {
        if (!KoLQuestAccess.HasRule(snarfblat)) return new(null, "Local access requirements have not been cataloged.");
        var status = await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
        var reader = (KoLAccountReadClient)_session.AccountReader;
        if (KoLQuestAccess.IsBatHole(snarfblat))
        {
            var map = await reader.FetchBatHoleMapAsync(cancellationToken).ConfigureAwait(false);
            return KoLQuestAccess.EvaluateBatHole(snarfblat, status, map);
        }
        var quests = KoLQuestAccess.RequiresQuestLog(snarfblat)
            ? await reader.FetchQuestLogAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var inventory = KoLQuestAccess.RequiresInventory(snarfblat)
            ? await _session.AccountReader.FetchInventoryAsync(true, cancellationToken).ConfigureAwait(false)
            : null;
        return KoLQuestAccess.Evaluate(snarfblat, status, quests, inventory);
    }

    /// <summary>Read only: is a choice or fight open on this account?</summary>
    public Task<OpenChoiceDetection> DetectOpenChoiceAsync(CancellationToken cancellationToken = default)
        => _session.DetectOpenChoiceAsync(cancellationToken);

    /// <summary>Submits one option to the open choice (one POST); callers need the user's decision.</summary>
    public Task<OpenChoiceSubmitResult> SubmitOpenChoiceAsync(
        string choiceId, int option, CancellationToken cancellationToken = default)
        => _session.SubmitOpenChoiceAsync(choiceId, option, cancellationToken);

    public Task<KoLStatusSnapshot> ReadStatusAsync(CancellationToken cancellationToken = default)
        => _session.AccountReader.FetchStatusAsync(forceRefresh: true, cancellationToken: cancellationToken);

    public ValueTask DisposeAsync() => _session.DisposeAsync();
}

public sealed record ManualAdventureBatchResult(
    int TurnsStarted, int TurnsRemaining, BoundedAdventureRunResult LastRun)
{
    /// <summary>Everything the fight pages of this batch reported as acquired.</summary>
    public IReadOnlyList<QuestDrop> Drops { get; init; } = Array.Empty<QuestDrop>();
}
