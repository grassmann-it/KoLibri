using Avalonia.Controls;
using Avalonia.Interactivity;
using KolArbitrage.Core.Adventure;
using KolArbitrage.Core.Accounts;
using KolArbitrage.Core.Data;
using KolArbitrage.Core.Security;

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
    private readonly Func<AccountRole, AdventureExecutionGateway> _gatewayFactory;
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

    public EncounterBrowserViewModel? Browser { get; private set; }
    public EncounterBrowserSessionViewModel? Session { get; private set; }

    public void Initialise(string catalogDirectory)
    {
        var catalog = new MafiaDataLoader().Load(catalogDirectory);
        Browser = new EncounterBrowserViewModel(catalog);
        _tokenStore = new ApprovalTokenStore(catalogDirectory, "manual-approval-token.json");
        Session = new EncounterBrowserSessionViewModel(
            new AdventureManualDriver(_role, _gatewayFactory(_role), TokenPlaceholder),
            _tokenStore,
            _role);
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
        var gateway = _gatewayFactory(_role);
        var newDriver = new AdventureManualDriver(_role, gateway, token);
        Session.ReplaceDriver(newDriver);
    }

    public async ValueTask StartAdventureAsync(EncounterBrowserLocationRow row, CancellationToken cancellationToken = default)
    {
        if (Browser is null || Session is null) return;
        var location = new AdventureLocation(row.LocationId, row.DisplayName, row.Zone,
            Snarfblat: row.Snarfblat, TurnCost: row.TurnCost,
            Encounters: Array.Empty<AdventureEncounterMetadata>());
        var outcome = await Session.StartAdventureAsync(location, Browser, cancellationToken).ConfigureAwait(false);
        if (outcome is null)
        {
            _setStatus(Session.LastMessage ?? "Adventure start failed.");
            return;
        }
        _setStatus($"Adventure '{row.DisplayName}' gestartet.");
    }

    public async ValueTask SubmitStepAsync(EncounterBrowserActionRow row, CancellationToken cancellationToken = default)
    {
        if (Browser is null || Session is null) return;
        var step = Browser.BuildStepSpec(row);
        var outcome = await Session.SubmitStepAsync(step, Browser, cancellationToken).ConfigureAwait(false);
        if (outcome is null)
        {
            _setStatus(Session.LastMessage ?? "Step submission failed.");
            return;
        }
        _setStatus(Session.LastMessage ?? "Step submitted.");
    }

    public void EndSession()
    {
        Session?.EndSession();
        _setStatus("Manuelle Adventure-Sitzung beendet.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tokenStore?.Dispose();
    }
}
