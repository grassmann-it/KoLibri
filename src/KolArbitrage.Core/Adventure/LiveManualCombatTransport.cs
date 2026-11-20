using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace KolArbitrage.Core.Adventure;

/// <summary>One page KoL returned during a quest.</summary>
public sealed record QuestPage
{
    /// <summary>The URL the page came from.</summary>
    public required Uri Url { get; init; }
    /// <summary>The page markup.</summary>
    public required string Html { get; init; }
    /// <summary>The HTTP status KoL answered with.</summary>
    public int StatusCode { get; init; }

    /// <summary>True when the page is the fight page.</summary>
    public bool IsFight => Url.AbsolutePath.EndsWith("fight.php", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the page is the encounter choice page.</summary>
    public bool IsChoice => Url.AbsolutePath.EndsWith("choice.php", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The live manual-combat transport. It reads and submits KoL's fight pages
/// over the authenticated session and hands the markup to the shared
/// observation parser.
///
/// Everything the transport does is bounded: it reads one page, it posts
/// one action, and it never follows a redirect on its own — the gateway
/// decides whether a redirect means the action was accepted.
/// </summary>
public sealed class LiveManualCombatTransport : IManualCombatTransport, IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly ICombatObservationParser _parser;
    private readonly TimeProvider _clock;
    private bool _disposed;

    public LiveManualCombatTransport(
        HttpClient http,
        Uri? baseUri = null,
        ICombatObservationParser? parser = null,
        TimeProvider? clock = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _baseUri = baseUri ?? new Uri("https://www.kingdomofloathing.com/");
        _parser = parser ?? new KoLCombatObservationParser();
        _clock = clock ?? TimeProvider.System;
    }

    public Task<ManualCombatTransportResponse> InspectAsync(
        ManualCombatTransportRequest request,
        CancellationToken cancellationToken = default)
        => SendAsync(request, cancellationToken, mutating: false);

    public Task<ManualCombatTransportResponse> ReadAsync(
        ManualCombatTransportRequest request,
        CancellationToken cancellationToken = default)
        => SendAsync(request, cancellationToken, mutating: false);

    public Task<ManualCombatTransportResponse> RefreshAsync(
        ManualCombatTransportRequest request,
        CancellationToken cancellationToken = default)
        => SendAsync(request, cancellationToken, mutating: false);

    public Task<ManualCombatTransportResponse> SubmitAsync(
        ManualCombatTransportRequest request,
        CancellationToken cancellationToken = default)
        => SendAsync(request, cancellationToken, mutating: true);

    public Task<ManualCombatTransportResponse> SubmitActionAsync(
        ManualCombatTransportRequest request,
        CancellationToken cancellationToken = default)
        => SendAsync(request, cancellationToken, mutating: true);

    /// <summary>
    /// Starts an adventure. KoL answers with a redirect to the encounter
    /// choice page, so the transport follows it and returns that page.
    /// </summary>
    public async Task<QuestPage> StartAdventureAsync(
        int snarfblat,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var uri = new Uri(_baseUri, $"adventure.php?snarfblat={snarfblat}");
        using var response = await _http
            .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var target = response.Headers.Location is { } location
            ? new Uri(uri, location)
            : uri;
        var html = await ReadAsync(target, cancellationToken).ConfigureAwait(false);
        return new QuestPage
        {
            Url = target,
            Html = html,
            StatusCode = (int)response.StatusCode
        };
    }

    /// <summary>
    /// Submits an encounter choice. The choice id and the option number come
    /// from the encounter page; KoL answers with the encounter or the fight.
    /// </summary>
    public async Task<QuestPage> ChooseEncounterAsync(
        int whichChoice,
        int option,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var uri = new Uri(_baseUri, "choice.php");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["whichchoice"] = whichChoice.ToString(CultureInfo.InvariantCulture),
            ["option"] = option.ToString(CultureInfo.InvariantCulture)
        };
        // KoL's choice form carries a per-page token; without it the
        // submission is ignored.
        if (!string.IsNullOrWhiteSpace(password)) fields["pwd"] = password;
        using var content = new FormUrlEncodedContent(fields);
        using var response = await _http
            .PostAsync(uri, content, cancellationToken)
            .ConfigureAwait(false);
        var target = response.Headers.Location is { } location
            ? new Uri(uri, location)
            : uri;
        var html = await ReadAsync(target, cancellationToken).ConfigureAwait(false);
        return new QuestPage
        {
            Url = target,
            Html = html,
            StatusCode = (int)response.StatusCode
        };
    }

    /// <summary>Reads the current fight page. When no fight is running
    /// KoL redirects to the encounter page, so the redirect is followed and
    /// the page that answers is returned.</summary>
    public async Task<QuestPage> ReadFightAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var uri = new Uri(_baseUri, "fight.php");
        using var response = await _http
            .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var target = response.Headers.Location is { } location
            ? new Uri(uri, location)
            : uri;
        var html = await ReadAsync(target, cancellationToken).ConfigureAwait(false);
        return new QuestPage
        {
            Url = target,
            Html = html,
            StatusCode = (int)response.StatusCode
        };
    }

