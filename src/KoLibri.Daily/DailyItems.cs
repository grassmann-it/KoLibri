using System.Globalization;
using System.Text.RegularExpressions;

namespace KoLibri.Daily;

/// <summary>
/// The once-per-day item steps of the daily run (0.5): cheap toaster, Clan VIP Lounge Klaw, Cargo Cultist Shorts and
/// the fancy chess set. Requests, limits and answer texts follow KoLmafia (UseItemRequest, ClanLoungeRequest,
/// CargoCultistShortsRequest); a daily marker is set only when KoL's answer shows the result.
/// </summary>
public static class DailyItems
{
    public static class Ids
    {
        public const int Toaster = 637;
        public const int FancyChessSet = 10622;
        public const int CargoCultistShorts = 10636;
        public const int ClanVipLoungeKey = 3947;
    }

    /// <summary>Step names for <c>--skip</c>/<c>--only</c> (CLI) and <see cref="DailyRunOptions.Skip"/>.</summary>
    public static class StepNames
    {
        public const string Toaster = "toaster";
        public const string Vip = "vip";
        public const string Cargo = "cargo";
        public const string Chess = "chess";
        public static readonly IReadOnlyList<string> All = new[] { Toaster, Vip, Cargo, Chess };
    }

    /// <summary>Step ids of <see cref="DailyStep"/> (kind "daily").</summary>
    public static class StepIds
    {
        public const string Toaster = "toaster";
        public const string VipKlaw = "vip-klaw";
        public const string CargoPocket = "cargo-pocket";
        public const string FancyChess = "fancy-chess";
    }

    /// <summary>KoLmafia: the Deluxe Mr. Klaw in the VIP lounge gives three prizes a day (_deluxeKlawSummons &lt; 3).</summary>
    public const int VipKlawDailyPulls = 3;

    /// <summary>KoL: "You need to have 10 Adventures to use this item." A lost game costs these 10 adventures.</summary>
    public const int ChessAdventuresNeeded = 10;

    public static class Markers
    {
        public const string Toaster = "toaster";
        public const string FancyChess = "fancy-chess";
        public const string CargoPocket = "cargo-pocket";
        /// <summary>One marker per Klaw pull (1..3), so a run that stopped after two pulls only does the third.</summary>
        public static string VipKlaw(int pull) => "vip-klaw:" + pull.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Klaw pulls already confirmed today (0..3), from the markers.</summary>
    public static int KlawPullsDone(DailyMarkers daily)
    {
        ArgumentNullException.ThrowIfNull(daily);
        var done = 0;
        for (var pull = 1; pull <= VipKlawDailyPulls; pull++)
            if (daily.IsDone(Markers.VipKlaw(pull))) done++;
        return done;
    }

    /// <summary>Option of the Cargo step in "auto" mode: the executor values the pockets still full and picks the best.</summary>
    public const string CargoAuto = "auto";

    /// <summary>
    /// The effective Cargo mode: Off when switched off; List when pockets are configured (an explicit list wins); Auto otherwise
    /// (the default: the most valuable pocket without a fight).
    /// </summary>
    public static CargoPocketMode CargoModeOf(DailyRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.CargoMode == CargoPocketMode.Off) return CargoPocketMode.Off;
        if (CargoPockets.Usable(options.CargoPockets).Count > 0) return CargoPocketMode.List;
        return options.CargoMode == CargoPocketMode.List ? CargoPocketMode.Off : CargoPocketMode.Auto;
    }

    /// <summary>The planned item steps, in order: toaster, Klaw, Cargo pocket, chess. Pure.</summary>
    internal static IEnumerable<DailyStep> Plan(DailyRunLive live, DailyRunOptions options)
    {
        if (!options.Skip.Contains(StepNames.Toaster) && live.HasToaster && !live.ToasterDoneToday)
            yield return new DailyStep("daily", StepIds.Toaster, "1");

        if (!options.Skip.Contains(StepNames.Vip) && live.HasVipKey)
            for (var pull = 1; pull <= VipKlawDailyPulls; pull++)
                if (!live.KlawPullsDoneToday.Contains(pull))
                    yield return new DailyStep("daily", StepIds.VipKlaw, pull.ToString(CultureInfo.InvariantCulture));

        var mode = CargoModeOf(options);
        if (!options.Skip.Contains(StepNames.Cargo) && live.HasCargoShorts && !live.CargoPocketDoneToday && mode != CargoPocketMode.Off)
            yield return new DailyStep("daily", StepIds.CargoPocket, mode == CargoPocketMode.Auto
                ? CargoAuto
                : string.Join(",", CargoPockets.Usable(options.CargoPockets).Select(p => p.ToString(CultureInfo.InvariantCulture))));

        if (!options.Skip.Contains(StepNames.Chess) && options.PlayChess && live.HasFancyChessSet && !live.FancyChessDoneToday
            && live.Adventures >= ChessAdventuresNeeded)
            yield return new DailyStep("daily", StepIds.FancyChess, "1");
    }

