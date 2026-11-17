# KoLibri Feature-Completeness- und Produktplan

Dieses Dokument definiert den schlanken KoLmafia-Ersatz, den KoLibri wirklich
braucht. KoLibri soll nicht jede historische KoLmafia-Funktion kopieren. Der
Kern ist eine kleine, testbare KoL-Anbindung mit sicheren Read-/Preview-/Commit-
Grenzen. Arbitrage, Mallbetrieb und wiederholbare Tagesworkflows haben Vorrang
vor einem vollständigen Script-Ökosystem.

## Zielbild

`LibKolibri` bleibt die transport- und UI-unabhängige KoL-Domäne. Die Domäne
kennt Konten, Status, Inventar, Mall, Preisquellen, Items, Rezepte,
Coinmaster, Abenteuer und Approval-Policies. Sie führt keine HTTP-Aufrufe
selbst aus.

Darauf sitzen drei gleichwertige Oberflächen:

1. **CLI:** reproduzierbare Jobs, JSON für Shell und KI.
2. **MCP/Automation Gateway:** dieselben Read- und Preview-Funktionen mit
   strukturierten Schemas; Mutationen nur über Preview → Approval → Commit →
   Verify.
3. **Avalonia-Desktop:** ein Bedienpanel für dieselben Services; keine
   Geschäftslogik und keine eigenen KoL-Requests im ViewModel.

Die Desktop-App bekommt eine kompakte Arbeitsoberfläche mit globalem
Account-/Session-Status, Tagesplan und einer dichten Tabelle für Preise und
Vorschläge. oChan/MeowChan dient als UX-Inspiration für Navigation,
Thumbnail-/Statuskarten, dunkles Theme und schnelle Detailansichten; KoLibri
übernimmt keine Imageboard-Fachlogik.

## Was KoLmafia abdeckt und was KoLibri übernimmt

| Bereich | KoLibri-Ziel | Priorität | Entscheidung |
|---|---|---:|---|
| Login, Session, zwei Konten | Verschlüsselte Profile, Cookie-Lifecycle, Ablauf-/Refreshstatus | P0 | vollständig früh |
| Status, Inventar, Shop, Mall | gecachte Snapshots, IDs, Mengen, Seller, Stale-Anzeige | P0 | vollständig früh |
| Preisquellen | Mall, Pricegun, eigene Verkäufe, Historie, robuste Referenz | P0 | vollständig früh |
| Repricing | Vorschläge, Mindestpreis, Gebühren, Konfliktprüfung, Commit/Verify | P0/P1 | früh |
| Underprice-Snatcher | Watchlists, Top-N, Liquidität, Seller-Blacklist, Budget, Preview | P1 | früh |
| Kaufadapter | kleine Chunks, frischer Re-Read, Approval, Post-Buy-Verifikation | P1 | nach Preview-Härtung |
| Main/Mallbot-Transfer | Item-/Meat-Transfer, Ziel-/Quellprüfung, Audit, Preview | P1 | früh nach Kaufadapter |
| Adventure planner | Knucklebones/Skeleton, Daily-Limits, Ressourcen- und Zielplanung | P1 | früh |
| Adventure executor | Zone/Fight/Choice-Aktionen, Checkpoints, Stop bei unbekanntem Zustand | P2 | später, konservativ |
| Kampfautomatisierung | deklarative Regeln, bekannte Kampfentscheidungen, Dry-Run | P3 | nur wenn nötig |
| Inventar-/Item-Master | Tradeability, Bindung, Package/Open, NPC, Rezept-/Fold-/Zap-Gruppen | P0/P1 | früh |
| Crafting/Transformation | Rezeptgraph, Opportunity-Cost, Fehlerrisiko, Ergebnisliquidität | P2 | nach Item-Master |
| Coinmaster/Airport | Währungsgraph, Ticketkosten, Opportunity-Cost, ROI | P1/P2 | früh als Preview |
| IotM/MrStore | dynamischer Katalog, Mr-A-Numeraire, Utility-NPV, Liquidität | P2 | später als Kaufautomatik |
| CRM | Verkäufe, Käufer-Key, Punkte, Segmentierung, Blockliste | P1/P2 | read-only zuerst |
| Messages/Gifts | Intent-/Approval-Objekte, Audit und Opt-in | P3 | später |
| Scripting-Language | kein JavaScript-/CLI-Klon am Anfang; typisierte Workflows | P3 | bewusst später |
| Relay Browser | kein Vollklon; optional eingebettete KoL-Seiten/Deep Links | P4 | skip/optional |
| Familiar/Equipment/Modifiers | Datenmodell und read-only Empfehlung | P3 | nur für Farm-/ROI-Fälle |
| Quest-/Path-/Ascension-Automation | nicht Ziel des Mallbots | P4 | skip |

