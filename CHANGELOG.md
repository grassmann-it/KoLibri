# Changelog

## 0.4.0 - Early Alpha

First tagged early-alpha release. Included: **LibKolibri**, the CLI (`kolibri` / `kolicli`), the MCP server (`kolibri-mcp`)
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
- **11-leaf clover arbitrage:** plan and buy from live Mall offers with per-shop limits, budget and meat reserve.
- **Desktop browser and auto-login:** a Browser page runs the relay in-process in an embedded web view (own cookie jar, relay origin only); "Log in again automatically" is on by default and renews stale sessions once at start.
- **Desktop tabs:** live character pane, inventory, skills (gated Cast), adventures, mall and daily tabs.

### Docs and tooling
- README, quickstart and usage carry an Early Alpha status block; CLI help states the same.
- Docs fixes: `daily go`, `rest` can cost adventures, MCP rights list, `cast` / `relay` are CLI-only; removed the REPL `bacon` entry that had no handler.
- Tag workflow builds four archives (`linux-x64`, `win-x64`, `osx-arm64`, `osx-x64`) from one commit, checks tag against version, runs a CLI smoke start; the draft is created as a pre-release.

### Known limits
- The desktop UI has not been live-tested broadly.
- NuGet publishing is planned for 0.5; use a source checkout for the library.
- Windows and macOS CI test failures are tolerated for this release; those platforms are validated manually.
- Live use needs your own account, one session per account; agents must get user confirmation.