    /// <summary>Why an owned item has no step today (preview notes). Items the account does not own are not listed.</summary>
    public static IReadOnlyList<DailyItemNote> Notes(DailyRunLive live, DailyRunOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(live);
        options ??= new DailyRunOptions();
        var notes = new List<DailyItemNote>();
        void Add(string step, string text) => notes.Add(new DailyItemNote(step, text));

        if (live.HasToaster)
        {
            if (options.Skip.Contains(StepNames.Toaster)) Add(StepNames.Toaster, "übersprungen (--skip toaster)");
            else if (live.ToasterDoneToday) Add(StepNames.Toaster, "heute schon getoastet");
            else Add(StepNames.Toaster, "cheap toaster benutzen: 1x am Tag, gibt toast, kostet nichts");
        }
        if (live.HasVipKey)
        {
            var done = live.KlawPullsDoneToday.Count;
            if (options.Skip.Contains(StepNames.Vip)) Add(StepNames.Vip, "übersprungen (--skip vip)");
            else if (done >= VipKlawDailyPulls) Add(StepNames.Vip, "Deluxe Mr. Klaw: heute schon 3x gezogen");
            else Add(StepNames.Vip, $"Deluxe Mr. Klaw im VIP-Raum: noch {VipKlawDailyPulls - done} von {VipKlawDailyPulls} Zügen, kostenlos. "
                + "Hot Tub, Pool, Wahrsagerin und Fotoautomat bleiben manuell.");
        }
        if (live.HasCargoShorts)
        {
            var configured = options.CargoPockets ?? Array.Empty<int>();
            var refused = configured.Where(CargoPockets.StartsFight).ToArray();
            if (options.Skip.Contains(StepNames.Cargo)) Add(StepNames.Cargo, "übersprungen (--skip cargo)");
            else if (live.CargoPocketDoneToday) Add(StepNames.Cargo, "heute schon eine Tasche geleert");
            else if (CargoModeOf(options) == CargoPocketMode.Off)
                Add(StepNames.Cargo, "aus (--cargo-pocket none): es wird keine Tasche gewählt");
            else if (CargoModeOf(options) == CargoPocketMode.Auto)
                Add(StepNames.Cargo, "nach Wert (--cargo-pocket auto): die wertvollste volle Tasche ohne Kampf und ohne Zug "
                    + "(Items: Stück × Marktpreis, Meat: Betrag; Effekt-/Werte-Taschen zählen 0; bei Gleichstand gewinnt Meat). Jede Tasche geht nur einmal pro Aszension.");
            else Add(StepNames.Cargo, "Tasche in dieser Reihenfolge (erste noch volle): "
                + string.Join(", ", CargoPockets.Usable(configured).Select(CargoPockets.Describe)));
            if (refused.Length > 0)
                Add(StepNames.Cargo, "nicht gewählt, weil sie einen Kampf startet: " + string.Join(", ", refused.Select(CargoPockets.Describe)));
        }
        if (live.HasFancyChessSet)
        {
            if (options.Skip.Contains(StepNames.Chess)) Add(StepNames.Chess, "übersprungen (--skip chess)");
            else if (live.FancyChessDoneToday) Add(StepNames.Chess, "heute schon gespielt");
            else if (!options.PlayChess)
                Add(StepNames.Chess, "nur auf Wunsch (--chess): Sieg gibt Yeg's Glory (100 Züge), eine Niederlage kostet 10 Abenteuer; kein Kampf");
            else if (live.Adventures < ChessAdventuresNeeded)
                Add(StepNames.Chess, $"braucht mindestens {ChessAdventuresNeeded} Abenteuer (hat {live.Adventures})");
            else Add(StepNames.Chess, "fancy chess set: eine Partie (Niederlage kostet 10 Abenteuer)");
        }
        return notes;
    }

