# Combat-Makros: Live-Check (Coordinator)

> **Wird vom Coordinator live ausgeführt**, nach dem Merge in `release/0.5.0`. Worker senden keine Makros.
> Quelle der Fälle: `planning/0.5-p2-adventure.md` §3. Ziel: echte KoL-Antworten auf `fight.php?action=macro` aufnehmen,
> die synthetischen Fixtures in `tests/KoLibri.Tests/Fixtures/macro/` ersetzen und danach entscheiden, ob KOLIBRI0001 fällt.

## Was die Library schickt

`POST fight.php` mit `action=macro`, `macrotext=<Makro>`, `pwd=<Hash>`, so wie KoLmafia es macht:
`FightRequest.handleMacroAction` setzt `action=macro` und `macrotext`, `GenericRequest.getDataString` hängt `pwd` an.
Den Makrotext verdichtet KoLibri wie KoLmafia (`MACRO_COMPACT_PATTERN`: Kommentare und doppelte Trenner weg).
Makros laufen nur mit `KolibriOptions.EnableExperimentalMacros = true`. Ohne die Option gilt weiter `NotSupportedException` (KOLIBRI0001).

Die Test-App `samples/macro-live-check` setzt die Option, führt **einen** Kampf mit **einem** Makro und schreibt nur mit `--yes`.
Ohne `--yes` liest sie nur Status und offenen Kampf und zeigt den Makrotext, den sie senden würde.

## Vorbereitung

1. Branch `release/0.5.0` mit diesem Merge auschecken und bauen:

   ```bash
   dotnet build samples/macro-live-check
   ```

2. Main ist mit der CLI angemeldet (die App nutzt dieselbe gespeicherte Sitzung, Konto `main`):

   ```bash
   kolibri login status
   ```

3. Im Browser: **Auto-Attack aus** (sonst verfälscht Runde 1), HP voll, mindestens 15 Adventures, MP mindestens 20, kein offener Kampf und keine offene Choice.
   Prüfen:

   ```bash
   kolibri choice pending
   ```

4. Einen billigen Kampfskill (MP-Kosten höchstens 5) notieren, Name für M4, so wie er im Skill-Dropdown der Kampfseite steht (ohne „(N mp)“).
5. Zone: The Haunted Pantry (`--location 113`). Ersatz: Skeleton Store (`--location 439`).
6. Ordner für die Seiten: `tests/KoLibri.Tests/Fixtures/macro/` (die App überschreibt die synthetischen Dateien dort).

Trockenlauf (sendet nichts, zeigt Status und Plan):

```bash
dotnet run --project samples/macro-live-check -- --case M1 --location 113
```

## Fälle

Jeder Fall ist ein frischer Kampf. Die App startet ihn mit `--location 113` selbst (ein Adventure), wenn keiner offen ist.
Bricht sie wegen einer Choice, eines Turtle-Monsters oder HP unter 30 % ab, ist nichts gesendet worden: im Browser erledigen und den Fall neu starten.

| Fall | Befehl | Erwartung |
|---|---|---|
| M1 | `dotnet run --project samples/macro-live-check -- --case M1 --location 113 --save tests/KoLibri.Tests/Fixtures/macro --yes` | genau eine Runde, `outcome=Ongoing` oder `Won`, `roundsInResponse=1` |
| M2 | `dotnet run --project samples/macro-live-check -- --case M2 --location 113 --save tests/KoLibri.Tests/Fixtures/macro --yes` | mehrere Runden in einer Antwort, `outcome=Won`, Beute und Meat auf derselben Seite |
| M3 | `dotnet run --project samples/macro-live-check -- --case M3 --location 113 --save tests/KoLibri.Tests/Fixtures/macro --yes` | `macro error: KoL aborted the macro…`, Kampf bleibt offen (`open encounter after: fight`) |
| M4 | `dotnet run --project samples/macro-live-check -- --case M4 --skill "<Skillname>" --location 113 --save tests/KoLibri.Tests/Fixtures/macro --yes` | Skill-Runde plus Angriffe, MP sinkt (`status after`) |
| M5 | `dotnet run --project samples/macro-live-check -- --case M5 --location 113 --save tests/KoLibri.Tests/Fixtures/macro --yes` | `outcome=RanAway` |
| M6 | `dotnet run --project samples/macro-live-check -- --case M6 --location 113 --save tests/KoLibri.Tests/Fixtures/macro --yes` | `outcome=Unknown`, `macro error: KoL rejected the macro as invalid…`, Kampf unverändert offen |
| M7 (optional) | M2-Befehl mehrmals, `--save` mit eigenem Ordner, bis nach einem Sieg eine Choice oder ein Folgekampf kommt; diese Seite als `macro-m7-followup.html` ablegen | Seite nach Sieg zeigt Choice/Folgekampf; `open encounter after` nennt sie |

