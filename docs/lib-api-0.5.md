# KoLibri 0.5: Bestandsaufnahme und API-Entwurf

Status: **Entwurf entschieden (§8), Schritt 3 umgesetzt: Lib (§11) und CLI (§12).** Grundlage ist `release/0.4.0` @ `911517b` (nach dem Tag `v0.4.0`).
Der Entwurf selbst baute keine App um; den Umsetzungsstand beschreibt §11. Das Dokument deckt Schritt 1 und 2 des Lib-Umbaus aus
[ROADMAP.md](../ROADMAP.md) (Abschnitt 0.5) ab.

Zum Dokument gehören diese Dateien:

| Datei | Inhalt |
|---|---|
| [lib-api-0.5/KoLibri.Api.cs](lib-api-0.5/KoLibri.Api.cs) | Der Entwurf als kompilierbarer Stub. Er ist die **Quelle der Wahrheit** für alle Signaturen hier. |
| [../src/KoLibri/PublicAPI.Unshipped.txt](../src/KoLibri/PublicAPI.Unshipped.txt) | `PublicAPI.Shipped.txt` und `PublicAPI.Unshipped.txt` des Pakets `KoLibri` (seit Schritt 3 im Projekt, vorher unter `lib-api-0.5/publicapi/`) |
| [../src/KoLibri.Safety/PublicAPI.Unshipped.txt](../src/KoLibri.Safety/PublicAPI.Unshipped.txt) | Dasselbe für das optionale Paket `KoLibri.Safety` |
| [lib-api-0.5/inventory.md](lib-api-0.5/inventory.md) | Generierte Typ-Inventur: jeder öffentliche Typ heute, nach Fähigkeit gruppiert, mit App-Nutzung |
| [lib-api-0.5/entry-methods.md](lib-api-0.5/entry-methods.md) | Generierte Methoden-Inventur der rund 45 Einstiegsklassen |
| [lib-api-0.5/check-api.sh](lib-api-0.5/check-api.sh) | Prüft Stub und echte Projekte gegen die PublicAPI-Dateien und erzeugt sie bei Bedarf aus dem Stub neu |

---

## 1. Befund in Zahlen

Gezählt wurden alle `public`-Typdeklarationen, auch verschachtelte, in `src/KoLibri.Core` und
`src/LibKolibri/src/LibKolibri`. Ihre Namen wurden per Wort-Abgleich im Code von CLI (`tools/KolLibri.Cli`),
MCP (`tools/KolLibri.Mcp`) und Desktop (`KoLibri.Desktop`) gesucht. Das ist eine Heuristik: Gleichnamige Typen in den
Apps erzeugen falsche Treffer, deshalb sind die Zahlen eher zu hoch als zu niedrig.

| | Typen |
|---|---:|
| Öffentliche Typen heute (Core + LibKolibri) | **1.195** |
| davon von mindestens einer App genutzt | **415** (CLI 195, MCP 154, Desktop 269) |
| nur von Tests, `OfflineDemo`, `UiQa` oder `ConsumerSmoke` genutzt | 228 |
| nirgends außerhalb der eigenen Datei genannt (tot) | **330** |
| **Entwurf 0.5: Paket `KoLibri`** | **108** |
| **Entwurf 0.5: Paket `KoLibri.Safety`** (optional) | **13** |

Nach Fähigkeit (aus [inventory.md](lib-api-0.5/inventory.md)):

| Fähigkeit | Typen heute | von Apps genutzt | nur Tests/Demos | tot |
|---|---:|---:|---:|---:|
| Session/Login | 45 | 19 | 18 | 8 |
| Roher Seitenabruf über Limiter | 31 | 2 | 18 | 11 |
| Status/Inventar (inkl. KoLmafia-Daten) | 71 | 20 | 13 | 38 |
| Konsum/Rest/Cast/Equip | 50 | 38 | 1 | 11 |
| Abenteuer/Kampf/Choice | 291 | 118 | 53 | 120 |
| Mall-Suche/-Kauf | 76 | 37 | 15 | 24 |
| Eigener Shop | 19 | 6 | 4 | 9 |
| Pirate-Keys | 6 | 3 | 1 | 2 |
| Daily | 90 | 38 | 7 | 45 |
| Kmail | 6 | 6 | 0 | 0 |
| Relay | 3 | 2 | 1 | 0 |
| Safety: Freigaben, Budgets, Ledger, Audit | 146 | 28 | 29 | 89 |
| M0M1-/Gateway-Schicht | 164 | 49 | 26 | 89 |
| Extras: Arbitrage, CRM, Analytics, Coinmaster, Clan, Chat … | 197 | 49 | 42 | 106 |

Was die Zahlen zeigen:

1. **Abenteuer/Kampf ist der größte und der zersplittertste Block.** Rund ein Dutzend Läufer und Gateways stehen
   nebeneinander, und die Apps benutzen fast alle davon: `AdventureExecutionGateway`, `BoundedAdventureRunner`,
   `BoundedChoiceChainRunner`, `AdventureRunKernel`, `AdventureManualDriver`, `ManualAdventureSession`,
   `KoLAdventureSession`, `ManualCombatGateway`, `LiveManualCombatTransport`, `AutoCombatRunner`,
   `AutoCombatAdventureAdapter`, `AutoCombatDailyBridge` und `DailyAdventurePlanExecutor`. Ungenutzt sind
   `BoundedManualCombatRunner` und `AutoCombatBatchRunner`. Dazu kommen die eigenen Läufer im Desktop
   (`DesktopAdventureRunBridge`, `AdventureJob`, der Batch in `EncounterBrowserSessionViewModel`).
2. **Rohe Requests haben keine öffentliche Form.** Die Apps bauen ihren `HttpClient` selbst über
   `KoLLoginClient.CreateAuthenticatedClient` (CLI 7×, Desktop 4×, MCP 1×). Allowlist und Limiter hängen darin, aber
   Events, Reconnect und Guard fehlen. Die zweite Request-Welt (`KoLRequestOrchestrator`, `KoLRequestCoordination`,
   31 Typen) nutzt keine App.
3. **Die Rollen sind überall verdrahtet.** `AccountRole.Main`/`Mallbot` steht 36× in der CLI, 15× im MCP und 40× im
   Desktop. Auch `RelayHost`, `KoLAdventureSessionFactory`, `KolibriClient.OpenAsync` und `AccountIdentityVerifier`
   bekommen eine Rolle übergeben.
4. **Policy steckt im Kern.** Approval-Tokens, Grants, Usage-Ledger, Spend-Ledger und Audit-Hash-Kette umfassen
   146 Typen. Die Apps nutzen davon 28, vor allem das MCP (`AccessGate`, `GrantStore`, `UsageLedger`).
5. **Die M0M1-Schicht lebt fast nur noch im Desktop.** Die Treffer kommen aus Vorschau-ViewModels
   (`ArbitrageViewModel`, `ApprovalViewModel`, `ShopViewModel`, `OperationsViewModel`). Das MCP nutzt die
   Rollen-Runtime (`RoleScopedRuntime`, `McpRuntimeBootstrap`, `CoreAccountGateway`, `CoreMallGateway`).
6. **Viele Aliase.** Zum Beispiel `AdventureRunKernel.Begin`/`Start` und `Observe`/`Advance`,
   `AdventureExecutionGateway.StartAsync`/`BeginAsync` und `StepAsync`/`AdvanceAsync`/`ExecutePendingAsync`,
   `MallPurchaseGateway.Preview`/`PreviewAsync`/`CreatePreviewAsync`, `CombatActionKind.UseItem = Item`,
   `CombatOutcome.Won = Victory` sowie `AutoCombatContext.MonsterId`/`MonsterName` als Aliase von `Monster`.
   Doppelte Typnamen in zwei Namespaces sind `AuditEvent`, `AuditEventKind`, `ArbitrageQuery` und `TransferRequest`.

---

## 2. Bestandsaufnahme: was die Apps wirklich nutzen

Legende: **C** = CLI, **M** = MCP, **D** = Desktop. Die vollständigen Listen stehen in
[inventory.md](lib-api-0.5/inventory.md) (Typen) und [entry-methods.md](lib-api-0.5/entry-methods.md) (Methoden).
Hier steht nur der Kern jeder Fähigkeit.

### 2.1 Session/Login

| Heute | Genutzt von | Methoden |
|---|---|---|
| `KoLLoginClient` | CMD | `LoginAsync`, `CreateAuthenticatedClient` |
| `AccountSessionManager` | CMD | `CreateDefault`, `InitializeAsync`, `GetSessionAsync(role)`, `SetCredentialsAsync(role)`; nur C/M: `GetAccountsAsync`, `GetProfileAsync`; nur M: `GetRequiredSessionAsync`; nur C/D: `ReconnectAsync`, `SetProfileAsync`; nur C: `ApplyVerifiedLoginAsync`, `ClearCredentialsAsync` |
| `AccountSession`, `AccountCredentials`, `AccountProfile`, `AccountRole` | CMD | `IsExpired`, `BindingKey` |
| `AccountIdentityVerifier`, `KoLPlayerIdentity`, `AccountIdentityMismatchException` | C | `VerifyRoleAsync` |
| `KoLPwdHashResolver` | CMD | `RequireAsync` (D), `Forget` (MD), `TryReadIdentityAsync` (C) |
| `LibKolibri.KolibriClient` | CM | `LoginAsync(role, …)`, `OpenAsync(role)` |
| ungenutzt | – | `SessionHealthEvaluator` (+6 Modelltypen), `JsonFileAccountStore`/`FileSecretStore`/`KeyProtection` (nur intern) |

### 2.2 Roher Seitenabruf über Limiter

| Heute | Genutzt von | Anmerkung |
|---|---|---|
| `KoLLoginClient.CreateAuthenticatedClient` → `KoLAllowlistHandler` → `KoLRateLimitedHandler` | CMD | Die Apps senden mit diesem `HttpClient` selbst, vor allem in der CLI (Daily, Mall-Scan, Login) und im Desktop (Daily, AutoCombat). |
| `KoLRateLimiter` | D (`ApplyOptions`, `EffectiveInterval`, `IsEnvironmentOverride`), CM (`WaitAsync`) | Lanes `Action`/`Read` existieren, `AcquireAsync(lane)` nutzt keine App direkt. |
| `KoLRequestOrchestrator`, `KoLRequestCoordination`, `KoLRequestContracts`, `HttpClientKoLRequestTransport`, `KoLResponseClassifier` | – | zweite, ungenutzte Request-Welt (nur Tests) |
| `KoLHtmlResponseKind`, `KoLHtmlResponseException` | MD | Klassifiziert Login-, Wartungs- und Fehlerseiten |

### 2.3 Status/Inventar

