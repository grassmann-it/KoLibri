#!/usr/bin/env bash
# Builds KoLibri and KoLibri.Safety as nupkgs into a local feed, then builds tests/ConsumerSmoke and every cookbook app
# against that feed (plus nuget.org for the transitive packages) and runs ConsumerSmoke offline.
# Usage: scripts/consumer-smoke.sh [workdir]     (default: a fresh temp dir; nothing is written into the repo)
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="${1:-$(mktemp -d)}"
mkdir -p "$work"; work="$(cd "$work" && pwd)"
feed="$work/feed"
# A unique version per run keeps stale packages in any NuGet cache from being picked up.
version="0.5.0-ci.$(date +%s)"
export NUGET_PACKAGES="$work/packages"   # empty cache: nothing is resolved from a previous run
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
rm -rf "$feed" "$NUGET_PACKAGES"; mkdir -p "$feed"

echo "== pack $version into $feed"
"$root/scripts/pack-lib.sh" "$feed" "$version"

src=(--source "$feed" --source https://api.nuget.org/v3/index.json)
# bin/obj go to the workdir, so the repo stays free of copies of the packages' DLLs.
build() {
  local n; n="$(basename "$1" .csproj)"
  dotnet build "$1" -c Release --nologo -v q -clp:ErrorsOnly "${src[@]}" -p:KoLibriPackageVersion="$version" \
    "-p:BaseOutputPath=$work/out/$n/" "-p:BaseIntermediateOutputPath=$work/obj/$n/"
}

echo "== build ConsumerSmoke"
build "$root/tests/ConsumerSmoke/ConsumerSmoke.csproj"
for p in "$root"/samples/cookbook/*/*.csproj; do
  echo "== build cookbook $(basename "$p" .csproj)"
  build "$p"
done

echo "== run ConsumerSmoke"
dotnet "$work/out/ConsumerSmoke/Release/net8.0/ConsumerSmoke.dll"
