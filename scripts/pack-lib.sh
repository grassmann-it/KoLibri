#!/usr/bin/env bash
# Packs KoLibri and KoLibri.Safety into <outdir> and checks the package contents.
# Usage: scripts/pack-lib.sh <outdir> [version]
set -euo pipefail
out="${1:?usage: pack-lib.sh <outdir> [version]}"
ver="${2:-}"
root="$(cd "$(dirname "$0")/.." && pwd)"
mkdir -p "$out"; out="$(cd "$out" && pwd)"
rm -f "$out"/*.nupkg
vprop=(); [ -n "$ver" ] && vprop=("-p:Version=$ver")

for p in src/KoLibri src/KoLibri.Safety; do
  dotnet pack "$root/$p" -c Release -o "$out" "${vprop[@]}" --nologo -v q -clp:ErrorsOnly
done

fail=0
err() { echo "FEHLER: $*" >&2; fail=1; }
count=$(ls "$out"/*.nupkg | wc -l)
[ "$count" -eq 2 ] || err "erwartet genau 2 nupkgs, gefunden $count: $(ls "$out")"

lib=$(ls "$out"/KoLibri.[0-9]*.nupkg 2>/dev/null | head -1 || true)
saf=$(ls "$out"/KoLibri.Safety.[0-9]*.nupkg 2>/dev/null | head -1 || true)
[ -n "$lib" ] || { echo "FEHLER: KoLibri-nupkg fehlt" >&2; exit 1; }
[ -n "$saf" ] || { echo "FEHLER: KoLibri.Safety-nupkg fehlt" >&2; exit 1; }

want() { local pkg="$1" list="$2"; shift 2; for f in "$@"; do grep -qxF -- "$f" <<<"$list" || err "$(basename "$pkg"): $f fehlt"; done; }
l1=$(unzip -Z1 "$lib"); l2=$(unzip -Z1 "$saf")
want "$lib" "$l1" lib/net8.0/KoLibri.dll lib/net8.0/KoLibri.Core.dll ref/net8.0/KoLibri.dll \
  data/kolmafia-data/items.jsonl data/kolmafia-data/LICENSE buildTransitive/KoLibri.targets build/KoLibri.targets README.md
grep -qxF ref/net8.0/KoLibri.Core.dll <<<"$l1" && err "KoLibri: ref/net8.0/KoLibri.Core.dll darf nicht im Paket sein"
n1=$(unzip -p "$lib" '*.nuspec')
grep -q 'id="System.Security.Cryptography.ProtectedData"' <<<"$n1" || err "KoLibri: Abhängigkeit ProtectedData fehlt"
grep -q 'id="KoLibri.Core"' <<<"$n1" && err "KoLibri: darf nicht von KoLibri.Core abhängen"

want "$saf" "$l2" lib/net8.0/KoLibri.Safety.dll README.md
n2=$(unzip -p "$saf" '*.nuspec')
pv=$(sed -n 's:.*<version>\(.*\)</version>.*:\1:p' <<<"$n1" | head -1)
grep -q "id=\"KoLibri\" version=\"$pv\"" <<<"$n2" || err "Safety: Abhängigkeit KoLibri $pv fehlt (nuspec: $(grep -o '<dependency [^>]*>' <<<"$n2" | tr '\n' ' '))"

[ "$fail" -eq 0 ] && echo "pack-lib: OK ($(basename "$lib"), $(basename "$saf"))"
exit "$fail"
