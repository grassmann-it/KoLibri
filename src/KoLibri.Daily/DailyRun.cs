using System.Globalization;
using System.Text.RegularExpressions;

namespace KoLibri.Daily;

/// <summary>One planned daily step, as the 0.4 CLI printed it: kind ("choice"/"item"), id, option.</summary>
public sealed record DailyStep(string Kind, string Id, string? Option)
{
    internal int? HermitCloverTarget { get; init; }
}

/// <summary>The live numbers behind a daily run (0.4: LiveDailySnapshot).</summary>
public sealed record DailyRunLive
{
    public int Leaves { get; init; }
    public int Clovers { get; init; }
    public int Permits { get; init; }
    public int WorthlessItems { get; init; }
    public int Bacon { get; init; }
    public int BaconMachines { get; init; }
    public long Meat { get; init; }
    public bool DayShortenerDoneToday { get; init; }
    public bool CampfireLeafDoneToday { get; init; }
    public bool HermitDoneToday { get; init; }
    internal int HermitCloversDoneToday { get; init; }
    public bool BaconMachineUsedToday { get; init; }
    public IReadOnlyList<string> MemeItemsDoneToday { get; init; } = Array.Empty<string>();

    // Daily items (0.5): owned = in the inventory (the Cargo Cultist Shorts also when worn).
    public int Adventures { get; init; }
    public bool HasToaster { get; init; }
    public bool HasFancyChessSet { get; init; }
    public bool HasCargoShorts { get; init; }
    public bool HasVipKey { get; init; }
    public bool ToasterDoneToday { get; init; }
    public bool FancyChessDoneToday { get; init; }
    public bool CargoPocketDoneToday { get; init; }
    /// <summary>Klaw pulls (1..3) confirmed today.</summary>
    public IReadOnlyList<int> KlawPullsDoneToday { get; init; } = Array.Empty<int>();
}