    /// <summary>Runs one "daily" step. Returns whether KoL confirmed it and a short message.</summary>
    internal static async Task<DailyItemOutcome> ExecuteAsync(KolibriSession session, DailyStep step, CancellationToken ct,
        DailyRunOptions? options = null)
        => step.Id switch
        {
            StepIds.Toaster => await UseToasterAsync(session, ct).ConfigureAwait(false),
            StepIds.VipKlaw => await PullKlawAsync(session, int.TryParse(step.Option, out var pull) ? pull : 1, ct).ConfigureAwait(false),
            StepIds.CargoPocket => string.Equals(step.Option, CargoAuto, StringComparison.OrdinalIgnoreCase)
                ? await PickPocketAsync(session, null, ct, options).ConfigureAwait(false)
                : await PickPocketAsync(session, CargoPockets.Parse(step.Option), ct).ConfigureAwait(false),
            StepIds.FancyChess => await PlayChessAsync(session, ct).ConfigureAwait(false),
            _ => DailyItemOutcome.Fail($"{step.Id}: unknown step")
        };

    /// <summary>One answer after redirects: the final page's path and HTML (the session follows redirects itself).</summary>
    internal sealed record ItemAnswer(int StatusCode, string Body, string FinalPath)
    {
        public bool IsSuccess => StatusCode is >= 200 and < 300;
    }

    private static ItemAnswer Of(Page page) => new(page.StatusCode, page.Html, page.Url.AbsolutePath.TrimStart('/'));

    private static async Task<ItemAnswer> GetAsync(KolibriSession session, string path, CancellationToken ct)
    {
        try { return Of(await session.GetPageAsync(path, cancellationToken: ct).ConfigureAwait(false)); }
        catch (UnexpectedPageException e) when (e.Page.Kind == PageKind.Error) { return Of(e.Page); }
    }

    private static async Task<ItemAnswer> PostAsync(KolibriSession session, string path, Dictionary<string, string> form, CancellationToken ct)
    {
        try { return Of(await session.PostAsync(path, form, cancellationToken: ct).ConfigureAwait(false)); }
        catch (UnexpectedPageException e) when (e.Page.Kind == PageKind.Error) { return Of(e.Page); }
    }

    private static async Task<string> UseUrlAsync(KolibriSession session, int itemId, CancellationToken ct)
    {
        var pwd = await session.GetPasswordHashAsync(ct).ConfigureAwait(false);
        return $"inv_use.php?which=3&whichitem={itemId.ToString(CultureInfo.InvariantCulture)}&ajax=1&pwd={pwd}";
    }

    /// <summary>KoLmafia UseItemRequest: "You push the lever and are rewarded with toast" (_toastSummoned).</summary>
    internal static async Task<DailyItemOutcome> UseToasterAsync(KolibriSession session, CancellationToken ct)
    {
        var answer = await GetAsync(session, await UseUrlAsync(session, Ids.Toaster, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        var body = answer.Body;
        if (answer.IsSuccess && Has(body, "You acquire") && Has(body, "toast"))
        {
            session.Daily.MarkDone(Markers.Toaster);
            return DailyItemOutcome.Done("toaster: toast erhalten") with { Gains = DailyGains.Parse(body) };
        }
        return DailyItemOutcome.Fail($"toaster (unklar: KoL's answer shows no toast; no daily marker set) {Short(body)}");
    }

    /// <summary>KoLmafia ClanLoungeRequest KLAW: "claw slowly descends" counts a pull; "look greedy" means all three are used.</summary>
    internal static async Task<DailyItemOutcome> PullKlawAsync(KolibriSession session, int pull, CancellationToken ct)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal) { ["action"] = "klaw" };
        var answer = await PostAsync(session, "clan_viplounge.php", form, ct).ConfigureAwait(false);
        if (answer.FinalPath.Contains("clan_signup", StringComparison.OrdinalIgnoreCase))
            return DailyItemOutcome.Fail("vip-klaw: not in a clan (KoL sent the clan sign-up page)");
        var body = answer.Body;
        if (answer.IsSuccess && Has(body, "claw slowly descends"))
        {
            session.Daily.MarkDone(Markers.VipKlaw(pull));
            return DailyItemOutcome.Done(Has(body, "You acquire") ? $"vip-klaw {pull}: Preis gegriffen" : $"vip-klaw {pull}: Klaue abgerutscht (Zug zählt)")
                with { Gains = DailyGains.Parse(body) };
        }
        if (answer.IsSuccess && Has(body, "you wouldn't want to look greedy"))
        {
            for (var p = 1; p <= VipKlawDailyPulls; p++) session.Daily.MarkDone(Markers.VipKlaw(p));
            return DailyItemOutcome.AlreadyDone("vip-klaw: KoL says the three pulls are used up today");
        }
        return DailyItemOutcome.Fail($"vip-klaw:{pull} (unklar: KoL's answer shows no Klaw pull; no daily marker set) {Short(body)}");
    }

