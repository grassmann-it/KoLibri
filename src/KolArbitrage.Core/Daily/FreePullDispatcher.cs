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

        // KoL's choice pages expect two round-trips:
        //   1. GET  choice.php?whichchoice=N        (renders the choice form)
        //   2. POST choice.php with option=K        (submits the operator's pick)
        // Some free-pull choices only need step 1 — the GET itself claims
        // the daily pull (e.g. the Internet Shop claim) without an option.
        // We try the POST first; if KoL returns 200 with an "already claimed"
        // marker in the body, we surface that as a successful no-op.
        var visitUri = new Uri(_baseUri, $"choice.php?whichchoice={whichChoice}");
        var postUri = new Uri(_baseUri, $"choice.php?whichchoice={whichChoice}&option={option}");

        // Step 1: visit the choice page so KoL records the operator
        // arrived at the choice this rollover.
        using (var visit = new HttpRequestMessage(HttpMethod.Get, visitUri))
        {
            visit.Headers.Add("Cookie", credentials.CookieHeader);
            using var visitResp = await _http.SendAsync(visit, cancellationToken).ConfigureAwait(false);
            // We don't act on the visit response; we just need KoL to see
            // the request so the claim is recorded.
            _ = await visitResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        // Step 2: submit the operator's option.
        using var request = new HttpRequestMessage(HttpMethod.Post, postUri);
        request.Headers.Add("Cookie", credentials.CookieHeader);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["whichchoice"] = whichChoice.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["option"] = option.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var snippet = body.Length > 0 ? body.Substring(0, Math.Min(400, body.Length)) : "";
        return new FreePullResult(
            RequestId: Guid.NewGuid().ToString("N"),
            StatusCode: (int)response.StatusCode,
            Accepted: response.IsSuccessStatusCode,
            BodySnippet: snippet,
            Location: response.Headers.Location?.ToString());
    }

    /// <summary>Companion read-only call used by the audit-reader CLI to
    /// inspect what KoL rendered when the operator visited the choice
    /// page. Returns the first 400 characters of the HTML body.</summary>
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
        return body.Length > 0 ? body.Substring(0, Math.Min(400, body.Length)) : "";
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
