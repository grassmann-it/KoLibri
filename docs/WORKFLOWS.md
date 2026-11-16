# KoLibri-Arbeitsabläufe

`KoLWorkflowPlanner` beschreibt den täglichen Dry-Run-Ablauf für Main und
Mallbot. Jeder mutierende Schritt trägt den Status `ApprovalRequired`; vor dem
Commit muss der Adapter den Account- und Preiszustand erneut lesen.

Der Ablauf ist absichtlich in Phasen getrennt:

1. Sessions, Meat, Adventures und Inventar von Main und Mallbot aktualisieren.
2. Eigenen Shop, Verkäufe und Pricegun/Mall-Referenzen aktualisieren.
3. Foreign-Bribe-Pilotcharge auf dem Mallbot planen; Bags ungeöffnet halten.
4. Ungeöffnete Bags zum Main verschieben und dort die Currency-Ausbeute messen.
5. Handelbare Coinmaster-Ausgaben sowie One-Day-Tickets nach Netto-Marge ranken.
6. Gekaufte Ware zum Mallbot verschieben und mit Preisleiter/History relisten.
7. Tägliches Skeleton-/Knucklebones-Ziel ausführen, danach Hermit- und
   One-Day-Ticket-Chancen prüfen.
8. Snapshots, Transfers, Käufe, Verkäufe und Entscheidungen auditierbar speichern.

Die ersten mutierenden Schritte bleiben getrennt: `apply-prices` kann bereits
explizit bestätigte Shoppreise setzen; Bag-Käufe, Transfers, Bag-Öffnungen,
Coinmaster-Käufe und Abenteuer benötigen eigene Approval-Adapter.

Das Modell bildet damit die Basis für `LibKolibri`: Login/HTTP, Mall, Pricegun,
Inventar, Adventure, Coinmaster und Transfer werden als austauschbare Adapter
behandelt. Die Desktop-App, CLI und ein späterer MCP-Server können denselben
Workflow-Plan verwenden.

Der Plan lässt sich auch ohne Netzwerkzugriff als Checkliste ausgeben. Das ist
für abgelaufene Sessions und Tests wichtig; es werden dabei weder Käufe noch
Transfers ausgeführt:

```bash
dotnet run --project tools/KolLibri.Cli/KolLibri.Cli.csproj -- \
  workflow-plan --offline \
  --main-ready --mallbot-ready \
  --meat 2409081 --bag-price 27000 --bag-stock 463 \
  --quantity 40 --reserve 1000000
```

Ohne `--main-ready`/`--mallbot-ready` bleiben die kontobezogenen Schritte
bewusst `Blocked`. Im normalen Modus liest `workflow-plan` den aktuellen
Mallbot-Status und den Bag-Mallboden read-only; schlägt dieser Snapshot wegen
einer abgelaufenen Session fehl, wird trotzdem ein blockierter Plan ausgegeben.