| Heute | Genutzt von | Methoden |
|---|---|---|
| `KoLAccountReadClient` / `IAccountReadClient` | CMD | `FetchStatusAsync`, `FetchInventoryAsync`; nur C: `FetchStorageAsync`. Ungenutzt sind `FetchClosetAsync`, `FetchQuestLogAsync`, `FetchBatHoleMapAsync` und `FetchFriarsPageAsync`. Clan-, Chat- und Mail-Methoden nutzt nur D (→ Extras, Kmail). |
| `KoLStatusSnapshot`, `KoLInventorySnapshot`, `KoLInventoryItem` | CMD | |
| `ItemClassifier`, `ItemCategory`, `ItemOrgan`, `ItemOrganCost` | D (M: `Matches`) | |
| `MafiaDataLoader`, `KoLmafiaCatalog`, `MafiaItemRecord`, `MafiaMonsterRecord`, `BundledItemNames` | CMD | `Load(dir)`, `DefaultDirectory()` |
| ungenutzt | – | `InventoryStateContracts` (16 Typen, nur Tests), `ItemMaster` (13), `ItemMetadata` (4, tot), `OrganLimitDeriver` (intern) |

### 2.4 Konsum/Rest/Cast/Equip

| Heute | Genutzt von | Methoden |
|---|---|---|
| `CharacterActionClient` / `LibKolibri.CharacterSession` | CMD | `ExecuteAsync(CharacterAction)`, `RestAsync`, `ReadAsync`; C/D: `CastAsync`; nur D: `EnsureEffectAsync`, `ReadFriarsAsync`, `ReadStorageAsync` |
| `CharacterAction`-Hierarchie: `UseInventoryItem`, `ConsumeCharacterItem`, `CastCharacterSkill`, `RestAtCampground`, `EquipInventoryItem`, `UnequipInventoryItem`, `WearCharacterOutfit`, `SelectCharacterFamiliar`, `EquipCharacterFamiliar`, `MoveClosetItem`, `PullStorageItem`, `CombineCharacterItems`, Friar-Typen | D (Use: C) | |
| `KolibriClient.ConsumeAsync`, `UseItemAsync`, `RestAsync`, `CastSkillAsync` | CM | Tupel-Rückgaben `(int Consumed, string? Rejection)` |
| `RecoveryPlanner`, `RecoveryPolicy`, `RecoverySnapshot`, `RecoveryPlan` | CM | HP/MP-Restore-Planung |

### 2.5 Abenteuer/Kampf/Choice

| Heute | Genutzt von | Methoden |
|---|---|---|
| `AdventureExecutionGateway` | CMD | `ExecuteAsync(request, allowlist)`, MD: `RefreshAsync`; nur D: `StartAsync`, `ResumeAsync`, `StepAsync`, `ExecutePendingAsync`, `RefreshEncounterAsync` |
| `KoLAdventureSessionFactory` / `KoLAdventureSession` | CMD | `CreateAsync(role, approval, verification, …)`, `DetectOpenChoiceAsync`, D: `SubmitOpenChoiceAsync` |
| `LibKolibri.ManualAdventureSession`, `AdventureManualDriver` | CMD | `OpenAsync(role, …)`, `ExecuteAsync(runId, allowlist, step)`; nur D: `StartAsync`, `RunTurnsAsync`, `ReadLocationAccessAsync`, `ReadStatusAsync` |
| `BoundedAdventureRunner`, `BoundedChoiceChainRunner` | CMD | `RunAsync` |
| `AdventureRunKernel` | C | `Start`/`Begin`, `Observe` |
| `ManualCombatGateway`, `LiveManualCombatTransport` | CMD | `PreviewAsync`, `ExecuteAsync`; nur D: `InspectAsync`, `CommitAsync`, `ApproveAsync`, `StartAdventureAsync`, `SubmitAsync` |
| `AutoCombatPolicy`, `AutoCombatContext`, `AutoCombatDecision`, `AutoCombatRule` | CMD | `Parse`, `Evaluate`, `Validate`; CM: `FromJson`; D: `Decide` |
| `AutoCombatRunner` | CMD | `RunAsync(policy, combatId, …)` |
| `OpenChoiceClient`, `OpenChoiceDetection` | CMD | `Parse`, CD: `DetectAsync`, `SubmitAsync` |
| `IChoiceResolver`, `ChoiceResolver`, `ChoiceResolutionConfigStore`, `TurtleTamerEncounters` | CMD (Interface: D) | `Resolve(ChoiceResolutionRequest)` |
| `AdventureRunRequest`, `AdventureActionAllowlist`, `AdventureCondition*`, `AdventureStepSpec`-Familie | CMD | |
| `CombatObservation`, `CombatOutcome`, `KoLCombatObservationParser`, `KoLAdventureEncounterParser` | CMD / C | Parser sind öffentlich, werden aber nur von C direkt aufgerufen |
| `AdventureLocationIds`, `AdventureLocationCatalog`, `KoLQuestAccess` | CMD | |
| `UnknownPageArchive` | CD | |

### 2.6 Mall-Suche/-Kauf

| Heute | Genutzt von | Methoden |
|---|---|---|
| `KoLMallReadClient` / `IMallReadClient` | CMD | `SearchAsync(itemId)`; D: `SearchItemsAsync(term)`, `Invalidate` |
| `MallPurchaseGateway` + `HttpMallPurchaseTransport` + 12 Vertragstypen | CMD | `Preview`/`PreviewAsync`; MD: `RefreshAsync`; CD: `Refresh`; D: `CommitAsync`, `PurchaseNowAsync`; M: `Commit` |
| `MallBuyClient` | CMD | `BuyAsync(credentials, storeId, itemId, price, qty, pwd)`; C: `ReadPasswordHashAsync` |
| `KolibriClient.SearchMallAsync`, `BuyCheapAsync` | MD / M | `BuyCheapAsync` liefert `BaconArbitrageExecutor.Outcome`, ein Name aus der Extras-Welt. |
| `PricegunReadClient`, `PricegunSnapshot` | CMD | `FetchAsync(itemId)` |
| `PurchaseApprovalService`, `PurchaseBudget`, `PurchaseApprovalPolicy` | M | → Safety |
| `MarketPriceEstimator`, `PricingEngine`, `PriceSnapshot`, `IPriceProvider` | CM | Preisschätzung; → Extras, Lib behält nur Pricegun-Rohdaten |

### 2.7 Eigener Shop

| Heute | Genutzt von | Methoden |
|---|---|---|
| `KoLMallReadClient.FetchOwnShopAsync`, `KolibriClient.GetOwnShopAsync` | CMD | |
| `KoLShopMutationClient`, `ShopPriceUpdate` | C (M über `KolibriClient.RepriceShopAsync`) | `ApplyPriceUpdatesAsync(updates, commit)` |
| `StoreLister` | C | `ListAsync`, `ReadItemsAsync`, `ReadOwnListingsAsync` |
| `KolibriClient.AddToShopAsync` | C | |
| `KoLShopSalesReadClient`, `ShopSalesSnapshot` | C (M über `GetShopSalesAsync(crm)`) | `FetchSalesAsync`; die Signatur hängt an `BuyerCrmStore`. |

### 2.8 Pirate-Keys

`PirateKeyClient` (C): `CountKeys`, `AssembleAsync`, `RedeemAsync`, `ResolveRare`. Dazu `KolibriClient.OpenPirateKeysAsync`
(C) und die Typen `PirateKeyCounts` und `PirateKeyRunResult`.

### 2.9 Daily

| Heute | Genutzt von | Anmerkung |
|---|---|---|
| `LiveDailySnapshotBuilder`, `DailyRoutineRunner`, `DailyRoutineSnapshot`, `DailyRoutineReport` | CMD | Plan und Snapshot |
| `AutoCombatDailyBridge`, `BridgeExecutionResult`, `BridgeAuditLog` | CMD | Ausführung über `AdventureExecutionGateway` |
| `DailyOrchestrator`, `RoleOverride` | C | rollenbasiert |
| `DailyDoneStore` | CMD | Einmal-pro-Tag-Marker |
| `CampfireLeavesPlanner`, `HermitPlanner`, `FreePullDispatcher`, `MemeShopPage`, `DailyInternetShopPlanner`, `BuyPlanner` | C / CM / CD | konkrete Routinen |
| `DailyAdventurePlanner`, `DailyAdventurePlanScheduler`, `DailyAdventurePlanExecutor` | CMD / M | |

### 2.10 Kmail

`KoLMailbox.SendAsync` (CD), `KoLAccountReadClient.FetchMailboxPageAsync`, `SaveInboxMessageAsync` und
`DeleteMailboxMessageAsync` (D), `KolibriClient.SendKMailAsync`/`SendKMailOutcomeAsync` (CM), dazu `KoLMailAttachment`,
`KoLMailSendOutcome`, `KoLMailEntry`, `KoLMailFolder` und `KoLMailboxSnapshot`. Ungenutzt sind `ParseInbox`,
`SendTextAsync` und `SendWithOutcomeAsync`, das dritte Sende-API.

### 2.11 Relay

`RelayHost.Start(session, …)` (CMD), `RelayHost.StartAsync(accounts, role, …)` und `SessionBindingCheck` (D),
`RelayHost.EnterUrl` (CD). `RelayServer` ist öffentlich, gebraucht wird er aber nur über `RelayHost`.

---

## 3. Leitlinien für den Entwurf

1. **Ein Paket `KoLibri`, ein Namespace `KoLibri`.** Wer `using KoLibri;` schreibt, hat alles. Core und LibKolibri
   werden zusammengelegt. Das Paket `KoLibri.Safety` ist optional und hängt nur an `KoLibri`.
2. **Zwei Wurzelobjekte:** `KolibriClient` (einer pro Prozess: Konten, Limiter, Daten, Events) und `KolibriSession`
   (eine angemeldete Sitzung je Konto). Die Fähigkeiten hängen als Facetten an der Sitzung:
   `session.Character`, `.Inventory`, `.Adventure`, `.Mall`, `.Shop`, `.Mail`, `.PirateKeys` und `.Daily`.
3. **Jeder Request läuft über die Sitzung.** Allowlist, Limiter-Lane, Reconnect, `IActionGuard` und Events gelten
   auch für rohe Seitenabrufe. `HttpClient` wird nicht mehr nach außen gegeben.
4. **Verändernde Aktionen liefern Ergebnisse, keine Tupel.** `ActionResult`, `PurchaseResult`, `FightResult` und
   `RunResult` enthalten einen Status (`Done`/`Partial`/`Rejected`/`Unclear`) und, wo gelesen, den Zustand vorher und
   nachher. "Unclear" bleibt ein eigener Wert, wie in 0.4.
5. **Strategien sind rein.** `ICombatStrategy` und `IChoiceResolver` sind synchron und schicken keine Requests. Sie
   bekommen einen Schnappschuss und geben eine Entscheidung zurück. Der Läufer führt aus.
6. **Policy ist ein Hook.** Der Kern kennt nur `IActionGuard`. Freigaben, Budgets, Ledger und Audit sind Guards im
   Paket `KoLibri.Safety`.
7. **Parser, Transports, Kernel und Fixtures sind `internal`.** Tests sehen sie über `InternalsVisibleTo`. Offline-Tests
   der Apps nutzen `KolibriOptions.HttpHandler`.
8. **Keine Aliase.** Jede Sache hat genau einen Namen.

---

## 4. API-Entwurf `KoLibri`

Die Signaturen sind hier gekürzt (ohne `CancellationToken cancellationToken = default` am Ende jeder Async-Methode).
Vollständig stehen sie in [KoLibri.Api.cs](lib-api-0.5/KoLibri.Api.cs) und
[PublicAPI.Unshipped.txt](../src/KoLibri/PublicAPI.Unshipped.txt).

