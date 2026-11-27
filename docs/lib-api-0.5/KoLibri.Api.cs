// KoLibri 0.5 – API-Entwurf als kompilierbarer Stub.
//
// Diese Datei ist NICHT Teil der Solution. Sie ist die Quelle der Wahrheit für die Signaturen in
// docs/lib-api-0.5.md und für PublicAPI.Unshipped.txt. Geprüft wird sie mit einem Wegwerfprojekt
// (net8.0, Nullable enable, Microsoft.CodeAnalysis.PublicApiAnalyzers), siehe docs/lib-api-0.5.md §9.
// Alle Rümpfe werfen; es geht nur um Form und Namen.

#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

#if !KOLIBRI_SAFETY
// ---------------------------------------------------------------------------------------------
// 1. Einstieg, Optionen, Konten
// ---------------------------------------------------------------------------------------------
namespace KoLibri
{
    /// <summary>Wurzelobjekt, eins pro Prozess. Hält Konten, Limiter, Daten und Events.</summary>
    public sealed class KolibriClient : IAsyncDisposable
    {
        private KolibriClient() { }
        public static KolibriClient Create(KolibriOptions? options = null) => throw new NotImplementedException();

        public KolibriOptions Options => throw new NotImplementedException();
        public AccountRegistry Accounts => throw new NotImplementedException();
        public GameData Data => throw new NotImplementedException();
        /// <summary>Events aller Sitzungen dieses Clients.</summary>
        public KolibriEvents Events => throw new NotImplementedException();

