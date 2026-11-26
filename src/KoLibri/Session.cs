using System.Net;
using KoLibri.Internal;
using CoreBasis = KoLibri.Core.Basis;
using CoreMarkt = KoLibri.Core.Markt;
using CoreSpiel = KoLibri.Core.Spiel;

namespace KoLibri
{
    /// <summary>Eine angemeldete Sitzung eines Kontos. Alle Requests laufen über Allowlist, Limiter und IActionGuard.</summary>
    public sealed class KolibriSession : IAsyncDisposable
    {
        private const int MaxRedirects = 5;
        private readonly SemaphoreSlim _reconnectGate = new(1, 1);
        private readonly object _gate = new();
        private AccountRecord _record;
        private HttpClient _http;
        private CoreSpiel.KoLAccountReadClient _read;
        private CoreMarkt.KoLMallReadClient _mallRead;
        private string? _passwordHash;
        private bool _expired;
        private bool _disposed;

        internal KolibriSession(KolibriClient client, AccountRecord record, string userAgent)
        {
            Client = client;
            _record = record;
            UserAgent = userAgent;
            Events = new KolibriEvents();
            (_http, _read, _mallRead) = BuildClients(record.CookieHeader ?? string.Empty);
            Character = new CharacterActions(this);
            Inventory = new InventoryReader(this);
            Adventure = new Adventurer(this);
            Mall = new Mall(this);
            Shop = new Shop(this);
            Mail = new Mailbox(this);
            PirateKeys = new PirateKeys(this);
            Daily = new DailyMarkers(client.Options.StateDirectory, DailyKey);
        }

        public Account Account => AccountRegistry.ToAccount(_record);
        public KolibriEvents Events { get; }
        public bool IsExpired => _expired || string.IsNullOrWhiteSpace(_record.CookieHeader)
            || (_record.ExpiresAt is { } at && at <= DateTimeOffset.UtcNow);

        public CharacterActions Character { get; }
        public InventoryReader Inventory { get; }
        public Adventurer Adventure { get; }
        public Mall Mall { get; }
        public Shop Shop { get; }
        public Mailbox Mail { get; }
        public PirateKeys PirateKeys { get; }
        public DailyMarkers Daily { get; }

        internal KolibriClient Client { get; }
        internal string UserAgent { get; }
        internal HttpClient Http { get { lock (_gate) return _http; } }
        internal CoreSpiel.KoLAccountReadClient Reader { get { lock (_gate) return _read; } }
        internal CoreMarkt.KoLMallReadClient MallReader { get { lock (_gate) return _mallRead; } }
        internal CoreBasis.AccountCredentials Credentials => new()
        {
            CookieHeader = _record.CookieHeader,
            Username = _record.Account.PlayerName,
            UserAgent = UserAgent,
            ExpiresAt = _record.ExpiresAt
        };

