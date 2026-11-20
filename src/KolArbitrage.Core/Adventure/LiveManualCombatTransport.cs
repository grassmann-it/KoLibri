using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace KolArbitrage.Core.Adventure;

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
