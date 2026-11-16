# KoLibri-Arbitrage-Arbeitspakete

Dieses Dokument trennt sichere Beobachtung und Bewertung von späteren Käufen,
Transfers und Mall-Änderungen. Jede Mutation braucht einen frischen Read,
Budgetprüfung, Approval, Commit und Verifikation.

## Bewertungsgrundlage

Für jedes Item werden Item-ID, Tradeability, Giftability, Autosell-/NPC-Wert,
Coinmaster-/Rezeptbeziehungen, Ausrüstungstyp, IotM-/Saisonstatus, Bestand,
Umschlag und Quellenalter gespeichert. Der erwartete Nettoerlös ist:

```text
netSell = expectedSellPrice * 0.99 - listingFees - packagingCost
netSpread = netSell - acquisitionCost - transferCost - adventureCost
```

`expectedSellPrice` darf nicht einfach der erste Mall-Eintrag sein. KoLibri
vergleicht einen robusten Mall-Schätzer (Top-k, zweiter Preis, Mengen und
eigene Listings ausgeschlossen) mit Pricegun-Median/gewichteten Verkäufen und
historischem Preis. Eine Empfehlung braucht Mindestbetrag, Mindestmarge,
Liquidität und eine nicht veraltete Quelle.

## Priorität P0: Daten und Scanner

- **Item-Katalog:** Tradeable/giftable/bound, NPC, Autosell, Rezept, Fold-/Zap-
  Gruppe, Coinmaster, Ausrüstungsslot, IotM und saisonale Tags.
- **Mall-Snapshot:** Top 10 Listings, Mengen, Verkäufer, Preisalter und
  `ownStoreContaminatesMarket`; die eigene Mall darf keine Kaufreferenz sein.
- **Pricegun/Historie:** lange TTL für Übersicht, kurzer TTL für einen
  Kaufentscheid; stale Fallback klar markieren.
- **Farm-vs-Buy:** gleicher Zielbestand wird als Eigenfarm, Mallkauf, Crafting
  oder Coinmaster-Ausgabe simuliert.

## Priorität P1: Underprice-Sniper

Der Worker scannt eine Kandidatenliste statt permanent den kompletten Mall zu
abzufragen. Kandidaten entstehen aus eigenen Zielitems, historischen Gewinnern,
Farm-/Coinmaster-Outputs, IotMs/Accessories und frischen Mall-Anomalien.

Ein Kaufvorschlag braucht:

1. unabhängige Referenz (Pricegun oder zweiter/robuster Mallpreis),
2. mindestens `minAbsoluteSpread` und `minMarginPercent`,
3. ausreichende Verkaufsmengen oder ein bewusst akzeptiertes Haltefenster,
4. Tradeability und keine Account-Bindung,
5. Tagesbudget, Item-Limit und Reserve für Main/Mallbot.

Vor dem Kauf werden Storepreis, Menge, Meat und Session erneut gelesen. Der
Commit kauft höchstens einen kleinen Chunk und verifiziert danach Inventar und
Meat. `priceChanged`, `soldOut`, `loginRedirect` und ein zu alter Snapshot
brechen den Vorgang ab.

## Priorität P1: IotM- und Accessory-Arbitrage

IotM-Items werden in drei Klassen geteilt: handelbare Utility-Items mit
laufendem Ertrag, reine Ausrüstungs-/Sammlerobjekte und Verbrauchsitems. Für
jede Klasse werden Mall-Wert, erwarteter Nutzen, Ersatzkosten, Haltedauer und
Wiederverkaufsrisiko getrennt gerechnet. Mr. Accessory ist nur ein
Numeraire/Opportunitätskostenwert; ein Preis unter einem Mr. A ist kein
automatischer Kaufgrund.

Nicht kaufen, wenn das Item account-bound, nicht giftable, nur ein einzelner
historischer Ausreißer oder ohne liquide Verkäufe ist. Bei Utility-IotMs wird
`resale + retained utility` gegen das gebundene Meat verglichen.

## Priorität P1: Farm- und Adventure-Arbitrage