    /// <summary>
    /// KoLmafia UseItemRequest FANCY_CHESS_SET: "You sit down at the chessboard" (played) or "Your poor heart can't handle"
    /// (already played today) set _fancyChessSetUsed; "You don't have time for a game of chess" means fewer than 10 adventures.
    /// </summary>
    internal static async Task<DailyItemOutcome> PlayChessAsync(KolibriSession session, CancellationToken ct)
    {
        var answer = await GetAsync(session, await UseUrlAsync(session, Ids.FancyChessSet, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        var body = answer.Body;
        if (answer.IsSuccess && Has(body, "You sit down at the chessboard"))
        {
            session.Daily.MarkDone(Markers.FancyChess);
            return DailyItemOutcome.Done(Has(body, "Yeg's Glory")
                ? "fancy-chess: gewonnen (Yeg's Glory)"
                : "fancy-chess: gespielt, kein Yeg's Glory auf der Seite (Niederlage kostet 10 Abenteuer)") with { Gains = DailyGains.Parse(body) };
        }
        if (answer.IsSuccess && Has(body, "Your poor heart can't handle"))
        {
            session.Daily.MarkDone(Markers.FancyChess);
            return DailyItemOutcome.AlreadyDone("fancy-chess: KoL says today's game is already played");
        }
        if (Has(body, "You don't have time for a game of chess"))
            return DailyItemOutcome.Fail($"fancy-chess: needs at least {ChessAdventuresNeeded} adventures");
        return DailyItemOutcome.Fail($"fancy-chess (unklar: KoL's answer shows no game; no daily marker set) {Short(body)}");
    }

    /// <summary>
    /// KoLmafia CargoCultistShortsRequest: GET inventory.php?action=pocket opens choice 1420 with the pockets still full
    /// this ascension; POST choice.php (whichchoice=1420, option=1, pocket=N) empties one. Pockets that start a fight are
    /// never sent. A pick that leaves the choice open is closed with option=2.
    /// </summary>
    internal static async Task<DailyItemOutcome> PickPocketAsync(KolibriSession session, IReadOnlyList<int>? wanted, CancellationToken ct,
        DailyRunOptions? options = null)
    {
        var stateDirectory = options?.StateDirectory;
        var auto = wanted is null;
        var usable = CargoPockets.Usable(wanted);
        if (!auto && usable.Count == 0) return DailyItemOutcome.Fail("cargo-pocket: no pocket configured (fight pockets are never picked)");
        CargoPriceBook? book = null;
        if (auto)
        {
            // All price requests (Pricegun, mall) happen before the pocket page opens the choice; afterwards only the cache counts.
            book = PriceBook(session, stateDirectory, options?.CargoPriceLookups ?? CargoPocketValue.DefaultPriceLookups);
            var known = CargoPocketState.Load(StateFile(stateDirectory, PocketStateFile), session.Account.Id.Value);
            await CargoPocketValue.RankAsync(known, book, ct).ConfigureAwait(false);
            book.Offline = true;
        }
        var html = (await GetAsync(session, "inventory.php?action=pocket", ct).ConfigureAwait(false)).Body;
        if (Has(html, CargoPockets.ExhaustedText))
        {
            session.Daily.MarkDone(Markers.CargoPocket);
            return DailyItemOutcome.AlreadyDone("cargo-pocket: KoL says today's pocket is already picked");
        }
        var available = CargoPockets.AvailablePockets(html);
        if (available is null)
            return DailyItemOutcome.Fail($"cargo-pocket (unklar: the pocket page did not open; nothing picked) {Short(html)}");
        CargoPocketState.Save(StateFile(stateDirectory, PocketStateFile), session.Account.Id.Value, available);
        int pocket;
        string why = "";
        if (auto)
        {
            var ranking = await CargoPocketValue.RankAsync(available, book!, ct).ConfigureAwait(false);
            if (ranking.Chosen is not { } best)
            {
                await LeavePocketsAsync(session, ct).ConfigureAwait(false);
                return DailyItemOutcome.Fail("cargo-pocket auto: no full pocket with a value (Meat or items) left; nothing picked");
            }
            pocket = best.Pocket;
            why = $" (Wert {best.Value:N0} Meat, {best.Source})";
        }
        else
        {
            pocket = usable.FirstOrDefault(available.Contains);
            if (pocket == 0)
            {
                await LeavePocketsAsync(session, ct).ConfigureAwait(false);
                return DailyItemOutcome.Fail("cargo-pocket: all configured pockets are already emptied this ascension ("
                    + string.Join(",", usable) + "); configure more (--cargo-pocket)");
            }
        }

        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["whichchoice"] = CargoPockets.ChoiceId.ToString(CultureInfo.InvariantCulture),
            ["option"] = "1",
            ["pocket"] = pocket.ToString(CultureInfo.InvariantCulture)
        };
        var answer = await PostAsync(session, "choice.php", form, ct).ConfigureAwait(false);
        var body = answer.Body;
        if (answer.FinalPath.Contains("fight.php", StringComparison.OrdinalIgnoreCase))
        {
            // KoLmafia registerPocketFight: the pocket counts as picked.
            session.Daily.MarkDone(Markers.CargoPocket);
            return DailyItemOutcome.Fail($"cargo-pocket {pocket}: KoL started a fight; finish it in the browser");
        }
        DailyItemOutcome outcome;
        if (Has(body, CargoPockets.ExhaustedText))
        {
            session.Daily.MarkDone(Markers.CargoPocket);
            outcome = DailyItemOutcome.AlreadyDone("cargo-pocket: KoL says today's pocket is already picked");
        }
        else if (Has(body, "That pocket is empty"))
            outcome = DailyItemOutcome.Fail($"cargo-pocket {pocket}: that pocket is empty (already emptied this ascension); no daily marker set");
        else if (Has(body, "leave your pockets unplundered"))
            outcome = DailyItemOutcome.Fail($"cargo-pocket {pocket}: KoL left the pockets alone; no daily marker set");
        else if (answer.IsSuccess && (Has(body, "You acquire") || Has(body, "You gain")))
        {
            session.Daily.MarkDone(Markers.CargoPocket);
            outcome = DailyItemOutcome.Done($"cargo-pocket: {CargoPockets.Describe(pocket)} geleert{why}") with { Gains = DailyGains.Parse(body) };
        }
        else
            outcome = DailyItemOutcome.Fail($"cargo-pocket {pocket} (unklar: KoL's answer shows no result; no daily marker set) {Short(body)}");

        if (CargoPockets.StillInChoice(body)) await LeavePocketsAsync(session, ct).ConfigureAwait(false);
        return outcome;
    }

    /// <summary>Closes the pocket choice (option 2, "leave your pockets alone").</summary>
    private static async Task LeavePocketsAsync(KolibriSession session, CancellationToken ct)
    {
        var leave = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["whichchoice"] = CargoPockets.ChoiceId.ToString(CultureInfo.InvariantCulture),
            ["option"] = "2"
        };
        await PostAsync(session, "choice.php", leave, ct).ConfigureAwait(false);
    }

