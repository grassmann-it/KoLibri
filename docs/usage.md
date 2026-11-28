# Using KoLibri

> **Early Alpha (0.5.0).** KoLibri is usable but not stable; commands, options, file formats and the library API may change before 1.0. (0.4.0 was never released.)
> - **Included:** the library `KoLibri` (+ optional `KoLibri.Safety`), `kolibri` / `kolicli` (CLI), `kolibri-mcp` (MCP server), and the desktop UI (an early client, not broadly tested live).
> - **Unstable:** the desktop UI, MCP tool names and schemas, and the Windows and macOS builds (validated manually; CI test failures there are tolerated).
> - **Live use:** needs your own Kingdom of Loathing account. Requests are rate limited in two lanes (actions one per 2 s, `KOLIBRI_MIN_REQUEST_INTERVAL_MS`; display reads a little faster and side by side; see [usage](usage.md)). Use one session per account; logging in elsewhere can end the stored session. Anything that spends Meat or turns needs `--execute` in the CLI; AI agents must get your confirmation first.
> - Details: [CHANGELOG.md](../CHANGELOG.md).

KoLibri has four ways in. All of them use the same stored game sessions and the same library.

| You are | Use | Command |
|---|---|---|
| a player at the keyboard | CLI | `kolibri` (alias `kolicli`) |
| a player who prefers windows | Desktop app (early alpha) | `kolibri-desktop` |
| an AI assistant (Claude, Codex, ...) | MCP server | `kolibri-mcp`, then add `http://127.0.0.1:8765/mcp` as a connector |
| a .NET developer | Library `KoLibri` | `PackageReference` from the release `.nupkg` (local feed) or a source checkout (`dotnet add reference`) |

## Everyday

```bash
kolibri login main                  # once per account; password is asked, session stored encrypted
kolibri status --role main
kolibri adventure run --location 113 --max-turns 5      # preview of a bounded auto-combat run
kolibri daily run --role main       # preview of the day
kolibri daily run --role main --execute --approval-token TOKEN
```

The CLI never changes the account without `--execute` (`--commit` for `apply-prices`). No token is needed for `consume`, `rest` and `cast` (`--execute` is enough) or for the experimental `apply-prices --commit` (changes prices in your own store, never buys). These act only on your own account and run exactly what the command line names; use the preview first. Adventures, fights, purchases, `keys`, `choice submit` and the daily run need `--approval-token`.
`kolibri --help` lists every command; commands removed in 0.5 exit with code 2 and say what replaces them.

### Adventures

```bash
kolibri adventure locations --name pantry     # location catalog, offline
kolibri adventure run --location 113 --max-turns 5 --execute --approval-token TOKEN
kolibri adventure inspect                     # open fight or choice, read-only (exit 4 if one is open)
kolibri adventure step --location 113 --execute --approval-token TOKEN     # exactly one adventure
kolibri adventure fight --action attack --until-end --execute --approval-token TOKEN
```

`adventure run` fights with `--fight-action` (default `attack`; `run`, `skill:ID`, `item:ID`), bounded by
`--max-fight-rounds` (30) and `--min-hp-percent` (20), and answers choices from `--choice-action CHOICE:OPTION` and
the choice policy (below). Turtle Tamer encounters are clicked through by default. Planned, not merged yet:
`--combat-rules`, `--recover-hp`, `--stop-item` and similar farm flags. <placeholder: update when merged>

### Daily routine

`daily run` covers the Day Shortener (campfire), Hermit clovers and the Internet Meme Shop, each at most once per KoL
day. Choose steps with `--only STEP` or `--skip STEP` (`campfire`, `hermit`, `internet-shop`); `--role all` runs main,
then mallbot. `daily campfire|hermit|internet-shop` are aliases for `daily run --only STEP`, and `daily go|breakfast|routine`
for `daily run`. `daily audit [--tail N] [--verify]` reads the audit log.

### Experimental (`--experimental`)

Arbitrage, price reports and shop CRM are hidden by default and not part of the library. With `--experimental`
(or `KOLIBRI_EXPERIMENTAL=1`):

```bash
kolibri --experimental arbitrage plan --role mallbot --item 10881        # read-only plan against the resale reference
kolibri --experimental arbitrage buy  --role mallbot --item 10881        # plan plus preview of every purchase, sends nothing
kolibri --experimental arbitrage buy  --role mallbot --item 10881 --execute --approval-token TOKEN
kolibri --experimental pricegun --item 10881
kolibri --experimental crm --role mallbot
```

`arbitrage buy` buys only up to `reference x (1 - margin)` (default margin 10 %), stays inside `--budget` and
`--reserve`, sends at most one purchase POST per step and never retries an unclear step. An experimental
`reprice` (price suggestions against the mall floor) is planned. <placeholder: update when merged>

### Relay browser (debugging)

```bash
kolibri relay --role main           # prints a URL with a secret; open exactly that URL
```

A local proxy that lets your own browser use the stored KoLibri session, so you can click through the game and compare it with what the CLI does.