### 4.1 Einstieg und Optionen

```csharp
public sealed class KolibriClient : IAsyncDisposable
{
    public static KolibriClient Create(KolibriOptions? options = null);
    public KolibriOptions Options { get; }
    public AccountRegistry Accounts { get; }
    public GameData Data { get; }
    public KolibriEvents Events { get; }                 // Events aller Sitzungen
    public Task<KolibriSession> LoginAsync(string userName, string password, LoginOptions? options = null);
    public Task<KolibriSession> OpenAsync(string accountIdOrName);   // gespeicherter Cookie, sonst Reconnect
}

public sealed class KolibriOptions
{
    Uri BaseUri; string? StateDirectory; string? DataDirectory; string? UserAgent; TimeSpan RequestTimeout;
    LimiterOptions Limiter; IAccountStore? AccountStore; HttpMessageHandler? HttpHandler; IActionGuard? ActionGuard;
}
public sealed class LimiterOptions { TimeSpan ActionInterval; TimeSpan ReadInterval; int ReadConcurrency; }
```

Umgebungsvariablen wie `KOLIBRI_MIN_REQUEST_INTERVAL_MS`, `KOLIBRI_READ_INTERVAL_MS` oder `KOLIBRI_LIMITER_PRESET` liest die Lib nicht mehr selbst. Die Apps übersetzen sie in
`LimiterOptions`. Damit entfallen `KoLRateLimiter.Shared`, `ApplyConfigured` und `IsEnvironmentOverride` als
öffentliche API. Der Desktop behält die Logik in `DesktopSettingsStore`.

### 4.2 Konten über Id/Name

```csharp
public readonly record struct AccountId(string Value);      // frei wählbar: "main", "mallbot", "alt-2"
public sealed record Account(AccountId Id, string PlayerName, int? PlayerId, string? Label, IReadOnlyList<string> Tags)
{ bool HasSession; DateTimeOffset? SessionExpiresAt; }

public sealed class AccountRegistry
{
    Task<IReadOnlyList<Account>> ListAsync();
    Task<Account?> FindAsync(string idOrName);          // erst Id, dann KoL-Name (ohne Groß/Klein)
    Task<Account> SetLabelAsync(AccountId id, string? label, IReadOnlyList<string>? tags = null);
    Task LogoutAsync(AccountId id);
    Task RemoveAsync(AccountId id);
}
public sealed class LoginOptions { string? AccountId; string? Label; bool RememberPassword; bool AllowIdentityChange; }
public interface IAccountStore { LoadAsync; SaveAsync(AccountRecord); DeleteAsync(AccountId); }
public sealed record AccountRecord(Account Account, string? CookieHeader, string? Password, DateTimeOffset? ExpiresAt);
```

- Die Lib kennt **keine Rollen** mehr. "Main" und "Mallbot" sind Konfiguration der Apps: CLI `--account <id|name>`,
  MCP `KOLIBRI_ACCOUNT`, Desktop-Auswahl. Wer weiter zwei feste Rollen will, legt Konten mit den Ids `main` und
  `mallbot` an.
- Die Identitätsprüfung (`AccountIdentityVerifier`) wird Teil von `LoginAsync`. Meldet KoL für eine bekannte Id eine
  andere Spieler-Id, schlägt der Login fehl, außer bei `AllowIdentityChange = true`.
- **Datenmigration:** Beim ersten Start liest `JsonAccountStore` die alte `accounts.json` mit Rollen. Er legt `Main`
  als `main` und `Mallbot` als `mallbot` an, mit `PlayerName` aus dem Profil. Cookie und Secret bleiben, wo sie sind.
- `Tags` ersetzen Sonderfälle wie "dieses Konto darf ein Shop sein". Die Lib wertet Tags nicht aus, Apps und
  Safety-Guards schon.

### 4.3 Sitzung und roher Seitenabruf

```csharp
public sealed class KolibriSession : IAsyncDisposable
{
    Account Account; KolibriEvents Events; bool IsExpired;
    CharacterActions Character; InventoryReader Inventory; Adventurer Adventure; Mall Mall; Shop Shop;
    Mailbox Mail; PirateKeys PirateKeys; DailyMarkers Daily;

    Task<Page> GetPageAsync(string path, IReadOnlyDictionary<string,string>? query = null, RequestLane lane = RequestLane.Auto);
    Task<Page> PostAsync(string path, IReadOnlyDictionary<string,string> form, RequestLane lane = RequestLane.Auto);
    Task<string> GetApiAsync(string what, IReadOnlyDictionary<string,string>? query = null);
    Task<string> GetPasswordHashAsync();
    Task ReconnectAsync();
    Task<RelayServer> StartRelayAsync(RelayOptions? options = null);
}
public enum RequestLane { Auto, Read, Action }
public enum PageKind { Normal, Fight, Choice, NotInFight, LoginRequired, Maintenance, TooFast, Error }
public sealed record Page(Uri Url, int StatusCode, string Html, PageKind Kind, DateTimeOffset ReceivedAt);
```

- `RequestLane.Auto` übernimmt die heutige Regel aus `KoLRateLimiter`: Bekannte Lesepfade (`api.php`, `charpane.php`,
  Inventar, Mall-Suche) laufen als `Read`, alles andere als `Action`. Eine `Action` läuft immer seriell.
- `PostAsync` ergänzt `pwd`, wenn es fehlt. Redirects folgen nur innerhalb der Allowlist.
- Ist die Sitzung abgelaufen und ein Passwort gespeichert, meldet sich die Sitzung einmal neu an und wiederholt nur
  `Read`-Requests. `Action`-Requests werden nie automatisch wiederholt; sie werfen `SessionExpiredException`.
- `UnexpectedPageException` steht für Wartung, Login-Seite und KoL-Fehlerseite. `ActionDeniedException` heißt, der
  Guard hat abgelehnt.

### 4.4 Status, Inventar, Spieldaten

```csharp
public readonly record struct ItemRef(int Id);   // implizit aus int
public readonly record struct SkillRef(int Id);
public readonly record struct Location(int Snarfblat);

public sealed record Status(string PlayerName, int Level, long Meat, int Adventures, DateTimeOffset FetchedAt)
{ int Hp, MaxHp, Mp, MaxMp; OrganState Fullness, Inebriety, Spleen; IReadOnlyList<ActiveEffect> Effects;
  int? FamiliarId; string? PathName; int? RoninTurnsRemaining; int? StoragePullsRemaining; }
public readonly record struct OrganState(int Current, int? Limit);
public sealed record ActiveEffect(int Id, string Name, int Turns);
public sealed record InventoryItem(ItemRef Item, string Name, int Quantity);
public sealed record Inventory(IReadOnlyList<InventoryItem> Items, DateTimeOffset FetchedAt) { int Count(ItemRef item); }
public sealed record Equipment(IReadOnlyDictionary<EquipSlot, ItemRef?> Slots, DateTimeOffset FetchedAt);

public sealed class InventoryReader
{ Task<Inventory> GetAsync(bool refresh = false); Task<Inventory> GetClosetAsync(); Task<Inventory> GetStorageAsync();
  Task<Equipment> GetEquipmentAsync(bool refresh = false); }

public sealed class GameData            // KoLmafia-Daten, eingebettet oder aus DataDirectory
{ ItemInfo? Item(ItemRef); ItemInfo? FindItem(string name); SkillInfo? Skill(SkillRef); SkillInfo? FindSkill(string name);
  LocationInfo? Location(Location); LocationInfo? FindLocation(string name); MonsterInfo? FindMonster(string name); }
```

`OrganState.Limit` ist `null`, wenn das Limit nicht sicher bekannt ist. Das entspricht den Organ-Limits aus 0.4
(`OrganLimitDeriver` wird intern). `ItemInfo.Use` ersetzt `ItemClassifier`/`ItemCategory` für die Fälle, die die
Apps brauchen (Food/Drink/Spleen/Usable/Equipment).

### 4.5 Konsum, Rest, Cast, Equip, Familiar

```csharp
public sealed class CharacterActions
{
    Task<Status> GetStatusAsync(bool refresh = false);
    Task<ActionResult> UseAsync(ItemRef item, int quantity = 1);
    Task<ActionResult> ConsumeAsync(ItemRef item, ConsumeKind kind, int quantity = 1);   // prüft Organ-Platz vorher
    Task<ActionResult> CastAsync(SkillRef skill, int times = 1);
    Task<ActionResult> RestAsync(int times = 1);
    Task<ActionResult> EquipAsync(ItemRef item, EquipSlot? slot = null);
    Task<ActionResult> UnequipAsync(EquipSlot slot);
    Task<ActionResult> WearOutfitAsync(int outfitId);
    Task<ActionResult> SetFamiliarAsync(int familiarId);
}
public enum ConsumeKind { Eat, Drink, Chew }
public enum ActionStatus { Done, Partial, Rejected, Unclear }
public sealed record ActionResult(ActionStatus Status, int Completed, string? Rejection, Page? Page)
{ IReadOnlyList<InventoryItem> ItemsGained, ItemsLost; Status? Before, After; }
```

Nicht aufgenommen (entschieden, §8 Punkt 5): Closet/Storage-Bewegungen, Combine, Friars und Moods
(`EnsureEffectAsync`). Der Desktop baut sie in 0.5 über `session.PostAsync` (Guard-Art `RawPost`). Eigene Facetten
wie `session.Storage` kommen erst mit "Danach – Richtung KoLmafia-Parität". Siehe Migration §7.

### 4.6 Abenteuer, Kampf, Choice

```csharp
public sealed class Adventurer
{
    Task<Encounter> AdventureAsync(Location location);         // ein Zug, Begegnung unaufgelöst
    Task<Encounter?> GetOpenEncounterAsync();                   // Wiederaufnahme nach Browser-Klicks
    Task<FightRound> FightAsync(CombatAction action);           // eine Runde
    Task<FightResult> FightToEndAsync(ICombatStrategy strategy);
    Task<Encounter> ChooseAsync(int choiceId, int option);
    Task<RunResult> RunAsync(RunRequest request);              // der eine Läufer
    Task<LocationAccess> GetLocationAccessAsync(Location location);
}

public abstract record Encounter(Page Page, Location? Location, string? Title);
public sealed record FightEncounter(…, string MonsterName, int? MonsterId, int Round) : Encounter;
public sealed record ChoiceEncounter(…, int ChoiceId, IReadOnlyList<ChoiceOption> Options) : Encounter;
public sealed record NonCombatEncounter(…, IReadOnlyList<InventoryItem> Items, long Meat) : Encounter;
public sealed record BlockedEncounter(…, BlockReason Reason) : Encounter;   // NoAdventures, LocationLocked, Beaten, UnknownPage
```

#### `ICombatStrategy`, analog zu `IChoiceResolver`