    internal const string PocketStateFile = "cargo-pockets.json";
    internal const string PriceCacheFile = "cargo-prices.json";

    /// <summary>A file next to the daily markers (the client's state directory, or the default KoLibri data folder).</summary>
    internal static string StateFile(string? stateDirectory, string name)
    {
        var root = stateDirectory;
        if (string.IsNullOrWhiteSpace(root))
        {
            var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrWhiteSpace(data)) data = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            root = Path.Combine(data, "KoLibri");
        }
        return Path.Combine(root, name);
    }

    /// <summary>Prices from Pricegun (no KoL request), then the mall's cheapest offer for the top candidates; cached per KoL day.</summary>
    internal static CargoPriceBook PriceBook(KolibriSession session, string? stateDirectory, int priceLookups)
        => new(StateFile(stateDirectory, PriceCacheFile), session.Daily.GameDay,
            async (id, ct) => (await session.Mall.GetPriceHistoryAsync(id, ct).ConfigureAwait(false))?.Value,
            async (id, ct) =>
            {
                var offers = await session.Mall.GetOffersAsync(id, cancellationToken: ct).ConfigureAwait(false);
                return offers.Listings.Count == 0 ? null : offers.Listings.Min(l => l.UnitPrice);
            },
            priceLookups);

    /// <summary>
    /// The value ranking for the preview: the pockets last read from the pocket page (no KoL page is opened; unknown before the
    /// first run), prices from the cache, Pricegun (up to <paramref name="priceLookups"/>) and the mall for the top candidates.
    /// </summary>
    public static Task<CargoRanking> RankCargoAsync(KolibriSession session, DailyRunOptions? options = null, int? priceLookups = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var dir = options?.StateDirectory;
        var available = CargoPocketState.Load(StateFile(dir, PocketStateFile), session.Account.Id.Value);
        return CargoPocketValue.RankAsync(available,
            PriceBook(session, dir, priceLookups ?? options?.CargoPriceLookups ?? CargoPocketValue.DefaultPriceLookups), cancellationToken);
    }

    private static bool Has(string? body, string text) => body?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;

    private static string Short(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "(empty answer)";
        var text = Regex.Replace(body, "<[^>]+>", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > 160 ? "| " + text[..160] : "| " + text;
    }
}

