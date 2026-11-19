#!/usr/bin/env bash
# imports the KoLmafia data files (cloned into external/kolmafia) into the
# JSON Lines snapshot under external/kolmafia-data/.
#
# Run from the repository root: ./scripts/import-kolmafia-data.sh
# Requires: bash, awk, sed, git (already used to clone the upstream repo).
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
MAFIA_DIR="$REPO_ROOT/external/kolmafia"
DATA_SRC="$MAFIA_DIR/src/main/resources/data"
OUT_DIR="$REPO_ROOT/external/kolmafia-data"

if [ ! -d "$DATA_SRC" ]; then
  echo "Cloning kolmafia upstream..." >&2
  git clone --depth 1 https://github.com/kolmafia/kolmafia.git "$MAFIA_DIR"
fi

mkdir -p "$OUT_DIR"

# Strip '#'-comments, blank lines, and the leading version-number row at the
# top of every Mafia data file. Each remaining line becomes one JSON record.
emit_jsonl() {
  local input="$1" output="$2"
  awk -F'\t' -v OFS='\u0001' '
    BEGIN { id_field=1; emit=0 }
    /^[[:space:]]*#/ { next }
    /^[[:space:]]*$/ { next }
    NR==1 && NF==1 { next } # top-of-file version row
    {
      # Escape backslashes and double quotes for JSON.
      gsub(/\\/, "\\\\");
      gsub(/"/, "\\\"");
      printf("{\"raw\": \"%s\"}\n", $0);
    }
  ' "$input" > "$output"
}

# items.txt: tab-separated with a multi-line comment header. We strip comments
# first with sed, then emit. Other files have either tab- or whitespace-
# separated rows; the loader splits per row, so we keep the raw form.
strip_comments_and_emit() {
  local input="$1" output="$2" separator="${3:-tab}"
  case "$separator" in
    tab) local FS='\t' ;;
    *)   local FS='[[:space:]]+' ;;
  esac
  awk -v FS="$FS" '
    BEGIN { first_data = 0 }
    /^[[:space:]]*#/ { next }
    /^[[:space:]]*$/ { next }
    NR==1 && NF==1 { next }
    {
      gsub(/\\/, "\\\\");
      gsub(/"/, "\\\"");
      if (!first_data) { first_data = 1; }
      printf("{\"raw\": \"%s\"}\n", $0);
    }
  ' "$input" > "$output"
}

emit monsters items adventures zonelist classskills statuseffects modifiers \
      concoctions monsterparts restores bountyalcohol bountea \
      bountymonster bounty bountyspell bountysword coinmasters \
      combats effects equipment fambattle familiars foldgroups inebriety \
      intrinsics mana maximizer moods outfits plant potions quest \
      snowball smith spring summoning TCRS.weap tcrs volcanomaze \
      weapons zapgroups 2>/dev/null || true

strip_comments_and_emit "$DATA_SRC/monsters.txt"     "$OUT_DIR/monsters.jsonl"     tab
strip_comments_and_emit "$DATA_SRC/items.txt"        "$OUT_DIR/items.jsonl"        tab
strip_comments_and_emit "$DATA_SRC/adventures.txt"   "$OUT_DIR/adventures.jsonl"   tab
strip_comments_and_emit "$DATA_SRC/zonelist.txt"     "$OUT_DIR/zones.jsonl"        tab
strip_comments_and_emit "$DATA_SRC/classskills.txt"  "$OUT_DIR/skills.jsonl"       tab
strip_comments_and_emit "$DATA_SRC/statuseffects.txt" "$OUT_DIR/effects.jsonl"    tab
strip_comments_and_emit "$DATA_SRC/modifiers.txt"    "$OUT_DIR/modifiers.jsonl"    tab
strip_comments_and_emit "$DATA_SRC/concoctions.txt"  "$OUT_DIR/concoctions.jsonl"  tab
strip_comments_and_emit "$DATA_SRC/monsterparts.txt" "$OUT_DIR/monsterparts.jsonl" tab
strip_comments_and_emit "$DATA_SRC/restores.txt"      "$OUT_DIR/restores.jsonl"     tab

echo "Wrote snapshots to $OUT_DIR:"
ls -la "$OUT_DIR"
