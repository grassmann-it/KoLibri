#!/usr/bin/env bash
# Prüft die öffentliche API von KoLibri 0.5 gegen die PublicAPI-Dateien in src/KoLibri und src/KoLibri.Safety.
#  1. Der Entwurfs-Stub (KoLibri.Api.cs) muss genau die PublicAPI-Dateien ergeben (Wegwerfprojekte im Temp-Ordner).
#  2. Die echten Projekte src/KoLibri, src/KoLibri.Safety und src/KoLibri.Daily müssen mit denselben Dateien bauen
#     (RS0016/RS0017/RS0026 sind dort Fehler, siehe csproj).
# Aufruf: docs/lib-api-0.5/check-api.sh [--regenerate]
#   --regenerate erzeugt PublicAPI.Unshipped.txt neu aus dem Stub (der Stub bleibt die Quelle der Wahrheit).
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mk() { # name defines extra
  mkdir -p "$work/$1"
  cat > "$work/$1/$1.csproj" <<XML
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>disable</ImplicitUsings>
    <NoWarn>CS0067</NoWarn><DefineConstants>\$(DefineConstants);$2</DefineConstants>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="$here/KoLibri.Api.cs" />
    <AdditionalFiles Include="PublicAPI.Shipped.txt;PublicAPI.Unshipped.txt" />
    <PackageReference Include="Microsoft.CodeAnalysis.PublicApiAnalyzers" Version="3.3.4" PrivateAssets="all" />
    $3
  </ItemGroup>
</Project>
XML
  cp "$repo/src/$1/PublicAPI.Shipped.txt" "$work/$1/"
  if [[ "${REGEN:-}" == 1 ]]; then echo '#nullable enable' > "$work/$1/PublicAPI.Unshipped.txt"
  else cp "$repo/src/$1/PublicAPI.Unshipped.txt" "$work/$1/"; fi
}
[[ "${1:-}" == "--regenerate" ]] && REGEN=1
mk KoLibri "KOLIBRI_CORE" ""
mk KoLibri.Safety "KOLIBRI_SAFETY" '<ProjectReference Include="../KoLibri/KoLibri.csproj" />'
if [[ "${REGEN:-}" == 1 ]]; then
  for p in KoLibri KoLibri.Safety; do
    (cd "$work/$p" && dotnet format analyzers "$p.csproj" --diagnostics RS0016 --severity info >/dev/null 2>&1 || true)
    cp "$work/$p/PublicAPI.Unshipped.txt" "$repo/src/$p/"
  done
fi
echo "== Stub (docs/lib-api-0.5/KoLibri.Api.cs)"
(cd "$work/KoLibri.Safety" && dotnet build -warnaserror:RS0016,RS0017,RS0026 -nologo -v q)
echo "== Implementierung (src/KoLibri, src/KoLibri.Safety, src/KoLibri.Daily)"
dotnet build "$repo/src/KoLibri.Safety/KoLibri.Safety.csproj" -nologo -v q
dotnet build "$repo/src/KoLibri.Daily/KoLibri.Daily.csproj" -nologo -v q
