# KoLibri

KoLibri ist der schlanke .NET/Avalonia-Nachfolger für die tägliche Kingdom-of-Loathing-Arbitrage: Preisquellen zusammenführen, unterbewertete Angebote markieren, Bestände zwischen Main und Mallbot planen und die täglichen Knucklebones vorbereiten. Die Oberfläche übernimmt die guten Grundideen aus MeowChan/oChan – kompakte Navigation, dunkles Theme, klare Statuskarten und ein gemeinsamer Workspace – ohne deren Imageboard-Code zu kopieren. Login und Mall-Abfrage sind read-only funktionsfähig; Käufe, Verkäufe und Transfers bleiben zunächst geschützt.

## Was jetzt enthalten ist

- Avalonia-Desktop mit dunkler, kompakter Arbeitsoberfläche und Navigation für Übersicht, Pricing, Konten/Transfer und Adventure.
- `PricingEngine` mit gewichtetem Median, Pricegun-/Mall-/Shop-Quellen, Referenzen über Pirate Encryption Key, Elf Guard Present und Crimbucaneer Booty sowie Fresh-/Stale-Cache. Pricegun wird read-only aus der öffentlichen Item-Historie gecacht.
- Sicherheitsregeln für Mindestmarge, Unterpreis-Erkennung und vorsichtiges Unterbieten. Die Engine platziert selbst keine Order.
- Zwei Rollen (`Main`, `Mallbot`) mit Profilen und verschlüsselter lokaler Credential-Ablage. Zugangsdaten werden nicht in der Profil-JSON gespeichert.
- KoL-Login über `login.php` mit Challenge-Response, wenn KoL einen Challenge-Token liefert, und HTTPS-Fallback für die aktuelle `secure=0`-Loginseite; Benutzername, optional gespeichertes Passwort und Session-Cookies landen nur verschlüsselt im Secret Store.
- Read-only-Mall-/Shop-Übersicht mit Cache, Request-Abstand und `searchmall.php`-Fallback.
- Kategorien für `HighPrice`, `SellsWell`, `Overpriced`, `Underpriced` und `Illiquid`, jeweils mit Score und Begründung.
- Täglicher Adventure-Planer für 100 Knucklebones, Skeleton of Crimbos Past und Lucky Surprise Eggs. Pirate Encryption Keys bleiben als Crimbo-Reserve markiert; Foreign Bribe Bags werden zur DLC-Recherche vorgemerkt.
- KoL-Routen und `AutomationPolicy` als Adaptergrenze. Käufe, Verkäufe und Transfers bleiben Dry-Run/Approval-geschützt.

## Start

```bash
dotnet run --project KoLibri.Desktop/KoLibri.Desktop.csproj
```

Bauen:

```bash
dotnet build KoLibri.slnx
```

Offline smoke harness (no KoL account, credentials or network):

```bash
dotnet run --project tools/KolLibri.OfflineDemo/KolLibri.OfflineDemo.csproj
```

The harness checks the KoL challenge response, encrypted account session
round-trips, fixture-based mall parsing/cache behavior, pricing recommendations,
inventory categories, and the guarded shop-price mutation flow using only local
fixtures. It exits non-zero if a check fails.

## CLI für KI-Workflows

Die CLI gibt JSON aus und nimmt Passwörter niemals als Argument. Die Account-,
Status-, Inventar- und Mall-Befehle sind read-only; die separate Preisänderung
erfordert `--commit` und prüft den erwarteten alten Preis. Interaktiv:

```bash
dotnet run --project tools/KolLibri.Cli/KolLibri.Cli.csproj -- login --role mallbot --username DEIN_NAME
```

Für einen automatisierten Aufruf kann das Passwort über stdin kommen:

```bash
printf '%s' "$KOL_PASSWORD" | dotnet run --project tools/KolLibri.Cli/KolLibri.Cli.csproj -- login --role mallbot --username DEIN_NAME
dotnet run --project tools/KolLibri.Cli/KolLibri.Cli.csproj -- shop --role mallbot
dotnet run --project tools/KolLibri.Cli/KolLibri.Cli.csproj -- crm --role mallbot --refresh
dotnet run --project tools/KolLibri.Cli/KolLibri.Cli.csproj -- categorize --role mallbot --limit 20
dotnet run --project tools/KolLibri.Cli/KolLibri.Cli.csproj -- reprice --role mallbot --filter tiny-and-high --refresh
dotnet run --project tools/KolLibri.Cli/KolLibri.Cli.csproj -- snatch-plan \
  --role mallbot --item 2880 --item 8691 --workers 8 --limit 20 \
  --budget 2400000 --reserve 1000000 --min-margin 0.10 --min-profit 1000
dotnet run --project tools/KolLibri.Cli/KolLibri.Cli.csproj -- compare --role mallbot --item 2880 --refresh
dotnet run --project tools/KolLibri.Cli/KolLibri.Cli.csproj -- bribe-rank --role mallbot --item 8691 --refresh
dotnet run --project tools/KolLibri.Cli/KolLibri.Cli.csproj -- bribe-plan --role mallbot --quantity 40 --reserve 1000000 --refresh
dotnet run --project tools/KolLibri.Cli/KolLibri.Cli.csproj -- workflow-plan --refresh
# deterministische Checkliste ohne Netzwerk (z. B. bei abgelaufener Session)
dotnet run --project tools/KolLibri.Cli/KolLibri.Cli.csproj -- workflow-plan --offline \
  --main-ready --mallbot-ready --meat 2409081 --bag-price 27000 --bag-stock 463
dotnet run --project tools/KolLibri.Cli/KolLibri.Cli.csproj -- adventure-plan --role main --target 100 --yield 0.5 --skeleton-available --refresh
dotnet run --project tools/KolLibri.Cli/KolLibri.Cli.csproj -- apply-prices \
  --role mallbot --set 2880:96969:1199
```