```csharp
public interface ICombatStrategy { CombatAction Next(CombatContext context); }
public interface IChoiceResolver  { ChoiceDecision Resolve(ChoiceContext context); }

public sealed record CombatContext(FightEncounter Fight, int Round, int Hp, int MaxHp, int Mp, int MaxMp,
                                   IReadOnlyList<FightRound> History) { IReadOnlyList<ActiveEffect> Effects; Inventory? Inventory; }
public sealed record ChoiceContext(Location? Location, int ChoiceId, string? Title, IReadOnlyList<ChoiceOption> Options);

public sealed record CombatAction(CombatActionKind Kind, int? Id = null, int? SecondId = null, string? Macro = null)
{ static Attack, RunAway, Stop; static Skill(SkillRef); static Item(ItemRef, ItemRef? second = null); static FromMacro(string); }
public sealed record ChoiceDecision(int? Option, ChoiceDecisionSource Source, string Reason) { static Ask(string reason); }

public static class CombatStrategies
{ AttackOnly; Macro(string macro); FromRules(string rules); FromDelegate(Func<CombatContext, CombatAction>); }
public static class ChoiceResolvers
{ AlwaysAsk; FromTable(IReadOnlyDictionary<int,int>); Safe(IReadOnlyDictionary<int,int>? = null); }
```

- Beide Schnittstellen haben dieselbe Form: synchron, rein, keine Requests, eine Entscheidung je Aufruf.
  Entscheidungen, die etwas kosten, trifft der Läufer nur auf ausdrückliche Antwort.
- `CombatAction.Stop` und `ChoiceDecision.Ask` sind das gemeinsame "gib an den Menschen zurück". Der Lauf endet dann
  mit `RunStopReason.NeedsCombat` bzw. `NeedsChoice`, und `RunResult.Open` enthält die offene Begegnung. Browser-Tab und
  Encounter-Ansicht machen dort weiter.
- `CombatStrategies.FromRules` übernimmt die Regel-DSL von 0.4 (`AutoCombatPolicy.Parse`). Damit laufen bestehende
  Policies unverändert. Intern wird `AutoCombatPolicy` zum Adapter.
- `CombatStrategies.Macro` schickt ein KoL-Combat-Makro (`fight.php?action=macro`). KoL wertet es serverseitig aus,
  das spart Requests pro Runde. **In 0.5 nur als Typ angelegt** (§8 Punkt 6): `CombatStrategies.Macro`,
  `CombatAction.FromMacro` und `CombatActionKind.Macro` tragen `[Experimental("KOLIBRI0001")]`
  (`KolibriDiagnostics.CombatMacros`). Wer sie nutzt, bekommt einen Compilerfehler, bis er die Id per `NoWarn`
  freischaltet. Läufer und `FightAsync` lehnen Makro-Aktionen in 0.5 mit `NotSupportedException` ab. Freigeschaltet
  wird erst nach einem festgehaltenen Live-Test; dann entfallen Attribut und Sperre.
- Turtle-Tamer-Begegnungen bleiben eine Choice. `ChoiceResolvers.Safe` erkennt sie am Titel wie heute
  `TurtleTamerEncounters` und fragt, solange keine Option konfiguriert ist.

#### Läufe

```csharp
public sealed record RunRequest(Location Location, int MaxTurns)
{ ICombatStrategy Combat = AttackOnly; IChoiceResolver Choices = AlwaysAsk;
  IReadOnlyList<StopCondition> StopWhen; int MaxRoundsPerFight = 30; }
public sealed record StopCondition(StopConditionKind Kind, int Value, int? Id = null)
{ static HpBelowPercent(int); MpBelowPercent(int); AdventuresAtMost(int); ItemCountAtLeast(ItemRef, int); EffectMissing(int); }
public enum RunStopReason { TurnsDone, Condition, NeedsChoice, NeedsCombat, Defeat, Blocked, Cancelled, Error }
public sealed record RunResult(RunStopReason StopReason, int TurnsSpent, IReadOnlyList<Encounter> Encounters, string? Detail)
{ Encounter? Open; IReadOnlyList<InventoryItem> Items; long MeatGained; }
```

`Adventurer.RunAsync` ersetzt alle Läufer aus §1, Punkt 1. Intern bleibt ein Kernel als Zustandsmaschine (heute
`AdventureRunKernel`), aber nur einer. Checkpoints (`IAdventureRunCheckpointStore`) und Allowlists
(`AdventureActionAllowlist`) entfallen als öffentliche API. Eine Strategie, die nur erlaubte Aktionen zurückgibt,
ersetzt die Allowlist. Ein Guard, der Aktionen ablehnt, ersetzt die Approval-Hooks.

### 4.7 Mall: Suche und Kauf

```csharp
public sealed class Mall
{
    Task<MallSearchResult> SearchAsync(string term, bool refresh = false);
    Task<MallOffers> GetOffersAsync(ItemRef item, bool refresh = false);
    Task<PurchaseQuote> QuoteAsync(PurchaseOrder order);         // plant gegen frische Angebote, kauft nicht
    Task<PurchaseResult> BuyAsync(PurchaseQuote quote);          // liest neu, nie über MaxUnitPrice, prüft danach
    Task<PurchaseResult> QuoteAndBuyAsync(PurchaseOrder order);
    Task<PriceHistory?> GetPriceHistoryAsync(ItemRef item);      // pricegun, gecacht und gedrosselt
}
public sealed record PurchaseOrder(ItemRef Item, int Quantity, long MaxUnitPrice) { long MeatReserve; IReadOnlyList<int> ExcludedStoreIds; }
public sealed record PurchaseQuote(PurchaseOrder Order, IReadOnlyList<MallListing> Plan, long TotalCost, DateTimeOffset QuotedAt)
{ int PlannedQuantity; string? Shortfall; }
public enum PurchaseStatus { Bought, Partial, NothingBought, PriceChanged, Refused, Unclear }
public sealed record PurchaseResult(PurchaseStatus Status, int QuantityBought, long MeatSpent, IReadOnlyList<PurchaseLine> Lines);
```

Der heutige Ablauf `Preview → Refresh → Commit → Verify` bleibt erhalten, ist aber auf zwei Schritte verdichtet:
`QuoteAsync` entspricht Preview, `BuyAsync` entspricht Refresh, Commit und Verify. Approval-Tokens sind nicht mehr
Teil der Mall-API. Wer sie will, setzt `ApprovalGuard` aus `KoLibri.Safety` für `GuardedActionKind.MallBuy`.

### 4.8 Eigener Shop, Pirate-Keys, Kmail, Daily-Marker

```csharp
public sealed class Shop
{ Task<IReadOnlyList<ShopListing>> GetListingsAsync(bool refresh = false);
  Task<ActionResult> AddAsync(ItemRef item, int quantity, long price, int limitPerDay = 0);
  Task<ActionResult> RepriceAsync(IReadOnlyList<ShopPriceChange> changes);
  Task<ActionResult> TakeBackAsync(ItemRef item, int quantity);
  Task<IReadOnlyList<ShopSale>> GetSalesAsync(bool refresh = false); }      // ohne CRM-Abhängigkeit

public sealed class PirateKeys
{ Task<PirateKeyCounts> GetCountsAsync(); Task<PirateKeyResult> AssembleAsync(int count);
  Task<IReadOnlyList<ChoiceOption>> GetRareOptionsAsync(); Task<PirateKeyResult> RedeemAsync(ItemRef rare, int count); }

public sealed class Mailbox
{ Task<MailPage> GetAsync(MailFolder folder = MailFolder.Inbox, int page = 1);
  Task<MailSendResult> SendAsync(MailMessage message);
  Task SaveAsync(string messageId); Task DeleteAsync(MailFolder folder, string messageId); }
public sealed record MailMessage(string Recipient, string Text) { IReadOnlyList<InventoryItem> Items; long Meat; }

public sealed class DailyMarkers       // Einmal-pro-KoL-Tag je Konto (Rollover 03:30 Arizona)
{ DateOnly GameDay; bool IsDone(string key); void MarkDone(string key); IReadOnlyList<string> DoneToday(); }
```

**Daily.** In der Lib bleiben nur die Bausteine: `DailyMarkers`, rohe Seiten, Choices, Läufe und Kauf. Die Routinen
selbst (Campfire, Hermit, Meme-Shop, Free-Pulls, Internet-Shop, Daily-Orchestrator) wandern in das Projekt
`src/KoLibri.Daily` (entschieden, §8 Punkt 4). Es hat `IsPackable=false`, wird also nicht auf NuGet veröffentlicht,
und nutzt nur die öffentliche API. CLI und Desktop referenzieren es beide. So wird Daily der erste echte Dogfooding-Test.

### 4.9 Events

```csharp
public sealed class KolibriEvents
{
    event EventHandler<RequestEventArgs>?       Request;        // Method, Path (redigiert), Lane, StatusCode, Duration, Waited, Kind
    event EventHandler<EncounterEventArgs>?     Encounter;      // jede erkannte Begegnung
    event EventHandler<FightEndedEventArgs>?    FightEnded;     // FightResult: Outcome, Runden, Items, Meat
    event EventHandler<ItemAcquiredEventArgs>?  ItemAcquired;   // InventoryItem + ItemSource (Fight, Choice, NonCombat, Mall, Mail, Use)
    event EventHandler<StatusChangedEventArgs>? StatusChanged;  // Before/After
    event EventHandler<HandlerFailedEventArgs>? HandlerFailed;
}
public abstract class KolibriEventArgs : EventArgs { AccountId Account; DateTimeOffset At; }
```

Regeln:

- Events kommen synchron auf dem aufrufenden Thread, nachdem die Antwort geparst ist. Sie gehen an
  `session.Events` und zusätzlich an `client.Events`.
- Handler dürfen keine Requests derselben Sitzung starten, weil die Action-Lane belegt ist. Wer reagieren will,
  sammelt im Handler und handelt danach. Ausnahmen in Handlern werden gefangen und als `HandlerFailed` gemeldet.
  Sie brechen nie eine Aktion ab.
- `RequestEventArgs.Path` ist redigiert: kein `pwd`, kein Passwort, keine Cookies. Das ersetzt die heutigen
  Request-Trails (`UnknownPageArchive`, Relay-Log) als gemeinsame Quelle.
- `ItemAcquired` entsteht aus geparsten Seiten (Kampf, Choice, Kauf), nicht aus Inventar-Diffs. So bleibt es ohne
  Zusatz-Request.

### 4.10 Guard-Hook

```csharp
public interface IActionGuard
{
    ValueTask<GuardDecision> CheckAsync(GuardedAction action, CancellationToken ct);
    ValueTask CompletedAsync(GuardedAction action, bool succeeded, CancellationToken ct);
}
public sealed record GuardedAction(AccountId Account, GuardedActionKind Kind, string Summary)
{ ItemRef? Item; int Quantity; long Meat; string? Path; }
public enum GuardedActionKind { Adventure, Fight, Choice, Use, Consume, Cast, Rest, Equip, MallBuy, ShopChange, SendMail, RawPost, Other }
public sealed record GuardDecision(bool Allowed, string? Reason) { static Allow; static Deny(string reason); }
```

Jede `Action`-Lane-Anfrage läuft durch `CheckAsync`, auch rohe `PostAsync`-Aufrufe (`RawPost`). Ohne Guard ist alles
erlaubt. Die Absicherung gegen Fehlkäufe bleibt im Kern, weil sie keine Policy ist: `MaxUnitPrice`, `MeatReserve`,
Neu-Lesen vor dem Kauf und die Organ-Prüfung vor dem Konsum.

---

## 5. Optionales Paket `KoLibri.Safety`

