using System.Net.Http.Headers;
using KolArbitrage.Core.Accounts;

namespace KolArbitrage.Core.Daily;

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

        // KoL's daily claims are NOT adventure choices. choice.php only
        // works while an adventure is running ("Whoops! You're not actually
        // in an adventure right now." otherwise). The daily pulls live on
        // their own pages:
        //   * Internet shop free pull → inventory.php?which=4
        //   * Campfire leaf burn      → campfire.php?action=burn
        //   * Hermit clover           → hermit.php?action=clover
        // We dispatch on the numeric choice id and use the matching page.
        var (path, form) = ResolveTarget(whichChoice, option);
        var uri = new Uri(_baseUri, path);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Add("Cookie", credentials.CookieHeader);
        request.Content = new FormUrlEncodedContent(form);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var snippet = body.Length > 0 ? body.Substring(0, Math.Min(600, body.Length)) : "";
        return new FreePullResult(
            RequestId: Guid.NewGuid().ToString("N"),
            StatusCode: (int)response.StatusCode,
            Accepted: response.IsSuccessStatusCode,
            BodySnippet: snippet,
            Location: response.Headers.Location?.ToString());
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
        public const int LuckySurpriseEgg = 53;
        /// <summary>Internet Bacon — the daily Internet-shop claim.</summary>
        public const int InternetBacon = 8763;
    }

    /// <summary>Maps a KoL numeric choice id onto the page that actually
    /// performs the daily claim, plus the form fields that page expects.
    /// The pages and field names mirror KoLmafia's request classes:
    /// HermitRequest uses hermit.php?action=trade with whichitem/quantity,
    /// StorageRequest uses storage.php?action=pull with whichitem1/howmany1.</summary>
    private static (string Path, Dictionary<string, string> Form) ResolveTarget(
        int whichChoice, int option)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal);
        switch (whichChoice)
        {
            case 1119:
                // Lucky Surprise Egg / Internet Bacon live in the storage
                // (free pulls). KoLmafia pulls them with
                // storage.php?action=pull&whichitem1=<id>&howmany1=1.
                // option 1 → Lucky Surprise Egg, option 2 → Internet Bacon.
                form["action"] = "pull";
                form["ajax"] = "1";
                form["whichitem1"] = (option == 2
                    ? ItemIds.InternetBacon
                    : ItemIds.LuckySurpriseEgg).ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                form["howmany1"] = "1";
                return ("storage.php", form);
            case 1118:
                // Infinite Bacon Machine — same storage pull mechanism.
                form["action"] = "pull";
                form["ajax"] = "1";
                form["whichitem1"] = ItemIds.BaconMachine.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                form["howmany1"] = "1";
                return ("storage.php", form);
            case 1106:
                // Campfire: burn one leaf to advance the day. The leaves
                // live in the Getaway Campsite, so the request targets
                // place.php?whichplace=campaway with the campfire action.
                form["whichplace"] = "campaway";
                form["action"] = "campaway_campfire";
                form["quantity"] = "1";
                return ("place.php", form);
            case 1100:
                // Hermit: trade one worthless item for an 11-leaf clover.
                // The Hermit charges one worthless item per clover and
                // needs a Hermit Permit for the trade itself.
                form["action"] = "trade";
                form["whichitem"] = ItemIds.ElevenLeafClover.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                form["quantity"] = option.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                return ("hermit.php", form);
            case 1107:
                // Hermit: buy a wish (a sealed circlet).
                form["action"] = "trade";
                form["whichitem"] = ItemIds.ElevenLeafClover.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                form["quantity"] = "1";
                return ("hermit.php", form);
            default:

                form["whichchoice"] = whichChoice.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                form["option"] = option.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                return ($"choice.php?whichchoice={whichChoice}", form);
        }
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
            // 1106 = burning leaves (campfire). We accept the legacy
            // choice-day-shortner id as a synonym.
            "choice-campfire" => 1106,
            "choice-day-shortner" => 1106,
            // 1100 = Hermit: open an 11-leaf clover.
            "choice-clover" => 1100,
            // 1119 = Daily free pull from the Internet shop (Lucky
            // Surprise Egg). Same choice id is also the way to claim the
            // daily Internet Bacon — KoL hands the operator a different
            // reward depending on the time of day.
            "choice-lucky-surprise-egg" => 1119,
            "choice-internet-bacon" => 1119,
            // 1118 = Infinite Bacon Machine daily claim.
            "choice-bacon-machine" => 1118,
            // 1107 = Hermit: wish for an item.
            "choice-hermit-wish" => 1107,
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