Die CLI hat bewusst keine Kauf-, Verkaufs- oder Transferbefehle.
`adventure-plan` liest Status und Inventar und erstellt nur eine Vorschau für das
100-Knucklebones-Ziel. Kämpfe und Choices werden noch nicht an KoL gesendet.
`reprice` erstellt nur einen JSON-Vorschlag aus aktuellem Shoppreis, niedrigstem
Mallpreis und einem leichten Unterbietungspreis; es ändert keine Listings.
`compare` ergänzt die Mall-Referenz um Pricegun-Minimum, Median, gewichteten
Durchschnitt, letzten Verkauf sowie `historicalHigher` und `buyCandidate`.
`snatch-plan` prüft eine begrenzte Watchlist (ohne Watchlist die eigenen
Shop-Items) gegen gecachte Mall- und Pricegun-Daten. Es berücksichtigt Reserve,
Budget, Mindestmarge, Mindestgewinn, Blacklist und bis zu 20 Worker und gibt nur
konkrete Underprice-Vorschläge aus. Der Befehl kauft nichts.
`apply-prices` ist standardmäßig eine Vorschau. Erst mit dem zusätzlichen
Flag `--commit` wird ein explizit mit dem erwarteten aktuellen Preis gebundener
Preis-POST gesendet; danach liest KoLibri den Shop erneut und verifiziert jeden
geänderten Preis. Bei einem Preis-Konflikt wird nichts gesendet.
Die Foreign-Bribe- und One-Day-Ticket-Recherche steht in
[`docs/ForeignBribeResearch.md`](docs/ForeignBribeResearch.md).

## Architektur

`src/KolArbitrage.Core` enthält die testbare Domäne. Der KoL-Login spricht ausschließlich die offizielle HTTPS-Seite an und gibt Session-Cookies an den read-only Mall-Client weiter. Preisabfragen laufen über `IPriceProvider`; Mall-/Shop-Daten werden gecacht. Alle Adapter verwenden Item-/Store-IDs, respektieren Rate-Limits und prüfen vor jedem späteren mutierenden Schritt den aktuellen Kontostand beziehungsweise die aktuelle Preisstufe.

Die vorhandenen KoLmafia-Skripte unter dem lokalen Benutzerprofil waren dabei fachliche Referenz für die Guardrails: Dry-Run, Ausgabenlimits, kleine Kauf-Chunks, erneute Preisprüfung und Relisting. Es wurden keine Zugangsdaten aus diesem Profil übernommen.

## Recherchegrundlage

- [KoLmafia-Projektübersicht](https://kolmafia.github.io/kolmafia.sourceforge.net/) – Funktionsumfang und Java-Desktop-Kontext.
- [KoLmafia GitHub](https://github.com/kolmafia/kolmafia) – aktuelle Struktur und API-Request-Muster.
- [Loathers Tools / Pricegun](https://www.loathers.net/tools/) – zeitgewichtete Preisübersicht als externe Referenzquelle.

## Nächste sinnvolle Schritte

1. Itemnamen und Inventar-IDs aus der Status-/Inventar-API in die Shopübersicht übernehmen.
2. Kategorien und Cache-Alter als echte sortierbare Tabellen anzeigen.
3. Transfer-Vorschauen zwischen Main und Mallbot ergänzen.
4. Erst danach: begrenzte Käufe/Relistings mit Bestätigung, Tagesbudget und Audit-Log.

## MCP-Anbindung

Eine MCP-freundliche Tool-Spezifikation für Status-, Inventar- und Preisabfragen sowie Approval-geschützte Vorschauen liegt in [`docs/MCP.md`](docs/MCP.md). Die maschinenlesbaren Eingabeschemas stehen in [`docs/mcp-tools.schema.json`](docs/mcp-tools.schema.json). Mutierende Aktionen folgen einem Preview-/Commit-Protokoll mit erneuter Status- und Policy-Prüfung; die vorhandenen Core-Adapter bleiben read-only.
