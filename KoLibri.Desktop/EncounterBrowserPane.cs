using LibKolibri;
using Avalonia.Controls;
using Avalonia.Interactivity;
using KolArbitrage.Core.Basis;
using KolArbitrage.Core.Spiel;

#pragma warning disable CA1822 // mark members static for testability where possible

namespace KoLibri.Desktop;

/// <summary>
/// Glue between the Avalonia MainWindow and the EncounterBrowser +
/// EncounterBrowserSession view-models. Lives outside MainWindow.axaml.cs
/// so that file stays readable while the browser UI grows.
/// </summary>
public sealed class EncounterBrowserPane : IDisposable
{
    private readonly AccountRole _role;
    private readonly Func<AccountRole, AdventureExecutionGateway>? _gatewayFactory;
    private readonly Func<AccountRole, IAdventureApprovalHook, IAdventureVerificationHook,
        CancellationToken, Task<ManualAdventureSession>>? _openSession;
    private AccountRole? _selectedAccount;
    private (string ProfileId, int Count)? _observedAscension;
    private CancellationTokenSource? _operationCancellation;
    private readonly Action<string> _setStatus;
    private readonly Func<string, IEnumerable<string>> _resolveChoices;
    private readonly Func<string, IReadOnlyList<MafiaMonsterRecord>> _monstersFor;
    private ApprovalTokenStore? _tokenStore;
    private bool _disposed;

    public EncounterBrowserPane(
        AccountRole role,
        Func<AccountRole, AdventureExecutionGateway> gatewayFactory,
        Action<string> setStatus,
        Func<string, IEnumerable<string>> resolveChoices,
        Func<string, IReadOnlyList<MafiaMonsterRecord>> monstersFor)
    {
        _role = role;
        _gatewayFactory = gatewayFactory ?? throw new ArgumentNullException(nameof(gatewayFactory));
        _setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
        _resolveChoices = resolveChoices ?? throw new ArgumentNullException(nameof(resolveChoices));
        _monstersFor = monstersFor ?? throw new ArgumentNullException(nameof(monstersFor));
    }

    public EncounterBrowserPane(AccountSessionManager accounts, Action<string> setStatus)
        : this((role, approval, verification, token) =>
            ManualAdventureSession.OpenAsync(role, approval, verification, accounts, token), setStatus)
    {
    }

    public EncounterBrowserPane(
        Func<AccountRole, IAdventureApprovalHook, IAdventureVerificationHook,
            CancellationToken, Task<ManualAdventureSession>> openSession, Action<string> setStatus)
    {
        _openSession = openSession;
        _setStatus = setStatus;
        _resolveChoices = _ => Array.Empty<string>();
        _monstersFor = _ => Array.Empty<MafiaMonsterRecord>();
    }

    public void SelectAccount(AccountRole role)
    {
        if (_openSession is null || _tokenStore is null) return;
        if (_selectedAccount != role) _observedAscension = null;
        _selectedAccount = role;
        _operationCancellation?.Cancel();
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
        if (_gatewayFactory is not null)
            Session = new EncounterBrowserSessionViewModel(
                new AdventureManualDriver(_role, _gatewayFactory(_role), TokenPlaceholder),
                _tokenStore, _role);
    }

    /// <summary>
    /// Construct a driver that does not yet have a real approval token. The
    /// session replaces the gateway with the real one once the operator
    /// submits a token via <see cref="ApplyApprovalToken"/>.
    /// </summary>
    private static string TokenPlaceholder => "placeholder-until-real-token";

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

    /// <summary>Wires a freshly-supplied approval token into the live driver.</summary>
    public void ApplyApprovalToken(string token)
    {
        if (Session is null) return;
        Session.ApprovalToken = token;
        if (_gatewayFactory is null) return;
        var gateway = _gatewayFactory(_role);
        var newDriver = new AdventureManualDriver(_role, gateway, token);
        Session.ReplaceDriver(newDriver);
    }

    public ValueTask StartAdventureAsync(EncounterBrowserLocationRow row, CancellationToken cancellationToken = default)
        => RunAsync((session, browser, token) =>
        {
            if (row.Snarfblat <= 0)
                throw new InvalidOperationException("This location requires a special route.");
            var location = new AdventureLocation(row.LocationId, row.DisplayName, row.Zone,
                row.Snarfblat, row.TurnCost, Array.Empty<AdventureEncounterMetadata>());
            return session.StartAdventureAsync(location, browser, token);
        }, cancellationToken);

    public ValueTask SubmitStepAsync(EncounterBrowserActionRow row, CancellationToken cancellationToken = default)
        => RunAsync((session, browser, token) =>
            session.SubmitStepAsync(browser.BuildStepSpec(row), browser, token), cancellationToken);

    public ValueTask RunTurnsAsync(
        EncounterBrowserLocationRow? row, int maxTurns = 0, string? itemId = null,
        CancellationToken cancellationToken = default)
        => RunAsync((session, browser, token) =>
        {
            var location = row is null ? null : new AdventureLocation(row.LocationId, row.DisplayName,
                row.Zone, row.Snarfblat, row.TurnCost, Array.Empty<AdventureEncounterMetadata>());
            return session.RunTurnsAsync(browser, location, maxTurns, itemId, token);
        }, cancellationToken);

    public ValueTask FightCurrentAsync(CombatTarget target, AutoCombatPolicy policy, int maxRounds, int minimumHpPercent)
        => RunAsync((session, browser, token) => session.FightCurrentAsync(browser, target, policy, maxRounds, minimumHpPercent, token));

    public ValueTask RecheckAsync(CancellationToken cancellationToken = default)
        => RunAsync((session, browser, token) => session.RecheckAsync(browser, token), cancellationToken);

    private async ValueTask RunAsync(
        Func<EncounterBrowserSessionViewModel, EncounterBrowserViewModel, CancellationToken, ValueTask<ManualStepOutcome?>> operation,
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
        catch (Exception exception)
        {
            if (ReferenceEquals(Session, session))
                _setStatus(exception.Message);
        }
        finally
        {
            _operationCancellation = null;
        }
    }

    public void EndSession()
    {
        if (_operationCancellation is not null) return;
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