/// <summary>One preview note of an item step (step name, German text).</summary>
public sealed record DailyItemNote(string Step, string Text);

internal sealed record DailyItemOutcome(bool Confirmed, bool Already, string Message)
{
    public IReadOnlyList<string> Gains { get; init; } = Array.Empty<string>();

    public static DailyItemOutcome Done(string message) => new(true, false, message);
    public static DailyItemOutcome AlreadyDone(string message) => new(false, true, message);
    public static DailyItemOutcome Fail(string message) => new(false, false, message);
}

/// <summary>
/// The 666 pockets of the Cargo Cultist Shorts (choice 1420). Each pocket can be emptied once per ascension, one pocket
/// per day. Data from KoLmafia's cultshorts.txt (BSD): the pockets that start a fight, and short spoilers for the
/// pockets that give Meat or a sellable item without a fight or a turn.
/// </summary>
public static class CargoPockets
{
    public const int ChoiceId = 1420;
    internal const string ExhaustedText = "the power of the pockets has been exhausted for the day";

    /// <summary>KoLmafia cultshorts.txt, type "Monster": these pockets start a fight and are never picked.</summary>
    public static readonly IReadOnlyDictionary<int, string> FightPockets = new Dictionary<int, string>
    {
        [30] = "bookbat", [47] = "dairy goat", [136] = "Knob Goblin Elite Guardsman", [143] = "dirty old lihc", [191] = "batrat",
        [220] = "lobsterfrogman", [235] = "modern zmobie", [250] = "Blooper", [267] = "creepy clown", [279] = "Hellion",
        [299] = "Knob Goblin Harem Girl", [306] = "big creepy spider", [317] = "Camel's Toe", [363] = "pufferfish",
        [383] = "Skinflute", [402] = "Fruit Golem", [425] = "eXtreme Orcish snowboarder", [428] = "Mob Penguin Thug",
        [443] = "War Hippy (space) cadet", [448] = "completely different spider", [452] = "pygmy shaman", [490] = "Booze Giant",
        [565] = "mountain man", [568] = "War Pledge", [589] = "Green Ops Soldier", [612] = "bugbear-in-the-box",
        [646] = "1335 HaXx0r", [666] = "smut orc pervert",
    };

    /// <summary>
    /// Safe daily picks (no fight, no turn): the Meat pockets (KoLmafia cultshorts.txt "Meat", the amount is fixed) and the
    /// "Cash" pockets, best first. There is no KoLmafia default; a pocket is picked only when configured.
    /// </summary>
    public static readonly IReadOnlyList<(int Pocket, string Spoiler)> Suggestions = new (int, string)[]
    {
        (621, "917 Meat"), (484, "872 Meat"), (54, "725 Meat"), (504, "678 Meat"), (554, "566 Meat"),
        (139, "451 Meat"), (115, "351 Meat"), (149, "245 Meat"), (577, "118 Meat"),
        (38, "fat wallet (Meat)"), (109, "handful of tips (Meat)"), (141, "ancient vinyl coin purse (Meat)"),
        (338, "black pension check (Meat)"), (417, "old leather wallet (Meat)"), (419, "old coin purse (Meat)"),
        (512, "Gathered Meat-Clip (Meat)"), (281, "CSA Discount Card"), (434, "Warm Subject gift certificate"),
        (524, "SMOOCH bottlecap"), (157, "collection of tiny spooky objects"),
    };