Freigaben, Budgets, Ledger und Audit, gebaut als Guards über dem Hook aus §4.10. Ein MCP-Server oder ein Bot nutzt
es; ein kleines Skript braucht es nicht.

```csharp
namespace KoLibri.Safety;
public sealed class CompositeGuard : IActionGuard { CompositeGuard(params IActionGuard[] guards); }   // erster Deny gewinnt

[Flags] public enum Rights { None, Read, Adventure, Consume, MallBuy, ShopChange, SendMail, RawPost, All }
public sealed record Grant(string Subject, AccountId Account, Rights Rights, DateTimeOffset? ExpiresAt) { long? MeatPerDay; int? TurnsPerDay; }
public interface IGrantStore { ListAsync; SaveAsync(Grant); RevokeAsync(subject, account); }
public sealed class JsonGrantStore : IGrantStore { JsonGrantStore(string path); }
public sealed class GrantGuard : IActionGuard { GrantGuard(string subject, IGrantStore grants, UsageLedger ledger); }

public sealed record Budget(AccountId Account, long MeatPerDay, int? TurnsPerDay = null, long? MaxUnitPrice = null);
public sealed class UsageLedger { UsageLedger(string path); long MeatSpentToday(AccountId); int TurnsSpentToday(AccountId); void Record(AccountId, long meat, int turns); }
public sealed class BudgetGuard : IActionGuard { BudgetGuard(IReadOnlyList<Budget> budgets, UsageLedger ledger); }

public sealed record ApprovalToken(string Value, GuardedAction Action, DateTimeOffset ExpiresAt);
public sealed class ApprovalGuard : IActionGuard
{ ApprovalGuard(IReadOnlySet<GuardedActionKind> requireApprovalFor, TimeSpan tokenLifetime);
  ApprovalToken Issue(GuardedAction action); void Present(string token); }

public sealed record AuditEntry(long Sequence, DateTimeOffset At, AccountId Account, string Kind, string Summary, string Hash, string PreviousHash);
public sealed class AuditJournal : IActionGuard, IDisposable
{ AuditJournal(string path); void Attach(KolibriEvents events); IReadOnlyList<AuditEntry> Read(DateTimeOffset? since = null); long? Verify(); }
```

Zuordnung von heute nach Safety:

| Heute | Safety |
|---|---|
| `LibKolibri.Access.AccessGate`, `Grant`, `GrantStore`, `Rights`, `RightLimit`, `OperationCatalog`, `OperationKind`, `AccessRequest`, `AccessDecision`, `PlanTicket` | `GrantGuard`, `Grant`, `IGrantStore`/`JsonGrantStore`, `Rights` |
| `LibKolibri.Access.UsageLedger`, `UsageReservation`, `ArbitrageSpendLedger`, `FileSpendLedger`, `SharedMallSpendLedger` | `UsageLedger`, `BudgetGuard`, `Budget` |
| `ApprovalTokenStore`, `ApprovalToken`, `PurchaseApprovalService`, `PurchaseApprovalPolicy`, `PurchaseBudget`, `TokenAdventureApprovalHook`, `IAdventureApprovalHook`, `DelegateAdventureApprovalHook`, `ManualCombatApprovalDecision` | `ApprovalGuard`, `ApprovalToken` |
| `AuditEvent`, `AuditEventKind`, `SealedAuditEvent`, `AuditHashChain`, `IAuditEventJournal`, `AuditEventJournal`, `BridgeAuditLog`, `AuditedAdventureHooks` | `AuditJournal`, `AuditEntry` |
| `OperationLedger*` (29 Typen), `DurableSnapshotStore` (16), `ShopRepriceApproval*` (19), `TransferApproval*` (28) | entfällt; nicht von Apps genutzt (nur Tests) |

Das MCP baut sich so einen `CompositeGuard(new GrantGuard(…), new BudgetGuard(…), new ApprovalGuard(…), audit)` und
gibt ihn in `KolibriOptions.ActionGuard`.

---

## 6. Was `internal` wird und was gelöscht wird

### 6.1 Löschen

**M0M1-/Gateway-Schicht (LibKolibri, ~4.000 Zeilen):**
`M0M1Contracts.cs` (51 Typen), `M0M1GatewayContracts.cs`, `CoreM0M1GatewayAdapters.cs`, `CoreM0M1Registration.cs`,
`CoreGatewayAdapters.cs`, `CoreGatewayRegistration.cs`, `LibKolibriContracts.cs` (`IAccountGateway`, `IMallGateway`,
`IAdventureGateway`, `ITransferGateway`), `LibKolibriRuntime.cs`, `RoleScopedRuntime.cs`,
`MainMallbotTransferContracts.cs`, `MainMallbotTransferGatewayAdapter.cs`, `PreviewOnlyTransferGateway.cs`.
`LibKolibri.McpRuntimeBootstrap` geht in der MCP-eigenen Klasse `KolLibri.Mcp.McpRuntimeBootstrap` auf.

**Gegenstücke im Core:** `Markt/Mall/CoreArbitrageQueryService.cs`, `CoreShopQueryService.cs`,
`ReadQueryContracts.cs`, `ReadQueryCoordinator.cs` und `MainMallbotTransferGateway.cs` (1.096 Zeilen).

**Zweite Request-Welt:** `Basis/Requests/KoLRequestOrchestrator.cs`, `KoLRequestCoordination.cs`,
`KoLRequestContracts.cs` und `HttpClientKoLRequestTransport.cs`. Brauchbares aus `KoLResponseClassifier` geht im
neuen `PageKind`-Klassifizierer auf.

**Tote Dateien** (kein Verweis außerhalb der eigenen Datei, auch nicht aus Tests):
`Basis/Audit/AuditEventJournalExtensions.cs`, `Markt/Pricing/RotatingSales.cs`,
`Spiel/Adventure/AdventureCheckpointExtensions.cs`, `Spiel/Adventure/AdventureDailyContracts.cs`,
`Spiel/Client/ItemMetadata.cs`, `Spiel/Daily/AutoCombatBatchRunner.cs`, `Spiel/Daily/AutoCombatPolicyBuilder.cs`,
`Spiel/Daily/FleaMarketReader.cs`, `Spiel/Daily/QuestRunner.cs`. Die übrigen 330 toten Typen stehen in
[inventory.md](lib-api-0.5/inventory.md) mit der Markierung "(tot)".

**Nur von Tests genutzt, keine App:** `Spiel/Client/InventoryStateContracts.cs` (1.162 Zeilen),
`Spiel/Adventure/TurnCounterContracts.cs` (593), `Spiel/Adventure/BoundedManualCombatRunner.cs`,
`Spiel/Adventure/AccountRecoverySnapshotProvider.cs`, `Basis/Accounts/SessionHealth*`,
`Basis/Audit/OperationLedger*`, `Basis/Audit/DurableSnapshotStore.cs` und `Markt/Mall/ShopRepriceApproval*`.
Sie werden mit ihren Tests gelöscht. Turn-Counter und Session-Health kommen in "Danach – Richtung KoLmafia-Parität"
neu auf der öffentlichen API.

**Aliase:** alle unter §1, Punkt 6.

### 6.2 Aus der Lib in Tools oder Extras verschieben

Diese Typen nutzen die Apps, sie gehören aber nicht zum Versprechen der Lib. Sie wandern in ein experimentelles
Paket `KoLibri.Extras` (nicht auf NuGet) oder direkt in die App, die sie braucht:

- **Arbitrage und Markt:** `Markt/Arbitrage/*`, `BaconArbitrageExecutor`, `CloverRestockExecutor`, `MarketScanner`,
  `UnderpricedPurchase*`, `MarketPriceEstimator`, `PricingEngine`, `Reprice*`, `FarmRoi*`, `FarmVsBuyPlanner`,
  `Coinmaster*`, `IotM*`, `MrStoreCatalog`, `ForeignBribe*`, `Spiel/Daily/ArbitragePlanner`, `ArbitrageRule`,
  `RestockPlanner`, `BuyPlanner`
- **CRM und Analytics:** `BuyerCrm*`, `ShopBuyerAnalytics`, `Markt/Analytics/*`, `SellerStoreBlacklist`,
  `JsonFileSellerBlacklistStore`, `OwnStoreExclusionPolicy`
- **Clan und Chat** (nur Desktop): `KoLClanLounge`, `KoLClanProfile`, `KoLClanStash`, `KoLChat`, dazu die
  Clan-/Chat-Methoden von `KoLAccountReadClient`. Ziel ist `KoLibri.Desktop`; die Methoden werden dort auf
  `session.GetPageAsync`/`PostAsync` umgebaut.
- **Workflow und Skripte:** `KoLWorkflowPlanner`, `KoLWorkflowSnapshot`, `MafiaReadScript`
- **Daily-Routinen:** alles unter `Spiel/Daily/` außer den Markern, dazu `Spiel/Adventure/DailyAdventure*`.
  Ziel ist `src/KoLibri.Daily` mit `IsPackable=false` (§4.8).

### 6.3 `internal` (bleibt im Paket `KoLibri`, Tests per `InternalsVisibleTo`)

- **Login und Konten:** `KoLLoginClient`, `AccountSessionManager`, `AccountSession`, `AccountCredentials`,
  `AccountProfile`, `JsonFileAccountStore`, `FileSecretStore`, `KeyProtection`, `KoLPwdHashResolver`,
  `AccountIdentityVerifier`, `KoLSecurityPolicy`, `KoLAllowlistHandler`, `KoLUserAgent`
- **Requests:** `KoLRateLimiter`, `KoLRateLimitedHandler`, `KoLRequestLane`, `KoLHtmlResponseKind` (→ `PageKind`),
  `KoLRoutes`
- **Lesen:** `KoLAccountReadClient`, `IAccountReadClient`, `KoLMallReadClient`, `IMallReadClient`,
  `KoLShopSalesReadClient`, `OrganLimitDeriver`, `ItemMaster`, `ItemClassifier`, `MafiaDataLoader`, `KoLmafiaCatalog`,
  `Mafia*Record`, `BundledItemNames`
- **Aktionen:** `CharacterActionClient` und die `CharacterAction`-Hierarchie, `KoLMailbox`, `PirateKeyClient`,
  `StoreLister`, `KoLShopMutationClient`, `MallBuyClient`, `MallPurchaseGateway`, `HttpMallPurchaseTransport`,
  `MallPurchase*`-Verträge, `PricegunReadClient`
- **Abenteuer:** `AdventureRunKernel` (der eine Kernel), `AdventureExecutionGateway`, `HttpAdventureExecutionTransport`,
  `LiveManualCombatTransport`, `ManualCombatGateway`, `OpenChoiceClient`, `KoLAdventureEncounterParser`,
  `KoLCombatObservationParser`, `ChoiceResolver`, `ChoiceResolutionConfigStore`, `TurtleTamerEncounters`,
  `AutoCombatPolicy` (hinter `CombatStrategies.FromRules`), `RecoveryPlanner`, `AdventureLocationCatalog`,
  `KoLQuestAccess`, `UnknownPageArchive`, alle `*FixtureTransport`
- **Relay:** `RelayHost`, `RelayServer` (öffentlich nur `KolibriSession.StartRelayAsync` und `RelayServer` als Handle)

---

## 7. Migrationsliste (Apps)

