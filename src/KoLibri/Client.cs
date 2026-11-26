using KoLibri.Internal;
using CoreBasis = KoLibri.Core.Basis;
using CoreMarkt = KoLibri.Core.Markt;

namespace KoLibri
{
    /// <summary>Wurzelobjekt, eins pro Prozess. Hält Konten, Limiter, Daten und Events.</summary>
    public sealed class KolibriClient : IAsyncDisposable
    {
        private readonly List<KolibriSession> _sessions = new();
        private readonly object _gate = new();
        private bool _disposed;

        private KolibriClient(KolibriOptions options)
        {
            Options = options;
            Policy = SessionPipeline.CreatePolicy(options.BaseUri);
            Limiter = SessionPipeline.CreateLimiter(options);
            Store = options.AccountStore ?? new RoleAccountStore(options.StateDirectory);
            Accounts = new AccountRegistry(this);
            Data = new GameData(options.DataDirectory);
            Events = new KolibriEvents();
            _pricegun = new Lazy<(HttpClient, CoreMarkt.PricegunReadClient)>(() =>
            {
                // pricegun.loathers.net is not KoL: its own client, never with the KoL cookie.
                var http = options.HttpHandler is { } handler ? new HttpClient(handler, disposeHandler: false) : new HttpClient();
                http.Timeout = options.RequestTimeout;
                return (http, new CoreMarkt.PricegunReadClient(http));
            }, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        private readonly Lazy<(HttpClient Http, CoreMarkt.PricegunReadClient Client)> _pricegun;
        internal CoreMarkt.PricegunReadClient Pricegun => _pricegun.Value.Client;

        /// <summary>Erzeugt einen Client. Ohne Optionen: echte KoL-Server, Konten wie bisher im App-Data-Ordner.</summary>
        public static KolibriClient Create(KolibriOptions? options = null) => new(options ?? new KolibriOptions());

        public KolibriOptions Options { get; }
        public AccountRegistry Accounts { get; }
        public GameData Data { get; }
        /// <summary>Events aller Sitzungen dieses Clients.</summary>
        public KolibriEvents Events { get; }

        internal IAccountStore Store { get; }
        internal CoreBasis.KoLSecurityPolicy Policy { get; }
        internal CoreBasis.KoLRateLimiter Limiter { get; }

        /// <summary>Meldet sich mit Name und Passwort an, legt das Konto an oder aktualisiert es.</summary>
        public async Task<KolibriSession> LoginAsync(string userName, string password, LoginOptions? options = null, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(userName);
            ArgumentException.ThrowIfNullOrEmpty(password);
            ThrowIfDisposed();
            options ??= new LoginOptions();
            var id = new AccountId(string.IsNullOrWhiteSpace(options.AccountId)
                ? userName.Trim().ToLowerInvariant()
                : options.AccountId.Trim());

            var login = await LoginCoreAsync(userName, password, cancellationToken).ConfigureAwait(false);
            var identity = await ReadIdentityAsync(id, login.CookieHeader!, cancellationToken).ConfigureAwait(false);

            var existing = await FindRecordAsync(id.Value, byIdOnly: true, cancellationToken).ConfigureAwait(false);
            if (existing is not null && identity is not null && !options.AllowIdentityChange && !SamePlayer(existing.Account, identity.Value))
                throw new IdentityMismatchException(id, existing.Account.PlayerName, identity.Value.Name);
            if (existing is { Account.PlayerId: not null } && identity is null && !options.AllowIdentityChange)
                throw new KolibriException(
                    $"KoL did not report which player this session belongs to, and '{id}' belongs to '{existing.Account.PlayerName}'; nothing was saved.");

            var account = new Account(
                id,
                identity?.Name ?? userName.Trim(),
                identity?.PlayerId ?? (existing is not null && SameName(existing.Account, userName) ? existing.Account.PlayerId : null),
                options.Label ?? existing?.Account.Label,
                existing?.Account.Tags ?? Array.Empty<string>())
            { HasSession = true };
            var record = new AccountRecord(account, login.CookieHeader, options.RememberPassword ? password : null, login.ExpiresAt);
            await Store.SaveAsync(record, cancellationToken).ConfigureAwait(false);
            return Track(new KolibriSession(this, record, UserAgentFor(id)));
        }

        /// <summary>Öffnet eine Sitzung mit gespeichertem Cookie. Id oder KoL-Name.</summary>
        public async Task<KolibriSession> OpenAsync(string accountIdOrName, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(accountIdOrName);
            ThrowIfDisposed();
            var record = await FindRecordAsync(accountIdOrName, byIdOnly: false, cancellationToken).ConfigureAwait(false)
                ?? throw new KolibriException($"No stored account '{accountIdOrName}'. Call LoginAsync first.");
            if (string.IsNullOrWhiteSpace(record.CookieHeader))
            {
                if (string.IsNullOrEmpty(record.Password) || !Options.MayAutoReconnect)
                    throw new SessionExpiredException(record.Account.Id);
                var session = Track(new KolibriSession(this, record, UserAgentFor(record.Account.Id)));
                await session.ReconnectAsync(cancellationToken).ConfigureAwait(false);
                return session;
            }
            return Track(new KolibriSession(this, record, UserAgentFor(record.Account.Id)));
        }

        /// <summary>
        /// Preisverlauf von pricegun.loathers.net, ohne KoL-Sitzung (gecacht, gedrosselt). Null bei Fehler.
        /// Ergänzt in 0.5 Schritt 3: die CLI zeigt Preise auch ohne angemeldetes Konto.
        /// </summary>
        public Task<PriceHistory?> GetPriceHistoryAsync(ItemRef item, CancellationToken cancellationToken = default)
            => Mall.FetchPriceHistoryAsync(this, item, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            KolibriSession[] sessions;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                sessions = _sessions.ToArray();
                _sessions.Clear();
            }
            foreach (var session in sessions)
                await session.DisposeAsync().ConfigureAwait(false);
            if (_pricegun.IsValueCreated) _pricegun.Value.Http.Dispose();
        }

        internal async Task<AccountRecord?> FindRecordAsync(string idOrName, bool byIdOnly, CancellationToken cancellationToken)
        {
            var records = await Store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var key = idOrName.Trim();
            return records.FirstOrDefault(r => string.Equals(r.Account.Id.Value, key, StringComparison.OrdinalIgnoreCase))
                ?? (byIdOnly ? null : records.FirstOrDefault(r => string.Equals(r.Account.PlayerName, key, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>Login request against login.php, through the same allowlist and HTTP handler as the sessions.</summary>
        internal async Task<(string? CookieHeader, DateTimeOffset? ExpiresAt)> LoginCoreAsync(string userName, string password, CancellationToken cancellationToken)
        {
            var client = new CoreBasis.KoLLoginClient(new CoreBasis.KoLLoginOptions
            {
                BaseUri = Options.BaseUri,
                UserAgent = Options.UserAgent ?? CoreBasis.KoLUserAgent.Value,
                Timeout = Options.RequestTimeout,
                Security = Policy,
            }, SessionPipeline.LoginHandlerFactory(this));
            try
            {
                var result = await client.LoginAsync(userName, password, rememberPassword: false, cancellationToken).ConfigureAwait(false);
                return (result.Credentials.CookieHeader, result.Credentials.ExpiresAt);
            }
            catch (CoreBasis.KoLLoginException exception)
            {
                throw new KolibriException($"Login failed: {exception.Message}", exception);
            }
        }

        private async Task<(int PlayerId, string Name)?> ReadIdentityAsync(AccountId id, string cookieHeader, CancellationToken cancellationToken)
        {
            using var http = SessionPipeline.CreateHttpClient(this, id, cookieHeader, UserAgentFor(id), events: null);
            var identity = await CoreBasis.KoLPwdHashResolver.TryReadIdentityAsync(http, cancellationToken).ConfigureAwait(false);
            if (identity is null || !int.TryParse(identity.PlayerId, out var playerId)) return null;
            return (playerId, identity.Name);
        }

        private static bool SamePlayer(Account stored, (int PlayerId, string Name) identity)
            => stored.PlayerId is { } known ? known == identity.PlayerId : SameName(stored, identity.Name);

        private static bool SameName(Account stored, string name)
            => string.Equals(stored.PlayerName, name.Trim(), StringComparison.OrdinalIgnoreCase);

        internal string UserAgentFor(AccountId id)
            => Options.UserAgent ?? (Store as RoleAccountStore)?.UserAgentFor(id) ?? CoreBasis.KoLUserAgent.Value;

        private KolibriSession Track(KolibriSession session)
        {
            lock (_gate) _sessions.Add(session);
            return session;
        }

        internal void Forget(KolibriSession session)
        {
            lock (_gate) _sessions.Remove(session);
        }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public sealed class KolibriOptions
    {
        public Uri BaseUri { get; init; } = new("https://www.kingdomofloathing.com/");
        /// <summary>Ordner für Konten, Cookies, Marker. Standard: plattformüblicher App-Data-Ordner.</summary>
        public string? StateDirectory { get; init; }
        /// <summary>Ordner mit KoLmafia-Daten. Null: der mitgelieferte Ordner neben der Anwendung (external/kolmafia-data).</summary>
        public string? DataDirectory { get; init; }
        public string? UserAgent { get; init; }
        public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
        public LimiterOptions Limiter { get; init; } = new();
        public IAccountStore? AccountStore { get; init; }
        /// <summary>Für Offline-Tests: ersetzt den HTTP-Handler (Allowlist und Limiter bleiben davor).</summary>
        public HttpMessageHandler? HttpHandler { get; init; }
        /// <summary>Hook vor jeder verändernden Aktion. Standard: alles erlaubt. Siehe KoLibri.Safety.</summary>
        public IActionGuard? ActionGuard { get; init; }
        /// <summary>
        /// Darf die Lib mit dem gespeicherten Passwort selbst neu anmelden (Lesen nach einer Login-Seite, OpenAsync ohne Cookie)?
        /// Wird vor jedem automatischen Reconnect gefragt; null heißt ja. Ein expliziter ReconnectAsync-Aufruf gilt immer.
        /// Ergänzt in Schritt 3 (Desktop-Schalter "Log in again automatically").
        /// </summary>
        public Func<bool>? AutoReconnect { get; init; }

        internal bool MayAutoReconnect => AutoReconnect?.Invoke() ?? true;

        /// <summary>Tests only (InternalsVisibleTo): no waiting between requests.</summary>
        internal bool DisableLimiterForTests { get; init; }
    }

    public sealed class LimiterOptions
    {
        /// <summary>Mindestabstand zwischen zwei verändernden Requests (Lane Action, seriell). Untergrenze 1 s.</summary>
        public TimeSpan ActionInterval { get; init; } = TimeSpan.FromSeconds(2);
        /// <summary>Mindestabstand zwischen zwei Lese-Requests. Untergrenze 200 ms.</summary>
        public TimeSpan ReadInterval { get; init; } = TimeSpan.FromSeconds(1);
        public int ReadConcurrency { get; init; } = 2;
    }

    public sealed class LoginOptions
    {
        /// <summary>Lokale Id; Standard: der KoL-Name in Kleinbuchstaben.</summary>
        public string? AccountId { get; init; }
        public string? Label { get; init; }
        public bool RememberPassword { get; init; }
        /// <summary>Wenn das Konto schon existiert und KoL eine andere Spieler-Id meldet: abbrechen (Standard) oder überschreiben.</summary>
        public bool AllowIdentityChange { get; init; }
    }

    /// <summary>Stabile lokale Kennung eines Kontos, frei wählbar ("main", "mallbot", "alt-2").</summary>
    public readonly record struct AccountId(string Value)
    {
        public override string ToString() => Value;
    }

    public sealed record Account(AccountId Id, string PlayerName, int? PlayerId, string? Label, IReadOnlyList<string> Tags)
    {
        public bool HasSession { get; init; }
        public DateTimeOffset? SessionExpiresAt { get; init; }
        /// <summary>A password is stored, so the session can be renewed without asking. Ergänzt in 0.5 Schritt 3.</summary>
        public bool HasPassword { get; init; }
    }

    public sealed class AccountRegistry
    {
        private readonly KolibriClient _client;

        internal AccountRegistry(KolibriClient client) => _client = client;

        public async Task<IReadOnlyList<Account>> ListAsync(CancellationToken cancellationToken = default)
        {
            var records = await _client.Store.LoadAsync(cancellationToken).ConfigureAwait(false);
            return records.Select(ToAccount).ToArray();
        }

        /// <summary>Sucht zuerst nach Id, dann nach KoL-Name (ohne Groß/Klein).</summary>
        public async Task<Account?> FindAsync(string idOrName, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(idOrName);
            var record = await _client.FindRecordAsync(idOrName, byIdOnly: false, cancellationToken).ConfigureAwait(false);
            return record is null ? null : ToAccount(record);
        }

        public async Task<Account> SetLabelAsync(AccountId id, string? label, IReadOnlyList<string>? tags = null, CancellationToken cancellationToken = default)
        {
            var record = await RequireAsync(id, cancellationToken).ConfigureAwait(false);
            var updated = record with { Account = record.Account with { Label = label, Tags = tags ?? record.Account.Tags } };
            await _client.Store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            return ToAccount(updated);
        }

        /// <summary>Vergisst Cookie und gespeichertes Passwort, behält das Konto.</summary>
        public async Task LogoutAsync(AccountId id, CancellationToken cancellationToken = default)
        {
            var record = await RequireAsync(id, cancellationToken).ConfigureAwait(false);
            await _client.Store.SaveAsync(record with { CookieHeader = null, Password = null, ExpiresAt = null }, cancellationToken)
                .ConfigureAwait(false);
        }

        public Task RemoveAsync(AccountId id, CancellationToken cancellationToken = default)
            => _client.Store.DeleteAsync(id, cancellationToken);

        private async Task<AccountRecord> RequireAsync(AccountId id, CancellationToken cancellationToken)
            => await _client.FindRecordAsync(id.Value, byIdOnly: true, cancellationToken).ConfigureAwait(false)
               ?? throw new KolibriException($"No stored account '{id}'.");

        internal static Account ToAccount(AccountRecord record)
        {
            var hasSession = !string.IsNullOrWhiteSpace(record.CookieHeader)
                && (record.ExpiresAt is null || record.ExpiresAt > DateTimeOffset.UtcNow);
            return record.Account with { HasSession = hasSession, SessionExpiresAt = record.ExpiresAt, HasPassword = !string.IsNullOrEmpty(record.Password) };
        }
    }

    /// <summary>Ablage für Konten und Geheimnisse. Standard: JSON plus geschützte Secret-Datei unter StateDirectory.</summary>
    public interface IAccountStore
    {
        Task<IReadOnlyList<AccountRecord>> LoadAsync(CancellationToken cancellationToken);
        Task SaveAsync(AccountRecord record, CancellationToken cancellationToken);
        Task DeleteAsync(AccountId id, CancellationToken cancellationToken);
    }

    public sealed record AccountRecord(Account Account, string? CookieHeader, string? Password, DateTimeOffset? ExpiresAt);

    public class KolibriException : Exception
    {
        public KolibriException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// Login für eine bekannte Konto-Id, aber KoL meldet einen anderen Spieler. Nichts wurde gespeichert.
    /// Ergänzt in 0.5 Schritt 3 (CLI <c>login refresh</c> meldet das als eigenes Ergebnis).
    /// </summary>
    public sealed class IdentityMismatchException : KolibriException
    {
        public IdentityMismatchException(AccountId account, string storedPlayer, string loginPlayer)
            : base($"The account '{account}' belongs to '{storedPlayer}', but this login is '{loginPlayer}'. " +
                   "Nothing was saved. Set LoginOptions.AllowIdentityChange to replace it.")
        {
            Account = account;
            StoredPlayer = storedPlayer;
            LoginPlayer = loginPlayer;
        }

        public AccountId Account { get; }
        public string StoredPlayer { get; }
        public string LoginPlayer { get; }
    }

    /// <summary>Sitzung abgelaufen und kein gespeichertes Passwort für ein automatisches Reconnect.</summary>
    public sealed class SessionExpiredException : KolibriException
    {
        public SessionExpiredException(AccountId account) : base("Session expired.") => Account = account;
        public AccountId Account { get; }
    }

    /// <summary>KoL ist im Wartungsmodus, oder die Antwort ist eine Login-/Fehlerseite.</summary>
    public sealed class UnexpectedPageException : KolibriException
    {
        public UnexpectedPageException(Page page, string message) : base(message) => Page = page;
        public Page Page { get; }
    }

    /// <summary>Der IActionGuard hat die Aktion abgelehnt.</summary>
    public sealed class ActionDeniedException : KolibriException
    {
        public ActionDeniedException(GuardedAction action, string reason) : base(reason) { Action = action; Reason = reason; }
        public GuardedAction Action { get; }
        public string Reason { get; }
    }
}
