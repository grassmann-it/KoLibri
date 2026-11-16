# Rotierende Ein-Stück-Angebote

KoL hat im Mall keinen nativen Kauf-Order-Bestand. Der Worker kann deshalb
zunächst nur eine **einzelne Verkaufs-Chance** planen: ein Stück eines
vorhandenen Items wird für einen kurzen Zeitraum unter den sichtbaren Markt-
Referenzen angeboten. Ein echtes Kaufangebot bleibt eine spätere, separate
Adapter-Funktion.

`RotatingSalePlanner` arbeitet ausschließlich mit Snapshots. Pro Lauf wird
standardmäßig höchstens ein Vorschlag erzeugt. Der Vorschlag wird verworfen,
wenn die Mall-/Pricegun-Daten stale sind, das eigene Geschäft den Mallboden
verunreinigt, kein Bestand vorhanden ist, der Cooldown noch läuft oder die
Mindestmarge nicht erreichbar ist. Der Preis ist an den zuletzt beobachteten
Shoppreis gebunden und kann dadurch nur über den bestehenden
`apply-prices --commit`-Pfad nach frischem Read verändert werden.

Empfohlener Worker-Zyklus:

1. Shop, Inventar, Mall und Pricegun aus dem Cache lesen; nur bei abgelaufenem
   TTL nachladen.
2. Kandidaten mit unabhängiger zweiter Mall-Listung oder Pricegun-Median
   bilden; eigene Mall-Listings nicht als Referenz verwenden.
3. Einen Kandidaten mit dem besten erwarteten Bruttospread auswählen und als
   `ApprovalRequired` anzeigen.
4. Nach Cooldown erneut prüfen, ob der Artikel verkauft wurde und der Markt
   noch passt. Erst dann darf ein späterer Commit-Adapter relisten.

Der aktuelle Planner listet noch keine Ware und kauft nichts. Für echte
Ein-Stück-Listings brauchen wir zusätzlich einen Adapter, der Bestand in eine
separate Listing-Zeile aufteilen kann; ein Preis-Update an einer bestehenden
Shop-Zeile würde sonst alle Stücke dieser Zeile betreffen.
