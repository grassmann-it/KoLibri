# KoLibri

**KoLmafia's automation, minus the clicking.**

KoLibri is a .NET 8 toolset for [Kingdom of Loathing](https://www.kingdomofloathing.com/).
It covers the same ground as [KoLmafia](https://github.com/kolmafia/kolmafia) —
the complete data snapshot, account reads, mall pricing, crafting,
adventures and the daily routines — but it leans on automation instead of
manual clicks: you tell it *what* should happen, it works out the *how*,
shows you the plan and asks for approval before it touches anything.

```
dotnet run --project tools/KolLibri.Cli -- daily breakfast --role all --execute --approval-token $TOK
```

That single command reads both accounts, works out the day's numbers
from the live data, plans the routine and runs it.

## What is in here

| Project | Role | Package |
|---|---|---|
| `src/KolArbitrage.Core` | The domain: KoL connector, data catalog, daily planners, mall arbitrage, bounded runner, audit ledger | `KoLibri.Core` |
| `src/LibKolibri` | The public API façade over the core | [`KoLibri.LibKolibri`](https://github.com/grassmann-it/LibKolibri) |
| `tools/KolLibri.Cli` | Command line front-end | — |
| `tools/KolLibri.Mcp` | MCP / JSON-RPC server for agent hosts | — |
| `KoLibri.Desktop` | Avalonia desktop client | — |
| `external/kolmafia-data` | The KoLmafia data dump as JSON Lines | — |

The split follows one question: *what gets reused?* Everything that
several tools would need — the connector, the planners, the arbitators —
lives in the core. The façade is deliberately thin. The apps are the
operator-facing part.

## Features

### Daily routines

| Routine | What it does |
|---|---|
| **Campfire** | Burns one inflammable leaf to advance the day, or crafts a Day Shortener (222 leaves) when the reserve runs thin. |
| **Hermit** | Trades worthless items for permits (`autopermit=on`), then opens 11-leaf clovers — as many as the daily stock allows, capped at three. |
| **Internet shop** | Claims the daily free pulls, skipping whatever was already taken. |
| **Foreign bribes** | Buys bribe bags on the mall when the spread justifies it. |
| **Bacon arbitrage** | Buys the Infinite BACON machine once and then BACON while the spread against the resale median is positive. |
| **Clover restock** | Buys cheap 11-leaf clovers so the main account stays stocked for the hermit trade. |

Every routine is split in two: a **planner** that is a pure function over
a snapshot, and an **executor** that submits. The planner never touches
the network, so the operator can see exactly what would happen before
anything is sent.

### Adventures

- A **manual adventure browser**: locations → encounter → monster →
  skill/item, with a token-gated live path.
- A **bounded runner** that submits only allowlisted actions, stops after
  a bounded number of choices and never replays a request.
- An **auto-combat adapter** that turns a strategy into typed step specs,
  so a manual run and an automated one share the same contract.

### Mall and pricing

- Mall search, own-store reads, pricegun history.
- A price provider that plugs into the pricing engine.
- An executor that buys from another player's store: it reads the store
  password hash out of the store page and submits the purchase with the
  exact listing price, because KoL rejects a purchase whose price moved.

### Multi-account

Main and Mallbot are first-class roles with their own sessions, their
own meat pools and their own daily shop limits. `daily-go --role all`
runs both, in parallel, with one hash-chained audit trail across the
whole run.

### Data

The complete KoLmafia data dump, bundled and typed:

| Records | Count |
|---|---|
| Items | 12 103 |
| Modifiers | 13 375 |
| Effects | 3 015 |
| Concoctions | 2 238 |
| Monster parts | 2 214 |
| Monsters | 2 366 |
| Skills | 1 657 |
| Restores | 164 |
| Adventures | 601 |
| Zones | 148 |

```bash
python3 scripts/import-kolmafia-data.py   # refresh from a KoLmafia checkout
```

## Install

The library:

```xml
<PackageReference Include="KoLibri.LibKolibri" Version="0.1.0-preview" />
```

```csharp
using KolArbitrage.Core.Basis;
using LibKolibri;

using var kol = await KolibriClient.OpenAsync(AccountRole.Main);
var status = await kol.GetStatusAsync();
Console.WriteLine($"{status.PlayerName}: {status.Meat} meat");
```

The CLI and the MCP server install as dotnet tools (`KoLibri.Cli`, `KoLibri.Mcp`) or build from source:

```bash
dotnet build KoLibri.slnx
dotnet run --project tools/KolLibri.Cli -- --help
```

## Desktop

The desktop client is the operator's console: it reads both accounts
live, shows the inventory with the KoLmafia catalog, the daily briefing,
the market scan with the buy rules, the mall and the combat panel.

```bash
dotnet run --project KoLibri.Desktop
```

It needs a session for each account — the panels stay empty without one:

```bash
kolibri login refresh --role main
kolibri login refresh --role mallbot
```

`kolibri login status` tells whether a session still works. It asks KoL,
not just the stored cookie: a session that KoL dropped says
`rejectedByServer: true`, and `usable: false`. The stored expiry only
covers the cookie's lifetime — KoL ends a session earlier when the
password changes or the account is signed out somewhere else.

The panels:

| Panel | What it shows |
|---|---|
| **Guts** | Meat, adventures, items and level of both accounts |
| **Inventory** | The live inventory with the catalog's flags, uses and images |
| **Daily** | The day's briefing from the live accounts |
| **Arbitrage** | Mall listings against the pricegun median, plus the buy rules |
| **Mall** | Own store and the market |
| **Adventures** | Locations, encounters and the bounded runner |
| **Combat** | The auto-combat policy and the fight log |

Everything that spends meat is behind the approval token:

```bash
kolibri token mint
```

## CLI

```bash
# Account sessions
kolibri login status
kolibri login refresh --role main          # interactive, password not echoed
kolibri login clear --role all

# Approval token
kolibri token mint

# Live reads
kolibri status --role main --refresh
kolibri inventory --role main --refresh
kolibri shop --role mallbot --refresh
kolibri pricegun --item 10881 --refresh

# Daily routine — the morning run
kolibri daily breakfast --role all                 # preview
kolibri daily breakfast --role all \
  --execute --approval-token "$TOK"                 # live

# Individual routines
kolibri daily campfire --leaves 37191 --meat 128310
kolibri daily hermit --permits 2 --clovers 3 --clover-sale 30000
kolibri daily internet-shop
kolibri daily meme-shop-read

# Audit trail
kolibri daily-audit --tail 20
kolibri daily-audit --verify
```

## MCP

`kolibri-mcp` serves MCP over HTTP on `127.0.0.1:8765` (local only). An AI client
connects, the browser opens a consent page, and **you** choose accounts, rights and
daily limits. The client only receives what you approved; see
[docs/mcp-access.md](docs/mcp-access.md).

```bash
kolibri-mcp                    # http://127.0.0.1:8765/mcp, consent code printed here
kolibri-mcp grants list        # approved clients with today's usage
kolibri-mcp grants revoke ID   # stop one client at once
kolibri-mcp --stdio            # read-only tools over stdio, no approval needed
```

| Tool | What it does |
|---|---|
| `kol_status` / `kol_inventory` | Account reads |
| `kol_price_summary` / `kol_price_recommendation` / `kol_underprice_preview` | Market reads |
| `kol_run_daily_bridge` / `kol_daily_*` | Plans the daily routine without executing |
| `kol_preview_purchase` / `kol_preview_transfer` | Validates a proposal without acting |
| `kol_adventure_run_execute` / `kol_choice_chain_execute` | Bounded live adventures (right `adventure`); not wired in `kolibri-mcp` yet, see docs/mcp-access.md |

Login management tools are not available to AI clients.

## Safety model

This tool acts on a game account that holds real value, so the guardrails
are structural rather than advisory.

1. **Reads are free, writes are gated.** An AI client needs a token you
   approved in the browser, with rights and per-day limits. The CLI and Desktop
   are yours and need none; under an AI harness the CLI asks for a
   confirmation at the real terminal before it writes (`kolibri agent-mode`).
2. **Credentials never appear in code.** Sessions live encrypted in the
   user's local app-data folder. The library reads them at runtime and
   keeps them inside the authenticated HTTP clients.
3. **Every write is auditable.** Actions land in an append-only journal
   where each row carries the SHA-256 hash of the row before it.
   `kolibri daily-audit --verify` reports the first tampered row.
4. **Bounded runs.** The runner stops after a configured number of
   choices or a configured duration, and never replays a request.
5. **Price discipline.** A purchase is submitted with the price the
   listing showed. If the price moved, KoL rejects it — KoLibri does not
   silently raise the bid.

## Limitations

Honest about what does not work yet:

- **Leaves cannot be bought.** The inflammable leaf only comes from the
  campfire, so a leaf shortage cannot be fixed by shopping.
- **Free pulls need a clan store.** The Lucky Surprise Egg and the
  Infinite BACON machine are claimed from the storage, which requires the
  clan store password.
- **The transfer path is separate.** Restocking the main account from
  the mallbot goes through its own preview/approval/commit flow rather
  than through the daily routine.
- **The combat preview is an estimate.** Until the effect and modifier
  tracker is complete, the auto-combat preview does not account for every
  conditional buff.
- **The daily shop limit is per account.** Running both accounts buys
  twice as much, but KoL's limit applies to each account separately.

## Development

```bash
dotnet build KoLibri.slnx
dotnet test tests/KolArbitrage.Core.Tests/KolArbitrage.Core.Tests.csproj
dotnet pack src/KolArbitrage.Core/KolArbitrage.Core.csproj -c Release
```

The `src/LibKolibri` submodule points at
[grassmann-it/LibKolibri](https://github.com/grassmann-it/LibKolibri).

## License

MIT — see [LICENSE](LICENSE).
