using System.Net;
using System.Text;
using KoLibri.Core.Basis;

namespace KoLibri.Core.Spiel;

public enum OpenChoiceKind { None, Choice, Fight }

/// <summary>A choice adventure that is currently open on the account.</summary>
public sealed record OpenChoiceState(string ChoiceId, string? Title, IReadOnlyList<ChoiceOptionInfo> Options)
{
    /// <summary>Hidden form fields (for example pwd) needed to submit an option; never shown to users.</summary>
    internal IReadOnlyDictionary<string, string> Fields { get; init; } = new Dictionary<string, string>();
}

/// <summary>Result of looking for an open choice or fight.</summary>
public sealed record OpenChoiceDetection(OpenChoiceKind Kind, OpenChoiceState? Choice)
{
    public static OpenChoiceDetection NoneOpen { get; } = new(OpenChoiceKind.None, null);
    public bool IsOpen => Kind != OpenChoiceKind.None;
    public string Message => Kind switch
    {
        OpenChoiceKind.Choice => "A choice adventure is open. Continue there (or decide with `kolibri choice submit`).",
        OpenChoiceKind.Fight => "A fight is open. Continue there.",
        _ => "No choice or fight is open."
    };
}

/// <summary>
/// Thrown by page clients when a request was redirected to choice.php or fight.php, which KoL does
/// for every page while a choice or fight is open. UIs show <see cref="Exception.Message"/>
/// instead of "302 Found".
/// </summary>
public sealed class OpenChoiceException : Exception
{
    public OpenChoiceException(OpenChoiceKind kind, string? requestedPath = null, OpenChoiceState? state = null)
        : base(kind == OpenChoiceKind.Fight
            ? "A fight is open. Continue there."
            : "A choice adventure is open. Continue there.")
    {
        Kind = kind;
        RequestedPath = requestedPath;
        State = state;
    }

    public OpenChoiceKind Kind { get; }
    public string? RequestedPath { get; }
    public OpenChoiceState? State { get; }
}

/// <summary>Outcome of submitting one option to an open choice.</summary>
public sealed record OpenChoiceSubmitResult(bool Submitted, string Message, OpenChoiceDetection Next);

public static class OpenChoiceRedirect
{
    /// <summary>Which page a redirect response points at (choice.php / fight.php), if any.</summary>
    public static OpenChoiceKind Classify(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if ((int)response.StatusCode is < 300 or > 399 || response.Headers.Location is not { } location)
            return OpenChoiceKind.None;
        return ClassifyPath(location.OriginalString);
    }

    public static OpenChoiceKind ClassifyPath(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return OpenChoiceKind.None;
        var path = location.Split('?', '#')[0].TrimEnd('/');
        var name = path[(path.LastIndexOf('/') + 1)..];
        if (name.Equals("choice.php", StringComparison.OrdinalIgnoreCase)) return OpenChoiceKind.Choice;
        if (name.Equals("fight.php", StringComparison.OrdinalIgnoreCase)) return OpenChoiceKind.Fight;
        return OpenChoiceKind.None;
    }

    /// <summary>Throws <see cref="OpenChoiceException"/> when the response redirects to choice.php/fight.php.</summary>
    public static void ThrowIfOpenChoice(HttpResponseMessage response)
    {
        var kind = Classify(response);
        if (kind != OpenChoiceKind.None)
            throw new OpenChoiceException(kind, response.RequestMessage?.RequestUri?.AbsolutePath);
    }
}

/// <summary>
/// Finds out whether a choice or fight is open and can submit one option. Needs an authenticated
/// client that does not follow redirects (the standard KoL client).
/// </summary>
public sealed class OpenChoiceClient
{
    private const int MaxBodyChars = 512 * 1024;
    private readonly HttpClient _http;
    private readonly Uri _baseUri;

    public OpenChoiceClient(HttpClient http, Uri? baseUri = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _baseUri = baseUri ?? new Uri("https://www.kingdomofloathing.com/");
    }

