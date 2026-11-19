# KoLibri

KoLibri is a drop-in .NET 8 connector for **Kingdom of Loathing**. It
bundles the full KoLmafia data snapshot, account reads, mall/pricing
lookups, the bounded adventure runner, the hash-chain audit ledger, the
daily planners and the KoLmafia catalog loader so other tools can plug in
without re-implementing the KoL plumbing.

The repository is split into:

| Project | Role |
|---|---|
| `src/LibKolibri` | Public KoL connector (NuGet: `KoLibri.LibKolibri`) |
| `src/KolArbitrage.Core` | Domain layer: bounded runner, daily planners, audit, token store, data records (NuGet: `KoLibri.Core`) |
| `tools/KolLibri.Cli` | Local CLI front-end |
| `tools/KolLibri.Mcp` | MCP/JSON-RPC bridge for agents |
| `KoLibri.Desktop` | Avalonia desktop UI with a manual adventure browser |
| `external/kolmafia-data/` | Bundled JSON Lines snapshot of the KoLmafia data dumps |
| `scripts/import-kolmafia-data.py` | One-shot converter for upstream KoLmafia updates |
| `tests/ConsumerSmoke` | External-consumer smoke test that consumes the built NuGet packages |

## Library usage

```xml
<PackageReference Include="KoLibri.LibKolibri" Version="0.1.0-preview" />
```

```csharp
using LibKolibri;
using KolArbitrage.Core.Data;
using KolArbitrage.Core.Security;

var runtime = new LibKolibriRuntime();
// configure account sessions, then expose the runtime to your tool
```

## Status

- Daily planners (campfire leaves, hermit permits/clovers, daily internet
  shop, Infinite Bacon Machine arbitrage) — preview only by default.
- Bounded adventure runner with allowlist enforcement and audit chain.
- 33 k records of KoLmafia data bundled as JSON Lines (items, monsters,
  skills, effects, zones, adventures, modifiers, concoctions, monster parts,
  restores).
- Approval-token gate with path-filtered persistence in the user's
  local app data folder.
- MCP bridge exposing read-only planning tools and the bounded-runner
  live path (gated by an approval token).

## Quick start (local CLI)

```bash
dotnet run --project tools/KolLibri.Cli -- daily routine \
  --leaves 7 --main-meat 1500000 --permits 3 --clovers 1 \
  --mallbot-meat 12000000 --bag-price 8500000 --bag-stock 5 \
  --output reports/$(date +%F)-routine.json
```

## License

MIT — see `LICENSE`.
