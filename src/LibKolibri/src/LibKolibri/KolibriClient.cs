using KoLibri.Core.Basis;
using KoLibri.Core.Markt;
using KoLibri.Core.Spiel;

namespace LibKolibri;

/// <summary>Raised when no usable KoL session exists and none could be renewed.</summary>
public sealed class KolibriLoginRequiredException(string message) : InvalidOperationException(message);

/// <summary>One step of the Internet Meme Shop run and what KoL answered.</summary>
public sealed record MemeShopStepResult(string Description, bool Accepted, string Message, bool Unclear = false);

/// <summary>Outcome of <see cref="KolibriClient.RunMemeShopAsync"/>.</summary>
public sealed record MemeShopRunResult(
    string Verdict,
    IReadOnlyList<MemeShopStepResult> Steps,
    BaconArbitrageExecutor.Outcome? BaconTopUp,
    bool Unclear = false);

/// <summary>
/// The front door of LibKolibri: sign in, read the account, search and buy in the mall, run the
/// daily meme shop and open an adventure session, without touching the core namespaces.
/// </summary>
/// <remarks>
/// Every write needs the caller's explicit call. Nothing here runs by itself, and purchases only
/// happen under a price ceiling and a meat reserve that the caller states.
/// </remarks>
public sealed class KolibriClient : IDisposable
{
    private static readonly Uri KolBase = new("https://www.kingdomofloathing.com/");
    private readonly AccountSessionManager _accounts;
    private HttpClient _http;
    private AccountSession _session;
    private KoLAccountReadClient _read;
    private KoLMallReadClient _mall;

    private KolibriClient(AccountSessionManager accounts, AccountSession session)
    {
        _accounts = accounts;
        _session = session;
        _http = KoLLoginClient.CreateAuthenticatedClient(session.Credentials, KolBase);
        _read = new KoLAccountReadClient(_http);
        _mall = new KoLMallReadClient(_http, itemNameResolver: KoLibri.Core.Spiel.BundledItemNames.Resolver);
    }

    public AccountRole Role => _session.Profile.Role;
    public string AccountName => _session.Profile.DisplayName;

    /// <summary>Signs in with a user name and password and stores the session for this role.</summary>
    /// <param name="rememberPassword">Keep the password (encrypted) so the session can be renewed later.</param>
    public static async Task<KolibriClient> LoginAsync(
        AccountRole role, string username, string password,
        bool rememberPassword = true, AccountSessionManager? accounts = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrEmpty(password);
        var manager = accounts ?? AccountSessionManager.CreateDefault();
        await manager.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var login = await new KoLLoginClient()
            .LoginAsync(username, password, rememberPassword, cancellationToken).ConfigureAwait(false);
        await manager.SetProfileAsync(new AccountProfile
        {
            Id = role == AccountRole.Main ? "main" : "mallbot",
            DisplayName = login.UserName,
            Role = role,
            Enabled = true
        }, cancellationToken).ConfigureAwait(false);
        await manager.SetCredentialsAsync(role, login.Credentials, cancellationToken).ConfigureAwait(false);
        var session = await manager.GetRequiredSessionAsync(role, cancellationToken).ConfigureAwait(false);
        return new KolibriClient(manager, session);
    }

