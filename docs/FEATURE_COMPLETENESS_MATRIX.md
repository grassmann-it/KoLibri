# KoLmafia ↔ KoLibri: Feature-Completeness-Matrix

Diese Matrix vergleicht den aktuellen KoLmafia-Stand (Upstream `main`) mit dem
Stand im Repository. Der Fokus liegt auf Adventure, Kampf und wiederholbaren
Workflows. Preis-/Mall-Funktionen werden nur dort erwähnt, wo sie den
Adventure-Lauf blockieren oder absichern.

**Statuszeichen:** `✓` ist im aktuellen Scope nutzbar, `◐` ist ein
Sicherheitskern/Fixture/Preview ohne vollständigen Live-Adapter, `✗` fehlt,
`↗` ist bewusst zurückgestellt. Eine positive KoLibri-Bewertung bedeutet dabei
nicht, dass eine HTTP-Mutation bereits ausgeführt wird.

## Vergleich

| Fähigkeit | KoLmafia (Referenz) | KoLibri heute | Lücke/Risiko | Prio | Abnahmekriterium für KoLibri |
|---|---|---|---|:---:|---|
| Adventure-Ziel planen | `adventure [#] <location>` und Zustands-/Questdaten steuern Ort und Anzahl. | `DailyAdventurePlanner` erzeugt einen deterministischen Plan für Knucklebones, Skeleton of Crimbos Past, Lucky Surprise Eggs und Turnbudget; `AdventureState.IsStale` ergibt explizit `BlockedStaleSnapshot`. `FarmVsBuyPlanner` vergleicht Farmen mit Mallkauf. `✓` (Preview) | Nur wenige domänenspezifische Ziele; keine generische Condition-Sprache. | **P1** | Plan bleibt bei fehlendem Yield, Turnbudget oder stale Snapshot blockiert und ist über CLI/MCP/Desktop identisch. |
| Adventure-Request/Live-Transport | `AdventureRequest` sendet `adventure.php` (`snarfblat`), validiert Orte und behandelt viele Spezialseiten. | `AdventureExecutionGateway` persistiert den numerischen Katalogwert im Checkpoint und bindet ihn in die Transportfelder; CLI `adventure run --snarfblat N` kann ihn explizit weiterreichen; produktiv existiert weiterhin nur die sichere Transportgrenze, die Fixture-Implementierung sendet nicht an KoL. `◐` | Kein produktiver, routegebundener Adapter; `adventure-plan` bleibt read-only. | **P2** | Für eine allowlistete Zone existiert ein echter Adapter mit Request-Limit, Session-Gate, frischem Status und Post-Verify; nicht unterstützte Seiten stoppen. |
| Redirect-/Phasen-Dispatch | `GenericRequest` dispatcht Adventure → Fight/Choice und verarbeitet Hooks, Ergebnisse und Redirects. | Kernel-Phasen `Ready → Encounter → Fight/Choice → Reward/Complete/Aborted`; Gateway macht pro Schritt eine Anfrage. `◐` | Redirects und Folgekämpfe sind nur über abstrahierte Beobachtungen abgedeckt; kein vollständiger KoL-Dispatcher. | **P2** | Redirects werden anhand von Route/Parser sicher klassifiziert; kein implizites POST bei unbekanntem Ziel. |
| Encounter-Parsing | KoLmafia pflegt umfangreiche Parser und Result-Registrierung für aktuelle Seiten. | `KoLAdventureEncounterParser` erkennt Login, Fight, Choice, Reward und `Unknown`, extrahiert Round/Choice/Options/Hash. `✓` für Minimalmodell, `◐` für reale Vielfalt | Regex-Minimalparser kann neue Formulare, Ergebnisse oder Sonderseiten nicht sicher abbilden. | **P0** | Fixture-Katalog für jede unterstützte Zone; unbekannte oder unvollständige Seite wird `Unknown`/`Blocked`, nie geraten. |
| Unbekannter Zustand / manueller Stop | KoLmafia kann bei Fehlern/`abort` anhalten, hat aber breite Fallback-Logik. | Unknown Fight/Choice/Encounter, Session-Grenze und ungültige Beobachtung führen zu explizitem Abort mit Checkpoint. `✓` | Stop-Gründe müssen in allen Oberflächen sichtbar und resume-fähig bleiben. | **P0** | Jeder Stop enthält Reason-Code, letzte Response-Hash, Checkpoint und manuellen nächsten Schritt. |
| Choice-Chain | `ChoiceManager` validiert `whichchoice`/`option`, übergibt Extra-Felder und loopt, bis kein Choice-Formular mehr vorhanden ist. | Allowlist für Choice-ID/Option/Encounter; Extra-Felder werden begrenzt übernommen; unbekannte Option bricht ab. `◐` (Fixture) | Keine allgemeine Choice-Datenbank, keine settingsbasierte Automatisierung, kein produktiver Transport. | **P1** für unterstützte Tageszonen, **P2** breit | Jede unterstützte Choice hat Parser-Fixture, erlaubte Optionen, Extra-Feld-Schema und Folge-Response-Tests. |
| Kampf-Request/Turn-Loop | `FightRequest` führt Round-0-Initialisierung, `fight.php`-Aktionen, Multi-Fights und Folge-Choices aus; der aktuelle Upstream-Code ist ein umfangreicher, zonenspezifischer Parser/Dispatcher. | `BoundedManualCombatRunner` führt über `ManualCombatGateway` eine begrenzte Round-Sequenz aus, inspiziert vor jedem Preview erneut und stoppt bei Terminal-, Gateway-, Timeout- oder Step-Limit-Zustand. `◐` | Ein vollständiger zonenspezifischer KoL-Dispatcher und AutoCombat-Live-Policy fehlen weiterhin. | **P2** | Ein bounded Runner führt eine allowlistete Sequenz aus, prüft Round/Monster-Fortschritt und stoppt bei Abweichung. |
| Kampfregeln / Skills / Items / Makros | KoLmafia nutzt Kampfpräferenzen, Combat Actions, Skills/Items, Makros, Pickpocket/Run und Spezialmonsterregeln. | `AutoCombatPolicy` bleibt deklarativ; `AutoCombatAdventureAdapter` übersetzt eine Entscheidung nur dann in eine `AdventureFightAction`, wenn die Action im aktuellen Fight-Snapshot sichtbar und aktiviert ist. CLI/MCP können zusätzlich einen Recovery-Plan als Gate anwenden. `◐` | Noch kein vollständiges Ressourcen-/Recovery-Commit und kein freies Macro-System. | **P2** (begrenzte Teilmenge) | Deklarative Regeln für wenige bekannte Monster/Runden (inkl. Dry-Run) werden separat versioniert; kein freies Script darf Requests erzeugen. |
| Recovery / Ressourcen / Safety-Checks im Kampf | RecoveryManager, HP/MP-Schwellen, Consumables, Effects, Equipment und Familiar fließen in die Entscheidung ein. | `RecoveryPlanner` erzeugt einen bounded Read-only-Plan; `AutoCombatAdventureAdapter` und `BoundedManualCombatRunner` akzeptieren optional nur einen Plan mit `CanEnterCombat`, andernfalls bleibt die Action vor Preview/POST blockiert. Der echte Fight-Gateway-POST braucht weiterhin einen frischen, transportgebundenen Recovery-Read. `◐` | Live-Kampf ohne Ressourcen-Gate wäre nicht sicher. | **P1** | Vor jedem Fight-POST frischer Status, Recovery-Reserve und erlaubte Consumables prüfen; bei fehlender Information `Blocked`. |
| Kampf-/Choice-Post-Approval | Kein einheitlicher Human-Approval-Token für jeden Kampf-POST. | `IAdventureApprovalHook` vor jedem mutierenden Adventure/Fight/Choice-Request; `IAdventureVerificationHook` danach. `✓` | Hook muss an echte Approval-/Audit-Inbox angebunden werden, bevor Live-Ausführung freigeschaltet wird. | **P0** | Ohne Approval kein POST; Verify-Fehler persistiert Abort und sendet nicht erneut. |
| Timeout / Retry / Idempotenz | Adventure/Fight-Requests erlauben Upstream-Retry-on-timeout; die breite Zustandslogik versucht weiterzulaufen. | Non-idempotentes POST bleibt bei Timeout `Indeterminate`; Gateway markiert In-Flight, verlangt Refresh und sendet nicht blind erneut. Request-ID, Fingerprint, Response-Hash und CAS-Checkpoint sind vorhanden. `✓` (Sicherheitskern) | Reconciliation muss an den echten KoL-Transport gebunden werden. | **P0** | Timeout-Fixture beweist: genau ein POST, danach Refresh und Entscheidung anhand des aktuellen Encounter; kein Doppelturn. |
| Checkpoint / Crash-Resume | KoLmafia hält Session-/Preference-Zustand, aber keinen vergleichbaren bounded CAS-Checkpoint pro Mutation. | `AdventureCheckpoint`, Response-History, In-Flight-Marker, Revision/CAS, `ResumeAsync` und lokaler JSON-CAS-Store sind implementiert. `✓` (Core) | Retention-/Cleanup-Policy und vollständige UI-Resume-Controls fehlen noch. | **P0** | Prozessabbruch nach jedem Server-Response ist resume-fähig; `Resume` emittiert zuerst nur Refresh, nie den alten POST. |
| Tageslimits / Counter | Conditions und Sessionzustand können Abenteuer stoppen; tägliche Sonderregeln sind verteilt in Fachlogik/Preferences. | `AdventureDailyLimitContract`, Counter-Store und Run-Reservation schützen Turns/Runs; Planner kennt Ziel und Egg-Cap. `✓` für Tagesplan | Keine allgemeine tägliche Resource-/Semi-Rare-/Choice-Condition. | **P1** | Limit wird vor Start reserviert, nach Response gespeichert und gegen frischen Account-Snapshot verifiziert. |
| Conditions / Objectives | `conditions add` unterstützt Items, Choice-Adventures, Level, Stats, HP und MP; `adventure` hält bei erfüllten Conditions. | `AdventureCondition`/`AdventureConditionSnapshot` evaluieren Item/Count, Stat, HP/MP, Choice-Count und Turns; `AccountAdventureConditionSnapshotProvider` bindet frische Status-/Inventar-Reads; `BoundedAdventureRunner` stoppt vor einem POST bei erfülltem Ziel oder fehlendem Snapshot. `◐` | HP/MP/Choice benötigen weiterhin spezialisierte Combat-/Choice-Reads; vollständiger Live-Run-Command fehlt. | **P1** | Typed `Condition`-Modell mit item/count/stat/HP/MP/choiceadv/turns; jede Condition liefert Fortschritt und Stop-Grund. |
| Farm-vs-Buy / Ticket-Ökonomie | `acquire` kann je nach Zustand Item erzeugen, kaufen, aus Storage holen oder Mall nutzen; Conditions treiben Adventure. | `FarmVsBuyPlanner` ist reine ROI-/Confidence-Vorschau; Coinmaster/One-Day-Ticket-Ranking ist read/preview. `✓` (Plan) | Kein Commit-Adapter, keine vollständige Acquire-Kette. | **P1** | Farm/Bay-Entscheidung bindet Ticket-/Transfer-/Consumable-Kosten, Confidence und aktuelle Mall-Referenz; Commit bleibt separater Approval-Schritt. |
| Wiederholbarer Tagesworkflow | CLI/ASH-Skripte, `repeat`, Moods/Triggers, `breakfast` und Quest-Kommandos erlauben weitgehende Automatisierung. | `KoLWorkflowPlanner` bildet Main/Mallbot-Phasen mit ReadOnly/Ready/ApprovalRequired/Blocked/Deferred; CLI/MCP erzeugen JSON-Plan. `adventure-plan --periodic` liest pro Tick frisch und nutzt den bounded Core-Scheduler. `✓` (Dry-Run), `◐` (Ausführung) | Kein mutierender langlaufender Adventure-Worker und keine zeitbasierte Commit-Ausführung. | **P1** | Ein Tageslauf ist deterministisch, stoppbar und resume-fähig; Plan, Warnungen und Approval-ID sind in CLI/MCP/Desktop gleich. |
| Scheduler / Watchlist | KoLmafia kann Skripte wiederholen; externe Skripte/cron übernehmen Zeitplanung. | `WatchlistScanner`/JSON-Store scannt Mall/Pricegun read-only mit Worker-Limit; `DailyAdventurePlanScheduler` liest pro Tick einen frischen Snapshot und erzeugt bounded Pläne ohne Mutation. Kein automatischer Kauf. `◐` | „Scheduler“ darf nicht den Eindruck erwecken, live zu mutieren. | **P1** | Scheduler erzeugt nur Plan/Preview und Audit; Ausführung erfordert explizite Run-Approval und bounded worker. |
| CLI / MCP / UI | KoLmafia CLI, GUI, Relay Browser und ASH teilen Sessionzustand. | CLI `adventure-plan`/`adventure run`, MCP Preview-Tools (`kol_adventure_daily_schedule` eingeschlossen), opt-in `LibKolibri`-Combat-Gateway und Desktop-Services teilen Planner/Gateway-Verträge; MCP-AutoCombat kann zusätzlich einen Recovery-Snapshot als Safety-Gate prüfen; Live-CLI ist explizit `--execute` + Approval-Token. `◐` | MCP/Desktop-Live-Command und Golden-JSON für den vollständigen Runner fehlen noch. | **P1** | Golden JSON für denselben Snapshot in CLI/MCP/Desktop; Executor erst nach demselben Gateway-Vertrag verfügbar. |
| Session-/Login-Grenze | `GenericRequest` erkennt Login/Redirect und verwaltet Cookies/Session. | Verschlüsselte Profile, Login, Cache und `KoLResponseClassifier` erkennen Login-/Stale-Seiten; Gateway stoppt bei Session-Grenze. `✓` | Adventure-Transport muss dieselben Session-Clients verwenden, nicht eigene Cookies. | **P0** | Login-Redirect oder abgelaufene Session erzeugt keinen Folge-POST und bleibt im Audit sichtbar. |
| Audit / Mutationsledger | KoLmafia protokolliert Requests/Events, aber ohne KoLibri-Preview→Approval→Commit-Vertrag. | Audit-Journal, Operation-Ledger und Approval-Contracts; `AuditedAdventureHooks` schreibt Adventure-Preview, Approval/Commit und Verify mit Request-ID, Checkpoint und Response-Hash. `✓` (Vertrag) | Live-Host muss ein Journal injizieren; Retention/Rotation ist noch offen. | **P0** | Jeder POST hat Plan-/Approval-/Request-ID, Vorher/Nachher-Status, Response-Hash und Verify-Ergebnis. |
| Test-/Fixture-Strategie | Upstream enthält Adventure-, Fight- und Choice-Fixtures sowie Fake-HTTP-Tests. | `tests/.../Fixtures/Adventure` plus Parser-, Kernel- und Gateway-Tests; Fixture-Transport prüft Korrelation/Timeout/Approval. `✓` für Sicherheitsfälle | Noch keine breite Zonenabdeckung und kein Live-Contract-Test gegen einen Route-Adapter. | **P0** | Jede neue Zone liefert HTML-Fixtures für Start/Fight/Choice/Reward/Login/Unknown und testet die nächste Request-Aktion. |
| Zonen-/Quest-/Path-Abdeckung | Viele Spezialorte, Quests, Paths, Ascension- und Mini-Games sind in Fachklassen integriert. | Kein Quest-/Path-/Ascension-Automation-Ziel; generischer `AdventureId`-Vertrag reicht für bounded Adapter. `↗` | Vollständige Parität wäre ein eigenes Produkt und erhöht Sicherheitsfläche. | **P2** für ausgewählte Farmzonen, sonst bewusst später | Unterstützte Zone explizit katalogisieren; alles andere bleibt `NotAllowlisted`/manuell. |
| Equipment / Familiar / Modifier / Maximizer | KoLmafia kann Outfit, Familiar, Modifier und `maximize` für den Kampf wählen. | Nur read-/ROI-relevante Bestandsdaten; keine Ausrüstungs- oder Familiar-Auswahl im Adventure-Gateway. `✗` | EV-/Survival-Schätzungen bleiben konservativ oder blockiert. | **P2** | Read-only Modifier-/Equipment-Snapshot zuerst; Auswahl erst nach deterministischer Policy und Preview. |
| Buffs / Mood / Breakfast / Healing außerhalb Kampf | CLI-Moods, Trigger, Breakfast, Rest/Relax und Buff-/Effect-Management sind integriert. | Nicht vorhanden; Eggs sind ein expliziter Planner-Schritt, keine allgemeine Effect-Policy. `✗` | Vor dem Farming kann eine manuelle Vorbereitung erforderlich sein. | **P2** (oder bewusst P3) | Nur typisierte, begrenzte Preflight-Schritte mit eigener Approval; kein ASH-Klon. |
| Freie Scripting-Sprache / Relay Browser | ASH/CLI mit Variablen, Funktionen, Branching, Loops und Relay Browser. | Bewusst kein JavaScript-/ASH-Klon; typisierte Workflows und MCP/CLI-Schemas. `↗` | Keine Drop-in-Kompatibilität für bestehende KoLmafia-Skripte. | **P3 / Nichtziel** | Nicht in P0–P2 einplanen; Plugin-/Provider-Schnittstelle erst nach stabilen Verträgen. |