    /// <summary>Read only: requests main.php, follows a redirect to choice.php/fight.php, and parses the choice page.</summary>
    public async Task<OpenChoiceDetection> DetectAsync(CancellationToken cancellationToken = default)
    {
        using var main = await GetAsync("main.php", cancellationToken).ConfigureAwait(false);
        var kind = OpenChoiceRedirect.Classify(main);
        if (kind == OpenChoiceKind.Fight) return new OpenChoiceDetection(OpenChoiceKind.Fight, null);
        if (kind == OpenChoiceKind.Choice) return await ReadChoicePageAsync(cancellationToken).ConfigureAwait(false);
        // Some pages answer 200 with a choice form directly.
        if (main.IsSuccessStatusCode)
        {
            var html = await ReadAsync(main, cancellationToken).ConfigureAwait(false);
            if (LooksLikeChoicePage(html)) return Parse(html);
        }
        return OpenChoiceDetection.NoneOpen;
    }

    public async Task<OpenChoiceSubmitResult> SubmitAsync(string choiceId, int option, CancellationToken cancellationToken = default)
    {
        var open = await DetectAsync(cancellationToken).ConfigureAwait(false);
        if (open.Kind != OpenChoiceKind.Choice || open.Choice is null)
            return new OpenChoiceSubmitResult(false, open.Message, open);
        var state = open.Choice;
        if (!string.Equals(state.ChoiceId, choiceId?.Trim(), StringComparison.Ordinal))
            return new OpenChoiceSubmitResult(false,
                $"The open choice is {state.ChoiceId}, not {choiceId}. Nothing was sent.", open);
        if (state.Options.All(o => o.Number != option))
            return new OpenChoiceSubmitResult(false, $"Option {option} is not visible on the open choice. Nothing was sent.", open);

        var fields = new Dictionary<string, string>(state.Fields, StringComparer.Ordinal)
        {
            ["whichchoice"] = state.ChoiceId,
            ["option"] = option.ToString()
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "choice.php"))
        {
            Content = new FormUrlEncodedContent(fields)
        };
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var redirect = OpenChoiceRedirect.Classify(response);
        if (!response.IsSuccessStatusCode && redirect == OpenChoiceKind.None)
            return new OpenChoiceSubmitResult(false, $"KoL answered HTTP {(int)response.StatusCode}.", open);
        var next = await DetectAsync(cancellationToken).ConfigureAwait(false);
        return new OpenChoiceSubmitResult(true,
            next.IsOpen ? "Submitted. Another choice or fight is now open." : "Submitted. No choice is open any more.", next);
    }

    private async Task<OpenChoiceDetection> ReadChoicePageAsync(CancellationToken ct)
    {
        using var page = await GetAsync("choice.php", ct).ConfigureAwait(false);
        if (OpenChoiceRedirect.Classify(page) == OpenChoiceKind.Fight)
            return new OpenChoiceDetection(OpenChoiceKind.Fight, null);
        var html = await ReadAsync(page, ct).ConfigureAwait(false);
        var parsed = Parse(html);
        // KoL redirected here, so a choice is open even when we cannot read its options.
        return parsed.IsOpen ? parsed : new OpenChoiceDetection(OpenChoiceKind.Choice, null);
    }

    public static bool LooksLikeChoicePage(string html)
        => html.Contains("name=whichchoice", StringComparison.OrdinalIgnoreCase)
        || html.Contains("name=\"whichchoice\"", StringComparison.OrdinalIgnoreCase)
        || html.Contains("name='whichchoice'", StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses a choice.php body; <see cref="OpenChoiceDetection.NoneOpen"/> when it has no choice form.</summary>
    public static OpenChoiceDetection Parse(string html)
    {
        var encounter = new KoLAdventureEncounterParser().Parse(
            "open-choice", new Uri("https://www.kingdomofloathing.com/choice.php"), html);
        if (encounter.Kind != AdventureEncounterKind.Choice || string.IsNullOrWhiteSpace(encounter.ChoiceId))
            return OpenChoiceDetection.NoneOpen;
        var options = encounter.AvailableChoices
            .Where(x => int.TryParse(x, out _))
            .Select(x => new ChoiceOptionInfo(int.Parse(x),
                encounter.ChoiceLabels.TryGetValue(x, out var label) ? label : x))
            .OrderBy(x => x.Number)
            .ToArray();
        return new OpenChoiceDetection(OpenChoiceKind.Choice,
            new OpenChoiceState(encounter.ChoiceId, encounter.ChoiceTitle, options)
            { Fields = encounter.ExtraFields });
    }

    private Task<HttpResponseMessage> GetAsync(string path, CancellationToken ct)
        => _http.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, path)),
            HttpCompletionOption.ResponseHeadersRead, ct);

    private static async Task<string> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return text.Length > MaxBodyChars ? text[..MaxBodyChars] : text;
    }
}