    /// <summary>
    /// Opens the stored session for a role. A session KoL no longer accepts is renewed with the
    /// saved password when there is one; otherwise <see cref="KolibriLoginRequiredException"/>.
    /// </summary>
    public static async Task<KolibriClient> OpenAsync(
        AccountRole role, AccountSessionManager? accounts = null, CancellationToken cancellationToken = default)
    {
        var manager = accounts ?? AccountSessionManager.CreateDefault();
        await manager.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var session = await manager.GetSessionAsync(role, includeExpired: true, cancellationToken: cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KolibriLoginRequiredException($"No stored account for {role}. Call LoginAsync first.");
        var client = new KolibriClient(manager, session);
        try
        {
            if (!session.IsExpired() && await client.SessionWorksAsync(cancellationToken).ConfigureAwait(false))
                return client;
            await client.RenewAsync(cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public Task<KoLStatusSnapshot> GetStatusAsync(bool forceRefresh = true, CancellationToken cancellationToken = default)
        => _read.FetchStatusAsync(forceRefresh, cancellationToken);

    public Task<KoLInventorySnapshot> GetInventoryAsync(bool forceRefresh = true, CancellationToken cancellationToken = default)
        => _read.FetchInventoryAsync(forceRefresh, cancellationToken);

    /// <summary>Searches the mall by item name (KoL's search runs on the name).</summary>
    public Task<MallSnapshot> SearchMallAsync(string itemName, CancellationToken cancellationToken = default)
        => _mall.SearchAsync(itemName, forceRefresh: true, cancellationToken);

    /// <summary>
    /// Buys up to <paramref name="quantity"/> of a tradeable item from the cheapest mall listings,
    /// never above <paramref name="maxPricePerUnit"/> and never below <paramref name="meatReserve"/>
    /// meat left. Stores that are at their daily limit are skipped. Success means KoL confirmed it.
    /// </summary>
    public async Task<BaconArbitrageExecutor.Outcome> BuyCheapAsync(
        int itemId, string itemName, int quantity, long maxPricePerUnit, long meatReserve = 0,
        CancellationToken cancellationToken = default, bool singlePurchase = false)
    {
        var status = await GetStatusAsync(true, cancellationToken).ConfigureAwait(false);
        return await new BaconArbitrageExecutor(new MallBuyClient(_http, MallBuyClient.DefaultBaseUri), _mall)
            .TopUpItemAsync(_session.Credentials, itemId, quantity, status.Meat, maxPricePerUnit,
                meatReserve, maxRounds: 60, cancellationToken: cancellationToken, itemName: itemName, singlePurchase: singlePurchase)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs today's Internet Meme Shop: pushes the BACON machine once and buys each daily item
    /// that is still open. Progress is kept in the local daily-state file, so a second call the
    /// same KoL day only does what is left.
    /// </summary>
    /// <param name="topUpBacon">Buy missing BACON in the mall first (price ceiling applies).</param>
    public async Task<MemeShopRunResult> RunMemeShopAsync(
        bool topUpBacon = false, long maxBaconPrice = 1_500, long meatReserve = 200_000,
        CancellationToken cancellationToken = default)
    {
        var store = new DailyDoneStore();
        var live = await new LiveDailySnapshotBuilder(_read, store)
            .BuildAsync(new DailyRoutineSnapshot(), cancellationToken, role: Role).ConfigureAwait(false);
        var bacon = live.Snapshot.Bacon;
        BaconArbitrageExecutor.Outcome? topUp = null;
        if (topUpBacon)
        {
            var needed = DailyInternetShopPlanner.BaconShortfall(
                live.Snapshot.AlreadyPulledItemIds, bacon,
                live.Snapshot.BaconMachineOwned, live.Snapshot.BaconMachineUsedToday);
            if (needed > 0)
            {
                topUp = await BuyCheapAsync(BaconArbitrageExecutor.Ids.Bacon, "BACON", needed,
                    maxBaconPrice, meatReserve, cancellationToken).ConfigureAwait(false);
                bacon += topUp.Quantity;
                // A BACON purchase whose answer is unknown may be booked: stop before the shop steps.
                if (topUp.Unclear)
                    return new MemeShopRunResult("Unclear", Array.Empty<MemeShopStepResult>(), topUp, Unclear: true);
            }
        }
        var plan = new DailyInternetShopPlanner().Plan(new InternetShopSnapshot
        {
            AlreadyPulledItemIds = live.Snapshot.AlreadyPulledItemIds,
            Bacon = bacon,
            BaconMachineOwned = live.Snapshot.BaconMachineOwned,
            BaconMachineUsedToday = live.Snapshot.BaconMachineUsedToday
        });
        var dispatcher = new FreePullDispatcher(_http, FreePullDispatcher.DefaultBaseUri);
        var results = new List<MemeShopStepResult>();
        var unclear = false;
        foreach (var step in plan.Steps)
        {
            var (accepted, message, stepUnclear) = await AutoCombatDailyBridge.ExecuteMemeStepDetailedAsync(
                new ChoiceStepSpec(step.ChoiceId, step.OptionId), dispatcher, _session.Credentials,
                store, Role.ToString(), cancellationToken).ConfigureAwait(false);
            results.Add(new MemeShopStepResult(step.Description, accepted, message, stepUnclear));
            if (stepUnclear) { unclear = true; break; }
        }
        return new MemeShopRunResult(unclear ? "Unclear" : plan.Verdict.ToString(), results, topUp, unclear);
    }

    /// <summary>
    /// Opens an adventure session. <paramref name="approvalToken"/> is the caller's own
    /// authorisation for this session's requests; without a deliberate call nothing is sent.
    /// </summary>
    public Task<ManualAdventureSession> OpenAdventureAsync(
        string approvalToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalToken);
        var approval = new DelegateAdventureApprovalHook((_, _) =>
            ValueTask.FromResult(AdventureApprovalDecision.Allow(approvalToken)));
        var verification = new DelegateAdventureVerificationHook((context, _) =>
            ValueTask.FromResult(context.Response.Accepted != false
                ? AdventureVerificationResult.Success()
                : AdventureVerificationResult.Failed("KoL refused the request.")));
        return ManualAdventureSession.OpenAsync(Role, approval, verification, _accounts, cancellationToken);
    }

    /// <summary>
    /// Sends a KMail, optionally with items and meat attached. True means KoL showed its
    /// "Message sent" confirmation; anything else must be treated as not sent.
    /// </summary>
    public async Task<bool> SendKMailAsync(
        string recipient, string message, IReadOnlyList<KoLMailAttachment>? items = null, long meat = 0,
        CancellationToken cancellationToken = default)
    {
        var hash = await new FreePullDispatcher(_http, FreePullDispatcher.DefaultBaseUri)
            .ReadSessionHashAsync(_session.Credentials, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(hash))
            throw new InvalidOperationException("No request hash for this account; nothing was sent.");
        return await KoLMailbox.SendAsync(_http, hash, recipient, message, items ?? [], meat, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Uses an inventory item <paramref name="quantity"/> times (one request each, stops at the first
    /// refusal). Returns how many uses KoL confirmed by the inventory count going down.
    /// </summary>
    public async Task<(int Used, string? Rejection)> UseItemAsync(
        int itemId, int quantity, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(quantity, 1);
        var hash = await new FreePullDispatcher(_http, FreePullDispatcher.DefaultBaseUri)
            .ReadSessionHashAsync(_session.Credentials, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(hash))
            throw new InvalidOperationException("No request hash for this account; nothing was used.");
        var actions = new CharacterActionClient(_http, hash);
        var key = itemId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        int Count(KoLInventorySnapshot i) => i.Items.Where(x => x.ItemId == key).Sum(x => x.Quantity);
        var current = Count(await GetInventoryAsync(true, cancellationToken).ConfigureAwait(false));
        var used = 0;
        string? rejection = null;
        for (var i = 0; i < quantity; i++)
        {
            var result = await actions.ExecuteAsync(new UseInventoryItem(itemId), cancellationToken).ConfigureAwait(false);
            var after = result.Inventory.IsStale ? current : Count(result.Inventory);
            if (after >= current) { rejection = result.RejectionReason ?? "KoL did not confirm the use."; break; }
            used++;
            current = after;
        }
        return (used, rejection);
    }

    private async Task<CharacterActionClient> OpenCharacterActionsAsync(CancellationToken cancellationToken)
    {
        var hash = await new FreePullDispatcher(_http, FreePullDispatcher.DefaultBaseUri)
            .ReadSessionHashAsync(_session.Credentials, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(hash))
            throw new InvalidOperationException("No request hash for this account; nothing was sent.");
        return new CharacterActionClient(_http, hash);
    }

    /// <summary>
    /// Eats, drinks or takes a spleen item. Returns how many KoL confirmed by the inventory count going
    /// down, and the reason when KoL refused (too full, too drunk, spleen limit).
    /// </summary>
    public async Task<(int Consumed, string? Rejection)> ConsumeAsync(
        int itemId, CharacterConsumableKind kind, int quantity, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(quantity, 1);
        var actions = await OpenCharacterActionsAsync(cancellationToken).ConfigureAwait(false);
        var key = itemId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        int Count(KoLInventorySnapshot i) => i.Items.Where(x => x.ItemId == key).Sum(x => x.Quantity);
        var before = Count(await GetInventoryAsync(true, cancellationToken).ConfigureAwait(false));
        var result = await actions.ExecuteAsync(new ConsumeCharacterItem(itemId, kind, quantity), cancellationToken).ConfigureAwait(false);
        var after = result.Inventory.IsStale ? before : Count(result.Inventory);
        var consumed = Math.Max(0, before - after);
        var rejection = result.RejectionReason ?? (consumed < quantity ? "KoL did not confirm all of it." : null);
        return (consumed, rejection);
    }

    /// <summary>
    /// Rests at the campground up to <paramref name="times"/> times. Refused or unconfirmed rests are not
    /// counted; <c>Problem</c> says why the series ended early.
    /// </summary>
    public async Task<CharacterRestOutcome> RestAsync(int times, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(times, 1);
        var actions = await OpenCharacterActionsAsync(cancellationToken).ConfigureAwait(false);
        return await actions.RestAsync(times, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Casts a skill <paramref name="times"/> times in one request (e.g. Soul Food, id 7185). Returns HP/MP after; Rejection is set when KoL refused or the effect is unconfirmed.</summary>
    public async Task<(int Hp, int Mp, string? Rejection, string? Page)> CastSkillAsync(int skillId, int times, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(times, 1);
        var actions = await OpenCharacterActionsAsync(cancellationToken).ConfigureAwait(false);
        var o = await actions.CastAsync(skillId, times, cancellationToken).ConfigureAwait(false);
        return (o.After.Status.CurrentHp, o.After.Status.CurrentMp, o.Problem, o.After.ResponseExcerpt);
    }

    /// <summary>Changes prices in the account's own mall store. Without <paramref name="commit"/> only checks.</summary>
    public Task<KoLibri.Core.Markt.ShopMutationResult> RepriceShopAsync(
        IEnumerable<KoLibri.Core.Markt.ShopPriceUpdate> updates, bool commit, CancellationToken cancellationToken = default)
        => new KoLShopMutationClient(_http).ApplyPriceUpdatesAsync(updates, commit, cancellationToken);

    /// <summary>The account's own mall store as KoL shows it now.</summary>
    public Task<IReadOnlyList<KoLibri.Core.Markt.OwnShopListing>> GetOwnShopAsync(bool forceRefresh = true, CancellationToken cancellationToken = default)
        => _mall.FetchOwnShopAsync(forceRefresh, cancellationToken);

    /// <summary>
    /// Reads the own store's sales log (read-only). Buyer names are replaced by the stable keys of
    /// <paramref name="crm"/>; the raw name is only kept in memory for the local CRM sync.
    /// </summary>
    public Task<KoLibri.Core.Markt.ShopSalesSnapshot> GetShopSalesAsync(
        KoLibri.Core.Markt.BuyerCrmStore crm, bool forceRefresh = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(crm);
        return new KoLibri.Core.Markt.KoLShopSalesReadClient(_http, buyerHasher: crm.CreateHasher())
            .FetchSalesAsync(forceRefresh, cancellationToken);
    }

    /// <summary>Display name of the stored account for a role, or null when none is stored.</summary>
    public async Task<string?> OtherAccountNameAsync(CancellationToken cancellationToken = default)
    {
        var other = Role == AccountRole.Main ? AccountRole.Mallbot : AccountRole.Main;
        var session = await _accounts.GetSessionAsync(other, includeExpired: true, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return session?.Profile.DisplayName;
    }

    private async Task<bool> SessionWorksAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _read.FetchStatusAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (KoLHtmlResponseException exception) when (exception.Kind == KoLHtmlResponseKind.LoginPage)
        {
            return false;
        }
    }

    private async Task RenewAsync(CancellationToken cancellationToken)
    {
        var renewed = await _accounts.ReconnectAsync(_session,
            (user, pass, token) => new KoLLoginClient().LoginAsync(user, pass, true, token), cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KolibriLoginRequiredException(
                $"The {Role} session expired and no saved password is stored. Call LoginAsync again.");
        _http.Dispose();
        _session = renewed;
        _http = KoLLoginClient.CreateAuthenticatedClient(renewed.Credentials, KolBase);
        _read = new KoLAccountReadClient(_http);
        _mall = new KoLMallReadClient(_http, itemNameResolver: KoLibri.Core.Spiel.BundledItemNames.Resolver);
    }

    public void Dispose() => _http.Dispose();
}
