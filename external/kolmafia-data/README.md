# KolLibri Vendor: KoLmafia data snapshot

This directory holds a minimal, KoLmafia-compatible data snapshot that
KoLibri loads to build its adventure, monster, skill and item catalogs.

## Source format

The original KoLmafia project distributes tab-separated `.txt` files
under `src/data/` (monsters, items, skills, adventures, zones). For
KoLibri's purposes we keep the same conceptual schema but store the
records as JSON Lines — one record per line — so the loader does not
depend on tab-position heuristics and the file is easy to diff and edit.

Converting from the KoLmafia `.txt` dumps is a one-off script and lives
outside of this repository. Each line carries the same fields the
corresponding Mafia file provides, with the following naming:

* `monsters.jsonl` — `{ "id": "...", "name": "...", "hp": N, "attack": N,
  "defense": N, "elements": ["cold", ...], "skills": [...],
  "items": [...], "phylum": "..." }`
* `items.jsonl` — `{ "id": "...", "name": "...", "type": "food" | ...,
  "tradeable": bool, "usable": bool }`
* `skills.jsonl` — `{ "id": "...", "name": "...", "type": "combat",
  "mpCost": N, "permable": bool }`
* `adventures.jsonl` — `{ "id": "...", "name": "...", "snarfblat": N,
  "zone": "...", "turnCost": N, "requirements": [...] }`
* `zones.jsonl` — `{ "id": "...", "name": "...", "type": "indoor" | ... }`

## License

The data describes items, monsters and zones from the Kingdom of
Loathing game. The values are reproduced verbatim from the open
KoLmafia data dumps (https://github.com/kolmafia/kolmafia/tree/main/src/data)
which are distributed alongside the KoLmafia source under the
KoLmafia licence. See `LICENSE` for the upstream licence text.
