using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using KoLibri.Core.Basis;

namespace KoLibri.Core.Spiel;

/// <summary>One choice of an item-use selection form (the option's value and its label).</summary>
public sealed record ItemChoiceOption(int Value, string Label);

/// <summary>
/// A KoL "use item, then pick one" form: <c>&lt;form action=inv_use.php&gt;</c> with hidden fields and one
/// <c>&lt;select&gt;</c>. Generic for any item that answers its use with such a form.
/// </summary>
public sealed record ItemChoiceForm(
    int WhichItem, string SelectName, IReadOnlyDictionary<string, string> HiddenFields, IReadOnlyList<ItemChoiceOption> Options)
{
    private static readonly Regex FormPattern = new(
        @"<form\b(?<attrs>[^>]*)>(?<body>.*?)</form>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex InputPattern = new(@"<input\b[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex OptionPattern = new(
        @"<option\b[^>]*\bvalue\s*=\s*[""']?(?<v>\d+)[""']?[^>]*>(?<label>[^<]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string? Attr(string tag, string name)
    {
        var m = Regex.Match(tag, $@"\b{name}\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>/]+))", RegexOptions.IgnoreCase);
        return m.Success ? WebUtility.HtmlDecode(m.Groups["v"].Value) : null;
    }

    /// <summary>Finds the inv_use.php form for <paramref name="whichItem"/> with a select named
    /// <paramref name="selectName"/>, or null when the page has none.</summary>
    public static ItemChoiceForm? Parse(string? html, int whichItem, string selectName)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        foreach (Match form in FormPattern.Matches(html))
        {
            var action = Attr(form.Groups["attrs"].Value, "action") ?? "";
            if (!action.Contains("inv_use.php", StringComparison.OrdinalIgnoreCase)) continue;
            var body = form.Groups["body"].Value;
            var hidden = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match input in InputPattern.Matches(body))
            {
                if (!string.Equals(Attr(input.Value, "type"), "hidden", StringComparison.OrdinalIgnoreCase)) continue;
                if (Attr(input.Value, "name") is { Length: > 0 } name) hidden[name] = Attr(input.Value, "value") ?? "";
            }
            if (!hidden.TryGetValue("whichitem", out var which) || which != whichItem.ToString(CultureInfo.InvariantCulture)) continue;
            var select = Regex.Match(body, $@"<select\b[^>]*\bname\s*=\s*[""']?{Regex.Escape(selectName)}[""']?[^>]*>(?<opts>.*?)</select>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!select.Success) continue;
            var options = OptionPattern.Matches(select.Groups["opts"].Value)
                .Select(o => new ItemChoiceOption(int.Parse(o.Groups["v"].Value, CultureInfo.InvariantCulture),
                    WebUtility.HtmlDecode(o.Groups["label"].Value).Trim()))
                .Where(o => o.Value > 0)
                .ToArray();
            return new ItemChoiceForm(whichItem, selectName, hidden, options);
        }
        return null;
    }
}

/// <summary>Counts of the Crimbo 2023 pirate encryption key parts and complete keys.</summary>
public sealed record PirateKeyCounts(int Alpha, int Bravo, int Charlie, int Delta, int Complete)
{
    /// <summary>How many complete keys the parts make (one of each part per key).</summary>
    public int AssemblableSets => Math.Min(Math.Min(Alpha, Bravo), Math.Min(Charlie, Delta));
}

/// <summary>One sent request and what the inventory showed afterwards.</summary>
public sealed record PirateKeyStep(int Number, bool Confirmed, string? Message, IReadOnlyDictionary<int, int> InventoryDelta);

/// <summary>
/// Outcome of a key series. Steps run one request at a time and each one is confirmed by the inventory delta.
/// <see cref="Unclear"/> means a request was sent but its effect could not be read back: check the account
/// before repeating anything.
/// </summary>
public sealed record PirateKeyRunResult(
    string Action, int Requested, int Confirmed, bool Unclear, string? Problem,
    IReadOnlyList<PirateKeyStep> Steps, PirateKeyCounts Before, PirateKeyCounts? After)
{
    public bool Complete => Confirmed == Requested && Problem is null;
}

/// <summary>
/// Crimbo 2023 pirate encryption keys: four tradable parts combine into a complete key, and the complete key
/// becomes one rare Crimbo 2023 item chosen from a form. Every step is one GET and is confirmed by the
/// inventory delta; an unclear step stops the series and is never retried.
/// </summary>
public sealed class PirateKeyClient
{
    public const int Alpha = 11386, Bravo = 11387, Charlie = 11388, Delta = 11389, CompleteKey = 11473;
    public const string RareSelectName = "rare";
    public static readonly IReadOnlyList<int> PartIds = [Alpha, Bravo, Charlie, Delta];

    /// <summary>The 28 options the complete key offers (read live on 2026-11-26).</summary>
    public static readonly IReadOnlyList<ItemChoiceOption> RareOptions =
    [
        new(11452, "ancient Elf dessert spoon"), new(11456, "baby rigging snake"), new(11448, "brown pirate pants"),
        new(11432, "Cocktails of the Age of Sail"), new(11458, "Crimbuccaneer bombjacket"),
        new(11445, "Crimbuccaneer fledges (disintegrating)"), new(11406, "Crimbuccaneer invasion map"),
        new(11404, "Crimbuccaneer lantern"), new(11442, "Crimbuccaneer runebone"), new(11420, "Crimbuccaneer tavern swab"),
        new(11419, "Elf Guard broom"), new(11434, "Elf Guard clipboard"), new(11429, "Elf Guard Field Manual: Culinary Arts"),
        new(11449, "Elf Guard Field Manual: Extortion"), new(11454, "Elf Guard Field Manual: Wilderness Sleeping"),
        new(11446, "Elf Guard fuel tank"), new(11421, "Elf Guard hangover cure"), new(11415, "Elf Guard insignia (general)"),
        new(11437, "Elf Guard red and white beret"), new(11453, "Elf Guard SCUBA tank"), new(11417, "Elf Guard strategic map"),
        new(11457, "Elvish underarmor"), new(11455, "free boots"), new(11439, "gunwale"), new(11447, "massive wrench"),
        new(11451, "punching mirror"), new(11450, "The Encyclopedia of Fruit"), new(11431, "whalegun"),
    ];

    /// <summary>Resolves an id or a name (exact, else a unique part of it) to one of <see cref="RareOptions"/>.</summary>
    public static ItemChoiceOption? ResolveRare(string idOrName, out string? problem)
    {
        problem = null;
        var text = (idOrName ?? "").Trim();
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            var byId = RareOptions.FirstOrDefault(o => o.Value == id);
            if (byId is null) problem = $"{id} is not one of the {RareOptions.Count} items the key offers.";
            return byId;
        }
        var exact = RareOptions.FirstOrDefault(o => string.Equals(o.Label, text, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        var partial = text.Length == 0 ? [] : RareOptions.Where(o => o.Label.Contains(text, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (partial.Length == 1) return partial[0];
        problem = partial.Length == 0
            ? $"'{text}' is not one of the {RareOptions.Count} items the key offers."
            : $"'{text}' matches several items: {string.Join(", ", partial.Select(o => $"{o.Label} ({o.Value})"))}.";
        return null;
    }

    public static int Count(KoLInventorySnapshot inventory, int id)
    {
        var key = id.ToString(CultureInfo.InvariantCulture);
        return inventory.Items.Where(i => i.ItemId == key).Sum(i => i.Quantity);
    }

    public static PirateKeyCounts CountKeys(KoLInventorySnapshot inventory)
        => new(Count(inventory, Alpha), Count(inventory, Bravo), Count(inventory, Charlie), Count(inventory, Delta), Count(inventory, CompleteKey));

    private readonly HttpClient _http;
    private readonly string _passwordHash;
    private readonly KoLSecurityPolicy _security;
    private readonly KoLAccountReadClient _reader;

    public PirateKeyClient(HttpClient http, string passwordHash, KoLSecurityPolicy? security = null, AccountReadPolicy? readPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        _security = security ?? new KoLSecurityPolicy();
        _security.Validate();
        _security.EnsureAllowed(http.BaseAddress ?? throw new ArgumentException("A KoL base address is required.", nameof(http)));
        _http = http;
        _passwordHash = passwordHash;
        _reader = new KoLAccountReadClient(http, http.BaseAddress, readPolicy, securityPolicy: _security);
    }

    /// <summary>Fresh inventory; throws when KoL only gave a stale copy (nothing is decided on old numbers).</summary>
    public async Task<KoLInventorySnapshot> ReadInventoryAsync(CancellationToken cancellationToken = default)
    {
        var inventory = await _reader.FetchInventoryAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
        if (inventory.IsStale) throw new InvalidOperationException("The inventory could not be read fresh; nothing was sent.");
        return inventory;
    }

    public async Task<PirateKeyCounts> ReadCountsAsync(CancellationToken cancellationToken = default)
        => CountKeys(await ReadInventoryAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>Combines <paramref name="count"/> complete keys: one use of the alpha part per key
    /// (<c>inv_use.php?whichitem=11386&amp;ajax=1&amp;pwd=…</c>). Confirmed per key: each part −1, complete key +1.</summary>
    public async Task<PirateKeyRunResult> AssembleAsync(int count, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        var inventory = await ReadInventoryAsync(cancellationToken).ConfigureAwait(false);
        var before = CountKeys(inventory);
        if (before.AssemblableSets < count)
            throw new InvalidOperationException($"Only {before.AssemblableSets} complete set(s) of parts in inventory, {count} requested; nothing was sent.");
        var expected = new Dictionary<int, int> { [Alpha] = -1, [Bravo] = -1, [Charlie] = -1, [Delta] = -1, [CompleteKey] = 1 };
        return await RunAsync("assemble", count, before, inventory, expected,
            _ => Task.FromResult<string?>(null),
            $"inv_use.php?whichitem={Alpha}&ajax=1&pwd={Uri.EscapeDataString(_passwordHash)}",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the rare-item form of the complete key. An open form on <c>main.php</c> is used as is; otherwise the
    /// key is used (<c>inv_use.php?whichitem=11473&amp;pwd=…</c>, which does not consume it), its one-time message
    /// page is followed once, and <c>main.php</c> is read when that page did not carry the form.
    /// </summary>
    public async Task<ItemChoiceForm> GetRareOptionsAsync(CancellationToken cancellationToken = default)
        => await OpenFormAsync(requireKey: true, cancellationToken).ConfigureAwait(false)
           ?? throw new InvalidOperationException("KoL did not show the key's item form.");

    private async Task<ItemChoiceForm?> OpenFormAsync(bool requireKey, CancellationToken cancellationToken)
    {
        var main = await GetAsync("main.php", followOnce: true, cancellationToken).ConfigureAwait(false);
        if (ItemChoiceForm.Parse(main.Body, CompleteKey, RareSelectName) is { } open) return open;
        if (requireKey && Count(await ReadInventoryAsync(cancellationToken).ConfigureAwait(false), CompleteKey) < 1)
            throw new InvalidOperationException("No complete pirate encryption key in inventory and no open key form.");
        var used = await GetAsync($"inv_use.php?whichitem={CompleteKey}&pwd={Uri.EscapeDataString(_passwordHash)}",
            followOnce: true, cancellationToken).ConfigureAwait(false);
        if (ItemChoiceForm.Parse(used.Body, CompleteKey, RareSelectName) is { } fromUse) return fromUse;
        var again = await GetAsync("main.php", followOnce: true, cancellationToken).ConfigureAwait(false);
        return ItemChoiceForm.Parse(again.Body, CompleteKey, RareSelectName);
    }

    /// <summary>
    /// Turns <paramref name="count"/> complete keys into <paramref name="rareItemId"/>, one request per key
    /// (<c>inv_use.php?whichitem=11473&amp;qty=1&amp;pwd=…&amp;rare=ID</c>). Only ids of <see cref="RareOptions"/>
    /// that the live form also offers are sent. Confirmed per key: complete key −1, chosen item +1.
    /// </summary>
    public async Task<PirateKeyRunResult> RedeemAsync(int rareItemId, int count, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        if (RareOptions.All(o => o.Value != rareItemId))
            throw new ArgumentException($"{rareItemId} is not one of the {RareOptions.Count} items the key offers; nothing was sent.", nameof(rareItemId));
        var inventory = await ReadInventoryAsync(cancellationToken).ConfigureAwait(false);
        var before = CountKeys(inventory);
        if (before.Complete < count)
            throw new InvalidOperationException($"Only {before.Complete} complete key(s) in inventory, {count} requested; nothing was sent.");
        var expected = new Dictionary<int, int> { [CompleteKey] = -1, [rareItemId] = 1 };
        return await RunAsync("redeem", count, before, inventory, expected,
            async ct =>
            {
                var form = await OpenFormAsync(requireKey: false, ct).ConfigureAwait(false);
                if (form is null) return "KoL did not show the key's item form; nothing was redeemed.";
                return form.Options.Any(o => o.Value == rareItemId)
                    ? null
                    : $"The key's form does not offer item {rareItemId}; nothing was redeemed.";
            },
            $"inv_use.php?whichitem={CompleteKey}&qty=1&pwd={Uri.EscapeDataString(_passwordHash)}&rare={rareItemId.ToString(CultureInfo.InvariantCulture)}",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<PirateKeyRunResult> RunAsync(
        string action, int count, PirateKeyCounts before, KoLInventorySnapshot inventory, IReadOnlyDictionary<int, int> expected,
        Func<CancellationToken, Task<string?>> prepare, string route, CancellationToken cancellationToken)
    {
        var steps = new List<PirateKeyStep>();
        var confirmed = 0;
        string? problem = null;
        var unclear = false;
        var current = inventory;
        for (var i = 1; i <= count; i++)
        {
            // Preparation (reading the form) happens before the action request: a failure there sends nothing that changes items.
            var refused = await prepare(cancellationToken).ConfigureAwait(false);
            if (refused is not null) { problem = refused; break; }

            Page page;
            try { page = await GetAsync(route, followOnce: true, cancellationToken).ConfigureAwait(false); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
            {
                problem = $"Request {i} got no usable answer ({e.Message}); it may have happened. Check the inventory before repeating.";
                unclear = true;
                break;
            }
            KoLInventorySnapshot after;
            try { after = await ReadInventoryAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException
                or IOException or System.Text.Json.JsonException)
            {
                problem = $"Request {i} was sent but the inventory could not be read back ({e.Message}). Check it before repeating.";
                unclear = true;
                break;
            }
            var ids = expected.Keys.Union(PartIds).Append(CompleteKey).Distinct();
            var delta = ids.Select(id => (id, d: Count(after, id) - Count(current, id))).Where(x => x.d != 0)
                .ToDictionary(x => x.id, x => x.d);
            var message = Excerpt(page.Body);
            var ok = expected.All(e => delta.GetValueOrDefault(e.Key) == e.Value)
                     && delta.Keys.All(expected.ContainsKey);
            steps.Add(new PirateKeyStep(i, ok, message, delta));
            if (!ok)
            {
                problem = delta.Count == 0
                    ? $"KoL did not confirm step {i}: the inventory is unchanged." + Said(message)
                    : $"Step {i} changed the inventory unexpectedly ({string.Join(", ", delta.Select(d => $"{d.Key}:{d.Value:+#;-#}"))})." + Said(message);
                unclear = delta.Count != 0;
                current = after;
                break;
            }
            confirmed++;
            current = after;
        }
        if (problem is not null && count > 1) problem = $"Confirmed {confirmed} of {count}. {problem}";
        return new PirateKeyRunResult(action, count, confirmed, unclear, problem, steps, before, CountKeys(current));
    }

    private static string Said(string? message) => string.IsNullOrWhiteSpace(message) ? "" : $" KoL said: \"{message}\"";

    private sealed record Page(HttpStatusCode Status, string Body, Uri Uri);

    /// <summary>One GET; a redirect is followed once on the same allowlisted host (KoL's one-time message page).
    /// Redirects are never followed further.</summary>
    private async Task<Page> GetAsync(string route, bool followOnce, CancellationToken cancellationToken)
    {
        var uri = new Uri(_http.BaseAddress!, route);
        for (var hop = 0; ; hop++)
        {
            _security.EnsureAllowed(uri, "Item use");
            using var response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            _security.EnsureResponseUri(uri, response.RequestMessage?.RequestUri);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location;
                if (!followOnce || hop > 0 || location is null) return new Page(response.StatusCode, body, uri);
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (!string.Equals(next.Host, uri.Host, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("KoL redirected to another host; not followed.");
                uri = next;
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (KoLHtmlResponseClassifier.Classify(body) == KoLHtmlResponseKind.LoginPage)
                throw new KoLHtmlResponseException(KoLHtmlResponseKind.LoginPage, "KoL returned a login page.");
            return new Page(response.StatusCode, body, uri);
        }
    }

    private static string? Excerpt(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var plain = Regex.Replace(Regex.Replace(html, @"(?is)<(script|style).*?</\1>", " "), "<[^>]+>", " ");
        plain = Regex.Replace(WebUtility.HtmlDecode(plain), @"\s+", " ").Trim();
        return plain.Length == 0 ? null : plain.Length > 300 ? plain[..300] : plain;
    }
}