    /// <summary>Submits one fight action and returns the next page. KoL's
    /// fight form posts to fight.php with the action name and carries the
    /// fight's confirmation token in the URL — without it the round does
    /// not advance.</summary>
    public async Task<QuestPage> SubmitFightActionAsync(
        string action,
        string? itemId,
        int quantity,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["action"] = action
        };
        if (!string.IsNullOrWhiteSpace(itemId))
        {
            fields["whichitem"] = itemId!;
            if (quantity > 1)
                fields["quantity"] = quantity.ToString(CultureInfo.InvariantCulture);
        }
        var uri = new Uri(_baseUri, "fight.php");
        using var content = new FormUrlEncodedContent(fields);
        using var response = await _http
            .PostAsync(uri, content, cancellationToken)
            .ConfigureAwait(false);
        // KoL answers the action with the page of the next round — and with
        // the loot, when the fight ends on that action. Reading the page
        // again would lose both: the fight is over by then and the page
        // answers "Not in a Fight".
        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var target = response.Headers.Location is { } location
            ? new Uri(uri, location)
            : uri;
        return new QuestPage
        {
            Url = target,
            Html = html,
            StatusCode = (int)response.StatusCode
        };
    }

    private async Task<string> ReadAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await _http
            .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ManualCombatTransportResponse> SendAsync(
        ManualCombatTransportRequest request,
        CancellationToken cancellationToken,
        bool mutating)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var uri = BuildUri(request);
        using var message = mutating
            ? new HttpRequestMessage(HttpMethod.Post, uri) { Content = BuildForm(request) }
            : new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _http
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(html))).ToLowerInvariant();
        var observation = _parser.Parse(html, response.RequestMessage?.RequestUri);

        return new ManualCombatTransportResponse
        {
            RequestId = request.RequestId,
            CombatId = request.CombatId,
            AdventureId = request.AdventureId,
            Observation = ToManualObservation(observation, request),
            Combat = observation,
            ResponseHash = hash,
            Accepted = response.IsSuccessStatusCode,
            Message = response.IsSuccessStatusCode
                ? null
                : $"KoL answered HTTP {(int)response.StatusCode}.",
            RequestFingerprint = request.ActionFingerprint
        };
    }

    /// <summary>The fight page lives under the adventure's snarfblat; the
    /// combat id carries the encounter the operator picked.</summary>
    private Uri BuildUri(ManualCombatTransportRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.AdventureId))
            return new Uri(_baseUri, $"fight.php?whichfight={Uri.EscapeDataString(request.CombatId)}");
        return new Uri(_baseUri, $"fight.php?whichfight={Uri.EscapeDataString(request.CombatId)}");
    }

    private static FormUrlEncodedContent BuildForm(ManualCombatTransportRequest request)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in request.Fields) fields[field.Key] = field.Value;
        var action = request.Action;
        if (action is not null)
        {
            fields["action"] = action.Kind switch
            {
                ManualCombatActionKind.Skill => "skill",
                ManualCombatActionKind.Item => "useitem",
                ManualCombatActionKind.Run => "runaway",
                _ => "attack"
            };
            if (!string.IsNullOrWhiteSpace(action.Id)) fields["whichitem"] = action.Id!;
            if (action.Quantity > 1) fields["quantity"] = action.Quantity.ToString(CultureInfo.InvariantCulture);
        }
        return new FormUrlEncodedContent(fields);
    }

    /// <summary>Projects the shared observation onto the gateway's own
    /// shape, so the gateway and the policy see the same fight.</summary>
    private static ManualCombatObservation ToManualObservation(
        CombatObservation observation, ManualCombatTransportRequest request)
        => new()
        {
            CombatId = request.CombatId,
            AdventureId = request.AdventureId,
            MonsterId = observation.MonsterId,
            MonsterName = observation.MonsterName,
            Round = observation.Round,
            PlayerHp = observation.PlayerHp ?? 0,
            PlayerMaxHp = observation.PlayerMaxHp ?? 0,
            PlayerMp = observation.PlayerMp ?? 0,
            PlayerMaxMp = observation.PlayerMaxMp ?? 0,
            EnemyHp = observation.EnemyHp ?? 0,
            EnemyMaxHp = observation.EnemyMaxHp ?? 0,
            EnemyMp = observation.EnemyMp ?? 0,
            EnemyMaxMp = observation.EnemyMaxMp ?? 0,
            AvailableActions = observation.AvailableActions
                .Select(action => action.Kind switch
                {
                    CombatActionKind.Skill => (ManualCombatAction)new ManualSkillAction(action.Id ?? string.Empty),
                    CombatActionKind.Item => new ManualItemAction(action.Id ?? string.Empty),
                    CombatActionKind.Run => new ManualRunAction(),
                    _ => new ManualAttackAction()
                })
                .ToArray(),
            Effects = observation.Effects.Select(effect => effect.Name).ToArray(),
            Familiar = observation.Familiar,
            Outcome = observation.Outcome switch
            {
                CombatOutcome.Ongoing => ManualCombatOutcome.Active,
                CombatOutcome.Won => ManualCombatOutcome.Won,
                CombatOutcome.Lost => ManualCombatOutcome.Lost,
                CombatOutcome.Fled => ManualCombatOutcome.Fled,
                _ => ManualCombatOutcome.Unknown
            },
            ResponseHash = observation.ResponseHash
        };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
