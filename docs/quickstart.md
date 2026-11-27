# Quickstart

> **Early Alpha (0.5.0).** KoLibri is usable but not stable; commands, options, file formats and the library API may change before 1.0. (0.4.0 was never released.)
> - **Included:** the library `KoLibri` (+ optional `KoLibri.Safety`), `kolibri` / `kolicli` (CLI), `kolibri-mcp` (MCP server), and the desktop UI (an early client, not broadly tested live).
> - **Unstable:** the desktop UI, MCP tool names and schemas, and the Windows and macOS builds (validated manually; CI test failures there are tolerated).
> - **Live use:** needs your own Kingdom of Loathing account. Requests are rate limited in two lanes (actions one per 2 s, `KOLIBRI_MIN_REQUEST_INTERVAL_MS`; display reads a little faster and side by side; see [usage](usage.md)). Use one session per account; logging in elsewhere can end the stored session. Anything that spends Meat or turns needs `--execute` in the CLI; AI agents must get your confirmation first.
> - Details: [CHANGELOG.md](../CHANGELOG.md).

KoLibri gives you a small .NET library (`using KoLibri;`), a command line (`kolibri`), an MCP server (`kolibri-mcp`) and an early-alpha desktop
client for Kingdom of Loathing. You act yourself through the CLI and desktop. An AI acts through MCP, only inside
limits you approve.

## What 0.5.0 can do / what is experimental

**Supported in 0.5.0** (the scope we test live before the release):

- Login and reconnect; status / charpane with organ limits.
- Inventory: use, eat, drink, chew; cast, rest, equip.
- Adventuring by hand: the browser tab and the encounter view (fight actions, choices); in the CLI
  `adventure inspect|step|fight`.
- A simple auto-combat batch: desktop turn batch and CLI `adventure run` (N turns in one location; stops on an
  unanswered choice, a defeat, a HP threshold or a round limit). Turtle Tamer encounters are clicked through by default.
- Mall search and price-guarded buying; sending KMail.
- The daily routine: `daily run` (Day Shortener via the campfire, Hermit clovers, Internet Meme Shop).
- The `KoLibri` library to write your own tool; see [src/KoLibri/README.md](../src/KoLibri/README.md) and the
  [cookbook](../samples/cookbook/README.md).

**Experimental, hidden by default:** arbitrage, price reports, shop CRM and related MCP tools. They are not part of
the library and get no live verification for this release.

- CLI: add `--experimental` to see and run those commands.
- MCP: start `kolibri-mcp` with `KOLIBRI_EXPERIMENTAL=1` to offer more than the 19 core tools.

Where this goes next: [ROADMAP.md](../ROADMAP.md).

## 1. Install

**Download (no .NET needed).** Take the archive for your system from the GitHub releases page
(`win-x64`, `linux-x64`, `osx-arm64`, `osx-x64`) and unpack it. It contains `kolibri` (CLI), `kolibri-mcp`
(MCP server) and `kolibri-desktop` (desktop, early alpha) plus the data folder next to them. Keep the `external` folder
beside the programs. On macOS the programs are not signed yet: allow them once under
System Settings, Privacy & Security. On Windows SmartScreen may warn for the same reason.

**Library.** The `KoLibri` and `KoLibri.Safety` packages are attached to the GitHub release as `.nupkg` files (not on nuget.org); add the folder as a local NuGet feed. `dotnet tool install` for the CLI and MCP server is not available yet.

**From source (needs the .NET 8 SDK or newer):** `dotnet build KoLibri.slnx`, then `dotnet run --project tools/KolLibri.Cli -- --help`.

## 2. Sign in

```bash
kolibri login main            # prompts for the password, stores an encrypted session
kolibri login mallbot         # optional second account
kolibri status --role main
```

The password is never printed or logged. With the saved password a session that KoL ended is renewed
automatically (`kolibri login refresh`).

## 3. Let an AI use it

```bash
kolibri-mcp
```

It listens on `http://127.0.0.1:8765/mcp` (this computer only). Add that URL as an MCP server in your client
(Claude Desktop, Cursor, ...). On first connect the client opens a browser page. There you choose

- which accounts it may use,
- which rights it gets (read, market, adventure, mall buy, daily run, messages, item use),
- the daily limits (for example 5,000,000 meat of mall purchases, price ceiling, meat reserve),
- how long the access lasts.

The confirmation code printed in the terminal running `kolibri-mcp` is required to approve. Every change to the game
is two steps: the AI previews and receives a plan id, then executes that plan once. See
[mcp-access.md](mcp-access.md) for rights, limits and the honest security limits.

```bash
kolibri-mcp grants list          # who has access, and today's usage
kolibri-mcp grants revoke <id>   # cut one client off immediately
```

## 4. Adventure and the daily routine yourself

```bash
kolibri adventure locations --name pantry            # find a location id (no login, no request)
kolibri adventure run --location 113 --max-turns 5   # preview, sends nothing
kolibri token mint                                   # approval token for live runs
kolibri adventure run --location 113 --max-turns 5 --execute --approval-token TOKEN
kolibri choice pending                               # a choice left open blocks the game; exit 4 if one is open

kolibri daily run --role main                        # preview of the day's routine from the live account
kolibri daily run --role main --execute --approval-token TOKEN
kolibri daily run --role main --only hermit --execute --approval-token TOKEN   # one step (or --skip STEP)
kolibri daily audit --tail 20
```

Under an AI harness the CLI asks for a confirmation at the real terminal before it writes (agent mode, see
`kolibri agent-mode status`).

## Platforms

Linux is the reference platform and the one everything was tested on. Windows and macOS builds come from CI.
Windows protects the local key file with DPAPI; on Linux and macOS it is an owner-only file (an OS keychain
is not used yet). The agent-mode confirmation uses the console device on Windows, which has not been tried on a
real machine yet.

## Known gaps in 0.5.0

- The desktop client is early alpha.
- Verified live over MCP (21 Nov 2026): `kol_mall_buy` and `kol_transfer`, including the usage ledger. The other write tools (meme shop, item use, shop prices, messages, adventures) are covered by tests with fakes and fixtures only; try them with small limits first.
- Foreign bribes are gone; arbitrage and price reports are experimental. Adventure farm flags (`--combat-rules`, `--recover-hp`, `--stop-item`, ...) are not merged yet. <placeholder>
- The CLI has no equip or item-use command; use the desktop or MCP.
- Adventure limits count the requested turn cap, not the exact turns used.
