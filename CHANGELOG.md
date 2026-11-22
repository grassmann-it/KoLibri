# Changelog

## 0.4.0 - Early Alpha

First tagged early-alpha release. Included: **LibKolibri**, the CLI (`kolibri` / `kolicli`), the MCP server (`kolibri-mcp`)
and the **desktop UI** (an early example client). It is usable but not stable; interfaces may change before 1.0.

### Highlights
- **Rate limiter:** one process-wide limiter for all KoL requests (default one per 2 s, `KOLIBRI_MIN_REQUEST_INTERVAL_MS`), shared by both account roles, redirect hops and Pricegun.
- **Relay:** `kolibri relay` browses KoL in your own browser through the stored session; loopback only, per-run secret cookie, request log without secrets.
- **Interstitial handling:** pages that interrupt a request are recognised and reported instead of being treated as results.
- **Choice policy and `needsDecision`:** choice adventures follow a configurable policy; when no rule applies the run stops and asks (CLI and MCP) instead of guessing.
- **MCP rights binding:** write tools need granted rights with daily limits, are two-step (preview returns a `planId`, the second call executes once), and `kol_consume` / `kol_rest` are covered by `item.use`. The handshake reports the real package version and an accurate server description.
- **Item classification:** item data and classification used by inventory, mall and planners.
- **Mall purchase reconciliation:** a purchase settles when a fresh inventory read shows exactly the ordered quantity; Meat is only a one-way plausibility check, so shop income no longer turns a successful buy into "unclear". Unclear entries can be resolved by explicit user action (`kolibri mall-reconcile`, Mall page button).
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
