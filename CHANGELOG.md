# Changelog

## 0.5.0 - Early Alpha

First public release. (0.4.0 was an internal milestone and was never published.)
KoLibri is a KoLmafia-like client for Kingdom of Loathing on a small .NET library: play by hand, farm with a simple
auto-combat loop, and write your own tools with `using KoLibri;`. Usable, not stable; APIs may change before 1.0.

### The library: `KoLibri`
- One package, one namespace, about 110 public types, locked by PublicAPI files.
- `KolibriClient` -> `KolibriSession` per account (free account ids, not fixed roles).
- Status, inventory, consume/use/cast/rest/equip, adventures with one runner (`RunAsync`), pure `ICombatStrategy` and
  `IChoiceResolver`, stop conditions, mall search and price-guarded buying, own shop, KMail, events, raw
  `GetPageAsync`/`PostAsync` through allowlist and rate limiter, one `IActionGuard` hook.
- Optional `KoLibri.Safety`: grants, budgets, approval tokens, audit journal.
- Not in the library on purpose: diet planning, arbitrage, shop CRM. Build them with it.
- Combat macros (`CombatStrategies.Macro`) are experimental (`KOLIBRI0001`): sent as `fight.php` `action=macro` only
  with `KolibriOptions.EnableExperimentalMacros = true`, otherwise refused. First live check on 2026-11-27: single attack,
  runaway and invalid macro behave as documented; repeat, abort and skill are still open
  ([docs/macro-live-check.md](docs/macro-live-check.md), test app `samples/macro-live-check`).
- A fight that KoL ends on the page that started it (auto-attack) is a `FinishedFightEncounter` with monster, outcome,
  loot and meat; the run counts it for `MonsterSeen` and `FightEnded` and sends no fight action.
- `FightRound.Hp`/`Mp` are always the player's values (page markup or `api.php?what=status`), never a number taken from
  the combat text. Choice titles are read from KoL's current header layout.

### Packages and cookbook
- `scripts/pack-lib.sh` packs `KoLibri` and `KoLibri.Safety` and checks the contents. `KoLibri.Core` is bundled inside
  `KoLibri` (private); `KoLibri.Core`, `LibKolibri`, `KoLibri.Daily` and `KoLibri.Extras` are not packable.
- The KoLmafia data files ship in the nupkg and are copied next to your app; `KolibriOptions.DataDirectory`
  overrides the location.
- The tag workflow attaches the two nupkgs to the GitHub release. They are not pushed to nuget.org.
- [samples/cookbook](samples/cookbook/README.md): five small apps (status, farm loop, mall buy, KMail bot, events)
  that use only the public API.
- `tests/ConsumerSmoke` builds consumers against the packed nupkgs. <ConsumerSmoke: confirm when merged>

### Apps
- CLI `kolibri`: login/refresh, status, inventory, consume, cast, rest, `adventure run` (auto-combat with HP floor,
  round limit, stop conditions, choice policy), `adventure inspect|step|fight|locations`, `choice pending|submit|policy`,
  `daily run` (with `--only` / `--skip`, aliases `daily campfire|hermit|internet-shop`; owned daily items: cheap toaster,
  VIP Klaw 3x, Cargo Cultist Shorts pocket with `--cargo-pocket`, fancy chess set with `--chess`) and `daily audit`, mall search,
  shop read and `shop add`, pirate keys, relay. Adventure farm flags (`--combat-rules`, `--recover-hp`,
  `--stop-item`, ...) and an experimental `reprice`: <placeholder until merged>.
- MCP `kolibri-mcp`: 19 core tools by default (`kol_status`, `kol_inventory`, `kol_login_status`,
  `kol_adventure_locations`, `kol_adventure_run_execute`, `kol_combat_inspect`, `kol_combat_decision`,
  `kol_choice_pending`, `kol_choice_options`, `kol_choice_chain_execute`, `kol_use_item`, `kol_consume`, `kol_rest`,
  `kol_price_summary`, `kol_mall_buy`, `kol_send_message`, `kol_daily_audit`, `kol_run_daily_bridge`,
  `kol_meme_shop`); every write is two-step with a `planId` and granted daily limits.
- Desktop: live character pane, inventory, skills, gear, adventures with the auto-combat batch, mall, daily, browser,
  settings. Turtle Tamer encounters are clicked through by default. <L3: final list>
- Experimental extras (arbitrage, bribes, CRM, price reports) stay hidden behind `--experimental` /
  `KOLIBRI_EXPERIMENTAL=1`.

### Breaking changes from 0.4 (source checkouts)
- `LibKolibri` / `KoLibri.Core` -> `KoLibri`; roles `Main`/`Mallbot` -> account ids `"main"`/`"mallbot"` (stored
  sessions keep working).