/// <summary>What the run should do on top of the live numbers (0.4: CLI flags on DailyRoutineSnapshot).</summary>
public sealed record DailyRunOptions
{
    public int HermitCloversWanted { get; init; }
    /// <summary>"campfire", "hermit", "internet-shop", "toaster", "vip", "cargo", "chess".</summary>
    public IReadOnlySet<string> Skip { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    internal IReadOnlySet<string> Only { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    internal bool IsExcludedByOnly(string step) => Only.Count > 0 && !Only.Contains(step);
    internal bool IsExcluded(string step) => Skip.Contains(step) || IsExcludedByOnly(step);
    public int CampfireReserve { get; init; } = 3;
    /// <summary>
    /// 0.4 DailyRoutineSnapshot values for the "sell clovers" branch. Since the 0.5 review the plan trades with the Hermit
    /// only for <see cref="HermitCloversWanted"/> clovers, so these values no longer add Hermit steps on their own.
    /// </summary>
    public long AdventureReserveValue { get; init; }
    public long CloverFloorValue { get; init; }
    public long ExpectedCloverSaleValue { get; init; }
    /// <summary>
    /// Cargo Cultist Shorts pockets in order of preference; the first one still full this ascension is picked. Empty (the
    /// default): no pocket is picked. Pockets that start a fight are never picked (<see cref="CargoPockets.FightPockets"/>).
    /// </summary>
    public IReadOnlyList<int> CargoPockets { get; init; } = Array.Empty<int>();
    /// <summary>Opt-in: play the fancy chess set (a lost game costs 10 adventures).</summary>
    public bool PlayChess { get; init; }
    /// <summary>
    /// Auto (default): the most valuable full pocket without a fight; Off: no pocket. A non-empty <see cref="CargoPockets"/> list
    /// always wins over Auto (see <see cref="DailyItems.CargoModeOf"/>).
    /// </summary>
    public CargoPocketMode CargoMode { get; init; } = CargoPocketMode.Auto;
    /// <summary>Pricegun lookups per run for the Cargo ranking; cached prices of the KoL day are free.</summary>
    public int CargoPriceLookups { get; init; } = CargoPocketValue.DefaultPriceLookups;
    /// <summary>Folder for the Cargo price cache and the last read pocket page (the client's StateDirectory; null = default KoLibri folder).</summary>
    public string? StateDirectory { get; init; }
}

public enum DailyRunCompleteness { None, Complete, Partial, Failed }

/// <summary>One step of the Internet Meme Shop run and what KoL answered (0.4: LibKolibri MemeShopStepResult).</summary>
public sealed record MemeShopStep(string Description, bool Accepted, string Message, bool Unclear = false);

/// <summary>Outcome of <see cref="DailyRun.RunMemeShopAsync"/> (0.4: LibKolibri MemeShopRunResult).</summary>
public sealed record MemeShopRun(string Verdict, IReadOnlyList<MemeShopStep> Steps, int BaconBought, long BaconSpent, long BaconUnclear, bool Unclear)
{
    public PurchaseStatus? BaconStatus { get; init; }
}

/// <summary>What one sent step did: state (done, already, failed), KoL's message and what was gained (items, Meat, effects).</summary>
public sealed record DailyStepReport(string Step, string? Option, string State, string Message, IReadOnlyList<string> Gains);

public sealed record DailyRunResult(bool Executed, int AttemptedSteps, int SubmittedSteps, IReadOnlyList<string> FailedSteps)
{
    /// <summary>One report per sent daily-item step (toaster, Klaw, Cargo pocket, chess), with the gains from the page.</summary>
    public IReadOnlyList<DailyStepReport> Steps { get; init; } = Array.Empty<DailyStepReport>();

    public DailyRunCompleteness Completeness
        => AttemptedSteps == 0 ? DailyRunCompleteness.None
         : FailedSteps.Count == 0 ? DailyRunCompleteness.Complete
         : SubmittedSteps > 0 ? DailyRunCompleteness.Partial
         : DailyRunCompleteness.Failed;
}

/// <summary>
/// The live daily routine of the 0.4 CLI (<c>daily run</c>): campfire leaf / Day Shortener, Hermit clovers and the
/// Internet Meme Shop. Planning is the 0.4 bridge logic; every request goes through <see cref="KolibriSession"/>
/// (allowlist, limiter, guard), and the once-per-day markers are <see cref="KolibriSession.Daily"/>.
/// </summary>
public static class DailyRun
{
    /// <summary>Marker keys, the same strings as 0.4 (so markers written by the 0.4 CLI stay valid).</summary>
    public static class Markers
    {
        public const string DayShortener = "day-shortener";
        public const string BaconMachine = "bacon-machine";
        public const string CampfireLeaf = "campfire-leaf";
        public const string HermitClovers = "hermit-clovers";
        internal static string HermitClover(int trade) => $"{HermitClovers}:{trade}";
        public static string MemeBuy(string itemName) => "meme-buy:" + itemName.ToLowerInvariant();
    }

    public static class Ids
    {
        public const int InflammableLeaf = 11341;
        public const int ElevenLeafClover = 10881;
        public const int HermitPermit = 42;
        public const int WorthlessTrinket = 43;
        public const int WorthlessGewgaw = 44;
        public const int WorthlessKnickKnack = 45;
        public const int Bacon = 8763;
        public const int BaconMachine = 9025;
    }

    public const int HermitDailyStock = 3;
    public const int DayShortenerLeafCost = 222;

    /// <summary>Reads status and inventory (two reads) and the day's markers.</summary>
    public static async Task<DailyRunLive> ReadAsync(KolibriSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var status = await session.Character.GetStatusAsync(refresh: true, cancellationToken).ConfigureAwait(false);
        var inventory = await session.Inventory.GetAsync(refresh: true, cancellationToken).ConfigureAwait(false);
        // The status read above holds the equipment: no extra request.
        var equipment = await session.Inventory.GetEquipmentAsync(refresh: false, cancellationToken).ConfigureAwait(false);
        int Count(params int[] ids) => ids.Sum(id => inventory.Count(id));
        bool Worn(int id) => equipment.Slots.Values.Any(item => item?.Id == id);
        var daily = session.Daily;
        return new DailyRunLive
        {
            Leaves = Count(Ids.InflammableLeaf),
            Clovers = Count(Ids.ElevenLeafClover),
            Permits = Count(Ids.HermitPermit),
            WorthlessItems = Count(Ids.WorthlessTrinket, Ids.WorthlessGewgaw, Ids.WorthlessKnickKnack),
            Bacon = Count(Ids.Bacon),
            BaconMachines = Count(Ids.BaconMachine),
            Meat = status.Meat,
            DayShortenerDoneToday = daily.IsDone(Markers.DayShortener),
            CampfireLeafDoneToday = daily.IsDone(Markers.CampfireLeaf),
            HermitDoneToday = daily.IsDone(Markers.HermitClovers),
            HermitCloversDoneToday = ConfirmedClovers(daily),
            BaconMachineUsedToday = daily.IsDone(Markers.BaconMachine),
            MemeItemsDoneToday = MemeShopCatalog.DailyItems.Select(i => i.Name).Where(n => daily.IsDone(Markers.MemeBuy(n))).ToArray(),
            Adventures = status.Adventures,
            HasToaster = Count(DailyItems.Ids.Toaster) > 0,
            HasFancyChessSet = Count(DailyItems.Ids.FancyChessSet) > 0,
            HasCargoShorts = Count(DailyItems.Ids.CargoCultistShorts) > 0 || Worn(DailyItems.Ids.CargoCultistShorts),
            HasVipKey = Count(DailyItems.Ids.ClanVipLoungeKey) > 0,
            ToasterDoneToday = daily.IsDone(DailyItems.Markers.Toaster),
            FancyChessDoneToday = daily.IsDone(DailyItems.Markers.FancyChess),
            CargoPocketDoneToday = daily.IsDone(DailyItems.Markers.CargoPocket),
            KlawPullsDoneToday = Enumerable.Range(1, DailyItems.VipKlawDailyPulls).Where(p => daily.IsDone(DailyItems.Markers.VipKlaw(p))).ToArray()
        };
    }

    /// <summary>Smallest leaf count with any reward (flaming leaflet fight at 11 leaves); below it burning is pointless.</summary>
    public const int CampfireMinUsefulLeaves = 11;

    /// <summary>Why the campfire step is not planned (null when the Day Shortener is planned or the step is skipped by the user).</summary>
    public static string? CampfireSkipReason(DailyRunLive live, DailyRunOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(live);
        options ??= new DailyRunOptions();
        if (options.IsExcluded("campfire") || live.DayShortenerDoneToday) return null;
        if (live.Leaves < CampfireMinUsefulLeaves)
            return $"zu wenige Blätter (n<{CampfireMinUsefulLeaves}), Verbrennen lohnt nicht";
        if (live.Leaves < DayShortenerLeafCost + options.CampfireReserve)
            return $"Blätter ({live.Leaves}) reichen noch nicht für den Day Shortener ({DayShortenerLeafCost}+{options.CampfireReserve} Reserve)";
        return null;
    }

    /// <summary>The 0.4 bridge plan (AutoCombatDailyBridge.BuildSteps), pure.</summary>
    public static IReadOnlyList<DailyStep> Plan(DailyRunLive live, DailyRunOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(live);
        options ??= new DailyRunOptions();
        var skipCampfire = options.IsExcluded("campfire");
        var skipHermit = options.IsExcluded("hermit");
        var skipShop = options.IsExcluded("internet-shop");
        var leaves = skipCampfire ? 0 : live.Leaves;
        var steps = new List<DailyStep>();

        // 1. Campfire: burning a single leaf yields nothing worthwhile (the first reward milestone is 11 leaves, the
        //    Day Shortener needs 222), so only the Day Shortener is planned. See CampfireSkipReason for the preview note.
        if (!(skipCampfire || live.DayShortenerDoneToday) && leaves >= DayShortenerLeafCost + options.CampfireReserve)
            steps.Add(new DailyStep("choice", "choice-day-shortner", DayShortenerLeafCost.ToString(CultureInfo.InvariantCulture)));

        // 2. Hermit: count trades confirmed today towards the requested daily total.
        if (!skipHermit && !live.HermitDoneToday)
        {
            var target = Math.Clamp(options.HermitCloversWanted, 0, HermitDailyStock);
            var remaining = target - live.HermitCloversDoneToday;
            for (var i = 0; i < Math.Min(remaining, live.WorthlessItems); i++)
                steps.Add(new DailyStep("choice", "choice-clover", "1") { HermitCloverTarget = target });
        }

        // 3. Internet Meme Shop: push the machine once, then the once-per-day items while BACON lasts.
        var shop = new DailyInternetShopPlanner().Plan(new InternetShopSnapshot
        {
            AlreadyPulledItemIds = live.MemeItemsDoneToday,
            AvailableMeat = live.Meat,
            Bacon = skipShop ? 0 : live.Bacon,
            BaconMachineOwned = !skipShop && live.BaconMachines > 0,
            BaconMachineUsedToday = live.BaconMachineUsedToday
        });
        foreach (var shopStep in shop.Steps)
            steps.Add(new DailyStep("choice", shopStep.ChoiceId, shopStep.OptionId));

        // 4. Daily items the account owns: toaster, VIP Klaw, Cargo pocket (only when configured), chess (only opt-in).
        steps.AddRange(DailyItems.Plan(live, options));
        return steps;
    }

    /// <summary>
    /// Today's Internet Meme Shop (0.4: LibKolibri KolibriClient.RunMemeShopAsync): optionally buys the missing BACON in
    /// the mall first (price ceiling and Meat reserve apply), then pushes the BACON machine once and buys each daily item
    /// that is still open. Progress is kept in the day's markers, so a second call the same KoL day only does what is left.
    /// An unclear BACON purchase stops before the shop steps; an unclear shop step stops the run.
    /// </summary>
    public static async Task<MemeShopRun> RunMemeShopAsync(
        KolibriSession session, bool topUpBacon = false, long maxBaconPrice = 1_500, long meatReserve = 200_000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var live = await ReadAsync(session, cancellationToken).ConfigureAwait(false);
        var bacon = live.Bacon;
        var bought = 0;
        long spent = 0;
        PurchaseStatus? baconStatus = null;
        if (topUpBacon)
        {
            var needed = DailyInternetShopPlanner.BaconShortfall(live.MemeItemsDoneToday, bacon, live.BaconMachines > 0, live.BaconMachineUsedToday);
            if (needed > 0)
            {
                var order = new PurchaseOrder(MemeShopCatalog.BaconItemId, needed, maxBaconPrice) { MeatReserve = meatReserve };
                var quote = await session.Mall.QuoteAsync(order, cancellationToken).ConfigureAwait(false);
                var result = await session.Mall.BuyAsync(quote, cancellationToken).ConfigureAwait(false);
                baconStatus = result.Status;
                bought = result.QuantityBought;
                spent = result.MeatSpent;
                bacon += bought;
                // A BACON purchase whose answer is unknown may be booked: stop before the shop steps.
                if (result.Status == PurchaseStatus.Unclear)
                    return new MemeShopRun("Unclear", Array.Empty<MemeShopStep>(), bought, spent, Math.Max(1, quote.TotalCost - spent), true)
                    { BaconStatus = baconStatus };
            }
        }
        var plan = new DailyInternetShopPlanner().Plan(new InternetShopSnapshot
        {
            AlreadyPulledItemIds = live.MemeItemsDoneToday,
            Bacon = bacon,
            BaconMachineOwned = live.BaconMachines > 0,
            BaconMachineUsedToday = live.BaconMachineUsedToday
        });
        var pulls = new FreePulls(session);
        var steps = new List<MemeShopStep>();
        var unclear = false;
        foreach (var step in plan.Steps)
        {
            var (accepted, message, stepUnclear) = await ExecuteMemeStepAsync(session, pulls,
                new DailyStep("choice", step.ChoiceId, step.OptionId), cancellationToken).ConfigureAwait(false);
            steps.Add(new MemeShopStep(step.Description, accepted, message, stepUnclear));
            if (stepUnclear) { unclear = true; break; }
        }
        return new MemeShopRun(unclear ? "Unclear" : plan.Verdict, steps, bought, spent, 0, unclear) { BaconStatus = baconStatus };
    }

    /// <summary>Sends the planned steps. Each step is one request (two for the leaves: visit, then burn).</summary>
    public static Task<DailyRunResult> ExecuteAsync(KolibriSession session, IReadOnlyList<DailyStep> steps, CancellationToken cancellationToken = default)
        => ExecuteAsync(session, steps, null, cancellationToken);

    /// <summary>Sends the planned steps. <paramref name="options"/> provides the Hermit daily target and Cargo price lookups.</summary>
    public static async Task<DailyRunResult> ExecuteAsync(KolibriSession session, IReadOnlyList<DailyStep> steps, DailyRunOptions? options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(steps);
        var choiceSteps = steps.Where(s => s.Kind == "choice").ToArray();
        var itemSteps = steps.Where(s => s.Kind == "daily").ToArray();
        if (choiceSteps.Length == 0 && itemSteps.Length == 0) return new DailyRunResult(false, 0, 0, Array.Empty<string>());
        var pulls = new FreePulls(session);
        var failed = new List<string>();
        var executed = 0;

        foreach (var step in choiceSteps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (step.Id is DailyInternetShopPlanner.MachineChoiceId or DailyInternetShopPlanner.BuyChoiceId)
            {
                var meme = await ExecuteMemeStepAsync(session, pulls, step, cancellationToken).ConfigureAwait(false);
                if (meme.Accepted) executed++;
                continue;
            }
            if (step.Id is not ("choice-clover" or "choice-campfire" or "choice-day-shortner"))
            {
                failed.Add($"{step.Id}:{step.Option} (unbekannter Schritt)");
                continue;
            }
            try
            {
                var clover = step.Id == "choice-clover";
                var leaves = step.Id == "choice-day-shortner" ? DayShortenerLeafCost : 1;
                using var tradeLock = clover
                    ? await session.Daily.LockActionAsync(Markers.HermitClovers, cancellationToken).ConfigureAwait(false)
                    : null;
                var cloversDone = 0;
                if (clover)
                {
                    var target = Math.Clamp(options?.HermitCloversWanted ?? step.HermitCloverTarget ?? HermitDailyStock, 0, HermitDailyStock);
                    cloversDone = ConfirmedClovers(session.Daily);
                    if (cloversDone >= target) continue;
                    if (step.Option is not (null or "1"))
                        throw new InvalidOperationException("Pro Schritt ist genau ein Kleeblatt vorgesehen.");
                }
                else if (step.Option is { } amount && (!int.TryParse(amount, out var parsed) || parsed != leaves))
                    throw new InvalidOperationException($"Dieser Schritt erfordert genau {leaves} Blätter.");
                var result = clover ? await pulls.TradeCloverAsync(cancellationToken).ConfigureAwait(false)
                    : await pulls.BurnLeavesAsync(leaves, cancellationToken).ConfigureAwait(false);
                if (!result.IsSuccess)
                    failed.Add($"{step.Id}:{step.Option} (http-{result.StatusCode})");
                else if (!Confirmed(step.Id, result.Body))
                {
                    // HTTP success alone is not the reward: without it on the page no daily marker is set.
                    var reason = clover && result.Body.Contains("Hermit Permit", StringComparison.OrdinalIgnoreCase)
                        ? "KoL verlangt eine Hermit Permit."
                        : "unklar: KoLs Antwort bestätigt den Erhalt nicht. Kein Tagesmarker gesetzt.";
                    failed.Add($"{step.Id}:{step.Option} ({reason})");
                }
                else
                {
                    executed++;
                    if (step.Id == "choice-day-shortner") session.Daily.MarkDone(Markers.DayShortener);
                    if (step.Id == "choice-campfire") session.Daily.MarkDone(Markers.CampfireLeaf);
                    if (clover)
                    {
                        var count = cloversDone + 1;
                        session.Daily.MarkDone(Markers.HermitClover(count));
                        if (count == HermitDailyStock) session.Daily.MarkDone(Markers.HermitClovers);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                failed.Add($"{step.Id}:{step.Option} ({exception.Message})");
            }
        }
        var reports = new List<DailyStepReport>();
        var klawUsedUp = false;
        foreach (var step in itemSteps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (klawUsedUp && step.Id == DailyItems.StepIds.VipKlaw) continue;
            try
            {
                var outcome = await DailyItems.ExecuteAsync(session, step, cancellationToken, options).ConfigureAwait(false);
                reports.Add(new DailyStepReport(step.Id, step.Option, outcome.Confirmed ? "done" : outcome.Already ? "already" : "failed",
                    outcome.Message, outcome.Gains));
                if (outcome.Confirmed) executed++;
                else if (!outcome.Already) failed.Add($"{step.Id}:{step.Option} ({outcome.Message})");
                // Already done today (KoL said so, marker set): neither submitted nor failed; the other Klaw pulls are not sent.
                if (outcome.Already && step.Id == DailyItems.StepIds.VipKlaw) klawUsedUp = true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                failed.Add($"{step.Id}:{step.Option} ({exception.Message})");
                reports.Add(new DailyStepReport(step.Id, step.Option, "failed", exception.Message, Array.Empty<string>()));
            }
        }
        return new DailyRunResult(executed > 0, executed + failed.Count, executed, failed) { Steps = reports };
    }

    private static int ConfirmedClovers(DailyMarkers daily)
    {
        var done = daily.DoneToday();
        if (done.Contains(Markers.HermitClovers)) return HermitDailyStock;
        return Enumerable.Range(1, HermitDailyStock).Count(trade => done.Contains(Markers.HermitClover(trade)));
    }

    /// <summary>Whether KoL's answer shows the clover, Day Shortener or campfire reward.</summary>
    private static bool Confirmed(string stepId, string body)
    {
        var item = stepId switch
        {
            "choice-clover" => "11-leaf clover",
            "choice-day-shortner" => "day shortener",
            _ => null
        };
        return item is not null
            ? DailyGains.Parse(body).Any(gain => gain.Equals(item, StringComparison.OrdinalIgnoreCase))
            : body.Contains("You acquire", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One meme shop step (0.4: AutoCombatDailyBridge.ExecuteMemeStepDetailedAsync).</summary>
    private static async Task<(bool Accepted, string Message, bool Unclear)> ExecuteMemeStepAsync(
        KolibriSession session, FreePulls pulls, DailyStep step, CancellationToken cancellationToken)
    {
        var sent = false;
        try
        {
            var page = await pulls.ReadMemeShopAsync(cancellationToken).ConfigureAwait(false);
            // Without BACON the shop page is just "Where's the bacon?", so the machine step takes the session's hash.
            var pwd = page.Pwd;
            if (string.IsNullOrWhiteSpace(pwd) && step.Id == DailyInternetShopPlanner.MachineChoiceId)
                pwd = await session.GetPasswordHashAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(pwd))
                return (false, "No request hash available (the meme shop is closed without BACON, or the session expired).", false);

            if (step.Id == DailyInternetShopPlanner.MachineChoiceId)
            {
                sent = true;
                var pushed = await pulls.UseBaconMachineAsync(pwd, cancellationToken).ConfigureAwait(false);
                if (pushed.Accepted || pushed.AlreadyUsed) session.Daily.MarkDone(Markers.BaconMachine);
                return (pushed.Accepted, pushed.BodySnippet, !pushed.Accepted && !pushed.AlreadyUsed && pushed.StatusCode >= 500);
            }

            var item = step.Option ?? "";
            var price = MemeShopCatalog.DailyItems.FirstOrDefault(i => string.Equals(i.Name, item, StringComparison.OrdinalIgnoreCase)).Price;
            if (price <= 0) return (false, $"'{item}' is not a known daily meme shop item.", false);
            var row = page.Find(item);
            if (row is null)
            {
                // KoL removes a daily item from the shop once it was bought.
                session.Daily.MarkDone(Markers.MemeBuy(item));
                return (false, $"'{item}' is not on offer; already bought today.", false);
            }
            if (page.Bacon is { } bacon && bacon < price)
                return (false, $"Only {bacon} BACON for '{item}' ({price} needed).", false);
            sent = true;
            var bought = await pulls.BuyMemeShopRowAsync(row.RowId, pwd, cancellationToken).ConfigureAwait(false);
            if (bought.Accepted) session.Daily.MarkDone(Markers.MemeBuy(item));
            return (bought.Accepted, bought.BodySnippet, !bought.Accepted && bought.StatusCode >= 500);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            return (false, exception.Message, sent);
        }
    }
}

/// <summary>The final KoL answer and the destination, if the session followed a redirect.</summary>
internal sealed record RawAnswer(int StatusCode, string Body, string? Location)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;
}

internal sealed record FreePullResult(int StatusCode, bool Accepted, string BodySnippet, bool AlreadyUsed = false);

/// <summary>
/// The daily requests of 0.4's FreePullDispatcher on the public session API.
/// </summary>
internal sealed class FreePulls(KolibriSession session)
{
    private async Task<RawAnswer> GetAsync(string pathAndQuery, CancellationToken ct)
    {
        try { return Answer(pathAndQuery, await session.GetPageAsync(pathAndQuery, cancellationToken: ct).ConfigureAwait(false)); }
        catch (UnexpectedPageException e) when (e.Page.Kind == PageKind.Error) { return new RawAnswer(e.Page.StatusCode, e.Page.Html, null); }
    }

    private async Task<RawAnswer> PostAsync(string pathAndQuery, IReadOnlyDictionary<string, string> form, CancellationToken ct)
    {
        try { return Answer(pathAndQuery, await session.PostAsync(pathAndQuery, form, cancellationToken: ct).ConfigureAwait(false)); }
        catch (UnexpectedPageException e) when (e.Page.Kind == PageKind.Error) { return new RawAnswer(e.Page.StatusCode, e.Page.Html, null); }
    }

    private static RawAnswer Answer(string requested, Page page)
    {
        var asked = "/" + requested.TrimStart('/');
        var location = string.Equals(page.Url.PathAndQuery, asked, StringComparison.OrdinalIgnoreCase)
            ? null : page.Url.PathAndQuery.TrimStart('/');
        return new RawAnswer(page.StatusCode, page.Html, location);
    }

    public Task<RawAnswer> TradeCloverAsync(CancellationToken ct)
        => PostAsync("hermit.php", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["action"] = "trade",
            ["whichitem"] = DailyRun.Ids.ElevenLeafClover.ToString(CultureInfo.InvariantCulture),
            ["quantity"] = "1"
        }, ct);

    /// <summary>Visits the pile of burning leaves first (KoL only accepts the choice there), then burns 1 or 222 leaves.</summary>
    public async Task<RawAnswer> BurnLeavesAsync(int leaves, CancellationToken ct)
    {
        if (leaves != DailyRun.DayShortenerLeafCost && leaves != 1)
            throw new ArgumentOutOfRangeException(nameof(leaves), "KoL burns either one leaf (one day) or 222 leaves (one Day Shortener).");
        var visit = await GetAsync("campground.php?preaction=leaves", ct).ConfigureAwait(false);
        if (!visit.IsSuccess) return visit;
        var amount = leaves.ToString(CultureInfo.InvariantCulture);
        var form = BurningLeavesPage.BurnForm(visit.Body, amount)
            ?? throw new InvalidOperationException("KoL zeigt kein Formular zum Verbrennen von Blättern.");
        var page = BurningLeavesPage.Parse(visit.Body);
        if (leaves > page.Leaves)
            throw new InvalidOperationException($"Für den Brand sind {leaves} Blätter nötig. KoL zeigt {page.Leaves} an.");
        form["leaves"] = amount;
        return await PostAsync("choice.php", form, ct).ConfigureAwait(false);
    }

    public async Task<FreePullResult> UseBaconMachineAsync(string pwd, CancellationToken ct)
    {
        var answer = await GetAsync($"inv_use.php?which=3&whichitem={MemeShopCatalog.InfiniteBaconMachineItemId}&pwd={pwd}", ct)
            .ConfigureAwait(false);
        var gained = answer.IsSuccess && DailyGains.Parse(answer.Body).Any(gain =>
            gain.Equals("BACON", StringComparison.OrdinalIgnoreCase) || gain.EndsWith("× BACON", StringComparison.OrdinalIgnoreCase));
        var text = System.Net.WebUtility.HtmlDecode(Regex.Replace(answer.Body, "<[^>]+>", " "));
        var spent = answer.IsSuccess && BaconMachineEmpty.IsMatch(text);
        return new FreePullResult(answer.StatusCode, gained, Snippet(answer), spent);
    }

    private static readonly Regex BaconMachineEmpty = new(
        @"\bno\s+more\s+bacon\b|\b(?:push|press)\s+the\s+button\b[^.!?]{0,100}\bnothing\s+happens\b|\b(?:bacon|machine)\b[^.!?]{0,100}\b(?:regenerate|until\s+tomorrow|fill\s+back\s+up)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public async Task<MemeShopPage> ReadMemeShopAsync(CancellationToken ct)
        => MemeShopPage.Parse((await GetAsync("shop.php?whichshop=" + MemeShopCatalog.ShopId, ct).ConfigureAwait(false)).Body);

    public Task<FreePullResult> BuyMemeShopRowAsync(int rowId, string pwd, CancellationToken ct)
        => GetResultAsync($"shop.php?whichshop={MemeShopCatalog.ShopId}&action=buyitem&quantity=1&whichrow={rowId.ToString(CultureInfo.InvariantCulture)}&pwd={pwd}", ct);

    private async Task<FreePullResult> GetResultAsync(string path, CancellationToken ct)
    {
        var answer = await GetAsync(path, ct).ConfigureAwait(false);
        // A real change shows "You acquire ..."; an empty or error page is not success.
        return new FreePullResult(answer.StatusCode, answer.IsSuccess && answer.Location is null
            && answer.Body.Contains("acquire", StringComparison.OrdinalIgnoreCase), Snippet(answer));
    }

    private static string Snippet(RawAnswer answer) => string.IsNullOrWhiteSpace(answer.Body)
        ? $"(empty body, HTTP {answer.StatusCode}, location {answer.Location ?? "none"})"
        : Snippet(answer.Body);

    private static readonly string[] Markers =
    {
        "You acquire", "You don't have enough", "not enough meat", "not allowed to visit", "he sends you packing",
        "doesn't have that item", "Hermit Permit required", "left in stock", "You lose", "You gain", "You get",
        "You burn", "burned", "leaves", "flames", "Pile of Burning Leaves"
    };

    private static string Snippet(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "(empty body)";
        foreach (var marker in Markers)
        {
            var index = body.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;
            var start = Math.Max(0, index - 60);
            return body.Substring(start, Math.Min(400, body.Length - start));
        }
        return body.Length > 300 ? body[..300] : body;
    }
}
