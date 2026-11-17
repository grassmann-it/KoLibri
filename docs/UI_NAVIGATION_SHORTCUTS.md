# KoLibri Desktop: Navigation, Shortcuts und Kontextaktionen

KoLibri übernimmt aus MeowChan/oChan die Trennung zwischen Arbeitsbereich,
Befehl und Darstellung. Die Sidebar beziehungsweise die kompakte obere Leiste
wechselt nur den Arbeitsbereich; dieselben Befehle können später aus dem
App-Menü, der Tastaturhilfe oder einem Kontextmenü ausgelöst werden.

Der aktuelle Shell-Vertrag stellt dafür `ShellPageContract`,
`ShellShortcutContract` und `ShellContextActionContract` bereit. Die Defaults
sind bewusst klein:

| Befehl | Tastenkürzel | Zweck |
|---|---|---|
| Übersicht, Arbitrage, Shop, Adventure, Combat | `Ctrl+1` … `Ctrl+5` | Arbeitsbereich wechseln |
| Aktualisieren | `F5` | aktuellen read-only Snapshot laden |
| Tastaturhilfe | `F1` | alle verfügbaren Bindings anzeigen |
| Lauf stoppen | `Escape` | lokalen Adventure-/Combat-Lauf abbrechen |

Kontextaktionen hängen vom ausgewählten Objekt ab. Für Adventures gehören
Encounter inspizieren, Encounter aktualisieren und Lauf stoppen dazu. Für den
Kampf gibt es eine manuelle Aktionsvorschau und Stoppen. Shopzeilen bieten
Detailpreis und Reprice-Vorschau. Eine Aktion mit `RequiresApproval` darf erst
nach dem bestehenden Preview → Approval → Commit-Vertrag mutieren.

Die Darstellung kann damit später zwischen Sidebar, Hamburger-/App-Menü und
Kontextmenü wechseln, ohne Befehle doppelt zu implementieren. Eingabefelder,
Dialoge und offene Overlays behalten ihre normalen Tasten; globale Bindings
werden nur ausgeführt, wenn der aktive Bereich sie nicht konsumiert.

Auf breiten Fenstern ergänzt eine ausblendbare rechte Kontextleiste die
Sidebar. Sie zeigt den Typ des aktiven Arbeitsbereichs und sichere
Vorschau-/Refresh-Aktionen für eine ausgewählte Mall-Zeile, ein Encounter oder
eine Combat-Observation. Unterhalb der Desktop-Breite verschwindet sie; die
gleichen Aktionen bleiben über die kompakte obere Navigation und Kontextmenüs
erreichbar.

Die Avalonia-Shell routet diese Kernbelegung jetzt direkt am Fenster:
`F1`, `F5`, `Ctrl+Escape`, `Shift+F10` und `Ctrl+1..7`. Textfelder behalten
ihre normale Eingabe; die globalen Gesten werden nur für die ausdrücklich
definierten Kombinationen konsumiert.

## Nächste UI-Arbeitspakete

1. Einen zentralen Shortcut-Router mit konfigurierbaren Profilen und
   Konfliktprüfung anbinden.
2. F1 als Tastaturhilfe-Fenster mit Suche und Bereichsfilter ausgeben.
3. Avalonia-Kontextmenüs für Shopzeilen, Adventure-Encounter und Combat-Actions
   an die Shell-Verträge binden.
4. Globales Stoppen an den gemeinsamen Cancellation-/Checkpoint-Store hängen;
   ein Stop darf keinen neuen KoL-POST auslösen.

MeowChan/oChan bleibt dabei UX-Referenz für Navigation, Fokus und
Kontextaktionen. KoLibri übernimmt keine Bildboard- oder Downloadlogik.