## Priorisierte Meilensteine

### M0: Vertrauenswürdige Datenbasis

**Abhängigkeiten:** keine.

- `AccountReadClient`, `KoLMallReadClient`, `PricegunReadClient` und
  `KoLShopSalesReadClient` auf gemeinsame Snapshot-/Cache-Verträge bringen.
- Item-ID statt Name als Primärschlüssel; Namen nur als Anzeige-/Suchalias.
- Source-Freshness, Stale-Fallback, Request-Gate, Inflight-Deduplizierung und
  Cache-Invalidierung testen.
- Item-Master als versioniertes lokales Artefakt mit Parser-Diagnostics,
  Tradeability, Account-Bound, Package/Open und expliziten Variant-Links.
- Audit-Event für jede Beobachtung: Account, Quelle, Zeitpunkt, Stale-Status,
  Request-ID.

**Abnahmekriterien:** beliebiger Offline-Snapshot kann ohne Netz reproduziert
werden; kein Vorschlag basiert unmarkiert auf einer alten oder eigenen Listing-
Quelle.

### M1: Arbitrage-MVP

**Abhängigkeit:** M0.

- Watchlist-Scheduler für Tiny Plastic, High-Value, Crimbo/Elf Guard,
  Pirate-Encryption, IotM-Packages und Coinmaster-Outputs.
- `MarketPriceEstimator` und `UnderpricedPurchasePlanner` als eine Policy-
  Pipeline mit Gebühren, Reserve, Mindestmarge, Liquidität, Stale- und
  Tradeability-Gates.
- `snatch-plan` und `reprice` als JSON-Preview; sortierbare Desktop-Tabelle.
- Seller-/Store-Blacklist, Own-Store-Exclusion und `reason codes` in jeder
  Entscheidung.
- Tagesreport: neue Kandidaten, akzeptierte Vorschläge, abgelehnte Gründe,
  gebundenes Kapital, erwarteter Nettoerlös.

**Abnahmekriterien:** dieselben Eingabesnapshots erzeugen deterministisch
dieselbe Rangfolge; keine Aktion sendet einen Kauf-/Preis-POST.

### M2: Mutationskern

**Abhängigkeit:** M1, Approval-Verträge und Audit-Log.

- Mallkauf: frischer Listing-Read, Store/Preis/Menge exakt binden, kleiner
  Chunk, Budget-/Reserveprüfung, Commit, Inventar-/Meat-Verifikation.
- Shop-Preisupdate: erwarteten alten Preis binden, Konflikt bricht ab, danach
  erneuter Shop-Read.
- Transfer: Main ↔ Mallbot als zwei getrennte Preview-/Commit-Schritte;
  keine implizite Kombination mit Kauf oder Adventure.
- Idempotency-Key pro Operation; Wiederholung darf nicht doppelt kaufen,
  transferieren oder relisten.

**Abnahmekriterien:** künstliche Price-Change-, Sold-Out-, Login-Redirect-,
Budget- und Partial-Failure-Fixtures führen zu sicherem Abort und klarer
Recovery-Anzeige.

### M3: Tagesbetrieb und Farming

**Abhängigkeit:** M0/M1; mutierende Adventure-Adapter erst nach M2.

- täglicher Knucklebones-/Skeleton-Plan mit Adventure-Budget, Lucky-Egg-
  Verbrauch und Tages-/Semi-Rare-Limits.
- Farm-vs-Buy für Glaciest, Volcano, Beach, Coinspiracy, Hermit-Clovers,
  Pirate-/Crimbo-Material und saisonale Reserven.
