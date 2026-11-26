# LibKolibri

**A drop-in .NET 8 connector for [Kingdom of Loathing](https://www.kingdomofloathing.com/).**

LibKolibri gives a .NET tool the building blocks that
[KoLmafia](https://github.com/kolmafia/kolmafia) gives its scripts: the
KoLmafia data snapshot, an authenticated session, account reads,
adventures and fights, consumables, mall search and price-guarded buying,
and KMail. The idea is *write your own tool with LibKolibri*: the library
ships primitives, not finished solutions such as diet optimizers,
arbitrage bots or a shop CRM.

> **Early Alpha (0.4.0).** The public API is still large and will shrink in
> 0.5 (one core package, a small documented surface, NuGet). Expect breaking
> changes; see [ROADMAP.md](../../ROADMAP.md).

```csharp
using KoLibri.Core.Basis;   // AccountRole
using LibKolibri;

// Sign in once; the session is stored encrypted and renewed with the saved password when KoL drops it.
using var kol = await KolibriClient.LoginAsync(AccountRole.Main, "name", password);
// Later runs: using var kol = await KolibriClient.OpenAsync(AccountRole.Main);

var status = await kol.GetStatusAsync();
Console.WriteLine($"{status.PlayerName}: {status.Meat} meat, {status.Adventures} adventures");

// Mall: search, then buy under a price ceiling and a meat reserve you state.
var listings = await kol.SearchMallAsync("11-leaf clover");
var bought = await kol.BuyCheapAsync(10881, "11-leaf clover", quantity: 3, maxPricePerUnit: 27_000, meatReserve: 100_000);

// Adventures: one action at a time, or bounded batches, under your own approval token.
await using var adventure = await kol.OpenAdventureAsync(approvalToken: Guid.NewGuid().ToString("N"));
```

`KolibriClient` is the front door: login, account reads, mall search and
price-guarded buying, the daily Internet Meme Shop, and adventure sessions.
The lower-level building blocks stay available underneath it.

## Use it with an AI assistant

Two ready-made tools sit on top of the library:

```bash
dotnet tool install -g KoLibri.Cli      # `kolibri`: login, reads, mall, daily, adventure; preview unless --execute
dotnet tool install -g KoLibri.Mcp      # `kolibri-mcp`: an MCP server an assistant can drive
kolibri login main                      # asks for name and password, stores the session
```

Point an MCP-capable assistant at the `kolibri-mcp` command. Reads are free.
Every action that changes the account needs an explicit approval, and each
purchase has a price ceiling and a meat reserve.

## Install

NuGet publishing is planned for 0.5. Until then reference the project from
a source checkout:

```xml
<ProjectReference Include="path/to/KoLibri/src/LibKolibri/src/LibKolibri/LibKolibri.csproj" />
```

The project targets `net8.0` and pulls in `KoLibri.Core`, which carries the
game logic. In 0.4.0 you still need a few core types directly — for example
`AccountRole` from `KoLibri.Core.Basis`, as in the example above. 0.5 merges
both into one package.

## What is in the box

| Area | What you get |
|---|---|
| **Data** | The complete KoLmafia data dump as JSON Lines: 12 103 items, 2 366 monsters, 1 657 skills, 3 015 effects, 601 adventures, 148 zones, 13 375 modifiers, 2 238 concoctions, 2 214 monster parts, 164 restores. Loaded into typed records. |
| **Account reads** | Status, inventory, equipment, familiar, skills, effects — read-only, rate-limited, with a stale-cache fallback. |
| **Adventures** | `OpenAdventureAsync`: a manual adventure session — visit a zone, fight (attack, skill, item), pick a choice option — one request at a time. |
| **Consumables & character** | `UseItemAsync`, `ConsumeAsync` (eat/drink/chew), `CastSkillAsync`, `RestAsync`. |
| **KMail** | `SendKMailAsync` / `SendKMailOutcomeAsync`, with items and meat. |
| **Mall & pricing** | Mall search, own-store reads, pricegun history, a price provider that plugs into the pricing engine. |
| **Daily routines** | Campfire leaves and the Day Shortener (222 leaves), the hermit permit / 11-leaf clover flow, the Internet Meme Shop (machine push, then the five once-a-day items). Planners are pure functions over a snapshot. Progress is kept per KoL day, so a repeat call only does what is left. |
| **Mall buying** | Price-guarded buying: cheapest listing first, a price ceiling, a meat reserve, stores at their daily limit skipped, success only on KoL's own confirmation. Clover restock and the BACON top-up for the meme shop run on it. The Infinite BACON machine is not tradeable and is never bought. |
| **Adventure runner** | A bounded runner that submits only allowlisted actions, stops after a bounded number of choices and never replays a request. |
| **Audit** | An append-only journal with a SHA-256 hash chain over the rows. `VerifyChain()` reports the first tampered row. |
| **Transfer** | Main ↔ Mallbot transfers with a preview/approval/commit flow and a dry-run default. |

Daily routines, Main ↔ Mallbot transfer, own-shop repricing and the audit
ledger are still in the box in 0.4.0, but they are not what the library is
for. In 0.5 they move into tools or an optional package, and account roles
give way to accounts addressed by id or name.

## The sample

`sample/KolLibri.Sample` is a runnable consumer that shows what most
tools need first: open the stored session (or log in), read status and
inventory, and search the mall.

```bash
dotnet run --project sample/KolLibri.Sample -- main
```

## API overview

Start with `KolibriClient`; it is the only entry point we intend to keep.
Older runtime and gateway types (`LibKolibriRuntime`, `RoleScopedRuntime`,
`McpRuntimeBootstrap`, `CoreGatewayAdapters`, transfer adapters) still
exist in the assembly but are internal plumbing for the CLI and MCP
server. Do not build on them; they go away in 0.5.

## What is not there yet

Be clear-eyed before you build on it:

- No ASH or JavaScript scripting layer, no relay browser, no maximizer combination search.
- Chat is read and send only; KMail cannot delete and has no outbox view.
- Quest and path coverage is partial; unusual adventure choices need your own allowlist.
- The API is `0.x` and may still move. The core is grouped in three layers: `KoLibri.Core.Basis` (accounts, security, safety, audit), `.Spiel` (adventure, daily, client reads, game data) and `.Markt` (mall, pricing, analytics). A lower layer never names a higher one.

## Safety model

This library talks to a game account that holds real value. Three rules
are built in and are not optional:

1. **Reads are free, writes are gated.** Every mutating path requires an
   approval token. A tool that reads without a token simply cannot
   write.
2. **Credentials never appear in code.** Sessions are stored encrypted in
   the user's local app-data folder. The library reads them at runtime and
   keeps them inside the authenticated HTTP clients.
3. **Every write is auditable.** Actions land in a hash-chained journal.
   Each row carries the hash of the row before it, so a tampered log is
   detectable.

Planning is kept apart from execution where the library plans at all:
a plan is a pure function over a snapshot, so you can show the operator
exactly what would happen before anything is submitted.

## Related

- **[grassmann-it/KoLibri](https://github.com/grassmann-it/KoLibri)** — the
  repository this package lives in, together with the CLI, the MCP server and
  the Avalonia desktop client built on top of it.
- **KoLmafia** — the original client whose data and behaviour this
  connector mirrors.

## License

MIT — see [LICENSE](LICENSE).
