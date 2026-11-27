# KoLibri Cookbook

Kleine, kopierbare Apps, die nur die öffentliche API der Bibliothek `KoLibri` nutzen. Die Bibliothek liefert die
Bausteine, die Tools schreibst du selbst.

| App | Was sie zeigt | Schreibt? |
|---|---|---|
| `Status` | Name, Level, Meat, Adventures, HP/MP, Organe, Effekte, optional Inventar-Top-10 (`--inventory`) | nein |
| `FarmLoop` | eigene `ICombatStrategy` (`CautiousStrategy`), `RunRequest` mit `StopWhen` und `ChoiceResolvers.Safe()` | mit `--yes` (Default 5 Turns, hart max. 100) |
| `MallBuy` | `PurchaseOrder` mit Preisobergrenze und `MeatReserve`: erst `QuoteAsync`, dann `BuyAsync` | mit `--yes` |
| `KmailBot` | Inbox lesen, auf `!ping` mit "pong" antworten, verarbeitete Ids in eigener JSON-Datei merken | mit `--yes` |
| `Events` | Request-, Encounter- und ItemAcquired-Events der Sitzung loggen | nein |

Ohne `--yes` ist jede App ein **Trockenlauf**: sie zeigt, was sie tun würde.

## Aufbau jeder App

- `<Name>Recipe.cs` enthält die Logik: `Cookbook.<Name>.Recipe.RunAsync(KolibriSession, <Name>Options, TextWriter, CancellationToken)`.
  Das braucht keine Konsole und lässt sich gegen einen Fake testen.
- `Program.cs` ist dünn: Argumente lesen, `KolibriClient.Create()`, `OpenAsync` oder `LoginAsync`, `Recipe.RunAsync`.

## Starten

Beim ersten Mal einloggen (Passwort aus `KOLIBRI_PASSWORD` oder per Prompt, nie im Code), danach reicht das Konto:

```bash
export KOLIBRI_PASSWORD=...   # nur beim ersten Login
dotnet run --project samples/cookbook/Status -- --user MeinChar --account main
dotnet run --project samples/cookbook/Status -- --account main --inventory
dotnet run --project samples/cookbook/FarmLoop -- --location 113 --turns 5            # Trockenlauf
dotnet run --project samples/cookbook/MallBuy -- --item 2 --qty 3 --max-price 500      # nur Angebot
```

## Bauen gegen NuGet oder einen lokalen Feed

Die Projekte referenzieren `KoLibri` über `$(KoLibriPackageVersion)` (Default `0.5.0-dev`, siehe `samples/Directory.Build.props`).

```bash
# gegen nuget.org (sobald veröffentlicht)
dotnet build samples/cookbook/Status -p:KoLibriPackageVersion=0.5.0

# gegen ein lokal gepacktes Paket
dotnet pack src/KoLibri -o /tmp/feed
dotnet build samples/cookbook/Status --source /tmp/feed --source https://api.nuget.org/v3/index.json
```

Zum Kopieren: Ordner einer App plus `samples/Directory.Build.props` (oder dessen Inhalt in die eigene csproj) übernehmen.