Die Apps werden in diesem Schritt nicht umgebaut. Die Liste ist der Fahrplan für Schritt 3 ("Dogfooding").

| Heute | 0.5 | Betroffen |
|---|---|---|
| `AccountRole.Main` / `AccountRole.Mallbot` | `AccountId("main")` / `AccountId("mallbot")` aus App-Konfiguration; `client.OpenAsync("main")` | CMD (91 Stellen) |
| `AccountSessionManager.CreateDefault()` + `InitializeAsync()` | `KolibriClient.Create(options)` | CMD |
| `AccountSessionManager.GetSessionAsync(role)` / `GetRequiredSessionAsync(role)` | `client.OpenAsync(idOrName)` | CMD |
| `AccountSessionManager.GetAccountsAsync` / `GetProfileAsync` / `SetProfileAsync` | `client.Accounts.ListAsync` / `FindAsync` / `SetLabelAsync` | CMD |
| `AccountSessionManager.ReconnectAsync(expired, login)` | `session.ReconnectAsync()` (oder automatisch für Reads) | CD |
| `SetCredentialsAsync` / `ApplyVerifiedLoginAsync` / `AccountIdentityVerifier.VerifyRoleAsync` | `client.LoginAsync(user, pwd, new LoginOptions { AccountId = …, AllowIdentityChange = … })` | C |
| `ClearCredentialsAsync(role)` | `client.Accounts.LogoutAsync(id)` | C |
| `KoLLoginClient.CreateAuthenticatedClient(...)` + eigene `HttpClient`-Aufrufe | `session.GetPageAsync` / `PostAsync` / `GetApiAsync` | CMD (12 Stellen) |
| `KoLPwdHashResolver.RequireAsync` / `Forget` | `session.GetPasswordHashAsync()` (Cache intern) | MD |
| `KoLRateLimiter.Shared.ApplyOptions(KoLLimiterOptions)` | `KolibriOptions.Limiter` beim Erzeugen | D |
| `KolibriClient.LoginAsync(role, …)` / `OpenAsync(role)` (LibKolibri) | `KolibriClient.Create().LoginAsync(...)` / `OpenAsync(id)` | CM |
| `KoLAccountReadClient.FetchStatusAsync` / `KolibriClient.GetStatusAsync` | `session.Character.GetStatusAsync()` | CMD |
| `KoLStatusSnapshot` | `Status` (`Fullness` usw. als `OrganState`) | CMD |
| `FetchInventoryAsync` / `GetInventoryAsync` / `FetchStorageAsync` | `session.Inventory.GetAsync()` / `GetStorageAsync()` | CMD |
| `KoLInventorySnapshot`, `KoLInventoryItem(string ItemId, …)` | `Inventory`, `InventoryItem(ItemRef, …)` | CMD |
| `CharacterActionClient.ExecuteAsync(new UseInventoryItem(…))`, `KolibriClient.UseItemAsync` | `session.Character.UseAsync(item, qty)` | CMD |
| `ExecuteAsync(new ConsumeCharacterItem(id, kind, n))`, `KolibriClient.ConsumeAsync` | `session.Character.ConsumeAsync(item, ConsumeKind.Eat, n)` | CMD |
| `CastAsync`, `CastSkillAsync` (Tupel) | `session.Character.CastAsync(skill, n)` → `ActionResult` | CD |
| `RestAsync` → `CharacterRestOutcome` | `session.Character.RestAsync(n)` → `ActionResult` | CMD |
| `EquipInventoryItem` / `UnequipInventoryItem` / `WearCharacterOutfit` / `SelectCharacterFamiliar` | `EquipAsync` / `UnequipAsync` / `WearOutfitAsync` / `SetFamiliarAsync` | D |
| `MoveClosetItem`, `PullStorageItem`, `CombineCharacterItems`, Friars, `EnsureEffectAsync` | vorerst `session.PostAsync` im Desktop; später Parität | D |
| `KoLAdventureSessionFactory.CreateAsync(role, approval, verification, …)` | `session.Adventure` | CMD |
| `AdventureExecutionGateway.ExecuteAsync(request, allowlist)` | `session.Adventure.AdventureAsync(location)` oder `RunAsync(new RunRequest(location, 1))` | CMD |
| `StartAsync` / `ResumeAsync` / `StepAsync` / `ExecutePendingAsync` / `RefreshEncounterAsync` | `GetOpenEncounterAsync` + `FightAsync` / `ChooseAsync` | D |
| `BoundedAdventureRunner.RunAsync(request, allowlist, stopWhen)` | `session.Adventure.RunAsync(new RunRequest(loc, n) { StopWhen = … })` | CMD |
| `BoundedChoiceChainRunner.RunAsync` | `RunAsync` mit `Choices = ChoiceResolvers.FromTable(…)` | CMD |
| `ManualAdventureSession.RunTurnsAsync`, Desktop-Batch, `DesktopAdventureRunBridge`, `AdventureJob` | `session.Adventure.RunAsync` | D |
| `ManualCombatGateway.PreviewAsync` + `ExecuteAsync`/`CommitAsync`, `LiveManualCombatTransport.SubmitAsync` | `session.Adventure.FightAsync(CombatAction.…)` | CMD |
| `AutoCombatRunner.RunAsync(policy, combatId, …)` | `session.Adventure.FightToEndAsync(CombatStrategies.FromRules(text))` | CMD |
| `AutoCombatPolicy.Parse(text)` + `Evaluate(ctx)` | `CombatStrategies.FromRules(text)` (`ICombatStrategy`) | CMD |
| `AutoCombatContext`, `AutoCombatDecision` | `CombatContext`, `CombatAction` | CMD |
| `IChoiceResolver.Resolve(ChoiceResolutionRequest)` → `ChoiceResolution` | `IChoiceResolver.Resolve(ChoiceContext)` → `ChoiceDecision` | D |
| `ChoiceResolver` + `ChoiceResolutionConfigStore` | `ChoiceResolvers.Safe(table)`; die Tabelle lädt die App | CMD |
| `OpenChoiceClient.DetectAsync` / `SubmitAsync`, `KoLAdventureSession.DetectOpenChoiceAsync` | `GetOpenEncounterAsync()` / `ChooseAsync(choiceId, option)` | CMD |
| `AdventureActionAllowlist` | entfällt: Strategie/Resolver bestimmen die Aktionen, `IActionGuard` lehnt ab | CMD |
| `IAdventureApprovalHook`, `IAdventureVerificationHook`, `TokenAdventureApprovalHook`, `KnownEncounterVerificationHook` | `IActionGuard` (Safety: `ApprovalGuard`) | CMD |
| `AdventureCondition` / `AdventureConditionEvaluator` | `StopCondition` | CMD |
| `AdventureStepSpec`-Familie, `AdventureManualDriver` | `CombatAction` / `ChooseAsync` | CMD |
| `CombatObservation`, `CombatOutcome` | `FightRound`, `FightOutcome` | CMD |
| `AdventureLocationIds` / `AdventureLocationCatalog` | `Location` + `client.Data.FindLocation(name)` | CMD |
| `KoLQuestAccess` / `ReadLocationAccessAsync` | `session.Adventure.GetLocationAccessAsync(loc)` | D |
| `KoLMallReadClient.SearchAsync(itemId)` / `SearchItemsAsync(term)` / `KolibriClient.SearchMallAsync` | `session.Mall.GetOffersAsync(item)` / `SearchAsync(term)` | CMD |
| `MallPurchaseGateway.Preview` + `Refresh` + `Commit`, `PurchaseNowAsync`, `MallBuyClient.BuyAsync`, `KolibriClient.BuyCheapAsync` | `session.Mall.QuoteAsync(order)` + `BuyAsync(quote)` / `QuoteAndBuyAsync(order)` | CMD |
| `MallPurchaseRequest`, `MallPurchasePolicy`, `MallPurchasePreview`, `MallPurchaseCommitResult` | `PurchaseOrder`, `PurchaseQuote`, `PurchaseResult` | CMD |
| `PricegunReadClient.FetchAsync` | `session.Mall.GetPriceHistoryAsync(item)` | CD |
| `FetchOwnShopAsync` / `GetOwnShopAsync` | `session.Shop.GetListingsAsync()` | CMD |
| `StoreLister.ListAsync` / `KolibriClient.AddToShopAsync` | `session.Shop.AddAsync(...)` | C |
| `KoLShopMutationClient.ApplyPriceUpdatesAsync` / `RepriceShopAsync` | `session.Shop.RepriceAsync(changes)` (Vorschau = Guard) | CM |
| `KoLShopSalesReadClient.FetchSalesAsync` / `GetShopSalesAsync(crm)` | `session.Shop.GetSalesAsync()`; CRM bleibt in Extras | CM |
| `PirateKeyClient.*`, `KolibriClient.OpenPirateKeysAsync` | `session.PirateKeys.*` | C |
| `KoLMailbox.SendAsync(http, pwd, …)`, `SendKMailAsync`, `SendKMailOutcomeAsync` | `session.Mail.SendAsync(new MailMessage(...))` | CMD |
| `FetchMailboxPageAsync` / `SaveInboxMessageAsync` / `DeleteMailboxMessageAsync` | `session.Mail.GetAsync` / `SaveAsync` / `DeleteAsync` | D |
| `RelayHost.Start(session, …)` / `StartAsync(accounts, role, …)`, `EnterUrl` | `session.StartRelayAsync(options)`, `RelayServer.EntryUri(path)` | CMD |
| `DailyDoneStore` | `session.Daily` (`DailyMarkers`) | CMD |
| `LiveDailySnapshotBuilder`, `DailyRoutineRunner`, `AutoCombatDailyBridge`, `DailyOrchestrator`, Planner | `src/KoLibri.Daily` (`IsPackable=false`, auf öffentlicher API) | CMD |
| `MafiaDataLoader.Load(dir)` / `KoLmafiaCatalog` / `ItemClassifier` | `client.Data` (`GameData`), `KolibriOptions.DataDirectory` | CMD |
| `AccessGate`, `GrantStore`, `UsageLedger`, `ApprovalTokenStore`, `PurchaseApprovalService`, `AuditEventJournal` | `KoLibri.Safety` (§5) | CMD |
| `RoleScopedRuntime`, `McpRuntimeBootstrap` (Lib), `CoreAccountGateway`, `CoreMallGateway`, `IAccountGateway`, `IMallGateway` | entfällt; MCP nutzt `KolibriClient`/`KolibriSession` direkt | M |
| M0M1-Verträge in `ArbitrageViewModel`, `ApprovalViewModel`, `ShopViewModel`, `OperationsViewModel` | entfällt mit den Vorschau-Panels oder wandert als App-eigene Modelle in den Desktop | D |

---

## 8. Entscheidungen

Die offenen Fragen des ersten Entwurfs hat der Nutzer am 2026-11-26 entschieden. Die Abschnitte oben, der Stub und
die PublicAPI-Dateien sind daran angepasst.