        /// <summary>GET auf eine relative KoL-Seite. Lane Auto: bekannte Lesepfade sind Read, alles andere Action.</summary>
        public async Task<Page> GetPageAsync(string path, IReadOnlyDictionary<string, string>? query = null, RequestLane lane = RequestLane.Auto, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            var uri = BuildUri(path, query);
            var effective = EffectiveLane(HttpMethod.Get, uri, lane);
            if (effective == RequestLane.Action)
            {
                var action = new GuardedAction(Account.Id, GuardedActionKind.Other, $"GET {SessionPipeline.Redact(uri)}") { Path = SessionPipeline.Redact(uri) };
                return await GuardAsync(action, () => SendAsync(HttpMethod.Get, uri, null, lane, cancellationToken), _ => true, cancellationToken)
                    .ConfigureAwait(false);
            }
            return await SendAsync(HttpMethod.Get, uri, null, lane, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>POST; fügt "pwd" automatisch hinzu, wenn es fehlt.</summary>
        public async Task<Page> PostAsync(string path, IReadOnlyDictionary<string, string> form, RequestLane lane = RequestLane.Auto, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            ArgumentNullException.ThrowIfNull(form);
            var uri = BuildUri(path, null);
            var action = new GuardedAction(Account.Id, GuardedActionKind.RawPost, $"POST {SessionPipeline.Redact(uri)}") { Path = SessionPipeline.Redact(uri) };
            return await GuardAsync(action, async () =>
            {
                var fields = new Dictionary<string, string>(form, StringComparer.Ordinal);
                if (!fields.ContainsKey("pwd"))
                    fields["pwd"] = await GetPasswordHashAsync(cancellationToken).ConfigureAwait(false);
                return await SendAsync(HttpMethod.Post, uri, fields, lane, cancellationToken).ConfigureAwait(false);
            }, _ => true, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>api.php?what=status als JSON-Text.</summary>
        public async Task<string> GetApiAsync(string what, IReadOnlyDictionary<string, string>? query = null, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(what);
            var all = new Dictionary<string, string>(StringComparer.Ordinal) { ["what"] = what };
            if (query is not null) foreach (var (key, value) in query) all[key] = value;
            all.TryAdd("for", "KoLibri");
            var page = await SendAsync(HttpMethod.Get, BuildUri("api.php", all), null, RequestLane.Read, cancellationToken).ConfigureAwait(false);
            return page.Html;
        }

        public async Task<string> GetPasswordHashAsync(CancellationToken cancellationToken = default)
        {
            if (_passwordHash is { } known) return known;
            try
            {
                var hash = await CoreBasis.KoLPwdHashResolver.RequireAsync(Http, _record.CookieHeader ?? string.Empty, cancellationToken).ConfigureAwait(false);
                return _passwordHash = hash;
            }
            catch (InvalidOperationException exception)
            {
                throw new KolibriException(exception.Message, exception);
            }
        }

        /// <summary>Neu anmelden mit gespeichertem Passwort. Wirft SessionExpiredException ohne Passwort.</summary>
        public async Task ReconnectAsync(CancellationToken cancellationToken = default)
        {
            await _reconnectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var stored = await Client.FindRecordAsync(_record.Account.Id.Value, byIdOnly: true, cancellationToken).ConfigureAwait(false) ?? _record;
                var password = stored.Password ?? _record.Password;
                if (string.IsNullOrEmpty(password))
                {
                    _expired = true;
                    throw new SessionExpiredException(_record.Account.Id);
                }
                var login = await Client.LoginCoreAsync(stored.Account.PlayerName, password, cancellationToken).ConfigureAwait(false);
                var renewed = stored with { CookieHeader = login.CookieHeader, ExpiresAt = login.ExpiresAt, Password = password };
                await Client.Store.SaveAsync(renewed, cancellationToken).ConfigureAwait(false);
                if (_record.CookieHeader is { } old) CoreBasis.KoLPwdHashResolver.Forget(old);
                var (http, read, mall) = BuildClients(renewed.CookieHeader ?? string.Empty);
                HttpClient previous;
                lock (_gate)
                {
                    previous = _http;
                    (_http, _read, _mallRead) = (http, read, mall);
                    _record = renewed;
                    _passwordHash = null;
                    _expired = false;
                }
                previous.Dispose();
            }
            finally
            {
                _reconnectGate.Release();
            }
        }

        /// <summary>Startet einen lokalen Relay-Server, der den Browser mit dieser Sitzung verbindet.</summary>
        public Task<RelayServer> StartRelayAsync(RelayOptions? options = null, CancellationToken cancellationToken = default)
        {
            options ??= new RelayOptions();
            cancellationToken.ThrowIfCancellationRequested();
            var session = new CoreBasis.AccountSession(
                new CoreBasis.AccountProfile { Id = _record.Account.Id.Value, DisplayName = _record.Account.PlayerName, Role = CoreBasis.AccountRole.Main },
                Credentials);
            var host = CoreBasis.RelayHost.Start(session, options.Port, options.LogPath, options.UseBrowserUserAgent, options.Output,
                upstreamHandler: Client.Options.HttpHandler is null ? null : new NonDisposingHandler(Client.Options.HttpHandler),
                kolOrigin: Client.Options.BaseUri.GetLeftPart(UriPartial.Authority),
                limiter: Client.Limiter);
            return Task.FromResult(new RelayServer(host));
        }

        public ValueTask DisposeAsync()
        {
            HttpClient http;
            lock (_gate)
            {
                if (_disposed) return ValueTask.CompletedTask;
                _disposed = true;
                http = _http;
            }
            http.Dispose();
            Client.Forget(this);
            return ValueTask.CompletedTask;
        }

        // ------------------------------------------------------------------ internals

        /// <summary>
        /// Marker owner, as 0.4's DailyDoneStore.AccountKey: for the CLI accounts "main"/"mallbot" it is
        /// "Main:&lt;player id&gt;", with the player id KoL itself reported in the last status read (the stored
        /// profile may not have one), else the stored id, else the name. Markers written by the 0.4 CLI stay valid.
        /// Other accounts use their account id.
        /// </summary>
        private string DailyKey()
        {
            var account = _record.Account;
            if (account.Id.Value is not ("main" or "mallbot")) return account.Id.Value;
            var role = account.Id.Value == "main" ? "Main" : "Mallbot";
            var identity = _observedPlayerId is { Length: > 0 } seen ? seen
                : account.PlayerId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? account.PlayerName;
            return string.IsNullOrWhiteSpace(identity) ? role : $"{role}:{identity.Trim()}";
        }

        private string? _observedPlayerId;

        /// <summary>A read through the core read client: one reconnect on a login page, never stale data.</summary>
        internal async Task<T> ReadAsync<T>(Func<CoreSpiel.KoLAccountReadClient, Task<T>> read, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            T result;
            try { result = await read(Reader).ConfigureAwait(false); }
            catch (Exception exception) when (Mapping.IsLoginPage(exception))
            {
                _expired = true;
                if (!Client.Options.MayAutoReconnect) throw new SessionExpiredException(_record.Account.Id);
                await ReconnectAsync(cancellationToken).ConfigureAwait(false);
                result = await read(Reader).ConfigureAwait(false);
            }
            if (result is CoreSpiel.KoLStatusSnapshot { IsStale: true } or CoreSpiel.KoLInventorySnapshot { IsStale: true })
                throw new KolibriException("KoL could not be read just now; only an older copy is available, so nothing is assumed.");
            if (result is CoreSpiel.KoLStatusSnapshot { PlayerId: { Length: > 0 } playerId })
                _observedPlayerId = playerId;
            return result;
        }

        /// <summary>A core call that changes the account: a login page ends it, nothing is repeated.</summary>
        internal async Task<T> ActAsync<T>(Func<Task<T>> call)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try { return await call().ConfigureAwait(false); }
            catch (Exception exception) when (Mapping.IsLoginPage(exception))
            {
                _expired = true;
                throw new SessionExpiredException(_record.Account.Id);
            }
        }

        internal async Task<Status> StatusAsync(CancellationToken cancellationToken)
        {
            var status = Mapping.ToStatus(await ReadAsync(r => r.FetchStatusAsync(true, cancellationToken), cancellationToken).ConfigureAwait(false), Client.Data);
            Status? before;
            lock (_gate) { before = _lastStatus; _lastStatus = status; }
            if (before is null || Changed(before, status)) RaiseStatus(before, status);
            return status;
        }

        private Status? _lastStatus;

        private static bool Changed(Status a, Status b)
            => a.Hp != b.Hp || a.MaxHp != b.MaxHp || a.Mp != b.Mp || a.MaxMp != b.MaxMp || a.Meat != b.Meat
               || a.Adventures != b.Adventures || a.Level != b.Level || a.Fullness != b.Fullness || a.Inebriety != b.Inebriety
               || a.Spleen != b.Spleen || a.FamiliarId != b.FamiliarId || !a.Effects.SequenceEqual(b.Effects);


        private (HttpClient, CoreSpiel.KoLAccountReadClient, CoreMarkt.KoLMallReadClient) BuildClients(string cookieHeader)
        {
            var http = SessionPipeline.CreateHttpClient(Client, _record.Account.Id, cookieHeader, UserAgent, this);
            var resolver = Client.Data.ItemNameResolver;
            var read = new CoreSpiel.KoLAccountReadClient(http, Client.Options.BaseUri, securityPolicy: Client.Policy, itemNameResolver: resolver)
            {
                ReadCharsheetForOrganLimits = true
            };
            var mall = new CoreMarkt.KoLMallReadClient(http, Client.Options.BaseUri, securityPolicy: Client.Policy, itemNameResolver: resolver);
            return (http, read, mall);
        }

        internal Uri BuildUri(string path, IReadOnlyDictionary<string, string>? query)
        {
            var relative = path.TrimStart('/');
            if (query is { Count: > 0 })
            {
                var q = string.Join('&', query.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
                relative += (relative.Contains('?') ? "&" : "?") + q;
            }
            var uri = new Uri(Client.Options.BaseUri, relative);
            Client.Policy.EnsureAllowed(uri, "KoLibri request");
            return uri;
        }

        private static RequestLane EffectiveLane(HttpMethod method, Uri uri, RequestLane lane)
        {
            if (lane != RequestLane.Auto) return lane;
            using var probe = new HttpRequestMessage(method, uri);
            return SessionPipeline.EffectiveLane(probe);
        }

        /// <summary>
        /// Sends one request (no guard) and follows redirects inside the allowlist. A login page triggers one
        /// reconnect with the stored password, but only read-lane requests are repeated afterwards.
        /// </summary>
        internal async Task<Page> SendAsync(HttpMethod method, Uri uri, IReadOnlyDictionary<string, string>? form, RequestLane lane, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var effective = EffectiveLane(method, uri, lane);
            var page = await SendOnceAsync(method, uri, form, lane, cancellationToken).ConfigureAwait(false);
            if (page.Kind == PageKind.LoginRequired)
            {
                _expired = true;
                if (effective == RequestLane.Read && Client.Options.MayAutoReconnect && !string.IsNullOrEmpty(_record.Password ?? (await Client.FindRecordAsync(_record.Account.Id.Value, true, cancellationToken).ConfigureAwait(false))?.Password))
                {
                    await ReconnectAsync(cancellationToken).ConfigureAwait(false);
                    page = await SendOnceAsync(method, uri, form, lane, cancellationToken).ConfigureAwait(false);
                    if (page.Kind != PageKind.LoginRequired) return Check(page);
                }
                throw new SessionExpiredException(_record.Account.Id);
            }
            return Check(page);
        }

        private static Page Check(Page page) => page.Kind switch
        {
            PageKind.Maintenance => throw new UnexpectedPageException(page, "KoL is down for maintenance."),
            PageKind.TooFast => throw new UnexpectedPageException(page, "KoL asked to slow down; nothing more was sent."),
            PageKind.Error => throw new UnexpectedPageException(page, $"KoL answered HTTP {page.StatusCode}."),
            _ => page
        };

        private async Task<Page> SendOnceAsync(HttpMethod method, Uri uri, IReadOnlyDictionary<string, string>? form, RequestLane lane, CancellationToken cancellationToken)
        {
            var current = uri;
            var currentMethod = method;
            var currentForm = form;
            for (var redirect = 0; ; redirect++)
            {
                using var request = new HttpRequestMessage(currentMethod, current);
                if (currentForm is not null) request.Content = new FormUrlEncodedContent(currentForm);
                request.Options.Set(SessionPipeline.LaneKey, lane);
                using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (status is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    if (PageClassifier.IsLogin(location))
                        return new Page(current, status, string.Empty, PageKind.LoginRequired, DateTimeOffset.UtcNow);
                    if (redirect >= MaxRedirects)
                        throw new KolibriException("KoL exceeded the redirect limit.");
                    current = Client.Policy.ResolveRedirect(current, location.OriginalString);
                    // A redirect after a POST is followed with GET, like a browser does.
                    currentMethod = HttpMethod.Get;
                    currentForm = null;
                    continue;
                }
                var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var kind = PageClassifier.Classify(current, status, html, response.Headers.Location);
                return new Page(current, status, html, kind, DateTimeOffset.UtcNow);
            }
        }

        /// <summary>Runs <paramref name="body"/> under the configured <see cref="IActionGuard"/>.</summary>
        internal async Task<T> GuardAsync<T>(GuardedAction action, Func<Task<T>> body, Func<T, bool> succeeded, CancellationToken cancellationToken)
        {
            var guard = Client.Options.ActionGuard;
            if (guard is null) return await body().ConfigureAwait(false);
            var decision = await guard.CheckAsync(action, cancellationToken).ConfigureAwait(false);
            if (!decision.Allowed)
                throw new ActionDeniedException(action, decision.Reason ?? "The action guard refused the action.");
            var ok = false;
            try
            {
                var result = await body().ConfigureAwait(false);
                ok = succeeded(result);
                return result;
            }
            finally
            {
                await guard.CompletedAsync(action, ok, CancellationToken.None).ConfigureAwait(false);
            }
        }

        internal void RaiseRequest(RequestEventArgs args) => Raise(e => e.OnRequest(args), args.Account, nameof(KolibriEvents.Request));
        internal void RaiseEncounter(Encounter encounter)
            => Raise(e => e.OnEncounter(new EncounterEventArgs(Account.Id, DateTimeOffset.UtcNow, encounter)), Account.Id, nameof(KolibriEvents.Encounter));
        internal void RaiseFightEnded(FightResult result)
            => Raise(e => e.OnFightEnded(new FightEndedEventArgs(Account.Id, DateTimeOffset.UtcNow, result)), Account.Id, nameof(KolibriEvents.FightEnded));
        internal void RaiseItems(IEnumerable<InventoryItem> items, ItemSource source)
        {
            foreach (var item in items)
                Raise(e => e.OnItemAcquired(new ItemAcquiredEventArgs(Account.Id, DateTimeOffset.UtcNow, item, source)), Account.Id, nameof(KolibriEvents.ItemAcquired));
        }
        internal void RaiseStatus(Status? before, Status after)
            => Raise(e => e.OnStatusChanged(new StatusChangedEventArgs(Account.Id, DateTimeOffset.UtcNow, before, after)), Account.Id, nameof(KolibriEvents.StatusChanged));

        private void Raise(Func<KolibriEvents, List<Exception>> raise, AccountId account, string name)
        {
            foreach (var target in new[] { Events, Client.Events })
            {
                var failures = raise(target);
                foreach (var failure in failures)
                    target.OnHandlerFailed(new HandlerFailedEventArgs(account, DateTimeOffset.UtcNow, name, failure));
            }
        }

        private sealed class NonDisposingHandler(HttpMessageHandler target) : HttpMessageHandler
        {
            private readonly HttpMessageInvoker _invoker = new(target, disposeHandler: false);
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => _invoker.SendAsync(request, cancellationToken);
        }
    }

    public enum RequestLane { Auto, Read, Action }

    public enum PageKind { Normal, Fight, Choice, NotInFight, LoginRequired, Maintenance, TooFast, Error }

    public sealed record Page(Uri Url, int StatusCode, string Html, PageKind Kind, DateTimeOffset ReceivedAt);

    public sealed class RelayOptions
    {
        public int Port { get; init; }
        public string? LogPath { get; init; }
        public bool UseBrowserUserAgent { get; init; }
        /// <summary>Gets one line per relayed request (redacted), e.g. for a console. Ergänzt in 0.5 Schritt 3.</summary>
        public Action<string>? Output { get; init; }
    }

    public sealed class RelayServer : IAsyncDisposable
    {
        private readonly CoreBasis.RelayHost _host;

        internal RelayServer(CoreBasis.RelayHost host) => _host = host;

        public Uri BaseUri => new(_host.Origin + "/");
        /// <summary>URL, die den Browser anmeldet und auf <paramref name="path"/> weiterleitet.</summary>
        public Uri EntryUri(string path = "main.php") => new(_host.EnterUrl(path));
        public ValueTask DisposeAsync()
        {
            _host.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