- No `IsStale` snapshots: reads throw `KolibriException`/`SessionExpiredException` instead of returning old data.
- Removed CLI commands exit with code 2 and send nothing: `daily-audit` (now `daily audit`), `combat`, `autocombat`
  (now `adventure inspect` / `adventure run --fight-action`), `adventure-plan` (now `adventure run --max-turns`),
  `mafia-script`, `repl`, `adventure stop|choice-chain`, `daily bribes-sample|send-items|item-arbitrage`. The
  `daily go|breakfast|routine` names are aliases of `daily run`. There are no transfer commands.
- MCP: planner and analytics tools removed from the default surface (experimental only).

### Verified live
- <from the acceptance table: IDs and date>

### Known limits
- CLI gaps: equip and item use are desktop and MCP only; combat macros are not implemented.
- Windows and macOS are validated manually; CI test failures there are tolerated.
- The desktop client has not been live-tested broadly.
- Live use needs your own account, one session per account; agents must get user confirmation.

## 0.4.0 - internal milestone (never released)

This version was never tagged or published; everything below ships as part of 0.5.0. Included: **LibKolibri** (now `KoLibri`), the CLI (`kolibri` / `kolicli`), the MCP server (`kolibri-mcp`)
and the **desktop UI** (an early example client). It is usable but not stable; interfaces may change before 1.0.

### Highlights
- **Rate limiter:** one process-wide limiter with an action lane (serialized, one per 2 s, `KOLIBRI_MIN_REQUEST_INTERVAL_MS`) and a read lane (display reads, 2 in flight) under a global per-second cap, shared by both account roles and redirect hops; `browser-like` preset and env/settings overrides (see usage). Pricegun has its own limiter.
- **Relay:** `kolibri relay` browses KoL in your own browser through the stored session; loopback only, per-run secret cookie, request log without secrets.
- **Interstitial handling:** pages that interrupt a request are recognised and reported instead of being treated as results.
- **Choice policy and `needsDecision`:** choice adventures follow a configurable policy; when no rule applies the run stops and asks (CLI and MCP) instead of guessing.
- **MCP rights binding:** write tools need granted rights with daily limits, are two-step (preview returns a `planId`, the second call executes once), and `kol_consume` / `kol_rest` are covered by `item.use`. The handshake reports the real package version and an accurate server description.
- **Item classification:** item data and classification used by inventory, mall and planners.
- **Mall purchase reconciliation:** a purchase settles when a fresh inventory read shows exactly the ordered quantity; Meat is only a one-way plausibility check, so shop income no longer turns a successful buy into "unclear". Unclear entries can be resolved by explicit user action (`kolibri mall-reconcile`, Mall page button).
- **Background buy queue:** Buy returns at once; buys are queued per account, each with its own budget slice and status row, sent one POST each, and verified later in batches with one inventory read. Purchase answers are read like KoLmafia does (item table, "You spent", "This store doesn't", "You can't afford", ...); an unrecognised answer shows a redacted snippet, HTTP status and the inventory counts compared.
- **Simpler Desktop Buy:** one click opens one compact confirm (item, shop, unit price, quantity, total, budget left); price, stock and budget are checked once at send time against the offer just read, and the purchase is refused only if the price rose.
- **Pirate encryption keys (Crimbo 2023):** `kolibri keys status|options|assemble|redeem` combines the four parts into complete keys and turns keys into a chosen rare item (only the 28 items the key offers). One request per key, each confirmed by the inventory delta; an unclear step stops the series. `kolibri shop add` lists items in the own store and confirms by reading the store back. Previews send nothing; `--execute` needs `--approval-token`.
- **Arbitrage (experimental, hidden):** plan and buy from live Mall offers with per-shop limits, budget and meat reserve; needs `--experimental`.
- **Desktop browser and auto-login:** a Browser page runs the relay in-process in an embedded web view (own cookie jar, relay origin only); "Log in again automatically" is on by default and renews stale sessions once at start.
- **Desktop tabs:** live character pane, inventory, skills (gated Cast), adventures, mall and daily tabs.

### Docs and tooling
- README, quickstart and usage carry an Early Alpha status block; CLI help states the same.
- Docs fixes: `daily go`, `rest` can cost adventures, MCP rights list, `cast` / `relay` are CLI-only; removed the REPL `bacon` entry that had no handler.
- Tag workflow builds four archives (`linux-x64`, `win-x64`, `osx-arm64`, `osx-x64`) from one commit, checks tag against version, runs a CLI smoke start; the draft is created as a pre-release.

### Known limits
- The desktop UI has not been live-tested broadly.
- (Superseded by 0.5.0: the library nupkgs come with the release.)
- Windows and macOS CI test failures are tolerated for this release; those platforms are validated manually.
- Live use needs your own account, one session per account; agents must get user confirmation.