| # | Frage | Entscheidung | Wirkung im Entwurf |
|---|---|---|---|
| 1 | Ein Namespace oder mehrere? | **Ein Namespace `KoLibri`** (dazu `KoLibri.Safety` im optionalen Paket). | Alle 108 Typen in `KoLibri`; keine Konflikte mit Facetten-Properties wie `session.Adventure`. |
| 2 | Synchrone oder asynchrone Strategien? | **`ICombatStrategy` und `IChoiceResolver` bleiben synchron.** Ein Mensch übernimmt über `CombatAction.Stop` / `ChoiceDecision.Ask` und `RunResult.Open`. | Kein `ValueTask` in den Strategien (§4.6). |
| 3 | Events oder Streams? | **Klassische .NET-Events.** Streams (`Channel`, `IObservable`) baut die App selbst. | `KolibriEvents` wie in §4.9; keine Stream-API in der Lib. |
| 4 | Wo liegt `KoLibri.Daily`? | **`src/KoLibri.Daily`, `IsPackable=false`**, geteilt von CLI und Desktop. | §4.8, §6.2, §7. |
| 5 | Closet, Storage, Moods, Friars? | **In 0.5 über `PostAsync`.** Eigene Facetten kommen später. | Nicht in `CharacterActions` (§4.5); Desktop-Migration über `session.PostAsync` (§7). |
| 6 | Combat-Makros? | **In 0.5 nur als Typ angelegt**, erst nach Live-Test freigeschaltet. | `[Experimental("KOLIBRI0001")]` auf `CombatStrategies.Macro`, `CombatAction.FromMacro` und `CombatActionKind.Macro`; neue Klasse `KolibriDiagnostics`. Der Läufer lehnt Makros mit `NotSupportedException` ab (§4.6). |

---

## 9. PublicAPI-Vorschlag und Prüfung

- `PublicAPI.Shipped.txt` ist für beide Pakete leer (nur `#nullable enable`). Bisher ist nichts auf NuGet
  veröffentlicht; 0.4.0 versprach ausdrücklich "public surface … will change in 0.5". Mit dem 0.5-Release wandert
  `Unshipped` nach `Shipped`.
- `PublicAPI.Unshipped.txt` wurde aus [KoLibri.Api.cs](lib-api-0.5/KoLibri.Api.cs) mit
  `Microsoft.CodeAnalysis.PublicApiAnalyzers` 3.3.4 erzeugt (`dotnet format analyzers --diagnostics RS0016`). Das Paket
  `KoLibri` hat 931 Zeilen und 108 Typen, `KoLibri.Safety` 99 Zeilen und 13 Typen.
- Prüfen und neu erzeugen:

  ```bash
  docs/lib-api-0.5/check-api.sh
  ```

  ```bash
  docs/lib-api-0.5/check-api.sh --regenerate
  ```

  Das Skript legt zwei Wegwerfprojekte im Temp-Ordner an (net8.0, Nullable enable). Es baut mit RS0016, RS0017 und
  RS0026 als Fehler. Zuletzt lief es mit 0 Fehlern und 0 Warnungen. Der Analyzer 3.3.4 hält `[Experimental]` nicht
  in den PublicAPI-Dateien fest. Die Markierung steht deshalb nur im Stub und in §8; eine neuere Analyzer-Version
  (mit `[RSEXPERIMENTAL]`-Präfix) kann das beim Umbau nachholen.
- Seit Schritt 3 sitzt der Analyzer in `src/KoLibri/KoLibri.csproj` und `src/KoLibri.Safety/KoLibri.Safety.csproj`
  (RS0016/RS0017/RS0026 als Fehler); die PublicAPI-Dateien liegen dort. `check-api.sh` prüft zuerst den Stub und
  baut dann die echten Projekte, beide gegen dieselben Dateien. Stub und Implementierung können also nicht
  auseinanderlaufen. ApiCompat gegen das zuletzt veröffentlichte Paket kommt mit dem ersten NuGet-Release.

## 10. Nächste Schritte

1. ~~Entwurf abstimmen und die offenen Fragen entscheiden.~~ Erledigt, siehe §8.
2. ~~`src/KoLibri` anlegen, der Analyzer läuft gegen `PublicAPI.Unshipped.txt`.~~ Erledigt als Übergang (§11.1):
   Core bleibt eigene Assembly hinter der Fassade. Zusammenlegen, M0M1 löschen (§6.1) und Extras ausziehen (§6.2)
   folgen, wenn MCP und Desktop umgezogen sind.
3. ~~Neue Fassade über den internen Clients implementieren, Offline-Tests über `KolibriOptions.HttpHandler`.~~
   Erledigt (§11).
4. Apps nach §7 umziehen: zuerst das MCP (kleinste Fläche), dann die CLI, zuletzt der Desktop.
5. Das Cookbook als `ConsumerSmoke` gegen die gepackten Pakete bauen.

---

## 11. Umsetzungsstand Schritt 3 (Teil 1: Paketstruktur und API)

Stand 2026-11-26, Zweig `release/0.5.0`. Geliefert in zwei Teil-PRs: **dieser Teil** bringt die Pakete und die
implementierte API; **Teil 2** zieht die CLI um (§11.4).

### 11.1 Struktur

| Projekt | Inhalt | Verweise |
|---|---|---|
| `src/KoLibri` | Paket `KoLibri` 0.5.0-dev, Namespace `KoLibri`, genau die 108 Typen aus dem Stub | `KoLibri.Core` mit `PrivateAssets="all"` |
| `src/KoLibri.Safety` | Paket `KoLibri.Safety` 0.5.0-dev: `CompositeGuard`, `GrantGuard`/`JsonGrantStore`, `BudgetGuard`/`UsageLedger`, `ApprovalGuard`, `AuditJournal` | nur `KoLibri` |
| `src/KoLibri.Daily` | `IsPackable=false`: `IDailyTask`, `DelegateDailyTask`, `DailyRunner` (Vorschau, einmal pro KoL-Tag, Stopp nach Unclear) | nur `KoLibri` |
| `tests/KoLibri.Tests` | 28 Offline-Tests mit Fake-Handler, nur gegen die öffentliche API | `KoLibri`, `KoLibri.Safety`, `KoLibri.Daily` |

**Übergang Core.** Die Spiellogik bleibt in `KoLibri.Core`. `KoLibri` verweist mit `PrivateAssets="all"` darauf:
Wer `KoLibri` referenziert, kompiliert nur gegen die Entwurfs-API (ein Zugriff auf `KoLibri.Core.*` ist ein
Compilerfehler, siehe Test `Public_surface_lives_in_the_KoLibri_namespaces_only`), `KoLibri.Core.dll` liegt zur
Laufzeit trotzdem daneben. MCP, Desktop und die alte CLI greifen weiter direkt auf Core zu und bauen unverändert.
Core bekommt `InternalsVisibleTo("KoLibri")` und zwei kleine interne Ergänzungen: einen `KoLLoginClient`-Konstruktor
mit eigener Handler-Kette (Login über Limiter und Offline-Handler) und `DailyDoneStore.DoneToday`. Für das
NuGet-Paket muss `KoLibri.Core.dll` noch ins `lib/`-Verzeichnis gepackt werden; das kommt mit dem Pack-Schritt.

**PublicAPI.** `Shipped` ist leer, `Unshipped` ist der Entwurf; der Analyzer meldet keine Abweichung. Keine
Signatur aus dem Stub wurde geändert, kein öffentliches Mitglied kam dazu.

### 11.2 Bewusste Abweichungen im Verhalten

Die Signaturen sind unverändert; abweichend vom Text in §4 ist:

| # | Stelle | Verhalten in 0.5.0-dev | Grund |
|---|---|---|---|
| 1 | Standard-`IAccountStore` | liest und schreibt die Dateien von 0.4 (`accounts.json` + verschlüsselte Secrets), Ids nur `main` und `mallbot`; andere Ids → `KolibriException`. `RemoveAsync` löscht die Zugangsdaten, das Profil bleibt. Labels/Tags in einer Nebendatei | gespeicherte Sitzungen von CLI/MCP/Desktop bleiben gültig; freie Ids mit eigenem `AccountStore` oder nach der Datenmigration |
| 2 | `LimiterOptions` | Standard 2 s (Action) / 1 s (Read) / 2 parallel wie 0.4, Untergrenze 1 s bzw. 200 ms | gleiche Drosselung wie heute live bewiesen |
| 3 | `GameData` ohne `DataDirectory` | mitgelieferter Ordner `external/kolmafia-data` neben der Anwendung, keine eingebettete Ressource | Paketgröße; eingebettet beim Pack-Schritt |
| 4 | `DailyMarkers` | Rollover 03:30 US Eastern wie 0.4 (nicht Arizona); gleiche Datei `daily-done.json` (ohne `StateDirectory` `LocalApplicationData/KoLibri`); für die Konten `main`/`mallbot` der 0.4-Schlüssel `Main:<Spieler-Id>`, die Spieler-Id aus dem zuletzt gelesenen Status (wie 0.4), sonst aus dem Profil, sonst der Name | Marker der 0.4-CLI (`daily run`) gelten weiter |
| 5 | `Shop.TakeBackAsync` | `NotSupportedException` | Core hat keinen geprüften Request; vorerst `session.PostAsync` |
| 6 | `Shop.RepriceAsync` | ändert nur Preise; ein abweichendes `LimitPerDay` → `Rejected`, nichts gesendet | Core kann das Tageslimit nicht setzen |
| 7 | `Mailbox` | `MailFolder.Pvp` → `NotSupportedException`; `MailEntry.Items` bleibt leer | Core liest diesen Ordner und die Anhänge nicht |
| 8 | `PirateKeyCounts.Parts` | Anzahl kompletter Teile-Sätze (je ein Alpha/Bravo/Charlie/Delta) | das ist die Zahl, die `AssembleAsync` braucht |
| 9 | `ActionResult.Page` | `null` bei Use/Consume/Cast/Rest/Equip | `CharacterActionClient` gibt die Seite nicht heraus; Status vorher/nachher ist gesetzt |
| 10 | `Adventurer` | eigener kleiner Ablauf über rohe Seiten (`adventure.php`, `fight.php`, `choice.php`) mit den Core-Parsern statt `AdventureRunKernel`; Interstitial nach dem ersten Zug wird nur wiederholt, wenn der Adventure-Zähler gleich blieb; ein Zug ohne Zählerwechsel stoppt den Lauf (wie die 0.4-CLI) | der Kernel hängt an Checkpoints, Allowlists und Rollen; die Schleife der CLI bleibt bis Teil 2 unverändert |
| 11 | `GetPriceHistoryAsync` | eigener `HttpClient` für pricegun.loathers.net, ohne KoL-Cookie, nicht über die KoL-Allowlist | anderer Host |
| 12 | Lesefehler | liefert Core nur eine alte Kopie (`IsStale`), wirft die Fassade `KolibriException` | Läufe sollen nie mit geratenem Zustand weitermachen |
| 13 | `CombatStrategies.FromRules` | Regelnutzung ("x3") wird bei jedem Aufruf aus `CombatContext.History` nachgerechnet | Strategien bleiben rein |
| 14 | Guard | GET auf der Action-Lane über `GetPageAsync` → `GuardedActionKind.Other`; Kampf-/Choice-/Kaufschritte der Facetten mit ihrer eigenen Art | Safety kann rohe Aktionen trotzdem sehen |

### 11.3 Prüfung

