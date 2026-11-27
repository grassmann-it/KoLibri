using Avalonia.Controls;
using Avalonia.Interactivity;

#pragma warning disable CA1822 // mark members static for testability where possible

namespace KoLibri.Desktop;

/// <summary>
/// Glue between the Avalonia MainWindow and the EncounterBrowser +
/// EncounterBrowserSession view-models. Lives outside MainWindow.axaml.cs
/// so that file stays readable while the browser UI grows.
/// </summary>
public sealed class EncounterBrowserPane : IDisposable
{
    private readonly Func<AccountRole, CancellationToken, Task<KolibriSession?>> _openSession;
    private AccountRole? _selectedAccount;
    private (string ProfileId, int Count)? _observedAscension;
    private CancellationTokenSource? _operationCancellation;
    private readonly Action<string> _setStatus;
    private ApprovalTokenStore? _tokenStore;
    private bool _disposed;

    public EncounterBrowserPane(DesktopAccounts accounts, Action<string> setStatus)
        : this(OpenerOf(accounts), setStatus)
    {
    }

    /// <param name="openSession">The session of a role, null when it is signed out (see <see cref="DesktopAccounts.TryOpenAsync"/>).</param>
    public EncounterBrowserPane(Func<AccountRole, CancellationToken, Task<KolibriSession?>> openSession, Action<string> setStatus)
    {
        _openSession = openSession ?? throw new ArgumentNullException(nameof(openSession));
        _setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
    }

    private static Func<AccountRole, CancellationToken, Task<KolibriSession?>> OpenerOf(DesktopAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        return accounts.TryOpenAsync;
    }

    public void SelectAccount(AccountRole role)
    {
        if (_tokenStore is null) return;
        if (_selectedAccount != role) _observedAscension = null;
        _selectedAccount = role;
        _operationCancellation?.Cancel();
        _operationCancellation = null; // the cancelled operation must not block the new account
        Session = new EncounterBrowserSessionViewModel(_openSession, _tokenStore, role);
        Browser?.ShowEncounter(null);
    }

    public bool ObserveAscension(AccountRole role, string profileId, int? count)
    {
        if (_selectedAccount != role || count is not { } ascensions) return false;
        var changed = _observedAscension is { } previous
            && (previous.ProfileId != profileId || previous.Count != ascensions);
        _observedAscension = (profileId, ascensions);
        if (changed) SelectAccount(role);
        return changed;
    }

    public EncounterBrowserViewModel? Browser { get; private set; }
    public EncounterBrowserSessionViewModel? Session { get; private set; }

    public void Initialise(string catalogDirectory, string? tokenDirectory = null)
    {
        var catalog = new MafiaDataLoader().Load(catalogDirectory);
        Browser = new EncounterBrowserViewModel(catalog);
        _tokenStore = new ApprovalTokenStore(tokenDirectory ?? catalogDirectory, "manual-approval-token.json");
    }

