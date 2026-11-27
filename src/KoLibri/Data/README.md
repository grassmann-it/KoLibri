# KoLibri/Data

## choices.json

Eingebettete Choice-Defaults (`KoLibri.Data.choices.json`), gelesen von `ChoiceDefaults`.
Format: Version 2 (Version 1 bleibt lesbar).

- Einträge mit `"confidence": "live"` (oder ohne `confidence`) sind live verifiziert und werden von
  Hand gepflegt (z.B. Choice 1060 in Zone 439). Sie schlagen KoLmafia immer.
- Einträge mit `"confidence": "kolmafia-default"` erzeugt `scripts/import-kolmafia-choices.py` aus
  KoLmafias `ChoiceAdventures.java` (Spoiler, Kosten) und `defaults.txt` (`choiceAdventureN`).
  Optionen tragen Flags: `costsMeat`, `costsItem`, `startsFight`, `hurts`, `skipsTurn`, `safe`.
  Der Resolver nimmt einen KoLmafia-Default nur, wenn die Option `safe: true` ist; sonst fragt er
  und nennt die Spoiler, damit man `--choice-action ID:OPT` setzen kann.

Neu erzeugen (liest die KoLmafia-Quellen nur als Text, führt nichts davon aus):

    python3 -I scripts/import-kolmafia-choices.py
    # ohne external/kolmafia: KOLIBRI_MAFIA_ROOT=<Checkout> python3 -I scripts/import-kolmafia-choices.py

Gepinnte Quelle: https://github.com/kolmafia/kolmafia.git, Commit
`95ebac8dce63f6532b4b10b339c2f38d6d2dad6f` (2026-11-19).

## Lizenz der KoLmafia-Anteile

Optionstexte, Kosten und Default-Optionen in `choices.json` stammen aus KoLmafia
(https://github.com/kolmafia/kolmafia) und stehen unter der BSD-3-Clause-Lizenz:

    Copyright (c) 2005-2021, KoLmafia development team
    All rights reserved.

    Redistribution and use in source and binary forms, with or without
    modification, are permitted provided that the following conditions are met:

    1. Redistributions of source code must retain the above copyright notice, this
       list of conditions and the following disclaimer.

    2. Redistributions in binary form must reproduce the above copyright notice,
       this list of conditions and the following disclaimer in the documentation
       and/or other materials provided with the distribution.

    3. Neither the name of the copyright holder nor the names of its
       contributors may be used to endorse or promote products derived from
       this software without specific prior written permission.

    THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
    AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
    IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
    DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
    FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
    DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
    SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
    CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
    OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
    OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