- Coinmaster-Graph inklusive Ticket-, Währungs- und Opportunity-Cost.
- Inventory-Aging: `hold`, `reprice`, `rotate-one`, `craft`, `liquidate`.
- Zeitplaner: Beobachten → Planen → Preview ausgeben; Commit bleibt separat.

**Abnahmekriterien:** ein Tageslauf ist resume-fähig, stoppt bei unbekannter
Choice/Fight oder stale Session und erzeugt einen vollständigen Audit-Report.

### M4: IotM, Crafting und CRM

**Abhängigkeit:** M0, M1, Coinmaster- und Item-Master-Verträge.

- dynamischer MrStore-Katalog statt hardcodierter Monatsliste.
- Tradeability und Giftability getrennt bewerten; `t`-Items können Mall-
  handelbar sein, ohne dass sie zwischen Accounts giftbar sind.
- Utility-NPV gegen Mr-A-Opportunity-Cost, historische Verkäufe und
  Liquiditätsrisiko.
- Rezeptgraph für Cook/Mix/Smith/Still/Fold/Zap/Pulverize/Untinker; jede
  Kante mit Input-Opportunity-Cost und Absatzrisiko.
- CRM read-only: Wiederkäufer, Umsatz, Punkte, Segment- und Blocklisten.

**Abnahmekriterien:** kein IotM-/Crafting-Vorschlag ohne explizite Quelle,
Tradeability und Liquiditätsbegründung.

### M5: Schlanker KoLmafia-Ersatz

**Abhängigkeit:** stabile M0–M4 APIs und reale Betriebsdaten.

- typisierte Workflow-Definitionen und kleine Plugin-/Provider-Schnittstelle.
- bekannte Kampfregeln und Adventure-Checkpoints, aber kein vollständiger
  Java-Scripting-Klon.
- Equipment/Familiar/Modifier-Daten nur dort, wo sie Farm- oder ROI-Entscheide
  verbessern.
- optionaler Relay-/Browser-Link statt eingebettetem Vollbrowser.
- MCP-Toolset auf dieselben Domain-Commands abbilden; kein zweiter Policy-Stack.

**Abnahmekriterium:** ein Nutzer kann denselben Tageslauf per CLI, MCP oder
Desktop starten und erhält denselben Plan, dieselben Warnungen und dieselbe
Approval-ID.

## Desktop-UX-Grenzen

Die erste Avalonia-Version braucht fünf Hauptbereiche:

1. **Cockpit:** Main/Mallbot-Session, Meat, Adventures, stale Quellen,
   offene Approvals und Tagesziel.
2. **Arbitrage:** dichte Tabelle mit Item, Bestand, Mallboden, Referenz,
   erwarteter Nettoerlös, Marge, Liquidität, Alter, Entscheidung und Grund.
3. **Shop:** eigene Listings, Reprice-Vorschläge, Preisverlauf, Batch-Preview.
4. **Adventure/Farm:** Ziel, Turns, EV/Turn, Farm-vs-Buy, Reserven,
   Stop-Gründe.
5. **Operations:** Approval-Inbox, Audit-Log, Blacklist, Cache und Worker-
   Status.

Kein ViewModel darf KoL-HTML parsen oder selbst `HttpClient` aufrufen. Jede
Ansicht konsumiert einen Query-/Command-Service, der auch von CLI und MCP
verwendet wird. Tabellen brauchen Spalten-Pinning, Filter, Keyboard-Navigation,
Copy-as-JSON und klare Stale-/Preview-Badges. Farbige Hervorhebung darf nie
der einzige Statuskanal sein; Text und Icon müssen dieselbe Information tragen.

## API-Schnittstellen

```text
IAccountQuery              status, inventory, sessions
IMarketQuery               mall, price history, own shop, snapshots
IArbitragePlanner          underprice, reprice, farm-vs-buy
IWorkflowPlanner           daily plan, dependencies, stop reasons
IItemCatalog               item facts, variants, tradeability
IApprovalService            preview, approve, expire, commit, verify
IAuditLog                  immutable operation and source events
ITransferGateway            preview/commit/verify Main <-> Mallbot
IAdventureGateway           preview first; later bounded execution
```

CLI, MCP und Avalonia dürfen nur diese Verträge verwenden. Transportadapter
implementieren KoL-Routen; LibKolibri enthält die abstrahierten Contracts und
Gateway-Registrierung, Core enthält die reine Bewertung.