## Priorisierte Umsetzung

### P0 – vor jeder Live-Mutation

1. Den vorhandenen Parser-/Kernel-/Gateway-Sicherheitskern als verbindlichen
   Vertrag festschreiben: unbekannt = Stop, keine implizite Aktion.
2. Adventure-Fixtures und Contract-Tests für Login-Redirect, stale Session,
   Round-/Choice-Mismatch, Duplicate-Response, Timeout, Approval-Deny und
   Checkpoint-CAS vervollständigen.
3. Adventure-POSTs an das bestehende Session-/Request-Gate und Audit-Ledger
   anbinden. Kein separater Cookie- oder Retry-Stack im Adapter.
4. Approval- und Verify-Hooks an dieselbe Preview → Approval → Commit → Verify-
   Kette wie Kauf, Transfer und Reprice anschließen.

**P0-Fertig:** Ein absichtlich fehlerhafter oder unbekannter Server-Response
führt reproduzierbar zu `Blocked`/`Aborted`, hinterlässt einen resume-fähigen
Checkpoint und sendet keinen zweiten POST.

### P1 – täglicher, kontrollierter Mehrwert

1. Typed Conditions (Item/Count, Turns, HP/MP, Choice-Count, Stats) und ein
   frischer Status-/Inventory-Read vor jedem geplanten Lauf.