- **Only your browser.** It listens on 127.0.0.1 only (default port 8766, so it does not collide with the MCP server's 8765; change it with `--port`). It accepts only the exact host `127.0.0.1:PORT`. The URL printed at start contains a random per-run secret; opening it sets a `SameSite=Strict` cookie that every later request must carry. Other pages, other programs and requests with a foreign `Origin`, `Referer` or `Sec-Fetch-Site` get `403`, so a web page cannot trigger game actions through the relay. Restarting the relay creates a new secret.
- **Minimal forwarding.** Only `Accept`, `Accept-Language`, `Referer`, `Origin`, `X-Requested-With`, conditional-GET headers and the body's `Content-Type` are sent to KoL, plus the stored session cookie and User-Agent. `Set-Cookie` from KoL is not passed to the browser.
- **Streaming and limits.** Images and other binary responses are streamed. HTML, JavaScript and CSS are rewritten (KoL links point back to the relay) in the page's own charset, up to 2 MB; larger text passes through unchanged. At most 8 requests run at once; the rest wait, then get `503`. Upstream requests that take over 60 s get `504`.
- **Rate limit.** Every request to KoL goes through the same process-wide limiter as the rest of KoLibri (default one per 2 s, see `KOLIBRI_MIN_REQUEST_INTERVAL_MS`), so pages with many images load slowly. This is intended.
- **Log.** Every request is logged to `~/.local/share/KoLibri/relay-log.jsonl` and the console: method, path, status, redirect target, byte count and outcome. Query and form values are shown as `…` except a few harmless keys (`snarfblat`, `action`, `whichchoice`, `option`, `which`, `whichitem`, `ajax`); cookies, `pwd` hashes, other header values and error details are never logged or returned in error pages.

Logging in to KoL separately in a browser can end the stored session, so browse through the relay instead and run `kolibri login refresh --role main` if KoL redirects to the login page.

## With an AI assistant

```bash
kolibri-mcp                  # prints a consent code in this terminal
```

The assistant is sent to a consent page, you choose which rights it gets (read, mall buy, adventure, message, item use, ...)
and the limits (Meat per day, price ceiling, reserve). Everything outside that is refused. See
[mcp-access.md](mcp-access.md). The default surface is the 19 core tools (see the README); market, shop and analytics tools need `KOLIBRI_EXPERIMENTAL=1`.

## Request rate

Requests go through one limiter per process, shared by all accounts, every manually followed
redirect and every tool, with two lanes like a browser's main frame and sidebar:

- **Action lane**: anything that changes state or spends turns (adventure, fight, choice, eat/drink/spleen, mall buys,
  kmail, every POST, and any page not known to be a plain read). Strictly one at a time, started at least 2 s apart
  (`KOLIBRI_MIN_REQUEST_INTERVAL_MS`, never below 1000). The lane stays occupied until the response has arrived.
- **Read lane**: idempotent display reads (`api.php` status, `charpane.php`, inventory, mall search, item descriptions).
  Default: starts 1 s apart, at most 2 in flight, so a sidebar or status refresh runs next to an adventure step instead of
  queueing behind it.
- **Global cap**: at most 2 request starts per second over both lanes (a safety net, never above 5).

Presets and overrides: `KOLIBRI_LIMITER_PRESET=browser-like` (or `"limiterPreset": "browser-like"` in the settings file)
reads every 0.4 s, 3 in flight, cap 4 per second; actions keep their 2 s. Single values: `KOLIBRI_READ_INTERVAL_MS`
(min 200), `KOLIBRI_READ_CONCURRENCY` (1 to 4), `KOLIBRI_MAX_REQUESTS_PER_SECOND` (1 to 5); environment beats settings
(`kolReadIntervalSeconds`, `kolReadConcurrency`, `kolMaxPerSecond`), settings beat the preset. After an adventure step the
status and inventory refresh is one parallel read pair, not a second sequential round trip.

Separate processes (for example the CLI and the desktop app running at once) do not coordinate, so together they can
exceed these limits. Pricegun is a different host with its own per-process limiter (2 s,
`KOLIBRI_PRICEGUN_MIN_REQUEST_INTERVAL_MS`); cached results send nothing.

Both intervals, the cache duration, budgets and combat limits are also editable in the desktop **Settings** view
(saved as `desktop-settings.json` in the KoLibri data folder, no secrets; every value has a default, minimum and
maximum). An environment variable, when set, overrides the saved interval and the view says so. Intervals are
applied at startup, so restart after changing them. The game itself ends a fight at 50 rounds; that ceiling is fixed.

## As a library

```csharp
using KoLibri;

await using var kol = KolibriClient.Create();
await using var session = await kol.OpenAsync("main");
var status = await session.Character.GetStatusAsync(refresh: true);
```

`KolibriClient` in package `KoLibri` is the front door: login, one `KolibriSession` per account with facets for
character, inventory, adventures (one runner, `RunAsync`), mall (quote and guarded buy), shop, KMail and daily steps.
See [src/KoLibri/README.md](../src/KoLibri/README.md), [lib-api-0.5.md](lib-api-0.5.md) and the
[cookbook](../samples/cookbook/README.md).

## Choice adventures

If a choice adventure appears during `adventure run`, the run answers it only when the
choice policy has an answer; otherwise it stops with `stopReason: NeedsDecision` and exit code 4 (not an error). The
JSON `needsDecision` lists the choice id, title, options and the exact commands to resume. A choice left open
blocks every page of the game, so always resolve it:

```bash
kolibri choice pending --role mallbot                  # read only: id, title, options (exit 4 if one is open)
kolibri choice submit --role mallbot --choice ID --option N                       # preview
kolibri choice submit --role mallbot --choice ID --option N --execute --approval-token TOKEN
kolibri choice policy list
kolibri choice policy set --choice ID --option N       # always use option N for this choice
kolibri choice policy set --mode auto-safe             # also allow built-in/"leave it" options that cost nothing
kolibri choice policy clear --choice ID                # or --all
```

Policy file: `choice-policy.json` in the KoLibri app data directory (override with `KOLIBRI_CHOICE_POLICY`).
Limits `--max-meat` (0-1,000,000), `--max-hp-percent` (0-100), `--max-adventures` (0-10) default to 0 and only apply
to built-in options; a per-choice default is your explicit decision. The built-in table is empty until choice
data is verified (KoLmafia's bundled data has no choice table); `auto-safe` therefore only takes an option whose text
clearly means leave/ignore/skip and mentions no cost, and only if exactly one option does.
