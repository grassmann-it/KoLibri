using System.Collections.ObjectModel;

namespace KoLibri.Desktop;

/// <summary>One line of the daily briefing.</summary>
public sealed record DailyBriefingRow
{
    public required string Surface { get; init; }
    public required string Description { get; init; }
    public required string Detail { get; init; }
    /// <summary>open, done, skipped.</summary>
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
    private readonly DesktopLiveDataProvider? _provider;
    private readonly AccountRole _role;
    private readonly Func<DesktopSettings> _settings;
    private readonly Func<DateOnly> _today;
    private DesktopDailySnapshot? _lastLive;
    private bool _sessionStale;

    public DailyBriefingViewModel(
        AccountRole role = AccountRole.Main,
        DesktopLiveDataProvider? provider = null,
        Func<DesktopSettings>? settings = null,
        Func<DateOnly>? today = null)
    {
        _role = role;
        // Without a provider (tests, design time) the briefing behaves as signed out.
        _provider = provider;
        _settings = settings ?? (() => new DesktopSettings());
        _today = today ?? (() => DailyStepBoard.KolToday());
    }

    /// <summary>The per-account step list (adventure target, egg, rests, booze, spleen, clover check).</summary>
    public ObservableCollection<DailyStepRow> Steps { get; } = new();

    /// <summary>True when the stored session was rejected and no automatic log-in happened.</summary>
    public bool SessionStale
    {
        get => _sessionStale;
        private set { if (SetProperty(ref _sessionStale, value)) OnPropertyChanged(nameof(SessionBannerText)); }
    }

    public string SessionBannerText => SessionStale
        ? $"The {_role} session has expired. Log in again to load today's steps."
        : string.Empty;

    /// <summary>Lucky Surprise Eggs in the last loaded inventory.</summary>
    public int EggsOwned => _lastLive?.Count(DailyStepBoard.LuckySurpriseEggItemId) ?? 0;

    /// <summary>Rebuilds the step list from the last live numbers (after a skip or a settings change).</summary>
    public void RebuildSteps()
    {
        Steps.Clear();
        if (_lastLive is null) return;
        foreach (var row in DailyStepBoard.Build(_lastLive, _settings(), _today())) Steps.Add(row);
    }

    public AccountRole Role => _role;

    /// <summary>The saved settings (Cargo pockets, chess opt-in) the daily view shows.</summary>
    public DesktopSettings CurrentSettings => _settings();

    public ObservableCollection<DailyBriefingRow> Rows { get; } = new();

    /// <summary>The live numbers the plan was built from.</summary>
    public string SnapshotText { get; private set; } = "—";

    public string SummaryText => Rows.Count == 0
        ? "No steps for today."
        : $"{Rows.Count(x => x.IsOpen)} open · {Rows.Count - Rows.Count(x => x.IsOpen)} done";

    public bool HasPlan => Rows.Count > 0;