2. Einen kleinen Tages-Runner für eine katalogisierte Zone bauen: Ziel,
   Turnbudget, Egg-/Ticket-Reserve, Stop-Gründe und Audit; der bounded Core-
   Runner ist vorhanden, CLI/MCP/Desktop-Preview und Live-Statusbindung fehlen.
3. Farm-vs-Buy sowie Ticket-/Coinmaster-Kosten in denselben Tagesplan
   integrieren; Kauf/Farm bleibt getrennte Approval-Aktion.
4. CLI, MCP und Desktop auf Golden-JSON und dieselben Checkpoints/Approval-IDs
   bringen. Der vorhandene Mall-/Pricegun-Watchlist-Worker bleibt read-only.

**P1-Fertig:** Ein Nutzer kann einen Tageslauf planen, einen bounded Run
   freigeben, bei stale/unknown/limit sicher stoppen und anschließend ohne
   Doppelturn fortsetzen.

### P2 – gezielte Abdeckung statt KoLmafia-Klon

1. Produktiven Adventure-/Fight-/Choice-Transport für wenige katalogisierte
   Farmzonen ergänzen; je Zone eigene Formfelder und Fixtures.
2. Deklarative Kampfregeln für bekannte Monster/Runden (Attack/Skill/Item/Run)
   sowie minimale Recovery-Gates; keine freie Macro-/ASH-Ausführung.