## Teststrategie

- Parser-/HTML-Fixtures für jede KoL-Seite und Login-Redirect.
- Deterministische Clock und Fake-Transport für Cache, Rate-Limit und Stale.
- Property-Tests für Gebühren, Preis-/Mengenlimits, Idempotency und
  Budgetreserven.
- Golden JSON für dieselbe Snapshot-Eingabe in CLI, MCP und Desktop.
- Mutation Tests: Preis geändert, Listing ausverkauft, Session abgelaufen,
  Meat/Inventar abweichend, Transfer teilweise erfolgreich.
- Contract Tests für KoL-Routen hinter Fake-Server; keine Live-Käufe in CI.
- UI Smoke Tests für Navigation, Filter, Approval-Inbox und Tastaturbedienung.

## Bewusste Nicht-Ziele

- kein vollständiger Klon von KoLmafia-Scripting, Relay Browser oder Java-GUI;
- keine globale Mall-Crawling-Schleife ohne Watchlist und Cache;
- keine automatische Gift-/Message-Kampagne ohne Opt-in und Approval;
- keine IotM-/Seasonal-Hardcodierung ohne dynamische Quelle und Ablaufdatum;
- keine automatische Entscheidung aus einem einzelnen Preis-Ausreißer.


## Design-System: Stickman Standard und Ops Dark

Der Standardmodus soll sofort KoL-inspiriert wirken, ohne die alte KoLmafia-Java-GUI nachzubauen. `Stickman Standard` nutzt eine helle, leicht papierartige Fläche, schwarze Linien, einfache Strichmännchen-Silhouetten und kleine handgezeichnete Markierungen. Funktionale Motive sind Knochen für Tagesziele, ein Meat-Beutel für Kapital, Piratenflaggen für Crimbo-/Pirate-Reserven und ein Preisschild für Mallaktionen. Keine Information wird ausschließlich über Farbe oder Zeichnung vermittelt; Text, Icon und Status-Badge bleiben zusammen sichtbar.

Semantische Tokens gehören zentral in `App.axaml` bzw. eine eigene ResourceDictionary: Canvas/Ink, Panel/Raised, Text/Muted, Border/Focus, Accent, Success, Warning und Error. Meat-Gold, Crimbo-Rot, Adventure-Grün und Tintenblau sind Akzente, keine unabhängigen Seitenfarben. `comfortable` ist die Standarddichte, `compact` die Dichte für große Malltabellen. Theme-Wechsel dürfen Navigation, Filter und laufende Snapshots nicht verlieren.

`Ops Dark` ist optional für lange Scans: dunkle Flächen, dieselben semantischen Tokens und dieselbe Informationsordnung. Ein späteres saisonales Crimbo-Akzentset darf Statusfarben nicht umdeuten.

## oChan/MeowChan-Patterns für KoLibri

- Shell-Header mit Account-/Session-Badges, Cache-Alter, Refresh und More-Menü;
- responsive Navigation mit fünf direkten Seiten und More-Flyout;
- zentrale Light/Dark-Theme-Dictionaries, Tokens für Typografie, Flächen, Fokus, Dialoge und Dichte;
- kompakte, virtualisierte Tabellen mit persistenten Spalten, Filtern und Tastaturfokus;
- rechter Inspector erst nach Zeilenauswahl: Preisverlauf, Top-Listings, Stale-Quellen, Confidence und Approval-Diff;
- nicht blockierende Loading-, Empty-, Stale- und Error-Banner sowie ein kompakter Event-Stream;
- Command Palette und `Ctrl/Cmd+1..5`, Suche, Refresh und Approval-Inbox;
- Fokus kehrt nach Refresh, Navigation oder Inspector-Schließen zur letzten Zeile bzw. zum letzten Control zurück.

Die aktuelle [KoLibri.Desktop/MainWindow.axaml](../KoLibri.Desktop/MainWindow.axaml) ist noch ein statischer Mock: hardcodierte KPI-Werte, Text statt DataGrid, keine echten Seiten-ViewModels und keine Theme-Umschaltung. UI-Arbeit startet mit M0/M1-Query-Services; oChan liefert Shell-, Fokus- und Layout-Patterns, nicht Geschäftslogik.