Nach M3 und M6 ist der Kampf noch offen: im Browser zu Ende kämpfen, bevor der nächste Fall startet.

Die App gibt nach jedem Fall `status before`/`status after` (HP, MP, Adventures aus `api.php?what=status`) und die offene Begegnung aus.
Diese Ausgabe pro Fall in die Notizen kopieren.

**Abbruch:** HP unter 30 % oder unklare Seite: im Browser beenden, Fall als „offen“ notieren.

## Nachbereitung

1. Gespeicherte Seiten prüfen. Die App ersetzt den `pwd`-Hash durch `PWDHASH` und den Spielernamen durch `PLAYER`.
   Session-Ids, Clan, Chat-Reste von Hand entfernen. Die erste Zeile `<!-- SYNTHETIC … -->` gibt es in echten Seiten nicht.
2. Offline-Tests gegen die echten Seiten laufen lassen (seriell):

   ```bash
   dotnet test tests/KoLibri.Tests --filter "FullyQualifiedName~CombatMacroTests" -m:1
   ```

   Die Tests prüfen Struktur (Outcome, Rundenzahl, Fehlermeldung), wenige feste Zahlen (Meat, Beute, Rundennummer)
   stammen aus den synthetischen Seiten. Weichen die echten Seiten ab, die Zahlen im Test anpassen, nicht den Parser raten lassen.
3. Notieren und im PR festhalten:
   - Ob `pwd` nötig ist (KoLibri schickt es immer mit, wie KoLmafia).
   - Was `var onturn = N` auf der Antwortseite bedeutet (KoLibri nimmt an: Runde, in der das Makro begann; KoLmafia-Fixture `test_fight_lose.html` zeigt `onturn = 1` nach 19 Aktionen).
   - Wie Abbruch (`Macro Aborted: …`) und Fehler (`Invalid Macro`) genau aussehen.
   - Ob die Kampfseite Spieler-HP zeigt (bisher nein: KoLibri liest nach einem Makro einmal `api.php?what=status`).
4. Erst danach (Brief W6, §3 „Code-Pfade“ Punkte 1, 6, 8): `[Experimental]` entfernen, CLI-Flags `--macro`/`--macro-file`, Parser im Core.

## Was der Code heute erkennt (offline geprüft, `CombatMacroTests`)

| Signal in der Antwort | Ergebnis |
|---|---|
| je ein `<!-- macroaction: … -->` pro ausgeführter Aktion | `FightRound.RoundsInResponse` |
| `<!--WINWINWIN-->` | `Won`, Beute und Meat von derselben Seite |
| `<!--LOSELOSELOSE-->` | `Lost` |
| „You run away“ ohne Kampfformular | `RanAway` |
| „Macro Abort…“ / „Could not match item(s) for use“, Kampf offen | `Ongoing` mit `MacroError`; `FightToEndAsync` schickt dasselbe Makro nicht noch einmal (endet mit `Stopped`), eine andere Aktion der Strategie schon |
| „Invalid Macro“ | `Unknown` mit `MacroError`, kein zweiter Request |

Die Texte stammen aus KoLmafia (`FightRequest.containsMacroError`, `MACRO_PATTERN`, `updateRoundData`) und den echten KoL-Seiten in KoLmafias Testordner `src/test/root/request`.
