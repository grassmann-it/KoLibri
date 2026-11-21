# Quickstart

KoLibri gives you a .NET library, a command line (`kolibri`), an MCP server (`kolibri-mcp`) and an early-alpha desktop
client for Kingdom of Loathing. You act yourself through the CLI and desktop. An AI acts through MCP, only inside
limits you approve.

## 1. Install

**Download (no .NET needed).** Take the archive for your system from the GitHub releases page
(`win-x64`, `linux-x64`, `osx-arm64`, `osx-x64`) and unpack it. It contains `kolibri` (CLI), `kolibri-mcp`
(MCP server) and `KoLibri` (desktop, early alpha) plus the data folder next to them. Keep the `external` folder
beside the programs. On macOS the programs are not signed yet: allow them once under
System Settings, Privacy & Security. On Windows SmartScreen may warn for the same reason.

**dotnet tools (needs the .NET 8 SDK or newer).**

```bash
dotnet tool install -g KoLibri.Cli      # command: kolibri
dotnet tool install -g KoLibri.Mcp      # command: kolibri-mcp
```

Or from source: `dotnet build KoLibri.slnx`, then `dotnet run --project tools/KolLibri.Cli -- --help`.

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
- which rights it gets (read, market, adventure, mall buy, daily run, transfer, messages, item use, shop prices),
- the daily limits (for example 5,000,000 meat of mall purchases, price ceiling, meat reserve),
- how long the access lasts.

The confirmation code printed in the terminal running `kolibri-mcp` is required to approve. Every change to the game
is two steps: the AI previews and receives a plan id, then executes that plan once. See
[mcp-access.md](mcp-access.md) for rights, limits and the honest security limits.

```bash
kolibri-mcp grants list          # who has access, and today's usage
kolibri-mcp grants revoke <id>   # cut one client off immediately
```

## 4. Daily routine yourself

```bash
kolibri daily-go --role all                 # preview of the whole routine from the live accounts
kolibri daily-go --role all --execute       # run the confirmed steps
kolibri daily meme-shop --role main --execute   # Internet Meme Shop (BACON top-up is on by default; --no-top-up)
```

Under an AI harness the CLI asks for a confirmation at the real terminal before it writes (agent mode, see
`kolibri agent-mode status`).

## Platforms

Linux is the reference platform and the one everything was tested on. Windows and macOS builds come from CI.
Windows protects the local key file with DPAPI; on Linux and macOS it is an owner-only file (an OS keychain
is not used yet). The agent-mode confirmation uses the console device on Windows, which has not been tried on a
real machine yet.

## Known gaps in 0.4.0

- The desktop client is early alpha.
- Verified live over MCP (21 Nov 2026): `kol_mall_buy` and `kol_transfer`, including the usage ledger. The other write tools (meme shop, item use, shop prices, messages, adventures) are covered by tests with fakes and fixtures only; try them with small limits first.
- Foreign bribes are not part of this release.
- Adventure limits count the requested turn cap, not the exact turns used.