    private static readonly Dictionary<int, string> Known = Suggestions.ToDictionary(s => s.Pocket, s => s.Spoiler);

    public static bool StartsFight(int pocket) => FightPockets.ContainsKey(pocket);

    /// <summary>"621 (917 Meat)", "565 (Kampf: mountain man)" or "123 (siehe Wiki)".</summary>
    public static string Describe(int pocket)
    {
        var p = pocket.ToString(CultureInfo.InvariantCulture);
        if (FightPockets.TryGetValue(pocket, out var monster)) return $"{p} (Kampf: {monster})";
        if (Known.TryGetValue(pocket, out var spoiler)) return $"{p} ({spoiler})";
        return CargoPocketValue.Pocket(pocket) is { } data ? $"{p} ({data.Content})" : $"{p} (Inhalt siehe Wiki)";
    }

    public static string SuggestionText(int count = 6)
        => string.Join(", ", Suggestions.Take(count).Select(s => Describe(s.Pocket)));

    /// <summary>The configured pockets in order, without duplicates, out-of-range numbers and fight pockets.</summary>
    public static IReadOnlyList<int> Usable(IEnumerable<int>? pockets)
        => (pockets ?? Array.Empty<int>()).Where(p => p is >= 1 and <= 666 && !StartsFight(p)).Distinct().ToArray();

    /// <summary>Parses "621,484 54" into pocket numbers; anything that is not a number is an error.</summary>
    public static IReadOnlyList<int> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<int>();
        var result = new List<int>();
        foreach (var part in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var pocket) || pocket is < 1 or > 666)
                throw new FormatException($"'{part}' is not a Cargo Cultist Shorts pocket (1-666).");
            result.Add(pocket);
        }
        return result;
    }

    // KoLmafia AVAILABLE_POCKET_PATTERN: one inline form per pocket that is still full.
    private static readonly Regex AvailablePocket = new(
        "<form method=\"post\" action=\"choice.php\" style=\"display: inline\">.*?name=\"pocket\" value=\"(\\d+)\".*?</form>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The pockets still full this ascension, or null when the page is not the pocket choice.</summary>
    public static IReadOnlySet<int>? AvailablePockets(string? html)
    {
        if (string.IsNullOrEmpty(html) || html.IndexOf("There appear to be 666 pockets on these shorts.", StringComparison.OrdinalIgnoreCase) < 0)
            return null;
        var set = new HashSet<int>();
        foreach (Match match in AvailablePocket.Matches(html))
            set.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
        return set;
    }

    internal static bool StillInChoice(string? html)
        => !string.IsNullOrEmpty(html)
           && Regex.IsMatch(html, "name=[\"']?whichchoice[\"']?\\s+value=[\"']?1420\\b", RegexOptions.IgnoreCase);
}

/// <summary>What a KoL answer page says was gained: items ("You acquire"), Meat ("You gain N Meat") and effects.</summary>
public static class DailyGains
{
    private static readonly Regex OneItem = new(@"You acquire an item:\s*<b>([^<]+)</b>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ManyItems = new(@"You acquire\s*<b>([0-9][0-9,]*)\s+([^<]+)</b>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Effect = new(@"You acquire an effect:\s*<b>([^<]+)</b>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Meat = new(@"You gain\s+([0-9][0-9,]*)\s+Meat", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<string> Parse(string? html)
    {
        if (string.IsNullOrEmpty(html)) return Array.Empty<string>();
        var gains = new List<string>();
        foreach (Match m in OneItem.Matches(html)) gains.Add(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value.Trim()));
        foreach (Match m in ManyItems.Matches(html)) gains.Add($"{m.Groups[1].Value.Replace(",", "")}× {System.Net.WebUtility.HtmlDecode(m.Groups[2].Value.Trim())}");
        foreach (Match m in Effect.Matches(html)) gains.Add("Effekt: " + System.Net.WebUtility.HtmlDecode(m.Groups[1].Value.Trim()));
        foreach (Match m in Meat.Matches(html)) gains.Add($"{m.Groups[1].Value.Replace(",", "")} Meat");
        return gains;
    }
}
