#!/usr/bin/env bash
# Builds the self-contained single-file programs for one runtime and lays out a release folder:
#   dist/KoLibri-<version>-<rid>/{kolibri (+kolicli), kolibri-mcp, kolibri-desktop, external/kolmafia-data, README.md, CHANGELOG.md, LICENSE, NOTICE.md}
# Usage: scripts/package.sh <rid> [version]     rid: win-x64 | linux-x64 | osx-arm64 | osx-x64
set -euo pipefail
rid="${1:?runtime identifier required, e.g. linux-x64}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version="${2:-$(sed -n 's:.*<KoLibriVersion>\(.*\)</KoLibriVersion>.*:\1:p' "$root/Directory.Build.props")}"
ext=""; [[ "$rid" == win-* ]] && ext=".exe"
out="$root/dist/KoLibri-$version-$rid"
tmp="$root/dist/.build-$rid"
rm -rf "$out" "$tmp"; mkdir -p "$out" "$tmp"

flags=(-c Release -r "$rid" --self-contained -p:PublishSingleFile=true
       -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
       -p:DebugType=none -p:DebugSymbols=false -v q)

dotnet publish "$root/tools/KolLibri.Cli"  "${flags[@]}" -o "$tmp/cli"
dotnet publish "$root/tools/KolLibri.Mcp"  "${flags[@]}" -o "$tmp/mcp"
dotnet publish "$root/KoLibri.Desktop"     "${flags[@]}" -o "$tmp/desktop"

cp "$tmp/cli/KolLibri.Cli$ext"        "$out/kolibri$ext"
cp "$tmp/mcp/KolLibri.Mcp$ext"        "$out/kolibri-mcp$ext"
cp "$tmp/desktop/KoLibri.Desktop$ext" "$out/kolibri-desktop$ext"
mkdir -p "$out/external"
cp -R "$root/external/kolmafia-data" "$out/external/kolmafia-data"
cp "$root/README.md" "$root/CHANGELOG.md" "$root/LICENSE" "$root/NOTICE.md" "$out/"
mkdir -p "$out/docs" && cp "$root/docs/quickstart.md" "$root/docs/mcp-access.md" "$root/docs/usage.md" "$out/docs/"
# `kolicli` is a second name for `kolibri` (a shim on Windows, a symlink elsewhere).
if [[ "$rid" == win-* ]]; then
  printf '@echo off\r\n"%%~dp0kolibri.exe" %%*\r\n' > "$out/kolicli.cmd"
else
  ln -s kolibri "$out/kolicli"
  chmod +x "$out/kolibri" "$out/kolibri-mcp" "$out/kolibri-desktop"
fi
rm -rf "$tmp"
echo "$out"