- `docs/lib-api-0.5/check-api.sh`: Stub und echte Projekte, 0 Fehler.
- `tests/KoLibri.Tests`: Login mit Identitätsprüfung, Kontensuche, Status/Inventar/Equipment, redigierte
  Request-Events, Handler-Fehler, Guard (Deny sendet nichts, RawPost mit `pwd`), Login-Seite auf einer Action wird
  nie wiederholt, Wartungsseite, Spieldaten, Daily-Marker, 0.4-Kontendateien, Abenteuer → Kampf → Sieg mit Beute,
  Lauf mit Choice-Tabelle, Stopp an unbekannter Choice, Stopp-Bedingung, Regel-Strategie, Makro-Sperre,
  Choice-Vorprüfung, Organ-Prüfung vor dem Konsum, Kaufplanung, alle Safety-Guards, `DailyRunner`, README-Beispiel.
- Die bestehende Suite (`tests/KoLibri.Core.Tests`) bleibt grün. Keine Live-Requests.

### 11.4 Teil 2: CLI-Umzug (Plan, umgesetzt in §12)

Die CLI (`tools/KolLibri.Cli`, rund 6.600 Zeilen, 35 Befehle) bleibt in diesem Teil unverändert auf Core. Ihre
Ausgaben hängen tief an Core-Typen (z. B. `adventure run`: `requestId`, `responseHash`, `abortReason` je Schritt).
Plan für Teil 2:

1. Live bewiesene Befehle zuerst auf `KolibriClient`/`KolibriSession`: `login`/`login refresh|status|clear`,
   `status`, `inventory`, `consume`/`rest`/`cast`, `adventure run` (Schleife über `AdventureAsync`/`FightAsync`/
   `ChooseAsync` mit `--fight-action`/`--choice-action` als Strategie bzw. Choice-Tabelle), `choice submit`, Mall-Kauf
   (`QuoteAsync`/`BuyAsync`), `shop`/`shop add`, `apply-prices` (`RepriceAsync`), `keys`, `daily run` (Routinen nach
   `KoLibri.Daily`), `arbitrage buy`. Ausgabe-Struktur und Exit-Codes bleiben; die JSON-Felder werden in der CLI aus den
   neuen Ergebnissen gebaut. Approval-Token und AgentGuard bleiben CLI-Logik (`token` über `KoLibri.Safety.ApprovalGuard`
   oder die bestehende Token-Datei), das Experimental-Gate ebenfalls.
2. Experimentelle Markt-Befehle (CRM, Bribe, Watchlist, Snatch, Reprice-Analyse) brauchen Extras (§6.2). Sie ziehen
   mit ihrem Code in die CLI oder in `KoLibri.Extras`; erst danach fällt der Core-Verweis der CLI.
3. CLI-Tests mit `KolibriOptions.HttpHandler` gegen die neue API, dazu ein Golden-Vergleich der JSON-Ausgaben alt/neu.

---

## 12. Umsetzungsstand Schritt 3, Teil 2: CLI auf der neuen Lib

Stand 2026-11-26. `tools/KolLibri.Cli` referenziert nur noch `KoLibri`, `KoLibri.Safety`, `KoLibri.Daily` und das neue
`KoLibri.Extras`; kein `KoLibri.Core`, kein `LibKolibri` (`KoLibri.Core.dll` liegt nur noch als private
Laufzeit-Abhängigkeit von `KoLibri` im Ausgabeordner). MCP und Desktop sind unverändert auf Core.

### 12.1 Neue Projekte und verschobener Code

| Projekt | Inhalt |
|---|---|
| `src/KoLibri.Extras` (`IsPackable=false`, nur `KoLibri`) | CRM (`BuyerCrmStore`/`BuyerCrmService`/`ShopBuyerAnalytics`, Käufer-Hash wie 0.4), Spend-Ledger (`FileSpendLedger`, `SharedMallSpendLedger`, `ArbitrageSpendLedger`, gleiche Dateien), Arbitrage (`ItemArbitragePlanner` unverändert, `ItemArbitrageService` und `ItemArbitrageExecutor` neu auf `Mall.GetOffersAsync`/`BuyAsync`, Reservierung im Ledger vor jedem Kauf) |
| `src/KoLibri.Daily` | `DailyRun`: die Planung von `daily run` (0.4: `AutoCombatDailyBridge.BuildSteps`) und die Ausführung (Lagerfeuer/Day Shortener, Hermit, Meme-Shop) über `session.GetPageAsync`/`PostAsync`; dazu `MemeShopPage`, `BurningLeavesPage`, `DailyInternetShopPlanner` aus Core. Marker über `session.Daily` mit den 0.4-Schlüsseln |
| `tools/KolLibri.Cli/Stores` | CLI-Kopien der 0.4-Dateiformate: Approval-Token (`approval-token.json`) und Choice-Policy |

Die Core-Kopien dieser Klassen bleiben, bis MCP und Desktop umgezogen sind; dann werden sie in Core gelöscht.

### 12.2 API-Ergänzungen (in `PublicAPI.Unshipped.txt` und im Stub)

| Ergänzung | Wofür |
|---|---|
| `KolibriClient.GetPriceHistoryAsync(ItemRef)` | `pricegun` ohne angemeldetes Konto (wie 0.4) |
| `Account.HasPassword` | `login refresh` ohne Flags (gespeichertes Passwort) und "frühere Wahl beibehalten" |
| `IdentityMismatchException` | `login refresh` meldet einen Kontowechsel als eigenes Ergebnis (`ok=false`, Exit 1) |
| `MallListing.LimitKnown`, `MallListing.QuantityKnown` | Arbitrage plant unbekannte Tageslimits und Bestände vorsichtig (wie 0.4) |
| `ShopSale.ItemName` | `crm` wertet nach Itemnamen aus (das Verkaufslog nennt keine Ids) |
| `PirateKeyCounts.Alpha/Bravo/Charlie/Delta`, `PirateKeyResult.Unclear`/`Steps`, `PirateKeyStep`, `PirateKeys.RareOptions` | `keys status/options/assemble/redeem` mit den 0.4-Ausgaben |
| `RelayOptions.Output` | `relay` schreibt jede Anfrage auf die Konsole |

Dazu eine Lib-Regel (keine Signatur): `LoginAsync` überschreibt ein bekanntes Konto mit Spieler-Id nicht, wenn KoL
die Identität nicht meldet (außer `AllowIdentityChange`) – wie `login refresh` in 0.4.

### 12.3 Befehle

**Auf der neuen API, gleiche Exit-Codes und Sicherheitsprüfungen** (Approval-Token vor jedem Request,
AgentGuard, Experimental-Gate unverändert): `login`, `login status|clear|refresh`, `token mint|show`, `status`,
`inventory` (inkl. `--probe`), `shop`, `shop add`, `consume`, `rest`, `cast`, `keys status|options|assemble|redeem`,
`mall --item`, `mall-reconcile`, `relay`, `adventure run`, `choice pending|submit|policy`, `daily run`,
experimentell `pricegun`, `crm`, `crm-block`, `apply-prices`, `arbitrage plan|buy`, sowie `agent-mode`.

**Vorerst nicht in 0.5-dev** (Meldung "moves onto the new KoLibri library in a follow-up PR. Nothing was sent.",
Exit 3, kein Request): `mafia-script`, `repl`, `adventure-plan`, `adventure locations|start|inspect|step|stop|
bounded-preview|choice-chain`, `combat`, `autocombat`, `daily campfire|hermit|internet-shop|routine|go|bridge|
breakfast|meme-shop|…`, `daily-audit`, experimentell `compare`, `bribe-rank`, `bribe-plan`, `workflow-plan`,
`categorize`, `reprice`, `snatch-plan`, `watchlist`, `market-scan`.

### 12.4 Abweichungen in der Ausgabe

Die Hüllen (`command`, `role`, `executed`, `previewOnly`, `approvalRequired`, Zähler, `stopReason`, `needsDecision`,
`openFight`, …) sind gleich. Nutzdaten, die in 0.4 rohe Core-Records waren, baut die CLI jetzt aus den 0.5-Records:

| Befehl | Abweichung |
|---|---|
| `status` | gleiche Feldnamen; entfallen: `pathId`, `ascensions`, `hardcore`, `familiarName`, `familiarWeight`, `skills`, `classId`, `organLimitConfidence`/`Note`. `equipment` hat Slot-Namen (`Hat`, `Weapon`, …) statt der api.php-Schlüssel; `timedEffects` haben zusätzlich `id`; Organ-Limits sind `null`, wenn unsicher |
| `login status` | `profileId` ist die Konto-Id, `endpoint` ist `null` |
| `consume` | die Organ-Prüfung läuft vor dem Request (zu voll → `rejection`, nichts gesendet); neues Feld `unclear` |
| `rest` | `afterEach` enthält nur noch den Stand nach der letzten Rast; `adventuresSpent` aus vorher/nachher |
| `cast` | `page` ist `null` |
| `shop add` | `outcome.storeMessage` ist `null`; die übrigen Felder kommen aus dem Ergebnis und einem Nachlesen |
| `mall --item` | ohne `sellerId`/`seller`/`ownerId`; `limitKind` aus `LimitKnown` |
| `pricegun`, `crm`, `keys status` | `IsStale` ist immer `false` (die Lib liefert keine alten Kopien, sie wirft) |
| `adventure run` | `steps` sind Begegnungen (`status` = `Fight`/`Choice`/`NonCombat`/`Blocked:…`, `requestId`/`responseHash` `null`), neues Feld `detail`; `auditPath`/`unknownPagePath` `null`; `--conditions-file` kennt nur `ItemCount AtLeast` und `AdventuresRemaining AtMost` (andere werden vor dem Senden abgelehnt); von mehreren `--fight-action` gilt die erste; ohne ermittelbare Snarfblat wird vor dem Senden abgebrochen |
| `daily run` | `auditPath` ist `null` (kein Bridge-Audit-Log mehr); der Archäologen-Spaten-Schritt wird nicht mehr gelistet |
| `arbitrage buy` | `steps[].reasons` sind Texte statt `MallPurchaseAbortReason`-Werte |
| `relay` | die User-Agent-Zeile sagt "the stored session's" statt den Wert zu nennen |

### 12.5 Tests

- `tests/KoLibri.Tests/CliTests.cs`: 22 Tests, die die CLI über `KolibriCli.OptionsOverride` gegen den Fake-Handler
  laufen lassen (Status-Felder, Inventar, Platzhalter senden nichts, Experimental-Gate, Session-Probe, Login-Status,
  `login clear`, Organ-Prüfung bei `consume`, Token-Prüfung bei `adventure run`, Lauf mit `--choice-action`,
  `needsDecision` mit Exit 4, Kampf mit Standard-`attack`, `choice submit`/`pending`, `daily run` Vorschau und
  Ausführung mit Marker, `mall --item`, `keys`, `apply-prices`-Regeln, Limiter aus Umgebungsvariablen).
- In `tests/KoLibri.Core.Tests` angepasst bzw. umgezogen: `ReplSessionTests` entfernt (REPL folgt), LoginClear-,
  REPL-Parser-, Session-Probe- und Status-Row-Tests nach `CliTests` verschoben; die übrigen CLI-Tests nutzen die
  CLI-Typen (`KoLibri.Cli.AccountRole`, `ApprovalTokenStore`, `ChoiceResolutionConfig`).
