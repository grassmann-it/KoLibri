#!/usr/bin/env python3
"""Translates the KoLibri desktop shell to English.

English is the base language of the interface; a localisation layer comes
later. This script rewrites the user-visible strings in the AXAML and in
the code-behind so the window reads like a KoL client rather than a
half-translated one.

Only user-visible strings are touched — identifiers, section keys used for
navigation and the logic stay as they are, because the navigation switch
matches on the German section names.
"""

import re
import sys
from pathlib import Path

AXAML = Path("KoLibri.Desktop/MainWindow.axaml")
CS = Path("KoLibri.Desktop/MainWindow.axaml.cs")

# AXAML: attribute values, keyed by the exact source text.
AXAML_REPLACEMENTS = {
    "Content=\"Guts\"": "Content=\"Guts\"",
    "Content=\"Inventar\"": "Content=\"Inventory\"",
    "Content=\"Täglich\"": "Content=\"Daily\"",
    "Content=\"Pricing\"": "Content=\"Pricing\"",
    "Content=\"Mall\"": "Content=\"Mall\"",
    "Content=\"Abenteuer\"": "Content=\"Adventures\"",
    "Content=\"Combat\"": "Content=\"Combat\"",
    "Text=\"Konto\"": "Text=\"Account\"",
    "Text=\"Guten Morgen\"": "Text=\"Good morning\"",
    "Text=\"Dein Arbitrage-Überblick für heute.\"": "Text=\"Your arbitrage overview for today.\"",
    "Text=\"Tägliches Briefing\"": "Text=\"Daily briefing\"",
    "Text=\"Bereich\"": "Text=\"Area\"",
    "Text=\"Aktion\"": "Text=\"Action\"",
    "Text=\"Details\"": "Text=\"Details\"",
    "Text=\"Status\"": "Text=\"Status\"",
    "Text=\"Ausführung\"": "Text=\"Execution\"",
    "Text=\"Inventar\"": "Text=\"Inventory\"",
    "Text=\"Keine Einträge.\"": "Text=\"No entries.\"",
    "Text=\"Suchen (Name oder ID) …\"": "Text=\"Search (name or id) …\"",
    "Text=\"Items\"": "Text=\"Items\"",
    "Text=\"Kein Item gewählt\"": "Text=\"No item selected\"",
    "Text=\"Keine Schritte für heute.\"": "Text=\"No steps for today.\"",
    "Text=\"Noch nichts ausgeführt.\"": "Text=\"Nothing executed yet.\"",
    "Text=\"—\"": "Text=\"—\"",
    "Text=\"Vorschau öffnen\"": "Text=\"Open preview\"",
    "Text=\"Kontext aktualisieren\"": "Text=\"Refresh context\"",
    "Text=\"(noch keine Adventure-Sitzung)\"": "Text=\"(no adventure session yet)\"",
}

# Code-behind: user-visible message strings.
CS_REPLACEMENTS = {
    '"Bereich „{section}“ ausgewählt."': '"Section “{section}” selected."',
    '"Inventar"': '"Inventory"',
    '"Täglich"': '"Daily"',
    '"Übersicht"': '"Guts"',
    '"Konten & Transfer"': '"Accounts & Transfer"',
    '"Mall & Shop"': '"Mall & Shop"',
    '"Einstellungen"': '"Settings"',
    '"Live-Inventar mit Suche, Kategorien und Item-Detail."':
        '"Live inventory with search, categories and item detail."',
    '"Was der Bot heute macht — Campfire, Hermit, Free-Pulls und Arbitrage."':
        '"What the bot does today — campfire, hermit, free pulls and arbitrage."',
    '"Shopzeile / Preisvergleich"': '"Shop row / price comparison"',
    '"Encounter / Tagesplan"': '"Encounter / daily plan"',
    '"Fight-Observation / Policy"': '"Fight observation / policy"',
    '"Konto / Rollenstatus"': '"Account / role status"',
    '"Arbeitsbereich"': '"Work area"',
    '"Wähle ein Listing für Detailpreis, historische Spanne und Reprice-Vorschau."':
        '"Pick a listing for detail price, historical range and reprice preview."',
    '"Wähle ein Encounter für Status, Allowlist und bounded Preview."':
        '"Pick an encounter for status, allowlist and bounded preview."',
    '"Wähle eine legale Action für manuelle oder AutoCombat-Preview."':
        '"Pick a legal action for a manual or auto-combat preview."',
    '"Wähle ein Objekt, um sichere Kontextaktionen zu sehen."':
        '"Pick an object to see the safe context actions."',
    '"Tastatur: Ctrl+1..7 Bereiche · F5 Aktualisieren · Shift+F10 Kontextvorschau · Ctrl+Esc Abbrechen · F1 Hilfe."':
        '"Keyboard: Ctrl+1..7 sections · F5 refresh · Shift+F10 context preview · Ctrl+Esc cancel · F1 help."',
    '"Kein Approval-Token — es wurde nichts gesendet."':
        '"No approval token — nothing was sent."',
    '"Keine Session für das gewählte Konto."':
        '"No session for the selected account."',
    '"Tagesroutine ausgeführt. Details stehen im Audit-Log."':
        '"Daily routine executed. Details are in the audit log."',
    '"Tagesroutine konnte nicht vollständig ausgeführt werden."':
        '"The daily routine could not be completed."',
    '"Inventar laden"': '"Loading inventory"',
    '"Tagesplan laden"': '"Loading daily plan"',
    '"Tagesroutine"': '"Daily routine"',
    '"Mall-Aktualisierung"': '"Refreshing mall"',
    '"Konten laden"': '"Loading accounts"',
    '"Adventure-Plan laden"': '"Loading adventure plan"',
    '"Aktualisierung"': '"Refreshing"',
    '"Konto: {role}"': '"Account: {role}"',
    '"Ctrl+1..7 wechseln den Bereich. F5 aktualisiert. Shift+F10 öffnet die sichere Kontextvorschau. Ctrl+Esc bricht laufende lokale Reads ab."':
        '"Ctrl+1..7 switches the section. F5 refreshes. Shift+F10 opens the safe context preview. Ctrl+Esc cancels running reads."',
    '"Preview erzeugt: aktuelle Daten und erlaubte nächste Schritte werden angezeigt; Commit bleibt separat freizugeben."':
        '"Preview created: current data and allowed next steps are shown; the commit stays separately gated."',
    '"Der Bereichs-Refresh lädt den gemeinsamen Cache. Das Kontextpanel bleibt read-only."':
        '"The section refresh reloads the shared cache. The context panel stays read-only."',
    '"Keine Observation geladen. Preview bleibt blockiert, bis Monster, Runde und legale Actions frisch gelesen wurden."':
        '"No observation loaded. The preview stays blocked until monster, round and legal actions were read fresh."',
    '"Theme „{themeName}“ aktiviert."': '"Theme “{themeName}” activated."',
    '"Konten"': '"Accounts"',
    '"Übersicht"': '"Guts"',
}


def replace_in_file(path: Path, replacements: dict[str, str]) -> int:
    text = path.read_text(encoding="utf-8")
    count = 0
    for source, target in replacements.items():
        if source == target:
            continue
        occurrences = text.count(source)
        if occurrences:
            text = text.replace(source, target)
            count += occurrences
    path.write_text(text, encoding="utf-8")
    return count


def main() -> int:
    axaml_count = replace_in_file(AXAML, AXAML_REPLACEMENTS)
    cs_count = replace_in_file(CS, CS_REPLACEMENTS)
    print(f"{AXAML}: {axaml_count} replacement(s)")
    print(f"{CS}: {cs_count} replacement(s)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