Für jede Zone wird `expectedDropValue / adventure` aus aktuellen Drops,
Pricegun/Mall und Verkaufsgeschwindigkeit berechnet. Davon gehen Ticket,
Verbrauchsmaterial, Turn-Opportunity-Cost und Zielressourcen ab. Das Ergebnis
wird mit einem direkten Mallkauf verglichen.

Die ersten Kandidaten sind Skeleton/Knucklebones, Glaciest-Bücher und Tickets,
Volcano-Bücher/Tickets, Coinspiracy-Outputs, Pirate-Encryption-/Crimbo-
Materialien, Elf-Guard-/Crimbuccaneer-Quellen und Hermit-Clovers. Daily limits,
Semirares, Lucky Eggs und saisonale Fenster werden als eigene Ressourcen
geführt; der Adventure-Runner stoppt bei unbekanntem Fight/Choice, stale Session,
zu wenig HP/Turns oder erreichtem Ziel.

## Priorität P1: Coinmaster- und Event-Lager

Alle `coinmasters.txt`-/`shoprows.txt`-Zeilen werden als Graph gespeichert:
Currency → Ticket/Item → erwarteter Nettoerlös. Currency bekommt einen
Opportunity-Cost aus der besten alternativen Ausgabe. Käufe sind nur sinnvoll,
wenn der Nettoerlös nach Ticketkosten, Transfer, Mall-Fee und Halte-/Saisonrisiko
positiv ist. Crimbo-/Pirate-Bestände erhalten ein eigenes Reservekonto und
werden nicht mit laufendem Mallbestand vermischt.

## Priorität P2: Transformationen

Craft, fold, zap, pulverize, untinker und andere Rezeptpfade werden als
gerichteter Graph bewertet. Inputs werden mit Eigenbestand-Opportunity-Cost
oder Mallpreis angesetzt; hinzu kommen Meatpaste, tägliche Limits,
Fehlerrisiko und Absatzgeschwindigkeit. Glücksspielpfade brauchen eine
separate Verlustgrenze und bleiben standardmäßig Preview-only.

## Priorität P2: Shop, Überbestand und CRM

- Bestandsalter und Menge erzeugen `hold`, `reprice`, `rotate-one`, `bundle`,
  `transfer` oder `liquidate` Vorschläge.
- Der Rotationsworker stellt höchstens ein Stück aus echtem Überschuss als
  zeitlich begrenzte Promo vor; Cooldown, unabhängiger Preis und Mindestmarge
  sind Pflicht.
- Eigene Verkäufe liefern Durchsatz- und Wiederkäuferdaten. Loyalty-Punkte,
  Blockliste und spätere Message/Gift-Intents bleiben lokal und approval-
  gebunden.

## Worker- und Approval-Grenze

```text
observe -> cache -> score -> preview -> refresh -> approve -> commit -> verify
```

Workers dürfen lesen, cachen und Vorschläge erzeugen. Kaufen, Transfer,
Bag-Öffnen, Abenteuer, Geschenk, Nachricht und Preisänderung bleiben getrennte
Capabilities mit Audit-ID. Ein Sniper darf nie aus einem stale Cache oder aus
seinem eigenen Mall-Listing kaufen.

KoLmafia-Referenzen: [CLI-/Acquire-Dokumentation](https://github.com/kolmafia/kolmafia.sourceforge.net/blob/main/scripting.html),
[InventoryManager](https://github.com/kolmafia/kolmafia/blob/main/src/main/java/net/sourceforge/kolmafia/session/InventoryManager.java),
[MallPurchaseRequest](https://github.com/kolmafia/kolmafia/blob/main/src/main/java/net/sourceforge/kolmafia/request/MallPurchaseRequest.java),
[ManageStoreRequest](https://github.com/kolmafia/kolmafia/blob/main/src/main/java/net/sourceforge/kolmafia/request/ManageStoreRequest.java),
[Pricegun](https://pricegun.loathers.net/) und [Tradeability-Funktionen](https://wiki.kolmafia.us/index.php?title=Is_tradeable).