3. Redirect-/Multi-Fight-/Sonderchoice-Ketten nur nach Fixture-Belegen
   erweitern. Quest-/Path-/Ascension-Seiten bleiben nicht automatisch.
4. Read-only Equipment/Familiar/Modifier-Daten anschließen, wenn sie Farm-ROI
   oder Überlebensentscheidung tatsächlich verbessern.

## Abgrenzung zur KoLmafia-Parität

KoLmafia bleibt für allgemeine Adventure- und Kampfbreite die Referenz. KoLibri
strebt für P0/P1/P2 eine kleinere, überprüfbare Teilmenge an: wenige bekannte
Zonen, explizite Aktionen, frische Reads, Approval und Resume. ASH-/Relay-
Kompatibilität, vollständige Quest-/Path-Automation, globale Choice-Datenbank
und freie Kampf-Macros sind keine Voraussetzung für den Mallbot-/Tagesbetrieb
und bleiben nachgelagert.

## Quellen

- Repo-Plan und bewusstes Scope: [`FEATURE_COMPLETENESS_PLAN.md`](FEATURE_COMPLETENESS_PLAN.md)
- Repo-Adventure-Referenz: [`KOLMAFIA_ADVENTURE_RESEARCH.md`](KOLMAFIA_ADVENTURE_RESEARCH.md)
- KoLibri-Minimalparser: [`KoLAdventureEncounterParser.cs`](../src/KolArbitrage.Core/Adventure/KoLAdventureEncounterParser.cs)
- KoLibri-State-Machine: [`AdventureRunKernel.cs`](../src/KolArbitrage.Core/Adventure/AdventureRunKernel.cs)
- KoLibri-Gateway/Approval/CAS: [`AdventureExecutionGateway.cs`](../src/KolArbitrage.Core/Adventure/AdventureExecutionGateway.cs)
- KoLibri-Tagesplan: [`DailyAdventurePlanner.cs`](../src/KolArbitrage.Core/Adventure/DailyAdventurePlanner.cs)
- KoLibri-Workflow: [`WorkflowModels.cs`](../src/KolArbitrage.Core/Integration/WorkflowModels.cs)
- KoLmafia [`AdventureRequest`](https://github.com/kolmafia/kolmafia/blob/main/src/main/java/net/sourceforge/kolmafia/request/AdventureRequest.java), [`FightRequest`](https://github.com/kolmafia/kolmafia/blob/main/src/main/java/net/sourceforge/kolmafia/request/FightRequest.java), [`ChoiceManager`](https://github.com/kolmafia/kolmafia/blob/main/src/main/java/net/sourceforge/kolmafia/session/ChoiceManager.java)
- KoLmafia [CLI-/Conditions-/Adventure-Handbuch](https://github.com/kolmafia/kolmafia.sourceforge.net/blob/main/scripting.html)
