using System.Globalization;
using System.Net.Http.Headers;
using KolArbitrage.Core.Basis;

namespace KolArbitrage.Core.Spiel;

/// <summary>
/// Lightweight KoL HTTP client used by the bridge to post one-shot choice
/// requests (free pulls, day-shortner, hermit permit trades). It bypasses
/// the adventure runner because those requests are not part of an
/// adventure chain: KoL accepts a plain <c>POST choice.php?whichchoice=N
/// option=1</c> and returns the new state.
///
/// The dispatcher requires a <see cref="AccountCredentials"/> and reads
/// the cookie from the secret store; it never sends a password.
/// </summary>
public sealed class FreePullDispatcher
{
    /// <summary>Campfire leaf burn — advances the day by one leaf.</summary>
    public const int ChoiceCampfire = 1106;
    /// <summary>Day Shortener — 222 leaves craft one extra day.</summary>
    public const int ChoiceDayShortner = 1110;
    /// <summary>Hermit: open an 11-leaf clover.</summary>
    public const int ChoiceClover = 1100;
    /// <summary>Hermit: buy a wish.</summary>
    public const int ChoiceHermitWish = 1107;
    /// <summary>Number of inflammable leaves one Day Shortener costs.</summary>
    public const int DayShortenerLeafCost = 222;

    private readonly HttpClient _http;
    private readonly Uri _baseUri;