        /// <summary>Meldet sich mit Name und Passwort an, legt das Konto an oder aktualisiert es.</summary>
        public Task<KolibriSession> LoginAsync(string userName, string password, LoginOptions? options = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        /// <summary>Öffnet eine Sitzung mit gespeichertem Cookie. Id oder KoL-Name.</summary>
        public Task<KolibriSession> OpenAsync(string accountIdOrName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        /// <summary>Preisverlauf von pricegun.loathers.net ohne KoL-Sitzung. Ergänzt in Schritt 3 (CLI).</summary>
        public Task<PriceHistory?> GetPriceHistoryAsync(ItemRef item, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public ValueTask DisposeAsync() => throw new NotImplementedException();
    }

    public sealed class KolibriOptions
    {
        public Uri BaseUri { get; init; } = new("https://www.kingdomofloathing.com/");
        /// <summary>Ordner für Konten, Cookies, Marker. Standard: plattformüblicher App-Data-Ordner.</summary>
        public string? StateDirectory { get; init; }
        /// <summary>Ordner mit KoLmafia-Daten. Null: eingebettete Ressource.</summary>
        public string? DataDirectory { get; init; }
        public string? UserAgent { get; init; }
        public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
        public LimiterOptions Limiter { get; init; } = new();
        public IAccountStore? AccountStore { get; init; }
        /// <summary>Für Offline-Tests: ersetzt den HTTP-Handler (Allowlist und Limiter bleiben davor).</summary>
        public HttpMessageHandler? HttpHandler { get; init; }
        /// <summary>Hook vor jeder verändernden Aktion. Standard: alles erlaubt. Siehe KoLibri.Safety.</summary>
        public IActionGuard? ActionGuard { get; init; }
        /// <summary>Darf die Lib mit gespeichertem Passwort selbst neu anmelden? Vor jedem automatischen Reconnect gefragt; null = ja. Ergänzt in Schritt 3 (Desktop).</summary>
        public Func<bool>? AutoReconnect { get; init; }
    }

    public sealed class LimiterOptions
    {
        /// <summary>Mindestabstand zwischen zwei verändernden Requests (Lane Action, seriell).</summary>
        public TimeSpan ActionInterval { get; init; } = TimeSpan.FromSeconds(1);
        public TimeSpan ReadInterval { get; init; } = TimeSpan.FromMilliseconds(250);
        public int ReadConcurrency { get; init; } = 2;
        /// <summary>Höchstens so viele Requests je Sekunde (1–5; 0 = aus). Ergänzt in Schritt 3 (Desktop).</summary>
        public double MaxPerSecond { get; init; } = 2;
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
        /// <summary>Ein Passwort ist gespeichert (automatisches Reconnect möglich). Ergänzt in Schritt 3 (CLI).</summary>
        public bool HasPassword { get; init; }
    }

    public sealed class AccountRegistry
    {
        private AccountRegistry() { }
        public Task<IReadOnlyList<Account>> ListAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        /// <summary>Sucht zuerst nach Id, dann nach KoL-Name (ohne Groß/Klein).</summary>
        public Task<Account?> FindAsync(string idOrName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Account> SetLabelAsync(AccountId id, string? label, IReadOnlyList<string>? tags = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        /// <summary>Vergisst Cookie und gespeichertes Passwort, behält das Konto.</summary>
        public Task LogoutAsync(AccountId id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task RemoveAsync(AccountId id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
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
    /// <summary>Bekannte Konto-Id, aber KoL meldet einen anderen Spieler; nichts gespeichert. Ergänzt in Schritt 3 (CLI).</summary>
    public sealed class IdentityMismatchException : KolibriException
    {
        public IdentityMismatchException(AccountId account, string storedPlayer, string loginPlayer) : base("Identity mismatch.")
        { Account = account; StoredPlayer = storedPlayer; LoginPlayer = loginPlayer; }
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

// ---------------------------------------------------------------------------------------------
// 2. Sitzung und roher Seitenabruf
// ---------------------------------------------------------------------------------------------
namespace KoLibri
{
    /// <summary>Eine angemeldete Sitzung eines Kontos. Alle Requests laufen über Allowlist, Limiter und IActionGuard.</summary>
    public sealed class KolibriSession : IAsyncDisposable
    {
        private KolibriSession() { }
        public Account Account => throw new NotImplementedException();
        public KolibriEvents Events => throw new NotImplementedException();
        public bool IsExpired => throw new NotImplementedException();

        public CharacterActions Character => throw new NotImplementedException();
        public InventoryReader Inventory => throw new NotImplementedException();
        public Adventurer Adventure => throw new NotImplementedException();
        public Mall Mall => throw new NotImplementedException();
        public Shop Shop => throw new NotImplementedException();
        public Mailbox Mail => throw new NotImplementedException();
        public PirateKeys PirateKeys => throw new NotImplementedException();
        public DailyMarkers Daily => throw new NotImplementedException();

        /// <summary>GET auf eine relative KoL-Seite. Lane Auto: bekannte Lesepfade sind Read, alles andere Action.</summary>
        public Task<Page> GetPageAsync(string path, IReadOnlyDictionary<string, string>? query = null, RequestLane lane = RequestLane.Auto, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        /// <summary>POST; fügt "pwd" automatisch hinzu, wenn es fehlt.</summary>
        public Task<Page> PostAsync(string path, IReadOnlyDictionary<string, string> form, RequestLane lane = RequestLane.Auto, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        /// <summary>api.php?what=status als JSON-Text.</summary>
        public Task<string> GetApiAsync(string what, IReadOnlyDictionary<string, string>? query = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<string> GetPasswordHashAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        /// <summary>Neu anmelden mit gespeichertem Passwort. Wirft SessionExpiredException ohne Passwort.</summary>
        public Task ReconnectAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        /// <summary>Startet einen lokalen Relay-Server, der den Browser mit dieser Sitzung verbindet.</summary>
        public Task<RelayServer> StartRelayAsync(RelayOptions? options = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public ValueTask DisposeAsync() => throw new NotImplementedException();
    }

    public enum RequestLane { Auto, Read, Action }

    public enum PageKind { Normal, Fight, Choice, NotInFight, LoginRequired, Maintenance, TooFast, Error }

    public sealed record Page(Uri Url, int StatusCode, string Html, PageKind Kind, DateTimeOffset ReceivedAt);

    public sealed class RelayOptions
    {
        public int Port { get; init; }
        public string? LogPath { get; init; }
        public bool UseBrowserUserAgent { get; init; }
        /// <summary>Eine Zeile je weitergeleitetem Request (redigiert). Ergänzt in Schritt 3 (CLI).</summary>
        public Action<string>? Output { get; init; }
        /// <summary>Vor jeder weitergeleiteten Anfrage: null leitet weiter, ein Text lehnt ab. Ergänzt in Schritt 3 (Desktop-Browser).</summary>
        public Func<Task<string?>>? SessionCheck { get; init; }
    }

    public sealed class RelayServer : IAsyncDisposable
    {
        private RelayServer() { }
        public Uri BaseUri => throw new NotImplementedException();
        /// <summary>URL, die den Browser anmeldet und auf <paramref name="path"/> weiterleitet.</summary>
        public Uri EntryUri(string path = "main.php") => throw new NotImplementedException();
        public ValueTask DisposeAsync() => throw new NotImplementedException();
    }
}

// ---------------------------------------------------------------------------------------------
// 3. Status, Inventar, Spieldaten
// ---------------------------------------------------------------------------------------------
namespace KoLibri
{
    public readonly record struct ItemRef(int Id)
    {
        public static implicit operator ItemRef(int id) => new(id);
    }
    public readonly record struct SkillRef(int Id)
    {
        public static implicit operator SkillRef(int id) => new(id);
    }
    public readonly record struct Location(int Snarfblat)
    {
        public static implicit operator Location(int snarfblat) => new(snarfblat);
    }

    public sealed record Status(string PlayerName, int Level, long Meat, int Adventures, DateTimeOffset FetchedAt)
    {
        public int Hp { get; init; }
        public int MaxHp { get; init; }
        public int Mp { get; init; }
        public int MaxMp { get; init; }
        public OrganState Fullness { get; init; }
        public OrganState Inebriety { get; init; }
        public OrganState Spleen { get; init; }
        public IReadOnlyList<ActiveEffect> Effects { get; init; } = Array.Empty<ActiveEffect>();
        public int? FamiliarId { get; init; }
        public string? PathName { get; init; }
        public int? RoninTurnsRemaining { get; init; }
        public int? StoragePullsRemaining { get; init; }
        /// <summary>Ergänzt in Schritt 3 (Desktop-Charpane).</summary>
        public int? PlayerId { get; init; }
        public string? FamiliarName { get; init; }
        public int? FamiliarWeight { get; init; }
        public int? PathId { get; init; }
        public int? Ascensions { get; init; }
        public bool? Hardcore { get; init; }
    }

    public readonly record struct OrganState(int Current, int? Limit);
    public sealed record ActiveEffect(int Id, string Name, int Turns);

    public sealed record InventoryItem(ItemRef Item, string Name, int Quantity);

    public sealed record Inventory(IReadOnlyList<InventoryItem> Items, DateTimeOffset FetchedAt)
    {
        public int Count(ItemRef item) => throw new NotImplementedException();
    }

    public sealed record Equipment(IReadOnlyDictionary<EquipSlot, ItemRef?> Slots, DateTimeOffset FetchedAt);

    public enum EquipSlot { Hat, Back, Shirt, Weapon, OffHand, Pants, Accessory1, Accessory2, Accessory3, Familiar }

    public sealed class InventoryReader
    {
        private InventoryReader() { }
        public Task<Inventory> GetAsync(bool refresh = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Inventory> GetClosetAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Inventory> GetStorageAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Equipment> GetEquipmentAsync(bool refresh = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    /// <summary>KoLmafia-Daten: Items, Skills, Monster, Orte. Eingebettet oder aus DataDirectory.</summary>
    public sealed class GameData
    {
        private GameData() { }
        public ItemInfo? Item(ItemRef item) => throw new NotImplementedException();
        public ItemInfo? FindItem(string name) => throw new NotImplementedException();
        public SkillInfo? Skill(SkillRef skill) => throw new NotImplementedException();
        public SkillInfo? FindSkill(string name) => throw new NotImplementedException();
        public LocationInfo? Location(Location location) => throw new NotImplementedException();
        public LocationInfo? FindLocation(string name) => throw new NotImplementedException();
        public MonsterInfo? FindMonster(string name) => throw new NotImplementedException();
    }

    public enum ItemUse { None, Food, Drink, Spleen, Usable, MultiUsable, Equipment, Familiar, Combat }

    public sealed record ItemInfo(ItemRef Item, string Name, ItemUse Use, bool Tradeable, int AutosellPrice)
    {
        public int OrganSize { get; init; }
        public EquipSlot? Slot { get; init; }
    }
    public sealed record SkillInfo(SkillRef Skill, string Name, int MpCost, bool UsableInCombat);
    public sealed record LocationInfo(Location Location, string Name, string Zone);
    public sealed record MonsterInfo(int Id, string Name, int? Hp, int? Attack, int? Defense);
}

// ---------------------------------------------------------------------------------------------
// 4. Konsum, Rest, Cast, Equip, Familiar
// ---------------------------------------------------------------------------------------------
namespace KoLibri
{
    public enum ConsumeKind { Eat, Drink, Chew }

    public sealed class CharacterActions
    {
        private CharacterActions() { }
        public Task<Status> GetStatusAsync(bool refresh = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ActionResult> UseAsync(ItemRef item, int quantity = 1, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        /// <summary>Prüft Organ-Platz vor dem Request; zu volle Organe ergeben ActionResult mit Rejection, keinen Request.</summary>
        public Task<ActionResult> ConsumeAsync(ItemRef item, ConsumeKind kind, int quantity = 1, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ActionResult> CastAsync(SkillRef skill, int times = 1, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ActionResult> RestAsync(int times = 1, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ActionResult> EquipAsync(ItemRef item, EquipSlot? slot = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ActionResult> UnequipAsync(EquipSlot slot, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ActionResult> WearOutfitAsync(int outfitId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ActionResult> SetFamiliarAsync(int familiarId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    public enum ActionStatus { Done, Partial, Rejected, Unclear }

    /// <summary>Ergebnis einer verändernden Aktion, mit Status vorher/nachher, wo gelesen.</summary>
    public sealed record ActionResult(ActionStatus Status, int Completed, string? Rejection, Page? Page)
    {
        public IReadOnlyList<InventoryItem> ItemsGained { get; init; } = Array.Empty<InventoryItem>();
        public IReadOnlyList<InventoryItem> ItemsLost { get; init; } = Array.Empty<InventoryItem>();
        public Status? Before { get; init; }
        public Status? After { get; init; }
    }
}

// ---------------------------------------------------------------------------------------------
// 5. Abenteuer, Kampf, Choice
// ---------------------------------------------------------------------------------------------
namespace KoLibri
{
    public sealed class Adventurer
    {
        private Adventurer() { }
        /// <summary>Ein Zug in <paramref name="location"/>. Liefert die Begegnung, ohne sie aufzulösen.</summary>
        public Task<Encounter> AdventureAsync(Location location, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        /// <summary>Offene Begegnung (Kampf oder Choice), sonst null. Für Wiederaufnahme nach Browser-Klicks.</summary>
        public Task<Encounter?> GetOpenEncounterAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        /// <summary>Eine Kampfrunde.</summary>
        public Task<FightRound> FightAsync(CombatAction action, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        /// <summary>Kämpft mit der Strategie bis Sieg, Niederlage, Flucht oder CombatAction.Stop.</summary>
        public Task<FightResult> FightToEndAsync(ICombatStrategy strategy, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Encounter> ChooseAsync(int choiceId, int option, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        /// <summary>Der eine Adventure-Läufer: N Züge, Strategie, Resolver, Stopp-Regeln.</summary>
        public Task<RunResult> RunAsync(RunRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LocationAccess> GetLocationAccessAsync(Location location, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    public abstract record Encounter(Page Page, Location? Location, string? Title);
    public sealed record FightEncounter(Page Page, Location? Location, string? Title, string MonsterName, int? MonsterId, int Round) : Encounter(Page, Location, Title);
    public sealed record ChoiceEncounter(Page Page, Location? Location, string? Title, int ChoiceId, IReadOnlyList<ChoiceOption> Options) : Encounter(Page, Location, Title);
    public sealed record NonCombatEncounter(Page Page, Location? Location, string? Title, IReadOnlyList<InventoryItem> Items, long Meat) : Encounter(Page, Location, Title);
    /// <summary>Kein Zug möglich: keine Adventures, Ort gesperrt, Organ zu voll, unbekannte Seite.</summary>
    public sealed record BlockedEncounter(Page Page, Location? Location, string? Title, BlockReason Reason) : Encounter(Page, Location, Title);

    public enum BlockReason { NoAdventures, LocationLocked, Beaten, UnknownPage, Other }

    public sealed record ChoiceOption(int Option, string Text);

    public sealed record LocationAccess(Location Location, bool Accessible, string? Reason);

    // --- Kampf ----------------------------------------------------------------------------

    public enum CombatActionKind
    {
        Attack,
        Skill,
        Item,
        RunAway,
        /// <summary>Experimentell in 0.5: der Läufer lehnt Makros mit NotSupportedException ab, bis sie live geprüft sind.</summary>
        [Experimental(KolibriDiagnostics.CombatMacros)]
        Macro,
        Stop
    }

    /// <summary>Diagnose-Ids für [Experimental]-APIs. Nutzer schalten sie per NoWarn bewusst frei.</summary>
    public static class KolibriDiagnostics
    {
        /// <summary>KoL-Combat-Makros: angelegt, aber vor dem Live-Test nicht freigeschaltet.</summary>
        public const string CombatMacros = "KOLIBRI0001";
    }

    public sealed record CombatAction(CombatActionKind Kind, int? Id = null, int? SecondId = null, string? Macro = null)
    {
        public static CombatAction Attack { get; } = new(CombatActionKind.Attack);
        public static CombatAction RunAway { get; } = new(CombatActionKind.RunAway);
        /// <summary>Kampf an den Menschen zurückgeben (Browser/Encounter-Ansicht).</summary>
        public static CombatAction Stop { get; } = new(CombatActionKind.Stop);
        public static CombatAction Skill(SkillRef skill) => new(CombatActionKind.Skill, skill.Id);
        public static CombatAction Item(ItemRef item, ItemRef? second = null) => new(CombatActionKind.Item, item.Id, second?.Id);
        /// <summary>KoL-Combat-Makro, wird über fight.php?action=macro geschickt. Experimentell in 0.5.</summary>
        [Experimental(KolibriDiagnostics.CombatMacros)]
        public static CombatAction FromMacro(string macro) => new(CombatActionKind.Macro, Macro: macro);
    }

    public enum FightOutcome { Ongoing, Won, Lost, RanAway, Stopped, Unknown }

    public sealed record FightRound(int Round, CombatAction Action, FightOutcome Outcome, int Hp, int Mp, Page Page)
    {
        public int DamageDealt { get; init; }
        public int DamageTaken { get; init; }
        public IReadOnlyList<InventoryItem> Items { get; init; } = Array.Empty<InventoryItem>();
        public long Meat { get; init; }
    }

    public sealed record FightResult(FightOutcome Outcome, string MonsterName, IReadOnlyList<FightRound> Rounds)
    {
        public IReadOnlyList<InventoryItem> Items { get; init; } = Array.Empty<InventoryItem>();
        public long Meat { get; init; }
    }

    /// <summary>Was eine Strategie pro Runde sieht. Reiner Schnappschuss, keine Requests.</summary>
    public sealed record CombatContext(FightEncounter Fight, int Round, int Hp, int MaxHp, int Mp, int MaxMp, IReadOnlyList<FightRound> History)
    {
        public IReadOnlyList<ActiveEffect> Effects { get; init; } = Array.Empty<ActiveEffect>();
        public Inventory? Inventory { get; init; }
        /// <summary>Skills, die das Dropdown der Kampfseite gerade anbietet. Leer = die Seite hat keine Liste gezeigt (dann nichts ausschließen).</summary>
        public IReadOnlyList<CombatOption> AvailableSkills { get; init; } = Array.Empty<CombatOption>();
        /// <summary>Kampf-Items, die das Dropdown der Kampfseite gerade anbietet. Leer = keine Liste gezeigt.</summary>
        public IReadOnlyList<CombatOption> AvailableItems { get; init; } = Array.Empty<CombatOption>();
        public int? MonsterId => Fight.MonsterId;
        public bool IsTurtleMonster => false;
    }

    /// <summary>Ein Eintrag aus dem Skill- oder Item-Dropdown der Kampfseite. MpCost ist null, wenn weder Seite noch Spieldaten ihn kennen.</summary>
    public sealed record CombatOption(int Id, string Name, int? MpCost);

    /// <summary>Wählt die nächste Kampfaktion. Analog zu IChoiceResolver: synchron, rein, ohne Requests.</summary>
    public interface ICombatStrategy
    {
        CombatAction Next(CombatContext context);
    }

    public static class CombatStrategies
    {
        public static ICombatStrategy AttackOnly { get; } = null!;
        /// <summary>Jede Runde dasselbe Makro (KoL wertet es serverseitig aus). Experimentell in 0.5, siehe KolibriDiagnostics.CombatMacros.</summary>
        [Experimental(KolibriDiagnostics.CombatMacros)]
        public static ICombatStrategy Macro(string macro) => throw new NotImplementedException();
        /// <summary>Regel-DSL von 0.4 (AutoCombatPolicy.Parse), z.B. "if hp &lt; 30% then run; skill 1005 x3; attack".</summary>
        public static ICombatStrategy FromRules(string rules) => throw new NotImplementedException();
        /// <summary>Wie FromRules(rules), aber Skills und Items dürfen per Name stehen; unbekannte Namen oder fehlende Spieldaten werfen ArgumentException.</summary>
        public static ICombatStrategy FromRules(string rules, GameData data) => throw new NotImplementedException();
        /// <summary>Gibt bei einem Turtle-Monster den Kampf an den Menschen zurück (Stop).</summary>
        public static ICombatStrategy StopOnTurtle(ICombatStrategy inner) => throw new NotImplementedException();
        public static ICombatStrategy FromDelegate(Func<CombatContext, CombatAction> next) => throw new NotImplementedException();
    }

    // --- Choice ---------------------------------------------------------------------------

    public sealed record ChoiceContext(Location? Location, int ChoiceId, string? Title, IReadOnlyList<ChoiceOption> Options);

    public enum ChoiceDecisionSource { Configured, BuiltIn, Strategy, None }

    public sealed record ChoiceDecision(int? Option, ChoiceDecisionSource Source, string Reason)
    {
        /// <summary>Keine Entscheidung: der Lauf stoppt mit RunStopReason.NeedsChoice.</summary>
        public static ChoiceDecision Ask(string reason) => new(null, ChoiceDecisionSource.None, reason);
    }

    /// <summary>Wählt eine Option für eine Choice. Synchron, rein, ohne Requests.</summary>
    public interface IChoiceResolver
    {
        ChoiceDecision Resolve(ChoiceContext context);
    }

    public static class ChoiceResolvers
    {
        /// <summary>Löst nie auf; jeder Choice stoppt den Lauf.</summary>
        public static IChoiceResolver AlwaysAsk { get; } = null!;
        /// <summary>Feste Optionen je Choice-Id; unbekannte Choices stoppen.</summary>
        public static IChoiceResolver FromTable(IReadOnlyDictionary<int, int> optionByChoiceId) => throw new NotImplementedException();
        /// <summary>Tabelle plus eingebaute kostenfreie Optionen (heute ChoiceResolutionMode.AutoSafe).</summary>
        public static IChoiceResolver Safe(IReadOnlyDictionary<int, int>? optionByChoiceId = null) => throw new NotImplementedException();
    }

    // --- Läufe ----------------------------------------------------------------------------

    public sealed record RunRequest(Location Location, int MaxTurns)
    {
        public ICombatStrategy Combat { get; init; } = CombatStrategies.AttackOnly;
        public IChoiceResolver Choices { get; init; } = ChoiceResolvers.AlwaysAsk;
        public IReadOnlyList<StopCondition> StopWhen { get; init; } = Array.Empty<StopCondition>();
        public int MaxRoundsPerFight { get; init; } = 30;
        /// <summary>Erholung und Upkeep vor jedem neuen Zug (nicht mitten in einer Kette). Standard: keine.</summary>
        public IRecoveryPolicy Recovery { get; init; } = RecoveryPolicies.None;
        /// <summary>Obergrenze der Erholungsschritte vor einem Zug.</summary>
        public int MaxRecoveryStepsPerTurn { get; init; } = 5;
    }

    public enum StopConditionKind { HpBelowPercent, MpBelowPercent, AdventuresAtMost, ItemCountAtLeast, EffectMissing, MeatAtMost, MonsterSeen, EncounterTitle, TurtleEncounter }

    public sealed record StopCondition(StopConditionKind Kind, int Value, int? Id = null)
    {
        /// <summary>Monstername (MonsterSeen) bzw. regulärer Ausdruck (EncounterTitle); sonst null.</summary>
        public string? Pattern { get; init; }
        public static StopCondition HpBelowPercent(int percent) => new(StopConditionKind.HpBelowPercent, percent);
        public static StopCondition MpBelowPercent(int percent) => new(StopConditionKind.MpBelowPercent, percent);
        public static StopCondition AdventuresAtMost(int adventures) => new(StopConditionKind.AdventuresAtMost, adventures);
        public static StopCondition ItemCountAtLeast(ItemRef item, int count) => new(StopConditionKind.ItemCountAtLeast, count, item.Id);
        public static StopCondition EffectMissing(int effectId) => new(StopConditionKind.EffectMissing, 0, effectId);
        /// <summary>Stoppt nach dem Zug, in dem ein Kampf gegen dieses Monster vorkam.</summary>
        public static StopCondition MonsterSeen(string name) => throw new NotImplementedException();
        /// <summary>Stoppt nach dem Zug, dessen Begegnungstitel auf den regulären Ausdruck passt.</summary>
        public static StopCondition EncounterTitle(string regex) => throw new NotImplementedException();
        /// <summary>Stoppt nach dem Zug mit einer Turtle-Tamer-Begegnung.</summary>
        public static StopCondition TurtleEncounter() => throw new NotImplementedException();
    }

    public enum RunStopReason { TurnsDone, Condition, NeedsChoice, NeedsCombat, Defeat, Blocked, Cancelled, Error, Recovery }

    public sealed record RunResult(RunStopReason StopReason, int TurnsSpent, IReadOnlyList<Encounter> Encounters, string? Detail)
    {
        /// <summary>Offene Begegnung bei NeedsChoice/NeedsCombat: dort kann ein Mensch weitermachen.</summary>
        public Encounter? Open { get; init; }
        public IReadOnlyList<InventoryItem> Items { get; init; } = Array.Empty<InventoryItem>();
        public long MeatGained { get; init; }
        /// <summary>Die Erholungs- und Upkeep-Schritte, die der Lauf ausgeführt hat.</summary>
        public IReadOnlyList<RecoveryStep> RecoverySteps { get; init; } = Array.Empty<RecoveryStep>();
    }

    public enum RecoveryStepKind { None, Rest, UseItem, Cast, Stop, NeedInventory }

    public sealed record RecoveryStep(RecoveryStepKind Kind, int Id = 0, int Times = 1, string? Reason = null)
    {
        public static RecoveryStep None { get; } = null!;
        public static RecoveryStep Rest { get; } = null!;
        public static RecoveryStep NeedsInventory { get; } = null!;
        public static RecoveryStep UseItem(ItemRef item) => throw new NotImplementedException();
        public static RecoveryStep Cast(SkillRef skill, int times = 1) => throw new NotImplementedException();
        public static RecoveryStep Stop(string reason) => throw new NotImplementedException();
    }

    public sealed record RecoveryContext(Status Status, Inventory? Inventory, IReadOnlyList<RecoveryStep> DoneThisTurn);

    public interface IRecoveryPolicy
    {
        RecoveryStep Next(RecoveryContext context);
    }

    public static class RecoveryPolicies
    {
        public static IRecoveryPolicy None { get; } = null!;
        public static IRecoveryPolicy Simple(int hpBelowPercent, int mpBelowPercent, IReadOnlyList<RecoveryStep> hpSteps, IReadOnlyList<RecoveryStep> mpSteps) => throw new NotImplementedException();
        public static IRecoveryPolicy Upkeep(int effectId, int minTurns, RecoveryStep how) => throw new NotImplementedException();
        public static IRecoveryPolicy Combine(params IRecoveryPolicy[] policies) => throw new NotImplementedException();
    }
}

// ---------------------------------------------------------------------------------------------
// 6. Mall: Suche und Kauf
// ---------------------------------------------------------------------------------------------
namespace KoLibri
{
    public sealed class Mall
    {
        private Mall() { }
        public Task<MallSearchResult> SearchAsync(string term, bool refresh = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<MallOffers> GetOffersAsync(ItemRef item, bool refresh = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        /// <summary>Plant einen Kauf gegen frische Angebote; kein Request an einen Shop.</summary>
        public Task<PurchaseQuote> QuoteAsync(PurchaseOrder order, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        /// <summary>Kauft laut Quote. Liest Preise neu, kauft nie über MaxUnitPrice, prüft danach Inventar und Meat.</summary>
        public Task<PurchaseResult> BuyAsync(PurchaseQuote quote, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        /// <summary>Quote und Kauf in einem Schritt.</summary>
        public Task<PurchaseResult> QuoteAndBuyAsync(PurchaseOrder order, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        /// <summary>Preisverlauf von pricegun.loathers.net (gecacht, gedrosselt). Null bei Fehler.</summary>
        public Task<PriceHistory?> GetPriceHistoryAsync(ItemRef item, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    public sealed record MallListing(int StoreId, string StoreName, ItemRef Item, long UnitPrice, int Quantity, int? LimitPerDay)
    {
        /// <summary>False: KoL zeigte nicht, ob es ein Tageslimit gibt. Ergänzt in Schritt 3 (Arbitrage).</summary>
        public bool LimitKnown { get; init; } = true;
        public bool QuantityKnown { get; init; } = true;
    }
    public sealed record MallOffers(ItemRef Item, string Name, IReadOnlyList<MallListing> Listings, DateTimeOffset FetchedAt);
    public sealed record MallSearchResult(string Term, IReadOnlyList<MallOffers> Items, DateTimeOffset FetchedAt);

    public sealed record PurchaseOrder(ItemRef Item, int Quantity, long MaxUnitPrice)
    {
        /// <summary>Meat, das nach dem Kauf mindestens übrig bleiben muss.</summary>
        public long MeatReserve { get; init; }
        public IReadOnlyList<int> ExcludedStoreIds { get; init; } = Array.Empty<int>();
    }

    public sealed record PurchaseQuote(PurchaseOrder Order, IReadOnlyList<MallListing> Plan, long TotalCost, DateTimeOffset QuotedAt)
    {
        public int PlannedQuantity { get; init; }
        public string? Shortfall { get; init; }
    }

    public enum PurchaseStatus { Bought, Partial, NothingBought, PriceChanged, Refused, Unclear }

    public sealed record PurchaseResult(PurchaseStatus Status, int QuantityBought, long MeatSpent, IReadOnlyList<PurchaseLine> Lines);
    public sealed record PurchaseLine(int StoreId, long UnitPrice, int Requested, int Bought, string? Message);

    public sealed record PriceHistory(ItemRef Item, long? Value, long? Volume, IReadOnlyList<PricePoint> Points, DateTimeOffset FetchedAt);
    public sealed record PricePoint(DateTimeOffset At, long Price, long Volume);
}

// ---------------------------------------------------------------------------------------------
// 7. Eigener Shop, Pirate-Keys, Kmail, Daily-Marker
// ---------------------------------------------------------------------------------------------
namespace KoLibri
{
    public sealed class Shop
    {
        private Shop() { }
        public Task<IReadOnlyList<ShopListing>> GetListingsAsync(bool refresh = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ActionResult> AddAsync(ItemRef item, int quantity, long price, int limitPerDay = 0, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ActionResult> RepriceAsync(IReadOnlyList<ShopPriceChange> changes, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ActionResult> TakeBackAsync(ItemRef item, int quantity, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ShopSale>> GetSalesAsync(bool refresh = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    public sealed record ShopListing(ItemRef Item, string Name, int Quantity, long Price, int LimitPerDay);
    public sealed record ShopPriceChange(ItemRef Item, long Price, int? LimitPerDay = null);
    public sealed record ShopSale(DateTimeOffset At, string Buyer, ItemRef Item, int Quantity, long Meat)
    {
        /// <summary>Itemname aus dem Verkaufslog. Ergänzt in Schritt 3 (CLI crm).</summary>
        public string ItemName { get; init; } = string.Empty;
    }

    public sealed class PirateKeys
    {
        private PirateKeys() { }
        public Task<PirateKeyCounts> GetCountsAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<PirateKeyResult> AssembleAsync(int count, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ChoiceOption>> GetRareOptionsAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<PirateKeyResult> RedeemAsync(ItemRef rare, int count, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        /// <summary>Die seltenen Items, zu denen ein Schlüssel werden kann (offline). Ergänzt in Schritt 3 (CLI keys).</summary>
        public static IReadOnlyList<ChoiceOption> RareOptions { get; } = Array.Empty<ChoiceOption>();
    }

    public sealed record PirateKeyCounts(int Parts, int Keys)
    {
        /// <summary>Einzelteile. Ergänzt in Schritt 3 (CLI keys status).</summary>
        public int Alpha { get; init; }
        public int Bravo { get; init; }
        public int Charlie { get; init; }
        public int Delta { get; init; }
    }
    public sealed record PirateKeyResult(int Requested, int Done, PirateKeyCounts Before, PirateKeyCounts After, string? Message)
    {
        /// <summary>Ergänzt in Schritt 3 (CLI keys assemble|redeem).</summary>
        public bool Unclear { get; init; }
        public IReadOnlyList<PirateKeyStep> Steps { get; init; } = Array.Empty<PirateKeyStep>();
    }
    public sealed record PirateKeyStep(int Number, bool Confirmed, string? Message, IReadOnlyDictionary<int, int> InventoryDelta);

    public enum MailFolder { Inbox, Saved, Outbox, Pvp }

    public sealed class Mailbox
    {
        private Mailbox() { }
        public Task<MailPage> GetAsync(MailFolder folder = MailFolder.Inbox, int page = 1, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<MailSendResult> SendAsync(MailMessage message, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task SaveAsync(string messageId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task DeleteAsync(MailFolder folder, string messageId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    public sealed record MailMessage(string Recipient, string Text)
    {
        public IReadOnlyList<InventoryItem> Items { get; init; } = Array.Empty<InventoryItem>();
        public long Meat { get; init; }
    }
    public enum MailSendStatus { Sent, Refused, Unclear }
    public sealed record MailSendResult(MailSendStatus Status, string? Message);
    public sealed record MailEntry(string Id, string From, DateTimeOffset? At, string Text, IReadOnlyList<InventoryItem> Items, long Meat);
    public sealed record MailPage(MailFolder Folder, int Page, int PageCount, IReadOnlyList<MailEntry> Entries);

    /// <summary>Einmal-pro-KoL-Tag-Marker (Rollover 03:30 Arizona) je Konto. Baustein für eigene Daily-Routinen.</summary>
    public sealed class DailyMarkers
    {
        private DailyMarkers() { }
        public DateOnly GameDay => throw new NotImplementedException();
        public bool IsDone(string key) => throw new NotImplementedException();
        public void MarkDone(string key) => throw new NotImplementedException();
        public IReadOnlyList<string> DoneToday() => throw new NotImplementedException();
    }
}

// ---------------------------------------------------------------------------------------------
// 8. Events und Guard-Hook
// ---------------------------------------------------------------------------------------------
namespace KoLibri
{
    /// <summary>
    /// Events werden nach dem Parsen synchron auf dem aufrufenden Thread ausgelöst. Handler dürfen keine
    /// Requests derselben Sitzung starten (Action-Lane ist belegt) und sollten schnell sein. Ausnahmen in
    /// Handlern werden gefangen und als HandlerFailed gemeldet; sie brechen die Aktion nie ab.
    /// </summary>
    public sealed class KolibriEvents
    {
        private KolibriEvents() { }
        public event EventHandler<RequestEventArgs>? Request;
        public event EventHandler<EncounterEventArgs>? Encounter;
        public event EventHandler<FightEndedEventArgs>? FightEnded;
        public event EventHandler<ItemAcquiredEventArgs>? ItemAcquired;
        public event EventHandler<StatusChangedEventArgs>? StatusChanged;
        public event EventHandler<HandlerFailedEventArgs>? HandlerFailed;
    }

    public abstract class KolibriEventArgs : EventArgs
    {
        protected KolibriEventArgs(AccountId account, DateTimeOffset at) { Account = account; At = at; }
        public AccountId Account { get; }
        public DateTimeOffset At { get; }
    }

    public enum RequestMethod { Get, Post }

    /// <summary>Ein HTTP-Request an KoL. Path ist redigiert (kein pwd, kein Passwort, keine Cookies).</summary>
    public sealed class RequestEventArgs : KolibriEventArgs
    {
        public RequestEventArgs(AccountId account, DateTimeOffset at, RequestMethod method, string path, RequestLane lane, int statusCode, TimeSpan duration, TimeSpan waited, PageKind kind)
            : base(account, at) { Method = method; Path = path; Lane = lane; StatusCode = statusCode; Duration = duration; Waited = waited; Kind = kind; }
        public RequestMethod Method { get; }
        public string Path { get; }
        public RequestLane Lane { get; }
        public int StatusCode { get; }
        public TimeSpan Duration { get; }
        /// <summary>Zeit, die der Limiter vor dem Senden gewartet hat.</summary>
        public TimeSpan Waited { get; }
        public PageKind Kind { get; }
    }

    public sealed class EncounterEventArgs : KolibriEventArgs
    {
        public EncounterEventArgs(AccountId account, DateTimeOffset at, Encounter encounter) : base(account, at) => Encounter = encounter;
        public Encounter Encounter { get; }
    }

    public sealed class FightEndedEventArgs : KolibriEventArgs
    {
        public FightEndedEventArgs(AccountId account, DateTimeOffset at, FightResult result) : base(account, at) => Result = result;
        public FightResult Result { get; }
    }

    public enum ItemSource { Fight, Choice, NonCombat, Mall, Mail, Use, Other }

    public sealed class ItemAcquiredEventArgs : KolibriEventArgs
    {
        public ItemAcquiredEventArgs(AccountId account, DateTimeOffset at, InventoryItem item, ItemSource source) : base(account, at) { Item = item; Source = source; }
        public InventoryItem Item { get; }
        public ItemSource Source { get; }
    }

    public sealed class StatusChangedEventArgs : KolibriEventArgs
    {
        public StatusChangedEventArgs(AccountId account, DateTimeOffset at, Status? before, Status after) : base(account, at) { Before = before; After = after; }
        public Status? Before { get; }
        public Status After { get; }
    }

    public sealed class HandlerFailedEventArgs : KolibriEventArgs
    {
        public HandlerFailedEventArgs(AccountId account, DateTimeOffset at, string eventName, Exception exception) : base(account, at) { EventName = eventName; Exception = exception; }
        public string EventName { get; }
        public Exception Exception { get; }
    }

    public enum GuardedActionKind { Adventure, Fight, Choice, Use, Consume, Cast, Rest, Equip, MallBuy, ShopChange, SendMail, RawPost, Other }

    /// <summary>Beschreibung einer verändernden Aktion, bevor der Request rausgeht.</summary>
    public sealed record GuardedAction(AccountId Account, GuardedActionKind Kind, string Summary)
    {
        public ItemRef? Item { get; init; }
        public int Quantity { get; init; }
        public long Meat { get; init; }
        public string? Path { get; init; }
    }

    public sealed record GuardDecision(bool Allowed, string? Reason)
    {
        public static GuardDecision Allow { get; } = new(true, null);
        public static GuardDecision Deny(string reason) => new(false, reason);
    }

    /// <summary>Einziger Policy-Hook im Kern. Freigaben, Budgets und Audit hängen sich hier an (KoLibri.Safety).</summary>
    public interface IActionGuard
    {
        ValueTask<GuardDecision> CheckAsync(GuardedAction action, CancellationToken cancellationToken);
        /// <summary>Nach dem Request, auch bei Fehler. Für Ledger und Audit.</summary>
        ValueTask CompletedAsync(GuardedAction action, bool succeeded, CancellationToken cancellationToken);
    }
}

#endif

#if KOLIBRI_SAFETY
// ---------------------------------------------------------------------------------------------
// 9. Optionales Paket KoLibri.Safety (eigene Assembly, hängt nur an KoLibri)
// ---------------------------------------------------------------------------------------------
namespace KoLibri.Safety
{
    /// <summary>Kombiniert mehrere Guards; der erste Deny gewinnt.</summary>
    public sealed class CompositeGuard : IActionGuard
    {
        public CompositeGuard(params IActionGuard[] guards) { }
        public ValueTask<GuardDecision> CheckAsync(GuardedAction action, CancellationToken cancellationToken) => throw new NotImplementedException();
        public ValueTask CompletedAsync(GuardedAction action, bool succeeded, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    // --- Rechte / Grants (heute LibKolibri.Access) -----------------------------------------
    [Flags]
    public enum Rights { None = 0, Read = 1, Adventure = 2, Consume = 4, MallBuy = 8, ShopChange = 16, SendMail = 32, RawPost = 64, All = 127 }

    public sealed record Grant(string Subject, AccountId Account, Rights Rights, DateTimeOffset? ExpiresAt)
    {
        public long? MeatPerDay { get; init; }
        public int? TurnsPerDay { get; init; }
    }

    public interface IGrantStore
    {
        Task<IReadOnlyList<Grant>> ListAsync(CancellationToken cancellationToken);
        Task SaveAsync(Grant grant, CancellationToken cancellationToken);
        Task RevokeAsync(string subject, AccountId account, CancellationToken cancellationToken);
    }

    public sealed class JsonGrantStore : IGrantStore
    {
        public JsonGrantStore(string path) { }
        public Task<IReadOnlyList<Grant>> ListAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task SaveAsync(Grant grant, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task RevokeAsync(string subject, AccountId account, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    /// <summary>Lehnt Aktionen ab, für die der Subject keinen Grant hat (heute AccessGate).</summary>
    public sealed class GrantGuard : IActionGuard
    {
        public GrantGuard(string subject, IGrantStore grants, UsageLedger ledger) { }
        public ValueTask<GuardDecision> CheckAsync(GuardedAction action, CancellationToken cancellationToken) => throw new NotImplementedException();
        public ValueTask CompletedAsync(GuardedAction action, bool succeeded, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    // --- Budgets / Ledger ------------------------------------------------------------------
    public sealed record Budget(AccountId Account, long MeatPerDay, int? TurnsPerDay = null, long? MaxUnitPrice = null);

    /// <summary>Verbrauch je Konto und KoL-Tag, dateibasiert, über Prozesse hinweg (heute UsageLedger + SharedMallSpendLedger).</summary>
    public sealed class UsageLedger
    {
        public UsageLedger(string path) { }
        public long MeatSpentToday(AccountId account) => throw new NotImplementedException();
        public int TurnsSpentToday(AccountId account) => throw new NotImplementedException();
        public void Record(AccountId account, long meat, int turns) => throw new NotImplementedException();
    }

    public sealed class BudgetGuard : IActionGuard
    {
        public BudgetGuard(IReadOnlyList<Budget> budgets, UsageLedger ledger) { }
        public ValueTask<GuardDecision> CheckAsync(GuardedAction action, CancellationToken cancellationToken) => throw new NotImplementedException();
        public ValueTask CompletedAsync(GuardedAction action, bool succeeded, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    // --- Freigaben (Approval-Token) --------------------------------------------------------
    public sealed record ApprovalToken(string Value, GuardedAction Action, DateTimeOffset ExpiresAt);

    /// <summary>Verlangt für ausgewählte Aktionsarten ein vorher ausgestelltes Token (heute ApprovalTokenStore).</summary>
    public sealed class ApprovalGuard : IActionGuard
    {
        public ApprovalGuard(IReadOnlySet<GuardedActionKind> requireApprovalFor, TimeSpan tokenLifetime) { }
        public ApprovalToken Issue(GuardedAction action) => throw new NotImplementedException();
        /// <summary>Hinterlegt ein Token für die nächste passende Aktion dieses Kontos.</summary>
        public void Present(string token) => throw new NotImplementedException();
        public ValueTask<GuardDecision> CheckAsync(GuardedAction action, CancellationToken cancellationToken) => throw new NotImplementedException();
        public ValueTask CompletedAsync(GuardedAction action, bool succeeded, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    // --- Audit -----------------------------------------------------------------------------
    public sealed record AuditEntry(long Sequence, DateTimeOffset At, AccountId Account, string Kind, string Summary, string Hash, string PreviousHash);

    /// <summary>JSONL-Journal mit Hash-Kette. Hängt sich an KolibriEvents und an den Guard.</summary>
    public sealed class AuditJournal : IActionGuard, IDisposable
    {
        public AuditJournal(string path) { }
        public void Attach(KolibriEvents events) => throw new NotImplementedException();
        public IReadOnlyList<AuditEntry> Read(DateTimeOffset? since = null) => throw new NotImplementedException();
        /// <summary>Prüft die Hash-Kette; liefert die erste kaputte Sequenz oder null.</summary>
        public long? Verify() => throw new NotImplementedException();
        public ValueTask<GuardDecision> CheckAsync(GuardedAction action, CancellationToken cancellationToken) => throw new NotImplementedException();
        public ValueTask CompletedAsync(GuardedAction action, bool succeeded, CancellationToken cancellationToken) => throw new NotImplementedException();
        public void Dispose() => throw new NotImplementedException();
    }
}
#endif
