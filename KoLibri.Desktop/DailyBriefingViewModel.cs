using System.Collections.ObjectModel;
using KolArbitrage.Core.Basis;
using KolArbitrage.Core.Spiel;

namespace KoLibri.Desktop;

/// <summary>One line of the daily briefing.</summary>
public sealed record DailyBriefingRow
{
    public required string Surface { get; init; }
    public required string Description { get; init; }
    public required string Detail { get; init; }
    /// <summary>offen, erledigt, übersprungen.</summary>
    public required string State { get; init; }
    public bool IsOpen => string.Equals(State, "open", StringComparison.Ordinal);
}

/// <summary>
/// The morning briefing: what the bot intends to do today, derived from
/// the live accounts. This is the screen that replaces KoLmafia's "do
/// everything by hand" — the operator sees the plan and presses one
/// button.
///
/// The briefing reuses the same bridge the CLI and the MCP server use, so
/// the plan is identical no matter which surface asks for it.
/// </summary>
public sealed class DailyBriefingViewModel : DesktopViewModel
{
    private readonly DesktopLiveDataProvider _provider;
    private readonly AccountRole _role;

    public DailyBriefingViewModel(
        AccountRole role = AccountRole.Main,
        DesktopLiveDataProvider? provider = null)
    {
        _role = role;
        _provider = provider ?? new DesktopLiveDataProvider();
    }

    public AccountRole Role => _role;

    public ObservableCollection<DailyBriefingRow> Rows { get; } = new();

    /// <summary>The live numbers the plan was built from.</summary>
    public string SnapshotText { get; private set; } = "—";

    public string SummaryText => Rows.Count == 0
        ? "No steps for today."
        : $"{Rows.Count(x => x.IsOpen)} offen · {Rows.Count - Rows.Count(x => x.IsOpen)} erledigt";

    public bool HasPlan => Rows.Count > 0;

    /// <summary>Loads the live numbers and builds the briefing rows.</summary>
    public async Task LoadAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        LoadState = DesktopLoadState.Loading;
        try
        {
            var live = await _provider.LoadDailySnapshotAsync(
                _role, new DailyRoutineSnapshot(), forceRefresh, cancellationToken);
            if (live is null)
            {
                SnapshotText = "No session — please log in.";
                Rows.Clear();
                LoadState = DesktopLoadState.Empty;
                return;
            }

            SnapshotText = $"{live.Meat:N0} Meat · {live.Adventures} Abenteuer · Level {live.Level} · "
                + $"{live.Leaves} Leaves · {live.Clovers} Clovers · {live.Permits} Permits · "
                + $"{live.WorthlessItems} worthless items";

            Rows.Clear();
            Rows.Add(BuildRow("Campfire", "Burn a leaf",
                live.Leaves > 0
                    ? $"{live.Leaves} Leaves vorhanden"
                    : "no leaves — the day cannot be advanced",
                live.Leaves > 0 ? "open" : "skipped"));
            Rows.Add(BuildRow("Hermit", "Open 11-leaf clovers",
                live.Clovers > 0
                    ? $"{live.Clovers} Clovers, {live.Permits} Permits, {live.WorthlessItems} worthless items"
                    : "no clovers in the inventory",
                live.Clovers > 0 ? "open" : "skipped"));
            Rows.Add(BuildRow("Internet-Shop", "Claim the free pulls",
                "3 free pulls (egg, internet bacon, BACON machine)",
                "open"));
            Rows.Add(BuildRow("Arbitrage", "Restock clovers",
                "buy 11-leaf clovers below the price ceiling",
                "open"));
            Rows.Add(BuildRow("Bacons", "Buy BACON",
                live.BaconMachines > 0
                    ? $"{live.BaconMachines} Maschine(n), {live.Bacon} BACON im Inventar"
                    : "no infinite BACON machine",
                live.BaconMachines > 0 ? "open" : "skipped"));

            LoadState = Rows.Count == 0 ? DesktopLoadState.Empty : DesktopLoadState.Ready;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LoadState = DesktopLoadState.Empty;
            throw;
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            LoadState = DesktopLoadState.Error;
        }
        finally
        {
            OnPropertyChanged(nameof(SnapshotText));
            OnPropertyChanged(nameof(SummaryText));
            OnPropertyChanged(nameof(HasPlan));
        }
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
        => LoadAsync(forceRefresh: true, cancellationToken);

    private static DailyBriefingRow BuildRow(string surface, string description, string detail, string state)
        => new()
        {
            Surface = surface,
            Description = description,
            Detail = detail,
            State = state
        };
}