    public FreePullDispatcher(HttpClient http, Uri baseUri)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _baseUri = baseUri ?? throw new ArgumentNullException(nameof(baseUri));
    }

    public static Uri DefaultBaseUri => new("https://www.kingdomofloathing.com/");

    public async Task<FreePullResult> PullAsync(
        AccountCredentials credentials,
        int whichChoice,
        int option,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (string.IsNullOrWhiteSpace(credentials.CookieHeader))
            throw new InvalidOperationException(
                "Free pull requires a logged-in account with a cookie header.");
        if (whichChoice <= 0)
            throw new ArgumentOutOfRangeException(nameof(whichChoice));
        if (option <= 0)
            throw new ArgumentOutOfRangeException(nameof(option));

        var (path, form) = ResolveTarget(whichChoice, option);
        return await PostAsync(credentials, path, form, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Burns <paramref name="leaves"/> inflammable leaves at the
    /// campfire to advance the day. The campground's "Pile of Burning Leaves"
    /// has to be visited first — only then does KoL accept the
    /// <c>choice.php?whichchoice=1510</c> submission. KoLmafia does the same
    /// (BurningLeavesRequest.visit() before the creation request).</summary>
    public async Task<FreePullResult> BurnLeavesAsync(
        AccountCredentials credentials,
        int leaves,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (leaves <= 0)
            throw new ArgumentOutOfRangeException(nameof(leaves));
        // KoL's burning-leaves choice burns exactly the number of leaves we
        // ask for, and it happily takes the whole pile. One leaf advances
        // one day; only the Day Shortener takes a bigger batch (222).
        if (leaves != DayShortenerLeafCost && leaves != 1)
            throw new ArgumentOutOfRangeException(nameof(leaves),
                "KoL burns either one leaf (one day) or 222 leaves (one Day Shortener).");
        var page = await VisitBurningLeavesAsync(credentials, cancellationToken).ConfigureAwait(false);
        if (page is not null && page.Leaves > 0 && leaves > page.Leaves)
            throw new InvalidOperationException(
                $"Cannot burn {leaves} leaves; the pile only holds {page.Leaves}.");
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["whichchoice"] = "1510",
            ["option"] = "1",
            ["leaves"] = leaves.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        // Carry the page's own hidden fields (CSRF-style tokens KoL puts on
        // the form) so the submission is accepted.
        if (page is not null)
        {
            foreach (var field in page.Fields)
                form[field.Key] = field.Value;
            form["whichchoice"] = "1510";
            form["option"] = "1";
            form["leaves"] = leaves.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return await PostAsync(credentials, "choice.php", form, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Visits the campground's Pile of Burning Leaves and parses the
    /// page. KoL only accepts the burning-leaves choice while the operator is
    /// standing at the pile, so this GET has to happen before the
    /// submission (KoLmafia's BurningLeavesRequest.visit() does the same).</summary>
    public async Task<BurningLeavesPage?> VisitBurningLeavesAsync(
        AccountCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (string.IsNullOrWhiteSpace(credentials.CookieHeader))
            throw new InvalidOperationException(
                "Visiting the burning leaves requires a logged-in account.");
        var uri = new Uri(_baseUri, "campground.php?preaction=leaves");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("Cookie", credentials.CookieHeader);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        // KoL answers with a redirect to the choice page. The client is
        // configured not to follow redirects (so cookies never leak
        // cross-host), so we follow the location ourselves.
        if (!response.IsSuccessStatusCode && response.Headers.Location is { } location)
        {
            var target = new Uri(uri, location);
            using var follow = new HttpRequestMessage(HttpMethod.Get, target);
            follow.Headers.Add("Cookie", credentials.CookieHeader);
            using var followed = await _http.SendAsync(follow, cancellationToken).ConfigureAwait(false);
            body = await followed.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        return BurningLeavesPage.Parse(body);
    }

    /// <summary>Diagnostic helper: returns the raw burning-leaves page so the
    /// operator (or a test) can inspect the form KoL renders.</summary>
    public async Task<string> ReadBurningLeavesPageAsync(
        AccountCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var uri = new Uri(_baseUri, "campground.php?preaction=leaves");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("Cookie", credentials.CookieHeader);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Crafts one Day Shortener. It costs
    /// <see cref="DayShortenerLeafCost"/> inflammable leaves and grants one
    /// extra day without burning a leaf.</summary>
    public async Task<FreePullResult> CraftDayShortenerAsync(
        AccountCredentials credentials,
        CancellationToken cancellationToken = default)
        => await BurnLeavesAsync(credentials, DayShortenerLeafCost, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Pushes the Infinite BACON machine (100 BACON, once per day). KoL's own
    /// "use" link is a GET; the request hash comes from the meme shop page.
    /// </summary>
    public async Task<FreePullResult> UseBaconMachineAsync(
        AccountCredentials credentials, string pwd, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(pwd);
        var path = $"inv_use.php?which=3&whichitem={MemeShopCatalog.InfiniteBaconMachineItemId}&pwd={pwd}";
        return await GetActionAsync(credentials, path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the Internet Meme Shop page. Items bought today are no longer listed.</summary>
    public async Task<MemeShopPage> ReadMemeShopAsync(
        AccountCredentials credentials, CancellationToken cancellationToken = default)
        => MemeShopPage.Parse(await ReadMemeShopHtmlAsync(credentials, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// The session's request hash from the inventory page. Needed when the meme
    /// shop page is not available yet (no BACON in the inventory).
    /// </summary>
    public async Task<string?> ReadSessionHashAsync(
        AccountCredentials credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (string.IsNullOrWhiteSpace(credentials.CookieHeader))
            throw new InvalidOperationException("This action needs a logged-in account with a cookie header.");
        // KoL embeds the hash in links ("pwd=<hash>") and in the charpane script
        // ("pwdhash = \"<hash>\""). Try the inventory page first, then the charpane.
        foreach (var page in new[] { "inventory.php?which=3", "charpane.php" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, page));
            request.Headers.Add("Cookie", credentials.CookieHeader);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var found = System.Text.RegularExpressions.Regex.Match(
                body, @"(?:pwd=|pwdhash\s*=\s*[""'])(?<h>[0-9a-fA-F]{16,})");
            if (found.Success) return found.Groups["h"].Value;
        }
        return null;
    }

    /// <summary>Raw HTML of the Internet Meme Shop page (diagnostics).</summary>
    public async Task<string> ReadMemeShopHtmlAsync(
        AccountCredentials credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (string.IsNullOrWhiteSpace(credentials.CookieHeader))
            throw new InvalidOperationException("The meme shop requires a logged-in account.");
        var uri = new Uri(_baseUri, "shop.php?whichshop=" + MemeShopCatalog.ShopId);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("Cookie", credentials.CookieHeader);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Buys one unit of a shop row, exactly as the shop's Buy button does (a GET).</summary>
    public async Task<FreePullResult> BuyMemeShopRowAsync(
        AccountCredentials credentials, int rowId, string pwd, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(pwd);
        if (rowId <= 0) throw new ArgumentOutOfRangeException(nameof(rowId));
        var path = $"shop.php?whichshop={MemeShopCatalog.ShopId}&action=buyitem&quantity=1&whichrow={rowId.ToString(CultureInfo.InvariantCulture)}&pwd={pwd}";
        return await GetActionAsync(credentials, path, cancellationToken).ConfigureAwait(false);
    }

    private async Task<FreePullResult> GetActionAsync(
        AccountCredentials credentials, string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credentials.CookieHeader))
            throw new InvalidOperationException("This action needs a logged-in account with a cookie header.");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, path));
        request.Headers.Add("Cookie", credentials.CookieHeader);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var snippet = string.IsNullOrWhiteSpace(body)
            ? $"(empty body, HTTP {(int)response.StatusCode}, location {response.Headers.Location?.ToString() ?? "none"})"
            : ExtractMeaningfulSnippet(body);
        // A real change shows "You acquire ..."; an empty or error page is not success.
        var accepted = response.IsSuccessStatusCode
            && body.Contains("acquire", StringComparison.OrdinalIgnoreCase);
        return new FreePullResult(
            RequestId: Guid.NewGuid().ToString("N"),
            StatusCode: (int)response.StatusCode,
            Accepted: accepted,
            BodySnippet: snippet,
            Location: response.Headers.Location?.ToString());
    }

    private async Task<FreePullResult> PostAsync(
        AccountCredentials credentials,
        string path,
        Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(_baseUri, path);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Add("Cookie", credentials.CookieHeader);
        request.Content = new FormUrlEncodedContent(form);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var snippet = string.IsNullOrWhiteSpace(body)
            ? $"(empty body, HTTP {(int)response.StatusCode}, location {response.Headers.Location?.ToString() ?? "none"})"
            : ExtractMeaningfulSnippet(body);
        return new FreePullResult(
            RequestId: Guid.NewGuid().ToString("N"),
            StatusCode: (int)response.StatusCode,
            Accepted: response.IsSuccessStatusCode,
            BodySnippet: snippet,
            Location: response.Headers.Location?.ToString());
    }

    /// <summary>Extracts the interesting part of KoL's response. The pages
    /// start with a large <c>&lt;script&gt;</c> block, so we skip past it
    /// and surface the text that says what actually happened ("You acquire
    /// 1 11-leaf clover", "You don't have enough Hermit Permits", …).</summary>
    private static string ExtractMeaningfulSnippet(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "(empty body)";
        var markers = new[]
        {
            "You acquire", "You don't have enough", "not enough meat",
            "not allowed to visit", "he sends you packing", "doesn't have that item",
            "Hermit Permit required", "left in stock", "You lose", "You gain", "You get",
            "You burn", "burned", "leaves", "flames", "Pile of Burning Leaves"
        };
        foreach (var marker in markers)
        {
            var index = body.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;
            var start = Math.Max(0, index - 60);
            var length = Math.Min(400, body.Length - start);
            return body.Substring(start, length);
        }
        return body.Length > 300 ? body.Substring(0, 300) : body;
    }

    /// <summary>KoL item ids the daily flow needs. These come straight from
    /// KoLmafia's ItemPool so the ids stay in sync with the game data.</summary>
    public static class ItemIds
    {
        /// <summary>Hermit Permit — required to trade with the Hermit.</summary>
        public const int HermitPermit = 42;
        /// <summary>Worthless trinket — the trade currency for permits.</summary>
        public const int WorthlessTrinket = 43;
        /// <summary>11-leaf clover — the daily Hermit item worth opening.</summary>
        public const int ElevenLeafClover = 10881;
        /// <summary>Infinite Bacon Machine — the daily Internet-shop claim.</summary>
        public const int BaconMachine = 9025;
        /// <summary>Lucky Surprise Egg — the daily Internet-shop claim.</summary>
        public const int LuckySurpriseEgg = 995;
        /// <summary>Internet Bacon — the daily Internet-shop claim.</summary>
        public const int InternetBacon = 8763;
    }

    /// <summary>Maps a KoL numeric choice id onto the page that actually
    /// performs the daily claim, plus the form fields that page expects.
    /// The pages and field names mirror KoLmafia's request classes:
    /// BurningLeavesRequest posts choice.php with whichchoice=1510 plus a
    /// `leaves` count, HermitRequest posts hermit.php?action=trade with
    /// whichitem/quantity.</summary>
    private static (string Path, Dictionary<string, string> Form) ResolveTarget(
        int whichChoice, int option)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal);
        switch (whichChoice)
        {
            case ChoiceCampfire:
                // Campfire: burn leaves to advance the day. KoLmafia's
                // BurningLeavesRequest posts choice.php with
                // whichchoice=1510, option=1 and the leaf count. Burning a
                // single leaf advances one day; 222 leaves craft a Day
                // Shortener instead.
                form["whichchoice"] = "1510";
                form["option"] = "1";
                form["leaves"] = option.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                return ("choice.php", form);
            case ChoiceDayShortner:
                // 222 inflammable leaves craft one Day Shortener, which
                // grants an extra day without burning a leaf.
                form["whichchoice"] = "1510";
                form["option"] = "1";
                form["leaves"] = DayShortenerLeafCost.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                return ("choice.php", form);
            case ChoiceClover:
                // Hermit: trade one worthless item for an 11-leaf clover.
                // The Hermit charges one worthless item per clover and
                // needs a Hermit Permit for the trade itself. autopermit=on
                // lets KoL hand out the permit automatically (paid for by
                // one worthless item), which is what KoLmafia does.
                form["action"] = "trade";
                form["whichitem"] = ItemIds.ElevenLeafClover.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                form["quantity"] = option.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                form["autopermit"] = "on";
                return ("hermit.php?autopermit=on", form);
            case ChoiceHermitWish:
                // Hermit: buy a wish (a sealed circlet).
                form["action"] = "trade";
                form["whichitem"] = ItemIds.ElevenLeafClover.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                form["quantity"] = "1";
                form["autopermit"] = "on";
                return ("hermit.php?autopermit=on", form);
            default:
                form["whichchoice"] = whichChoice.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                form["option"] = option.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                return ($"choice.php?whichchoice={whichChoice}", form);
        }
    }

    /// <summary>Trades worthless items for Hermit Permits. KoL's Hermit
    /// grants a permit automatically when the request carries
    /// <c>autopermit=on</c> — KoLmafia appends that flag to hermit.php so
    /// the operator never has to buy permits by hand. One permit costs one
    /// worthless item.</summary>
    public async Task<FreePullResult> BuyPermitAsync(
        AccountCredentials credentials,
        int quantity = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (string.IsNullOrWhiteSpace(credentials.CookieHeader))
            throw new InvalidOperationException(
                "Permit purchase requires a logged-in account with a cookie header.");
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity));

        // autopermit=on makes the Hermit hand out a permit (paid for by a
        // worthless item) before the trade is processed.
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["action"] = "trade",
            ["whichitem"] = ItemIds.HermitPermit.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["quantity"] = quantity.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["autopermit"] = "on"
        };
        var uri = new Uri(_baseUri, "hermit.php?autopermit=on");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Add("Cookie", credentials.CookieHeader);
        request.Content = new FormUrlEncodedContent(form);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var snippet = ExtractMeaningfulSnippet(body);
        return new FreePullResult(
            RequestId: Guid.NewGuid().ToString("N"),
            StatusCode: (int)response.StatusCode,
            Accepted: response.IsSuccessStatusCode,
            BodySnippet: response.Headers.Location is { } loc
                ? $"[{response.StatusCode}] -> {loc} | {snippet}"
                : snippet,
            Location: response.Headers.Location?.ToString());
    }

    /// <summary>Companion read-only call used by the audit-reader CLI to
    /// inspect what KoL rendered when the operator visited a daily page.
    /// Returns the first 600 characters of the HTML body.</summary>
    public async Task<string> ProbeAsync(
        AccountCredentials credentials,
        int whichChoice,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (string.IsNullOrWhiteSpace(credentials.CookieHeader))
            throw new InvalidOperationException("Probe requires a logged-in account.");
        if (whichChoice <= 0)
            throw new ArgumentOutOfRangeException(nameof(whichChoice));
        var uri = new Uri(_baseUri, $"choice.php?whichchoice={whichChoice}");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("Cookie", credentials.CookieHeader);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return body.Length > 0 ? body.Substring(0, Math.Min(600, body.Length)) : "";
    }

    /// <summary>Maps a free-pull <c>ChoiceStepSpec</c> to its KoL numeric
    /// <c>whichchoice</c>. Returns 0 when the choice id is not a known
    /// free pull — callers must handle that case (the bounded runner or a
    /// follow-up helper picks up unknown choices).</summary>
    public static int WhichChoiceFor(string choiceId)
    {
        if (string.IsNullOrWhiteSpace(choiceId)) return 0;
        return choiceId.ToLowerInvariant() switch
        {
            "choice-campfire" => ChoiceCampfire,
            "choice-day-shortner" => ChoiceDayShortner,
            "choice-clover" => ChoiceClover,
            "choice-hermit-wish" => ChoiceHermitWish,
            _ => 0
        };
    }
}

public sealed record FreePullResult(
    string RequestId,
    int StatusCode,
    bool Accepted,
    string BodySnippet,
    string? Location);