    public async ValueTask LoadStoredTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_tokenStore is null || Session is null) return;
        await Session.LoadStoredTokenAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask PersistTokenAsync(string label, CancellationToken cancellationToken = default)
    {
        if (Session is null) return;
        await Session.PersistTokenAsync(label, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Hands a freshly-supplied approval token to the session (the gate before every start, step and batch).</summary>
    public void ApplyApprovalToken(string token)
    {
        if (Session is null) return;
        Session.ApprovalToken = token;
    }

    public ValueTask StartAdventureAsync(EncounterBrowserLocationRow row, CancellationToken cancellationToken = default)
        => RunAsync((session, browser, token) =>
        {
            if (row.Snarfblat <= 0)
                throw new InvalidOperationException("This location requires a special route.");
            var location = new AdventureLocation(row.LocationId, row.DisplayName, row.Zone, row.Snarfblat, row.TurnCost);
            return session.StartAdventureAsync(location, browser, token);
        }, cancellationToken);

    public ValueTask SubmitStepAsync(EncounterBrowserActionRow row, CancellationToken cancellationToken = default)
        => RunAsync((session, browser, token) =>
            session.SubmitStepAsync(browser.BuildStepSpec(row), browser, token), cancellationToken);

    public ValueTask RunTurnsAsync(
        EncounterBrowserLocationRow? row, int maxTurns = 0, string? itemId = null,
        CancellationToken cancellationToken = default, int maxFightRounds = 0, int minimumHpPercent = 0,
        string? combatRules = null)
        => RunAsync((session, browser, token) =>
        {
            var location = row is null ? null : new AdventureLocation(row.LocationId, row.DisplayName,
                row.Zone, row.Snarfblat, row.TurnCost);
            return session.RunTurnsAsync(browser, location, maxTurns, itemId, token, maxFightRounds, minimumHpPercent,
                combatRules);
        }, cancellationToken);

    /// <summary>Runs (or, with a null row, continues) the batch described by <paramref name="job"/>.</summary>
    public ValueTask RunJobAsync(EncounterBrowserLocationRow? row, AdventureJob job, CancellationToken cancellationToken = default)
        => RunTurnsAsync(row, job.Turns, job.ItemId, cancellationToken, job.MaxFightRounds, job.MinimumHpPercent,
            job.CombatRules);

    /// <summary>Fights the open fight with the plan (<paramref name="rules"/>: AutoCombatPolicy rule text).</summary>
    public ValueTask FightCurrentAsync(CombatTarget target, string rules, int maxRounds, int minimumHpPercent)
        => RunAsync((session, browser, token) => session.FightCurrentAsync(browser, target, rules, maxRounds, minimumHpPercent, token));

    public ValueTask RecheckAsync(CancellationToken cancellationToken = default)
        => RunAsync((session, browser, token) => session.RecheckAsync(browser, token), cancellationToken);

    private async ValueTask RunAsync(
        Func<EncounterBrowserSessionViewModel, EncounterBrowserViewModel, CancellationToken, ValueTask<AdventureStepOutcome?>> operation,
        CancellationToken cancellationToken = default)
    {
        if (_disposed || _operationCancellation is not null
            || Session is not { } session || Browser is not { } browser) return;
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _operationCancellation = pending;
        try
        {
            await operation(session, browser, pending.Token);
            if (ReferenceEquals(Session, session))
                _setStatus(session.LastMessage ?? "No adventure result.");
            else
                browser.ShowEncounter(null);
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested || _disposed)
        {
            // "End run", an account switch or shutdown: expected, never an unhandled exception.
            if (ReferenceEquals(Session, session)) _setStatus("The adventure run was canceled; nothing more was sent.");
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(Session, session))
            {
                _setStatus(AdventureFailureMapper.Describe(exception).Text);
                // A redirect to choice.php/fight.php means something is open: show it as buttons.
                if (AdventureFailureMapper.Describe(exception).Kind is AdventureFailureKind.OpenChoice or AdventureFailureKind.OpenFight)
                    await session.Decisions.RefreshAsync(CancellationToken.None);
            }
        }
        finally
        {
            if (ReferenceEquals(_operationCancellation, pending)) _operationCancellation = null;
        }
    }

    private async ValueTask RunDecisionAsync(
        Func<EncounterBrowserSessionViewModel, CancellationToken, Task<string>> operation)
    {
        if (_disposed || Session is not { } session || _operationCancellation is not null) return;
        using var pending = new CancellationTokenSource();
        _operationCancellation = pending;
        try
        {
            var message = await operation(session, pending.Token);
            // A late answer from a switched account must not land in the new account's status.
            if (ReferenceEquals(Session, session)) _setStatus(message);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(Session, session)) _setStatus("The choice request was canceled.");
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(Session, session)) _setStatus(AdventureFailureMapper.Describe(exception).Text);
        }
        finally
        {
            if (ReferenceEquals(_operationCancellation, pending)) _operationCancellation = null;
        }
    }

    public ValueTask SubmitDecisionAsync(int option)
        => RunDecisionAsync((session, token) => session.Decisions.SubmitAsync(option, token));

    public ValueTask ApplyPolicyAsync()
        => RunDecisionAsync((session, token) => session.Decisions.ApplyPolicyAsync(token));

    public ValueTask RefreshDecisionAsync()
        => RunDecisionAsync((session, token) => session.Decisions.RefreshAsync(token));

    /// <summary>Ends the run. A batch or decision in flight is cancelled first, so no further turn is sent.</summary>
    public void EndSession()
    {
        _operationCancellation?.Cancel();
        _operationCancellation = null;
        Session?.EndSession();
        Browser?.ShowEncounter(null);
        _setStatus("Manuelle Adventure-Sitzung beendet.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _operationCancellation?.Cancel();
        _tokenStore?.Dispose();
    }
}
