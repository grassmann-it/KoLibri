using System.Text.Json;
using KoLibri.Core.Basis;

namespace KoLibri.Core.Spiel;

public sealed record KoLTimedEffect(string Name, int TurnsRemaining);

public sealed record KoLStatusSnapshot(
    string? PlayerName,
    int Adventures,
    long Meat,
    int Level,
    DateTimeOffset FetchedAt,
    bool IsStale = false)
{
    // Optional combat fields keep the original positional contract intact.
    // Older status endpoints may omit them; callers must then treat recovery
    // as blocked until a richer snapshot is available.
    public int CurrentHp { get; init; }
    public int MaximumHp { get; init; }
    public int CurrentMp { get; init; }
    public int MaximumMp { get; init; }
    public IReadOnlyCollection<string> Effects { get; init; } = Array.Empty<string>();
    public int? Fullness { get; init; }
    public int? Inebriety { get; init; }
    public int? Spleen { get; init; }
    public int? StoragePullsRemaining { get; init; }
    public int? PathId { get; init; }
    public string? PathName { get; init; }
    public int? Ascensions { get; init; }
    public int? RoninTurnsRemaining { get; init; }
    public bool? Hardcore { get; init; }
    public IReadOnlyList<KoLTimedEffect> TimedEffects { get; init; } = Array.Empty<KoLTimedEffect>();
    public string? PlayerId { get; init; }
    public string? FamiliarId { get; init; }
    public string? ClassId { get; init; }
    public IReadOnlyDictionary<string, string> Equipment { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record KoLInventoryItem(
    string ItemId,
    string Name,
    int Quantity);

public sealed record KoLInventorySnapshot(
    IReadOnlyList<KoLInventoryItem> Items,
    DateTimeOffset FetchedAt,
    bool IsStale = false);

public sealed record AccountReadPolicy
{
    public TimeSpan CacheTtl { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan MaxStale { get; init; } = TimeSpan.FromHours(12);
    public TimeSpan MinimumRequestInterval { get; init; } = TimeSpan.FromSeconds(1);
    public KoLSecurityPolicy Security { get; init; } = new();
    public KoLSecurityPolicy SecurityPolicy
    {
        get => Security;
        init => Security = value;
    }
    public IReadOnlyCollection<string> AllowedHosts
    {
        get => Security.AllowedHosts;
        init => Security = Security with { AllowedHosts = value };
    }

    public void Validate()
    {
        if (CacheTtl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(CacheTtl));
        if (MaxStale < CacheTtl) throw new ArgumentException("MaxStale must be at least CacheTtl.", nameof(MaxStale));
        if (MinimumRequestInterval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(MinimumRequestInterval));
        (Security ?? throw new ArgumentException("A KoL account HTTP security policy is required.", nameof(Security))).Validate();
    }
}

public interface IAccountReadClient
{
    Task<KoLStatusSnapshot> FetchStatusAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);
    Task<KoLInventorySnapshot> FetchInventoryAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);
}

/// <summary>
/// Read-only API adapter for status and inventory. It never sends a mutating
/// request, and it keeps the last good snapshot for short outages.
/// </summary>
public sealed class KoLAccountReadClient : IAccountReadClient
{
    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly AccountReadPolicy _policy;
    private readonly KoLSecurityPolicy _security;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly object _cacheGate = new();
    private DateTimeOffset? _lastRequest;
    private KoLStatusSnapshot? _status;
    private KoLInventorySnapshot? _inventory;
    private readonly Func<string, string?>? _itemNameResolver;

    public KoLAccountReadClient(
        HttpClient httpClient,
        Uri? baseUri = null,
        AccountReadPolicy? policy = null,
        TimeProvider? clock = null,
        KoLSecurityPolicy? securityPolicy = null,
        Func<string, string?>? itemNameResolver = null)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _baseUri = baseUri ?? new Uri("https://www.kingdomofloathing.com/", UriKind.Absolute);
        _policy = policy ?? new AccountReadPolicy();
        if (securityPolicy is not null)
            _policy = _policy with { Security = securityPolicy };
        _policy.Validate();
        _security = _policy.Security;
        _security.EnsureAllowed(_baseUri, "KoL account base");
        _clock = clock ?? TimeProvider.System;
        _itemNameResolver = itemNameResolver;
    }

    public async Task<KoLStatusSnapshot> FetchStatusAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        lock (_cacheGate)
        {
            if (!forceRefresh && _status is { } cached && now - cached.FetchedAt <= _policy.CacheTtl)
                return cached;
        }

        try
        {
            var json = await GetJsonAsync(KoLRoutes.Status("status"), cancellationToken).ConfigureAwait(false);
            var snapshot = ParseStatus(json, _clock.GetUtcNow());
            lock (_cacheGate) _status = snapshot;
            return snapshot;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested
            && exception is not KoLHtmlResponseException { Kind: KoLHtmlResponseKind.LoginPage })
        {
            lock (_cacheGate)
            {
                if (_status is { } stale && now - stale.FetchedAt <= _policy.MaxStale)
                    return stale with { IsStale = true };
            }
            throw;
        }
    }

    public async Task<KoLInventorySnapshot> FetchInventoryAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        lock (_cacheGate)
        {
            if (!forceRefresh && _inventory is { } cached && now - cached.FetchedAt <= _policy.CacheTtl)
                return cached;
        }

        try
        {
            var json = await GetJsonAsync(KoLRoutes.Status("inventory"), cancellationToken).ConfigureAwait(false);
            var snapshot = ParseInventory(json, _clock.GetUtcNow());
            lock (_cacheGate) _inventory = snapshot;
            return snapshot;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested
            && exception is not KoLHtmlResponseException { Kind: KoLHtmlResponseKind.LoginPage })
        {
            lock (_cacheGate)
            {
                if (_inventory is { } stale && now - stale.FetchedAt <= _policy.MaxStale)
                    return stale with { IsStale = true };
            }
            throw;
        }
    }

    public Task<KoLInventorySnapshot> FetchClosetAsync(CancellationToken cancellationToken = default)
        => FetchItemStoreAsync("closet", cancellationToken);

    public Task<KoLInventorySnapshot> FetchStorageAsync(CancellationToken cancellationToken = default)
        => FetchItemStoreAsync("storage", cancellationToken);

    private async Task<KoLInventorySnapshot> FetchItemStoreAsync(string store, CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync(KoLRoutes.Status(store), cancellationToken).ConfigureAwait(false);
        return ParseInventory(json, _clock.GetUtcNow());
    }

    public async Task<KoLQuestLogSnapshot> FetchQuestLogAsync(CancellationToken cancellationToken = default)
    {
        var active = await ReadAccountPageAsync("questlog.php?which=1", cancellationToken).ConfigureAwait(false);
        var completed = await ReadAccountPageAsync("questlog.php?which=2", cancellationToken).ConfigureAwait(false);
        return KoLQuestAccess.Parse(active, completed);
    }

    public Task<string> FetchBatHoleMapAsync(CancellationToken cancellationToken = default)
        => ReadAccountPageAsync("bathole.php", cancellationToken);

    public Task<string> FetchFriarsPageAsync(CancellationToken cancellationToken = default)
        => ReadAccountPageAsync("friars.php", cancellationToken);

    public async Task<KoLClanProfile> FetchClanProfileAsync(CancellationToken cancellationToken = default)
    {
        var status = await FetchStatusAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (status.PlayerId is not { Length: > 0 } id || id.Any(character => character is < '0' or > '9'))
            throw new InvalidOperationException("KoL status did not include the character ID.");

        var html = await ReadAccountPageAsync($"showplayer.php?who={id}", cancellationToken).ConfigureAwait(false);
        if (KoLHtmlResponseClassifier.Classify(html) == KoLHtmlResponseKind.LoginPage)
            throw new KoLHtmlResponseException(KoLHtmlResponseKind.LoginPage, "KoL returned a login page.");

        return KoLClanProfileParser.Parse(html, id);
    }

    public async Task<IReadOnlyList<KoLClanMember>> FetchClanMembersAsync(
        string clanId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clanId);
        if (clanId.Any(character => character is < '0' or > '9'))
            throw new ArgumentException("Clan ID must contain only digits.", nameof(clanId));

        var members = new Dictionary<string, KoLClanMember>(StringComparer.Ordinal);
        for (var page = 0; ; page++)
        {
            var route = FormattableString.Invariant($"showclan.php?whichclan={clanId}&page={page}");
            var html = await ReadAccountPageAsync(route, cancellationToken).ConfigureAwait(false);
            if (KoLHtmlResponseClassifier.Classify(html) == KoLHtmlResponseKind.LoginPage)
                throw new KoLHtmlResponseException(KoLHtmlResponseKind.LoginPage, "KoL returned a login page.");

            var previousCount = members.Count;
            foreach (var member in KoLClanProfileParser.ParseMembers(html))
                members.TryAdd(member.PlayerId, member);

            if (members.Count == previousCount)
                throw new InvalidOperationException("KoL did not return a readable clan roster page.");
            if (!html.Contains("next page &gt;&gt;", StringComparison.OrdinalIgnoreCase))
                break;
        }

        var ranksHtml = await ReadAccountPageAsync("clan_detailedroster.php", cancellationToken).ConfigureAwait(false);
        if (KoLHtmlResponseClassifier.Classify(ranksHtml) == KoLHtmlResponseKind.LoginPage)
            throw new KoLHtmlResponseException(KoLHtmlResponseKind.LoginPage, "KoL returned a login page.");
        var ranks = KoLClanProfileParser.ParseRanks(ranksHtml);
        return members.Values.Select(member => ranks.TryGetValue(member.Name, out var rank)
            ? member with { Rank = rank }
            : member).ToArray();
    }

    public async Task<KoLClanLoungeSnapshot> FetchClanLoungeAsync(CancellationToken cancellationToken = default)
    {
        var ground = await ReadClanLoungeFloorAsync(1, cancellationToken).ConfigureAwait(false);
        if (ground is null || !KoLClanLoungeParser.IsFloor(ground, 1))
            return new(false, []);

        var attic = await ReadClanLoungeFloorAsync(2, cancellationToken).ConfigureAwait(false);
        if (attic is null || !KoLClanLoungeParser.IsFloor(attic, 2))
            throw new InvalidOperationException("KoL did not return the clan lounge attic.");

        return new(true, KoLClanLoungeParser.ParseFacilities(ground, 1)
            .Concat(KoLClanLoungeParser.ParseFacilities(attic, 2))
            .DistinctBy(facility => facility.Action, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public async Task<bool> UseClanLookingGlassAsync(string sessionHash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionHash);
        var before = await ReadLookingGlassPotionCountAsync(cancellationToken).ConfigureAwait(false);
        await SubmitClanLoungeActionAsync("lookingglass", "2", sessionHash, cancellationToken).ConfigureAwait(false);
        return await ReadLookingGlassPotionCountAsync(cancellationToken).ConfigureAwait(false) > before;
    }

    public async Task<KoLCrimboTreeResult> UseClanCrimboTreeAsync(
        string sessionHash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionHash);
        var html = await SubmitClanLoungeActionAsync("crimbotree", null, sessionHash, cancellationToken).ConfigureAwait(false);
        return KoLClanLoungeParser.ParseCrimboTree(html);
    }

    public async Task<KoLClanKlawResult> PlayClanKlawAsync(
        string sessionHash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionHash);
        var html = await SubmitClanLoungeActionAsync("klaw", null, sessionHash, cancellationToken).ConfigureAwait(false);
        return KoLClanLoungeParser.ParseKlaw(html);
    }

    public async Task<string> UseClanHotTubAsync(string sessionHash, bool allowRemovingCurse,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionHash);
        var ground = await ReadClanLoungeFloorAsync(1, cancellationToken).ConfigureAwait(false);
        var before = ground is null ? null : KoLClanLoungeParser.ReadHotTubSoaks(ground);
        if (before is null) return "Hot Tub availability could not be verified.";
        if (before == 0) return "No Hot Tub soaks left today.";

        if (!allowRemovingCurse)
        {
            var status = await FetchStatusAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
            if (status.IsStale) return "Live effects could not be verified.";
            if (status.Effects.Any(effect => effect is "Once-Cursed" or "Twice-Cursed" or "Thrice-Cursed"))
                return "The Hot Tub would remove a Cursed effect. Allow curse removal to proceed.";
        }

        var html = await SubmitClanLoungeActionAsync("hottub", null, sessionHash, cancellationToken).ConfigureAwait(false);
        var after = KoLClanLoungeParser.ReadHotTubSoaks(html);
        if (after is null)
        {
            ground = await ReadClanLoungeFloorAsync(1, cancellationToken).ConfigureAwait(false);
            after = ground is null ? null : KoLClanLoungeParser.ReadHotTubSoaks(ground);
        }
        return after < before ? $"Hot Tub soak used. {after} left today." : "KoL did not confirm a Hot Tub soak.";
    }

    public async Task<KoLClanPoolResult> PlayClanPoolAsync(
        string sessionHash, KoLClanPoolStance stance, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionHash);
        if (!Enum.IsDefined(stance)) throw new ArgumentOutOfRangeException(nameof(stance));
        var html = await SubmitClanLoungeActionAsync("poolgame", "2", sessionHash, cancellationToken, ("stance", ((int)stance).ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .ConfigureAwait(false);
        return KoLClanLoungeParser.ParsePoolGame(html);
    }

    public async Task<KoLClanShowerResult> TakeClanShowerAsync(
        string sessionHash, KoLClanShowerTemperature temperature, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionHash);
        if (!Enum.IsDefined(temperature)) throw new ArgumentOutOfRangeException(nameof(temperature));
        var html = await SubmitClanLoungeActionAsync("takeshower", "2", sessionHash, cancellationToken,
            ("temperature", ((int)temperature).ToString(System.Globalization.CultureInfo.InvariantCulture))).ConfigureAwait(false);
        return KoLClanLoungeParser.ParseShower(html);
    }

    public async Task<KoLClanSwimResult> SwimInClanPoolAsync(
        string sessionHash, KoLClanSwimWorkout workout, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionHash);
        var subaction = workout switch
        {
            KoLClanSwimWorkout.Laps => "laps",
            KoLClanSwimWorkout.Sprints => "submarine",
            _ => throw new ArgumentOutOfRangeException(nameof(workout))
        };
        var html = await SubmitClanLoungeActionAsync("goswimming", "2", sessionHash, cancellationToken,
            ("subaction", subaction)).ConfigureAwait(false);
        return KoLClanLoungeParser.ParseSwim(html, workout);
    }

    public async Task<KoLClanPoolChoice> ReadClanPoolChoiceAsync(CancellationToken cancellationToken = default)
        => KoLClanLoungeParser.ParsePoolChoice(
            await ReadAccountPageAsync("choice.php", cancellationToken).ConfigureAwait(false));

    public async Task<KoLClanPoolChoice> CannonballAsync(string sessionHash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionHash);
        var html = await SubmitClanLoungeActionAsync("goswimming", "2", sessionHash, cancellationToken,
            ("subaction", "screwaround"), followPoolRedirect: true).ConfigureAwait(false);
        return KoLClanLoungeParser.ParsePoolChoice(html);
    }

    public async Task<KoLClanPoolChoice> MoveInClanPoolAsync(
        string sessionHash, KoLClanPoolMove move, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionHash);
        var action = move switch
        {
            KoLClanPoolMove.Handstand => "flip",
            KoLClanPoolMove.Dive => "treasure",
            KoLClanPoolMove.Blink => "blink",
            KoLClanPoolMove.Leave => "leave",
            _ => throw new ArgumentOutOfRangeException(nameof(move))
        };
        if (!(await ReadClanPoolChoiceAsync(cancellationToken).ConfigureAwait(false)).IsActive)
            return new(false, null);

        var html = await SubmitClanLoungeFormAsync("choice.php", new Dictionary<string, string>
        {
            ["whichchoice"] = "585", ["option"] = "1", ["action"] = action, ["pwd"] = sessionHash
        }, true, cancellationToken).ConfigureAwait(false);
        return KoLClanLoungeParser.ParsePoolChoice(html, move == KoLClanPoolMove.Dive);
    }

    public async Task<string?> ReadClanPhotocopyMonsterAsync(CancellationToken cancellationToken = default)
    {
        const string photocopyDescriptionId = "835898159";
        var html = await ReadAccountPageAsync($"desc_item.php?whichitem={photocopyDescriptionId}", cancellationToken)
            .ConfigureAwait(false);
        if (KoLHtmlResponseClassifier.Classify(html) == KoLHtmlResponseKind.LoginPage)
            throw new KoLHtmlResponseException(KoLHtmlResponseKind.LoginPage, "KoL returned a login page.");
        return KoLClanLoungeParser.ParsePhotocopiedMonster(html);
    }

    public async Task<bool> TransferClanFaxAsync(
        string sessionHash, bool send, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionHash);
        var html = await SubmitClanLoungeActionAsync(send ? "sendfax" : "receivefax", "2", sessionHash,
            cancellationToken, usePreaction: true).ConfigureAwait(false);
        return html.Contains(send ? "Your photocopy slowly slides into the machine" : "top half of a document prints out",
            StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> SubmitClanLoungeActionAsync(
        string action, string? floor, string sessionHash, CancellationToken cancellationToken,
        (string Field, string Value)? option = null, bool followPoolRedirect = false, bool usePreaction = false)
    {
        var fields = new Dictionary<string, string> { [option is not null || usePreaction ? "preaction" : "action"] = action, ["pwd"] = sessionHash };
        if (option is { } selectedOption)
            fields[selectedOption.Field] = selectedOption.Value;
        if (floor is not null) fields["whichfloor"] = floor;
        return await SubmitClanLoungeFormAsync("clan_viplounge.php", fields, followPoolRedirect, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string> SubmitClanLoungeFormAsync(string route, Dictionary<string, string> fields,
        bool followPoolRedirect, CancellationToken cancellationToken)
    {
        var uri = new Uri(_baseUri, route);
        _security.EnsureAllowed(uri, "Clan lounge action");
        using var form = new FormUrlEncodedContent(fields);

        try
        {
            using var response = await _http.PostAsync(uri, form, cancellationToken).ConfigureAwait(false);
            _security.EnsureResponseUri(uri, response.RequestMessage?.RequestUri);
            RejectLoginRedirect(uri, response);
            if (followPoolRedirect && (int)response.StatusCode is 302 or 303 && response.Headers.Location is { } location)
            {
                var target = _security.ResolveRedirect(uri, location.ToString());
                if (target.Authority != uri.Authority || target.AbsolutePath is not ("/choice.php" or "/clan_viplounge.php"))
                    throw new InvalidOperationException("Unexpected clan pool redirect.");
                return await ReadAccountPageAsync(target.PathAndQuery, cancellationToken).ConfigureAwait(false);
            }
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (KoLHtmlResponseClassifier.Classify(html) == KoLHtmlResponseKind.LoginPage)
                throw new KoLHtmlResponseException(KoLHtmlResponseKind.LoginPage, "KoL returned a login page.");
            return html;
        }
        finally
        {
            Invalidate();
        }
    }

    private async Task<int> ReadLookingGlassPotionCountAsync(CancellationToken cancellationToken)
    {
        const string drinkMePotionId = "4508";
        var inventory = await FetchInventoryAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
        if (inventory.IsStale) throw new InvalidOperationException("Live inventory is unavailable.");
        return inventory.Items.FirstOrDefault(item => item.ItemId == drinkMePotionId)?.Quantity ?? 0;
    }

    private async Task<string?> ReadClanLoungeFloorAsync(int floor, CancellationToken cancellationToken)
    {
        var route = floor == 2 ? "clan_viplounge.php?whichfloor=2" : "clan_viplounge.php";
        var uri = new Uri(_baseUri, route);
        _security.EnsureAllowed(uri, "Clan lounge");
        using var response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        _security.EnsureResponseUri(uri, response.RequestMessage?.RequestUri);
        RejectLoginRedirect(uri, response);
        if (!response.IsSuccessStatusCode && response.Headers.Location is { } location
            && _security.ResolveRedirect(uri, location.ToString()).AbsolutePath == "/clan_signup.php")
            return null;

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (KoLHtmlResponseClassifier.Classify(html) == KoLHtmlResponseKind.LoginPage)
            throw new KoLHtmlResponseException(KoLHtmlResponseKind.LoginPage, "KoL returned a login page.");
        return html;
    }

    public async Task<KoLClanStashSnapshot> FetchClanStashAsync(CancellationToken cancellationToken = default)
    {
        var html = await ReadAccountPageAsync("clan_stash.php", cancellationToken).ConfigureAwait(false);
        if (KoLHtmlResponseClassifier.Classify(html) == KoLHtmlResponseKind.LoginPage)
            throw new KoLHtmlResponseException(KoLHtmlResponseKind.LoginPage, "KoL returned a login page.");
        return KoLClanStashParser.Parse(html);
    }

    public Task<bool> TakeClanStashItemAsync(string itemId, int quantity, string sessionHash,
        CancellationToken cancellationToken = default)
        => TransferClanStashItemAsync(itemId, quantity, sessionHash, deposit: false, cancellationToken);

    public Task<bool> DepositClanStashItemAsync(string itemId, int quantity, string sessionHash,
        CancellationToken cancellationToken = default)
        => TransferClanStashItemAsync(itemId, quantity, sessionHash, deposit: true, cancellationToken);

    private async Task<bool> TransferClanStashItemAsync(string itemId, int quantity, string sessionHash,
        bool deposit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionHash);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        if (itemId.Any(character => character is < '0' or > '9'))
            throw new ArgumentException("Item ID must contain only digits.", nameof(itemId));

        var uri = new Uri(_baseUri, "clan_stash.php");
        _security.EnsureAllowed(uri, "Clan stash");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["action"] = deposit ? "addgoodies" : "takegoodies",
            [deposit ? "item1" : "whichitem"] = itemId,
            [deposit ? "qty1" : "quantity"] = quantity.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ajax"] = "1", ["pwd"] = sessionHash
        });
        try
        {
            using var response = await _http.PostAsync(uri, form, cancellationToken).ConfigureAwait(false);
            _security.EnsureResponseUri(uri, response.RequestMessage?.RequestUri);
            RejectLoginRedirect(uri, response);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return html.Contains(deposit ? "to the Goodies Hoard" : "You acquire", StringComparison.Ordinal);
        }
        finally
        {
            Invalidate();
        }
    }

    public Task<KoLMailboxSnapshot> FetchInboxAsync(CancellationToken cancellationToken = default)
        => FetchInboxPageAsync(1, cancellationToken);

    public Task<KoLMailboxSnapshot> FetchInboxPageAsync(int page, CancellationToken cancellationToken = default)
        => FetchMailboxPageAsync(KoLMailFolder.Inbox, page, cancellationToken);

    public async Task<KoLMailboxSnapshot> FetchMailboxPageAsync(
        KoLMailFolder folder, int page, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        if (!Enum.IsDefined(folder)) throw new ArgumentOutOfRangeException(nameof(folder));
        var route = FormattableString.Invariant($"messages.php?box={folder}&begin={page}");
        var html = await ReadAccountPageAsync(route, cancellationToken).ConfigureAwait(false);
        if (KoLHtmlResponseClassifier.Classify(html) == KoLHtmlResponseKind.LoginPage)
            throw new KoLHtmlResponseException(KoLHtmlResponseKind.LoginPage, "KoL returned a login page.");
        return KoLMailbox.ParseFolder(html, folder);
    }

    public Task SaveInboxMessageAsync(string id, string sessionHash, CancellationToken cancellationToken = default)
        => ChangeMailboxMessageAsync(KoLMailFolder.Inbox, "save", id, sessionHash, cancellationToken);

    public Task DeleteMailboxMessageAsync(KoLMailFolder folder, string id, string sessionHash,
        CancellationToken cancellationToken = default)
        => ChangeMailboxMessageAsync(folder, "delete", id, sessionHash, cancellationToken);

    private async Task ChangeMailboxMessageAsync(KoLMailFolder folder, string action, string id,
        string sessionHash, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(folder)) throw new ArgumentOutOfRangeException(nameof(folder));
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionHash);
        if (id.Any(character => character is < '0' or > '9'))
            throw new ArgumentException("Message ID must contain only digits.", nameof(id));

        var uri = new Uri(_baseUri, "messages.php");
        _security.EnsureAllowed(uri, "KoLmail");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["box"] = folder.ToString(), ["the_action"] = action, [id] = "on", ["pwd"] = sessionHash
        });
        using var response = await _http.PostAsync(uri, form, cancellationToken).ConfigureAwait(false);
        _security.EnsureResponseUri(uri, response.RequestMessage?.RequestUri);
        RejectLoginRedirect(uri, response);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (KoLHtmlResponseClassifier.Classify(html) == KoLHtmlResponseKind.LoginPage)
            throw new KoLHtmlResponseException(KoLHtmlResponseKind.LoginPage, "KoL returned a login page.");
    }

    public async Task<IReadOnlyList<KoLChatContact>> FetchChatContactsAsync(CancellationToken cancellationToken = default)
    {
        var html = await ReadAccountPageAsync("account_contactlist.php", cancellationToken).ConfigureAwait(false);
        if (KoLHtmlResponseClassifier.Classify(html) == KoLHtmlResponseKind.LoginPage)
            throw new KoLHtmlResponseException(KoLHtmlResponseKind.LoginPage, "KoL returned a login page.");
        return KoLChatParser.ParseContacts(html);
    }

    public async Task<KoLChatBatch> FetchChatAsync(long cursor, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cursor);
        var html = await ReadAccountPageAsync(FormattableString.Invariant(
            $"newchatmessages.php?j=1&lasttime={cursor}"), cancellationToken).ConfigureAwait(false);
        if (KoLHtmlResponseClassifier.Classify(html) == KoLHtmlResponseKind.LoginPage)
            throw new KoLHtmlResponseException(KoLHtmlResponseKind.LoginPage, "KoL returned a login page.");
        return KoLChatParser.Parse(html, cursor);
    }

    public async Task<KoLChatBatch> SendChatAsync(
        string sessionHash, string target, string message, long cursor, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentOutOfRangeException.ThrowIfNegative(cursor);
        target = target.Trim();
        message = message.Trim();
        var channel = target.StartsWith('/');
        var name = channel ? target[1..] : target.Replace(' ', '_');
        if (name.Length == 0 || !name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))
            throw new ArgumentException("Choose a chat channel or enter a player name or ID.", nameof(target));
        if (message.StartsWith('/') || message.Any(character => char.IsControl(character) || character > 255))
            throw new ArgumentException("Enter one line of chat text using characters supported by KoL.", nameof(message));

        var status = await FetchStatusAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (status.IsStale || status.PlayerId is not { Length: > 0 } playerId || !playerId.All(char.IsAsciiDigit))
            throw new InvalidOperationException("KoL did not confirm the active character ID.");
        var graf = channel ? $"/{name} {message}" : $"/msg {name} {message}";
        var encoded = string.Concat(System.Text.Encoding.Latin1.GetBytes(graf)
            .Select(value => $"%{value:X2}"));
        var route = $"submitnewchat.php?j=1&pwd={Uri.EscapeDataString(sessionHash)}&playerid={playerId}&graf={encoded}";
        var reply = await ReadAccountPageAsync(route, cancellationToken).ConfigureAwait(false);
        if (KoLHtmlResponseClassifier.Classify(reply) == KoLHtmlResponseKind.LoginPage)
            throw new KoLHtmlResponseException(KoLHtmlResponseKind.LoginPage, "KoL returned a login page.");
        var batch = KoLChatParser.Parse(reply, cursor, requireCursor: false);
        var expectedChannel = channel ? "/" + name : "Private";
        return batch with
        {
            DeliveryConfirmed = batch.Messages.Any(entry => entry.SenderId == playerId
                && entry.Channel.Equals(expectedChannel, StringComparison.OrdinalIgnoreCase) && entry.Text == message)
        };
    }

    private async Task<string> ReadAccountPageAsync(string route, CancellationToken cancellationToken)
    {
        var uri = new Uri(_baseUri, route);
        _security.EnsureAllowed(uri, "Account page");
        using var response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        _security.EnsureResponseUri(uri, response.RequestMessage?.RequestUri);
        RejectLoginRedirect(uri, response);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private void RejectLoginRedirect(Uri uri, HttpResponseMessage response)
    {
        if (response.Headers.Location is { } location && !response.IsSuccessStatusCode)
        {
            var destination = _security.ResolveRedirect(uri, location.ToString());
            if (destination.AbsolutePath.Equals("/login.php", StringComparison.OrdinalIgnoreCase))
                throw new KoLHtmlResponseException(KoLHtmlResponseKind.LoginPage,
                    "KoL redirected to login; the session is not authenticated.");
        }
    }

    public void Invalidate()
    {
        lock (_cacheGate)
        {
            _status = null;
            _inventory = null;
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string relativePath, CancellationToken cancellationToken)
    {
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lastRequest is { } last)
            {
                var wait = _policy.MinimumRequestInterval - (_clock.GetUtcNow() - last);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            var uri = new Uri(_baseUri, relativePath);
            _security.EnsureAllowed(uri);
            using var response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            _security.EnsureResponseUri(uri, response.RequestMessage?.RequestUri);
            RejectLoginRedirect(uri, response);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var htmlKind = KoLHtmlResponseClassifier.Classify(body);
            if (htmlKind is KoLHtmlResponseKind.LoginPage or KoLHtmlResponseKind.Empty)
                throw new KoLHtmlResponseException(htmlKind,
                    htmlKind == KoLHtmlResponseKind.LoginPage
                        ? "KoL returned a login page; the session is not authenticated."
                        : "KoL returned an empty API response.");
            _lastRequest = _clock.GetUtcNow();
            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException("KoL API returned a non-JSON response; the session may have expired.", exception);
            }
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private static KoLStatusSnapshot ParseStatus(JsonDocument document, DateTimeOffset fetchedAt)
    {
        var root = document.RootElement;
        if (root.TryGetProperty("status", out var nested) && nested.ValueKind == JsonValueKind.Object)
            root = nested;
        var timedEffects = ReadTimedEffects(root);
        return new KoLStatusSnapshot(
            StringValue(root, "name", "player", "username"),
            IntValue(root, "adventures", "adventure", "turns"),
            LongValue(root, "meat", "currency"),
            IntValue(root, "level"),
            fetchedAt)
        {
            CurrentHp = IntValue(root, "currenthp", "current_hp", "hp", "hitpoints", "health"),
            MaximumHp = IntValue(root, "maximumhp", "maximum_hp", "maxhp", "max_hp", "maxhitpoints", "max_health"),
            CurrentMp = IntValue(root, "currentmp", "current_mp", "mp", "mana"),
            MaximumMp = IntValue(root, "maximummp", "maximum_mp", "maxmp", "max_mp", "maxmana"),
            PlayerId = StringValue(root, "playerid"),
            FamiliarId = StringValue(root, "familiarid", "familiar_id", "familiar"),
            ClassId = StringValue(root, "classid", "class_id", "class"),
            Fullness = NullableIntValue(root, "full", "fullness"),
            Inebriety = NullableIntValue(root, "drunk", "drunkenness", "inebriety"),
            Spleen = NullableIntValue(root, "spleen", "spleenuse"),
            StoragePullsRemaining = NullableIntValue(root, "pullsleft"),
            PathId = NullableIntValue(root, "path"),
            PathName = StringValue(root, "pathname"),
            Ascensions = NullableIntValue(root, "ascensions"),
            RoninTurnsRemaining = NullableIntValue(root, "roninleft"),
            Hardcore = NullableIntValue(root, "hardcore") is { } hardcore ? hardcore != 0 : null,
            TimedEffects = timedEffects,
            Effects = StringCollection(root, "effects", "active_effects")
                .Concat(timedEffects.Select(effect => effect.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Equipment = StringMap(root, "equipment", "equipped")
        };
    }

    private KoLInventorySnapshot ParseInventory(JsonDocument document, DateTimeOffset fetchedAt)
    {
        var root = document.RootElement;
        if (root.TryGetProperty("inventory", out var nested)) root = nested;
        var items = new List<KoLInventoryItem>();
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object) continue;
                var quantity = IntValue(property.Value, "quantity", "count", "amount");
                if (quantity <= 0) continue;
                var name = StringValue(property.Value, "name", "itemname") ?? property.Name;
                var itemId = StringValue(property.Value, "itemid", "id") ?? property.Name;
                items.Add(new KoLInventoryItem(itemId, name, quantity));
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in root.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var quantity = IntValue(entry, "quantity", "count", "amount");
                if (quantity <= 0) continue;
                items.Add(new KoLInventoryItem(
                    StringValue(entry, "itemid", "id") ?? string.Empty,
                    StringValue(entry, "name", "itemname") ?? "Unknown item",
                    quantity));
            }
        }
        // KoL's Mafia-mode payload (api.php?what=inventory&for=KoLibri) returns a
        // flat object whose keys are KoL numeric item ids and whose values are
        // the quantities. The values are mixed: KoL writes most of them as
        // numbers and some of them as strings — {"163":139,"44":"655"}.
        // A reader that only accepts the string form drops half the
        // inventory, so both forms are read.
        if (items.Count == 0 && root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                var quantity = 0;
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    if (!int.TryParse(property.Value.GetString(), out quantity)) continue;
                }
                else if (property.Value.ValueKind == JsonValueKind.Number)
                {
                    if (!int.TryParse(property.Value.GetRawText(), out quantity)) continue;
                }
                else continue;
                if (quantity <= 0) continue;
                var name = _itemNameResolver?.Invoke(property.Name) ?? property.Name;
                items.Add(new KoLInventoryItem(property.Name, name, quantity));
            }
        }
        return new KoLInventorySnapshot(items.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray(), fetchedAt);
    }

    private static string? StringValue(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String) return value.GetString();
            if (value.ValueKind == JsonValueKind.Number) return value.GetRawText();
        }
        return null;
    }

    private static int IntValue(JsonElement element, params string[] names)
        => int.TryParse(StringValue(element, names), out var value) ? value : 0;

    private static int? NullableIntValue(JsonElement element, params string[] names)
        => int.TryParse(StringValue(element, names), out var value) ? value : null;

    private static IReadOnlyList<KoLTimedEffect> ReadTimedEffects(JsonElement root)
    {
        if (!root.TryGetProperty("effects", out var effects) || effects.ValueKind != JsonValueKind.Object)
            return Array.Empty<KoLTimedEffect>();
        var result = new List<KoLTimedEffect>();
        foreach (var effect in effects.EnumerateObject())
        {
            if (effect.Value.ValueKind != JsonValueKind.Array || effect.Value.GetArrayLength() < 2
                || effect.Value[0].ValueKind != JsonValueKind.String)
                continue;
            var name = effect.Value[0].GetString();
            if (!string.IsNullOrWhiteSpace(name) && int.TryParse(effect.Value[1].ToString(), out var turns) && turns >= 0)
                result.Add(new KoLTimedEffect(name, turns));
        }
        return result;
    }

    private static IReadOnlyCollection<string> StringCollection(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Array)
                return value.EnumerateArray()
                    .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item!)
                    .ToArray();
        }
        return Array.Empty<string>();
    }

    private static IReadOnlyDictionary<string, string> StringMap(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object) continue;
            return value.EnumerateObject()
                .Select(property => (property.Name, Value: property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : property.Value.GetRawText()))
                .Where(item => !string.IsNullOrWhiteSpace(item.Value))
                .ToDictionary(item => item.Name, item => item.Value!, StringComparer.OrdinalIgnoreCase);
        }
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static long LongValue(JsonElement element, params string[] names)
        => long.TryParse(StringValue(element, names), out var value) ? value : 0L;
}
