using System.Globalization;
using System.Text.RegularExpressions;

namespace KoLibri.Daily;

/// <summary>One planned daily step, as the 0.4 CLI printed it: kind ("choice"/"item"), id, option.</summary>
public sealed record DailyStep(string Kind, string Id, string? Option);

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
    public bool BaconMachineUsedToday { get; init; }
    public IReadOnlyList<string> MemeItemsDoneToday { get; init; } = Array.Empty<string>();
}

/// <summary>What the run should do on top of the live numbers (0.4: CLI flags on DailyRoutineSnapshot).</summary>
public sealed record DailyRunOptions
{
    public int HermitCloversWanted { get; init; }
    /// <summary>"campfire", "hermit", "internet-shop".</summary>
    public IReadOnlySet<string> Skip { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public int CampfireReserve { get; init; } = 3;
}

public enum DailyRunCompleteness { None, Complete, Partial, Failed }

public sealed record DailyRunResult(bool Executed, int AttemptedSteps, int SubmittedSteps, IReadOnlyList<string> FailedSteps)
{
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
        int Count(params int[] ids) => ids.Sum(id => inventory.Count(id));
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
            BaconMachineUsedToday = daily.IsDone(Markers.BaconMachine),
            MemeItemsDoneToday = MemeShopCatalog.DailyItems.Select(i => i.Name).Where(n => daily.IsDone(Markers.MemeBuy(n))).ToArray()
        };
    }

    /// <summary>Smallest leaf count with any reward (flaming leaflet fight at 11 leaves); below it burning is pointless.</summary>
    public const int CampfireMinUsefulLeaves = 11;

    /// <summary>Why the campfire step is not planned (null when the Day Shortener is planned or the step is skipped by the user).</summary>
    public static string? CampfireSkipReason(DailyRunLive live, DailyRunOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(live);
        options ??= new DailyRunOptions();
        if (options.Skip.Contains("campfire") || live.DayShortenerDoneToday) return null;
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
        var skipCampfire = options.Skip.Contains("campfire");
        var skipHermit = options.Skip.Contains("hermit");
        var skipShop = options.Skip.Contains("internet-shop");
        var leaves = skipCampfire ? 0 : live.Leaves;
        var steps = new List<DailyStep>();

        // 1. Campfire: burning a single leaf yields nothing worthwhile (the first reward milestone is 11 leaves, the
        //    Day Shortener needs 222), so only the Day Shortener is planned. See CampfireSkipReason for the preview note.
        if (!(skipCampfire || live.DayShortenerDoneToday) && leaves >= DayShortenerLeafCost + options.CampfireReserve)
            steps.Add(new DailyStep("choice", "choice-day-shortner", DayShortenerLeafCost.ToString(CultureInfo.InvariantCulture)));

        // 2. Hermit: clovers for worthless items, capped by the daily stock of 3.
        var wanted = skipHermit ? 0 : options.HermitCloversWanted;
        if (live.HermitDoneToday)
        {
            // The Hermit's daily clover stock was already bought today.
        }
        else if (wanted > 0 && live.Permits > 0)
        {
            for (var i = 0; i < Math.Min(wanted, HermitDailyStock); i++)
                steps.Add(new DailyStep("choice", "choice-clover", "1"));
        }
        else if (live.Permits == 0 && live.Clovers > 0 && live.WorthlessItems >= 1)
        {
            var tradeRuns = Math.Min(Math.Min(live.Clovers, live.WorthlessItems), HermitDailyStock);
            for (var i = 0; i < tradeRuns; i++)
                steps.Add(new DailyStep("item", "worthless-item", "1"));
            for (var i = 0; i < tradeRuns; i++)
                steps.Add(new DailyStep("choice", "choice-clover", "1"));
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
        return steps;
    }

    /// <summary>Sends the planned steps. Each step is one request (two for the leaves: visit, then burn).</summary>
    public static async Task<DailyRunResult> ExecuteAsync(KolibriSession session, IReadOnlyList<DailyStep> steps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(steps);
        var choiceSteps = steps.Where(s => s.Kind == "choice").ToArray();
        if (choiceSteps.Length == 0) return new DailyRunResult(false, 0, 0, Array.Empty<string>());
        var pulls = new FreePulls(session);
        var failed = new List<string>();
        var executed = 0;

        foreach (var permit in steps.Where(s => s.Kind == "item" && s.Id == "worthless-item"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await pulls.BuyPermitAsync(int.TryParse(permit.Option, out var q) ? q : 1, cancellationToken).ConfigureAwait(false);
                if (result.Accepted) executed++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not ActionDeniedException)
            {
                // As in 0.4: a failed permit purchase shows up through the clover steps that follow.
            }
        }

        foreach (var step in choiceSteps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (step.Id is DailyInternetShopPlanner.MachineChoiceId or DailyInternetShopPlanner.BuyChoiceId)
            {
                var meme = await ExecuteMemeStepAsync(session, pulls, step, cancellationToken).ConfigureAwait(false);
                if (meme.Accepted) executed++;
                continue;
            }
            var whichChoice = FreePulls.WhichChoiceFor(step.Id);
            if (whichChoice <= 0)
            {
                failed.Add($"{step.Id}:{step.Option} (unknown step)");
                continue;
            }
            try
            {
                var option = int.TryParse(step.Option, out var parsed) ? parsed : 1;
                var result = step.Id is "choice-campfire" or "choice-day-shortner"
                    ? await pulls.BurnLeavesAsync(option, cancellationToken).ConfigureAwait(false)
                    : await pulls.PullAsync(whichChoice, option, cancellationToken).ConfigureAwait(false);
                if (result.Accepted)
                {
                    executed++;
                    if (step.Id == "choice-day-shortner") session.Daily.MarkDone(Markers.DayShortener);
                    if (step.Id == "choice-campfire") session.Daily.MarkDone(Markers.CampfireLeaf);
                    if (step.Id == "choice-clover") session.Daily.MarkDone(Markers.HermitClovers);
                }
                else failed.Add($"{step.Id}:{step.Option} (http-{result.StatusCode})");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                failed.Add($"{step.Id}:{step.Option} ({exception.Message})");
            }
        }
        return new DailyRunResult(executed > 0, executed + failed.Count, executed, failed);
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
                var spent = pushed.BodySnippet.Contains("nothing happens", StringComparison.OrdinalIgnoreCase)
                    || pushed.BodySnippet.Contains("no more bacon", StringComparison.OrdinalIgnoreCase)
                    || pushed.BodySnippet.Contains("regenerate", StringComparison.OrdinalIgnoreCase)
                    || pushed.BodySnippet.Contains("until tomorrow", StringComparison.OrdinalIgnoreCase)
                    || pushed.BodySnippet.Contains("fill back up", StringComparison.OrdinalIgnoreCase);
                // KoL answers a successful push with a redirect to the inventory message page.
                var redirected = pushed.StatusCode is >= 300 and < 400
                    && (pushed.Location?.Contains("inventory.php", StringComparison.OrdinalIgnoreCase) ?? false);
                var ok = pushed.Accepted || redirected;
                if (ok || spent) session.Daily.MarkDone(Markers.BaconMachine);
                return (ok, pushed.BodySnippet, !ok && !spent && pushed.StatusCode >= 500);
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

/// <summary>One raw KoL answer: a redirect shows as 3xx with the target, exactly like the 0.4 dispatcher saw it.</summary>
internal sealed record RawAnswer(int StatusCode, string Body, string? Location)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;
}

internal sealed record FreePullResult(int StatusCode, bool Accepted, string BodySnippet, string? Location);

/// <summary>
/// The daily requests of 0.4's FreePullDispatcher on the public session API. The session follows redirects by
/// itself; when the final page differs from the requested one the answer is reported as a 302 to that page, so the
/// 0.4 success rules (a redirected POST is not a success, a redirected machine push is) stay as they were.
/// </summary>
internal sealed class FreePulls(KolibriSession session)
{
    public const int ChoiceCampfire = 1106;
    public const int ChoiceDayShortner = 1110;
    public const int ChoiceClover = 1100;
    public const int ChoiceHermitWish = 1107;

    public static int WhichChoiceFor(string choiceId) => choiceId.ToLowerInvariant() switch
    {
        "choice-campfire" => ChoiceCampfire,
        "choice-day-shortner" => ChoiceDayShortner,
        "choice-clover" => ChoiceClover,
        "choice-hermit-wish" => ChoiceHermitWish,
        _ => 0
    };

    public async Task<RawAnswer> GetAsync(string pathAndQuery, CancellationToken ct)
    {
        try { return Answer(pathAndQuery, await session.GetPageAsync(pathAndQuery, cancellationToken: ct).ConfigureAwait(false)); }
        catch (UnexpectedPageException e) when (e.Page.Kind == PageKind.Error) { return new RawAnswer(e.Page.StatusCode, e.Page.Html, null); }
    }

    public async Task<RawAnswer> PostAsync(string pathAndQuery, IReadOnlyDictionary<string, string> form, CancellationToken ct)
    {
        try { return Answer(pathAndQuery, await session.PostAsync(pathAndQuery, form, cancellationToken: ct).ConfigureAwait(false)); }
        catch (UnexpectedPageException e) when (e.Page.Kind == PageKind.Error) { return new RawAnswer(e.Page.StatusCode, e.Page.Html, null); }
    }

    private static RawAnswer Answer(string requested, Page page)
    {
        var asked = "/" + requested.TrimStart('/');
        return string.Equals(page.Url.PathAndQuery, asked, StringComparison.OrdinalIgnoreCase)
            ? new RawAnswer(page.StatusCode, page.Html, null)
            : new RawAnswer(302, string.Empty, page.Url.PathAndQuery.TrimStart('/'));
    }

    public Task<FreePullResult> PullAsync(int whichChoice, int option, CancellationToken ct)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal);
        string path;
        switch (whichChoice)
        {
            case ChoiceCampfire:
                form["whichchoice"] = "1510"; form["option"] = "1"; form["leaves"] = option.ToString(CultureInfo.InvariantCulture);
                path = "choice.php";
                break;
            case ChoiceDayShortner:
                form["whichchoice"] = "1510"; form["option"] = "1"; form["leaves"] = DailyRun.DayShortenerLeafCost.ToString(CultureInfo.InvariantCulture);
                path = "choice.php";
                break;
            case ChoiceClover:
                form["action"] = "trade"; form["whichitem"] = DailyRun.Ids.ElevenLeafClover.ToString(CultureInfo.InvariantCulture);
                form["quantity"] = option.ToString(CultureInfo.InvariantCulture); form["autopermit"] = "on";
                path = "hermit.php?autopermit=on";
                break;
            case ChoiceHermitWish:
                form["action"] = "trade"; form["whichitem"] = DailyRun.Ids.ElevenLeafClover.ToString(CultureInfo.InvariantCulture);
                form["quantity"] = "1"; form["autopermit"] = "on";
                path = "hermit.php?autopermit=on";
                break;
            default:
                form["whichchoice"] = whichChoice.ToString(CultureInfo.InvariantCulture); form["option"] = option.ToString(CultureInfo.InvariantCulture);
                path = $"choice.php?whichchoice={whichChoice}";
                break;
        }
        return PostResultAsync(path, form, ct);
    }

    /// <summary>Visits the pile of burning leaves first (KoL only accepts the choice there), then burns 1 or 222 leaves.</summary>
    public async Task<FreePullResult> BurnLeavesAsync(int leaves, CancellationToken ct)
    {
        if (leaves != DailyRun.DayShortenerLeafCost && leaves != 1)
            throw new ArgumentOutOfRangeException(nameof(leaves), "KoL burns either one leaf (one day) or 222 leaves (one Day Shortener).");
        var visit = await GetAsync("campground.php?preaction=leaves", ct).ConfigureAwait(false);
        var body = visit.Body;
        if (!visit.IsSuccess && visit.Location is { } location)
            body = (await GetAsync(location, ct).ConfigureAwait(false)).Body;
        var page = BurningLeavesPage.Parse(body);
        if (page is not null && page.Leaves > 0 && leaves > page.Leaves)
            throw new InvalidOperationException($"Cannot burn {leaves} leaves; the pile only holds {page.Leaves}.");
        var form = new Dictionary<string, string>(StringComparer.Ordinal);
        if (page is not null)
            foreach (var field in page.Fields) form[field.Key] = field.Value;
        form["whichchoice"] = "1510";
        form["option"] = "1";
        form["leaves"] = leaves.ToString(CultureInfo.InvariantCulture);
        return await PostResultAsync("choice.php", form, ct).ConfigureAwait(false);
    }

    public Task<FreePullResult> UseBaconMachineAsync(string pwd, CancellationToken ct)
        => GetResultAsync($"inv_use.php?which=3&whichitem={MemeShopCatalog.InfiniteBaconMachineItemId}&pwd={pwd}", ct);

    public async Task<MemeShopPage> ReadMemeShopAsync(CancellationToken ct)
        => MemeShopPage.Parse((await GetAsync("shop.php?whichshop=" + MemeShopCatalog.ShopId, ct).ConfigureAwait(false)).Body);

    public Task<FreePullResult> BuyMemeShopRowAsync(int rowId, string pwd, CancellationToken ct)
        => GetResultAsync($"shop.php?whichshop={MemeShopCatalog.ShopId}&action=buyitem&quantity=1&whichrow={rowId.ToString(CultureInfo.InvariantCulture)}&pwd={pwd}", ct);

    /// <summary>One permit per worthless item (autopermit=on, as KoLmafia does).</summary>
    public async Task<FreePullResult> BuyPermitAsync(int quantity, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["action"] = "trade",
            ["whichitem"] = DailyRun.Ids.HermitPermit.ToString(CultureInfo.InvariantCulture),
            ["quantity"] = quantity.ToString(CultureInfo.InvariantCulture),
            ["autopermit"] = "on"
        };
        var answer = await PostAsync("hermit.php?autopermit=on", form, ct).ConfigureAwait(false);
        var snippet = Snippet(answer.Body);
        return new FreePullResult(answer.StatusCode, answer.IsSuccess,
            answer.Location is { } loc ? $"[{answer.StatusCode}] -> {loc} | {snippet}" : snippet, answer.Location);
    }

    private async Task<FreePullResult> GetResultAsync(string path, CancellationToken ct)
    {
        var answer = await GetAsync(path, ct).ConfigureAwait(false);
        var snippet = string.IsNullOrWhiteSpace(answer.Body)
            ? $"(empty body, HTTP {answer.StatusCode}, location {answer.Location ?? "none"})"
            : Snippet(answer.Body);
        // A real change shows "You acquire ..."; an empty or error page is not success.
        return new FreePullResult(answer.StatusCode, answer.IsSuccess && answer.Body.Contains("acquire", StringComparison.OrdinalIgnoreCase), snippet, answer.Location);
    }

    private async Task<FreePullResult> PostResultAsync(string path, Dictionary<string, string> form, CancellationToken ct)
    {
        var answer = await PostAsync(path, form, ct).ConfigureAwait(false);
        var snippet = string.IsNullOrWhiteSpace(answer.Body)
            ? $"(empty body, HTTP {answer.StatusCode}, location {answer.Location ?? "none"})"
            : Snippet(answer.Body);
        return new FreePullResult(answer.StatusCode, answer.IsSuccess, snippet, answer.Location);
    }

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