    /// <summary>Loads the live numbers and builds the briefing rows.</summary>
    public async Task LoadAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        LoadState = DesktopLoadState.Loading;
        try
        {
            // The daily numbers are always read fresh (the markers decide what is left today).
            var session = _provider is null ? null : await _provider.SessionAsync(_role, cancellationToken);
            var live = session is null ? null : await DesktopDailySnapshot.ReadAsync(_role, session, cancellationToken);
            if (live is null)
            {
                SessionStale = _provider is not null && await _provider.HasExpiredSessionAsync(_role, cancellationToken);
                SnapshotText = SessionStale ? "Session expired — please log in." : "No session — please log in.";
                Rows.Clear();
                Steps.Clear();
                _lastLive = null;
                LoadState = DesktopLoadState.Empty;
                return;
            }
            SessionStale = false;
            _lastLive = live;
            RebuildSteps();

            SnapshotText = $"{live.Meat:N0} Meat · {live.Adventures} adventures · Level {live.Level} · "
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
            foreach (var row in ItemRows(live.Run, _settings())) Rows.Add(row);
            foreach (var row in await CargoTopRowsAsync(session!, live.Run, cancellationToken)) Rows.Add(row);
            if (ExperimentalFeatures.IsEnabled(_settings()))
                Rows.Add(BuildRow("Arbitrage", "Restock clovers",
                    "buy 11-leaf clovers below the price ceiling",
                    "open"));
            Rows.Add(BuildRow("Bacons", "Buy BACON",
                live.BaconMachines > 0
                    ? $"{live.BaconMachines} machine(s), {live.Bacon} BACON in inventory"
                    : "no infinite BACON machine",
                live.BaconMachines > 0 ? "open" : "skipped"));

            LoadState = Rows.Count == 0 ? DesktopLoadState.Empty : DesktopLoadState.Ready;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LoadState = DesktopLoadState.Empty;
            throw;
        }
        catch (SessionExpiredException)
        {
            SessionStale = true;
            Rows.Clear();
            Steps.Clear();
            _lastLive = null;
            SnapshotText = "Session expired — please log in.";
            ErrorMessage = SessionBannerText;
            LoadState = DesktopLoadState.Error;
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

    /// <summary>
    /// Rows for the daily items the account owns (toaster, VIP Klaw, Cargo Cultist Shorts, fancy chess set), with the
    /// same planning and notes as <c>daily run</c>. Items the account does not own get no row.
    /// </summary>
    internal static IEnumerable<DailyBriefingRow> ItemRows(global::KoLibri.Daily.DailyRunLive run, DesktopSettings settings)
    {
        var options = DesktopDailyItems.Options(settings);
        var notes = global::KoLibri.Daily.DailyItems.Notes(run, options);
        string Note(string step) => string.Join(" ", notes.Where(n => n.Step == step).Select(n => n.Text));
        var planned = global::KoLibri.Daily.DailyRun.Plan(run, options).Where(s => s.Kind == "daily").ToArray();
        bool Planned(string id) => planned.Any(s => s.Id == id);
        string State(bool done, string id) => done ? "done" : Planned(id) ? "open" : "skipped";
        if (run.HasToaster)
            yield return BuildRow("Toaster", "Use the cheap toaster", Note("toaster"),
                State(run.ToasterDoneToday, global::KoLibri.Daily.DailyItems.StepIds.Toaster));
        if (run.HasVipKey)
            yield return BuildRow("VIP Lounge", "Deluxe Mr. Klaw (3x)", Note("vip"),
                State(run.KlawPullsDoneToday.Count >= global::KoLibri.Daily.DailyItems.VipKlawDailyPulls, global::KoLibri.Daily.DailyItems.StepIds.VipKlaw));
        if (run.HasCargoShorts)
            yield return BuildRow("Cargo Shorts", "Empty one pocket", Note("cargo"),
                State(run.CargoPocketDoneToday, global::KoLibri.Daily.DailyItems.StepIds.CargoPocket));
        if (run.HasFancyChessSet)
            yield return BuildRow("Chess set", "Play one game (opt-in)", Note("chess"),
                State(run.FancyChessDoneToday, global::KoLibri.Daily.DailyItems.StepIds.FancyChess));
    }

    /// <summary>Pricegun lookups while loading the tab (the run itself may do more); cached prices of the KoL day are free.</summary>
    internal const int BriefingPriceLookups = 10;

    private async Task<IReadOnlyList<DailyBriefingRow>> CargoTopRowsAsync(KolibriSession session, global::KoLibri.Daily.DailyRunLive run, CancellationToken ct)
    {
        var options = DesktopDailyItems.Options(_settings());
        if (!run.HasCargoShorts || run.CargoPocketDoneToday || global::KoLibri.Daily.DailyItems.CargoModeOf(options) != global::KoLibri.Daily.CargoPocketMode.Auto)
            return Array.Empty<DailyBriefingRow>();
        try
        {
            var ranking = await global::KoLibri.Daily.DailyItems.RankCargoAsync(session, options, BriefingPriceLookups, ct);
            return CargoRows(ranking).ToArray();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new[] { BuildRow("Cargo (Wert)", "Ranking not available", exception.Message, "skipped") };
        }
    }

    /// <summary>The top pockets by value (same list as the CLI preview); the chosen one is marked.</summary>
    internal static IEnumerable<DailyBriefingRow> CargoRows(global::KoLibri.Daily.CargoRanking ranking)
    {
        var rank = 0;
        foreach (var c in ranking.Top)
        {
            rank++;
            var unit = c.UnitPrice is { } u ? $"{c.Quantity} × {u:N0}" : $"{c.Quantity} Stück";
            yield return BuildRow($"Cargo #{rank}", $"Tasche {c.Pocket}: {c.Content}", $"{unit} = {c.Value:N0} Meat ({c.Source})",
                ranking.Chosen?.Pocket == c.Pocket ? "chosen" : "candidate");
        }
        if (ranking.Note is { } note)
            yield return BuildRow("Cargo (Wert)", "Hinweis", note, "open");
    }

    private static DailyBriefingRow BuildRow(string surface, string description, string detail, string state)
        => new()
        {
            Surface = surface,
            Description = description,
            Detail = detail,
            State = state
        };
}
