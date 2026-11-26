#!/usr/bin/env bash
# Prüft den API-Stub (KoLibri.Api.cs) und erzeugt PublicAPI.Unshipped.txt neu.
# Legt zwei Wegwerfprojekte in einem Temp-Ordner an; die Solution bleibt unberührt.
# Aufruf: docs/lib-api-0.5/check-api.sh [--regenerate]
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
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
  cp "$here/publicapi/$1/PublicAPI.Shipped.txt" "$work/$1/"
  if [[ "${REGEN:-}" == 1 ]]; then echo '#nullable enable' > "$work/$1/PublicAPI.Unshipped.txt"
  else cp "$here/publicapi/$1/PublicAPI.Unshipped.txt" "$work/$1/"; fi
}
[[ "${1:-}" == "--regenerate" ]] && REGEN=1
mk KoLibri "KOLIBRI_CORE" ""
mk KoLibri.Safety "KOLIBRI_SAFETY" '<ProjectReference Include="../KoLibri/KoLibri.csproj" />'
if [[ "${REGEN:-}" == 1 ]]; then
  for p in KoLibri KoLibri.Safety; do
    (cd "$work/$p" && dotnet format analyzers "$p.csproj" --diagnostics RS0016 --severity info >/dev/null 2>&1 || true)
    cp "$work/$p/PublicAPI.Unshipped.txt" "$here/publicapi/$p/"
  done
fi
(cd "$work/KoLibri.Safety" && dotnet build -warnaserror:RS0016,RS0017,RS0026 -nologo -v q)
